using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// PlayerEscorter의 밧줄 묶기·끌기(#269/#369) — 본체와 partial로 분리.
/// 밧줄 <b>1개당 NPC 1명</b>이라 연결은 목록이다 (#390) — 동시 인원의 상한은 소지한 밧줄 개수다.
/// 장력 계산은 끌리는 <see cref="NpcController"/>가 갖고, 여기 남은 것은 "누구를 묶고 있나"의
/// 참조·자원 관리와 서버 권위 진입 판정이다.
/// </summary>
public partial class PlayerEscorter
{
    [Header("밧줄 끌기 (#269)")]
    // 장력 튜닝 값(밧줄 길이·추종 스무딩·흔들림)은 NpcRopeDragConfig로 옮겼다 (#390) —
    // 장력 계산 자체가 끌리는 NPC로 이관되면서 값도 다른 NPC 상태 config들과 같은 자리에 둔다.
    [Tooltip("이 거리(m)를 넘게 멀어지면 밧줄이 끊겨 NPC가 풀려난다 — 벽에 막혀 못 따라오거나 놓아둔 채 걸어가면 발생. 밧줄 길이보다 넉넉해야 한다")]
    [SerializeField] private float m_ropeBreakDistance = 10f;

    // 이 플레이어의 밧줄에 묶여 있는 NPC들 — 서버(또는 오프라인) 진실. 끌기를 멈춰도(E) 남는다 (#369).
    // 놓기는 손에서 줄을 놓는 게 아니라 끌기를 멈추는 것이다: 대상은 묶인 채 그 자리에 서고 줄은 이어져 있다.
    // 실제로 푸는 건 밧줄 좌클릭 채널링(풀기)뿐이고, 그 외에는 인계 판정·방치 탈주·거리 끊김처럼
    // 대상이 커스터디를 벗어날 때 저절로 끊긴다.
    private readonly List<NpcController> m_tethered = new List<NpcController>();

    // 위 목록의 클라이언트 사본 — 서버만 쓴다. 표시(RopeDragView)가 선의 양 끝점을 알아야 하고,
    // 오너 클라의 조기검증(Rope.CanTarget·E 놓기 대상 판정)도 "내가 이걸 묶었나"를 물어야 한다.
    // late-join 클라는 OnListChanged를 못 받으므로 읽는 쪽이 현재 목록을 직접 훑어야 한다 (WantedListManager와 같은 주의).
    // 항목마다 '끌고 있는가'를 함께 싣는다 — 줄다리기로 한 NPC에 여러 명이 걸릴 수 있어
    // NpcController.IsRoped("누구든 끌고 있다")만으로는 내가 놓았는지를 알 수 없다. (#390)
    private readonly NetworkList<RopeTether> m_tetheredSynced = new NetworkList<RopeTether>();

    // 서버 진실의 끌기 여부 — m_tethered와 같은 인덱스. 오프라인(미스폰)에서도 동작해야 해서
    // 동기화 목록에만 의존하지 않는다(NpcController.m_networkState와 같은 이중 구조).
    private readonly List<bool> m_dragging = new List<bool>();

    /// <summary>지금 밧줄에 묶여 있는 인원 수. 전 피어에서 유효. (#390)</summary>
    public int TetheredCount => IsSpawned && !IsServer ? m_tetheredSynced.Count : m_tethered.Count;

    /// <summary>밧줄이 하나라도 묶여 있는가 — 끌고 있지 않아도 참이다. (#369)</summary>
    public bool IsTethered => TetheredCount > 0;

    /// <summary>동시에 묶을 수 있는 상한 — 소지한 밧줄 개수. 로드아웃이 없으면(테스트 구성) 무제한. (#390)</summary>
    private int RopeCapacity => Loadout != null ? Loadout.RopeCount : int.MaxValue;

    /// <summary>소지한 밧줄을 전부 쓰고 있는가 — 새 대상을 묶는 것을 막는 자원 게이트. (#390)</summary>
    public bool IsAtRopeCapacity => TetheredCount >= RopeCapacity;

