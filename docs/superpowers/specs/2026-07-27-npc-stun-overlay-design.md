# NPC 스턴 오버레이 전환 설계 (#292)

> 상태: 설계 확정 — 구현 플랜 대기
> 선행: #289(Tier 1 스턴 게이트), #259(NpcController 분리, 완료), #366(NPC 체력, PR #381 리뷰 통과)

## 0. 목표

스턴을 **FSM 상태 전이가 아니라 `CurrentState` 위에 얹는 동기화 플래그**로 바꾼다. 상태 enum이 바뀌지 않으므로 호송·수감·페널티 링크와 각 상태의 타이머가 스턴에 끊기지 않고, 스턴이 풀리면 하던 일을 그대로 재개한다.

이슈 #292의 Arch B를 채택하되, **이슈 작성 이후 바뀐 두 가지**를 반영한다.

- **#259 완료** — 이슈가 "합류 권장, 담당자와 순서 조율"로 걸어둔 선행 조건은 해소됐다. `NpcController`는 이미 partial 10개로 분리돼 있어 `IsStunned`/freeze를 그 구조 위에 바로 얹으면 된다.
- **#366이 기절 생산자를 3개로 늘렸다** — 이슈는 테이저·넉백 KO 둘만 상정했으나 이제 **HP 0 도달**이 추가됐다. 이슈의 "테이저=오버레이 / 넉백 KO=enum" 이원 구조를 그대로 쓰면 기절이 두 종류로 갈린다.

## 1. 확정 결정

