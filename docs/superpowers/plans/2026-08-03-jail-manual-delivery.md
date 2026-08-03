# 유치장 직접 이송 구현 계획 (#492)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 유치장 수용을 NavMesh 자동 이송에서 플레이어가 직접 끌고 들어가 앉히는 방식으로 바꾼다.

**Architecture:** 신규 `JailIntake`가 서버에서 0.1초마다 훑으며 규칙 두 개(진입 시 판정 / 놓으면 착석)를 집행하고, `JailZone`은 대장(수용 인원·현상금·좌석) 역할만 유지한다. 인계존 판정 경로는 통째로 제거한다. 수감자 빼내기는 밧줄 없이 `StartEscort`만 걸어 기존 추종 로직을 재사용한다.

**Tech Stack:** Unity 6000.3.15f1 · Netcode for GameObjects · AI Navigation(NavMesh) · UniTask

**승인된 스펙:** [docs/superpowers/specs/2026-08-03-jail-manual-delivery-design.md](../specs/2026-08-03-jail-manual-delivery-design.md)

## Global Constraints

- **브랜치:** `feature/492-jail-manual-delivery` (`feature/462-jail-seat-bench` 위에 스택). PR base는 `feature/462-jail-seat-bench`.
- **네이밍:** `m_` private/protected 인스턴스 · `s_` static · `k_` const · `I` 인터페이스 · camelCase 매개변수/지역변수 · PascalCase public 프로퍼티/메서드/enum 값 · `On` 이벤트 접두사.
- **전역 접근:** `App` 파사드만 사용 (`App.Game.ArrestJudge` 등). 매니저 검색에 `FindFirstObjectByType` 금지, 신규 `static Instance` 싱글톤 금지. **단, `JailZone`·`JailIntake` 같은 장소 오브젝트는 App 등록 대상이 아니며 씬 탐색을 쓴다** (`JailLock`·`HqDropoffZone`과 같은 기존 관례).
- **서버 권위:** FSM 전이·판정·좌석 배정은 서버(또는 오프라인)에서만. `NetworkBehaviour`가 아닌 컴포넌트는 `NetworkManager.Singleton != null && IsListening && !IsServer`로 게이트한다 (`CustodyRouter`와 같은 패턴).
- **서드파티 금지:** `Assets/Imported/Synty/` 아래 파일은 수정하지 않는다.
- **커밋 메시지:** `#492: <한국어 설명>` 형식. 기존 커밋 이력과 동일.
- **매 태스크 종료 조건:** Unity Console에 컴파일 에러 0. 새 타입을 쓰기 전에 반드시 컴파일 확인.

## 검증 방식에 대한 주의 — 이 계획은 단위 테스트를 쓰지 않는다

이 저장소에는 테스트 코드가 하나도 없고 테스트 어셈블리(`.asmdef`)도 없다. 이 작업에서 다루는 것은 NavMesh 영역 샘플링·NetworkVariable 동기화·FSM 전이라 **EditMode 단위 테스트로 의미 있게 덮이지 않는다**(NavMesh는 베이크된 씬이, Netcode는 살아 있는 세션이 필요하다).

승인된 스펙도 검증을 "Editor Play + Multiplayer Play Mode 2인"으로 확정했다. 따라서 각 태스크의 검증 단계는 **컴파일 확인 + Editor에서 눈으로 확인할 구체 절차**로 쓴다. 테스트 인프라 도입은 이 이슈의 범위가 아니다 — 필요하면 별도 이슈로 뺀다.

---

## Task 1: Jail 영역 판정을 공용 헬퍼로 추출

Jail NavMesh 영역 마스크 해석이 지금 **두 곳에 복제**돼 있다(`NpcController.JailAreaMask`, `JailDoor.s_jailAreaMask`). `JailIntake`가 세 번째 사본을 만들지 않도록 먼저 하나로 모은다. 동작 변화는 없다.

**Files:**
- Create: `Assets/Scripts/Interaction/JailArea.cs`
- Modify: `Assets/Scripts/Interaction/JailDoor.cs` (282-315행의 마스크 캐시·`IsInsideJailArea` 제거, 호출부 교체)
- Modify: `Assets/Scripts/NPC/Controller/NpcController.Custody.cs` (8-10행 `s_jailAreaMask`, 65-77행 `JailAreaMask` 위임으로 교체)

**Interfaces:**
- Produces: `public static class JailArea` — `public static int Mask { get; }`, `public static bool Contains(Vector3 position)`

- [ ] **Step 1: `JailArea` 생성**

`Assets/Scripts/Interaction/JailArea.cs`:

```csharp
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// 유치장 내부(Jail) NavMesh 영역 판정 — 마스크 해석과 "이 지점이 유치장 안인가"를 한 곳에 모은다. (#415/#492)
///
/// 이 영역이 유치장 규칙의 단일 기준이다: 시민의 통행 차단(NpcController.SetJailAccess),
/// 문 자동 개폐 판정, 수감 판정·착석(JailIntake)이 전부 같은 폴리곤을 본다.
/// 차단은 문이 아니라 NavMesh 영역이 한다 — 통행이 없으면 문이 열려 있어도 경로가 잡히지 않는다.
///
/// Jail 영역이 없는 프로젝트(단독 테스트 씬 등)에서는 <see cref="Mask"/>가 0이고
/// <see cref="Contains"/>는 항상 false다 — 호출부가 각자 폴백을 정한다.
/// </summary>
public static class JailArea
{
    // 영역 판정 허용치(m). 실측상 Jail 영역은 문짝(z 49.70)보다 0.2m 안쪽(z 49.90)에서 시작한다 —
    // 0.5m로 잡으면 문 밖 접근 지점(LockApproach)까지 '안'으로 걸리고, 0.02m는 유치장 안 좌석도 놓친다.
    private const float k_sampleRadius = 0.2f;

    // 이름으로 한 번만 해석해 캐시한다. -1은 '아직 안 봤다', 0은 '이 프로젝트엔 Jail 영역이 없다'.
    private static int s_mask = -1;

    /// <summary>유치장 내부(Jail) NavMesh 영역 마스크 — 없는 프로젝트면 0.</summary>
    public static int Mask
    {
        get
        {
            if (s_mask < 0)
            {
                int area = NavMesh.GetAreaFromName("Jail");
                s_mask = area >= 0 ? 1 << area : 0;
            }
            return s_mask;
        }
    }

    /// <summary>이 지점이 유치장 내부(Jail 영역) 위인가 — Jail 영역이 없는 프로젝트면 항상 false.</summary>
    public static bool Contains(Vector3 position)
    {
        int mask = Mask;
        if (mask == 0)
            return false;

        return NavMesh.SamplePosition(position, out NavMeshHit _, k_sampleRadius, mask);
    }
}
```