    /// <summary>
    /// 묶인 대상을 순번으로 얻는다 — 전 피어에서 유효한 표현·검증용 접근자. 없거나 못 찾으면 null. (#390)
    /// 클라이언트는 동기화 참조를 그때그때 푼다.
    /// </summary>
    public NpcController GetTetheredNpc(int index)
    {
        if (!IsSpawned || IsServer)
            return index >= 0 && index < m_tethered.Count ? m_tethered[index] : null;

        if (index < 0 || index >= m_tetheredSynced.Count)
            return null;

        // 세션이 내려가는 중에는 NetworkManager가 이미 사라져 있는데, TryGet은 내부에서 그것을
        // 참조하므로 그대로 부르면 NullReferenceException이 난다. IsSpawned만으로는 이 순간을
        // 거를 수 없다 — 디스폰 통지보다 매니저 소멸이 앞설 수 있어, 라운드 종료 후 씬이 바뀌는
        // 동안 표시(RopeDragView.LateUpdate)가 매 프레임 예외를 뱉는다.
        NetworkManager manager = NetworkManager.Singleton;
        if (manager == null || !manager.IsListening)
            return null;

        return manager.SpawnManager.SpawnedObjects.TryGetValue(
            m_tetheredSynced[index].NpcId, out NetworkObject npcObject)
            && npcObject.TryGetComponent(out NpcController npc)
            ? npc
            : null;
    }

    /// <summary>이 NPC가 <b>내</b> 밧줄에 묶여 있는가 — 전 피어에서 유효. 좌클릭 분기·E 놓기 대상 판정이 쓴다. (#390)</summary>
    public bool IsTetheredTo(NpcController npc) => IndexOfTether(npc) >= 0;

    /// <summary>이 NPC를 <b>내가 지금 끌고</b> 있는가 — 전 피어에서 유효. (#390)
    /// 묶여만 있는(E로 놓아둔) 대상은 false다 — <b>남이 대신 끌고 있어도</b> 그렇다. 줄다리기 중
    /// 내 E가 계속 '놓기'로 소비되지 않게 하려면 이 구분이 필요하다.</summary>
    public bool IsDraggingNpc(NpcController npc)
    {
        int index = IndexOfTether(npc);
        if (index < 0)
            return false;

        return !IsSpawned || IsServer ? m_dragging[index] : m_tetheredSynced[index].Dragging;
    }

    // 이 NPC가 내 목록의 몇 번째인가 — 없으면 -1. 전 피어에서 유효.
    private int IndexOfTether(NpcController npc)
    {
        if (npc == null)
            return -1;
        if (!IsSpawned || IsServer)
            return m_tethered.IndexOf(npc);

        if (npc.NetworkObject == null)
            return -1;

        ulong id = npc.NetworkObject.NetworkObjectId;
        for (int i = 0; i < m_tetheredSynced.Count; i++)
            if (m_tetheredSynced[i].NpcId == id)
                return i;
        return -1;
    }

    /// <summary>밧줄 묶기 시도 — 오너가 호출(Rope 아이템 좌클릭). 서버/오프라인 즉시 실행, 원격은 서버로 요청. (#269)</summary>
    public void RequestRopeDrag(NpcController target)
    {
        if (target == null)
            return;
        if (!IsSpawned || IsServer)
        {
            ServerBeginRopeDrag(target);
            return;
        }
        if (!IsOwner)
            return;
        if (!IsTargetNetworkReady(target))
            return;
        RopeDragRequestRpc(new NetworkObjectReference(target.NetworkObject));
    }

    /// <summary>밧줄 끌기 재개 — 오너가 호출(E, NpcSubdueInteractable). 놓아뒀던 체포 대상을 다시 끈다. (#91 재연행의 자리, #369)</summary>
    public void RequestRopeResume(NpcController target)
    {
        if (target == null)
            return;
        if (!IsSpawned || IsServer)
        {
            ServerResumeRopeDrag(target);
            return;
        }
        if (!IsOwner)
            return;
        if (!IsTargetNetworkReady(target))
            return;
        RopeResumeRequestRpc(new NetworkObjectReference(target.NetworkObject));
    }

