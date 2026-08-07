# NpcController 컴포넌트 분리 계획 (#503)

> 작성: 2026-08-05 · 관련 이슈 [#503](https://github.com/hyunjin0814/undercover-team4-project/issues/503)
> 2026-07-21 [npccontroller-partial-split-plan.md](npccontroller-partial-split-plan.md)를 **대체**한다. 그 문서의 partial 분리는
> #259로 완료됐고(현재 상태), 이 문서는 그 다음 단계인 **부품 컴포넌트 분리**를 다룬다.
> **진행 중이다 — 1단계는 닫혔고(5개 전부) 남은 것은 2단계 6·7번과 코어 정리 8번이다**
> (#540 · #541 · #542 · #545 · #552). 착수 전 § 6의 진행 상황 표와 대기 조건을 먼저 확인할 것 —
> **2026-08-07 기준 남은 것은 7번 하나이고 착수 조건은 없다** — 자세한 것은 § 0.
>
> **갱신 2026-08-06** — #529·#522가 머지되며 2단계 대기 조건이 대부분 풀렸다(남은 제약은 Knockback 하나다).
> 그 두 PR이 들여온 코드로 실측치가 바뀌어 § 1·§ 2-2·§ 6·§ 8을 재측정했고, 새로 발견된 **공용 헬퍼 공유** 때문에
> 분리 규약이 하나(§ 4-6) 늘었다.
>
> **갱신 2026-08-06 (2차)** — 1단계 5번(#545)까지 머지된 시점의 main 기준으로 § 1·§ 2-2·§ 6·§ 8을 다시 실측했다.
> 가장 큰 변화는 **#537이 열린 PR([#547](https://github.com/hyunjin0814/undercover-team4-project/pull/547))이 된 것**이다 —
> 1단계 4번(`NpcCustody`)의 순서 판단이 "합의할 것"에서 **"#547 뒤로 미룬다"로 확정**됐다(§ 6). 그 PR이
> 좌석 개념을 실제로 걷어내면서 4번의 이동 대상과 `NetworkVariable` 구성도 바뀐다(§ 3).
>
> **갱신 2026-08-06 (3차)** — **#547이 머지됐다**(`0b6c268`). 위 2차 갱신이 예고한 변화가 전부 코드가 됐으므로
> § 1·§ 2-2·§ 3·§ 6·§ 8·§ 9·§ 10의 미래형 표현을 실측값으로 바꿨다. **1단계 4번의 대기 조건은 풀렸고**
> (§ 6에 착수 준비 실측을 붙였다 — 지금 그 절은 결과까지 합쳐 "4번 실측과 결과"가 됐다), 그 PR이
> `SendToJail → ExitStun` **새 상호참조 1건**을 들여와 § 2-2가 14행 15건이 됐다.
> 아직 4번에 착수하지는 않았다 — 이 문서만 현재 기준으로 맞춰 둔 상태다.
>
> **갱신 2026-08-06 (4차)** — **1단계 4번(`NpcCustody`)이 [#552](https://github.com/hyunjin0814/undercover-team4-project/pull/552)로 머지됐다.** 1단계가 이로써 전부 닫혔고(5개 중
> 추출 4개 + 삭제 1개), 남은 것은 2단계 6·7번과 코어 정리 8번이다. § 1·§ 2-2·§ 6·§ 8을 재측정했다.
> **§ 2-2가 처음으로 줄었다 — 14행 15건 → 9행 10건**이고, 그중 가짜 의존이던 헬퍼 1건은 규약 § 4-6대로
> 코어 승격으로 사라졌다(남은 헬퍼는 `SweepHitsObstacle` 하나). 얽힘이 § 2-2 결론 3이 예고한
> **무력화 5개에만 남은 상태**가 실측으로 확인됐다.
>
> **갱신 2026-08-07 (5차)** — **2단계 6번(`NpcHealth`+`NpcStun`)을 마쳤다.** partial이 **3개**만 남았고
> (Rope · Knockback · StandUp — 전부 7번 소관) § 2-2는 **9행 10건 → 5행 5건**이 됐다. § 1·§ 2-2·§ 6·§ 8을
> 재측정했다. 이 PR은 **순수 이동이 아니다** — 회귀 테스트에서 기존 버그 하나가 나와 함께 고쳤고
> (기절이 밧줄에 걸린 대상에서 제대로 안 풀리던 것), 밧줄 전환 잔재 하나를 걷어냈다([#562](https://github.com/hyunjin0814/undercover-team4-project/issues/562)).
> 자세한 것은 § 6 "6번 결과".
>
> **갱신 2026-08-07 (6차)** — 6번이 [#563](https://github.com/hyunjin0814/undercover-team4-project/pull/563)으로
> 머지된 뒤 **main이 7번 대상 파일을 크게 건드렸다**(#557 · #559 · #506 · #554). 코어가 310 → 385줄로 늘었고
> **`Update` 게이트가 하나 늘었다**(§ 5-1 갱신). **7번 관문은 전부 사라졌다** — `feature/506`은 머지됐고(#565)
> `feature/423`은 기다리지 않기로 확정했다(2026-08-07 팀 결정 — #557이 같은 문제를 main에 먼저 넣었다).
> § 1·§ 2-2·§ 5-1·§ 6·§ 8·§ 10을 재측정했다.
> **§ 2-2는 5행 5건 그대로다** — main의 변경이 상호참조를 늘리지 않았다.

## 0. 이어서 하는 사람을 위한 재개 지점 (2026-08-07 · 6번 머지 후 기준)

다른 기기·다른 세션에서 이어받을 때 **이 절만 읽고 시작할 수 있게** 유지한다.

**끝난 것** — 1단계 5개 전부와 2단계 6번. `NpcIntruder`(#540) · ~~`NpcHolding`~~ 삭제(#541) ·
`NpcPenaltyAgent`(#542) · `NpcReaction`(#545) · `NpcCustody`(#552) · `NpcHealth`+`NpcStun`([#563](https://github.com/hyunjin0814/undercover-team4-project/pull/563)).
클래스는 **4파일 1,131줄**이고 남은 partial 3개(Rope · Knockback · StandUp)는 **전부 7번 소관**이다.

**다음 할 것 — 2단계 7번(`NpcRopeDrag` + `NpcStandUp` + `NpcKnockback`)**. 이걸 끝내면 partial이 0이 되고,
남는 것은 코어 정리 8번뿐이다. 실측은 § 6 "**7번 착수 준비 실측**"에 있다.

**착수 조건 — 없다. 바로 시작하면 된다.** 2026-08-06에 세워 둔 브랜치 대기 조건 둘이 모두 풀렸다:

- ~~`feature/506-ragdoll`~~ — 머지됐다(#565). `BombDevice`의 `ServerApplyKnockback` 호출은 그대로다.
- ~~`feature/423-knockback-navmesh-recovery`~~ — **기다리지 않기로 팀 결정(2026-08-07).** 그 브랜치의 미머지
  커밋 주제("넉백으로 NavMesh 밖에 떨어진 NPC 자동 복귀")는 #557로 main에 이미 들어와 있다
  (`TickNavMeshRecovery`). 충돌이 나면 머지 순서로 푼다.

**6번에서 배운 것 둘을 먼저 볼 것:**

- **회귀 테스트에서 기존 버그가 나올 수 있다.** 6번이 그랬다 — 부품화 자체는 무결했는데 무력화 도메인의
  기존 결함이 드러났다(§ 6 "6번 결과"). 순수 이동을 목표로 삼되, 나오면 별도 커밋으로 가른다.
- **밧줄 전환(#446/#390) 잔재가 무력화 코드에 남아 있다.** 6번에서 하나 걷어냈고(#562) 7번은 밧줄 도메인
  본체라 더 나올 가능성이 크다. 수갑을 전제로 쓰인 주석·분기를 발견하면 그때그때 이슈로 남길 것.

**작업 순서** — 앞선 PR들과 동일: 브랜치 → (필요하면 "제 집 정리" 선행 커밋) → 부품 분리 커밋 →
**프리팹 4개 배선 커밋** → 계획서 반영 커밋. 규약은 § 4, 검증은 § 9.

**환경 주의 둘**

- **게임 씬이 Apocalypse 맵이다** — `d98bbae`(2026-08-06, 이현석). **Play 회귀는 이 맵에서 돌린다.**
  #557이 맵 NavMesh 볼륨을 추가하고 재베이크했으므로(`46073f0`) 최신 main으로 받아 둘 것.
- **프리팹은 모든 분리 PR의 공통 충돌면이다**(§ 6). 7번은 프리팹 4개에 부품을 **3개씩** 붙인다.
- **main이 7번 대상 파일을 최근에 크게 고쳤다** — #557(NavMesh 회수) · #559(무력화 시 밧줄 해제) ·
  #554(테이저 납치범 격퇴) · #506(래그돌). 착수 전에 최신 main을 받고 실측을 한 번 더 대조할 것.

## 1. 현재 상태와 문제

`NpcController`는 **partial 12개 파일 · 합계 1,978줄**이다(2026-08-06 실측).

> 아래 표는 **착수 시점의 기준선**이다 — 분리가 진행되면서 실제 파일 수는 줄어든다.
> 진행 상황은 § 6의 체크 표시로 추적한다(도메인 PR마다 이 표를 다시 재는 대신).
>
> **현재(2026-08-07, 6번 머지 후 실측)**: 클래스 파일 **12개 → 4개 · 1,978줄 → 1,131줄**
> (코어 385 + partial 3개 — Rope 390 · Knockback 190 · StandUp 166). 부품 6개는 **1,053줄**.
> 착수 기준선 대비 **-8파일 · -847줄(-43%)** 이고, 코어 자체는 343 → 385줄이다.
> 남은 partial 3개는 **전부 7번 소관**이다 — 7번이 가져가면 partial은 0이 된다.
>
> **코어가 목표치(~200줄)에서 오히려 멀어졌다.** 원인이 둘인데 성격이 다르다:
> ① 부품 배선의 고정 비용(6번에서 296 → 310줄 — 부품 필드·접근자 2쌍 + SO를 여는 `internal` 2개),
> ② **#557이 `TickNavMeshRecovery`를 코어에 얹었다**(310 → 385줄). ②는 세 도메인(밧줄 놓기·넉백 착지·
> 기절 해제)이 함께 만드는 상태 하나를 보는 공용 안전망이라 **코어가 맞는 자리**다(§ 4-6과 같은 성격) —
> 줄어들 몫이 아니라는 뜻이다. 목표치는 7번이 끝난 뒤 다시 잡는 편이 낫다.
>
> **분리로 빠져나간 몫만 보면** 부품 6개 1,053줄 + 삭제된 `Holding` 31줄이다. 부품 쪽 줄 수가 원래
> partial보다 큰 것은 클래스 헤더· `m_owner` 배선·코어에서 함께 데려온 멤버 때문이다.
> 이 리팩토링이 사는 것은 줄 수가 아니라 **컴파일러가 강제하는 경계**이고, 그 차액이 대가다.
>
> (#545 PR 본문에 "10개 → 9개"로 적힌 것은 오기다 — 그 시점 실제 수치는 9개 → 8개였다.)

| 파일 | 줄 수 | 도메인 |
|---|---|---|
| `NpcController.Rope.cs` | 406 | 밧줄 끌기 (#269/#369/#398) |
| `NpcController.cs` | 343 | 코어 + **도메인 멤버 일부**(§ 8) |
| `NpcController.Stun.cs` | 238 | 스턴 오버레이 (#292) + 피격 연출 (#529) |
| `NpcController.Knockback.cs` | 185 | 넉백 비행 (#232) |
| `NpcController.Penalty.cs` | 170 | 오검거·납치 페널티 (#277~#279/#371) |
| `NpcController.Health.cs` | 153 | 체력 (#366/#371) + 피격 연출 (#529) |
| `NpcController.StandUp.cs` | 142 | 줄 풀림 기상 · 재포획 창 (#513) |
| `NpcController.Custody.cs` | 139 | 유치장·좌석·통행 (#228/#415/#462) + 반출 재연행 (#522) |
| `NpcController.Reaction.cs` | 99 | 도주·저항·스윙 (#76/#205/#220) |
| `NpcController.Intrude.cs` | 45 | 침입 (#231) + 수갑 해제(오배치, § 7) |
| `NpcController.Holding.cs` | 31 | 임시 거처 (#291) |
| `NpcController.Escort.cs` | 27 | 연행 (#59) |

파일만 쪼개졌을 뿐 **클래스는 하나**라서 도메인 경계가 컴파일러로 강제되지 않는다 — 모든 private 필드가 서로 다 보이고,
어느 파일에서든 다른 도메인의 내부 상태를 만질 수 있다. 새 도메인이 생길 때마다 이 클래스가 계속 자란다
(11파일 1,573줄 → #513으로 12파일 1,830줄 → #529·#522로 1,978줄. 2주 만에 +405줄이고 파일 수는 그대로다 —
partial 분리가 성장을 막지 못한다는 증거다.)

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

### 2-2. partial 간 상호참조: 5건 — 이제 7번 클러스터에만 남았다

"도메인이 서로 얽혀서 하나씩 빼기 어렵다"는 우려를 검증하기 위해 partial 간 **실코드 상호참조를 전수 확인**했다
(주석 제외, 코어의 Tick·Init 호출 제외). 2026-08-06 1차 재측정에서 **11행 12건 → 15행 16건**으로 늘었고(늘어난 4건은
전부 #529·#522가 들여온 것이며 그중 2건은 **도메인 멤버가 아닌 private 헬퍼 공유**라 성격이 다르다), #545로
Reaction 관련 2건이 빠져 13행 14건, #547이 `Custody → Stun` 1건을 들여와 14행 15건이 됐고,
**1단계 4번(#552)으로 5행 5건이 한꺼번에 빠져 9행 10건이 됐고**(처음으로 줄어든 재측정),
**2단계 6번으로 다시 4행 5건이 빠져 5행 5건이 됐다.** 아래는 6번 완료 후 실측이다.

| 방향 | 참조 | 위치 |
|---|---|---|
| Rope → StandUp | `CancelStandUp()` (재포획, #513) | `Rope.cs:146` |
| Rope → Knockback | `m_knockbackActive` (비행 중이면 에이전트 양보) | `Rope.cs:204` |
| StandUp → Rope | `IsTethered` / `IsRoped` (누운 자세 판정) | `StandUp.cs:98` |
| StandUp → Knockback | `m_knockbackActive` (일어나기 취소 판정) | `StandUp.cs:141` |
| **[헬퍼]** Rope → Knockback | `SweepHitsObstacle(...)` — private, Knockback 도메인 멤버 아님 | `Rope.cs:359` (선언 `Knockback.cs:121`) |

**남은 5건은 전부 7번 클러스터(Rope·StandUp·Knockback) 안쪽이다** — 결론 3이 예고한 그림대로다.
7번이 셋을 가져가면 표가 통째로 비고, 남은 헬퍼도 `SweepHitsObstacle` 하나뿐이다
(`TryWarpNear`는 4번 선행 커밋에서 코어로 올라가 사라졌다 — § 4-6이 실제로 가짜 의존을 지운 첫 사례다).

빠진 자리에는 **부품 참조 9건**이 생겼다 — 남은 partial·코어가 이미 나간 부품을 부르는 경로다. 총량이 줄지
않는다고 분리가 헛돈 것이 아니다: 컴파일러가 강제하는 명시적 경계를 지나가고(`m_custody.` 없이는 접근이
안 된다), 그 자리가 다음 PR의 정리 대상으로 표에 남는다.

| 방향 | 참조 | 위치 | 정리 시점 |
|---|---|---|---|
| Rope → `NpcReaction` | `ThreatTarget` 쓰기 (끌기 시작 시 가해자 기록, #369) | `Rope.cs:139` | 7번 |
| Rope → `NpcCustody` | `SetJailExtracted(false)` (묶이면 반출 흐름 종료, #517) | `Rope.cs:149` | 7번 |
| Knockback → `NpcStun` | `ClearStunOverlay()` (넉백이 오버레이를 이긴다, #292) | `Knockback.cs:39` | 7번 |
| Knockback → `NpcCustody` | `StopEscort()` (넉백이 연행을 끊는다, #232) | `Knockback.cs:48` | 7번 |
| StandUp → `NpcStun` | `ExitStun(false)` (예약 직전 오버레이 해제) | `StandUp.cs:96` | 7번 |
| StandUp → `NpcStun` | `HasStunOverlay` (일어나기 취소 판정) | `StandUp.cs:142` | 7번 |
| 코어 → `NpcHealth` | `InitHealth()` (FSM 시동 전 체력 채우기, #366) | `cs:172` | 코어에 남는다 (§ 8) |
| 코어 → `NpcStun` | `HasStunOverlay` / `Tick()` (Update 스턴 게이트, § 5-1) | `cs:211,213` | 코어에 남는다 (§ 8) |
| 코어 → `NpcCustody` | `SetJailExtracted(false)` (커스터디 이탈 전이) | `cs:228` | 코어에 남는다 (§ 8) |

반대 방향 — 부품이 코어를 부르는 것은 전부 예정된 경로다: `StateMachine`(FSM 전이) · `Agent`·`TryWarpNear`
(코어 소유 유틸) · `CurrentState` · SO 접근자(`StunConfig`·`CommonConfig`·`ChaseConfig`·`ResistConfig`).
**부품 ↔ 부품도 6번에서 처음 생겼다** — `NpcHealth → NpcStun`(`EnterStunned`)과 `NpcStun → NpcHealth`
(`ServerRestoreHp`)이 양방향 쌍이고, `NpcStun → NpcReaction` 2건(`ThreatTarget` 쓰기 · `StartFlee`)이
코어를 거치지 않는다. 쌍을 한 PR로 묶은 덕에 이 넷이 전부 명시적 컴포넌트 호출로 한 번에 정리됐다.

결론 넷:

1. **"상태머신이라 못 뺀다"는 진단은 아니다.** FSM은 이미 `NpcStateMachine` + `Assets/Scripts/NPC/States/*`(13개 상태
   클래스)로 분리돼 있고, 컨트롤러는 상태들이 읽는 **데이터·API 허브**다. 위 10건은 전부 "다른 부품의 메서드
   호출 또는 플래그 읽기"라 `m_stun.EnterStunned(...)` 형태의 기계적 경로 변경으로 끝난다(private인 것은 접근
   수준만 열어 준다). **1단계 5개가 이 예측대로 끝났다** — 호출부 변경은 전부 기계적이었고, 접근 수준을
   열어야 했던 것은 부품 쪽 setter 하나뿐이었다(그리고 4번에서 그것마저 메서드로 닫았다).
2. **다만 도메인은 FSM 상태와 1:1이 아니다.** `Captured` 하나에 Rope·StandUp·Custody가 함께 얹혀 있다 —
   **"상태 하나씩" 빼는 것은 불가능**하고, 분리 단위는 반드시 도메인이어야 한다.
3. **얽힘은 무력화 5개(Health·Stun·Rope·StandUp·Knockback)에만 남았고 양방향 쌍이 둘이다**
   (Health↔Stun, Rope↔StandUp). 한쪽만 먼저 빼면 "A→B는 컴포넌트 참조, B→A는 여전히 코어 내부 호출"인 어정쩡한
   중간 상태가 남는다 → **쌍은 같은 PR로 묶는다.** 나머지 6개(Escort·Holding·Intrude·Penalty·Custody·Reaction)는
   밖으로 나가는 참조가 0~1건이라 하나씩 빼도 무해했고, **1단계로 전부 빠졌다**(추출 4 + 삭제 1, Escort는
   Custody에 병합). "여전히 집중돼 있다"였던 이 문장이 이제 **"거기에만 남았다"** 가 됐다.

   **Custody 판단의 결과 기록** — `Rope → Custody`(`SetJailExtracted`)는 **들어오는** 참조라 걸림돌이 아니라고
   봤고 맞았다(Rope가 코어에 남은 채 `m_custody.SetJailExtracted(...)`를 부른다). `Custody → Rope`(`TryWarpNear`)는
   결론 4의 **가짜 의존**이라 헬퍼를 코어로 올려 지웠다 — 선행 커밋 하나로 끝났고 호출부·동작 무변경이었다.
   `SendToJail → ExitStun`(#547)만 진짜 나가는 참조로 남아 `m_owner.ExitStun(false)`가 됐다 —
   2단계 6번이 Stun을 빼갈 때 `m_owner.Stun.ExitStun(false)`로 경로만 바뀐다.
4. **도메인 멤버가 아닌 private 헬퍼가 파일 경계를 넘어 공유되고 있(었)다.** `TryWarpNear`(NavMesh 워프)와
   `SweepHitsObstacle`(물리 스윕)은 선언된 파일의 도메인 소유물이 아니라 **범용 유틸**이다. 부품에 딸려 보내면
   "Custody가 Rope를 참조한다" 같은 **가짜 의존**이 생겨 위 표가 실제보다 얽혀 보이게 된다 → 규약 § 4-6.

   **`TryWarpNear`는 4번 선행 커밋에서 코어로 올라갔다**(`internal`, 상수도 `k_ropeReleaseSnapRadius` →
   `k_warpSnapRadius`로 개명 — 코어에서 "밧줄 놓기 반경"이라는 이름은 맞지 않는다). 이 규약이 실제로
   가짜 의존을 지운 첫 사례이고, 그만큼 § 2-2 표가 한 줄 줄었다. 남은 것은 `SweepHitsObstacle` 하나 —
   7번이 셋을 한 PR로 가져가므로 코어 승격이 필수는 아니지만 같은 이유로 올려 두는 편이 낫다.

## 3. 목표 구성

| 컴포넌트 | 이동 대상 | NetworkVariable / RPC |
|---|---|---|
| `NpcHealth` | 체력 + `IDamageable` + `OnDamaged` | `m_syncedHp` |
| `NpcKnockback` | 넉백 비행 | — |
| `NpcRopeDrag` | 밧줄 끌기 · 무게 · 테더 | `m_ropedSynced`, `m_tetheredSynced`, `m_draggerCountSynced` |
| `NpcStandUp` | 기상 예약 · 재포획 창 (#513) | `m_standUpPendingSynced` |
| `NpcStun` | 스턴 오버레이 | `m_syncedStunned` |
| ✅ `NpcPenaltyAgent` | 오검거·납치 페널티 | `m_abductionDuty` + `OnPenaltyDutyChanged` |
| ✅ `NpcCustody` | 연행 · 인계 판정 표식 · 수감(순간이동) · 감옥 퇴장 · 수갑 해제 · 반출 표식 | `m_jailExtractedSynced` (#547로 `m_seatedSynced`는 사라졌다) |
| ✅ `NpcIntruder` | 침입 | — |
| ✅ `NpcReaction` | 도주 · 저항 · 스윙 | 스윙 RPC |
| `NpcController` (코어) | NavMeshAgent, FSM 구축·틱, 상태 동기화(#56), Update 게이트 순서, `SetFrozen`, `RaiseStandUp`/`RaiseAttackSwing` 중계, config SO 보관 | `m_networkState` |

`NpcCustody`의 이동 대상은 **#547로 줄어든 상태에서** 부품화됐다 — 좌석 개념이 사라지며 `m_seated`·
`m_seatedSynced`·`IsSeated`·`SetSeated`가 지워졌고 `SetJailAccess`·`JailAreaMask`도 함께 없어졌다(감옥이 별도
NavMesh 섬이 되어 영역 마스크로 막을 일이 없다). 코어의 `JailSeat`는 `JailSpot`이 됐다 — "걸어가 앉을 좌석"이
"순간이동해 설 지점"이다. `TryWarpNear`는 새 `ServerExitJail`이 계속 쓰므로 § 4-6의 헬퍼 승격이 선행 조건이었고,
선행 커밋으로 처리했다. 표에 없던 `IsDelivered` 계열(인계 판정 표식)도 함께 왔다 — 수감·석방과 같은 참조를
세우고 내리는 멤버라 § 8에서 이 부품 몫으로 잡혀 있었다.
임시 거처(`Holding`)는 이 표에서 빠졌다 — 도달 불가 코드로 판정돼 삭제됐다(§ 6 1단계 2번).

**남은 부품 5개는 전부 2단계다** — `NpcHealth`+`NpcStun`(6번)과 `NpcRopeDrag`+`NpcStandUp`+`NpcKnockback`(7번).

`NpcStandUp`을 별 부품으로 두는 이유: 기상 예약은 밧줄 풀림(#513)과 기절 기상(#269) **두 도메인이 공유**한다 —
`RaiseStandUp` 순간 이벤트와 클립 길이(`NpcStunConfig.StandUpSeconds`)를 같이 쓰면서, 판정 자체는 "누가 어떻게
끄는가"(Rope)와 무관한 "언제 일어나는가"다. 어느 한쪽에 넣으면 다른 쪽이 그 내부 상태를 만지게 된다.

## 4. 분리 규약 — 파일럿 PR에서 확정하고 이후 반복

1. **컴포넌트 접근자는 둔다, 값 위임은 두지 않는다.**
   - O: `public NpcRopeDrag Rope => m_rope;` → 호출부는 `npc.Rope.IsRoped`
   - X: `public bool IsRoped => m_rope.IsRoped;` (줄 수만 옮긴 partial과 같아진다)
   - 접근자가 필요한 이유: 상태 클래스는 `m_owner`(코어)만 들고 있어서, 없으면 매 틱 `GetComponent`가 된다.
2. **부품에 `Update`/`FixedUpdate`를 만들지 않는다.** 코어가 `Tick()`을 정해진 순서로 호출하고, 게이트 조건도 코어가
   부품에 묻는다 — `if (m_knockback.IsKnockedBack) { m_knockback.Tick(); return; }`. 근거는 § 5-1.
   (플래그 프로퍼티는 이미 `IsKnockedBack`으로 존재한다 — `Knockback.cs:9`. 새 이름을 만들지 말 것.)
3. **튜닝 SO는 코어가 계속 들고, 부품이 코어에서 읽는다.** 인스펙터 재배선을 최소화한다(지금도 코어가 SO를 들고 상태
   클래스에 주입한다). 부품이 `[SerializeField]`로 SO를 따로 받으면 프리팹 4개 × 부품 수만큼 배선이 늘어난다.

   **읽는 경로는 코어의 `internal` 프로퍼티다** — 3번 PR(#542)에서 `NpcController.ChaseConfig`로 확정했다
   (`NpcPenaltyAgent.ApplyChaseRepel`이 `RepelFleeSeconds`를 읽는다). 부품은 같은 어셈블리라 `public`까지
   열 필요가 없고, 이 방식은 § 4-6의 공용 헬퍼 승격과 같은 관례다. 필요한 SO마다 한 줄씩 늘려 쓴다.
4. **부품은 `NetworkBehaviour`, `[RequireComponent]`는 코어 → 부품 한 방향만 건다.** 같은 프리팹에 여러
   `NetworkBehaviour`는 정상이다. 양방향으로 걸면 순환 의존이 되어 둘 중 하나만 떼는 것이 막히므로 선언은
   코어에만 둔다 — 저장소에 순환 사례가 없고 `ShopStand.cs:23`(허브가 자기 View 부품을 요구)이 같은 구조다.

   **그리고 부품은 프리팹에 명시 저장한다 — 자동 생성에 기대지 않는다.** `[RequireComponent]`는 이미 배선된
   프리팹에도 **소급 적용된다**(파일럿 임포트 로그: `Creating missing NpcIntruder component for NpcController
   in ...` × 4). 그러나 **그 결과가 에셋에 저장되지 않는다** — 저장소가 실제 구성을 기록하지 못하고, 나중에
   `[RequireComponent]`를 떼면 프리팹 4개에서 조용히 사라지며, 무엇보다 **직렬화 필드를 가진 부품은 임포트마다
   인스펙터 값이 초기값으로 돌아간다**(`NpcPenaltyAgent`·`NpcRopeDrag` 등 뒤 도메인에서 터진다).
   인스펙터에서 부품을 한 번 껐다 켠 뒤 저장하면 에셋에 기록된다(그냥 열고 저장만 하면 dirty가 잡히지 않는다).

   **`NetworkVariable`이나 RPC를 든 부품은 명시 저장이 더 중요하다** — 3번 PR(#542)의 `NetworkVariable`과
   5번 PR(#545)의 `ClientRpc`에서 확인했다. NGO의
   NetworkBehaviour 인덱스는 **프리팹의 컴포넌트 구성 순서로 정해지므로**, 에셋에 기록되지 않은 구성은
   피어 간 매칭의 근거가 없다. 전 피어가 같은 프리팹을 받으면 일관되지만 그 "같은 프리팹"이 저장소에
   남아 있어야 한다. 같은 이유로 **컴포넌트 순서 정리는 도메인 PR마다 하지 말 것** — 프리팹 4개가 통째로
   재직렬화되는 큰 diff라 충돌면을 키운다. 필요하면 § 6 8번(코어 정리)에서 한 번에 한다.

   그리고 **`NetworkVariable`은 훅까지 함께 옮긴다** — `OnValueChanged` 구독을 코어의
   `OnNetworkSpawn`/`OnNetworkDespawn`에 남기면 코어가 부품의 private 필드를 보게 되어 분리가 무의미해진다.
   부품이 자기 `OnNetworkSpawn`/`OnNetworkDespawn`을 갖는다 (`NpcPenaltyAgent`의 `m_abductionDuty` 사례).
5. **프리팹 배선 대상은 4개** — `NPC_Citizen` · `NPC_Citizen_Generic` · `NPC_Rioter` · `NPC_Streaker`.
   `NPC_Abductor`는 `NPC_Citizen`의 **변형(variant)** 이라 자동 상속된다. 프리팹 YAML 충돌이 나면 머지하지 말고
   **에디터에서 다시 배선**하는 쪽이 빠르고 안전하다.
6. **도메인 PR은 "제 집 정리" 선행 커밋으로 시작한다.** 파일 경계와 도메인 경계가 어긋난 곳을 먼저 바로잡는다.
   전부 **같은 partial 클래스 안의 이동이라 호출부·프리팹·동작이 무변경**이고, 리뷰가 싸다. 두 종류가 있다:
   - **남의 도메인 멤버** → 제 집 partial로 보낸다. 예: `ReleaseFromCustody`가 `Intrude.cs:35`에 있는데
     짝인 `SendToJail`(`Custody.cs:127`)과 함께 `JailSeat`를 다루는 Custody 멤버다 → `Custody.cs`로 옮긴다.
   - **도메인 멤버가 아닌 공용 헬퍼** → 코어로 올린다. 예: `TryWarpNear`(`Rope.cs:245`, Custody가 사용)와
     `SweepHitsObstacle`(`Knockback.cs:121`, Rope가 사용)은 NavMesh 워프·물리 스윕 유틸이다. 부품에 딸려 보내면
     § 2-2 결론 4의 가짜 의존이 생긴다. 코어에서는 `internal`로 열어 둔다 — 부품은 같은 어셈블리
     (`Assembly-CSharp`, `Assets/Scripts`에 `.asmdef` 없음)이므로 `public`까지 열 필요는 없다.

   정리를 미루면 **분리 후에도 옛 partial 파일이 껍데기로 남는다.** 도메인 PR의 성과는 "partial 파일 하나가
   사라졌다"로 재는 게 정확하므로, 정리를 그 PR 안에 넣는다.

   **점검은 매번 하되 선행 커밋이 늘 생기는 것은 아니다** — 3번(Penalty)은 두 종류 모두 없어 선행 커밋
   없이 갔다(#542). 파일럿과 다른 모양이라 리뷰어가 의아해하므로, 없으면 "점검했고 없었다"를 PR 본문에 적는다.

## 5. 지켜야 할 불변식

### 5-1. Update 게이트 순서가 사양이다

코어 `Update`의 현재 순서 — 각 단계에 "이 순서여야 하는 이유"가 주석으로 박혀 있다:

```
TickNavMeshRecovery() → TickRopeDrag() → TickStandUp() → 넉백 게이트 → 스턴 게이트 → FSM Tick
```

- NavMesh 회수는 **전부보다 먼저** — 뒤로 내리면 스턴 게이트에 가려 기절한 채 굳은 NPC에 영영 닿지 못한다 (#557).
  **2026-08-07에 늘어난 단계다** — 게이트가 6개가 됐다
- 밧줄은 게이트보다 **먼저** — 묶인 채 기절한 대상은 스턴 오버레이를 단 채 끌려가야 한다 (#390)
- 기상 대기도 게이트보다 **먼저** — 뒤로 내리면 일어나는 도중 기절·넉백을 맞은 대상의 예약이 영원히 남는다 (#513)
- 스턴은 넉백 **뒤** — 둘이 겹치면 넉백이 이긴다 (#292)

**단계가 늘어난다는 것 자체가 이 불변식의 근거다.** #557은 새 단계를 넣으면서 "왜 맨 앞이어야 하는가"를
주석으로 남겼다 — 부품이 각자 `Update`를 가졌다면 그 판단을 적을 자리조차 없다.

부품이 각자 `Update`를 가지면 이 순서가 Unity의 컴포넌트 실행 순서에 위임된다 — **조용히 깨지는 종류의 회귀**다.

### 5-2. `OnDamaged`는 HP 반영 *직전* 발행이 계약이다 (#371)

구독자(납치 이벤트)가 상태를 바꿀 수 있어야 하고(임무 해제 → 배회) 그 다음에 기절 전이가 얹혀야 순서가 맞는다.
뒤로 옮기면 넉백 기절로 바뀐 상태를 임무 해제가 덮어써 **기절이 조용히 취소**된다.

### 5-3. NetworkVariable 도착 순서를 새로 신뢰하지 않는다

동기화 값이 컴포넌트별로 갈리면 원격 피어의 도착 순서 보장이 더 약해진다. 이미 그 전제로 짠 코드가 있어
방향은 정합하다 — #462 착석 폴링과 #369 끌림 폴링은 "별개 NetworkVariable의 도착 순서를 신뢰하지 않는다"를 근거로
상태 훅 대신 폴링을 쓴다. 도메인별로 **새로 폴링이 필요해지는 지점이 없는지** 확인할 것.

## 6. PR 순서 — 도메인당 브랜치 1개 · PR 1개

독립 도메인 먼저, 무력화 클러스터는 뒤로.

### 진행 중 작업 현황 (2026-08-06 2차 재확인 — #545 머지 후)

| 진행 중 | 상태 | 겹치는 도메인 |
|---|---|---|
| #529 (피격 연출) · #522 (반출 재연행) | ✅ **머지됨** | 해제 — Health · Stun · Custody · Rope · StandUp · 코어 |
| #535 (추격 폭탄) · #538 (사운드) · #544 (1인칭 손) | ✅ **머지됨** — NPC 스크립트 무변경 | 없음. #535가 진압봉을 고쳤지만(밀어내기 제거 → 폭탄 즉발) `Baton.cs`만이고, `BombDevice`의 `ServerApplyKnockback` 호출은 그대로다 |
| **#547** (감옥을 별도 공간으로, 담당 김준영) | ✅ **머지됨** (`0b6c268`) — 좌석 폐기·순간이동 수감/반출이 코드가 됐다. `Custody.cs`·`NpcJailedState`·`PlayerEscortCommands`·`NpcCapturedState`·`NpcStateRules`·`StandUp.cs`·코어 + Jail 계열 대개편 | 해제 — **Custody**. 4번의 대기 조건이 이 머지로 풀렸고 4번은 끝났다(실측·결론은 1단계 표 아래 "4번 실측과 결과") |
| `feature/423-knockback-navmesh-recovery` (담당 김준영) | PR 여전히 미개설. main 대비 `.Knockback.cs` +101줄 · 코어 +12줄 · `NpcStunnedState` · `NpcCommonConfig` · `PlayerEscortCommands` | Knockback — **기다리지 않기로 팀 결정 (2026-08-06)**. 7번과 머지 순서만 맞춘다 |
| `feature/506-ragdoll` (담당 이현진) | 열린 브랜치. NPC 스크립트는 무변경이고 `BombDevice.cs` 161줄을 고친다(사망자 래그돌 임펄스 추가) | 없음 — 다만 7번의 **외부 참조 1건**(`BombDevice`의 `ServerApplyKnockback`)이 그 파일에 있어 머지 순서만 본다 |
| **#401** (스턴/다운 분리 — 밧줄 검거는 다운 대상만, 담당 이현진) | 열린 이슈. **전용 브랜치는 아직 없다**(원격 브랜치 목록 기준) | **Health · Stun** — 2단계 6번이 옮기려는 `EnterStunned`·`ServerRestoreHp`·검거 게이트다 |

프리팹은 **모든 분리 PR의 공통 충돌면**이라, 스크립트가 안 겹쳐도 프리팹에서 만난다.

**브랜치 대기 조건은 전부 풀렸다.** `feature/423`은 `.Knockback.cs`를 크게 고치므로 한때 Knockback을 맨 뒤로
미뤘지만, **기다리지 않고 분리에 포함하기로 했다**(2026-08-06 결정). 따라서 Knockback은 최초 계획대로 2단계
7번 클러스터에 **되돌아온다** — Rope가 `m_knockbackActive`와 `SweepHitsObstacle`을, StandUp이 `m_knockbackActive`를
읽으므로 셋을 갈라 놓으면 § 2-2 결론 3이 경고한 어정쩡한 중간 상태가 남는다. 충돌은 `feature/423` 담당자와
머지 순서로 푼다.

**남은 조율은 둘인데, 성격이 갈렸다** (#539 자동 리뷰가 짚은 두 건이다):

- **#547 ↔ 1단계 4번(`NpcCustody`)** — **끝난 조율이다.** 그 PR을 기다린 판단은 결과적으로 맞았다: 좌석
  멤버 4개와 `SetJailAccess`·`JailAreaMask`가 사라져 **이동 대상이 줄어든 상태에서** 부품화하게 됐고,
  좌석 코드를 부품으로 옮겼다 되지우는 일도 없었다. 기록으로 남길 교훈은 **"열린 PR이 대상 파일을 재작성
  중이면 부품화를 먼저 하지 않는다"** — 반대로 이슈 단계면 "먼저 부품화" 쪽이 유리하다(아래 #401).
- **#401 ↔ 2단계 6번(`NpcHealth`+`NpcStun`)** — 여기는 **"먼저 부품화"가 유리하다.** 전용 브랜치가
  없어 충돌면이 아직 없고, 부품 경계가 생긴 뒤가 재설계하기 쉽다. 다만 상대 담당자의 일정이 걸린
  판단이라 착수 전에 알리는 것이 먼저다.

**순서 제약은 2026-08-07로 전부 사라졌다** — `feature/506`은 머지됐고(#565) `feature/423`은 기다리지 않기로
확정했다. 6번의 #401 통보도 끝났다. (위 두 문단은 그 결정에 이르기까지의 판단 기록으로 남긴다.)

1단계/2단계 구분은 "얽힘이 적은 것부터"라는 뜻이고 **도메인 간 선후 의존이 아니다** — 6·7번 중 어느 것을
먼저 해도 된다.

**1단계가 닫혔다** — 5개 중 추출 4개(#540 · #542 · #545 · 4번) + 삭제 1개. 남은 것은 2단계 6·7번과 8번이다.
6번을 먼저 하면 4번이 남긴 `m_owner.ExitStun` 1건이 함께 정리되고, 7번을 먼저 하면 `SweepHitsObstacle` 헬퍼가
정리된다 — 어느 쪽이든 § 2-2 표가 절반씩 빈다.

### 1단계 — 독립 도메인 (밖으로 나가는 참조 0~1건)

| # | 도메인 | 줄 수 | 비고 |
|---|---|---|---|
| 1 | ✅ **`NpcIntruder`** (#540 머지) | 45 | **파일럿** — 규약 § 4-1·4-2·4-4·4-6을 실제 코드로 확정했다. 선행 커밋으로 `ReleaseFromCustody`를 `Custody.cs`에 보내 `Intrude.cs`를 삭제 (§ 7) |
| 2 | ~~`NpcHolding`~~ → **삭제** | 31 | **추출하지 않는다 — 도달 불가 코드였다.** `60f42aa`(#310 "경범죄자도 유치장 수감 — 임시 거처 소멸 폐지")가 `JailbreakEvent`·`MisdemeanorLoiterer`·`SpawnedNpcEvent`의 호출부·구독을 전부 지웠고(207줄), 컨트롤러 API와 FSM 상태만 고아로 남아 있었다. 뽑았으면 죽은 `NetworkBehaviour`를 프리팹 4개에 붙일 뻔했다 |
| 3 | ✅ **`NpcPenaltyAgent`** (#542) | 170 | `NetworkVariable`(`m_abductionDuty`) 이관 첫 사례 — 훅까지 함께 옮기는 규약(§ 4-4)과 SO를 읽는 `internal` 접근자(§ 4-3)를 확정했다. "제 집 정리" 선행 커밋은 없었다(오배치 없음) |
| 4 | ✅ **`NpcCustody`** (#552, + `Escort` 병합) | 135 + 27 → 237 | **§ 4-6 헬퍼 승격의 첫 사례** — 선행 커밋으로 `TryWarpNear`를 코어에 올려 가짜 의존을 지웠고(§ 2-2 결론 4), 그 결과 § 2-2 표가 처음으로 줄었다(15건 → 10건). 코어 멤버 5개를 함께 데려가 § 8에서 이 도메인 행이 전부 사라졌다. `EscortTarget`의 `internal` setter는 없애고 `internal ClearEscortTarget()`으로 닫았다(아래 주의 2의 결론) |
| 5 | ✅ **`NpcReaction`** (#545) | 99 | `ClientRpc`(`PlayAttackSwingClientRpc`) 이관 첫 사례 — RPC 라우팅도 프리팹 구성이 정하는 NetworkBehaviour 인덱스를 탄다(§ 4-4). 코어의 반응 멤버 4개를 함께 데려가 § 8에서 이 도메인 행이 사라졌다. `StartFlee` 호출부가 10파일이라 1단계 마지막으로 잡았지만, **4번보다 먼저 갔다** — 4번은 #537 순서 합의가 남아 있고 5번은 대기 조건이 없다. "제 집 정리" 선행 커밋은 없었다(오배치 없음) |

#### 4번 실측과 결과 (2026-08-06 착수 전 실측 / 완료 후 대조)

**이동 대상 11개** — `Custody.cs` 6개(`ServerExitJail` · `FindFollowersOf` · `IsJailExtracted` · `SetJailExtracted` ·
`SendToJail` · `ReleaseFromCustody`) + `Escort.cs` 2개(`StartEscort` · `StopEscort`) + 코어 3종(`EscortTarget` ·
`JailSpot` · `IsDelivered`/`MarkDelivered`/`ClearDelivered`). `NetworkVariable`은 `m_jailExtractedSynced` 하나이고
**`OnValueChanged` 훅이 없었다** — 3번(#542)처럼 훅을 함께 옮길 일은 없었다.

> **완료 후 대조** — 서명 기준 실제 이동은 **15개**였다(위 "11개"는 `m_jailExtracted` 2종과
> `MarkDelivered`/`ClearDelivered`를 묶어 센 수다). 유실 0건. 늘어난 것은 부품 배선(`m_owner`·`Awake`)과
> `ClearEscortTarget` 하나. 호출부는 **34건 / 12파일 + 코어 4건**으로 아래 예측(≈35 / 12)과 맞았다.

**경로가 바뀌는 호출부 ≈35건 / 12파일** (착수 전 실측 — 줄 번호는 그 시점 기준):

| 멤버 | 호출부 |
|---|---|
| `StopEscort` | `ArrestJudge:156` · `NpcEscortedState:65,75` · `PlayerEscortCommands:649` · `PlayerEscorter:482` + **코어 partial 2건**(`Knockback.cs:48` · `Stun.cs:149`) |
| `EscortTarget` | `NpcEscortedState:46,48,49,61` + **부품 1건**(`NpcPenaltyAgent:105`) |
| `JailSpot` | `NpcJailedState` 6건 |
| `IsJailExtracted` | `JailIntake:303` · `NpcCapturedState:99` · `NpcStateRules:121,156` · `PlayerEscortCommands:411` |
| `IsDelivered` 계열 | `ArrestJudge:78,84` · `JailbreakEvent:404` · `SuspectRevealer:96` · `NpcStateRules:156` |
| `StartEscort` | `JailIntake:283` · `PlayerEscortCommands:486,625` |
| `SetJailExtracted` | `JailIntake:138,282` + **코어 2건**(`cs:251` · `Rope.cs:152`) |
| `ServerExitJail` | `JailbreakEvent:423` · `JailIntake:346` |
| `ReleaseFromCustody` | `CustodyRouter:66` · `PlayerEscortCommands:537` |
| `SendToJail` | `JailIntake:180` |

주의 셋과 각각의 결론:

1. **`ServerExitJail`은 동명 메서드가 둘이다** — `JailIntake.ServerExitJail(PlayerMovement)`(`JailDoor`가 부른다)는
   **다른 것**이다. grep 결과를 그대로 세면 호출부를 과대 집계한다.
   → 실제로 이 자리에서 걸렸다: NPC 쪽 호출부는 `JailbreakEvent`·`JailIntake` 2건뿐이고, `JailIntake` 안에서
   두 메서드가 **한 화면에 같이 있다**(`ServerExitJail(PlayerMovement)`가 동행들의 `Custody.ServerExitJail`을
   부른다). 리뷰할 때 이 자리를 먼저 볼 것.
2. **`EscortTarget`의 `internal` setter는 없앴다** — 3번 PR이 `NpcPenaltyAgent.SendToDetention`을 위해
   열어 둔 것이다(§ 8).
   → **`StopEscort`와 겹친다고 본 것은 틀렸다.** `SendToDetention`이 원하는 것은 "연행 참조만 끊기"이고
   전이는 `Detained`로 따로 가는데, `StopEscort`는 `Captured`로 전이한다 — 재사용하면 상태를 두 번 바꾼다.
   그래서 `internal void ClearEscortTarget()`을 두고 세터를 `private`으로 닫았다. **밖에서 연행 대상을
   지정하는 문은 `StartEscort` 하나여야 한다**는 것이 이 선택의 근거다(세터를 열면 그 문이 둘이 된다).
3. **`FindFollowersOf`는 `static`이고 `FindObjectsByType<NpcController>`를 돈다** — 부품으로 옮기면
   `FindObjectsByType<NpcCustody>`로 바꾸는 것이 자연스럽고, 그러면 `IsJailExtracted`·`EscortTarget`을
   같은 부품 안에서 읽어 `npc.Custody.` 경유가 사라진다.
   → 그대로 했다. 반환형도 `List<NpcCustody>`가 되어 호출부(`JailIntake`)에서 `followers[i].ServerExitJail(...)`이
   부품 직접 호출이 됐다. **이 PR에서 유일하게 시그니처가 바뀐 멤버**다(나머지 14개는 순수 이동).

### 2단계 — 무력화 클러스터 (양방향 쌍은 한 PR로)

| # | 도메인 | 대기 조건 |
|---|---|---|
| 6 | ✅ **`NpcHealth` + `NpcStun`** (`EnterStunned` ↔ `ServerRestoreHp` 쌍) | **완료** — #401 담당자(이현진) 동의를 받고 착수했다. 결과는 아래 "6번 결과" |
| 7 | **`NpcRopeDrag` + `NpcStandUp` + `NpcKnockback`** (`CancelStandUp` + `m_knockbackActive`·`SweepHitsObstacle` 게이트로 3자 결합) | ✅ **해제 — 착수 조건 없음.** `feature/506`은 머지됐고(#565) `feature/423`은 기다리지 않기로 했다(2026-08-07, § 0). 실측은 아래 "7번 착수 준비 실측" |
| 8 | 코어 정리 — 남은 도메인 멤버 이동 확인, partial 0개, 이전 계획서 갱신 마무리 | — |

#### 7번 착수 준비 실측 (2026-08-07, 6번 머지 후)

**부품은 셋이다** — `NpcRopeDrag`(390줄) · `NpcKnockback`(190줄) · `NpcStandUp`(166줄). 3자 결합이라 한 PR로
가되 컴포넌트는 갈라 둔다(§ 3). 프리팹 4개에 **3개씩** 추가하게 된다. 끝나면 **partial 0개**다.

**이동 대상** — `Rope.cs` 24개 · `Knockback.cs` 6개 + 코어 넉백 필드 5개 · `StandUp.cs` 6개:

| 파일 | 멤버 |
|---|---|
| `Rope.cs` | `m_ropedSynced` · `m_roped` · `m_dragAnchors` · `IsRoped` · `m_tetheredSynced` · `m_tetherCount` · `IsTethered` · `AddTether` · `RemoveTether` · `SyncTethered` · `ClearTethers` · `RopeLength` · `m_dragWeight` · `DragWeight` · `m_draggerCountSynced` · `DraggerCount` · `InitDragWeight` · `StartRopeDrag` · `IsDraggedBy` · `m_dragSlotCount` · `SetDragSlot` · `SetRoped` · `StopRopeDrag` · `PruneDeadAnchors` · `SyncDraggerCount` · `TickRopeDrag` · `ResolveDragPosition` · `k_dragGroundSnapRadius` |
| `Knockback.cs` | `IsKnockedBack` · `ServerApplyKnockback` · `TickKnockback` · `EndKnockback` · `s_sweepBuffer` · `SweepHitsObstacle` + **코어 필드 5개**(`m_knockbackVelocity` · `m_knockbackLaunch` · `m_knockbackElapsed` · `m_knockbackActive` · `m_knockbackLandingState`) |
| `StandUp.cs` | `m_standUpNext` · `m_standUpPending` · `m_standUpRemaining` · `m_standUpDownRemaining` · `m_standUpPendingSynced` · `IsStandingUp` · `SetStandUpPending` · `ServerStandUpThen` · `CancelStandUp` · `TickStandUp` |

**`NetworkVariable`은 4개**(`m_ropedSynced` · `m_tetheredSynced` · `m_draggerCountSynced` · `m_standUpPendingSynced`)
이고 **`OnValueChanged` 훅은 하나도 없다** — 전부 폴링으로 읽힌다(§ 5-3이 근거로 든 #369·#462가 그것이다).
6번의 주의 1(훅 이관)이 여기서는 해당 없다. **Rpc도 0개**라 6번의 주의 5도 없다.

**경로가 바뀌는 호출부 49건 / 15파일:**

| 멤버 | 건수 | 호출부 |
|---|---|---|
| `IsRoped` | 11 | `NpcAnimationDriver` · `NpcEscortedState` · `NpcStateRules` · `PlayerEscorter` · `RopeDragView` · `RopeTether` · `NpcHealthBarPresenter` + **부품 1건**(`NpcStun`) |
| `ServerStandUpThen` | 7 | `NpcCapturedState` · `PlayerEscortCommands` · `PlayerEscorter` |
| `StartRopeDrag` | 6 | `AbductionEvent.Carry` · `JailIntake` · `NpcCapturedState` · `NpcStateRules` · `PlayerEscortCommands` |
| `IsDraggedBy` | 6 | `PlayerEscorter` · `RopeDragLoad` · `RopeTether` |
| `IsStandingUp` | 4 | `NpcAnimationDriver` · `NpcCapturedState` · `NpcJailedState` |
| `RemoveTether` · `DraggerCount` | 각 3 | `PlayerEscortCommands` · `PlayerEscorter` · `RopeDragLoad` |
| `AddTether` · `IsTethered` | 각 2 | `PlayerEscortCommands` · `PlayerEscorter` / `NpcAnimationDriver` + **부품 1건**(`NpcStun`) |
| `RopeLength` · `DragWeight` · `SetDragSlot` · `StopRopeDrag` · `ServerApplyKnockback` | 각 1 | `RopeDragView` · `RopeDragLoad` ×2 · `PlayerEscorter` · `BombDevice` |
| `IsKnockedBack` | **0** | 외부 참조 없음 — 아래 주의 2 |

주의 넷:

1. **`SweepHitsObstacle`을 코어로 올린다** (§ 4-6). 셋이 한 PR로 가므로 필수는 아니지만 도메인 유틸이
   아니다 — `TryWarpNear`와 같은 자리다. 올려 두면 § 2-2 헬퍼 행이 마지막으로 사라진다.
2. **`IsKnockedBack`은 외부 참조가 0건이다.** § 4-2 규약이 "새 이름을 만들지 말고 이걸 쓰라"고 지목한
   프로퍼티인데, 정작 지금은 아무도 안 쓴다(`m_knockbackActive` 직접 참조만 3건). 분리하면 그 셋이
   `m_knockback.IsKnockedBack`이 되어 **비로소 쓰이게 된다** — 규약이 예측한 그림이다.
3. **`TickNavMeshRecovery`는 코어에 남는다** (#557). 밧줄 놓기·넉백 착지·기절 해제 셋이 함께 만드는
   상태 하나를 보는 안전망이라 어느 부품의 소유물도 아니다. 다만 **`Update` 게이트 맨 앞이라는 순서가
   사양이므로**(§ 5-1) 부품에 `Update`를 만들지 않는 규약(§ 4-2)이 여기서도 그대로 걸린다.
4. **`TryWarpNear`가 반경 인자를 받게 바뀌었다** (#557 — `TryWarpNear(from, k_stuckRecoverRadius)`).
   4번에서 코어로 올린 헬퍼가 실제로 두 번째 호출부를 얻은 사례다. § 4-6의 승격 판단이 옳았다는 기록.

**"제 집 정리" 점검** — 세 파일에 남의 도메인 멤버는 없다. 공용 헬퍼는 `SweepHitsObstacle` 하나이고
위 주의 1대로 선행 커밋에서 코어로 올린다. 4번(`TryWarpNear`)과 같은 모양이다.

#### 6번 결과 (2026-08-07)

**예측대로 끝난 것** — 이동 대상 31개(서명 기준) 유실 0건, 시그니처가 바뀐 멤버 0건(4번의 `FindFollowersOf`
같은 사례 없음), 호출부 28건 / 15파일로 아래 예측과 일치. 프리팹 4개는 부품 추가 외 **부수 변경 0건**이고
`GlobalObjectIdHash`도 그대로였다(§ 9-6에서 앞선 PR들이 겪은 잔재 정리조차 없었다).

주의 다섯도 예측대로였다 — 훅은 `NpcStun.OnNetworkSpawn/Despawn`으로 함께 갔고(#542 선례),
`BombDevice`는 `GetComponent<IDamageable>()`라 무변경, `Update` 게이트는 `m_stun.Tick()` 호출 형태가 됐고,
`ClearStunOverlay`·`HasStunOverlay`는 `internal`로 올렸다. Rpc 2개는 프리팹 명시 저장으로 덮었다.

**예측하지 못한 것 — 회귀 테스트에서 기존 버그가 나왔다.** 부품화 자체는 무결했는데, 무력화 도메인의
기존 결함 하나가 이 PR의 Play 회귀에서 드러났다: **밧줄에 걸린 대상의 기절이 제대로 풀리지 않는다.**
증상이 둘이라 원인도 둘이었고 서로 반대쪽이었다.

- 기상 알림(`RaiseStandUp`)이 묶인 몸에도 나가서 벌떡 섰다가 도로 눕는다 → 밧줄이 걸려 있으면 알림만 건너뛴다.
- 타이머가 `IsRoped`면 멈춰서(#269) `IsStunned`가 안 내려간다 → 감전 연출 종료(`NpcShockView`)와 테이저
  무효 판정(`Taser.EvaluateAim`)이 둘 다 그 값을 보므로 연출이 끝나지 않고 재타격도 막힌다.
  → 타이머는 밧줄과 무관하게 흘려보낸다. 확보 상태는 `NpcStateRules.IsReactive`가 아니라 깨어나도 잃는 게 없다.

여기에 **잠재 버그 하나**가 딸려 나왔다 — 줄을 푸는 경로는 전부 줄이 걸린 채 `ServerStandUpThen`을 부르는데
(#513이 강제한 순서), 기절 창 안에 풀기가 들어오면 `TickStandUp`이 예약을 취소하며 후속 콜백까지 버린다.
`JailIntake`만 선행 `ExitStun`으로 자기 경로를 막아 뒀고 나머지는 노출돼 있었다 → 해제를 `ServerStandUpThen`
한 곳으로 모았다.

**밧줄 전환 잔재도 하나 걷어냈다** ([#562](https://github.com/hyunjin0814/undercover-team4-project/issues/562))
— `EnterStunned`가 `Escorted`를 만나면 연행을 끊던 규칙이다. #292가 **수갑 연행을 전제로** 넣은 것인데,
밧줄로 바뀐 뒤로는 '묶어 둔 것'과 '끌던 것'이 같은 확보 상태라 갈라 다룰 이유가 없고, #390이 막기로 한
탈취의 뒷문이기도 했다. **게임 규칙 변경이라 독립 커밋으로 분리했다** — 팀이 반대하면 그것만 드롭한다.

**다음 PR을 위한 교훈 둘:**

1. **"순수 이동"은 목표지 보장이 아니다.** 도메인 코드를 처음으로 한자리에 모아 놓고 읽으면 기존 결함이
   드러난다. 나오면 별도 커밋으로 가르고 PR 본문에서 성격을 나눠 적을 것 — 리뷰어가 "동작 무변경" 검증과
   "이 수정이 옳은가" 판단을 섞지 않게.
2. **밧줄 전환(#446/#390) 잔재가 무력화 코드에 남아 있다.** 수갑을 전제로 쓰인 주석·분기를 발견하면
   그때그때 이슈로 남길 것. 7번은 밧줄 도메인 본체라 더 나올 가능성이 크다.

**후속으로 남긴 것** — 원격 클라에서 줄을 풀면 기상 모션 끝에 수갑 자세가 한 번 스친다. 클라가 자기 시계로
기상 클립을 세는데 배회 복귀 전이(`m_networkState`)가 그보다 늦게 도착해서다. **기절과 무관하게 재현되는
기존 문제**이고 표현 계층(`NpcAnimationDriver`) 소관이라 이 PR에서 손대지 않았다. 고친다면 기상 종료 처리에
`m_controller.IsStandingUp` 가드를 더하는 자리다 — 같은 계열 방어가 `m_ropeBoundMotion`·`m_ropeProneMotion`
엣지에는 이미 있다.

#### 6번 착수 준비 실측 (2026-08-06, #552 머지 후)

**부품은 둘이다** — `NpcHealth`(153줄) · `NpcStun`(238줄). 양방향 쌍이라 **한 PR**로 가지만 컴포넌트는 갈라
둔다(§ 3). 프리팹 4개에 **2개씩** 추가하게 된다.

**이동 대상** — `Health.cs` 13개 · `Stun.cs` 15개 · 코어 3종:

| 파일 | 멤버 |
|---|---|
| `Health.cs` | `m_syncedHp` · `m_hp` · `MaxHp` · `CurrentHp` · `OnDamaged` · `OnHit` · `InitHealth` · `TakeDamage` · `BroadcastDamaged` · `PlayDamagedRpc` · `RaiseDamaged` · `ServerRestoreHp` · `SetHp` + **`IDamageable` 구현** |
| `Stun.cs` | `m_syncedStunned` · `m_stunned` · `IsStunned` · `HasStunOverlay` · `OnStunnedChanged` · `OnTaserStunStarted` · `SetStunned` · `HandleSyncedStunnedChanged` · `BroadcastTaserStun` · `TaserStunStartedRpc` · `m_stunElapsed` · `m_stunDuration` · `m_standingUp` · `m_agentStoppedBefore` · `EnterStunned` · `TickStun` · `ClearStunOverlay` · `ExitStun` |
| 코어 | `StunSeconds` (67) · `m_syncedStunned.OnValueChanged` 구독·해제 (119 · 136) · `Update`의 스턴 게이트 (192~197) |

**경로가 바뀌는 호출부 ≈28건**(NPC 타입 수신자만 — 동명 `PlayerHealth` 멤버는 제외했다):

| 멤버 | 호출부 |
|---|---|
| `IsStunned` | `Baton:265,514` · `Taser:250` · `NpcAnimationDriver:430,595` · `NpcFleeState:280` · `NpcStateRules:100` + **부품 1건**(`NpcReaction:66`) |
| `OnStunnedChanged` | `NpcAnimationDriver:208,218` · `NpcShockView:47,53` |
| `OnDamaged` | `AbductionEvent:161,222,348,378` |
| `ExitStun` | `JailIntake:152` · `PlayerEscortCommands:491` + **부품 1건**(`NpcCustody:129`) |
| `OnTaserStunStarted` | `NpcShockView:46,52` |
| `CurrentHp` · `MaxHp` | `Baton:302` · `NpcHealthBarView:56,61` |
| `TakeDamage` | `Baton:300` (`BombDevice:411`은 **무변경** — 아래 주의 2) |
| `EnterStunned` | `Taser:185` + **코어 partial 1건**(`Health.cs:148`) |
| `ServerRestoreHp` | `NpcStunnedState:79` + **코어 partial 1건**(`Stun.cs:229`) |
| `StunSeconds` | `Taser:190` |

주의 다섯 — **4번보다 무거운 지점이 셋 있다**:

1. **`NetworkVariable` 훅이 코어에 있다.** `m_syncedStunned.OnValueChanged` 구독·해제가 **코어의
   `OnNetworkSpawn`/`OnNetworkDespawn`**(`cs:119` · `cs:136`)에 있다. § 4-4대로 **훅을 부품으로 함께 옮겨야
   한다** — 코어에 남기면 코어가 부품의 private 필드를 보게 되어 분리가 무의미해진다. 4번에는 훅이 없어
   해당 없었고, **3번(#542)의 `m_abductionDuty`가 유일한 선례**다. 그 PR을 먼저 볼 것.
2. **`IDamageable` 구현이 `NpcHealth`로 넘어간다.** 지금은 `partial class NpcController : IDamageable`이다 →
   `class NpcHealth : NetworkBehaviour, IDamageable`. **`BombDevice:411`은 `GetComponent<IDamageable>()`로 찾으므로
   무변경**이고(§ 2-1의 예측이 여기서 확인된다), 대신 `Baton`은 `NpcController target`을 받아 부르므로
   `target.Health.TakeDamage(...)`가 된다. `TakeDamage` 안의 `NpcStateRules.CanBeDamaged(this)`는 `m_owner`가 된다.
3. **`Update` 게이트가 갈라진다.** 코어 `Update`의 스턴 게이트(`if (HasStunOverlay) { TickStun(); return; }`)는
   § 4-2대로 **코어가 부품에 묻고 부품의 `Tick()`을 부르는** 형태가 된다 — 게이트 순서가 사양이므로(§ 5-1)
   부품에 `Update`를 만들지 말 것. `HasStunOverlay`는 `internal` 이상으로 올려야 한다(코어·StandUp이 읽는다).
4. **`internal`로 올릴 것 둘** — `ClearStunOverlay`(Knockback `:39`이 읽는다) · `HasStunOverlay`(StandUp `:135`,
   코어 `Update`). 둘 다 지금 `private`이다.
5. **Rpc 2개가 함께 간다** — `PlayDamagedRpc`(`SendTo.Everyone`) · `TaserStunStartedRpc`. 5번(#545)의
   `ClientRpc` 사례와 같은 이유로 **프리팹 명시 저장이 중요하다**(§ 4-4) — RPC 라우팅이 NetworkBehaviour
   인덱스를 탄다. 부품 둘이 각자 Rpc를 들고 가므로 4번보다 이 위험이 크다.

**정리되는 것 둘**(§ 2-2 표에서 빠진다) — `Stun.cs`의 `NpcReaction` 참조 2건(`:151` `ThreatTarget` 쓰기 ·
`:236` `StartFlee`)이 부품 간 참조가 되고, 그때 `NpcReaction.ThreatTarget`의 `internal` setter를 **좁힐 수 있다**
(남는 쓰기가 `Rope.cs:139` 하나뿐이 되므로 — 7번까지 가면 완전히 닫힌다).

**코어에 열어야 할 SO** — `internal NpcStunConfig StunConfig`(§ 4-3). `StunSeconds`·`EnterStunned`의 기본값·
`KnockdownStunSeconds`가 읽는다. 코어의 `AddState(Stunned, new NpcStunnedState(this, m_stunConfig))`는 **코어에
남는다**(FSM 구축은 코어 책임).

**"제 집 정리" 점검** — `Health.cs`·`Stun.cs`에 남의 도메인 멤버는 없다. 공용 헬퍼도 없다(둘 다 자기 도메인
멤버만 쓴다). **선행 커밋 없이 갈 것으로 보이고**, 그러면 3번·5번과 같은 모양이니 § 4-6대로 "점검했고
없었다"를 PR 본문에 적을 것.

## 7. 파일럿 상세 — `NpcIntruder`

### 이동 대상

| 지금 위치 | 멤버 |
|---|---|
| `Intrude.cs` | `StartIntrude(Transform, float)` · `NotifyIntrudeFinished(bool)` · `NotifyIntrudeUnlockStarted()` |
| **`NpcController.cs` (코어)** | `IntrudeTarget` (118) · `IntrudeUnlockSeconds` (121) · `OnIntrudeFinished` (124) · `OnIntrudeUnlockStarted` (130) |

FSM 전이(`m_stateMachine.ChangeState(NpcState.Intruding)`)가 필요하므로 부품은 코어의 `StateMachine` 접근자를 쓴다.
동기화 값이 없어 `NetworkBehaviour`가 꼭 필요하진 않지만, 서버 전용 가드(`IsSpawned && !IsServer`)를 그대로 쓰려면
`NetworkBehaviour`가 편하다 — 규약 4를 따라 통일한다.

### 변경 파일 (스크립트 6 + 프리팹 4)

커밋 둘로 나눈다 — ① 제 집 정리(§ 4-6) ② 컴포넌트 분리. 리뷰어가 "침입 PR에 왜 Custody 파일이"를 바로 넘어간다.

| 파일 | 변경 | 커밋 |
|---|---|---|
| `NPC/Controller/NpcController.Custody.cs` | `ReleaseFromCustody` 받기 — 짝인 `SendToJail`(127) 옆에 둔다 | ① |
| `NPC/Controller/NpcController.Intrude.cs` | **파일 삭제**(`.meta` 함께) — 침입 멤버는 부품으로, `ReleaseFromCustody`는 Custody.cs로 | ①② |
| `NPC/Controller/NpcIntruder.cs` | 신규 | ② |
| `NPC/Controller/NpcController.cs` | 코어에 있던 침입 멤버 4개 제거 + `Intruder` 접근자 추가 | ② |
| `NPC/States/NpcIntrudeState.cs` | `m_owner.IntrudeTarget`(34·42) · `IntrudeUnlockSeconds`(85) · `NotifyIntrudeUnlockStarted`(88) · `NotifyIntrudeFinished`(97) | ② |
| `Events/JailbreakEvent.cs` | 구독·해제 6곳(177·178·282·283·409·410) + `StartIntrude`(197) | ② |
| NPC 프리팹 4개 | 컴포넌트 추가 | ② |

### 주의 — 파일 경계 ≠ 도메인 경계

`NpcController.Intrude.cs`에는 침입과 무관한 **`ReleaseFromCustody()`**(수갑 해제, #228)가 들어 있다. `JailSeat`를
비우고 Idle로 돌리는 **Custody 도메인**이고, `JailSeat`를 채우는 짝(`SendToJail`, `Custody.cs:127`)과 갈라져 있다.

이걸 4번 PR(`NpcCustody`)까지 미루면 파일럿 후 `Intrude.cs`가 이 메서드 하나만 든 껍데기로 남는다. 그래서 § 4-6에 따라
**파일럿의 선행 커밋에서 `Custody.cs`로 옮기고 `Intrude.cs`를 삭제한다.** 같은 partial 클래스 안 이동이라
외부 호출부(`CustodyRouter.cs:66` · `PlayerEscortCommands.cs:545`)는 **무변경**이고, 4번 PR이 다른 Custody
멤버들과 함께 부품으로 데려갔다(그때 두 호출부가 `npc.Custody.ReleaseFromCustody`로 바뀌었다).

**다른 도메인 파일에도 같은 오배치가 있을 수 있으니 도메인 PR마다 파일 전체를 확인할 것.**
1단계 전체를 점검한 결과 **이 종류(남의 도메인 멤버)는 여기 하나뿐이었고**, 대신 4번에서 다른 종류가
걸렸다 — 공용 헬퍼(`TryWarpNear`). 두 종류를 나눠 보는 § 4-6의 구성이 실제로 필요했다.

> 이 절은 **파일럿 시점(2026-08-05)의 기록**이라 이름·줄 번호가 지금과 다르다 — `JailSeat`는 #547로
> `JailSpot`이 됐고 `SendToJail`은 `NpcCustody.cs`로 옮겨졌다. 규약을 확인하는 용도로 읽을 것.

## 8. 코어 파일에 남아 있는 도메인 멤버

`NpcController.cs`(**385줄** — 착수 시 343줄)는 아직 순수 코어가 아니다. 아래 멤버들을 7번이 함께 데려가야
한다. 줄 번호는 2026-08-07 실측(6번 머지 후)이다.

**목표치(~200줄)는 다시 잡아야 한다** — #557이 `TickNavMeshRecovery`를 얹어 코어가 오히려 커졌는데(310 → 385),
그건 도메인 멤버가 아니라 **코어가 들고 있는 게 맞는 공용 안전망**이다(§ 1). 7번이 넉백 필드 5개를 데려가도
코어는 350줄 안팎에 남을 것으로 보인다.

| 코어의 멤버 | 갈 곳 |
|---|---|
| `m_knockbackVelocity` · `m_knockbackLaunch` · `m_knockbackElapsed` · `m_knockbackActive` · `m_knockbackLandingState` (46~50) | `NpcKnockback` |
| `OnStandUp` · `RaiseStandUp` · `PlayStandUpClientRpc` | 판단 필요 — 기상 모션은 Stun(#269)과 StandUp(#513) 공용이라 **코어 중계로 남기는 편**이 낫다. 6번이 끝난 지금 `NpcStun.Tick`이 이걸 부르므로(부품 → 코어 중계) 7번에서 `NpcStandUp`으로 옮기면 **부품 → 부품**이 된다 |
| `TickNavMeshRecovery` · `m_offNavMeshSeconds` · `m_stuckReported` · `k_stuckGraceSeconds` · `k_stuckRecoverRadius` (#557) | **코어에 남는다** — 세 도메인이 함께 만드는 상태 하나를 보는 안전망이고, `Update` 게이트 맨 앞이라는 순서가 사양이다(§ 5-1) |
| ~~`StunSeconds`~~ | ✅ `NpcStun`으로 이동 완료 (6번). 대신 코어가 `internal NpcStunConfig StunConfig`를 열었다 — `NpcStun`이 지속 시간·기상 클립 길이를, `NpcHealth`가 쓰러짐 기절 시간을 읽는다 |
| ~~`m_syncedStunned.OnValueChanged` 구독·해제~~ | ✅ `NpcStun.OnNetworkSpawn/Despawn`으로 이동 완료 (6번, § 4-4). 코어 `OnNetworkSpawn`에 남은 훅은 `m_networkState` 하나다 |
| ~~`IDamageable` 구현~~ | ✅ `NpcHealth`로 이동 완료 (6번). 코어는 더 이상 이 인터페이스를 구현하지 않는다 — 같은 GameObject라 `BombDevice`의 `GetComponent<IDamageable>()`는 무변경이었다 |
| ~~`IntrudeTarget` · `IntrudeUnlockSeconds` · `OnIntrudeFinished` · `OnIntrudeUnlockStarted`~~ | ✅ `NpcIntruder`로 이동 완료 (#540 파일럿) |
| ~~`ThreatTarget` · `ThreatSearchRadius` · `OnAttackSwing` · `RaiseAttackSwing`~~ | ✅ `NpcReaction`로 이동 완료 (#545). `PlayAttackSwingClientRpc`도 함께 갔고, `ThreatSearchRadius`가 읽을 `ResistConfig`가 코어에 `internal`로 열렸다 |
| ~~`EscortTarget` · `JailSpot` · `IsDelivered` · `MarkDelivered` · `ClearDelivered`~~ | ✅ `NpcCustody`로 이동 완료 (#552). `EscortTarget`의 `internal` setter는 예고대로 없앴고, 대신 `internal ClearEscortTarget()`이 부품 간 경로가 됐다(§ 6 주의 2) |

코어에 새로 생긴 것은 부품 배선과 공용 헬퍼뿐이다 — 부품 필드 6개(`m_custody`·`m_health`·`m_intruder`·
`m_penalty`·`m_reaction`·`m_stun`, 37~42) · 접근자 6개 · SO를 여는 `internal` 프로퍼티 4개
(`ChaseConfig` · `ResistConfig` · `StunConfig` · `CommonConfig`) · `TryWarpNear`와 `k_warpSnapRadius`(§ 4-6 승격).
`internal` SO 접근자는 도메인 PR마다 한 줄씩 늘어나는 항목이다(§ 4-3) — 6번에서 둘이 늘었고, 그만큼
코어가 296 → 310줄로 커졌다. **부품이 늘수록 코어의 배선 줄도 늘어난다**는 것이 이 리팩토링의 고정 비용이다.

코어에 남는 것: config SO 10개, `m_agent`, `m_stateMachine`, `m_networkState`, `m_frozen`, FSM 구축(`Awake`),
`OnNetworkSpawn/Despawn`, `Start`/`InitBehavior`, `Update` 게이트, `HandleFsmStateChanged`, `SetFrozen`,
부품 접근자, 순간 이벤트 중계, 공용 헬퍼(`TryWarpNear` — 7번에서 `SweepHitsObstacle`도 올라올 수 있다).

코어 `HandleFsmStateChanged`가 커스터디 이탈 시 `ClearTethers()`(Rope, `cs:213`)와
`m_custody.SetJailExtracted(false)`(`cs:214`)를 부른다. 이 둘은 "전이와 같은 프레임에 표식을 내린다"는 코어의
조정 책임이므로 코어에 남는다 — Custody 쪽은 4번으로 이미 부품 호출이 됐고, Rope 쪽은 7번에서
`m_rope.ClearTethers()`로 경로만 바뀐다.

## 9. 검증

이전 계획서 § 5의 방법을 그대로 쓴다(한 번 해 본 절차다). 컴포넌트 분리에서 달라지는 부분만 적는다.

1. **컴포넌트 누락 검사** — 프리팹 4개에서 부품이 붙어 있는지, `NPC_Abductor`가 상속받았는지 확인.
   `[RequireComponent]`가 소급 적용해 주긴 하지만 **에셋에 저장되지 않으므로**(§ 4-4) 프리팹 파일에
   부품 guid가 실제로 들어갔는지로 확인한다 — `grep -rl <부품 guid> Assets --include=*.prefab`.
2. **멤버 유실 검사** — 분리 전 시그니처 목록과 분리 후 합본 비교 (이전 문서 § 5의 `comm -23` 스크립트를 부품 파일까지
   포함하도록 경로만 바꿔 쓴다).
3. **컴파일** — Unity Console 에러 0. 새 타입을 쓰기 전에 반드시 확인(`CLAUDE.md`).

   **MCP를 안 쓰면** `%LOCALAPPDATA%\Unity\Editor\Editor.log`로 대조할 수 있다. 이때 **에러 건수만 세면
   오판한다** — 로그는 append-only이고, 파일을 여러 개 고친 뒤 에디터를 열면 **첫 리프레시가 일부 파일만
   집어 컴파일해 실패했다가 다음 리프레시가 나머지를 집어 성공**하는 일이 정상적으로 일어난다(4번에서
   23파일 변경 → 첫 블록 에러 36건, 재컴파일 성공). 마지막
   `CopyFiles Library/ScriptAssemblies/Assembly-CSharp.dll` **뒤에** 에러가 있는지로 판정할 것.
4. **Play 회귀 (MPPM 2인 이상)** — 도메인 PR마다 해당 항목 + 무력화 조합.
   **게임 씬이 Apocalypse 맵으로 바뀌었으므로**(`d98bbae`, 2026-08-06) 그 씬에서 돌린다:
   배회 / 피격→기절→일어나기 / 저항·스윙(**원격 클라에서도** — 스윙은 `ClientRpc`다) / 밧줄 끌기·놓기·줄다리기 /
   폭발 넉백 / 테이저 스턴 + 밧줄 콤보(#390) / 줄 풀림 기상과 재포획 창(#513) / 연행→**문 앞 E로 순간이동
   수감**(#547 — 걸어가 앉히는 경로는 없어졌다) / 반출과 동행 퇴장(#517/#537) / 탈옥 방출 / 오검거 페널티 3단 /
   납치 임무(#371) / 라운드 종료 freeze (임시 거처는 목록에서 빠졌다 — 도달 불가로 삭제된 도메인이다)

   **전부 다 돌릴 필요는 없다.** 순수 이동이라 위험은 ① 프리팹 배선 누락(NRE) ② `NetworkVariable`/RPC 인덱스
   ③ 그 PR에서 *쓰기 방향*이 바뀐 경로에 몰린다. 5번 PR(#545)에서는 이 기준으로 4개만 돌렸고, 남긴 항목은
   PR 본문 "후속으로 미룬 것"에 이유와 함께 적었다 — 자동 리뷰가 누락으로 잡지 않게 하는 관례이기도 하다.

   4번에서 돌린 것 **5개(MPPM 2인, 전부 통과)** — 위 세 위험에 걸리는 항목만 골랐다:
   연행→문 앞 E 수감(`SendToJail`의 `ExitStun` 경유까지 — 감옥 안 배회가 도는지로 확인) / 반출과 동행 퇴장
   (`FindFollowersOf` 반환형 변경) / 오검거 석방(`ReleaseFromCustody`가 `Action`으로 넘어가는 자리) /
   탈옥 방출(`TryWarpNear` 코어 승격을 타는 경로) / **원격 클라에서 반출 신병 E 조준**(유일한 클라 읽기 경로 —
   `m_jailExtractedSynced` → `NpcStateRules.CanResumeUnropedEscort`. NetworkBehaviour 인덱스가 어긋나면 여기서
   먼저 티가 난다). 넉백·기절 콤보는 생략했다 — `StopEscort` 경로가 위 1~3번에 이미 포함된다.
5. **인스펙터 튜닝값 유실 확인** — SO를 코어에 남기므로(규약 3) 값 자체는 움직이지 않는다. 프리팹 저장 후
   config 참조가 살아 있는지만 본다.
6. **프리팹 diff에 딸려 온 것 확인** — 부품을 저장하면 프리팹이 통째로 재직렬화되므로, 부품 추가 외의 변경이
   함께 들어온다. 파일럿(#540)에서 둘 나왔다: ① 그동안 프리팹에 기록된 적 없던 필드가 기본값으로 쓰인다
   (`m_standUpSeconds: 0.585` — **코드 기본값과 같은지 대조할 것**. 다르면 인스펙터에서 조정한 값이 덮인 것이다)
   ② `GlobalObjectIdHash`가 재계산될 수 있다. `DefaultNetworkPrefabs.asset`은 프리팹을 GUID로 등록하므로
   레지스트리는 무관하지만, **옛 해시를 참조하는 곳이 없는지 확인**하고 PR 본문에 적는다.

   4번에서 ①의 변종이 나왔다 — **코드에서 지워진 필드의 잔재가 함께 정리된다.** `NpcAnimationDriver`의
   `m_sitBeginSeconds: 0.7`이 프리팹 4개에서 빠졌다(#547이 좌석을 걷어내며 코드에서 지운 필드인데 YAML에는
   남아 있었다). 읽는 코드가 없으면 무해하지만, **지워진 필드인지 아직 쓰이는 필드인지 grep으로 가를 것** —
   후자면 인스펙터 값이 날아간 것이다. `GlobalObjectIdHash`는 4개 모두 변경 없었다.

## 10. 착수 전 확인할 것

- [x] #529 · #522 머지 — 2026-08-06 확인. 2단계 6·7번 해제
- [x] `feature/423-knockback-navmesh-recovery` — 기다리지 않고 Knockback을 분리에 포함하기로 결정(2026-08-06).
      2단계 7번 클러스터로 되돌렸다. 남은 일은 담당자와 **머지 순서**를 맞추는 것뿐이다
- [x] **`NpcCustody`(1단계 4번) 완료 (#552)** — #547 머지로 대기 조건이 풀린 뒤 착수해 끝냈다. `TryWarpNear` 코어
      승격은 선행 커밋으로 처리(§ 4-6 첫 사례). 실측·결론은 § 6 "4번 실측과 결과"에 있다
- [x] **#401 담당자에게 `NpcHealth`+`NpcStun`(2단계 6번) 착수를 알릴 것** — 2026-08-07 통보·동의 후 착수해
      끝냈다. "이슈 단계면 먼저 부품화"라는 판단(§ 6)은 결과적으로 맞았다 — 충돌 없었다
- [x] `feature/506`(`BombDevice`) — 머지됐다(#565). 조율할 것이 없어졌다
- [x] **`feature/423`을 기다리지 않기로 확정** (2026-08-07 팀 결정) — #557이 같은 문제를 main에 먼저 넣었다.
      **이로써 7번의 착수 조건이 전부 사라졌다.** 충돌이 나면 머지 순서로 푼다
- [ ] `RaiseStandUp` 계열을 코어 중계로 남길지 `NpcStandUp`으로 옮길지 (§ 8 마지막 행) — 7번 PR에서 결정된다.
      6번이 끝나 지금은 `NpcStun.Tick`이 이걸 부르므로, 옮기면 부품 → 부품이 된다
- [ ] [#562](https://github.com/hyunjin0814/undercover-team4-project/issues/562)(테이저가 밧줄 끌기를 끊던
      수갑 시절 규칙) — 6번 PR에 독립 커밋으로 담았다. **팀 확인이 남았고**, 넉백의 같은 절단을 남길지도 그 이슈에 있다
- [x] 다른 partial에도 `ReleaseFromCustody` 같은 오배치가 있는지 (§ 7 주의) — **1단계 전체 점검 결과 그 하나뿐이었다.**
      `Penalty.cs`(#542)·`Reaction.cs`(#545)·`Custody.cs`+`Escort.cs`(4번) 모두 남의 도메인 멤버는 없었다.
      대신 4번에서 **다른 종류**가 걸렸다 — 도메인 멤버가 아닌 공용 헬퍼(`TryWarpNear`). 2단계에서도
      두 종류를 나눠 점검할 것(§ 4-6): 남의 도메인 멤버는 제 집으로, 공용 헬퍼는 코어로
- [x] 파일럿 PR에서 § 4 규약 6개를 실제 코드로 확정 — #540에서 4개, #542·#545가 § 4-3·4-4를 사례로 보강했고
      4번이 § 4-6을 실코드로 확정했다(선행 커밋 하나로 가짜 의존 1건 소멸). **규약 6개 전부 사례가 생겼다**