- [ ] **Step 2: `NpcController.JailAreaMask`를 위임으로 교체**

`Assets/Scripts/NPC/Controller/NpcController.Custody.cs`에서 8-10행의 `s_jailAreaMask` 필드를 **삭제**하고, 65-77행의 프로퍼티를 아래로 교체:

```csharp
    /// <summary>유치장 내부(Jail) NavMesh 영역 마스크 — 없는 프로젝트면 0.
    /// 실제 해석은 <see cref="JailArea"/>가 한다 (#492에서 사본 통합).</summary>
    public static int JailAreaMask => JailArea.Mask;
```

- [ ] **Step 3: `JailDoor`의 사본 제거**

`Assets/Scripts/Interaction/JailDoor.cs` 맨 아래 282-315행(`s_jailAreaMask` 필드, `k_insideSampleRadius`, `JailAreaMask` 프로퍼티, `IsInsideJailArea` 메서드)을 **통째로 삭제**하고, 218행의 호출부를 교체:

```csharp
            // 이미 유치장 안으로 들어선 대상은 문을 잡아 두지 않는다 — 들어가면 등 뒤로 닫힌다.
            if (JailArea.Contains(npcs[i].transform.position))
                continue;
```

- [ ] **Step 4: 컴파일 확인**

Unity Editor에서 컴파일이 끝나기를 기다린 뒤 Console 확인.
기대: 에러 0. `JailArea`·`JailDoor`·`NpcController`가 모두 `Assembly-CSharp`에 로드된다.

- [ ] **Step 5: 동작 무변경 확인**

Main Scene Play. 유치장 문 앞에 서서 E로 열고 닫는다.
기대: #462 이전과 동일하게 여닫힌다 (이 태스크는 리팩토링이라 동작이 바뀌면 안 된다).

- [ ] **Step 6: 커밋**

```bash
git add Assets/Scripts/Interaction/JailArea.cs Assets/Scripts/Interaction/JailArea.cs.meta Assets/Scripts/Interaction/JailDoor.cs Assets/Scripts/NPC/Controller/NpcController.Custody.cs
git commit -m "#492: Jail 영역 판정을 JailArea로 통합 — 마스크 사본 2개 제거"
```

---

## Task 2: 흐름 교체 — `JailIntake` 신설, 인계존 판정에서 유치장 판정으로

이 태스크가 끝나면 **새 흐름으로 게임이 동작한다.** 인계 단말 경로는 아직 살아 있지만(제거는 Task 3) 쓰지 않아도 유치장 수용이 성립한다.

세 변경이 서로를 필요로 하므로 한 태스크로 묶는다: `ReserveSeat`를 최근접 방식으로 바꾸면 유일한 호출부인 `CustodyRouter`를 같이 고쳐야 하고, `ArrestJudge`가 밧줄을 강제로 풀면 착석이 자동으로 일어나 "E로 놓아야 앉는다"가 깨진다.

**Files:**
- Create: `Assets/Scripts/Interaction/JailIntake.cs`
- Modify: `Assets/Scripts/Interaction/JailZone.cs` (`ReserveSeat` 교체, 정원 초과 경로 분리)
- Modify: `Assets/Scripts/Interaction/ArrestJudge.cs` (판정 후 밧줄 강제 해제 블록 제거)
- Modify: `Assets/Scripts/Interaction/CustodyRouter.cs` (수감 분기 제거)
- Modify: `Assets/Scenes/Main Scene.unity` (`Jail` 오브젝트에 `JailIntake` 추가)

**Interfaces:**
- Consumes: `JailArea.Contains(Vector3)` (Task 1)
- Produces:
  - `JailZone.ReserveSeat(NpcController npc, Vector3 near) → Transform`
  - `JailIntake.ServerExtract(NpcController npc, Transform follower) → void` (Task 5에서 호출)

- [ ] **Step 1: `JailZone.ReserveSeat`를 최근접 배정으로 교체**

`Assets/Scripts/Interaction/JailZone.cs`의 기존 `ReserveSeat(NpcController npc)` 메서드 전체를 아래로 교체한다. `NextOverflowSeat`·`ReleaseSeat`는 그대로 둔다.

```csharp
    /// <summary>
    /// 좌석 배정 — 수감 대상 1명이 앉을 좌석을 내준다. 서버(또는 오프라인)에서 호출. (#462/#492)
    ///
    /// <paramref name="near"/>에서 <b>가장 가까운 빈 좌석</b>을 고른다. 플레이어가 신병을 내려놓은
    /// 자리에서 가장 가까운 자리에 앉히기 위한 것이다 — 앞에서부터 채우면 방 반대편 좌석이 배정돼
    /// 걸어가는 거리가 공연히 길어진다(실측 최대 5.6m).
    ///
    /// 정원을 넘으면 좌석을 돌려 써 겹쳐 앉힌다 — 좌석은 전부 통로 밖이라 겹쳐도 통행을 막지 않는다
    /// (팀 확정 2026-07-30). 조용히 넘어가지 않게 경고를 남긴다.
    /// </summary>
    public Transform ReserveSeat(NpcController npc, Vector3 near)
    {
        if (npc == null || m_seatPoints == null || m_seatPoints.Length == 0)
            return transform;

        // 이미 자리가 있는 대상이면 그 자리를 그대로 준다 — 재판정·중복 통보로 한 명이 두 자리를 쥐지 않게
        for (int i = 0; i < m_seatPoints.Length; i++)
            if (m_seatOccupants[i] == npc && m_seatPoints[i] != null)
                return m_seatPoints[i];

        // 빈 자리 중 기준 위치에서 가장 가까운 곳. 점유자가 파괴됐으면(라운드 종료 잔류 정리 등)
        // Unity의 null 비교가 빈 자리로 본다.
        int best = -1;
        float bestSqrDistance = float.MaxValue;
        for (int i = 0; i < m_seatPoints.Length; i++)
        {
            if (m_seatPoints[i] == null || m_seatOccupants[i] != null)
                continue;

            float sqrDistance = (m_seatPoints[i].position - near).sqrMagnitude;
            if (sqrDistance >= bestSqrDistance)
                continue;

            bestSqrDistance = sqrDistance;
            best = i;
        }

        if (best < 0)
            return ShareOverflowSeat(npc);

        m_seatOccupants[best] = npc;
        return m_seatPoints[best];
    }

    // 정원 초과 — 좌석을 돌려 써 겹쳐 앉힌다. 경고는 여기 한 곳에서만 낸다.
    private Transform ShareOverflowSeat(NpcController npc)
    {
        Transform shared = NextOverflowSeat();
        Debug.LogWarning(
            $"[유치장] 좌석 정원({m_seatPoints.Length}석) 초과 — {npc.name}을(를) {shared.name}에 겹쳐 앉힌다. "
                + "정원을 늘리려면 유치장에 벤치·좌석 지점을 추가할 것",
            this
        );
        return shared;
    }
```