    [Rpc(SendTo.Server)]
    private void RopeDragRequestRpc(NetworkObjectReference targetRef)
    {
        if (targetRef.TryGet(out NetworkObject targetObj) &&
            targetObj.TryGetComponent(out NpcController target))
        {
            ServerBeginRopeDrag(target);
        }
    }

    [Rpc(SendTo.Server)]
    private void RopeResumeRequestRpc(NetworkObjectReference targetRef)
    {
        if (targetRef.TryGet(out NetworkObject targetObj) &&
            targetObj.TryGetComponent(out NpcController target))
        {
            ServerResumeRopeDrag(target);
        }
    }

    /// <summary>밧줄 묶기 진입 — 검증 후 채널링을 시작한다. 서버(또는 오프라인) 실행. (#369)</summary>
    private void ServerBeginRopeDrag(NpcController target)
    {
        if (!CanBeginRopeDrag(target))
            return;

        // 남이 끌고 있는 대상에는 밧줄을 덧건다 — 줄다리기 합류 (#390). 기존 끌기는 끊지 않는다(탈취 차단).
        // 이미 커스터디라 반응 판정·기절 지름길이 필요 없어 채널링만 태우고 바로 붙인다.
        if (NpcStateRules.CanJoinDrag(target.CurrentState))
        {
            ServerRopeChannelAsync(target, joining: true).Forget();
            return;
        }

        if (!NpcStateRules.CanArrest(target.CurrentState))
            return; // 이미 신병이 확보됐거나 다른 시스템이 소유한 상태 제외 — 클라 검증·윤곽선과 단일 기준 (#184)

        // 기절 대상은 채널링 없이 즉시 묶는다 — 기절 지속(2.67초)이 채널(3초)보다 짧아 채널을 걸면
        // 묶기 전에 깨어나 테이저→밧줄 콤보가 깨진다. (#269)
        // 상태값이 아니라 IsStunned를 보는 이유: 스턴이 오버레이가 되면서 테이저 기절은 CurrentState를
        // 바꾸지 않는다(넉백 KO만 NpcState.Stunned). 상태로 보면 이 지름길이 조용히 죽어
        // 기절 대상에게도 채널링을 요구하게 되고, 깨어나기 전에 못 묶어 콤보가 깨진다. (#292)
        if (target.IsStunned)
        {
            ServerApplyRopeDrag(target);
            return;
        }

        ServerRopeChannelAsync(target, joining: false).Forget();
    }

    /// <summary>밧줄 끌기 재개 — 이미 체포되어 멈춘 대상을 채널링·반응 판정 없이 즉시 다시 끈다. (#369)
    /// 수갑 시절의 재연행(ServerEscort)이 그랬듯, 이미 확보된 신병에 반응 판정을 다시 굴리면
    /// 잡아 둔 대상이 그 자리에서 도망치게 된다.</summary>
    private void ServerResumeRopeDrag(NpcController target)
    {
        // 이미 내 줄에 묶여 있는(E로 놓아둔) 대상을 다시 끄는 것은 새 밧줄을 쓰지 않는다 —
        // 용량 게이트를 태우면 밧줄을 꽉 채워 놓아둔 순간 아무도 다시 못 끌게 된다. (#390)
        if (m_channel.IsActive)
            return;
        if (!IsInRange(target))
            return;

        if (!IsTetheredTo(target))
        {
            // 남이 묶어 둔 대상은 가져올 수 없다 — 탈취 차단 (#390 규칙 1).
            // 합류(줄다리기)는 상대가 실제로 끌고 있을 때(Escorted) 밧줄 좌클릭으로만 열린다.
            if (FindEscorterOf(target) != null)
                return;
            if (!CanBeginRopeDrag(target))
                return;
        }
        if (!NpcStateRules.CanRelease(target.CurrentState))
            return; // 체포되어 멈춘 대상만

        ServerApplyRopeDrag(target);
    }

