# #903 차·폭탄 즉사 — 구현 근거

## Context

GDD는 차와 폭탄을 **즉사**로 적어 두었다:

- §7-5 데미지 소스 목록 — "도로 차량(§6-6 — **닿으면 즉사**)"
- §6-6 — "피해 120(MaxHp 100 초과) → 플레이어는 **기능 정지**, 시민은 사망"
- §6-4 — "**폭심 즉사**(시체가 폭심 반대쪽으로 날아감) / 가장자리 넉백만"

그런데 #725가 다운 유예를 되살릴 때 스위치를 [PlayerHealth.SetHp](../Assets/Scripts/Player/PlayerHealth.cs)의 한 줄(`Incapacitate(Down)`)에 두었고, 그 줄은 **어떤 피해원이 HP를 0으로 내렸는지 모른다.** 그래서 차에 치이거나 폭심에서 터져도 60초 유예가 붙었다 — GDD가 약속한 동작이 조용히 깨진 것이다.

이 문서는 그것을 되돌린 근거를 남긴다. **새 규칙이 아니다.**

## 확정된 결정 (사용자 승인, 2026-08-27)

| 항목 | 결정 |
|---|---|
| 즉사 판정 | **HP 0에 도달하면 유예만 건너뛴다.** 데미지 계산·거리 감쇠는 그대로 — 폭탄 가장자리에서 HP가 남으면 생존한다 |
| 적용 범위 | **차량 + 폭탄만.** 낙뢰(`LightningEvent`)는 지금처럼 유예를 준다 |
| 차 치임 연출 | 즉사 시 캡슐 넉백 대신 **래그돌 임펄스** |
| 차 치임 대상 | **플레이어와 NPC 둘 다** — NPC는 2026-08-28에 이어서 넣으며 #634/#768 결정을 뒤집었다(아래) |

## 왜 `TakeEnvironmentalDamage`가 아니라 `TakeLethalDamage`인가