- [ ] **Step 2: `ArrestJudge`의 밧줄 강제 해제 제거**

`Assets/Scripts/Interaction/ArrestJudge.cs`에서 `LogVerdict(result);` 다음에 오는 블록(주석 132-137행 + `if (deliverers.Count > 0) { ... } else { npc.StopEscort(); }` 138-149행)을 **통째로 삭제**하고, 그 자리에 아래 주석만 남긴다:

```csharp
        // 판정은 신병 상태를 건드리지 않는다 (#492). 예전에는 여기서 밧줄을 강제로 풀었는데,
        // CustodyRouter가 곧바로 Jailed로 전이시키던 것을 대비한 순서 강제였다. 이제 판정 직후
        // 아무도 전이시키지 않으므로 풀 이유가 없고, 오히려 풀면 유치장 문을 통과하는 순간
        // 자동으로 착석해 "E로 놓아야 앉는다"가 깨진다.
        //
        // 오검거는 WrongfulArrestPenalty가 Detained로 전이시키므로, 남은 밧줄은
        // PlayerEscorter.TickTetherCleanup의 커스터디 이탈 정리가 끊는다.
```

- [ ] **Step 3: `CustodyRouter`의 수감 분기 제거**

`Assets/Scripts/Interaction/CustodyRouter.cs`의 `HandleArrestJudged`에서 오검거 분기 이후 전체(75-96행 — `m_jailZone` null 검사, `Admit`, `ReserveCell`, `SendToJail`)를 삭제하고 아래로 교체. `m_jailZone` 필드와 `Awake`의 탐색도 함께 삭제한다 (더 이상 쓰지 않는다).

```csharp
        // 현상수배범·경범죄의 수용은 여기서 하지 않는다 (#492) — 플레이어가 직접 끌고 들어가
        // 좌석에 놓을 때 JailIntake가 배정·계상한다. 판정만 나고 안 앉히면 0원이다.
    }
```

클래스 주석 상단의 행선지 설명도 함께 고친다:

```csharp
/// - 현상수배범·경범죄 → 수용은 JailIntake가 한다 (플레이어가 직접 끌고 들어가 앉힌다, #492)
/// - 오검거(무고한 시민) → 수갑 해제 후 석방(배회 복귀)
```

- [ ] **Step 4: `JailIntake` 생성**

`Assets/Scripts/Interaction/JailIntake.cs`:

