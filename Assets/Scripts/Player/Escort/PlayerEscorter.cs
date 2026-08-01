using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 플레이어의 밧줄 연결 상태 — <b>누구를 묶고 있고, 그 연결을 매 프레임 어떻게 유지하는가</b>. (#269/#369/#390/#398)
/// 밧줄 <b>1개당 NPC 1명</b>이라 연결은 목록이고, 동시 인원의 상한은 소지한 밧줄 개수다.
///
/// 요청·검증·채널링은 <see cref="PlayerEscortCommands"/>가 갖는다 — 구동 주체가 다르기 때문이다:
/// 저쪽은 플레이어 입력이 올 때만 돌고(좌클릭·E), 이쪽은 서버에서 매 프레임 돈다. 의존은
/// <c>Commands → Escorter</c> 한 방향뿐이고, 목록의 소유자는 이 컴포넌트 하나다.
///
/// 장력 계산 자체는 끌리는 <see cref="NpcController"/>가, 끄는 쪽이 지는 대가(무게·목줄 제한, #398)는
/// <see cref="RopeDragLoad"/>가 갖는다 — 여기 있는 것은 "누구를 묶고 있나"의 참조 관리와
/// 커스터디 이탈·거리 끊김 감지다.
///
/// ⚠ 채널링을 하지 않는데도 <see cref="ChanneledInteractionBehaviour"/>를 상속하는 이유는 오너
/// 판정 피드백(<c>NotifyOwner</c>, #91)이 거기 있기 때문이다 — 줄 끊김·놓기를 오너 화면에 알려야 한다.
/// 그 기반이 지금 "채널링 게이지 + 오너 피드백" 두 가지를 함께 들고 있어서 생긴 어긋남이고,
/// 피드백만 별도 기반으로 가르는 것은 <c>ItemBase</c> 계층까지 건드리므로 후속 과제로 둔다.
/// </summary>
public class PlayerEscorter : ChanneledInteractionBehaviour
{
    [Header("밧줄 끌기")]
    // 장력 튜닝 값(길이·스무딩·흔들림·간격)은 NpcRopeDragConfig에 있다 — 장력 계산과 같은 자리.
    [Tooltip("이 거리(m)를 넘게 멀어지면 밧줄이 끊겨 NPC가 풀려난다 — 벽에 막혀 못 따라오거나 놓아둔 채 걸어가면 발생. 밧줄 길이보다 넉넉해야 한다")]
    [SerializeField] private float m_ropeBreakDistance = 10f;

    [Header("무게 페널티 — #398")]
    [Tooltip("끌고 있는 무게 1당 깎이는 이동속도 비율 — 표준 무게(1.0) 1명을 혼자 끌면 이만큼 느려진다. 무게 자체는 NpcCommonConfig에서 추첨된다")]
    [SerializeField] private float m_dragSlowPerWeight = 0.25f;

    [Tooltip("무게 페널티 하한(배율) — 아무리 무거워도 이 아래로는 느려지지 않는다. 0으로 두면 이동이 완전히 막힐 수 있다")]
    [SerializeField] private float m_minDragSpeedFactor = 0.35f;

    // 내 밧줄에 묶여 있는 NPC들 — 서버(또는 오프라인) 진실. 끌기를 멈춰도(E) 남는다.
    private readonly List<NpcController> m_tethered = new List<NpcController>();

    // 위 목록의 클라 사본(서버만 쓴다). 표시(RopeDragView)가 선의 양 끝점을 알아야 하고,
    // 오너 조기검증(Rope.CanTarget·E 놓기 대상)도 "내가 이걸 묶었나"를 물어야 한다.
    // 항목마다 '끌고 있는가'를 싣는 이유: 줄다리기로 한 NPC에 여러 명이 걸리면
    // NpcController.IsRoped("누구든 끌고 있다")로는 내가 놓았는지를 알 수 없다.
    // ⚠ late-join 클라는 OnListChanged를 못 받는다 — 읽는 쪽이 현재 목록을 직접 훑을 것 (WantedListManager와 같은 주의).
    private readonly NetworkList<RopeTether> m_tetheredSynced = new NetworkList<RopeTether>();

    /// <summary>지금 밧줄에 묶여 있는 인원 수. 전 피어에서 유효.</summary>
    public int TetheredCount => IsSpawned && !IsServer ? m_tetheredSynced.Count : m_tethered.Count;

    // 끌고 있는 무게로 깎인 이동속도 배율 — 서버(또는 오프라인) 진실. 매 프레임 다시 계산된다.
    private float m_dragSpeedFactor = 1f;

    // 위 값의 클라 사본 — 이동 권한이 오너라 배율도 오너가 적용해야 하고, 원격 피어의 걷기 애니메이션
    // 블렌드도 같은 기준 속도를 봐야 한다(PlayerAnimationDriver).
    // ⚠ 기본값 1 — 0으로 두면 세션 없는 오프라인 Play에서 배율 0이 되어 아예 못 움직인다.
    private readonly NetworkVariable<float> m_dragSpeedFactorSynced = new NetworkVariable<float>(1f);

    /// <summary>무게로 깎인 이동속도 배율(0~1) — 아무것도 끌지 않으면 1. 전 피어에서 유효. (#398)</summary>
    public float DragSpeedFactor =>
        IsSpawned && !IsServer ? m_dragSpeedFactorSynced.Value : m_dragSpeedFactor;

    private const int k_leashDraggerCount = 2;

    // 수갑 자원 게이트·소모용 로드아웃 (#229). 테스트 구성 등 없을 수 있어 null 허용.
    private PlayerLoadout m_loadout;

    private PlayerLoadout Loadout
    {
        get
        {
            if (m_loadout == null)
                m_loadout = GetComponent<PlayerLoadout>();
            return m_loadout;
        }
    }

    // 기능 정지된 동료를 끄는 중인가 (#365) — 운반도 밧줄 한 개를 쓰므로 용량 계산(RopesInUse)에 들어간다.
    // '한 번에 1명'이 폐기된 뒤(#390) 운반과 NPC 끌기는 배타가 아니라 같은 자원을 나눠 쓰는 관계다.
    // 없는 구성(테스트 등)이면 false.
    private PlayerCarrier m_carrier;

    private bool IsCarryingPlayer
    {
        get
        {
            if (m_carrier == null)
                m_carrier = GetComponent<PlayerCarrier>();
            return m_carrier != null && m_carrier.IsCarrying;
        }
    }

    /// <summary>동시에 묶을 수 있는 상한 — 로드아웃이 없으면(테스트 구성) 무제한.</summary>
    internal int RopeCapacity => Loadout != null ? Loadout.RopeCount : int.MaxValue;

    // 지금 쓰고 있는 줄 수 — 묶어 둔 NPC + 기능 정지 동료 운반 1명(#365). 동료도 같은 밧줄로 끌기 때문에
    // NPC와 같은 자원 풀을 나눠 쓴다: 밧줄 2개면 NPC 1명을 끌면서 동료 1명을 옮길 수 있고, 1개면 둘 중 하나다.
    private int RopesInUse => TetheredCount + (IsCarryingPlayer ? 1 : 0);

    /// <summary>소지한 밧줄을 전부 쓰고 있는가 — 새 대상을 묶는(또는 동료를 드는) 것을 막는 자원 게이트.</summary>
    public bool IsAtRopeCapacity => RopesInUse >= RopeCapacity;

    /// <summary>
    /// 서버 진실 목록 — <see cref="PlayerEscortCommands"/>의 인계 순회 전용. 서버(또는 오프라인)에서만 유효.
    /// 순회 중 목록이 바뀌므로(판정 성공 → Jailed → 정리) 읽는 쪽이 복사해서 돌 것.
    /// </summary>
    internal IReadOnlyList<NpcController> ServerTethered => m_tethered;

    // ---- 정적 조회 (전 피어) ----

    /// <summary>
    /// 해당 NPC를 밧줄에 묶고 있는 플레이어를 찾는다 — 없으면 null. 서버(또는 오프라인)에서만 유효.
    /// 인계 판정(ArrestJudge)이 이 결과로 끌기를 물리적으로 풀기 때문에, 빼면 인계자가 "알 수 없음"이 되고
    /// 끌기가 안 풀린 채(에이전트 꺼진 채) 상태 전이가 일어나 NavMeshAgent 예외가 난다. (#269)
    /// 여러 명이 걸려 있으면 그중 하나다 — 전원이 필요하면 <see cref="FindEscortersOf"/>.
    /// </summary>
    public static PlayerEscorter FindEscorterOf(NpcController npc)
    {
        if (npc == null)
            return null;

        PlayerEscorter[] escorters = FindObjectsByType<PlayerEscorter>(FindObjectsSortMode.None);
        foreach (PlayerEscorter escorter in escorters)
            if (escorter.IsTetheredTo(npc))
                return escorter;

        return null;
    }

    /// <summary>
    /// 해당 NPC에 밧줄을 걸고 있는 플레이어를 <b>전부</b> 찾는다 — 아무도 없으면 빈 목록. 서버(또는 오프라인) 전용.
    /// 줄다리기로 여러 명이 한 대상에 걸릴 수 있어, 인계 판정·오검거 페널티처럼 "관여한 사람 전원"을
    /// 알아야 하는 쪽이 이걸 쓴다.
    /// </summary>
    public static List<PlayerEscorter> FindEscortersOf(NpcController npc)
    {
        var found = new List<PlayerEscorter>();
        if (npc == null)
            return found;

        PlayerEscorter[] escorters = FindObjectsByType<PlayerEscorter>(FindObjectsSortMode.None);
        foreach (PlayerEscorter escorter in escorters)
            if (escorter.IsTetheredTo(npc))
                found.Add(escorter);

        return found;
    }

    // ---- 연결 조회 (전 피어) ----

    /// <summary>묶인 대상을 순번으로 얻는다 — 전 피어에서 유효한 표현·검증용. 없거나 못 찾으면 null.</summary>
    public NpcController GetTetheredNpc(int index)
    {
        if (!IsSpawned || IsServer)
            return index >= 0 && index < m_tethered.Count ? m_tethered[index] : null;

        if (index < 0 || index >= m_tetheredSynced.Count)
            return null;

        // 세션이 내려가는 중에는 매니저가 이미 사라져 있다. IsSpawned만으로는 이 순간을 거를 수 없어
        // (디스폰 통지보다 매니저 소멸이 앞설 수 있다) 라운드 종료 후 씬이 바뀌는 동안
        // 표시(RopeDragView.LateUpdate)가 매 프레임 NullReferenceException을 뱉는다.
        NetworkManager manager = NetworkManager.Singleton;
        if (manager == null || !manager.IsListening)
            return null;

        return manager.SpawnManager.SpawnedObjects.TryGetValue(
            m_tetheredSynced[index].NpcId, out NetworkObject npcObject)
            && npcObject.TryGetComponent(out NpcController npc)
            ? npc
            : null;
    }

    /// <summary>이 NPC가 <b>내</b> 밧줄에 묶여 있는가 — 전 피어에서 유효. 좌클릭 분기·E 놓기 대상 판정이 쓴다.</summary>
    public bool IsTetheredTo(NpcController npc) => IndexOfTether(npc) >= 0;

    /// <summary>이 NPC를 <b>내가 지금 끌고</b> 있는가 — 전 피어에서 유효.
    /// 묶여만 있는(E로 놓아둔) 대상은 <b>남이 대신 끌고 있어도</b> false다 — 그래야 줄다리기 중
    /// 내 E가 계속 '놓기'로 소비되지 않는다.</summary>
    public bool IsDraggingNpc(NpcController npc)
    {
        if (!IsTetheredTo(npc))
            return false;

        // 서버는 NPC의 앵커 목록이 단일 진실. 클라는 그게 실려 온 동기화 항목을 읽는다.
        if (!IsSpawned || IsServer)
            return npc.IsDraggedBy(transform);

        int index = IndexOfSynced(npc);
        return index >= 0 && m_tetheredSynced[index].Dragging;
    }

    // 이 NPC가 내 목록의 몇 번째인가 — 없으면 -1. 전 피어에서 유효하되 인덱스는 서버·클라가 다를 수 있다
    // (미스폰 NPC는 서버 목록에만 들어가 길이가 어긋난다).
    private int IndexOfTether(NpcController npc)
    {
        if (npc == null)
            return -1;

        return !IsSpawned || IsServer ? m_tethered.IndexOf(npc) : IndexOfSynced(npc);
    }

    // 동기화 목록에서 이 NPC의 항목 위치 — 없거나 id를 못 읽으면 -1.
    private int IndexOfSynced(NpcController npc)
    {
        if (npc == null || npc.NetworkObject == null)
            return -1;

        ulong id = npc.NetworkObject.NetworkObjectId;
        for (int i = 0; i < m_tetheredSynced.Count; i++)
            if (m_tetheredSynced[i].NpcId == id)
                return i;
        return -1;
    }

    // ---- 연결 목록 관리 (서버·오프라인 전용) ----

    /// <summary>
    /// 연결을 맺는다 — 묶기·합류·재개가 전부 여기로 온다 (<see cref="PlayerEscortCommands"/>가 검증 후 호출).
    /// 직후 StartRopeDrag로 앵커가 붙으므로 항상 '끌고 있음'으로 시작한다.
    /// </summary>
    internal void AddTether(NpcController npc)
    {
        if (npc == null)
            return;

        if (!m_tethered.Contains(npc))
            m_tethered.Add(npc);

        SetTetherDragging(npc, true);
    }

    // 동기화 항목의 끌기 표시를 갱신한다 — 값이 그대로면 쓰지 않는다(매 프레임 정리가 불러도 대역폭을 먹지 않게).
    // 서버 자신은 이 표시를 읽지 않는다(NpcController.IsDraggedBy가 진실) — 순전히 클라에 알리는 용도다.
    private void SetTetherDragging(NpcController npc, bool dragging)
    {
        if (!IsSpawned || !IsServer)
            return;

        int index = IndexOfSynced(npc);
        if (index < 0)
        {
            // 스폰된 대상만 동기화 목록에 실을 수 있다 — 아니면 표시 없이 끌기만 진행된다(오프라인 테스트 등)
            if (npc.NetworkObject != null && npc.NetworkObject.IsSpawned)
            {
                m_tetheredSynced.Add(
                    new RopeTether { NpcId = npc.NetworkObject.NetworkObjectId, Dragging = dragging });
            }
            return;
        }

        RopeTether entry = m_tetheredSynced[index];
        if (entry.Dragging == dragging)
            return;

        entry.Dragging = dragging;
        m_tetheredSynced[index] = entry;
    }

    /// <summary>이 대상과의 연결을 끊는다 — 밧줄 풀기(<see cref="PlayerEscortCommands"/>) 전용. 서버(또는 오프라인).</summary>
    internal void RemoveTether(NpcController npc)
    {
        int index = m_tethered.IndexOf(npc);
        if (index >= 0)
            RemoveTetherAt(index);
    }

    // 인덱스로 지운다 — 매 프레임 정리가 역순 순회하며 부르기 때문. npc가 이미 파괴됐을 수 있다.
    private void RemoveTetherAt(int index)
    {
        NpcController npc = m_tethered[index];
        m_tethered.RemoveAt(index);

        if (!IsSpawned || !IsServer)
            return;

        // 살아 있으면 id로 정확히 지우고, 파괴돼 id를 못 읽으면 죽은 항목을 훑어 정리한다.
        int syncedIndex = IndexOfSynced(npc);
        if (syncedIndex >= 0)
        {
            m_tetheredSynced.RemoveAt(syncedIndex);
            return;
        }

        if (npc == null || npc.NetworkObject == null)
            PruneDeadSyncedEntries();
    }

    // 대상이 파괴·디스폰돼 더는 풀리지 않는 항목을 걷어낸다 — 남겨두면 원격 피어가 없는 줄을 계속 찾는다.
    private void PruneDeadSyncedEntries()
    {
        NetworkManager manager = NetworkManager.Singleton;
        if (manager == null || !manager.IsListening)
            return;

        for (int i = m_tetheredSynced.Count - 1; i >= 0; i--)
            if (!manager.SpawnManager.SpawnedObjects.ContainsKey(m_tetheredSynced[i].NpcId))
                m_tetheredSynced.RemoveAt(i);
    }

    // ---- 매 프레임 연결 유지 (서버·오프라인 전용) ----

    private void Update()
    {
        // 참조 정리는 서버(또는 오프라인)에서만 — 끌기 상태 자체가 서버 권위다 (#56/#118).
        if (IsSpawned && !IsServer)
            return;

        // 순서 강제 — 정리가 먼저 돌아야 사라진 대상이 무게 계산에 잡히지 않는다.
        TickTetherCleanup();
        AssignDragSlotsAndSpeed();
    }

    /// 밧줄 연결 매 프레임 정리. 장력은 <see cref="NpcController"/>가 계산한다 —
    /// 여기는 커스터디 이탈·거리 끊김에 따른 참조 관리만.
    private void TickTetherCleanup()
    {
        // 지우면서 도니 역순 — 각 연결은 서로 독립이라 하나가 끊겨도 나머지는 유지된다.
        for (int i = m_tethered.Count - 1; i >= 0; i--)
        {
            NpcController npc = m_tethered[i];

            // 대상이 커스터디를 벗어나면 밧줄 연결도 끊는다 — 인계 판정(→Jailed)·방치 탈주·풀기(→Idle)·
            // 라운드 종료 파괴가 전부 여기로 수렴한다(참조가 Unity 가짜 null이 되는 파괴 경로 포함, #356).
            if (npc == null
                || (npc.CurrentState != NpcState.Escorted && npc.CurrentState != NpcState.Captured))
            {
                RemoveTetherAt(i);
                continue;
            }

            // 너무 멀어지면 줄이 끊겨 풀려나 달아난다 — 벽에 막혀 못 따라오거나 놓아둔 채 걸어간 경우 (#369).
            // ReleaseDrag가 먼저인 이유: 도주(Run)가 NavMesh를 쓰는데 끌기 중엔 에이전트가 꺼져 있다.
            // 목줄이 걸린 동안(줄다리기)만 예외다 (#398) — 둘을 함께 켜 두면 같은 거리를 경계로
            // "막힘"과 "끊김"이 매 프레임 다툰다.
            if (!IsLeashedTo(npc) && IsTooFarToTether(npc))
            {
                ReleaseDrag(npc);
                RemoveTetherAt(i);

                // 다른 참가자가 아직 잡고 있으면 도주시키지 않는다 — 내 줄만 끊긴 것이다.
                // 줄다리기에서 밀린 쪽이 빠지는 정상 결말이라, 여기서 도주시키면 이긴 쪽 손에서 사라진다.
                if (FindEscorterOf(npc) != null)
                {
                    NotifyOwner($"밧줄 끊김 — 내 줄만 끊겼다 (다른 참가자가 계속 확보 중): {npc.name}");
                    continue;
                }

                NotifyOwner($"밧줄 끊김 — 너무 멀어져 도주: {npc.name}");
                npc.StartFlee(transform);
                continue;
            }

            // 외부 요인으로 커스터디에서 벗어났으면(넉백·페널티 등 강제 상태 전이) 끌기만 정리한다 — 줄은 유지.
            if (npc.CurrentState != NpcState.Escorted)
                ReleaseDrag(npc);
        }
    }

    // 끊김 판정 — 수평 거리만 본다(끌기 장력과 같은 기준, 계단·경사에서 y차로 오작동하지 않게).
    private bool IsTooFarToTether(NpcController npc)
    {
        Vector3 delta = npc.transform.position - transform.position;
        delta.y = 0f;
        return delta.sqrMagnitude > m_ropeBreakDistance * m_ropeBreakDistance;
    }

    // ---- 무게·목줄 (#398) ----

    /// <summary>
    /// 이 대상의 밧줄이 지금 <b>목줄</b>로 나를 붙잡는가 — 끊김 판정(서버)과 이동 제한(오너)이 같은 기준을
    /// 봐야 해서 한 곳에 둔다. 전 피어에서 유효. (#398)
    ///
    /// 혼자 끌 때는 걸지 않는다 — 걸면 벽에 걸린 대상을 흘리고 갈 수 없어져 "막혀서 못 감"이 유일한
    /// 결말이 된다. 놓아둔 줄도 제외 — 늘어나다 끊기는 것이 손을 떼는 수단이다.
    /// </summary>
    private bool IsLeashedTo(NpcController npc) =>
        IsDraggingNpc(npc) && npc.DraggerCount >= k_leashDraggerCount;

    /// <summary>
    /// 팽팽해진 밧줄이 허용하는 만큼으로 수평 입력 이동 속도(m/s)를 깎아 돌려준다 —
    /// <b>오너 로컬</b>, 매 프레임 <see cref="PlayerMovement"/>가 부른다. (#398)
    ///
    /// 반경은 <b>끊김 거리 ÷ 참가자 수</b>다. NPC가 합력으로 참가자들의 중점에 오므로
    /// (NpcController.TickRopeDrag) 각자의 거리가 곧 전체의 1/n이고, 각자 자기 반경만 지키면
    /// <b>합이 끊김 거리로 묶인다</b> — 2인이면 5m씩, 총 10m. 상대 위치를 몰라도 성립한다.
    /// </summary>
    public Vector3 ConstrainByTautRopes(Vector3 horizontalVelocity)
    {
        int count = TetheredCount;
        for (int i = 0; i < count; i++)
        {
            NpcController npc = GetTetheredNpc(i);
            if (npc == null || !IsLeashedTo(npc))
                continue;

            Vector3 toNpc = npc.transform.position - transform.position;
            toNpc.y = 0f; // 끊김 판정·장력과 같은 기준 — 계단·경사에서 y차로 오작동하지 않게
            float distance = toNpc.magnitude;
            if (distance < 0.001f)
                continue;

            float radius = m_ropeBreakDistance / Mathf.Max(1, npc.DraggerCount);
            if (distance < radius)
                continue; // 아직 늘어져 있다 — 자유롭게 움직인다

            // 멀어지는 성분만 지운다. 안쪽·접선 방향은 열어 둬야 팽팽한 채로 원을 돌거나 되돌아올 수 있다.
            Vector3 outward = -toNpc / distance;
            float away = Vector3.Dot(horizontalVelocity, outward);
            if (away > 0f)
                horizontalVelocity -= outward * away;
        }

        return horizontalVelocity;
    }

    // 끌고 있는 대상들에 자리 번호를 매기고, 같은 순회에서 무게 페널티(#398)를 계산한다.
    //
    // 자리 번호를 받은 NPC가 밧줄 방향에 수직으로 벌려 부채꼴이 된다. 없으면 전원이 내 발밑 한 점에
    // 앵커돼 같은 지점으로 수렴하고, 벽 스윕이 사람은 장애물로 안 쳐서 (#313/#339) 서로 통과해
    // 한 덩어리가 된다.
    // 매 프레임 다시 도는 이유: 놓기·인계·끊김으로 구성이 수시로 바뀐다. n이 소지 밧줄 수(최대 3)라
    // 비용이 없고, 참가자 이탈에 따른 페널티 재계산도 공짜로 따라온다.
    private void AssignDragSlotsAndSpeed()
    {
        int draggingCount = 0;
        for (int i = 0; i < m_tethered.Count; i++)
            if (m_tethered[i].IsDraggedBy(transform))
                draggingCount++;

        int slot = 0;
        float weightSum = 0f;
        for (int i = 0; i < m_tethered.Count; i++)
        {
            NpcController npc = m_tethered[i];
            if (!npc.IsDraggedBy(transform))
                continue; // 묶여만 있는(E로 놓아둔) 대상은 안 끌고 있으니 무게도 지지 않는다

            npc.SetDragSlot(slot, draggingCount);
            slot++;

            // 여러 명이 같은 대상을 함께 끌면 참가자 수로 나눠 진다(다인 완화식). 여러 명을 동시에
            // 끌 때의 무게 합산도 이 누적이 그대로 한다 — 상한이 슬롯이 아니라 무게 예산이 되는 지점.
            weightSum += npc.DragWeight / Mathf.Max(1, npc.DraggerCount);
        }

        SetDragSpeedFactor(
            Mathf.Clamp(1f - m_dragSlowPerWeight * weightSum, m_minDragSpeedFactor, 1f));
    }

    // 배율과 동기화 값을 함께 갱신 — 서버(또는 오프라인)에서만 호출된다. (NpcController.SetRoped와 같은 관례)
    // 매 프레임 불려도 대역폭을 안 먹는다: NetworkVariable.Value 세터가 같은 값이면 스스로 조기 반환한다.
    private void SetDragSpeedFactor(float factor)
    {
        m_dragSpeedFactor = factor;
        if (IsSpawned && IsServer)
            m_dragSpeedFactorSynced.Value = factor;
    }

    // ---- 놓기 (서버·오프라인 전용) ----

    /// <summary>밧줄 끌기 놓기 — 지정한 NPC 하나만 그 자리에 풀어 체포(Captured) 상태로 세운다(에이전트 복구).
    /// 서버(또는 오프라인) 실행. 나머지 대상은 계속 끌린다.
    /// <b>밧줄은 풀리지 않는다</b> — 줄은 여전히 이 플레이어와 이어져 있고 다시 E로 끌 수 있다.
    /// 실제로 푸는 건 밧줄 좌클릭 채널링(<see cref="PlayerEscortCommands"/>)뿐이다.</summary>
    public void ReleaseDrag(NpcController npc)
    {
        if (IsSpawned && !IsServer)
            return;
        // 파괴된 대상은 건드리지 않는다 — 아래에서 NPC 쪽 상태를 직접 묻는다 (라운드 종료 정리 경로)
        if (npc == null)
            return;
        if (!m_tethered.Contains(npc) || !npc.IsDraggedBy(transform))
            return; // 안 묶었거나 이미 놓은 대상

        // 내 앵커만 뺀다 — 남이 함께 끌고 있으면(줄다리기) 대상은 계속 끌린다.
        bool stillDragged = npc.StopRopeDrag(transform); // 놓은 자리가 NavMesh 밖이면 이 플레이어가 선 자리로 대체 복귀
        SetTetherDragging(npc, false);

        NotifyOwner(
            stillDragged
                ? $"밧줄 끌기 놓기: {npc.name} — 다른 참가자가 계속 끌고 있다 (줄은 그대로)"
                : $"밧줄 끌기 놓기: {npc.name} — 묶인 채 그 자리에 정지 (줄은 그대로)");

        // 아직 아무도 안 끌고 커스터디면 그 자리에서 Captured로 멈춘다(방치 타이머·재확보로 이어짐).
        // 이미 다른 상태로 넘어갔으면(판정 후 수감·넉백·페널티) 그 행선지를 덮어쓰지 않는다. (#230)
        if (!stillDragged && npc.CurrentState == NpcState.Escorted)
            npc.StopEscort();
    }

    /// <summary>끌고 있는 대상 전부를 놓는다 — 디스폰 등 플레이어가 사라지는 경로 전용. 줄은 유지된다.</summary>
    public void ReleaseAllDrags()
    {
        if (IsSpawned && !IsServer)
            return;

        for (int i = m_tethered.Count - 1; i >= 0; i--)
            ReleaseDrag(m_tethered[i]);
    }

    public override void OnNetworkDespawn()
    {
        ReleaseAllDrags();
    }

    // 채널링 게이지와 오너 피드백(NotifyOwner)은 기반 ChanneledInteractionBehaviour가 제공한다. (#184/#91)
}