NPC 쪽에 같은 이름이 이미 있다(`NpcHealth.TakeEnvironmentalDamage`, #690). 그 이름을 빌리고 싶어지지만 **두 가지가 걸린다.**

1. **뜻이 다르다.** NPC 쪽은 "연행 게이트 우회"(밧줄에 매인 신병도 차에 치인다)이고, 플레이어 쪽은 "결과가 다르다(유예 없음)"다. 그런데 두 호출이 **같은 파일 인접 함수에 나란히 선다** — `TrafficVehicle.ServerHitNpc`와 `ServerHitPlayer`. 이름이 같으면 읽는 사람은 같은 뜻으로 읽는다.
2. **경계가 규칙과 어긋난다.** 낙뢰는 누가 봐도 환경 피해인데 범위 밖이다. 이름이 "환경"이면 다음 사람이 낙뢰를 **빠뜨린 것으로 보고** 배선한다. 그래서 `LightningEvent`의 호출부에 "환경 피해라서가 아니라 피해원별 결정"이라는 주석을 함께 박았다.

## `IDamageable`을 건드리지 않은 이유

`skipGrace`는 **플레이어에만 있는 개념**(다운 유예)이다. 인터페이스에 올리면 `NpcHealth`는 받아서 버리는 파라미터가 되고, 호출처 6곳(`Baton` ×2, `NpcResistState`, `LightningEvent`, `TrafficVehicle`, `BombBlast`) 중 넷이 영구히 `false`를 넘긴다. 실제로 구분이 필요한 것은 플레이어 2개 호출부뿐이라 진입점을 하나 더 두는 쪽이 싸다 — `NpcHealth`가 같은 이유로 같은 모양이다(두 진입점 → 내부 `ApplyDamage` 합류).

## 유예 여부를 `SetHp`까지 나르는 방법 — private 오버로드 체인

`public ModifyHp(int)`의 시그니처를 **바꾸지 않았다.** 외부 호출처가 셋이고(`HealPack` 회복, `UfoAbductor`·`AbductionEvent.Carry`의 `ModifyHp(-CurrentHp)`) 전부 이 개념이 없다. 기본 파라미터도 쓰지 않았다 — 이 프로젝트는 뜻을 나르는 값에 기본값을 두지 않는 것을 관례로 삼는다(`PlayerIncapacitation.Incapacitate`의 *"기본값을 두지 않는다 — 원인이 곧 복구 경로라, 호출자가 반드시 밝히게 한다"*).

```
TakeDamage        ─┐
                   ├→ ApplyDamage(amount, attacker, skipGrace)
TakeLethalDamage  ─┘        │
                            ↓
                   ApplyHpDelta(-amount, skipGrace) ←─── ModifyHp(delta) (skipGrace: false)
                            ↓
                   SetHp(value, skipGrace)
                            ↓
                   Incapacitate(skipGrace ? Die : Down)
```

⚠ **튜토리얼 하한과 Clamp는 `ApplyHpDelta` 안에 남겨야 한다.** 밖으로 빼면 즉사 경로가 `k_tutorialMinHp`(#663)를 우회해 튜토리얼에서 사람이 죽는다. 그 하한 때문에 **차량 생존 분기가 실재한다** — 피해 120으로도 HP 10에서 멈추는 경로가 있다.

## `PlayerIncapacitation`을 한 줄도 안 고친 이유

`Incapacitate`의 덮어쓰기 가드(`Cause == Die || Cause == Down`이면 무시)는 이 자리에 **도달하지 않는다**:

- 대상이 이미 `Down`이면 `ApplyDamage`의 확인사살 분기가 먼저 잡아 `ServerFinishOff()`로 끝낸다.
- 대상이 이미 `Die`면 HP가 이미 0이라 `previous > 0`이 거짓이다.

남는 것은 `Stun`·`Penalty`·`Abducted`·`Beamed`·`Launched`이고, 그 다섯은 **Die가 덮어야 맞다** — 기절한 동료가 차에 치이면 죽는다. `SetCause`를 지나므로 `ApplyDeathOwnership`·타이머 정리·`ReleaseAllDrags`가 모두 정상 동작한다.

`ServerKillInstantly()`를 새로 두는 안은 기각했다 — 본문이 `Incapacitate(Die)`와 같아지고 Die 진입점이 넷째로 늘어난다.

## 함정 — `RpcTarget.Single(OwnerClientId)`은 타이밍 위험이 아니라 **확정 버그**다

차량 즉사 임펄스를 기존 `HitClientRpc`처럼 보내면 안 된다.

```
TakeLethalDamage → SetHp(0, true) → Incapacitate(Die) → SetCause
                 → ApplyDeathOwnership → NetworkObject.ChangeOwnership(ServerClientId)
```

이 전부가 **같은 호출 스택에서 동기 실행**된다. 그래서 그 다음 줄에서 `victim.OwnerClientId`를 읽으면 **이미 서버**다. 게다가 기존 `HitClientRpc`는 피해자를 `nm.LocalClient.PlayerObject`로 되짚으므로 — **호스트가 자기 몸을 래그돌로 날린다.**

`HomeRunBaton`(#815)이 `Single`을 쓸 수 있는 것은 그쪽 사유(`Launched`)가 **소유권을 옮기지 않기** 때문이다. 사망 임펄스에 그 패턴을 복사하지 말 것.

**그래서 `BombBlast.NotifyBlastDeaths`와 같은 전 피어 브로드캐스트를 쓴다.** 무해한 근거 셋:

1. 래그돌 물리를 도는 피어가 서버로 바뀌므로(`HasMoveAuthority => IsOwner`) 임펄스는 서버에 **반드시** 닿아야 한다.
2. 원격에 가는 것은 무해하다 — 원격의 뼈는 전부 키네마틱이고 `RagdollRig.ApplyImpulse`가 키네마틱 바디를 건너뛴다.
3. 도착 순서 방어가 이미 양쪽에 있다 — `EnterRagdoll`은 멱등이고 반대 순서는 원인 폴링이 막는다([player-ragdoll.md](player-ragdoll.md) §8·§9). 브로드캐스트가 정확히 그 두 가드가 전제하는 형태다.

`SendTo.NotServer`를 쓰므로 `BombBlast`처럼 `if (IsServer) return` 가드를 둘 필요가 없다.

## 임펄스 세기 — 왜 별도 노브인가, 왜 기본값 1.0인가

`TrafficVehicle.m_ragdollImpulseScale = 1f`, `임펄스 = BuildKnockback() * 배율`.

- **넉백 값을 그대로 안 쓰는 이유는 단위가 다르기 때문이다.** 캡슐 넉백은 `CharacterController` 외력 속도 하나이고, 래그돌 임펄스는 뼈마다 `linearVelocity +=`로 얹히며 골반 높이차 회전 편향까지 곱해진다. 합치면 넉백 감각을 튜닝할 때마다 래그돌 궤적이 딸려 변해 **둘 중 하나가 영구히 인질이 된다.** 폭탄도 정확히 이 이유로 `BombBlastProfile`에 별도 노브를 든다.
- **기본값 1.0의 근거는 규모 교차 검증이다.** `HomeRunBaton`이 플레이어를 `m_launchSpeed = 14` / `m_liftRatio = 1.0` / `m_playerLaunchScale = 1.0`으로 날린다(정점 약 10m). 차량 넉백이 14 fwd / 6 up이므로 배율 1.0이면 **홈런 진압봉 한 대와 같은 규모**다. 폭탄의 0.22가 작은 것은 폭심 넉백 값이 캡슐 기준으로 튜닝된 것이라 비교 기준이 아니다.
- **`m_ragdollLiftRatio`는 두지 않았다.** 발사각(14:6 ≈ 23°)이 이미 넉백 필드 둘에 들어 있다. 지금 복제하면 유지할 비율이 둘로 늘어 한쪽만 만졌을 때 조용히 어긋난다. "낮게 쓸린다"가 나오면 그때 추가한다.
- 값은 네 차량 프리팹(`Vehicle_Runaway`, `_Sedan`, `_Ute`, `_Van`)에 **명시해 두었다** — [506 §9-12](506-explosion-ragdoll.md) *"`[SerializeField]` 기본값은 두 번 배신한다"*.

## 함께 닫힌 회귀 — 폭발 사망 래그돌

`BombBlast`는 `CurrentHp == 0`인 사람을 사망자로 보고 임펄스를 보내는데, #725 이후 그 사람은 `Die`가 아니라 `Down`이었다. `PollRagdollCause`(당시 `PollDeath`)가 `IsDead || IsLaunched`만 보므로 `m_sawCauseThisEpisode`가 서지 않고, 1초(`k_causeSyncGraceSeconds`) 뒤 **"부활한 것"으로 오인돼 `ExitToAnimator`가 불렸다** — 폭탄에 죽으면 래그돌로 날아갔다가 다시 일어나 Knockdown 자세로 누웠다.

**이것은 가드의 버그가 아니다.** [player-ragdoll.md §9](player-ragdoll.md)의 *"부활은 죽음을 본 뒤에만 성립한다"* 는 계약이 **정상 동작한 결과**다 — 임펄스는 오는데 사망이 영영 오지 않으므로 가드 입장에서 "1초 안에 안 죽었으면 부활"은 옳다. 버그는 **그 위쪽에서 Die를 세우지 않게 된 것**이고, 틈의 정체는 사망 판정이 `CurrentHp == 0`(체력)과 `IsDead`(원인) **두 값으로 갈려 있던 것**이다. 그래서 `PlayerRagdoll`은 이 이슈에서 한 줄도 고치지 않았다.

> **일반화되는 교훈**: 이 계약은 위쪽에서 **누가 `Die`를 세우는가**가 바뀌면 조용히 반대로 작동한다.

## 즉사에는 암전 시퀀스가 걸리지 않는다 — 의도된 동작

[PlayerDownView](../Assets/Scripts/Player/View/PlayerDownView.cs)의 Die 전이 감지 조건은 `m_lastCause == Down && cause == Die`다. 즉사는 `None → Die`라 걸리지 않아 **완전 암전 1초 · `AudioListener` 무음 · Vivox 뮤트 · 0.6초 페이드가 전부 건너뛰어지고** 관전 카메라로 스냅한다. (PTT 차단은 원인을 가리지 않아 정상 작동한다.)

**고치지 않는다.** 암전 1초의 뜻은 "유예가 끝났다"는 구두점인데, 즉사에는 그 구두점이 **충돌 자체와 날아가는 몸**이고 그게 이 변경의 목적이다. 암전을 걸면 1.6초 동안 래그돌 비행을 가린다. 무음도 틀리다 — 충돌음을 들어야 한다. 실질 손실은 관전 카메라 페이드-인 하나다.

⚠ 이 문단이 이 문서에 있는 이유는, 안 적어 두면 다음 사람이 *"즉사에 암전이 안 걸리는 버그"* 로 보고 조건을 `m_lastCause != Die`로 넓히기 때문이다. 스냅이 거칠다는 플레이테스트 피드백이 나오면 **암전 없이 짧은 페이드만** 넣는다.

## 회귀 확인 결과

| 지점 | 즉사자에게 |
|---|---|
| `ServerRevive`의 `CurrentHp > 0` 가드 | HP 0이라 통과 — 유예 만료 사망과 동일 |
| `PlayerSelfRevive` / `ReviveKit` (`IsDowned` 또는 `IsRevivable`) | `IsRevivable` 참 — 키트 부활 성립 |
| `PlayerReviver` 맨손 구조 (`IsDowned` 요구) | 거부 + "부활 키트가 필요하다" — **의도된 변화** |
| `PlayerCarrier` (`IsDead` 요구) | **즉시 운반 가능**해진다(전에는 60초 대기) — 의도된 개선 |
| `RoundManager` 전멸 판정 (`IsOutOfAction`) | Die 포함 — 무변화 |
| `SuddenEventUtil`의 표적 선정 (`IsTargetable`) | 즉사자도 다운자와 똑같이 제외 — 무변화 |
| 튜토리얼 하한 | HP 10에서 멈춘다 → 즉사 불가 → 캡슐 넉백 (설계로 보장) |
| `m_hitPeople` 재타격 | 캡슐이 꺼지고 뼈는 `Ragdoll` 레이어라 차량 `HitLayers`에서 빠진다 → 다음 차도 시체를 못 친다 |
| `HealPack` / `UfoAbductor` / `AbductionEvent.Carry` | `ModifyHp(int)` 시그니처 불변 — 무변경 |

## 범위 밖 / 후속

- ~~**NPC 차량 시체 임펄스**~~ → **2026-08-28에 넣었다.** 아래 절 참고.
- **환경 확인사살이 코드에서 성립하지 않는다.** GDD 7-5는 "유예 중 추가 피해는 환경·NPC·아군 진압봉 모두 즉시 확인사살"이라 적었지만, 차량은 `CurrentHp <= 0` 가드로, 폭발은 `CollectFieldPlayers`의 `IsTargetable` 필터로 이미 쓰러진 몸을 제외한다. 성립하는 것은 진압봉·저항 NPC뿐이다. 고칠 자리가 둘이고 `CollectFieldPlayers`는 돌발 이벤트 표적 선정 전체가 공유하므로 **별도 이슈**로 남긴다.
- **폭탄으로 동료를 즉사시키면 처치 집계가 없다** — 집계가 확인사살 분기 안에만 있다. 이번 변경으로 "폭탄 = 동료를 확실히 죽이는 수단"이 되므로 집계 정책을 다시 볼 지점이다.
- **밸런스** — 차·폭탄에 완충이 없어져 한 번의 실수가 곧 본부 왕복이다. #524가 접었던 이유이고 #725가 되살린 이유다. 범위가 GDD가 이미 즉사로 적어 둔 두 경로에 한정되므로 진압봉·NPC 전투의 완충은 그대로 남는다.

## NPC도 날아간다 — #634/#768 결정을 뒤집은 근거 (2026-08-28)

`TrafficVehicle.ServerHitNpc`의 옛 주석은 이렇게 적혀 있었다:

> ⚠ 넉백이 피해보다 먼저다: 나중이면 Dead가 되어 씹힌다. **시체 임펄스는 안 건다(가능하지만 #634 결정, #768).**

**뒤집는 근거 넷:**

1. **기술적 장애가 아니었다.** 옛 주석 자신이 *"가능하지만"* 이라고 적고 있다 — 당시의 범위 결정이다.
2. **폭발이 이미 넘어갔다** (#768). `BombBlast.ServerBlastNpcs`가 죽은 NPC에 `EnterRagdoll(impulse)`를
   건다. 차량만 남겨 두면 **같은 환경 피해 둘이 서로 다른 그림**을 낸다.
3. **옛 순서가 만들던 그림이 오히려 나빴다.** 넉백을 먼저 걸어야 했던 이유는 "나중이면 Dead가 되어
   씹힌다"인데, 피해 120으로 시민은 사실상 항상 죽는다 — 결과는 **뻣뻣한 포물선으로 날아가다 도중에
   죽는** 몸이었다. 피해를 먼저 넣고 결과로 갈리게 하면 그 우회 자체가 필요 없어진다.
4. **NavMesh 복귀 걱정은 시체에 해당하지 않는다.** `docs/npc-ragdoll.md`가 *"사망은 반대로 통째로
   끈다 — 시체는 NavMesh로 돌아가지 않고, 죽은 몸에는 깨질 전이도 없다"* 로 못박고 있다. 차도 위
   래그돌의 깨어날 자리 문제(#634 후속)는 **기절 래그돌**의 것이다.

**구현은 폭발과 같은 모양이다** — `wasAlive`를 재고, 피해를 넣고, `IsDead`면 임펄스 / 아니면 넉백.
세기는 플레이어와 같은 `m_ragdollImpulseScale` 하나를 쓴다(폭발도 둘에 같은 곡선을 쓴다).
**RPC가 필요 없다** — NPC 시체 자세는 `RagdollPoseStreamer`가 서버에서만 굴려 흘리므로, 서버인
`ServerHitNpc`에서 그대로 부르면 전 피어가 같은 결과를 본다. 플레이어 쪽의 브로드캐스트 함정이
여기에는 아예 없다.

⚠ **`wasAlive` 가드가 이 변경에서 특히 중요하다.** `EnterRagdoll`은 정착한 시체를 거부하지 않으므로,
가드가 없으면 도로에 누워 있던 몸을 **지나가는 차마다 계속 밀고 간다.** 그 몸은 유치장까지 끌고
가야 판정이 나는 검거 대상이라(GDD 7-3) 도로 저편으로 밀려가면 회수가 어려워진다.

**플레이테스트에서 볼 것** — 날아간 시민 시체가 벽·건물을 뚫거나 맵 밖으로 나가지 않는지, 그리고
**끌고 가서 인계할 수 있는 거리에 떨어지는지**(#690의 "끌고 가면 센다"가 실질적으로 유지되는지).
세기가 과하면 `m_ragdollImpulseScale`을 낮춘다 — 플레이어와 공유하는 값이라 함께 움직인다.

## 검증 상태

`dotnet build Assembly-CSharp.csproj` **오류 0개**. **Play 테스트는 하지 않았다** — 이 프로젝트 관례상 플레이 테스트는 사용자가 직접 한다. 실제로 켜서 봐야 하는 것은 [865-down-ragdoll.md](865-down-ragdoll.md)의 검증 목록과 함께 PR 본문에 체크리스트로 남긴다.