```csharp
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 유치장 출입구 — <b>누가 유치장에 들어왔고, 어디 앉히고, 언제 내보내나</b>를 담당한다. (#492)
///
/// <see cref="JailZone"/>과 역할이 갈린다: 저쪽은 대장(수용 인원·현상금·정산 레코드·좌석 소유),
/// 이쪽은 출입구다. 한 클래스에 두면 정산 책임과 출입 책임이 섞이고 크기도 감당이 안 된다.
///
/// 서버(또는 오프라인)에서 <see cref="m_checkInterval"/>마다 훑으며 규칙 두 개를 집행한다:
///
///  <b>R1 판정</b> — 확보된 신병(Escorted/Captured)이 Jail 영역에 들어서면 그 순간 판정한다.
///                   오검거를 좌석까지 끌고 가야 알게 되는 헛수고를 없앤다(팀 확정 2026-08-03).
///  <b>R2 착석</b> — 판정에서 수감 대상으로 확정된 대상이 Jail 영역 안에서 Captured가 되면
///                   (= 플레이어가 E로 놓으면) 가장 가까운 빈 좌석에 앉히고 계상한다.
///
/// <b>R1을 먼저 돌리는 것이 강제다.</b> 반출 직후 플레이어가 유치장 안에서 바로 E를 눌러 되돌리는
/// 경우, 같은 틱에 재판정(R1)과 착석(R2)이 함께 일어나야 한 프레임 늦게 앉는 것을 피할 수 있다.
///
/// 판정과 계상이 분리돼 있다 — 문턱만 넘고 안 앉히면 <b>0원</b>이다. 이것이 "직접 넣게 만든다"의
/// 실질적 강제력이고, GDD 9-2의 "이송 중 라운드 종료 시 보상 없음"과도 정확히 맞는다.
///
/// 장소 오브젝트라 App 파사드에 등록하지 않는다 — JailLock·JailZone과 같은 관례로 씬 탐색을 쓴다.
/// </summary>
public class JailIntake : MonoBehaviour
{
    [Header("유치장 (비우면 같은 오브젝트·부모에서 자동 탐색)")]
    [SerializeField] private JailZone m_jailZone;

    [Header("출입 검사")]
    [Tooltip("검사 주기(초) — 매 프레임 돌 필요가 없다. 0이면 매 프레임 검사한다 (JailDoor와 같은 관례)")]
    [SerializeField] private float m_checkInterval = 0.1f;

    // 다음 검사까지 남은 시간
    private float m_cooldown;

    // 판정에서 수감 대상으로 확정된 대상과 그 보상액 — R2가 여기 있는 대상만 앉힌다.
    // 보상액을 함께 들고 있는 이유: Admit이 착석 시점이라 판정 결과를 그때까지 보관해야 한다.
    // 서버(또는 오프라인) 전용.
    private readonly Dictionary<NpcController, int> m_pendingSeat = new Dictionary<NpcController, int>();

    // 판정 자체가 불가능했던 대상(신원·경범죄 마커 둘 다 없음) — 매 틱 재시도하면 경고가 폭주한다.
    // ArrestJudge가 이 경우 MarkDelivered를 부르지 않아 IsDelivered로는 걸러지지 않는다.
    private readonly HashSet<NpcController> m_unjudgeable = new HashSet<NpcController>();

    private void Awake()
    {
        // 유치장은 같은 오브젝트에 두는 것이 기본 — 인스펙터로 따로 지정할 수도 있다
        if (m_jailZone == null)
            m_jailZone = GetComponentInParent<JailZone>();

        if (m_jailZone == null)
            Debug.LogWarning("JailIntake: 유치장(JailZone)을 찾지 못했다 — 수용이 동작하지 않는다", this);
    }

    // 판정·좌석 배정은 서버 권위 — NetworkBehaviour가 아니므로 직접 게이트한다 (CustodyRouter와 같은 패턴)
    private static bool HasServerAuthority =>
        NetworkManager.Singleton == null
        || !NetworkManager.Singleton.IsListening
        || NetworkManager.Singleton.IsServer;

    private void Update()
    {
        if (!HasServerAuthority)
            return;

        m_cooldown -= Time.deltaTime;
        if (m_cooldown > 0f)
            return;
        m_cooldown = m_checkInterval;

        PruneDestroyed();

        // 검사 주기로 호출을 눌러 두었기에 목록 훑기로 충분하다 (JailDoor와 같은 판단)
        NpcController[] npcs = FindObjectsByType<NpcController>(FindObjectsSortMode.None);

        // 순서 강제 — R1이 먼저다 (클래스 주석 참고)
        for (int i = 0; i < npcs.Length; i++)
            TryJudgeOnEntry(npcs[i]);

        for (int i = 0; i < npcs.Length; i++)
            TrySeat(npcs[i]);
    }

    // 파괴된 대상을 걷어낸다 — 라운드 종료 잔류 정리(MisdemeanorLoiterer)로 NPC가 사라져도
    // 키가 남아 목록이 라운드마다 자란다. 위 두 규칙은 살아 있는 NPC를 훑어 도므로 스스로 지우지 못한다.
    private void PruneDestroyed()
    {
        // 지우면서 도니 키를 먼저 복사한다 — Unity의 가짜 null 비교로 파괴 여부를 본다
        var dead = new List<NpcController>();

        foreach (NpcController npc in m_pendingSeat.Keys)
            if (npc == null)
                dead.Add(npc);

        for (int i = 0; i < dead.Count; i++)
            m_pendingSeat.Remove(dead[i]);

        m_unjudgeable.RemoveWhere(npc => npc == null);
    }

    // R1 — 확보된 신병이 유치장에 들어선 순간 판정한다.
    private void TryJudgeOnEntry(NpcController npc)
    {
        if (npc == null || npc.IsDelivered || m_unjudgeable.Contains(npc))
            return;

        // 확보된 신병만 — 끌려오는 중(Escorted)과 내려놓은 대상(Captured) 둘 다 통과한다.
        // 배회 시민은 애초에 Jail 영역에 못 들어오지만(NavMesh 게이팅) 상태로도 한 번 더 막는다.
        if (npc.CurrentState != NpcState.Escorted && npc.CurrentState != NpcState.Captured)
            return;

        if (!JailArea.Contains(npc.transform.position))
            return;

        ArrestJudge judge = App.Game.ArrestJudge;
        if (judge == null)
        {
            Debug.LogWarning("JailIntake: ArrestJudge가 없어 판정할 수 없다", this);
            return;
        }

        ArrestResult? result = judge.Judge(npc);
        if (result == null)
        {
            // 신원도 경범죄 마커도 없는 대상 — 다시 물어도 답이 같으므로 한 번만 시도한다
            m_unjudgeable.Add(npc);
            return;
        }

        // 오검거는 WrongfulArrestPenalty가 Detained로 가져간다 — 앉힐 대상이 아니다
        if (result.Value.Verdict != ArrestVerdict.WrongfulArrest)
            m_pendingSeat[npc] = result.Value.Reward;
    }

    // R2 — 수감 대상이 유치장 안에서 멈추면(플레이어가 E로 놓으면) 좌석에 앉히고 계상한다.
    private void TrySeat(NpcController npc)
    {
        if (npc == null || m_jailZone == null)
            return;

        if (!m_pendingSeat.TryGetValue(npc, out int bounty))
            return;

        // 끌려가는 중에는 앉히지 않는다 — 놓아야(Captured) 앉는다
        if (npc.CurrentState != NpcState.Captured)
            return;

        if (!JailArea.Contains(npc.transform.position))
            return;

        // 놓은 자리에서 가장 가까운 빈 좌석 — 여기서 좌석까지는 NpcJailedState가 걸어간다(1.5~5.6m)
        Transform seat = m_jailZone.ReserveSeat(npc, npc.transform.position);
        npc.SendToJail(seat);

        // 계상은 착석 시점 (#492) — 판정만 받고 안 앉히면 0원이다
        m_jailZone.Admit(npc, bounty);
        m_pendingSeat.Remove(npc);
    }

    /// <summary>
    /// 반출 — 앉은 수감자를 일으켜 플레이어를 따라오게 한다. 서버(또는 오프라인) 전용. (#492)
    ///
    /// 밧줄을 걸지 않는다: <see cref="NpcEscortedState"/>가 밧줄 이전의 추종·근접 정지(#97)를 그대로
    /// 들고 있고 <c>IsRoped</c>일 때만 건너뛰므로, <c>StartRopeDrag</c> 없이 <c>StartEscort</c>만 부르면
    /// 추종·속도 부스트·거리 이탈이 전부 동작한다.
    ///
    /// 좌석 반납과 정산 제외가 함께 일어난다 — "끝까지 데리고 있어야 인정"(GDD 9-2)이 그대로 유지된다.
    /// </summary>
    public void ServerExtract(NpcController npc, Transform follower)
    {
        if (!HasServerAuthority)
            return;

        if (npc == null || follower == null || m_jailZone == null)
            return;

        if (npc.CurrentState != NpcState.Jailed)
            return;

        m_jailZone.ReleaseInmate(npc); // 좌석 반납 + 정산·진행도에서 제외

        // 수감 대상 기록도 지운다 — 남겨두면 재판정 결과가 오검거로 바뀌어도 R2가 옛 기록을 보고 앉힌다
        m_pendingSeat.Remove(npc);

        // 다시 넣으면 재판정 (#358) — 탈옥 방출(JailbreakEvent)이 같은 호출을 하는 것과 같은 이유다
        npc.ClearDelivered();

        // 앉은 자세를 전이보다 먼저 푼다 — 뒤에 두면 일어서는 순간이 한두 프레임 앉은 채로 보인다 (#462)
        npc.SetSeated(false);
        npc.StartEscort(follower);

        Debug.Log($"[유치장] 반출 — 따라오게 한다: {npc.name}");
    }
}
```

- [ ] **Step 5: 컴파일 확인**

기대: 에러 0. `JailIntake`가 `Assembly-CSharp`에 로드된다.

- [ ] **Step 6: 씬에 `JailIntake` 배치**

Main Scene에서 `=== WORLD ===/Jail` 오브젝트를 선택해 `JailIntake` 컴포넌트를 추가한다.
`m_jailZone`은 비워 둔다 — `GetComponentInParent`가 같은 오브젝트의 `JailZone`을 찾는다.
씬 저장.

- [ ] **Step 7: 새 흐름 동작 확인 (Editor Play)**

1. NPC를 테이저/진압봉으로 무력화한 뒤 밧줄로 묶어 유치장 문 앞까지 끈다.
2. E로 문을 열고 끌고 들어간다.
   기대: 문턱을 넘는 순간 Console에 `[검거 판정]` 로그. **밧줄은 유지된다**(NPC가 계속 끌려온다).
