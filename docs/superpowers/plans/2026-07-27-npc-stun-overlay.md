# 스턴 오버레이 전환 구현 플랜 (#292)

정본 스펙: [2026-07-27-npc-stun-overlay-design.md](../specs/2026-07-27-npc-stun-overlay-design.md)

## 전역 제약

- **서버 권위 + 오프라인 폴백** — `m_isStunned`는 서버만 쓰고 클라는 읽는다. `m_networkState`·`m_syncedHp`와 같은 이중 구조 (#56).
- **네이밍** — `m_`/`s_`/`k_`, public은 PascalCase (GDD 10-5).
- **프리팹을 건드리지 않는다** — NPC 프리팹 4개에 새 컴포넌트·필드를 추가하지 않는다. 리뷰어가 프리팹 diff를 읽을 수 없다.
- **base 브랜치는 `feature/366-npc-health`** — #366(PR #381)이 `EnterStunned`·`NpcStateRules`·`NpcStunnedState`를 이미 고쳤다. main에서 파면 충돌이 확정이다.

### 태스크 순서가 곧 안전장치다

가장 위험한 중간 상태는 **"테이저 게이트를 먼저 열어버리는 것"** 이다. 오버레이가 없는 채로 게이트를 열면 호송 중인 NPC가 `ChangeState(Stunned)`로 링크를 잃는다 — #289가 막아둔 바로 그 버그다.

그래서 **게이트 개방(Task 5)을 마지막에 둔다.** 그 전까지는 기존 게이트가 확보·페널티군을 계속 막아주므로, 어느 커밋에서 멈춰도 게임이 깨지지 않는다.

### 검증 방식

저장소에 자동화 테스트가 없다(CLAUDE.md). 각 태스크는 **Unity 콘솔 컴파일 확인 + Play 모드 수동 검증**으로 닫는다. 오버레이는 `NetworkVariable` 동기화가 핵심이라 클라 표현은 단독 Play에서 검증되지 않는다 — Task 6에서 MPPM 2인으로 따로 본다.

---

## Task 1: 오버레이 저장소 + 판정 헬퍼 (동작 변화 없음)

플래그를 켜는 쪽은 아직 만들지 않는다. **`IsStunned`가 항상 false인 상태로 판정부터 갈아끼워**, 나중에 플래그가 켜질 때 밧줄·수갑·반응 판정이 저절로 따라오게 만든다. 이 태스크만 놓고 보면 순수 리팩토링이라 회귀가 없다.

**Files:**
- 신규: `Assets/Scripts/NPC/NpcController.Stun.cs`
- 수정: `NpcStateRules.cs`, `Rope.cs`, `PlayerEscorter.cs`, `PlayerEscorter.RopeDrag.cs`

- [ ] **Step 1: `NpcController.Stun.cs` 생성**

`m_isStunned`(NetworkVariable\<bool\>) + `m_stunEndTime` + `IsStunned` 프로퍼티만 둔다. `CurrentHp`와 같은 형태:

```csharp
public bool IsStunned => IsSpawned ? m_syncedStunned.Value : m_stunned;
```

- [ ] **Step 2: `IsIncapacitated` 헬퍼 추가**

```csharp
/// <summary>무력화(기절) 상태인가 — 경로가 둘이라 호출부가 매번 OR를 쓰지 않게 모은다 (#292).
/// 오버레이(테이저·HP 0)와 넉백 KO(NpcState.Stunned)를 함께 잡는다.</summary>
public static bool IsIncapacitated(NpcController npc) =>
    npc != null && (npc.IsStunned || npc.CurrentState == NpcState.Stunned);
```

- [ ] **Step 3: `IsRopeable` 시그니처 변경**

`NpcState`가 아니라 `NpcController`를 받게 바꾸고 본문을 `IsIncapacitated(npc)`로. 호출부 3곳(`Rope.cs:33`, `Rope.cs:46`, `PlayerEscorter.RopeDrag.cs:92`)을 `target`/`DraggingNpc` 전달로 수정.

- [ ] **Step 4: raw 비교 2곳 전환**

`PlayerEscorter.cs:480`(`ResolveReaction`)과 `PlayerEscorter.RopeDrag.cs:132`(끌기 중단 검사)의 `== NpcState.Stunned` 비교를 `IsIncapacitated`로.

- [ ] **Step 5: 컴파일 확인 + 회귀 검증**

Unity 콘솔 에러 없음. 테이저로 기절 → 밧줄로 끌기 → 수갑이 **지금과 똑같이** 동작하는지. 이 태스크는 동작이 바뀌면 안 된다.

- [ ] **Step 6: 커밋** — `스턴 오버레이 — 저장소 골격 + 무력화 판정 헬퍼 (#292)`

---

## Task 2: 스턴 런타임 — `EnterStunned`를 오버레이로 전환

실제 동작이 바뀌는 태스크다. 테이저와 HP 0이 상태 전이 대신 플래그를 켠다.

**Files:** `NpcController.Stun.cs`, `NpcController.Reaction.cs`, `NpcController.cs`

- [ ] **Step 1: `EnterStunned` 재작성**

`ChangeState(NpcState.Stunned)` 대신 플래그를 켠다. **이미 스턴 중이면 no-op** — 추가 타격이 타이머를 리셋하지 못하게 하는 #366의 성질을 그대로 지킨다. 에이전트를 세우고(`isStopped`/`ResetPath`) `m_stunEndTime`을 잡는다.

- [ ] **Step 2: `Update`에 스턴 게이트 추가**

넉백 게이트 **바로 다음**, `m_stateMachine.Tick()` **앞**에 둔다. 순서가 스펙 §3의 겹침 규칙 근거다.

```csharp
if (m_knockbackActive) { TickKnockback(); return; }
if (IsStunned)         { TickStun();      return; }
m_stateMachine.Tick();
```

- [ ] **Step 3: `TickStun` 구현**

`NpcStunnedState.Tick`에서 이식: 밧줄에 묶여 있으면 타이머 정지, 마지막 구간에 `RaiseStandUp()` 1회, 만료 시 `ExitStun`.

- [ ] **Step 4: `ExitStun` 구현 — 만료/강제 두 경로**

```csharp
private void ExitStun(bool resumeReaction)
```

공통: `ServerRestoreHp()`, 플래그 해제, 에이전트 재개.
`resumeReaction`이 true(만료)일 때만 반응·배회군을 `StartFlee`로 보낸다. 확보·페널티군은 아무것도 하지 않는다 — 상태·링크가 그대로 살아 있으므로 다음 `Tick`부터 하던 일을 재개한다.

> **강제 해제가 따로 필요한 이유:** 스턴 중 수갑(Task 5)이 성공했을 때 만료 경로를 타면 `StartFlee`가 걸려 **수갑을 채우자마자 도망친다.**

- [ ] **Step 5: 상태군 판정 추가**

`NpcStateRules`에 반응·배회군 여부를 묻는 판정을 둔다(Idle/Walk/Run/Attack/Panic/Intruding). 확보·페널티군은 그 여집합이다.

- [ ] **Step 6: 컴파일 + Play 검증**

테이저로 배회 시민 기절 → 3초 후 **도주**. HP 0 기절 → 3초 후 도주 + **풀피 회복**(무적 구멍 회귀 — #366). 밧줄로 끄는 동안 안 깨어나는지.

- [ ] **Step 7: 커밋** — `스턴 오버레이 — EnterStunned를 플래그+freeze로 전환 (#292)`

---

## Task 3: 애니메이션 표현을 플래그 구독으로

오버레이는 `m_networkState`를 바꾸지 않으므로, 지금 구조로는 **클라 화면에서 기절 포즈가 안 나온다.**

**Files:** `NpcAnimationDriver.cs`, `NpcController.Stun.cs`

- [ ] **Step 1: `m_syncedStunned.OnValueChanged` 전파**

`m_networkState`가 `HandleNetworkStateChanged`로 전파되는 것과 같은 방식으로 플래그 변경을 밖에 알린다.

- [ ] **Step 2: 드라이버가 두 경로를 모두 보게**

`NpcAnimationDriver.cs:191`(`m_baseState != NpcState.Stunned`)과 `:202`의 상태 집합을 무력화 판정 기준으로 바꾼다. 넉백 KO(enum)와 오버레이 **둘 다** 누운 포즈여야 한다.

- [ ] **Step 3: 컴파일 + Play 검증** — 단독 Play에서 기절 포즈·일어나는 모션이 그대로인지.
- [ ] **Step 4: 커밋** — `스턴 오버레이 — 기절 포즈를 플래그 구독으로 전환 (#292)`

---

## Task 4: 넉백과 겹칠 때의 우선순위

**Files:** `NpcController.Knockback.cs`

- [ ] **Step 1: `ApplyKnockback`에서 오버레이 해제**

테이저로 기절한 NPC가 폭발에 날아가면 두 기절이 동시에 성립한다. 착지 후 오버레이 게이트가 `NpcStunnedState.Tick`을 가로채므로, 넉백을 걸 때 오버레이를 끄고 enum `Stunned`로 일원화한다. 기절 시간은 넉백 기준으로 새로 흐른다.

> 이때 `ServerRestoreHp`를 부르지 않는다 — 회복은 깨어날 때 한 번이면 된다. `NpcStunnedState.Exit`이 담당한다.

- [ ] **Step 2: 컴파일 + Play 검증** — 테이저 → 즉시 폭발 → 착지 후 정상적으로 깨어나는지. 저항 중 폭발 → 착지 후 에이전트 `speed`·`stoppingDistance`가 원복됐는지(상태 전이 유지의 근거).
- [ ] **Step 3: 커밋** — `스턴 오버레이 — 넉백 우선 규칙으로 이중 기절 정리 (#292)`

---

## Task 5: 게이트 개방 — 전 상태 스턴, 타격은 차단 유지

여기서 이슈의 목표가 완성된다. **마지막에 두는 이유는 전역 제약 참고.**

**Files:** `NpcStateRules.cs`, `Taser.cs`, `NpcController.Health.cs`, `Handcuffs.cs`, `PlayerEscorter.cs`, `NpcSubdueInteractable.cs`

- [ ] **Step 1: `CanBeStunned` → `CanBeDamaged` 개명**

제외 목록(Escorted/Captured/Jailed/Detained/Chasing/PenaltyEscorting)은 **그대로** 둔다. 이름과 XML 주석만 "타격 피해" 기준으로 다시 쓴다 — 더 이상 테이저 게이트가 아니다.

- [ ] **Step 2: 호출부 정리**

`NpcController.Health.cs:48`의 `TakeDamage` 게이트를 `CanBeDamaged`로. `NpcSubdueInteractable.cs:53` 주석도 갱신.

- [ ] **Step 3: 테이저 게이트 제거**

`Taser.cs:177`의 `if (!CanBeStunned(...)) return;`를 삭제한다. 이 줄이 사라지는 순간 전 상태 스턴이 열린다.

- [ ] **Step 4: 스턴 중 수갑 허용**

`IsCapturable`이 `Run`/`Attack`을 제외하므로 오버레이만으로는 수갑이 막힌다. 스턴 중이면 통과하도록 조건을 더한다. 성공 시 **강제 해제**(`resumeReaction: false`)로 스턴을 풀고 `StartEscort`로 넘긴다 — Task 2 Step 4의 이유.

- [ ] **Step 5: 컴파일 + Play 검증**

호송 중 NPC 테이저 → 3초 정지 후 **호송 재개**(수갑 안 풀림). 호송 중 NPC를 E로 타격 → **아무 일 없음**(`CanBeDamaged`). `Captured` 테이저 → #230 인계 방치 타이머가 리셋되지 않는지. `Jailed` 테이저 → 유치장 인원 이중 집계 없는지.

- [ ] **Step 6: 커밋** — `스턴 오버레이 — 전 상태 스턴 개방 + 타격 게이트 분리 (#292)`

---

## Task 6: MPPM 검증 + 문서 갱신

- [ ] **Step 1: MPPM 2인 검증**

`m_syncedStunned` 동기화는 단독 Play에서 검증되지 않는다.

1. 클라가 쏜 테이저 → 호스트·클라 **양쪽**에서 기절 포즈.
2. 호스트가 호송 중인 NPC를 클라가 테이저 → 3초 후 양쪽에서 호송 재개.
3. 넉백 KO도 양쪽에서 누운 포즈(enum 경로 회귀).

> 클라이언트 콘솔은 `Library/VP/mppm*/Logs/Editor.log`를 직접 읽어야 한다.

- [ ] **Step 2: 13개 상태 스모크**

각 상태에서 테이저 피격 → 크래시·에러 없음(이슈 완료 기준 1항).

- [ ] **Step 3: GDD 갱신**

7-4/8-3에서 스턴을 서술하는 부분에 "확보·페널티 상태도 스턴되며 원래 하던 일을 재개한다"를 반영. 페널티군 스턴 허용이 #277~#279의 "반격은 격퇴만" 설계와 갈리는 지점이므로 결정 근거로 남긴다.

- [ ] **Step 4: 커밋** — `스턴 오버레이 — GDD 갱신 + 멀티 검증 (#292)`

---

## 완료 후

PR 본문은 팀 고정 4섹션(요약 / 테스트 방법 / 씬·프리팹 변경 / 후속으로 미룬 것).

- **씬/프리팹 변경:** 없음.
- **머지 순서:** #381(#366)이 먼저다. 이 브랜치가 그 위에 있다.
- **후속으로 미룰 것:** `NpcSubdueGaugeHud`/`NpcSubdueInteractable`의 스턴 중 억제(이슈 파일 목록에 있으나 오버레이 도입만으로는 안 열림), 페널티군 스턴 밸런스 재조정(#277~#279 담당자와 협의).
