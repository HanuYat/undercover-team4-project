# 돌발 이벤트 개선 (#291) 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 돌발 이벤트 시스템에 (A) SuddenEventManager 명시 리스트 리팩토링, (B) 경범죄 이벤트 NPC의 임시 거처 이송 후 소멸, (C) 괴한→차저(돌진형) 동작 변경을 적용한다.

**Architecture:** A는 자동수집을 인스펙터 직렬화 리스트로 대체(순수 리팩토링). B는 NpcController에 `Holding` 상태·`SendToHolding` 진입을 부분클래스로 추가하고 SpawnedNpcEvent가 판정 후 임시 거처로 이송한다. C는 ThugAttacker를 돌진 사이클(접근→윈드업→돌진→경직) 상태 머신으로 재작성하고 튜닝값을 config SO로 분리한다. 세 파트 모두 서버 권위·오프라인 폴백을 유지한다.

**Tech Stack:** Unity 6000.3.15f1, C#, Netcode for GameObjects, NavMesh, ScriptableObject.

## Global Constraints

- **서버 권위 + 오프라인 폴백:** FSM/이동/판정/타격은 서버(또는 오프라인) 전용. 모든 상태 전이 public 메서드는 `if (IsSpawned && !IsServer) return;` 가드로 시작. 클라이언트는 NetworkTransform/NetworkVariable 동기화 결과만 표현.
- **네이밍(GDD 10-5):** private 인스턴스 `m_`, static `s_`, const `k_`, 인터페이스 `I`, 이벤트 `On` 접두사, public 프로퍼티/메서드 PascalCase.
- **튜닝 데이터는 ScriptableObject(GDD 10-4):** 차저 튜닝값은 `ThugChargerConfig` SO로. 코드 기본값 ≠ 에셋 값 — 에셋을 직접 수정.
- **NpcState enum 값 = Animator 상태 번호:** 새 값은 **반드시 enum 끝에만** 추가(기존 값 순서 변경 금지).
- **의존성:** B의 `NpcController.Holding.cs` 부분클래스는 #294(NpcController partial 분할)가 머지된 뒤 착수한다. A·C는 #294와 무관하게 착수 가능.
- **밸런싱 수치는 별개:** 데미지·속도·간격 등은 에셋에서 조정하며 이 계획의 검증 대상이 아니다.

## 테스트 방식 (Unity 현실)

이 저장소엔 아직 Unity Test Framework 테스트 코드가 없고, 대상 로직 대부분이 네트워크 FSM/이동이라 순수 단위테스트가 어렵다. 각 태스크의 검증은 다음으로 한다:

1. **컴파일 확인** — 스크립트 저장 후 Unity 콘솔 에러 0 (MCP: `read_console` type=Error, 또는 Editor 콘솔 육안).
2. **Play 모드 행동 검증** — Test Scene Play 후 MCP `execute_code`로 대상을 강제 구동해 상태·위치·콘솔을 관찰(기존 NPC 13상태 검증과 동일 방식). 각 태스크에 구체 스니펫 제공.
3. **커밋** — 검증 통과 후.