3. 그 상태로 좌석 근처까지 끌고 가 E로 놓는다.
   기대: 가장 가까운 빈 좌석에 앉고 `[유치장] 수용:` 로그와 함께 누적 현상금이 오른다.
4. 다른 NPC를 문턱만 넘긴 뒤 놓지 말고 다시 끌고 나온다.
   기대: 판정은 났지만 누적 현상금이 **오르지 않는다** (판정 ≠ 계상 확인).

- [ ] **Step 8: 커밋**

```bash
git add Assets/Scripts/Interaction/JailIntake.cs Assets/Scripts/Interaction/JailIntake.cs.meta Assets/Scripts/Interaction/JailZone.cs Assets/Scripts/Interaction/ArrestJudge.cs Assets/Scripts/Interaction/CustodyRouter.cs "Assets/Scenes/Main Scene.unity"
git commit -m "#492: 유치장 진입에서 판정하고 놓을 때 앉힌다 — JailIntake 신설"
```

---

## Task 3: 인계존 계열 제거

Task 2로 새 흐름이 동작하므로 인계 단말 경로는 죽은 코드가 됐다. 통째로 걷어낸다.

**Files:**
- Delete: `Assets/Scripts/Interaction/HqDropoffTerminal.cs` (+ `.meta`)
- Delete: `Assets/Scripts/Interaction/HqDropoffZone.cs` (+ `.meta`)
- Modify: `Assets/Scripts/Interaction/ArrestJudge.cs` (`TryDeliver`·`m_dropoffZone`·`Awake`의 탐색 제거)
- Modify: `Assets/Scripts/Player/Escort/PlayerEscortCommands.cs` (`RequestDeliver`·`DeliverRpc`·`ServerDeliver` 제거)
- Modify: `Assets/Scripts/NPC/States/NpcStateRules.cs` (`CanDeliver` 제거)
- Modify: `Assets/Scenes/Main Scene.unity` (인계 단말·인계 구역 오브젝트 삭제)

- [ ] **Step 1: 남은 참조 전수 확인**

```bash
grep -rn "HqDropoff\|TryDeliver\|CanDeliver\|RequestDeliver\|ServerDeliver\|DeliverRpc" Assets/Scripts
```

기대 출력: 위 6개 파일 안에서만 나온다. 다른 파일이 나오면 그 파일도 이 태스크에 포함시킨다.

- [ ] **Step 2: `ArrestJudge`에서 인계존 제거**

`Assets/Scripts/Interaction/ArrestJudge.cs`에서:
- `m_dropoffZone` 필드와 그 `[Header]`/`[Tooltip]` 삭제
- `Awake`의 `if (m_dropoffZone == null) m_dropoffZone = FindFirstObjectByType<HqDropoffZone>();` 삭제 (`base.Awake()`는 유지)
- `TryDeliver` 메서드 전체 삭제

클래스 주석 2번째 문단을 교체:

```csharp
/// 유치장에 들어선 신병을 JailIntake가 판정 요청하면(#492) 판정하고, 결과를 로그 + OnArrestJudged로
/// 알린다. 인계존 도달 자동 판정(#59)과 인계 단말 수동 판정(#414)은 모두 폐기됐다.
```

- [ ] **Step 3: `PlayerEscortCommands`에서 인계 요청 제거**

`Assets/Scripts/Player/Escort/PlayerEscortCommands.cs`에서 아래 셋을 삭제:
- `RequestDeliver()` (156-170행)
- `[Rpc(SendTo.Server)] private void DeliverRpc() => ServerDeliver();` (235-236행)
- `ServerDeliver()` 메서드 전체와 그 위 `// ---- 서버 실행: 본부 인계 (#414) ----` 구분 주석 (470-502행)

`using System.Collections.Generic;`가 다른 곳에서 안 쓰이면 함께 제거한다 — 컴파일 경고로 확인.

- [ ] **Step 4: `NpcStateRules.CanDeliver` 제거**

`Assets/Scripts/NPC/States/NpcStateRules.cs`의 105-111행(`CanDeliver` XML 주석 포함) 삭제.

- [ ] **Step 5: 스크립트 파일 삭제**

```bash
git rm Assets/Scripts/Interaction/HqDropoffTerminal.cs Assets/Scripts/Interaction/HqDropoffTerminal.cs.meta Assets/Scripts/Interaction/HqDropoffZone.cs Assets/Scripts/Interaction/HqDropoffZone.cs.meta
```

- [ ] **Step 6: 컴파일 확인**

기대: 에러 0.

- [ ] **Step 7: 씬에서 인계존 오브젝트 삭제**

Main Scene에서 `HqDropoffTerminal`·`HqDropoffZone` 컴포넌트가 붙어 있던 오브젝트를 찾아 삭제한다. 스크립트가 사라졌으므로 Missing Script로 표시된다.

**삭제한 오브젝트의 정확한 이름을 적어 둘 것** — PR 본문에 반드시 기재해야 한다(씬 diff는 리뷰어가 읽을 수 없다).

씬 저장 후 Console에 Missing Script 경고가 없는지 확인.

- [ ] **Step 8: 커밋**

```bash
git add -A Assets/Scripts Assets/Scenes
git commit -m "#492: 인계존·인계 단말 제거 — 판정이 유치장으로 옮겨져 죽은 경로"
```

---

## Task 4: `NpcJailedState` 주석 갱신 (로직 변경 없음)

**좌석까지 걸어가는 것은 그대로 둔다.** 폐기한 것은 "NPC가 도시에서 유치장까지 스스로 간다"이지 "벤치까지 두 걸음 간다"가 아니다.

실측하면 놓은 자리에서 가장 가까운 빈 좌석까지가 **1.5~5.6m**다(방 중앙 1.76m, 문 앞 1.51m, 앞자리가 다 찼을 때 최대 5.61m). 워프로 처리하면 눈에 띄는 순간이동이 된다. 게다가 이 걷기 코드는 #462에서 이미 다듬은 것이다 — 0.25m까지 걸어간 뒤 남은 오차만 워프로 흡수하고, 경로 실패·타임아웃에는 좌석으로 옮겨 앉히는 폴백이 있다.

#462의 입구 고착이 재발하지 않는 근거: 그 버그는 절차적으로 계산한 자리가 문↔셀 통로에 떨어져 생겼다. 지금 좌석은 손으로 배치해 통로를 비켜 있고, 걷는 거리도 방 하나 안이며, 실패해도 폴백이 좌석에 앉힌다.

**바뀌는 것은 주석뿐이다** — 계상 주체와 진입 경로 설명이 낡았다.

**Files:**
- Modify: `Assets/Scripts/NPC/States/NpcJailedState.cs` (클래스 XML 주석만)

- [ ] **Step 1: 클래스 주석 교체**