    // 새 대상을 묶을 수 있는가 — 자원(밧줄 개수)·중복·사거리. 상태 게이트는 호출부가 각자 건다.
    private bool CanBeginRopeDrag(NpcController target)
    {
        if (m_channel.IsActive)
            return false;
        if (IsTetheredTo(target))
            return false; // 이미 내 줄에 묶여 있다 — 좌클릭은 풀기/재개로 갈린다
        if (IsAtRopeCapacity)
            return false; // 소지한 밧줄 수만큼만 (#390 — 예전의 "한 번에 1명"을 대체)
        return IsInRange(target);
    }

    private async UniTaskVoid ServerRopeChannelAsync(NpcController target, bool joining)
    {
        NotifyOwner(
            joining
                ? $"줄다리기 합류 채널링 시작: {target.name} ({m_channelSeconds}초)"
                : $"밧줄 묶기 채널링 시작: {target.name} ({m_channelSeconds}초)");
        NotifyChannelGaugeStart(m_channelSeconds);

        // 수갑 체포와 동일한 keepAlive — 도중 거리 이탈은 즉시 실패시킨다 (#91)
        ServerChannel.Result result;
        try
        {
            result = await m_channel.RunAsync(
                m_channelSeconds, () => target != null && IsInRange(target));
        }
        finally
        {
            NotifyChannelGaugeEnd(); // 어떤 경로로 끝나도 게이지 숨김 보장 (#184)
        }

        switch (result)
        {
            case ServerChannel.Result.OutOfRange:
                NotifyOwner("묶기 실패 — 대상이 범위를 벗어남");
                return;

            case ServerChannel.Result.Canceled:
                NotifyOwner("묶기 취소됨 (홀드 뗌)");
                return;
        }

        // 채널링 도중 상태·자원이 바뀌었을 수 있다 — 완료 시점에 재확인(다른 플레이어가 먼저 확보,
        // 그 사이 밧줄을 버려 용량이 준 경우, 합류하려던 대상을 그새 놓아버린 경우 등).
        if (target == null || IsAtRopeCapacity)
            return;
        bool stateOk = joining
            ? NpcStateRules.CanJoinDrag(target.CurrentState)
            : NpcStateRules.CanArrest(target.CurrentState);
        if (!stateOk)
            return;

        // 반응 판정은 여기서 굴리지 않는다 (#400) — 밧줄은 순수 검거 수단이 됐고, 판정은
        // NpcController.ServerReactTo가 단독으로 갖는다. 함부로 묶는 것을 막던 장치도 함께 사라졌다 —
        // 이제는 이미 반응 중인 대상이 CanArrest에서 걸리는 것이 그 역할을 대신한다.
        ServerApplyRopeDrag(target);
    }

    // 실제 끌기 진입 — 검증·판정이 끝난 뒤의 상태 조작만 담당한다. 서버(또는 오프라인).
    // 이미 남이 끌고 있는 대상(합류)에도 그대로 쓴다: StartEscort/ExitStun은 같은 상태를 다시 쓰는 것뿐이고,
    // StartRopeDrag는 앵커를 하나 더할 뿐 기존 참가자를 건드리지 않는다.
    private void ServerApplyRopeDrag(NpcController target)
    {
        AddTether(target, dragging: true);

        // 커스터디 상태는 수갑 연행과 같은 Escorted를 재사용한다 — 인계존·이벤트 수명·가로채기 방지가
        // 이미 이 상태를 기준으로 판정하기 때문. 이동은 밧줄 장력이 하고 NpcEscortedState가 IsRoped를 보고
        // 추종을 건너뛴다. 상태 전이가 StartRopeDrag(에이전트 끄기)보다 먼저다 — 뒤집으면 직전 상태 Exit이
        // 꺼진 에이전트에 isStopped를 써 에러가 난다(넉백 ServerApplyKnockback과 같은 순서). (#369)
        target.StartEscort(transform);
        target.StartRopeDrag(transform); // 끈 플레이어를 위협으로 기억 — 풀려나면 이쪽에서 도망친다

        // 기절한 채 묶였으면 오버레이를 걷는다 (#292 — 수갑 체포 성공 분기에 있던 처리를 밧줄로 옮긴 것).
        // 남겨두면 만료 해제 경로(resumeReaction: true)를 타면서 StartFlee가 걸려 묶자마자 도망친다.
        // StartEscort 뒤에 두는 이유: EnterStunned가 Escorted를 만나면 StopEscort로 연행을 끊으므로
        // 순서를 뒤집으면 방금 건 커스터디가 풀린다.
        target.ExitStun(resumeReaction: false);

        NotifyOwner($"밧줄로 묶어 끌기 시작: {target.name} ({TetheredCount}/{RopeCapacity})");
    }

