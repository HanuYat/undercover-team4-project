# #865 다운 유예 래그돌 — 구현 근거

> **이 문서의 핵심은 §2다.** 소유권 계열의 네 번째 문서(#763 → #774 → #820 → 여기)이고,
> **#815가 제시한 판정 기준을 그대로 적용해 반대 결론을 낸 사례**다. 그 계보를 모르고 읽으면
> "왜 Launched는 오너인데 Down은 서버인가"가 모순처럼 보인다.

관련 문서
- [player-ragdoll.md](player-ragdoll.md) — `PlayerRagdoll` 설계 근거 아카이브. §1에 새 불변식을 실었다
- [725-death-grace-period.md](725-death-grace-period.md) — 다운 유예의 정본. **§8 첫 행을 이 이슈가 뒤집었다**
- [763-player-ragdoll-npc-parity.md](763-player-ragdoll-npc-parity.md) — 사망 중 소유권을 서버로 옮긴 최초
- [815-homerun-player-ragdoll.md](815-homerun-player-ragdoll.md) — 살아 있는 채로 래그돌을 태운 첫 사유, 오너 권위를 택한 근거
- [820-self-revive.md](820-self-revive.md) — 사망 중 `SendTo.Owner`가 새는 함정(함정 1)과 그 우회
- [903-instant-death.md](903-instant-death.md) — 함께 진행된 즉사 규칙

---

## 0. 무엇을 바꿨나

`PollRagdollCause`의 진입 술어 한 줄:

```csharp
// 전
bool wantsRagdoll = m_incapacitation.IsDead || m_incapacitation.IsLaunched;
// 후
bool wantsRagdoll = m_incapacitation.IsOutOfAction || m_incapacitation.IsLaunched;
```

HP 0으로 쓰러지면 애니메이터 Knockdown 자세가 아니라 **물리로 무너진다.** 유예가 끝나 `Die`로 넘어갈 때 몸에는 아무 변화가 없다 — 래그돌이 이미 켜져 있으므로 포즈가 튀지 않는다(#865 완료 기준 2가 그것이다).

그 한 줄이 요구하는 것이 나머지 전부다.

## 1. 설계의 전제가 되는 사실 넷

이 넷을 모르고 읽으면 §2의 판단이 과해 보인다.

**(A) 다운→사망은 60초 만료만이 아니다 — 확인사살이 주 경로다.**
[PlayerHealth.ApplyDamage](../Assets/Scripts/Player/PlayerHealth.cs)가 대상이 `IsDowned`면 즉시 `ServerFinishOff()`를 부른다. 진압봉 확인사살은 **언더커버가 동료를 죽이는 유일한 수단**이다(같은 함수의 처치 집계 주석). 따라서 "다운→사망 이음새"는 라운드당 몇 번이 아니라 **모든 의도적 살해마다** 발생한다.

**(B) 다운에 임펄스가 붙는 경로는 정말로 없다.**
`BombBlast`는 `SuddenEventUtil.CollectFieldPlayers`가 HP>0만 담고 `m_deathBuffer`도 "이번 폭발로 0이 된" 사람만 담으므로 **이미 다운인 몸은 수집조차 안 된다.** `HomeRunBaton`은 데미지 없이 `ServerLaunch`만 부르고, `Baton`은 유효타로 분류하되 임펄스를 주지 않는다. 그리고 #903으로 차·폭탄은 다운을 아예 건너뛴다. **다운 래그돌은 항상 임펄스 0에서 시작한다** — 제자리에서 1~2초 힘없이 무너지고 끝이다.

**(C) 코드에 암묵 불변식이 있다 — "소유권은 래그돌이 꺼져 있을 때만 바뀐다."**
`PlayerIncapacitation.SetCause`가 `m_cause` 대입 직후 **같은 프레임**에 `ApplyDeathOwnership`을 부르고, `PollRagdollCause`는 그 뒤 `Update`에서 돈다. 그래서 **이관은 언제나 `RagdollState.Animated` 구간에서 끝난다.** `ReleaseBonesToPhysics`가 키네마틱/동적을 진입 시점에 한 번만 정하고 다시 묻지 않는 것, `RagdollPoseStreamer.BeginStreaming`이 권위 게이트를 진입 시점에만 통과시키는 것이 **모두 이 불변식의 산물이다.**

**(D) `PlayerMovement`는 이미 서버 권위 래그돌을 전제로 배선돼 있다.**
그쪽 주석이 *"사망 중 소유권이 서버로 넘어가면 이 컴포넌트가 꺼져 있는 피어가 권위가 되므로, 추종은 `PlayerRagdoll`이 스스로 돈다"* 를 명시하고, 그 래그돌 분기가 호송/운반 분기보다 **앞**이다.

## 2. 소유권 — 다운도 서버로 옮긴다 (이 이슈의 핵심 판단)

```csharp
// PlayerIncapacitation.ApplyDeathOwnership
bool wantsServerOwner =
    cause == IncapacitationCause.Die || cause == IncapacitationCause.Down;
```

기존 `m_ownershipMovedToServer` 가드 덕분에 **다운→사망 전이는 문자 그대로 무동작**이 된다. 그것이 핵심이다.

### 2-1. "다운은 오너 권위" 안이 만드는 고장 다섯

그 안을 택하면 `ChangeOwnership`이 `RagdollState.Ragdoll` 구간 **안**에서 일어나 불변식 (C)가 깨진다.

| # | 피어 | 무엇이 깨지나 |
|---|---|---|
| 1 | 권위를 **잃는** 오너 클라 | 뼈가 **동적으로 남는다**(`ReleaseBonesToPhysics`는 진입 때 한 번만 돈다). 동시에 `IsPoseAuthority`가 거짓이 되어 서버 자세 패킷을 **받아 뼈에 입힌다** → PhysX와 스트림이 같은 뼈를 매 프레임 번갈아 쓴다 → 떨림/발산 |
| 2 | 권위를 **얻는** 서버 | 뼈가 **키네마틱으로 남는다.** 그런데 `RagdollRig.AllAsleep`은 **키네마틱 바디를 건너뛰므로 전부 키네마틱이면 참**이다 → 정착 판정이 **첫 프레임에 즉시 `Settle()`** 한다 |
| 3 | 서버 | 그 `Settle()`의 `EndStreaming()`이 **아무것도 안 보낸다** — 서버의 `m_streaming`은 진입 때 권위가 아니어서 켜진 적이 없고 `if (!m_streaming) return`으로 시작한다. 전 피어가 종착 자세를 못 받는다 |
| 4 | 서버 | 깨어남 폴링도 죽는다 — 키네마틱이라 `AllAsleep`이 영구히 참 → `ResumeFromSleep`이 절대 안 불린다. **밟혀도 반응하지 않는 영구 고착** |
| 5 | 전 피어 | 결과: 무너지던 도중 자세로 시체가 굳는다. **(A)에 따라 확인사살마다 이 그림이 난다** |

### 2-2. 채택안은 이관 횟수를 늘리지 않는다

"이관을 다운으로 확대한다"는 우려와 **반대**되는 지점이다.

| 안 | 이관 횟수 | 어디서 |
|---|---|---|
| 종전 (사망만) | 2회 | 사망 진입 시 서버로, 회복 시 오너로 |
| **채택안 (다운도)** | **2회** | 다운 진입 시 서버로, 회복 시 오너로 — 사망 전이는 가드가 삼킨다 |
| 다운은 오너 | 3회 | 사망 전이 시 서버로(**래그돌 안에서**), 회복 시 오너로 |

**이관 총량이 그대로인 채 불변식 (C)가 유지된다.** 이관 시점이 60초 앞으로 당겨질 뿐, 시퀀스는 #763/#774/#820에서 이미 세 번 검증된 것과 같다.

### 2-3. #815가 오너를 택한 근거는 다운에 적용되지 않는다

[815 문서](815-homerun-player-ragdoll.md)의 유일한 실질 근거는 이것이었다:

> 소유권을 서버로 옮기면 피격 당한 본인의 클라에서 `TickCapsuleFollow`가 멈춘다 — 본인은 자기 텀블을 NetworkTransform 보간으로만 보게 된다. 사망 래그돌은 이미 정착한 시체라 문제였던 적이 없지만, **2초짜리 격렬한 회전**에서는 체감이 갈린다.

**다운에는 그 격렬한 회전이 없다** — (B)에서 확인한 대로 임펄스가 0이다. 그리고 오너의 1인칭 카메라는 루트 기준 고정 높이(`PlayerLook.m_downCamHeight = 0.35f`)에 있어 뼈 자세와 무관하다. 즉 결론이 갈리는 것은 **모순이 아니라 그 문서가 제시한 판정 기준을 그대로 적용한 결과**다.

### 2-4. 대가 — `SendTo.Owner`가 새는 것

`SendTo.Owner`를 전수 조사했다. **플레이어 오브젝트에 붙은 것만** 위험하고, #820이 사망을 위해 이미 대부분 우회해 뒀다.

**안전 (무변경)** — 스폰 시점 오너를 굳혀 쓰는 것들(`PlayerInputHandler`·`PlayerMovement` #774, `PlayerDownView` #763 A-1, `PlayerReviveHud`, `PlayerHitView`, `PlayerHpUI`), `NetworkVariable` 기반인 것들(잔여 초·재부팅 진행 링), 아이템 소유권이 유지되는 `ReviveKit` 자가 부활(#820 함정 1·2·3), **구조자 자신의** 오브젝트에 붙은 `PlayerReviver` 게이지, `PlayerLooter`(약탈자 쪽), `PlayerKillCredit`(가해자 쪽).

**깨진다 — 고친 것 셋.** 공통 원인은 `NotifyOwner`가 서버로 새는 것이고, **다운의 60초가 주된 약탈 창**이라 실기능이다:

| 지점 | 증상 | 고친 방법 |
|---|---|---|
| `PlayerLootable.ServerNotifyRobbedItem/Funds` | 털린 본인이 통보를 못 받는다 | `BodyOwnerClientId` 대상 RPC로 직접 지정 |
| `PlayerTheftView.ShowStolen` | 도난 토스트가 안 뜬다 | 위 RPC에 함께 태운다(`ShowStolenLocal` 노출) |
| `PlayerLoadout.SyncHeldItemsRpc` | 본인 인벤토리 모델(`Slots`)이 다운 내내, **부활 후에도** 낡은 채로 남는다 | `ServerRevive` 끝에서 한 번 재동기화 |

`PlayerIncapacitation`에 노출한 것:

```csharp
internal ulong BodyOwnerClientId =>
    m_ownershipMovedToServer ? m_ownerBeforeDeath : OwnerClientId;
```

동기화하지 않는다 — 읽는 쪽이 전부 서버 컨텍스트다(`m_ownerBeforeDeath`의 기존 방침 그대로).

⚠ **`ChanneledInteractionBehaviour.NotifyOwner`(기반 클래스)는 고치지 말 것.** 아이템에 붙은 컴포넌트에서는 사망 중에도 소유권이 그대로라 `SendTo.Owner`가 정확하다 — #820이 자가 부활 채널링을 `ReviveKit`에 둔 근거가 그것이다.

`ServerRevive`의 재동기화 한 줄은 **다운의 새 결함과 사망의 기존 잠재 결함을 함께 닫는다.** `Recover()`가 소유권을 **먼저** 되돌리므로 그 뒤의 호출은 본인에게 간다 — #820 함정 3(`ServerRevive` → `ServerConsume` 순서)과 같은 논리다.

### 2-5. 잔여 위험 둘

- **호스트가 최대 6구를 시뮬레이션한다.** 뼈 약 12개 × 6 ≈ 72 Rigidbody. 다만 **정착 후에는 물리 수면**이라 60초 정지 구간 비용이 거의 0이고, 최악("6명 동시 무너짐")은 전멸=라운드 종료 상황이다. 호스트는 이미 사망 시체 전부와 NPC 래그돌을 다 돌린다. 대역폭은 25Hz × 약 62B ≈ 1.5KB/s **구당, 무너지는 동안만**.
- **`Launched → Down` 전이는 여전히 래그돌 중 이관이다.** `Incapacitate`의 가드는 Die/Down만 막으므로 비행 중 HP 0(저항 NPC·낙뢰)은 `Launched → Down`이 된다. `HomeRunBaton`이 데미지를 주지 않으므로 2초 창에 외부 데미지가 겹쳐야 하는 좁은 경로지만 **0은 아니다.** → 아래 안전망으로 받는다.

### 2-6. 버린 대안

- **스트리머·래그돌 권위를 항상 Server로 고정** — 루트 `NetworkTransform`의 `AuthorityMode`가 Owner라(살아있는 이동이 요구하는 값) 서버가 쓴 루트 위치를 오너가 덮는다. 정확히 #763이 *"둘 중 하나만 서버로 바꾸면 시체가 이름표를 두고 떠난다"* 고 적은 실패다.
- **사망 이관을 정착 이후로 미루고 명시적 인계** — (A)에 따라 다운→사망이 흔하므로 안전망이 아니라 **상시 경로**가 되고, 정착 상태를 피어 간에 인계할 수단이 없다(`m_settled`는 로컬 값).
- **`SendTo.Owner` 셋을 손대지 않고 넘어가기** — 약탈 통보는 60초 창이 주 무대다. 권고할 수 없다.

## 3. 새로 넣은 안전망·수정 셋

### 3-1. `TickAuthorityHandover` — 래그돌 중 권위 변경 (~12줄)

`Update`의 `PollRagdollCause()` 직후에서 돈다. 정상 경로에서는 걸리지 않고(불변식 C), 받는 것은 2-5의 `Launched → Down` 하나다.

하는 일은 **`ReleaseBonesToPhysics()`를 다시 부르는 것**이 전부에 가깝다 — 그 함수가 이미 양방향으로 정확하다:
- 권위를 잃은 쪽 → `SetKinematic(true)` + `CapturePose()` + `m_holdPoseUntilStream = true` (고장 1이 닫힌다)
- 권위를 얻은 쪽 → `SetKinematic(false)` 안의 `Physics.SyncTransforms()`가 **지금 화면에 있는 자세**에서 물리를 출발시킨다 (고장 2·4가 닫힌다)

거기에 `m_streamer?.BeginStreaming()`(멱등, ⚠ **양쪽 피어에서 부른다** — 원격 분기가 세우는 두 래치가 다시 서야 한다는 것이 그 함수의 명시적 요구다)과 정착 재판정(`m_settled = false`)을 얹는다. 고장 3이 여기서 닫힌다.

정착한 몸에서 타면 임펄스 0·속도 0으로 같은 자세에 다시 놓이므로 시각적으로 거의 무동작이다.

### 3-2. 구조 채널링 중에는 몸을 재운다

`PlayerReviver.IsInRange`는 루트를 보고 **완료 시점에만** 검사하며, 구조자는 이동하면 채널링이 취소된다. 그래서:

> 구조 채널링 3초 중에 시체가 밟혀 깨어나 밀리면, **게이지를 다 채운 뒤에 "범위를 벗어남"으로 실패**한다.

임펄스가 없어 변위가 크지는 않지만 경사·계단에서는 미끄러지고, 실패 모드가 나쁘다. `Update`의 권위 게이트 뒤에서 `IsBeingRevived`면 `SleepAll()` + `Settle()`.

**이 자리가 성립하는 근거**는 `ServerSetBeingRevived`를 쓰는 것도 서버이고 래그돌 권위도 서버라(§2) **같은 피어에서 같은 값을 본다**는 것이다. `IsBeingRevived`는 `m_reviveEndSynced` 기반이라 원격도 읽을 수 있어 어긋나지 않는다. 채널링이 끝나면 몸은 정착한 채 남고, 나중에 밟히면 깨어남 폴링이 받는다.

### 3-3. 루트 yaw 추종을 일방향 래치로

`TickCapsuleFollow`는 `if (!m_settled) FollowBodyYaw();`였다. 정착 후 밟혀 깨어나면 `ResumeFromSleep`이 `m_settled = false`로 되돌려 **yaw 추종이 다시 켜진다.**

다운은 1인칭이라 그 yaw가 **곧 쓰러진 본인의 시야**다 → **동료가 몸을 발로 차서 그 사람 화면을 돌릴 수 있다.** 사망은 3인칭 관전이라 없던 문제고, 비행은 정착 전이라 의도된 동작이었다.

그래서 `m_yawFollowDone`(이 에피소드에서 한 번이라도 정착했는가)으로 바꿨다 — `Settle()`에서 세우고 `EnterRagdoll`/`ExitToAnimator`에서 내린다. [player-ragdoll.md §11](player-ragdoll.md)이 *"상태가 아니라 깃발로 물어야 한다"* 고 적은 것과 같은 계열의 논거다. 사망·비행 거동은 바뀌지 않는다.

## 4. 이름 정리

| 전 | 후 |
|---|---|
| `PollDeath()` | `PollRagdollCause()` |
| `m_sawDeathThisEpisode` | `m_sawCauseThisEpisode` |
| `m_awaitingDeathSeconds` | `m_awaitingCauseSeconds` |

#815는 상수만 `k_causeSyncGraceSeconds`로 고치고 필드는 "사망으로 읽어라"는 주석으로 넘겼다. **사유가 둘일 때는 그 독법이 통했지만 셋이 되면 다수도 아니다** — 실제로 가장 흔한 진입은 다운이다. 전부 private이고 참조는 이 파일과 주석 두 군데뿐이라 비용이 없다.

## 5. 그대로 둔 것 — 확인 결과

| 지점 | 근거 |
|---|---|
| **`Baton` 확인사살** | `SphereCastNonAlloc(..., ~0, QueryTriggerInteraction.Ignore)`가 **전 레이어·트리거 제외**를 훑으므로 캡슐이 꺼져도 **뼈 콜라이더(비트리거)가 잡히고** `GetComponentInParent<PlayerHealth>()`가 루트까지 올라가 `isDownException`이 성립한다. **오히려 팔다리까지 덮여 판정이 넓어진다.** ⚠ 검증 항목: `AimOcclusion.IsBlocked`가 바닥에 누운 몸을 겨눌 때 지형을 가림으로 잡지 않는지 |
| `PlayerReviver`·`PlayerLooter` 조준 | #857이 `Ragdoll` 레이어 보조 레이를 넣고 술어를 `IsAimTargetable`(= `IsOutOfAction`)로 뒀다 → 뼈가 그대로 조준된다. 진입 술어를 `IsOutOfAction`으로 고른 이유가 이것과 **같은 값을 보게** 하려는 것이다 |
| `PlayerAnimationDriver` | `Down = IsProne \|\| IsRagdollActive` — 다운 래그돌은 이 배선의 원래 의도에 더 잘 맞는다 |
| 구조 완료 시 기상 | **코드 변경 없음.** 사망 부활(부활 키트·본부 장치)이 지금 매번 도는 `ExitToAnimator` 경로를 그대로 탄다. `Knockdown_Ground → StandUp`은 `Down` bool 하나로만 걸리고, 블렌드가 끝난 프레임부터 `IsProne`을 다시 따른다. **행동 변화 하나: 구조 완료가 블렌드만큼(0.4초) 늦어진다** — 채널링 3초에 비하면 미미하고 오히려 "무너져 있던 몸이 자세를 잡고 일어난다"가 자연스럽다 |
| `PlayerLook` | 다운은 `IsProne` 1인칭 바닥 시점, 사망은 `IsDead` 3인칭 관전. 카메라는 골격이 아니라 루트의 자식이고 높이가 루트 기준 고정이라 자세와 무관하다. **몸이 아니라 시점이 다운/사망을 가르는** 이번 결정의 근거 그 자체다 |
| `PlayerCarrier` | `CanBeCarried => IsDead && !IsBodyLost` **유지.** 래그돌이 켜졌다는 것이 운반 가능의 근거가 되면 GDD 7-5 표의 "다운은 구조 대상, 사망은 운반 대상"이 무너진다 |
| `RoundManager` 전멸 판정 / `PlayerDownView` / `InteractionFeedback` | 전부 동기화값 기반 — 무영향 |

**부수 이득: 서 있는 투명 기둥이 사라진다.** 종전 다운은 래그돌이 없어 `CharacterController` 캡슐이 **켜진 채 서 있는 1.8m 캡슐**로 사망 지점에 남았다. 래그돌 진입이 그 캡슐을 끄고 루트를 골반 아래 지면으로 끌어온다 → **누운 몸을 걸어서 지나갈 수 있게 된다.**

**6구가 쌓이지 않는다 (실측).** `ProjectSettings/DynamicsManager.asset`의 `m_LayerCollisionMatrix`를 디코드하면 레이어 10(`Ragdoll`)의 마스크가 `0x00000001` — **`Default`와만 충돌한다.** 뼈끼리 안 부딪히므로 6구가 겹쳐 누워도 서로 밀지 않고, 상호 깨어남 연쇄가 구조적으로 없다. 뼈 ↔ 살아있는 플레이어 캡슐(`Default`)은 부딪히므로 밟히면 깨어난다 — 의도한 거동.

## 6. 알려진 거동 변화 / 남은 위험

- **늦게 접속한 피어**의 `m_skipThisEpisode`가 다운→사망을 지나도 계속 참이다(`!wantsRagdoll`일 때만 내려간다) → 그 피어에서는 유예가 끝나도 몸이 `Knockdown_Ground` 애니로 남는다. **의도된 동작이다** — 이미 끝난 낙하를 뒤늦게 재생하면 시체가 한 번 더 무너진다(§9의 원래 근거). 위치·yaw는 루트 NT가 맞춘다. 문제가 되면 고칠 자리는 하나 — 마지막 관측 원인을 기억해 원인이 **바뀔 때** 플래그를 내리는 것.
- **현장 부활에서 캡슐이 설 수 없는 자리.** 사망 부활은 대부분 본부(평지)에서 일어나지만 다운 부활은 **현장 어디서나** 일어난다. 임펄스가 없어 변위는 작지만 경사·계단·좁은 실내에서 미끄러질 수 있다. 재현되면 고칠 자리는 `SetControllerEnabled(true)` 앞의 디페네트레이션 한 번이고, 사망 부활과 공통 문제라 별건이 맞다. §3-2가 이 위험도 상당히 줄인다.
- **`Abducted`/`Beamed` 호송 중 HP 0** → 다운이 성립하는데, 변경 후에는 `PlayerMovement`의 래그돌 분기가 이겨 **호송이 멈춘다.** `Abducted → Die`에서 이미 같은 일이 나므로 새 결함 종류는 아니다.
- **`PlayerHeldItemView`가 `IsRagdollActive`면 손에 든 물건 렌더러를 숨긴다** → 다운 중 부활 키트가 손에서 안 보인다. 연출 변화뿐이고(사망 자가 부활에서 이미 그렇다) 판정은 부착 자식을 보므로 무영향.
- **`TeamStatusPanel`에서 다운이 생존으로 떨어진다.** "#524 이후 발생하지 않는다"던 근거가 #725로 이미 거짓이 됐다 — 유예 중인 동료가 팀 현황판에서 멀쩡해 보인다. 어느 칸으로 보낼지는 기획 결정이라 이번 범위에서는 사실만 주석에 적어 뒀다.

## 7. 뒤집은 결정 — #725 §8

[725 문서](725-death-grace-period.md) §8 표의 첫 행은 이렇게 적었다:

> `PlayerRagdoll.cs` (`IsDead` 폴링) — 유예 중엔 Knockdown 애니, 만료 시 래그돌. 폴링이라 원인 전이도 잡는다

그리고 그 설계의 뜻을 §설계에 이렇게 적었다: *"몸이 무너지는 순간이 곧 '완전히 죽었다'는 신호가 된다."*

**그 신호를 버렸다.** 대신 다운/사망 구분은 이미 **화면·시점·소리**가 전부 맡고 있다:

| | 다운 | 사망 |
|---|---|---|
| 화면 | 점진 암전 + 중앙 잔여 초 | 완전 암전 1초 홀드 → 0.6초 페이드 |
| 시점 | 1인칭 바닥(`IsProne`) | 3인칭 관전 카메라 (`PlayerLook`이 `IsDead`로 가른다) |
| 소리 | 들리고 말할 수 있다 | SFX/BGM/Vivox 무음 + PTT 차단 |

원래 신호가 하던 역할은 **암전 1초 홀드 + 관전 카메라 전환**이 대신한다. 몸으로 다시 구분할 필요가 없다는 판단이고, 얻는 것은 #865 본문이 말한 두 가지다 — 쓰러지는 순간의 무게감, 그리고 다운→사망에서 포즈가 튀지 않는 것.

## 8. 검증

`dotnet build Assembly-CSharp.csproj` **오류 0개**. **Play 테스트는 하지 않았다** — 관례대로 사용자가 직접 한다.

**소유권 (래그돌을 켜기 전에 단독으로 볼 것 — 이관을 래그돌과 분리해 검증할 수 있는 유일한 창이다)**
1. 원격 클라 다운 진입 → 이관, 회복에서 복귀. 원격 본인의 커서·입력·화면 어두워짐 정상(#774 회귀)
2. **호스트 자신이 다운** → 이관 무동작 경로. 남의 다운이 호스트 화면을 어둡게 하지 않는지(#763 A-1 회귀)
3. 다운 중 원격 클라 **접속 종료** → 예외 없이 서버 소유로 남는지(#287)
4. 다운 → 60초 만료 → 사망. **이관이 두 번 일어나지 않는지**(가드가 삼키는지 로그로)

**다운 래그돌**
5. 진압봉 3대 → **래그돌로 무너진다.** 임펄스 없이 제자리에서. 원격에서 같은 자세로 정착하는지
6. 쓰러진 본인 1인칭 — 무너지는 동안 시점이 함께 눕고 정착 후 **멈추는지.** 마우스로 시야가 도는지
7. 동료가 조준 → 일으키기/뒤지기 프롬프트. **팔다리를 겨눠도 잡히는지**(#857 보조 레이)
8. E 구조 3초 → 부활. 블렌드 → StandUp → Locomotion이 정상적으로 돌고, 굴러간 자리에서 캡슐이 서는지(경사·계단·실내에서도)
9. **구조 채널링 중 제3자가 시체를 밟는다** → 시체가 안 움직이고 구조가 완료되는지(§3-2). **쓰러진 본인의 시야가 돌지 않는지**(§3-3)
10. **R 약탈** → 털린 본인에게 알림·토스트가 뜨는지(§2-4). **부활 후 본인 인벤토리가 갱신돼 있는지**
11. **진압봉 확인사살** → 다운 래그돌 도중 사망. **시체가 굳지 않는지**(§2-1 표 전체). 암전·관전 전환 정상
12. 60초 만료 → 암전 → 관전. 몸은 이미 누워 있으므로 **변화가 없는 것이 사양**
13. **누운 몸을 걸어서 지나갈 수 있는지.** 6명이 겹쳐 누워도 서로 밀지 않는지
14. 다운 중 밧줄 좌클릭 운반 거부. 본부 장치·키트의 사망 전용 게이트 유지
15. **늦은 접속** — 이미 다운인 몸이 `Knockdown_Ground`로 누워 있고, 사망으로 넘어가도 래그돌에 안 들어가는지(§6). 위치·yaw가 다른 피어와 어긋나지 않는지
16. **`Launched` 회귀** — 홈런 진압봉이 오너 권위 그대로 동작하는지. 비행 중 외부 데미지로 다운이 되는 경우 시체가 굳지 않는지(§3-1)
17. 납치·UFO 호송 중 HP 0 → 호송이 멈추는 것 확인(§6)
18. 전원 다운 → 전멸 게임오버 즉시. 라운드 리셋 후 다음 라운드에서 몸이 투명하거나 누운 채 시작하지 않는지