`Assets/Scripts/NPC/States/NpcJailedState.cs`의 클래스 XML 주석(4-14행) 전체를 아래로 교체한다. **코드는 한 줄도 건드리지 않는다.**

```csharp
/// <summary>
/// 수감(Jailed) 상태 — 배정된 좌석까지 걸어가 앉는다. (GDD 7-2, #228/#462/#492)
/// 걷기 → 좌석 방향으로 돌기 → 앉기를 한 상태에서 처리하고(연행 #97과 같은 구조), 앉으면 최종 상태다.
/// 이송 중에는 로컬 회피를 끈다 — 이유는 Enter 주석.
///
/// <b>여기 걷기는 방 하나 안에서의 마지막 몇 미터다</b> (#492). 유치장까지 데려오는 것은 플레이어의
/// 일이고(밧줄로 끌고 들어와 좌석 근처에서 놓는다), 이 상태는 놓인 자리에서 배정된 좌석까지
/// 1.5~5.6m를 걸어가 앉는 것만 한다. 도시에서 유치장까지 스스로 걷던 자동 이송은 폐기됐다.
///
/// <b>계약: 어떤 실패도 그 자리에 굳지 않는다.</b> 경로를 못 잡거나 잃거나 제 시간에 도착하지 못하면
/// 전부 좌석으로 옮겨 앉힌다(SeatByWarp) — 그 자리에 세우던 옛 처리가 입구를 막았다 (#462).
/// 목적지가 손으로 배치한 좌석인 근거는 JailZone.ReserveSeat 참고.
///
/// 진입 경로는 JailIntake다(#492) — 유치장 안에서 플레이어가 신병을 놓으면 좌석을 배정해 보낸다.
/// 정산 계상(JailZone.Admit)도 그쪽이 같은 시점에 한다 — 여기는 연출만 담당한다.
/// 빠져나가는 경로는 둘: 탈옥 방출(JailbreakEvent)과 플레이어의 반출(JailIntake.ServerExtract).
/// </summary>
```

- [ ] **Step 2: 컴파일 확인**

기대: 에러 0. (주석만 바꿨으므로 당연히 통과해야 한다 — 통과하지 않으면 코드를 잘못 건드린 것이다.)

- [ ] **Step 3: `git diff`로 코드 무변경 확인**

```bash
git diff --stat Assets/Scripts/NPC/States/NpcJailedState.cs
```

기대: 주석 줄 수만큼만 바뀐다. `SeatPhase`·`TickWalk`·`TickTurn`·`SeatByWarp`·`ArriveAtSeat`가 diff에 나오면 안 된다.

- [ ] **Step 4: 착석 동선 확인 (Editor Play)**

NPC를 묶어 유치장에 끌고 들어가 **방 한가운데서** E로 놓는다.
기대: 순간이동하지 않고 가장 가까운 빈 좌석까지 **걸어가서** 좌석 방향으로 돌아 앉는다. 몸이 벤치에 파묻히거나 허공에 뜨지 않는다.

앞자리가 찬 상태에서 한 번 더 해 본다(먼 좌석 배정).
기대: 더 먼 좌석까지 걸어가고, 도중에 문턱이나 다른 수감자에게 걸려 멈추지 않는다.

- [ ] **Step 5: 커밋**

```bash
git add Assets/Scripts/NPC/States/NpcJailedState.cs
git commit -m "#492: 수감 상태 주석 갱신 — 진입 경로와 계상 주체가 JailIntake로 옮겨졌다"
```

---
## Task 5: 수감자 빼내기 — 밧줄 없이 따라오게 한다

**Files:**
- Modify: `Assets/Scripts/NPC/States/NpcStateRules.cs` (`HasInteractKeyAction`에 `Jailed` 추가)
- Modify: `Assets/Scripts/NPC/NpcSubdueInteractable.cs` (`Jailed` 분기 추가)
- Modify: `Assets/Scripts/Player/Escort/PlayerEscortCommands.cs` (반출 요청 진입점 + RPC)

**Interfaces:**
- Consumes: `JailIntake.ServerExtract(NpcController npc, Transform follower)` (Task 2)
- Produces: `PlayerEscortCommands.RequestJailRelease(NpcController target)`

- [ ] **Step 1: `HasInteractKeyAction`에 `Jailed` 추가**

`Assets/Scripts/NPC/States/NpcStateRules.cs`의 `HasInteractKeyAction`을 교체:

```csharp
    /// <summary>E 상호작용이 반응하는 상태인가 — <b>신병 조작 전용</b>이다. (#438/#492)
    /// 포함 목록 방식 — 새 상태는 기본 'E 불가'이므로 열어야 하면 여기 추가할 것.
    /// NpcSubdueInteractable.Interact의 분기 집합과 반드시 일치해야 한다.
    ///
    /// <c>Captured</c>는 재연행(밧줄 끌기 재개), <c>Jailed</c>는 유치장 반출이다 —
    /// 앉은 수감자를 일으켜 밧줄 없이 따라오게 한다 (#492). 이미 확보가 끝난 대상이라
    /// 무력화나 채널링을 요구하지 않는다.
    /// 끌리는 중(<c>Escorted</c>)의 줄다리기 복귀는 상태가 아니라 "누구의 줄인가"로 갈리므로
    /// 순수 함수인 여기가 아니라 호출부가 판단한다 (#398).</summary>
    public static bool HasInteractKeyAction(NpcState state) =>
        state is NpcState.Captured or NpcState.Jailed;
```

- [ ] **Step 2: `NpcSubdueInteractable`에 `Jailed` 분기 추가**

`Assets/Scripts/NPC/NpcSubdueInteractable.cs`의 `Interact` switch에 `case NpcState.Captured:` 블록 **다음**에 추가:

```csharp
            case NpcState.Jailed:
                // 앉은 수감자를 일으켜 따라오게 한다 (#492) — 밧줄을 걸지 않는다.
                // 서버가 상태·사거리를 다시 검증하므로 여기 검사는 조기 차단일 뿐이다.
                Debug.Log($"E 입력 — 유치장 반출 요청: {m_controller.name}");
                escorter?.RequestJailRelease(m_controller);
                break;
```

클래스 주석 5행 뒤에 한 줄 추가:

```csharp
/// 수감(Jailed) 상태면 유치장에서 빼내 따라오게 한다 — 밧줄 없이 추종만 건다 (#492).
```

- [ ] **Step 3: `PlayerEscortCommands`에 반출 요청 추가**

`Assets/Scripts/Player/Escort/PlayerEscortCommands.cs`의 `RequestRelease` 다음에 추가:

```csharp
    /// <summary>유치장 반출 요청 — 오너가 호출(앉은 수감자에 E). 밧줄을 쓰지 않으므로 용량 게이트도 타지 않는다. (#492)</summary>
    public void RequestJailRelease(NpcController target)
    {
        if (target == null)
            return;
        if (!IsSpawned)
        {
            ServerJailRelease(target);
            return;
        }
        if (!IsOwner)
            return;
        if (!IsTargetNetworkReady(target))
            return;
        JailReleaseRpc(new NetworkObjectReference(target.NetworkObject));
    }
```

RPC 구역(`ReleaseRpc` 다음)에 추가:

```csharp
    [Rpc(SendTo.Server)]
    private void JailReleaseRpc(NetworkObjectReference targetRef)
    {
        if (
            targetRef.TryGet(out NetworkObject targetObj)
            && targetObj.TryGetComponent(out NpcController target)
        )
        {
            ServerJailRelease(target);
        }
    }
```

서버 실행부를 파일 끝의 `// ---- 공통 ----` **앞**에 추가:

```csharp
    // ---- 서버 실행: 유치장 반출 (#492) ----

    // 반출 실행 — 사거리만 확인하고 나머지(상태·좌석·정산)는 JailIntake가 판단한다.
    // 유치장을 아는 것은 저쪽이고 여기는 요청 허브일 뿐이다.
    private void ServerJailRelease(NpcController target)
    {
        if (IsSpawned && !IsServer)
            return;

        if (!IsInRange(target))
            return;

        JailIntake intake = FindFirstObjectByType<JailIntake>();
        if (intake == null)
        {
            Debug.LogWarning("PlayerEscortCommands: JailIntake가 없어 반출할 수 없다", this);
            return;
        }

        intake.ServerExtract(target, transform);
    }
```

> `FindFirstObjectByType`은 R1 규칙(App 파사드)의 예외다 — `JailIntake`는 매니저가 아니라 장소 오브젝트라 App 등록 대상이 아니고, `HqDropoffZone`·`JailLock`이 이미 같은 방식으로 탐색된다. 반출은 E 입력 때만 도는 경로라 비용도 문제되지 않는다.

- [ ] **Step 4: 컴파일 확인**

기대: 에러 0.

- [ ] **Step 5: 반출·되돌리기 확인 (Editor Play)**

1. 수감자를 하나 앉힌다. 누적 현상금을 기록해 둔다.
2. 앉은 수감자를 조준한다. 기대: 윤곽선이 켜진다.
3. E를 누른다. 기대: 일어나 따라온다(밧줄 선이 없다). 누적 현상금이 **즉시 줄어든다**.
4. 유치장 **안**에서 다시 E. 기대: 가장 가까운 빈 좌석에 다시 앉고 금액이 회복된다.
5. 다시 빼내 유치장 **밖**으로 데리고 나가 E. 기대: 그 자리에 서고(Captured) 앉지 않는다.
6. 수감자를 따라오게 한 채 멀리 걸어간다. 기대: 거리 이탈로 그 자리에 멈춘다.

- [ ] **Step 6: 커밋**

```bash
git add Assets/Scripts/NPC/States/NpcStateRules.cs Assets/Scripts/NPC/NpcSubdueInteractable.cs Assets/Scripts/Player/Escort/PlayerEscortCommands.cs
git commit -m "#492: 수감자 반출 — E로 일으켜 밧줄 없이 따라오게 한다"
```

---

## Task 6: `JailDoor` 자동 개폐 제거

자동 개폐가 `Jailed` NPC 근접 전용이라, 자동 이송이 사라진 지금 영영 발동하지 않는 죽은 코드다. 플레이어가 끄는 NPC는 `Escorted`이고 문은 플레이어 근접을 보지 않는다.

**Files:**
- Modify: `Assets/Scripts/Interaction/JailDoor.cs`

- [ ] **Step 1: 자동 개폐 필드·메서드 제거**

`Assets/Scripts/Interaction/JailDoor.cs`에서 삭제:
- `[Header("자동 개폐 ...")]`와 `m_autoOpenRadius`, `m_proximityCheckInterval` 필드
- `m_proximityCooldown`, `m_npcWasEnRoute` 필드
- `ServerTickAutoDoor()` 메서드 전체
- `IsJailBoundNpcNear(Vector3)` 메서드 전체

- [ ] **Step 2: `ShouldBeOpen`·`ServerToggleManual`·`Update` 정리**

`ShouldBeOpen`을 인자 없는 프로퍼티로 바꾼다:

```csharp
    // 지금 문이 열려 있어야 하는가 — 수동 개방(E)과 탈옥 진행 중 둘의 합.
    // 근접 자동 개폐는 제거됐다 (#492) — 수감 이송이 사라져 '스스로 걸어 들어오는 NPC'가 없다.
    private bool ShouldBeOpen => m_manualOpen || IsJailbreakHoldingOpen;
```

`ServerToggleManual`을 교체:

```csharp
    // 수동 개방 래치를 뒤집는다 — 서버(또는 오프라인) 전용.
    // 닫기를 눌러도 탈옥이 진행 중이면 열린 채로 남는다 — 그건 플레이어가 막을 개폐가 아니다(탈옥 신호).
    private void ServerToggleManual()
    {
        if (IsSpawned && !IsServer)
            return;

        m_manualOpen = !m_manualOpen;
        ServerSetOpen(ShouldBeOpen);
    }
```

`Update`를 교체 — 탈옥 상태가 바뀌면 문이 따라가야 하므로 서버는 계속 평가한다:

```csharp
    private void Update()
    {
        // 개폐 판단은 서버(또는 오프라인)만 한다 — 클라이언트는 동기화된 IsOpen을 보고 연출만 따라간다.
        // 매 프레임 평가해도 ServerSetOpen이 값이 같으면 조기 반환하므로 대역폭을 먹지 않는다.
        if (!IsSpawned || IsServer)
            ServerSetOpen(ShouldBeOpen);

        TickLeafSlide();
    }
```

- [ ] **Step 3: 클래스 주석 갱신**

13-21행의 자동 개폐 설명 문단을 교체:

```csharp
/// <b>플레이어는 상호작용키(E)로 여닫는다.</b> 경찰은 유치장에 드나들 권한이 있으니 자물쇠
/// (<see cref="JailLock"/>) 잠김과 무관하게 E가 먹힌다. E는 <b>토글</b>이라 한 번 열면 다시 누를
/// 때까지 열려 있다 — 신병을 끌고 드나드는 동안 등 뒤에서 닫히지 않게 하기 위해서다.
/// 근접 자동 개폐는 제거됐다 (#492): 수감 이송이 폐기돼 스스로 걸어 들어오는 NPC가 없고,
/// 신병을 끌고 오는 것은 플레이어라 열어 줄 주체가 이미 사람이다.
/// 자물쇠가 풀린 동안(탈옥, #231)에는 아무도 없어도 계속 열어 둔다: "문이 열려 있다"가 탈옥을
/// 알아채는 신호이기 때문이다.
```