    // ---- 연결 목록 관리 (서버·오프라인 전용) ----

    // 연결을 추가하거나, 이미 있으면 끌기 여부만 갱신한다 (묶기·합류·재개가 전부 여기로 온다).
    private void AddTether(NpcController npc, bool dragging)
    {
        if (npc == null)
            return;

        int index = m_tethered.IndexOf(npc);
        if (index >= 0)
        {
            SetTetherDragging(index, dragging);
            return;
        }

        m_tethered.Add(npc);
        m_dragging.Add(dragging);

        // 스폰된 대상만 동기화 목록에 실을 수 있다 — 아니면 표시 없이 끌기만 진행된다(오프라인 테스트 등)
        if (IsSpawned && IsServer && npc.NetworkObject != null && npc.NetworkObject.IsSpawned)
        {
            m_tetheredSynced.Add(
                new RopeTether { NpcId = npc.NetworkObject.NetworkObjectId, Dragging = dragging });
        }
    }

    // 끌기 여부만 바꾼다 — 값이 그대로면 쓰지 않는다(매 프레임 정리가 불러도 대역폭을 먹지 않게).
    private void SetTetherDragging(int index, bool dragging)
    {
        if (m_dragging[index] == dragging)
            return;

        m_dragging[index] = dragging;

        if (!IsSpawned || !IsServer)
            return;

        int syncedIndex = IndexOfSynced(m_tethered[index]);
        if (syncedIndex < 0)
            return;

        RopeTether entry = m_tetheredSynced[syncedIndex];
        entry.Dragging = dragging;
        m_tetheredSynced[syncedIndex] = entry;
    }

    /// <summary>이 대상과의 연결을 끊는다 — 밧줄 풀기(ServerBeginUnrope) 전용. 서버(또는 오프라인). (#390)</summary>
    private void RemoveTether(NpcController npc)
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
        m_dragging.RemoveAt(index);

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

    // 동기화 목록에서 이 NPC의 항목 위치 — 없거나 id를 못 읽으면 -1. 서버 전용.
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

    /// <summary>
    /// 밧줄 연결 매 프레임 정리 — 본체 Update가 서버(또는 오프라인)에서만 호출한다. (#269/#369)
    /// 장력 계산은 #390에서 <see cref="NpcController"/>로 넘어갔고, 여기 남은 것은
    /// "누구를 묶고 있나"의 참조 관리(커스터디 이탈·거리 끊김)뿐이다.
    /// </summary>
    private void TickTetherCleanup()
    {
        // 지우면서 도니 역순 — 각 연결은 서로 독립이라 하나가 끊겨도 나머지는 유지된다 (#390).
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

            // 너무 멀어지면 줄이 끊겨 풀려나 달아난다 — 벽에 막혀 못 따라오거나(끌기 중) 놓아둔 채 걸어간 경우(#369).
            // 끌던 중이면 먼저 놓아 에이전트를 되살린다(StopRopeDrag) — 도주(Run)가 NavMesh를 쓰기 때문.
            // 방치 탈주(NpcCapturedState.Escape)와 같은 반응: 끌던 플레이어에게서 도주한다.
            if (IsTooFarToTether(npc))
            {
                ReleaseDrag(npc);
                RemoveTetherAt(i);

                // 다른 참가자가 아직 잡고 있으면 도주시키지 않는다 — 내 줄만 끊긴 것이다 (#390 규칙 6).
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
            if (m_dragging[i] && npc.CurrentState != NpcState.Escorted)
                ReleaseDrag(npc);
        }

        AssignDragSlots();
    }

