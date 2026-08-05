# NpcController 컴포넌트 분리 계획 (#503)

> 작성: 2026-08-05 · 관련 이슈 [#503](https://github.com/hyunjin0814/undercover-team4-project/issues/503)
> 2026-07-21 [npccontroller-partial-split-plan.md](npccontroller-partial-split-plan.md)를 **대체**한다. 그 문서의 partial 분리는
> #259로 완료됐고(현재 상태), 이 문서는 그 다음 단계인 **부품 컴포넌트 분리**를 다룬다.
> 아직 착수 전 계획이다 — 진행 중 작업과 겹치는 도메인이 있어 § 6의 대기 조건을 먼저 확인할 것.

## 1. 현재 상태와 문제

`NpcController`는 **partial 12개 파일 · 합계 1,830줄**이다.

| 파일 | 줄 수 | 도메인 |
|---|---|---|
| `NpcController.Rope.cs` | 405 | 밧줄 끌기 (#269/#369/#398) |
| `NpcController.cs` | 338 | 코어 + **도메인 멤버 일부**(§ 8) |
| `NpcController.Knockback.cs` | 185 | 넉백 비행 (#232) |
| `NpcController.Stun.cs` | 182 | 스턴 오버레이 (#292) |
| `NpcController.Penalty.cs` | 170 | 오검거·납치 페널티 (#277~#279/#371) |
| `NpcController.StandUp.cs` | 140 | 줄 풀림 기상 · 재포획 창 (#513) |
| `NpcController.Custody.cs` | 107 | 유치장·좌석·통행 (#228/#415/#462) |
| `NpcController.Health.cs` | 101 | 체력 (#366/#371) |
| `NpcController.Reaction.cs` | 99 | 도주·저항·스윙 (#76/#205/#220) |
| `NpcController.Intrude.cs` | 45 | 침입 (#231) + 수갑 해제(오배치, § 7) |
| `NpcController.Holding.cs` | 31 | 임시 거처 (#291) |
| `NpcController.Escort.cs` | 27 | 연행 (#59) |

파일만 쪼개졌을 뿐 **클래스는 하나**라서 도메인 경계가 컴파일러로 강제되지 않는다 — 모든 private 필드가 서로 다 보이고,
어느 파일에서든 다른 도메인의 내부 상태를 만질 수 있다. 새 도메인이 생길 때마다 이 클래스가 계속 자란다
(최초 계획 시점 11파일 1,573줄 → #513으로 12파일 1,830줄).

## 2. 2026-07-21의 "컴포넌트 분리 비추천" 판단을 뒤집는 근거

이전 계획서는 컴포넌트 분리를 비추천했다 — *"프리팹 수정 + 상태 클래스의 참조 경로 변경이 필요해서 '이동만'
리팩토링이 아니게 된다"*. **"이동만"을 목표로 했을 때는 옳은 판단**이었고 그 목표는 달성됐다. 이제 비용을 실측해 보면
경로 변경 규모는 작다.

### 2-1. 상태 클래스 → 컨트롤러 참조: 285건 중 경로가 바뀌는 것은 76건

`Assets/Scripts/NPC/States/*.cs`의 `m_owner.*` 전수 집계(2026-08-05):

| 참조 | 건수 | 분리 후 |
|---|---|---|
| `Agent` | 159 | 코어 — 무변경 |
| `transform` | 30 | 코어 — 무변경 |
| `name` | 13 | 코어 — 무변경 |
| `StateMachine` | 6 | 코어 — 무변경 |
| `gameObject` | 1 | 코어 — 무변경 |
| 도메인 멤버 (`JailSeat` 9 · `ThreatTarget` 7 · `StartFlee` 4 · `ChaseRepelBy` 4 · `EscortTarget` 4 · `IsAbductionDuty` 3 ···) | **76** | 경로 변경 대상 |

외부 호출부도 도메인별로 작다 — 심볼 기준 파일 수(동명 심볼 포함 상한치): `StartFlee` 10 / `IsRoped` 9 /
`CurrentHp` 7 / `IsStunned` 5 / `IsAbductionDuty` 4 / `ServerApplyKnockback` 2 / `JailSeat` 1.

`IDamageable`은 같은 GameObject에 남으므로 `GetComponent<IDamageable>()`로 찾는 폭발 데미지 경로(`BombDevice`)는 **무변경**이다.

### 2-2. partial 간 상호참조: 12건, 전부 "무력화 5개"에 몰려 있다

"도메인이 서로 얽혀서 하나씩 빼기 어렵다"는 우려를 검증하기 위해 partial 간 **실코드 상호참조를 전수 확인**했다
(주석 제외, 코어의 Tick 호출 제외):

| 방향 | 참조 | 위치 |
|---|---|---|
| Health → Stun | `EnterStunned(...)` (HP 0 도달 순간) | `Health.cs:96` |
| Stun → Health | `ServerRestoreHp()` (오버레이 해제 회복) | `Stun.cs:173` |
| Stun → Rope | `IsRoped` (묶여 있으면 타이머 정지, #390) | `Stun.cs:119` |
| Stun → Escort | `StopEscort()` | `Stun.cs:97` |
| Stun → Reaction | `StartFlee(ThreatTarget)` | `Stun.cs:180` |
| Knockback → Escort | `StopEscort()` | `Knockback.cs:48` |
| Rope → StandUp | `CancelStandUp()` (재포획, #513) | `Rope.cs:151` |
| Rope → Knockback | `m_knockbackActive` (비행 중이면 에이전트 양보) | `Rope.cs:206` |
| StandUp → Rope | `IsTethered` / `IsRoped` (누운 자세 판정) | `StandUp.cs:77` |
| StandUp → Knockback·Stun | `m_knockbackActive` / `HasStunOverlay` (예약 취소) | `StandUp.cs:116` |
| Reaction → Stun | `IsStunned` | `Reaction.cs:30` |

결론 셋:

1. **"상태머신이라 못 뺀다"는 진단은 아니다.** FSM은 이미 `NpcStateMachine` + `Assets/Scripts/NPC/States/*`(13개 상태
   클래스)로 분리돼 있고, 컨트롤러는 상태들이 읽는 **데이터·API 허브**다. 위 12건은 전부 "다른 부품의 공개 메서드
   호출 또는 플래그 읽기"라 `m_stun.EnterStunned(...)` 형태의 기계적 경로 변경으로 끝난다.
2. **다만 도메인은 FSM 상태와 1:1이 아니다.** `Captured` 하나에 Rope·StandUp·Custody가 함께 얹혀 있다 —
   **"상태 하나씩" 빼는 것은 불가능**하고, 분리 단위는 반드시 도메인이어야 한다.
3. **얽힘은 무력화 5개(Health·Stun·Rope·StandUp·Knockback)에 집중돼 있고 양방향 쌍이 둘이다**
   (Health↔Stun, Rope↔StandUp). 한쪽만 먼저 빼면 "A→B는 컴포넌트 참조, B→A는 여전히 코어 내부 호출"인 어정쩡한
   중간 상태가 남는다 → **쌍은 같은 PR로 묶는다.** 나머지 6개(Escort·Holding·Intrude·Penalty·Custody·Reaction)는
   밖으로 나가는 참조가 0~1건이라 하나씩 빼도 무해하다.

## 3. 목표 구성

| 컴포넌트 | 이동 대상 | NetworkVariable / RPC |
|---|---|---|
| `NpcHealth` | 체력 + `IDamageable` + `OnDamaged` | `m_syncedHp` |
| `NpcKnockback` | 넉백 비행 | — |
| `NpcRopeDrag` | 밧줄 끌기 · 무게 · 테더 | `m_ropedSynced`, `m_tetheredSynced`, `m_draggerCountSynced` |
| `NpcStandUp` | 기상 예약 · 재포획 창 (#513) | `m_standUpPendingSynced` |
| `NpcStun` | 스턴 오버레이 | `m_syncedStunned` |
| `NpcPenaltyAgent` | 오검거·납치 페널티 | `m_abductionDuty` + `OnPenaltyDutyChanged` |
| `NpcCustody` | 연행 · 유치장 · 좌석 · 임시 거처 · 수갑 해제 | `m_seatedSynced` |
| `NpcIntruder` | 침입 | — |
| `NpcReaction` | 도주 · 저항 · 스윙 | 스윙 RPC |
| `NpcController` (코어) | NavMeshAgent, FSM 구축·틱, 상태 동기화(#56), Update 게이트 순서, `SetFrozen`, `RaiseStandUp`/`RaiseAttackSwing` 중계, config SO 보관 | `m_networkState` |

`NpcStandUp`을 별 부품으로 두는 이유: 기상 예약은 밧줄 풀림(#513)과 기절 기상(#269) **두 도메인이 공유**한다 —
`RaiseStandUp` 순간 이벤트와 클립 길이(`NpcStunConfig.StandUpSeconds`)를 같이 쓰면서, 판정 자체는 "누가 어떻게
끄는가"(Rope)와 무관한 "언제 일어나는가"다. 어느 한쪽에 넣으면 다른 쪽이 그 내부 상태를 만지게 된다.

## 4. 분리 규약 — 파일럿 PR에서 확정하고 이후 반복

1. **컴포넌트 접근자는 둔다, 값 위임은 두지 않는다.**
   - O: `public NpcRopeDrag Rope => m_rope;` → 호출부는 `npc.Rope.IsRoped`
   - X: `public bool IsRoped => m_rope.IsRoped;` (줄 수만 옮긴 partial과 같아진다)
   - 접근자가 필요한 이유: 상태 클래스는 `m_owner`(코어)만 들고 있어서, 없으면 매 틱 `GetComponent`가 된다.
2. **부품에 `Update`/`FixedUpdate`를 만들지 않는다.** 코어가 `Tick()`을 정해진 순서로 호출하고, 게이트 조건도 코어가
   부품에 묻는다 — `if (m_knockback.IsFlying) { m_knockback.Tick(); return; }`. 근거는 § 5-1.
3. **튜닝 SO는 코어가 계속 들고, 부품이 코어에서 읽는다.** 인스펙터 재배선을 최소화한다(지금도 코어가 SO를 들고 상태
   클래스에 주입한다). 부품이 `[SerializeField]`로 SO를 따로 받으면 프리팹 4개 × 부품 수만큼 배선이 늘어난다.
4. **부품은 `NetworkBehaviour`, `[RequireComponent]`로 누락을 막는다.** 같은 프리팹에 여러 `NetworkBehaviour`는 정상이다.
   단 `[RequireComponent]`는 **이미 배선된 프리팹에 소급 적용되지 않으므로** 프리팹 4개는 수동 추가가 필요하다.
5. **프리팹 배선 대상은 4개** — `NPC_Citizen` · `NPC_Citizen_Generic` · `NPC_Rioter` · `NPC_Streaker`.
   `NPC_Abductor`는 `NPC_Citizen`의 **변형(variant)** 이라 자동 상속된다. 프리팹 YAML 충돌이 나면 머지하지 말고
   **에디터에서 다시 배선**하는 쪽이 빠르고 안전하다.

## 5. 지켜야 할 불변식

### 5-1. Update 게이트 순서가 사양이다

코어 `Update`의 현재 순서 — 각 단계에 "이 순서여야 하는 이유"가 주석으로 박혀 있다:

```
TickRopeDrag() → TickStandUp() → 넉백 게이트 → 스턴 게이트 → FSM Tick
```

- 밧줄은 게이트보다 **먼저** — 묶인 채 기절한 대상은 스턴 오버레이를 단 채 끌려가야 한다 (#390, `TickStun`이
  `IsRoped`면 타이머를 멈추는 것과 짝)
- 기상 대기도 게이트보다 **먼저** — 뒤로 내리면 일어나는 도중 기절·넉백을 맞은 대상의 예약이 영원히 남는다 (#513)
- 스턴은 넉백 **뒤** — 둘이 겹치면 넉백이 이긴다 (#292)

부품이 각자 `Update`를 가지면 이 순서가 Unity의 컴포넌트 실행 순서에 위임된다 — **조용히 깨지는 종류의 회귀**다.

### 5-2. `OnDamaged`는 HP 반영 *직전* 발행이 계약이다 (#371)

구독자(납치 이벤트)가 상태를 바꿀 수 있어야 하고(임무 해제 → 배회) 그 다음에 기절 전이가 얹혀야 순서가 맞는다.
뒤로 옮기면 넉백 기절로 바뀐 상태를 임무 해제가 덮어써 **기절이 조용히 취소**된다.

### 5-3. NetworkVariable 도착 순서를 새로 신뢰하지 않는다

동기화 값이 컴포넌트별로 갈리면 원격 피어의 도착 순서 보장이 더 약해진다. 이미 그 전제로 짠 코드가 있어
방향은 정합하다 — #462 착석 폴링과 #369 끌림 폴링은 "별개 NetworkVariable의 도착 순서를 신뢰하지 않는다"를 근거로
상태 훅 대신 폴링을 쓴다. 도메인별로 **새로 폴링이 필요해지는 지점이 없는지** 확인할 것.

## 6. PR 순서 — 도메인당 브랜치 1개 · PR 1개

독립 도메인 먼저, 무력화 클러스터는 뒤로. 클러스터가 지금 열린 PR 두 개와 겹치는 쪽이기도 하다.

### 진행 중 작업 현황 (2026-08-05 기준 · 착수 전 재확인 필요)

| 진행 중 | 건드리는 파일 | 겹치는 도메인 |
|---|---|---|
| #529 (피격 연출) | `.Health.cs` · `.Stun.cs` · `Baton.cs` · `Taser.cs` · **NPC 프리팹 5개** | **Health · Stun** |
| #522 (반출 수감자 재연행) | `.Custody.cs` · `.Rope.cs` · `.StandUp.cs` · `NpcController.cs` · `NpcStateRules.cs` | **Custody · Rope · StandUp · 코어** |
| `feature/399-chase-bomb` | Bomb 계열만 — NPC 스크립트 무변경 | 없음 |
| `feature/423-knockback-navmesh-recovery` (7/29, PR 없음, #259 이동 이전 경로) | `NpcController.Knockback.cs` | Knockback — **생존 여부 미확인** |

프리팹은 **모든 분리 PR의 공통 충돌면**이라, 스크립트가 안 겹쳐도 프리팹에서 만난다. 위 두 PR이 머지될 때까지는
1단계만 진행하는 것이 안전하다.

### 1단계 — 독립 도메인 (밖으로 나가는 참조 0~1건)

| # | 도메인 | 줄 수 | 비고 |
|---|---|---|---|
| 1 | **`NpcIntruder`** | 45 | **파일럿** — 외부 의존 0, 호출부는 `JailbreakEvent` 하나 (§ 7) |
| 2 | `NpcHolding` | 31 | `HoldingSpot` · `OnReachedHolding` · `SendToHolding` · `NotifyReachedHolding` |
| 3 | `NpcPenaltyAgent` | 170 | `NetworkVariable`(`m_abductionDuty`) 이관 첫 사례 |
| 4 | `NpcCustody` (+ `Escort` 병합) | 107 + 27 | `StartEscort`가 여러 도메인의 진입점이라 함께. 오배치된 `ReleaseFromCustody`도 여기로 |
| 5 | `NpcReaction` | 99 | `StartFlee` 호출부가 10파일이라 1단계 마지막 |

### 2단계 — 무력화 클러스터 (양방향 쌍은 한 PR로)

| # | 도메인 | 대기 조건 |
|---|---|---|
| 6 | **`NpcHealth` + `NpcStun`** (`EnterStunned` ↔ `ServerRestoreHp` 쌍) | #529 머지 후 |
| 7 | **`NpcRopeDrag` + `NpcStandUp` + `NpcKnockback`** (`CancelStandUp` + 플래그 게이트로 3자 결합) | #522 머지 후 |
| 8 | 코어 정리 — 남은 도메인 멤버 이동 확인, partial 0개, 이전 계획서 갱신 마무리 | — |

7번이 부담되면 `NpcKnockback`을 먼저 떼도 된다 — 외부 참조는 `BombDevice`의 `ServerApplyKnockback` 1건뿐이고,
`m_knockbackActive`를 읽는 곳이 Rope·StandUp·코어 게이트 3곳이다(그 3곳을 `m_knockback.IsFlying`으로 바꾸면 된다).

## 7. 파일럿 상세 — `NpcIntruder`

### 이동 대상

| 지금 위치 | 멤버 |
|---|---|
| `Intrude.cs` | `StartIntrude(Transform, float)` · `NotifyIntrudeFinished(bool)` · `NotifyIntrudeUnlockStarted()` |
| **`NpcController.cs` (코어)** | `IntrudeTarget` (118) · `IntrudeUnlockSeconds` (121) · `OnIntrudeFinished` (124) · `OnIntrudeUnlockStarted` (130) |

FSM 전이(`m_stateMachine.ChangeState(NpcState.Intruding)`)가 필요하므로 부품은 코어의 `StateMachine` 접근자를 쓴다.
동기화 값이 없어 `NetworkBehaviour`가 꼭 필요하진 않지만, 서버 전용 가드(`IsSpawned && !IsServer`)를 그대로 쓰려면
`NetworkBehaviour`가 편하다 — 규약 4를 따라 통일한다.

### 변경 파일 (스크립트 4 + 프리팹 4)

| 파일 | 변경 |
|---|---|
| `NPC/Controller/NpcIntruder.cs` | 신규 |
| `NPC/Controller/NpcController.Intrude.cs` | 침입 멤버 제거 — **`ReleaseFromCustody`만 남는다**(아래) |
| `NPC/Controller/NpcController.cs` | 코어에 있던 침입 멤버 4개 제거 + `Intruder` 접근자 추가 |
| `NPC/States/NpcIntrudeState.cs` | `m_owner.IntrudeTarget`(34·42) · `IntrudeUnlockSeconds`(85) · `NotifyIntrudeUnlockStarted`(88) · `NotifyIntrudeFinished`(97) |
| `Events/JailbreakEvent.cs` | 구독·해제 6곳(177·178·282·283·409·410) + `StartIntrude`(197) |
| NPC 프리팹 4개 | 컴포넌트 추가 |

### 주의 — 파일 경계 ≠ 도메인 경계

`NpcController.Intrude.cs`에는 침입과 무관한 **`ReleaseFromCustody()`**(수갑 해제, #228)가 들어 있다. 이 메서드는
`JailSeat`를 비우고 Idle로 돌리는 **Custody 도메인**이므로 4번 PR(`NpcCustody`)에서 옮긴다. 외부 호출부는
`CustodyRouter.cs:66`과 `PlayerEscortCommands.cs:511` 두 곳이다. 따라서 파일럿 PR에서 `Intrude.cs`는 삭제되지 않고
축소만 된다 — **다른 도메인 파일에도 같은 오배치가 있을 수 있으니 도메인 PR마다 파일 전체를 확인할 것.**

## 8. 코어 파일에 남아 있는 도메인 멤버

`NpcController.cs`(338줄)는 순수 코어가 아니다. 아래 멤버들을 각 도메인 PR이 함께 데려가야 코어가 목표치(~200줄)가 된다.

| 코어의 멤버 | 갈 곳 |
|---|---|
| `EscortTarget` (111) | `NpcCustody` |
| `JailSeat` (115) | `NpcCustody` |
| `IsDelivered` · `MarkDelivered` · `ClearDelivered` (68~92) | `NpcCustody` |
| `IntrudeTarget` · `IntrudeUnlockSeconds` · `OnIntrudeFinished` · `OnIntrudeUnlockStarted` (118~130) | `NpcIntruder` |
| `ThreatTarget` (58) · `ThreatSearchRadius` (55) · `OnAttackSwing` (108) · `RaiseAttackSwing` (305) | `NpcReaction` |
| `StunSeconds` (49) | `NpcStun` |
| `m_knockbackVelocity` · `m_knockbackLaunch` · `m_knockbackElapsed` · `m_knockbackActive` · `m_knockbackLandingState` (33~37) | `NpcKnockback` |
| `OnStandUp` (283) · `RaiseStandUp` (287) · `PlayStandUpClientRpc` (295) | 판단 필요 — 기상 모션은 Stun(#269)과 StandUp(#513) 공용이라 **코어 중계로 남기는 편**이 낫다 |

코어에 남는 것: config SO 10개, `m_agent`, `m_stateMachine`, `m_networkState`, `m_frozen`, FSM 구축(`Awake`),
`OnNetworkSpawn/Despawn`, `Start`/`InitBehavior`, `Update` 게이트, `HandleFsmStateChanged`, `SetFrozen`,
부품 접근자, 순간 이벤트 중계.

## 9. 검증

이전 계획서 § 5의 방법을 그대로 쓴다(한 번 해 본 절차다). 컴포넌트 분리에서 달라지는 부분만 적는다.

1. **컴포넌트 누락 검사** — 프리팹 4개에서 부품이 붙어 있는지, `NPC_Abductor`가 상속받았는지 확인.
   `[RequireComponent]`가 있어도 기존 프리팹에는 소급 적용되지 않는다.
2. **멤버 유실 검사** — 분리 전 시그니처 목록과 분리 후 합본 비교 (이전 문서 § 5의 `comm -23` 스크립트를 부품 파일까지
   포함하도록 경로만 바꿔 쓴다).
3. **컴파일** — Unity Console 에러 0. 새 타입을 쓰기 전에 반드시 확인(`CLAUDE.md`).
4. **Play 회귀 (MPPM 2인 이상)** — 도메인 PR마다 해당 항목 + 무력화 조합:
   배회 / 피격→기절→일어나기 / 저항·스윙 / 밧줄 끌기·놓기·줄다리기 / 폭발 넉백 / 테이저 스턴 + 밧줄 콤보(#390) /
   줄 풀림 기상과 재포획 창(#513) / 연행→유치장 착석 / 탈옥 방출 / 오검거 페널티 3단 / 납치 임무(#371) /
   임시 거처 / 라운드 종료 freeze
5. **인스펙터 튜닝값 유실 확인** — SO를 코어에 남기므로(규약 3) 값 자체는 움직이지 않는다. 프리팹 저장 후
   config 참조가 살아 있는지만 본다.

## 10. 착수 전 확인할 것

- [ ] `feature/423-knockback-navmesh-recovery`(7/29, PR 없음)를 다시 쓸 것인지 — 담당자 확인. 살아 있으면 2단계 7번에서
      Knockback을 빼거나 그 브랜치 머지 후로 미룬다.
- [ ] #529 · #522 머지 시점 — 2단계 대기 조건이다.
- [ ] `RaiseStandUp` 계열을 코어 중계로 남길지 `NpcStandUp`으로 옮길지 (§ 8 마지막 행)
- [ ] 다른 partial에도 `ReleaseFromCustody` 같은 오배치가 있는지 (§ 7 주의)