- [ ] **Step 4: 컴파일 확인**

기대: 에러 0.

- [ ] **Step 5: 문 동작 확인 (Editor Play)**

1. 문 앞에서 E. 기대: 열린다. 다시 E. 기대: 닫힌다.
2. 문을 연 채 신병을 끌고 들어가 앉히고 나온다. 기대: 문이 등 뒤에서 저절로 닫히지 않는다(갇히지 않는다).
3. 탈옥 이벤트를 발동시킨다. 기대: 자물쇠가 풀린 동안 문이 열린 채 유지되고, 수감자가 다 빠져나가면 닫힌다.

- [ ] **Step 6: 커밋**

```bash
git add Assets/Scripts/Interaction/JailDoor.cs
git commit -m "#492: 유치장 문 자동 개폐 제거 — 수감 이송이 사라져 죽은 경로"
```

---

## Task 7: GDD 갱신

**Files:**
- Modify: `docs/GDD.md`

- [ ] **Step 1: 7-2 신병 처리 갱신**

219-232행 부근. 기존 문장은 지우지 않고 **변경 노트 형식**으로 덧붙인다 — GDD의 다른 변경 이력이 전부 이 형식이다(51행·142행 등 참고). 232행("유치장은 범인 탈출 이벤트의 무대이기도 하다 —") **바로 앞**에 삽입:

```markdown
> **2026-08-03 변경 (#492)** — **인계존과 인계 단말이 폐기됐다.** 분류(판정)를 트리거하는 장소는
> 이제 **유치장 진입**이다: 연행한 신병을 끌고 유치장 문턱을 넘는 순간 혐의가 갈린다. 오검거를
> 좌석까지 끌고 가야 알게 되는 헛수고를 없애기 위한 것이다.
>
> - **수용은 플레이어가 직접 한다.** 대상이 스스로 걸어가던 자동 이송은 폐기됐다. 신병을 끌고
>   들어가 **좌석에 놓아야**(E) 수용이 성립한다. 판정만 받고 앉히지 않으면 **정산에 계상되지 않는다** —
>   문턱까지만 데려다 놓으면 0원이다.
> - **수감자는 다시 빼낼 수 있다.** 앉은 수감자에게 E를 누르면 일어나 **밧줄 없이 따라온다**.
>   빼내는 순간 좌석이 비고 정산·할당량 진행도에서 빠진다("끝까지 데리고 있어야 인정", 9-2).
>   유치장 안에서 다시 E를 누르면 재수용되고 재판정된다.
> - 유치장 문의 근접 자동 개폐도 함께 제거됐다 — 스스로 걸어 들어오는 NPC가 없어졌으므로
>   여닫는 것은 플레이어의 E 토글뿐이다.
```

- [ ] **Step 2: 10-2 `Jailed` 설명 갱신**

472행 `- \`Jailed\` — 인계 판정 후 유치장으로 이송·수용...`을 교체:

```markdown
- `Jailed` — 유치장 좌석에 수감. 플레이어가 직접 끌고 들어가 놓으면 진입한다(자동 이송은 #492에서 폐기). 좌석에 앉은 전용 모션을 쓴다(#462)
```

- [ ] **Step 3: 용어집 갱신**

550-551행. `인계존` 항목을 삭제하고 `유치장` 항목을 교체:

```markdown
| 유치장 | 본부의 관리 구역. **판정과 수용이 함께 일어나는 곳** — 신병을 끌고 들어서면 혐의가 분류되고, 좌석에 놓으면 수용된다 (7-2) |
```

- [ ] **Step 4: 7-6 인계 방치 문구 갱신**

359행 `- **본부 인계 판정이 끝난 대상은 제외**...`를 교체. 타이머 동작은 그대로이고 면제 기준이 가리키는 지점만 바뀐다:

```markdown
- **판정이 끝난 대상은 제외** — 유치장에 들어서면 판정되고(#492) 그 뒤 처리는 유치장 시스템 몫이다.
```

- [ ] **Step 5: 커밋**

```bash
git add docs/GDD.md
git commit -m "#492: GDD 갱신 — 판정 장소가 인계존에서 유치장으로"
```

---

## 최종 확인 (전체 통합)

모든 태스크가 끝난 뒤 한 번에 돌린다. Multiplayer Play Mode **2인(호스트 + 클라)**.

- [ ] 진범을 끌고 문 통과 → 판정 로그가 뜨고 **밧줄은 유지**된다
- [ ] 그 상태로 라운드를 끝내면 **진행도 금액이 오르지 않는다** (판정 ≠ 계상)
- [ ] 좌석 앞에서 E로 놓으면 가장 가까운 빈 좌석에 앉고 그때 금액이 오른다
- [ ] 방 한가운데서 놓아도 **순간이동하지 않고 걸어가서** 앉는다 (배정 거리 최대 5.6m)
- [ ] 오검거를 끌고 문 통과 → 문턱에서 판정되고 페널티로 전이, **원한 구역까지 실제로 이동한다**
      ↳ 막히면 스펙의 "구현 중 실측이 필요한 것" 참고 — 판정 지점을 문 바깥으로 빼는 것이 대안
- [ ] 앉은 수감자에 E → 일어나 따라온다(밧줄 없음), 진행도 금액이 즉시 줄어든다
- [ ] 따라오는 수감자에 유치장 안에서 E → 다시 앉고 금액이 회복된다
- [ ] 따라오는 수감자에 유치장 밖에서 E → 그 자리에 서고 방치 타이머가 돈다
- [ ] 따라오는 수감자를 두고 멀리 가면 거리 이탈로 정지, 유치장 안이었으면 다시 앉는다
- [ ] 문이 E 토글로만 여닫히고, 수감자를 데리고 드나드는 동안 갇히지 않는다
- [ ] 정원 18석까지 채워도 입구가 막히지 않는다 (#462 회귀)
- [ ] 탈옥(#231) 방출·재검거가 정상 동작한다
- [ ] **클라이언트 화면에서도** 착석·기립 표현이 맞다 (`IsSeated` 동기화)
- [ ] 라운드 종료 정산(#340) 인원·현상금이 유치장 점유와 일치한다

## PR 준비

- base는 `feature/462-jail-seat-bench`
- 본문은 팀 고정 4섹션(요약 / 테스트 방법 / 씬·프리팹 변경 / 후속으로 미룬 것)
- **씬 변경 칸에 Task 3에서 삭제한 오브젝트 이름을 반드시 기재** — 씬 diff는 리뷰어가 읽을 수 없다
- `JailIntake` 추가 배치도 기재