    // 끌고 있는 대상들에 자리 번호를 매긴다 — 전원이 내 발밑 한 점에 앵커되면 밧줄 길이만큼 떨어진
    // 같은 지점으로 수렴해 몸이 겹치고, 벽 스윕이 사람은 장애물로 안 쳐서(#313/#339) 서로 통과해
    // 한 덩어리가 된다. 번호를 받은 NPC가 밧줄 방향에 수직으로 벌려 부채꼴이 된다. (#390)
    // 매 프레임 다시 매기는 이유: 놓기·인계·끊김으로 구성이 수시로 바뀐다. n이 소지 밧줄 수(최대 3)라 비용이 없다.
    private void AssignDragSlots()
    {
        int draggingCount = 0;
        for (int i = 0; i < m_dragging.Count; i++)
            if (m_dragging[i])
                draggingCount++;

        int slot = 0;
        for (int i = 0; i < m_tethered.Count; i++)
        {
            if (!m_dragging[i])
                continue;

            m_tethered[i].SetDragSlot(slot, draggingCount);
            slot++;
        }
    }

    // 끊김 판정 — 수평 거리만 본다(끌기 장력과 같은 기준, 계단·경사에서 y차로 오작동하지 않게).
    private bool IsTooFarToTether(NpcController npc)
    {
        Vector3 delta = npc.transform.position - transform.position;
        delta.y = 0f;
        return delta.sqrMagnitude > m_ropeBreakDistance * m_ropeBreakDistance;
    }

    /// <summary>밧줄 끌기 놓기 — 지정한 NPC를 그 자리에 풀어 체포(Captured) 상태로 세운다(에이전트 복구).
    /// 서버(또는 오프라인) 실행. (#269/#369/#390)
    /// <b>밧줄은 풀리지 않는다</b> — 줄은 여전히 이 플레이어와 이어져 있고, 다시 E로 끌 수 있다.
    /// 실제로 푸는 건 밧줄 좌클릭 채널링(ServerBeginUnrope)뿐이다.
    /// 나머지 대상은 계속 끌린다 — 놓기는 한 명 단위다.</summary>
    public void ReleaseDrag(NpcController npc)
    {
        if (IsSpawned && !IsServer)
            return;

        int index = m_tethered.IndexOf(npc);
        if (index < 0 || !m_dragging[index])
            return; // 안 묶었거나 이미 놓은 대상

        // 내 앵커만 뺀다 — 남이 함께 끌고 있으면(줄다리기) 대상은 계속 끌린다. (#390)
        bool stillDragged = npc.StopRopeDrag(transform); // 놓은 자리가 NavMesh 밖이면 이 플레이어가 선 자리로 대체 복귀
        SetTetherDragging(index, false);

        NotifyOwner(
            stillDragged
                ? $"밧줄 끌기 놓기: {npc.name} — 다른 참가자가 계속 끌고 있다 (줄은 그대로)"
                : $"밧줄 끌기 놓기: {npc.name} — 묶인 채 그 자리에 정지 (줄은 그대로)");

        // 아직 아무도 안 끌고 커스터디면 그 자리에서 Captured로 멈춘다(방치 타이머·재확보로 이어짐).
        // 이미 다른 상태로 넘어갔으면(판정 후 수감·넉백·페널티) 그 행선지를 덮어쓰지 않는다. (#230)
        if (!stillDragged && npc.CurrentState == NpcState.Escorted)
            npc.StopEscort();
    }

    /// <summary>끌고 있는 대상 전부를 놓는다 — 디스폰 등 플레이어가 사라지는 경로 전용. 줄은 유지된다. (#390)</summary>
    public void ReleaseAllDrags()
    {
        if (IsSpawned && !IsServer)
            return;

        for (int i = m_tethered.Count - 1; i >= 0; i--)
            ReleaseDrag(m_tethered[i]);
    }
}