1. **테이저·HP 0은 오버레이, 넉백 KO는 상태 전이 유지** — 이슈 원문의 이원 구조를 따르되 HP 0(#366)을 오버레이 쪽에 붙인다. `NpcState.Stunned` enum과 `NpcStunnedState`는 **넉백 착지 전용으로 존속**한다. (§5의 에이전트 정리 문제를 피하기 위한 선택 — 2026-07-27 결정)
2. **게이트를 둘로 분리** — 스턴은 **전 상태 허용**(`CanBeStunned` 삭제), 타격 피해는 **기존 제외 목록을 그대로 유지**(신규 `CanBeDamaged`). 지금은 `CanBeStunned` 하나가 두 역할을 겸하고 있어 분리가 필수다.
3. **스턴 해제 후 동작은 상태군으로 갈린다** — 반응·배회군은 **도주**(#269/#366 확정), 확보·페널티군은 **unfreeze만 하고 원래 상태·링크를 재개**한다.
4. **기절을 묻는 판정은 전부 "둘 중 하나"로 바꾼다** — 결정 1로 기절 경로가 둘이 되므로, `Stunned` enum만 보던 4곳은 `IsStunned || CurrentState == Stunned`를 봐야 한다. 안 고치면 테이저 기절이 밧줄·수갑·표현에서 조용히 누락된다(§4).
5. **체력 회복 지점이 둘이 된다** — 오버레이 해제(`ExitStun`)와 `NpcStunnedState.Exit()` 양쪽에서 `ServerRestoreHp()`를 부른다. 어느 경로로 기절하든 깨어날 때 풀피라는 #366의 불변식을 유지한다.

### 결정 2의 근거 — 왜 게이트를 쪼개나

이슈의 목표는 "어떤 NPC를 쏘든 3초 무력화"다. 그런데 #366이 `TakeDamage`를 같은 `CanBeStunned`에 물려놨기 때문에, 게이트를 그냥 열면 **연행 중인 NPC를 때려 신병에서 빼내는 우회**가 함께 열린다. 팀 결정은 "스턴은 허용, 타격은 계속 차단"이므로 두 게이트를 분리한다.

| 게이트 | 대상 | 제외 상태 | 호출부 |
|---|---|---|---|
| ~~`CanBeStunned`~~ | 삭제 | — | — |
| `CanBeDamaged` (신규) | 타격 피해 | Escorted · Captured · Jailed · Detained · Chasing · PenaltyEscorting | `NpcController.TakeDamage` |
| `IsCapturable` (유지) | 수갑 | 위 6개 + Run · Attack | Handcuffs ×2, PlayerEscorter |

`CanBeDamaged`는 삭제되는 `CanBeStunned`의 제외 목록을 **그대로** 물려받는다. 이름만 바뀌고 타격 동작은 #366과 동일하다.

### 결정 3의 근거 — 이슈 원문과 갈리는 지점

이슈는 "반응·배회군 → `Idle`로 진정"이라고 적었지만, 그 뒤 #366에서 **"기절이 풀리면 도주"**로 확정됐다(#269 원복). 최신 결정을 따른다. 확보·페널티군은 이슈대로 재개다.

## 2. 아키텍처

```
NpcController (partial)
  NetworkVariable<bool> m_isStunned   // 서버 권위, 전 피어 구독
  bool IsStunned                      // 세션 중엔 동기화 값, 오프라인은 진실값
  float m_stunEndTime

  EnterStunned(threat)                // 상태 전이 없음 — 플래그만 켠다
    ├─ 이미 스턴이면 no-op (타이머 리셋 금지 — #366의 엣지 성질 유지)
    ├─ ThreatTarget = threat
    ├─ m_isStunned = true, m_stunEndTime = now + StunSeconds
    └─ 에이전트 정지 (isStopped / ResetPath)

  Update()
    ├─ 서버 아님 → return
    ├─ m_frozen → return
    ├─ m_knockbackActive → TickKnockback(); return
    ├─ m_isStunned → TickStun(); return      ← 신규 게이트
    └─ m_stateMachine.Tick()

  TickStun()
    ├─ IsRoped면 타이머 정지 (끌려가는 내내 안 깨어남 — 기존 동작 이식)
    ├─ 남은 시간이 StandUpSeconds 이하면 RaiseStandUp() 1회
    └─ 만료 → ExitStun()

  ExitStun()
    ├─ ServerRestoreHp()               ← 오버레이 경로의 회복 지점
    │                                     (넉백 KO는 NpcStunnedState.Exit이 그대로 담당)
    ├─ m_isStunned = false, 에이전트 재개
    ├─ 반응·배회군  → StartFlee(ThreatTarget)
    └─ 확보·페널티군 → 아무것도 안 함 (상태·링크 그대로 재개)
```

`m_isStunned`는 `m_networkState`·`m_syncedHp`와 같은 이중 구조(서버 권위 + 오프라인 폴백)를 쓴다.

### 상태군 분류

| 군 | 상태 | 스턴 해제 시 |
|---|---|---|
| 반응·배회 | Idle · Walk · Run · Attack · Panic · Intruding | `StartFlee(ThreatTarget)` — 위협이 없으면 도주 상태가 알아서 배회로 가라앉힌다 |
| 확보·페널티 | Escorted · Captured · Jailed · Detained · Chasing · PenaltyEscorting | unfreeze만. 상태 유지, 타이머 이어서 진행 |

## 3. `NpcState.Stunned` 참조 8곳의 처리

enum과 `NpcStunnedState`는 **넉백 KO 전용으로 남는다.** 다만 "기절인가?"를 묻던 판정은 이제 두 경로를 모두 봐야 한다.

| 위치 | 현재 | 전환 |
|---|---|---|
| `NpcController.cs:140` | `AddState(Stunned, new NpcStunnedState(...))` | **유지** — 넉백 착지가 쓴다 |
| `NpcController.Knockback.cs:40` | 착지 상태 = `Stunned` | **유지** — 상태 전이라야 이전 상태의 `Exit()`이 에이전트를 정리한다 |
| `NpcController.Reaction.cs:91` | `EnterStunned`의 `ChangeState(Stunned)` | 플래그 세팅으로 교체 (테이저·HP 0 경로) |
| `NpcStateRules.cs:45` | `IsRopeable(state) => state == Stunned` | 둘 다 허용 + 컨트롤러를 받도록 시그니처 변경 |
| `PlayerEscorter.cs:480` | `ResolveReaction`의 `== Stunned` | 둘 다 허용 |
| `PlayerEscorter.RopeDrag.cs:132` | 끌기 중단 검사 `!= Stunned` | 둘 다 허용 |
| `NpcAnimationDriver.cs:191,202` | 기절 포즈 판정 | 둘 다 허용 — 오버레이는 enum이 안 바뀌므로 **플래그 구독**을 추가 |

판정이 흩어지지 않도록 헬퍼 하나로 모은다.

```csharp
// NpcStateRules — 기절 경로가 둘(오버레이/넉백 KO)이라 호출부가 매번 OR를 쓰지 않게 한다
public static bool IsIncapacitated(NpcController npc) =>
    npc.IsStunned || npc.CurrentState == NpcState.Stunned;
```

### 두 경로가 겹칠 때

테이저를 맞아 오버레이가 켜진 NPC가 폭발에 날아가면 두 기절이 동시에 성립한다. `Update`의 게이트 순서가 넉백 → 오버레이 → FSM이라 비행 중에는 오버레이 타이머가 멈추고, 착지 후에는 오버레이 게이트가 `NpcStunnedState.Tick`을 가로챈다.

→ **넉백이 우선한다.** `ApplyKnockback`이 오버레이를 끄고(`m_isStunned = false`) enum `Stunned`로 일원화한다. 기절 시간은 넉백 쪽 기준으로 새로 흐른다.

## 4. 안 옮기면 조용히 죽는 것 (이슈에 빠져 있던 항목)

이슈의 파일 목록은 `NpcStateRules`를 "게이트 규칙 갱신"으로만 적고 `IsRopeable`을 언급하지 않았다. 그대로 두면:

```csharp
public static bool IsRopeable(NpcState state) => state == NpcState.Stunned;
```

테이저로 기절시킨 NPC가 `NpcState.Stunned`가 아니게 되어 **밧줄로 못 끈다.** GDD 8-3의 "테이저(기절) → 밧줄(운반)" 콤보가 통째로 죽는다. 호출부는 `Rope.cs` 2곳과 `PlayerEscorter.RopeDrag.cs` 1곳, 그리고 raw 비교 1곳(`RopeDrag.cs:132`)이다.

`IsRopeable`은 `NpcState`가 아니라 컨트롤러를 받도록 시그니처를 바꾼다 — 오버레이는 상태값이 아니기 때문이다. 넉백 KO도 계속 밧줄 대상이어야 하므로 §3의 `IsIncapacitated`를 그대로 쓴다.

## 5. 위험 · 미해결

### 넉백 착지의 Exit 정리 — 해소됨 (결정 1)

`ApplyKnockback`은 비행 시작 **전에** `ChangeState`를 걸어 이전 상태의 `Exit()`이 에이전트를 정리하게 만든다(주석에 명시된 의도). 넉백까지 오버레이로 바꿨다면 그 정리가 실행되지 않아, 저항 중 폭발에 날아간 NPC는 `NpcResistState.Exit`이 되돌리던 `speed`·`stoppingDistance`가 그대로 남는다.

→ **넉백만 상태 전이를 유지**하는 것으로 결론냈다(2026-07-27). 대신 기절 경로가 둘로 갈리는 비용을 §3·§4에서 흡수한다.

### 기절 경로가 둘이라는 비용

앞으로 "기절인가?"를 묻는 코드가 생길 때마다 두 경로를 다 봐야 한다. `IsIncapacitated` 헬퍼로 모아두지만, 헬퍼를 안 쓰고 `CurrentState == Stunned`만 새로 짜면 테이저 기절이 조용히 누락된다. 리뷰에서 봐야 할 항목이다.

### 페널티군 스턴 허용은 #277~#279와 충돌한다

`Detained`/`Chasing`/`PenaltyEscorting`에 스턴이 걸리면 오검거 페널티 추격대를 테이저로 3초 멈출 수 있다. #277~#279는 "반격 수단은 격퇴(호루라기 #250)뿐"으로 설계했으므로 의도가 어긋난다.

2026-07-27 결정: **스턴 허용**(타격은 차단). 이슈가 "팀 확정 필요"로 남긴 항목이므로 팀 공유가 필요하다.

### 스턴 중 수갑

이슈대로 `IsCapturable`에 `|| IsStunned`를 더해, 스턴 중이면 `Run`/`Attack`이어도 수갑이 채워지게 한다. 성공 시 스턴을 해제하고 `StartEscort`로 넘긴다 — 이때 `ExitStun`의 "반응군이면 도주" 분기를 타면 안 된다(수갑을 채웠는데 도망간다). 해제 경로를 **만료**와 **강제 해제**로 나눌 것.

## 6. 테스트

Unity Play 단독 + **MPPM 2인**.

1. 13개 상태 각각에서 테이저 피격 → 크래시·에러 없음.
2. 반응·배회군: 3초 후 도주. 위협이 없으면 배회로 가라앉음.
3. 확보군: 호송 중 피격 → 3초 정지 후 **호송 재개**(수갑 안 풀림).
4. `Captured` 피격 → #230 인계 방치 타이머가 **리셋되지 않는지**(Arch A가 못 막던 항목).
5. `Jailed` 피격 → 유치장 인원 **이중 집계 없음**.
6. 페널티 집행 중 피격 → 3초 후 추격 재개, 매니저-NPC desync 없음.
7. **밧줄 회귀** — 테이저 기절 → 밧줄로 끌기 성공, 끌리는 동안 안 깨어남.
8. **타격 차단 회귀** — 연행 중 NPC를 E로 타격 → 아무 일 없음(`CanBeDamaged`).
9. HP 0 기절 → 3초 후 도주 + **풀피 회복**(무적 구멍 회귀 — #366).
10. 저항 중 폭발 넉백 → 착지 후 에이전트 속도·정지거리가 원복됐는지(상태 전이 유지의 근거).
11. **두 경로 겹침** — 테이저로 기절시킨 NPC를 폭발로 날림 → 오버레이가 꺼지고 넉백 기절로 일원화되는지, 착지 후 정상적으로 깨어나는지.
12. **넉백 KO도 밧줄로 끌리는지** — `IsIncapacitated`가 두 경로를 다 잡는지 확인.
13. MPPM: 클라 화면에서 기절 포즈가 보이는지 — 오버레이(플래그 구독)와 넉백 KO(enum) **양쪽 다**.