MCP `execute_code`는 CodeDom(C# 6): `using`문 금지(본문만), 보간 문자열 안에 중첩 문자열 리터럴 금지(변수로 분리).

---

## 파일 구조

| 파트 | 파일 | 책임 |
|---|---|---|
| A | `Assets/Scripts/Events/SuddenEventManager.cs` (수정) | 인스펙터 리스트 기반 풀 + 항목 토글 + ForceTrigger |
| B | `Assets/Scripts/NPC/NpcState.cs` (수정) | `Holding` enum 값 추가 |
| B | `Assets/Scripts/NPC/NpcHoldingState.cs` (신규) | 임시 거처 보행→도착 통보 상태 |
| B | `Assets/Scripts/NPC/NpcController.Holding.cs` (신규) | `SendToHolding`·`HoldingSpot`·`OnReachedHolding` |
| B | `Assets/Scripts/NPC/NpcController.cs` (수정) | Awake에 Holding 상태 등록 |
| B | `Assets/Scripts/NPC/NpcAnimationDriver.cs` (수정) | Holding→Walk 모션 매핑 |
| B | `Assets/Scripts/Events/SpawnedNpcEvent.cs` (수정) | 임시 거처 이송 + 도착 시 정리 예약 + 폴백 |
| C | `Assets/Scripts/Events/Config/ThugChargerConfig.cs` (신규) | 차저 튜닝 SO |
| C | `Assets/Scripts/Events/ThugAttacker.cs` (재작성) | 돌진 사이클 상태 머신 |

각 파트는 커밋 단위로 분리한다(리뷰·롤백 대비). 순서: A → B → C.

---

## Part A — SuddenEventManager 명시 리스트 리팩토링

### Task A1: 인스펙터 명시 리스트 + 항목 토글

**Files:**
- Modify: `Assets/Scripts/Events/SuddenEventManager.cs`

**Interfaces:**
- Consumes: `ISuddenEvent`, `ISuddenEventProvider.CollectEvents(List<ISuddenEvent>)` (기존).
- Produces: 인스펙터 필드 `m_eventEntries`(List<SuddenEventEntry>); `m_events` 런타임 풀은 이 리스트에서 구성.

- [ ] **Step 1: 직렬화 항목 타입과 리스트 필드 추가**

`SuddenEventManager` 클래스 안, `m_events` 선언 위에 중첩 타입과 필드를 추가한다:

```csharp
    [System.Serializable]
    private class SuddenEventEntry
    {
        [Tooltip("ISuddenEvent 또는 ISuddenEventProvider를 구현한 컴포넌트 (예: ThugAssaultEvent, DeviceBlackoutEvent, SpawnedNpcEventSet, JailbreakEvent)")]
        public MonoBehaviour component;

        [Tooltip("끄면 이 항목은 이벤트 풀에서 제외된다 — 특정 이벤트만 켜서 테스트할 때 쓴다")]
        public bool enabled = true;
    }

    [Header("이벤트 풀 (명시 리스트)")]
    [Tooltip("발생 후보 이벤트를 여기 등록한다. 자동수집은 쓰지 않는다 — 항목의 enabled로 개별 토글 (#291)")]
    [SerializeField]
    private System.Collections.Generic.List<SuddenEventEntry> m_eventEntries = new System.Collections.Generic.List<SuddenEventEntry>();
```

- [ ] **Step 2: Awake의 자동수집을 리스트 기반 구성으로 교체**

`Awake()`의 `GetComponents(...)` 두 블록(78~85행)을 아래로 교체한다:

```csharp
        // 명시 리스트에서 이벤트 풀을 구성한다 — 자동수집(GetComponents) 대신 인스펙터 등록분만 (#291).
        // enabled=false 항목은 풀에서 제외 — 특정 이벤트만 켜서 반복 테스트한다.
        m_events.Clear();
        for (int i = 0; i < m_eventEntries.Count; i++)
        {
            SuddenEventEntry entry = m_eventEntries[i];
            if (!entry.enabled || entry.component == null)
                continue;

            if (entry.component is ISuddenEventProvider provider)
                provider.CollectEvents(m_events); // 제공자형: 1개가 여러 종 (SpawnedNpcEventSet)
            else if (entry.component is ISuddenEvent evt)
                m_events.Add(evt); // 컴포넌트형: 1개 = 1종
            else
                Debug.LogWarning($"SuddenEventManager: '{entry.component.name}'은(는) ISuddenEvent/ISuddenEventProvider가 아니다 — 무시", entry.component);
        }
```

- [ ] **Step 3: 클래스 XML 주석의 "같은 오브젝트 자동 수집" 설명을 갱신**

클래스 상단 요약(20~26행)의 "같은 GameObject에서 이벤트 풀을 자동 수집한다 …" 문단을 다음으로 교체:

```csharp
/// 이벤트 풀은 인스펙터 <b>명시 리스트</b>(m_eventEntries)로 구성한다 — 자동수집을 쓰지 않는다 (#291).
///  · <see cref="ISuddenEvent"/> 컴포넌트 — 1개 = 1종 (괴한 습격·전자기기 먹통).
///  · <see cref="ISuddenEventProvider"/> 컴포넌트 — 1개가 여러 종을 품는다 (스폰형: SpawnedNpcEventSet).
/// 항목마다 enabled 토글이 있어 특정 이벤트만 켜서 추첨할 수 있다(테스트·튜토리얼). 리스트는 같은
/// 오브젝트가 아닌 이벤트 컴포넌트도 참조할 수 있다(씬 배선 최종형은 팀 결정).
```

- [ ] **Step 4: 컴파일 확인**

MCP: `read_console` (type=Error) → 0건. `execute_code`로 배선 확인(Play 후):

```csharp
var m = UnityEngine.Object.FindFirstObjectByType<SuddenEventManager>();
var f = typeof(SuddenEventManager).GetField("m_events", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
var list = f.GetValue(m) as System.Collections.IList;
return "events in pool = " + (list == null ? -1 : list.Count);
```
기대: 인스펙터 리스트에 등록·enabled된 항목 수만큼(SpawnedNpcEventSet은 내부 항목 수만큼 펼쳐짐).

> ⚠️ **씬/프리팹 작업(Editor):** 이 변경 후 SuddenEventManager 오브젝트의 인스펙터에서 `m_eventEntries`에 기존 이벤트 컴포넌트들(ThugAssaultEvent·DeviceBlackoutEvent·SpawnedNpcEventSet·JailbreakEvent 등)을 등록해야 한다. 등록 전에는 풀이 비어 아무 이벤트도 안 난다.

- [ ] **Step 5: 커밋**

```bash
git add Assets/Scripts/Events/SuddenEventManager.cs
git commit -m "#291: SuddenEventManager 이벤트 풀을 명시 리스트+항목 토글로 전환 (자동수집 제거)"
```

### Task A2: 강제 발동 디버그 API

**Files:**
- Modify: `Assets/Scripts/Events/SuddenEventManager.cs`

**Interfaces:**
- Produces: `public void ForceTrigger(int index)` — 풀의 index번 이벤트를 즉시 발동(서버/오프라인).

- [ ] **Step 1: ForceTrigger 메서드 추가**

`TryTriggerRandom()` 아래에 추가:

```csharp
    /// <summary>
    /// 디버그 — 풀의 index번 이벤트를 즉시 발동한다(밸런싱·테스트용). 서버(또는 오프라인)에서만 동작하며
    /// 이미 활성이거나 발생 불가(CanTrigger=false)면 무시한다. (#291)
    /// </summary>
    public void ForceTrigger(int index)
    {
        if (!IsAuthority)
            return;
        if (index < 0 || index >= m_events.Count)
        {
            Debug.LogWarning($"SuddenEventManager.ForceTrigger: 잘못된 index {index} (풀 크기 {m_events.Count})", this);
            return;
        }

        ISuddenEvent evt = m_events[index];
        if (evt.IsActive || !evt.CanTrigger())
        {
            Debug.Log($"[돌발이벤트] 강제발동 불가 — {evt.DisplayName} (활성이거나 조건 미충족)");
            return;
        }

        evt.ServerBegin();
        if (evt.IsActive && evt.AnnounceOnBegin)
            Announce(evt.DisplayName);
        Debug.Log($"[돌발이벤트] 강제발동 — {evt.DisplayName}");
    }

    [ContextMenu("Debug/Force Trigger First Event")]
    private void ForceTriggerFirst() => ForceTrigger(0);
```

- [ ] **Step 2: 컴파일 확인 + 강제발동 검증**

MCP `read_console` 0 에러. Play 후 `execute_code`:

```csharp
var m = UnityEngine.Object.FindFirstObjectByType<SuddenEventManager>();
m.ForceTrigger(0);
return "forced trigger index 0 — 콘솔에서 발생 로그 확인";
```
기대: 콘솔에 `[돌발이벤트] 강제발동 — <이름>` 또는 불가 사유 로그. 에러 없음.

- [ ] **Step 3: 커밋**

```bash
git add Assets/Scripts/Events/SuddenEventManager.cs
git commit -m "#291: 돌발 이벤트 강제발동 디버그 API(ForceTrigger) 추가"
```

---

## Part B — 경범죄 이벤트 NPC 임시 거처 이송

> **선행:** #294(NpcController partial 분할) 머지 후 착수.

### Task B1: NpcState.Holding 추가

**Files:**
- Modify: `Assets/Scripts/NPC/NpcState.cs`

**Interfaces:**
- Produces: `NpcState.Holding` (enum 끝값).

- [ ] **Step 1: enum 끝에 Holding 추가**

`PenaltyEscorting,` 다음(닫는 `}` 직전)에 추가:

```csharp

    /// <summary>
    /// 임시 거처 이송 — 경범죄 이벤트 NPC(난동자·난동꾼)가 판정 후 임시 거처 지점까지 걸어가 도착 시 소멸한다. (#291)
    /// Jailed와 같은 규약 예외: 대응 Animator 상태가 없어 NpcAnimationDriver가 Walk 모션을 대여한다.
    /// 원한 구역(Detained #277)과는 다른 지점·다른 목적이다(추격 출동 없이 정리 대상).
    /// </summary>
    Holding,
```

- [ ] **Step 2: 컴파일 확인**

MCP `read_console` (type=Error) → 0건. (enum 추가만으로는 동작 변화 없음.)

- [ ] **Step 3: 커밋**

```bash
git add Assets/Scripts/NPC/NpcState.cs
git commit -m "#291: NpcState.Holding 추가 (임시 거처 이송 상태)"
```

### Task B2: NpcHoldingState + NpcController.Holding 진입

**Files:**
- Create: `Assets/Scripts/NPC/NpcHoldingState.cs`
- Create: `Assets/Scripts/NPC/NpcController.Holding.cs`
- Modify: `Assets/Scripts/NPC/NpcController.cs` (Awake 등록)

**Interfaces:**
- Consumes: `NpcController.Agent`, `NpcController.HoldingSpot`, `NpcState.Holding`, `NpcStateBase(NpcController)`.
- Produces:
  - `NpcController.HoldingSpot` (Transform, get)
  - `NpcController.SendToHolding(Transform spot)`
  - `NpcController.NotifyReachedHolding()`
  - `event Action<NpcController> NpcController.OnReachedHolding`

- [ ] **Step 1: NpcController.Holding.cs 작성**

```csharp
using System;
using UnityEngine;

public partial class NpcController
{
    // ---- 임시 거처 이송 (#291) ----

    /// <summary>임시 거처 이송 중 걸어갈 지점. 이송 중이 아니면 null. 서버에서만 유효. (#291)</summary>
    public Transform HoldingSpot { get; private set; }

    /// <summary>임시 거처 도착 — NpcHoldingState 전용. SpawnedNpcEvent가 구독해 NPC를 정리(despawn)한다. 서버에서만 발생. (#291)</summary>
    public event Action<NpcController> OnReachedHolding;

    /// <summary>
    /// 임시 거처로 이송 — 경범죄 이벤트 NPC를 판정 후 spot으로 걸어가게 한다. 도착 시 <see cref="OnReachedHolding"/> 발행.
    /// spot이 null이면 진입 즉시 도착 처리(그 자리 통보). 유치장(#228)·원한구역(#277)과는 별개 경로다. (#291)
    /// </summary>
    public void SendToHolding(Transform spot)
    {
        // FSM 전이는 서버 권위 — StartEscort와 동일하게 클라이언트 호출은 무시한다
        if (IsSpawned && !IsServer)
            return;

        EscortTarget = null; // 판정 시점에 연행은 이미 풀렸지만, 참조가 남아 있으면 여기서 끊는다
        HoldingSpot = spot;
        m_stateMachine.ChangeState(NpcState.Holding);
    }

    /// <summary>임시 거처 도착 통보 — NpcHoldingState 전용.</summary>
    public void NotifyReachedHolding() => OnReachedHolding?.Invoke(this);
}
```

- [ ] **Step 2: NpcHoldingState.cs 작성** (NpcDetainedState 구조를 따르되 도착 시 통보)

```csharp
using UnityEngine;

/// <summary>
/// 임시 거처 이송(Holding) 상태 — 경범죄 이벤트 NPC가 판정 후 임시 거처 지점까지 걸어가 도착하면
/// <see cref="NpcController.OnReachedHolding"/>을 발행한다(그걸 받아 SpawnedNpcEvent가 정리한다). (#291)
/// NpcDetainedState(#277)와 같은 "이송 + 도착" 구조지만, 도착 후 대기가 아니라 소멸 통보를 낸다.
/// 대응 Animator 상태가 없어 NpcAnimationDriver가 Walk 모션을 대여한다.
/// </summary>
public class NpcHoldingState : NpcStateBase
{
    // 도착 판정 거리(m) — NavMesh 끝점 오차 흡수 (NpcDetainedState와 동일 기준)
    private const float k_arriveDistance = 0.9f;

    private bool m_arrived; // 도착 통보 완료 래치 — 중복 통보 방지

    public NpcHoldingState(NpcController owner)
        : base(owner) { }

    public override void Enter()
    {
        m_arrived = false;
        m_owner.Agent.isStopped = false;
        m_owner.Agent.stoppingDistance = 0f;

        // 임시 거처가 배선되지 않은 씬 — 그 자리에서 도착(소멸) 처리 (SendToJail의 null cell 관례)
        if (m_owner.HoldingSpot == null)
        {
            Arrive();
            return;
        }

        // 경로 실패(지점이 NavMesh 밖 등) — 영원히 걷는 자세로 남지 않게 그 자리에서 도착 처리
        if (!m_owner.Agent.SetDestination(m_owner.HoldingSpot.position))
        {
            Debug.LogWarning($"NpcHoldingState: 임시 거처 경로 실패 — 그 자리에서 소멸 통보: {m_owner.name}", m_owner);
            Arrive();
        }
    }

    public override void Tick()
    {
        if (m_arrived)
            return;
        if (m_owner.Agent.pathPending)
            return;
        if (m_owner.Agent.remainingDistance > k_arriveDistance)
            return;

        Arrive();
    }

    public override void Exit()
    {
        if (m_owner.Agent.isOnNavMesh)
        {
            m_owner.Agent.isStopped = false;
            m_owner.Agent.ResetPath();
        }
    }

    // 도착 — 정지 후 소멸 통보. 통보를 받은 SpawnedNpcEvent는 다음 틱에 despawn한다(파괴-중-틱 회피).
    private void Arrive()
    {
        m_arrived = true;

        if (m_owner.Agent.isOnNavMesh)
        {
            m_owner.Agent.isStopped = true;
            m_owner.Agent.velocity = Vector3.zero;
            m_owner.Agent.ResetPath();
        }

        m_owner.NotifyReachedHolding();
    }
}
```

- [ ] **Step 3: NpcController.cs Awake에 상태 등록**

`m_stateMachine.AddState(NpcState.PenaltyEscorting, new NpcPenaltyEscortState(this, m_escortConfig));` 다음 줄에 추가:

```csharp
        m_stateMachine.AddState(NpcState.Holding, new NpcHoldingState(this));
```

- [ ] **Step 4: 컴파일 확인 + Play 모드 검증**

MCP `read_console` 0 에러. Play 후(호스트) `execute_code` — NPC 하나를 잡아 임시 거처 대신 더미 지점으로 이송:

```csharp
var npcs = UnityEngine.Object.FindObjectsByType<NpcController>(UnityEngine.FindObjectsSortMode.None);
var n = npcs[0];
bool reached = false;
n.OnReachedHolding += (c) => UnityEngine.Debug.Log("[TEST] OnReachedHolding fired");
var spot = new UnityEngine.GameObject("TEST_HOLDING");
spot.transform.position = n.transform.position + new UnityEngine.Vector3(6f, 0, 0);
n.StateMachine.ChangeState(NpcState.Idle);
n.SendToHolding(spot.transform);
return "state=" + n.CurrentState + " (Holding 기대), dest 세팅됨";
```
이어서 잠시 후 다시 읽어 도착 시 상태 정지 + 콘솔에 `[TEST] OnReachedHolding fired` 확인.

- [ ] **Step 5: 커밋**

```bash
git add Assets/Scripts/NPC/NpcHoldingState.cs Assets/Scripts/NPC/NpcController.Holding.cs Assets/Scripts/NPC/NpcController.cs Assets/Scripts/NPC/NpcHoldingState.cs.meta Assets/Scripts/NPC/NpcController.Holding.cs.meta
git commit -m "#291: NpcController Holding 상태 추가 (임시 거처 보행→도착 통보)"
```

### Task B3: NpcAnimationDriver — Holding→Walk 매핑

**Files:**
- Modify: `Assets/Scripts/NPC/NpcAnimationDriver.cs`

**Interfaces:**
- Consumes: `NpcState.Holding`.

- [ ] **Step 1: AnimatorBaseState에 Holding 케이스 추가**

`AnimatorBaseState`의 switch에서 `NpcState.PenaltyEscorting => (int)NpcState.Walk,` 다음에 추가:

```csharp
            // 임시 거처 이송도 대응 Animator 상태가 없다 — 걷기 모션을 빌려 쓴다 (#291)
            NpcState.Holding => (int)NpcState.Walk,
```

- [ ] **Step 2: 컴파일 확인**

MCP `read_console` 0 에러. (Holding 진입 시 Walk 모션이 재생되는지는 Editor Play에서 육안 — 애니메이터 배선은 Editor 몫.)

- [ ] **Step 3: 커밋**

```bash
git add Assets/Scripts/NPC/NpcAnimationDriver.cs
git commit -m "#291: Holding 상태 Walk 모션 매핑"
```

### Task B4: SpawnedNpcEvent — 임시 거처 이송 + 도착 시 정리

**Files:**
- Modify: `Assets/Scripts/Events/SpawnedNpcEvent.cs`

**Interfaces:**
- Consumes: `NpcController.SendToHolding`, `NpcController.OnReachedHolding`.
- Produces: 인스펙터 필드 `m_holdingPoint`(Transform, 씬 배치).

- [ ] **Step 1: 임시 거처 지점 필드 추가**

`[Header("안전 장치")]` 블록 위에 추가:

```csharp
    [Header("임시 거처 (#291)")]
    [Tooltip("경범죄 판정 후 이 지점으로 걸어가 도착하면 소멸한다. 비우면 기존처럼 그 자리에서 소멸한다(폴백)")]
    [SerializeField] private Transform m_holdingPoint;
```

- [ ] **Step 2: HandleArrestJudged를 임시 거처 이송으로 교체**

기존 `HandleArrestJudged`(226~233행)를 아래로 교체:

```csharp
    // 검거 판정 수신 — 내가 스폰한 NPC가 판정됐으면 임시 거처로 이송한다(도착 시 정리). 수익은 ArrestJudge가 이미 지급.
    private void HandleArrestJudged(ArrestResult result)
    {
        if (m_npc == null || result.Npc != m_npc)
            return;

        // 임시 거처가 배선돼 있으면 그쪽으로 걸어가 도착 시 정리한다 — 눈앞에서 사라지지 않게 (#291)
        if (m_holdingPoint != null)
        {
            Debug.Log($"[돌발이벤트] {m_displayName} — 경범죄 판정, 임시 거처로 이송");
            m_startTime = Time.time; // 이송에 방치 타이머 예산을 새로 준다
            m_npc.OnReachedHolding += HandleReachedHolding;
            m_npc.SendToHolding(m_holdingPoint);
            return;
        }

        // 임시 거처 미배선 — 기존처럼 그 자리에서 정리 예약 (폴백)
        Debug.Log($"[돌발이벤트] {m_displayName} — 경범죄 판정 완료, 정리 예약");
        m_despawnQueued = true;
    }

    // 임시 거처 도착 — 다음 ServerTick에 정리한다(파괴-중-틱 회피, 기존 지연 despawn 경로 재사용). (#291)
    private void HandleReachedHolding(NpcController npc)
    {
        if (m_npc == null || npc != m_npc)
            return;

        Debug.Log($"[돌발이벤트] {m_displayName} — 임시 거처 도착, 정리 예약");
        m_despawnQueued = true;
    }
```

- [ ] **Step 3: Despawn에서 OnReachedHolding 구독 해제**

`Despawn()`의 `m_npc.OnStateChanged -= HandleStateChanged;` 다음 줄에 추가:

```csharp
        m_npc.OnReachedHolding -= HandleReachedHolding;
```

- [ ] **Step 4: 컴파일 확인 + Play 모드 검증**

MCP `read_console` 0 에러. 검증(호스트 Play): SuddenEventSet의 한 항목에 `m_holdingPoint`를 씬 더미 Transform으로 배선 → 강제발동(A2 `ForceTrigger`)으로 난동자 스폰 → 제압→연행→본부 인계 판정 → NPC가 임시 거처로 보행 후 소멸하는지 콘솔 로그(`임시 거처로 이송` → `임시 거처 도착, 정리 예약`)로 확인. `m_holdingPoint` 미배선 항목은 그 자리 소멸(폴백) 확인.

- [ ] **Step 5: 커밋**

```bash
git add Assets/Scripts/Events/SpawnedNpcEvent.cs
git commit -m "#291: 경범죄 이벤트 NPC를 임시 거처로 이송 후 소멸 (미배선 시 그 자리 폴백)"
```

---

## Part C — 괴한 → 차저

### Task C1: ThugChargerConfig SO

**Files:**
- Create: `Assets/Scripts/Events/Config/ThugChargerConfig.cs`

**Interfaces:**
- Produces: `ThugChargerConfig` (ScriptableObject) — 아래 프로퍼티 전부.

- [ ] **Step 1: config SO 작성**

```csharp
using UnityEngine;

/// <summary>
/// 차저(구 괴한) 튜닝 값. (#291 — ThugAttacker에서 분리, #259 config SO 패턴)
/// 돌진 사이클(접근·윈드업·돌진·경직)과 명중 효과(데미지·넉백)를 담는다.
/// </summary>
[CreateAssetMenu(fileName = "ThugChargerConfig", menuName = "Undercover/Events/Thug Charger Config")]
public class ThugChargerConfig : ScriptableObject
{
    [Header("표적 탐색")]
    [Tooltip("이 반경(m) 안 가장 가까운 행동 가능 현장 플레이어를 표적으로 (다운 제외)")]
    [SerializeField] private float m_targetSearchRadius = 40f;
    [Tooltip("표적을 다시 고르는 주기(초)")]
    [SerializeField] private float m_retargetInterval = 0.25f;

    [Header("접근")]
    [Tooltip("표적과 이 거리(m) 이내로 붙으면 돌진 준비(윈드업)를 시작한다")]
    [SerializeField] private float m_chargeStartRange = 8f;
    [Tooltip("접근 이동 속도(m/s)")]
    [SerializeField] private float m_approachSpeed = 4f;

    [Header("윈드업 (회피 창)")]
    [Tooltip("돌진 직전 제자리에서 표적 방향을 겨누는 준비 시간(초) — 이 동안 피하면 빗나간다 (#220 철학)")]
    [SerializeField] private float m_windupSeconds = 0.6f;

    [Header("돌진")]
    [Tooltip("돌진 이동 속도(m/s)")]
    [SerializeField] private float m_chargeSpeed = 12f;
    [Tooltip("한 번의 돌진 최대 이동 거리(m)")]
    [SerializeField] private float m_chargeMaxDistance = 12f;
    [Tooltip("돌진 안전장치 — 이 시간(초)이 지나면 돌진을 끝낸다")]
    [SerializeField] private float m_chargeMaxSeconds = 1.2f;
    [Tooltip("돌진 경로가 플레이어에 이 거리(m) 이내로 스치면 명중")]
    [SerializeField] private float m_hitRadius = 1.5f;
    [Tooltip("돌진 중 벽으로 칠 콜라이더 — 차저 자신 레이어는 런타임에 자동 제외")]
    [SerializeField] private LayerMask m_obstacleMask = ~0;

    [Header("명중 효과")]
    [Tooltip("명중 1회당 플레이어 HP 감소량")]
    [SerializeField] private int m_hitDamage = 20;
    [Tooltip("명중 시 플레이어에 가하는 수평 넉백 초기 속도(m/s)")]
    [SerializeField] private float m_knockbackSpeed = 10f;

    [Header("경직 / 쿨다운")]
    [Tooltip("빗맞거나 벽에 박은 뒤 제자리에서 움직이지 못하는 시간(초) — 반격 창")]
    [SerializeField] private float m_recoverSeconds = 1.5f;
    [Tooltip("명중 성공 후 다음 사이클까지 쉬는 시간(초)")]
    [SerializeField] private float m_hitCooldownSeconds = 0.8f;

    [Header("소란 (#81)")]
    [SerializeField] private float m_disturbanceRadius = 8f;
    [SerializeField] private float m_disturbancePulseInterval = 1f;

    public float TargetSearchRadius => m_targetSearchRadius;
    public float RetargetInterval => m_retargetInterval;
    public float ChargeStartRange => m_chargeStartRange;
    public float ApproachSpeed => m_approachSpeed;
    public float WindupSeconds => m_windupSeconds;
    public float ChargeSpeed => m_chargeSpeed;
    public float ChargeMaxDistance => m_chargeMaxDistance;
    public float ChargeMaxSeconds => m_chargeMaxSeconds;
    public float HitRadius => m_hitRadius;
    public LayerMask ObstacleMask => m_obstacleMask;
    public int HitDamage => m_hitDamage;
    public float KnockbackSpeed => m_knockbackSpeed;
    public float RecoverSeconds => m_recoverSeconds;
    public float HitCooldownSeconds => m_hitCooldownSeconds;
    public float DisturbanceRadius => m_disturbanceRadius;
    public float DisturbancePulseInterval => m_disturbancePulseInterval;
}
```

- [ ] **Step 2: 컴파일 확인**

MCP `read_console` 0 에러. 에디터에서 Create → Undercover/Events/Thug Charger Config로 `.asset` 1개 생성(값은 기본값). Editor 작업.

- [ ] **Step 3: 커밋**

```bash
git add Assets/Scripts/Events/Config/ThugChargerConfig.cs Assets/Scripts/Events/Config/ThugChargerConfig.cs.meta Assets/Scripts/Events/Config.meta
git commit -m "#291: ThugChargerConfig SO 추가 (차저 튜닝 데이터 분리)"
```

### Task C2: ThugAttacker 돌진 사이클 재작성

**Files:**
- Modify(전면 재작성): `Assets/Scripts/Events/ThugAttacker.cs`

**Interfaces:**
- Consumes: `ThugChargerConfig`, `SuddenEventUtil.FindNearestFieldPlayer`, `PlayerData.IsTargetable`, `IDamageable.TakeDamage`, `PlayerMovement.AddKnockback`, `NpcController.BroadcastDisturbance`.
- Produces: `event Action OnAttack` (기존 유지 — ThugAnimationDriver가 구독), 돌진 명중 시 발행.

- [ ] **Step 1: ThugAttacker 전체를 아래로 교체**

기존 추격+주기타격 로직을 돌진 사이클 상태 머신으로 재작성한다. `OnAttack` 이벤트는 유지(명중 순간 발행 → 애니메이션이 타격 모션 재생).

```csharp
using System;
using UnityEngine;
using UnityEngine.AI;
using Unity.Netcode;

/// <summary>
/// 차저 — 괴한 습격에서 스폰되는 돌진형 위협 개체. (#291, 구 ThugAttacker 재작성)
/// 제압 대상이 아니며(NpcSubdueInteractable 없음), 표적에게 접근 → 윈드업(회피 창) → 직선 돌진 →
/// 명중 시 데미지+넉백 / 빗맞음·벽 충돌 시 경직(반격 창) 사이클을 돈다. 포획은 없다.
/// 지속 시간·디스폰은 <see cref="ThugAssaultEvent"/>가 담당한다.
///
/// 서버 권위 — 사이클·타격은 서버(또는 오프라인) 전용, 클라이언트는 NetworkTransform 위치만 표현한다. (#56)
/// </summary>
[RequireComponent(typeof(NavMeshAgent))]
public class ThugAttacker : NetworkBehaviour
{
    private enum Phase { Approach, Windup, Charge, Recover }

    [Header("튜닝 (#291)")]
    [SerializeField] private ThugChargerConfig m_config;

    private NavMeshAgent m_agent;
    private PlayerData m_target;
    private Phase m_phase;
    private float m_phaseEndTime;      // Windup/Recover 종료 시각
    private float m_nextRetargetTime;
    private float m_nextPulseTime;

    // 돌진 상태 — 서버(또는 오프라인)에서만 의미
    private Vector3 m_chargeDir;
    private Vector3 m_chargeStart;
    private float m_chargeElapsed;

    /// <summary>타격(돌진 명중) 1회를 휘두를 때 발행 — 전 피어에서 발생(서버 로컬 + ClientRpc 중계).
    /// ThugAnimationDriver가 구독해 타격 모션을 재생한다. (#56 서버 권위 패턴)</summary>
    public event Action OnAttack;

    private bool IsAuthority => !IsSpawned || IsServer;

    private void Awake()
    {
        m_agent = GetComponent<NavMeshAgent>();
        m_phase = Phase.Approach;
    }

    public override void OnNetworkSpawn()
    {
        // 클라이언트의 위치는 NetworkTransform이 담당 — NavMeshAgent를 켜두면 동기화 위치와 싸운다
        if (IsSpawned && !IsServer)
            m_agent.enabled = false;
    }

    private void Update()
    {
        if (!IsAuthority)
            return;
        if (m_config == null)
            return;

        switch (m_phase)
        {
            case Phase.Approach: TickApproach(); break;
            case Phase.Windup:   TickWindup();   break;
            case Phase.Charge:   TickCharge();   break;
            case Phase.Recover:  TickRecover();  break;
        }

        EmitDisturbancePulse();
    }

    // 접근 — 표적에 붙는다. 돌진 사거리 안에 들면 윈드업으로.
    private void TickApproach()
    {
        PlayerData target = AcquireTarget();
        if (target == null)
        {
            if (m_agent.enabled && m_agent.isOnNavMesh)
                m_agent.isStopped = true;
            return;
        }

        if (m_agent.enabled && m_agent.isOnNavMesh)
        {
            m_agent.isStopped = false;
            m_agent.speed = m_config.ApproachSpeed;
            m_agent.SetDestination(target.transform.position);
        }

        float sqr = (target.transform.position - transform.position).sqrMagnitude;
        if (sqr <= m_config.ChargeStartRange * m_config.ChargeStartRange)
            EnterWindup(target);
    }

    private void EnterWindup(PlayerData target)
    {
        m_target = target;
        m_phase = Phase.Windup;
        m_phaseEndTime = Time.time + m_config.WindupSeconds;
        if (m_agent.enabled && m_agent.isOnNavMesh)
        {
            m_agent.isStopped = true;
            m_agent.ResetPath();
        }
    }

    // 윈드업 — 제자리에서 표적을 바라본다(회피 창). 끝나면 방향을 고정해 돌진 개시.
    private void TickWindup()
    {
        if (m_target != null && m_target.IsTargetable)
            FaceTowards(m_target.transform.position);

        if (Time.time < m_phaseEndTime)
            return;

        // 돌진 방향 고정 — 이 시점의 표적 방향으로 커밋(이후 표적이 움직여도 방향은 안 바뀐다 = 회피 가능)
        Vector3 aim = (m_target != null ? m_target.transform.position : transform.position + transform.forward) - transform.position;
        aim.y = 0f;
        m_chargeDir = aim.sqrMagnitude > 0.0001f ? aim.normalized : transform.forward;
        m_chargeStart = transform.position;
        m_chargeElapsed = 0f;
        m_phase = Phase.Charge;

        // 돌진 중에는 에이전트를 끄고 transform으로 직접 민다(넉백 비행과 동일 사고) — 켜두면 NavMesh가 직선을 꺾는다
        if (m_agent.enabled)
        {
            if (m_agent.isOnNavMesh)
                m_agent.ResetPath();
            m_agent.enabled = false;
        }
    }

    // 돌진 — 고정 방향으로 직선 이동. 플레이어에 스치면 명중, 벽/최대거리/시간이면 경직.
    private void TickCharge()
    {
        m_chargeElapsed += Time.deltaTime;
        float step = m_config.ChargeSpeed * Time.deltaTime;

        // 벽 스윕 — 넉백(#232)과 동일하게 몸통 굵기로 훑어 벽을 지나치지 않는다
        if (SweepHitsObstacle(m_chargeDir, step))
        {
            EnterRecover(m_config.RecoverSeconds);
            return;
        }

        transform.position += m_chargeDir * step;
        FaceTowards(transform.position + m_chargeDir);

        // 명중 판정 — 사거리(HitRadius) 안 행동 가능 플레이어
        PlayerData hit = SuddenEventUtil.FindNearestFieldPlayer(transform.position, m_config.HitRadius);
        if (hit != null)
        {
            HitPlayer(hit);
            EnterRecover(m_config.HitCooldownSeconds);
            return;
        }

        // 최대 거리/시간 소진 → 경직
        float traveled = (transform.position - m_chargeStart).magnitude;
        if (traveled >= m_config.ChargeMaxDistance || m_chargeElapsed >= m_config.ChargeMaxSeconds)
            EnterRecover(m_config.RecoverSeconds);
    }

    private void EnterRecover(float seconds)
    {
        m_phase = Phase.Recover;
        m_phaseEndTime = Time.time + seconds;

        // 에이전트를 되살려 NavMesh 위로 복귀(Warp) — 안 하면 이후 이동이 깨진다 (넉백 착지와 동일)
        if (!m_agent.enabled)
            m_agent.enabled = true;
        if (NavMesh.SamplePosition(transform.position, out NavMeshHit navHit, 2f, NavMesh.AllAreas))
            m_agent.Warp(navHit.position);
        if (m_agent.isOnNavMesh)
        {
            m_agent.isStopped = true;
            m_agent.ResetPath();
        }
    }

    // 경직 — 반격 창. 끝나면 접근으로 복귀.
    private void TickRecover()
    {
        if (Time.time >= m_phaseEndTime)
            m_phase = Phase.Approach;
    }

    // 표적 유지 — 유효 표적은 계속, 다운·소멸 표적은 버린다. 재선정은 주기적으로.
    private PlayerData AcquireTarget()
    {
        if (m_target != null && !m_target.IsTargetable)
            m_target = null;

        if (Time.time >= m_nextRetargetTime)
        {
            m_nextRetargetTime = Time.time + m_config.RetargetInterval;
            m_target = SuddenEventUtil.FindNearestFieldPlayer(transform.position, m_config.TargetSearchRadius);
        }
        return m_target;
    }

    // 명중 — 데미지(서버 권위 HP)는 직접, 넉백은 오너 피어가 적용하도록 ClientRpc로(비오너 self-무시). (BombExplosionView 패턴)
    private void HitPlayer(PlayerData player)
    {
        NotifyAttack();

        ((IDamageable)player).TakeDamage(m_config.HitDamage, gameObject);

        Vector3 kb = player.transform.position - transform.position;
        kb.y = 0f;
        kb = (kb.sqrMagnitude > 0.0001f ? kb.normalized : m_chargeDir) * m_config.KnockbackSpeed;

        if (IsSpawned && IsServer)
        {
            NetworkObject netObj = player.GetComponent<NetworkObject>();
            if (netObj != null)
                ApplyPlayerKnockbackClientRpc(netObj.NetworkObjectId, kb);
        }
        else if (!IsSpawned && player.TryGetComponent(out PlayerMovement pm))
        {
            pm.AddKnockback(kb); // 오프라인 — 오너 개념이 없으므로 직접 적용
        }

        Debug.Log($"차저 돌진 명중: {name} → {player.name} (-{m_config.HitDamage})");
    }

    [ClientRpc]
    private void ApplyPlayerKnockbackClientRpc(ulong targetPlayerObjectId, Vector3 velocity)
    {
        // 각 피어가 자기 오너 플레이어에만 적용 — AddKnockback이 비오너를 스스로 무시한다 (BombExplosionView와 동일)
        if (NetworkManager.Singleton == null)
            return;
        if (!NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(targetPlayerObjectId, out NetworkObject obj))
            return;
        if (obj.TryGetComponent(out PlayerMovement pm))
            pm.AddKnockback(velocity);
    }

    private void FaceTowards(Vector3 worldPoint)
    {
        Vector3 dir = worldPoint - transform.position;
        dir.y = 0f;
        if (dir.sqrMagnitude > 0.0001f)
            transform.rotation = Quaternion.LookRotation(dir);
    }

    // 이번 프레임 이동 구간에 벽이 있는지 — 넉백(#232)의 SweepHitsObstacle와 동일 기법
    private bool SweepHitsObstacle(Vector3 direction, float distance)
    {
        float radius = m_agent.radius;
        Vector3 origin = transform.position + Vector3.up * Mathf.Max(radius, m_agent.height * 0.5f);
        int mask = m_config.ObstacleMask & ~(1 << gameObject.layer);
        return Physics.SphereCast(origin, radius, direction, out RaycastHit _, distance, mask, QueryTriggerInteraction.Ignore);
    }

    private void EmitDisturbancePulse()
    {
        if (Time.time < m_nextPulseTime)
            return;
        m_nextPulseTime = Time.time + m_config.DisturbancePulseInterval;
        NpcController.BroadcastDisturbance(transform.position, m_config.DisturbanceRadius);
    }

    private void NotifyAttack()
    {
        OnAttack?.Invoke(); // 서버·오프라인 로컬 발행
        if (IsSpawned && IsServer)
            PlayAttackClientRpc();
    }

    [ClientRpc]
    private void PlayAttackClientRpc()
    {
        if (IsServer)
            return; // 호스트는 위에서 이미 발행
        OnAttack?.Invoke();
    }
}
```

> **참고(구현 시 확인):** `SuddenEventUtil.FindNearestFieldPlayer(Vector3, float)` 시그니처가 기존 코드에서 쓰이던 것과 동일한지 확인(기존 ThugAttacker가 `FindNearestFieldPlayer(transform.position, radius)`로 호출). 반환형 `PlayerData` 가정.

- [ ] **Step 2: 컴파일 확인**

MCP `read_console` (type=Error) → 0건. 특히 `m_config` 미주입 시 Update가 얼리 리턴하므로 널참조 없음 확인.

- [ ] **Step 3: Play 모드 행동 검증**

Editor에서 괴한(차저) 프리팹에 `ThugChargerConfig` 에셋 배선 후, A2 `ForceTrigger`로 괴한 습격 강제발동. 관찰:
- 차저가 플레이어에 접근 후 잠깐 멈췄다(윈드업) 직선 돌진하는가.
- 돌진 중 옆으로 피하면 빗나가고 경직에 들어가는가.
- 명중 시 플레이어 HP 감소 + 넉백(뒤로 밀림) + 콘솔 `차저 돌진 명중` 로그.
- 벽으로 돌진 시 벽 앞에서 멈추고 경직.
- 제압(E) 무반응 유지.

MCP `read_console`로 돌진 중 NavMesh 관련 에러(agent not on NavMesh 등) 0건 확인.

- [ ] **Step 4: 커밋**

```bash
git add Assets/Scripts/Events/ThugAttacker.cs
git commit -m "#291: 괴한을 차저(돌진 사이클)로 재작성 — 윈드업→돌진→명중(데미지+넉백)/경직, config SO 주입"
```

---

## 통합 검증 (전 파트 후)

- [ ] Test Scene Play(호스트) — 컴파일 에러 0.
- [ ] SuddenEventManager 인스펙터 리스트에 이벤트 등록, 항목 토글로 특정 이벤트만 발동됨 확인.
- [ ] 난동자/난동꾼 강제발동 → 제압→연행→판정→임시 거처 이송 후 소멸(미배선 폴백 포함).
- [ ] 괴한 습격 강제발동 → 차저 돌진 사이클·명중·경직·제압불가 확인.
- [ ] MPPM 2클라 — 이송·돌진·넉백이 원격 클라에서 동기화되는지 확인(플레이어 넉백은 오너 피어 적용).
- [ ] PR: 커밋을 A/B/C로 분리한 상태로 `feature/291/sudden-event-improvements` → main (본문 4섹션 컨벤션, 파트 구분 명시).

## 미해결/후속

- SuddenEventManager 항목 `enabled`는 Awake 수집 시점 필터다 — 런타임 라이브 토글은 재수집이 필요(현재 범위 밖, 필요 시 후속).
- 차저 튜닝값(속도·윈드업·경직·넉백)은 플레이 테스트로 조정(에셋에서).
- 애니메이터 컨트롤러에 차저 돌진/경직 모션 배선은 Editor 작업(코드 훅은 기존 속도기반 Run/Idle + OnAttack 재사용).
