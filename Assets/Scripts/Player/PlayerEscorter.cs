using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 플레이어의 검거·연행 서버 권위 허브. (#59, #56/#118 네트워크 전환)
/// 오너 클라의 아이템/상호작용(Rope·NpcSubdueInteractable)이 이 컴포넌트의 요청 API를 호출하면,
/// 요청을 서버로 넘겨(ServerRpc) 서버가 채널링·사거리·반응 판정을 실행한다.
/// 그 결과 NpcController 상태 변경은 서버에서 일어나고 NetworkVariable로 전 피어에 동기화된다.
/// 검거는 밧줄로 이관됐다(#369): Rope가 좌클릭에 RequestRopeDrag/RequestUnrope, 뗌에 CancelCapture(채널 취소).
/// 놓기·끌기 재개는 상호작용키(E) — PlayerInteractor가 RequestRelease, NpcSubdueInteractable이 RequestRopeResume.
/// 한 번에 1명만 연행 가능 (동시 1명 제약).
/// 채널링 게이지 피드백(#184)은 공통 기반 ChanneledInteractionBehaviour가 제공한다.
/// </summary>
public partial class PlayerEscorter : ChanneledInteractionBehaviour
{
    [Header("수갑 채널링 (서버 권위)")]
    [Tooltip("체포 채널링 시간(초)")]
    [SerializeField]
    private float m_channelSeconds = 3f;

    [Tooltip(
        "도주 NPC 근접 제압(E 홀드) 채널링 시간(초) — 딸깍 한 번이 아니라 붙어서 홀드를 유지해야 잡힌다 (#332)"
    )]
    [SerializeField]
    private float m_subdueChannelSeconds = 3f;

    // 밧줄 끌기(#269) 관련 필드·상태·로직은 PlayerEscorter.RopeDrag.cs로 분리돼 있다 (partial).

    // 사거리는 조준·윤곽선과 같은 기준을 쓴다 — PlayerInteractor.Range 재사용 (#147 패턴, #184).
    // "윤곽선은 뜨는데 체포가 안 되는" 거리 불일치를 구조적으로 차단한다.
    private const float k_fallbackRange = 3f; // 테스트 구성 등 PlayerInteractor가 없을 때

    private PlayerInteractor m_interactor;

    private PlayerInteractor Interactor
    {
        get
        {
            if (m_interactor == null)
                m_interactor = GetComponent<PlayerInteractor>();
            return m_interactor;
        }
    }

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

    // 밧줄을 들고 있는가 — 새 끌기의 자원 게이트(#269). 로드아웃이 없으면(테스트 구성) 통과.
    private bool HasRope => Loadout == null || Loadout.HasRope;

    private float CaptureRange => Interactor != null ? Interactor.Range : k_fallbackRange;

    // 거리 기준점 — 조준 레이캐스트·윤곽선 게이트와 동일한 AimOrigin(카메라).
    // 루트(발밑) 기준이면 카메라 오프셋만큼 사거리 경계에서 판정이 어긋난다 (#147 관례, #184)
    private Vector3 AimOriginPosition =>
        Interactor != null ? Interactor.AimOrigin.position : transform.position;

    // 밧줄 끌기 상태(DraggingNpc·IsDragging·TetheredNpc·DraggedNpcTransform·RopeLength)는 PlayerEscorter.RopeDrag.cs에 있다. (#269)

    /// <summary>밧줄로 끌기 중 — 새 묶기/풀기를 막는 '한 번에 1명' 게이트. 연행이 밧줄로 이관되며 끌기 하나로 수렴했다. (#269/#369)</summary>
    public bool IsBusy => IsDragging;

    /// <summary>
    /// 해당 NPC를 밧줄로 끌고 있는 플레이어를 찾는다 — 없으면 null. 서버(또는 오프라인)에서만 유효.
    /// 인계 판정(ArrestJudge)이 이 결과로 끌기를 물리적으로 풀기 때문에, 빼면 인계자가 "알 수 없음"이 되고
    /// 끌기가 안 풀린 채(에이전트 꺼진 채) 상태 전이가 일어나 NavMeshAgent 예외가 난다. (#269)
    /// </summary>
    public static PlayerEscorter FindEscorterOf(NpcController npc)
    {
        if (npc == null)
            return null;

        PlayerEscorter[] escorters = FindObjectsByType<PlayerEscorter>(FindObjectsSortMode.None);
        foreach (PlayerEscorter escorter in escorters)
            if (escorter.DraggingNpc == npc)
                return escorter;

        return null;
    }

    /// <summary>
    /// 해당 NPC를 밧줄로 <b>묶어 둔</b> 플레이어를 찾는다(끌고 있지 않아도 포함) — 없으면 null. (#414)
    /// 서버(또는 오프라인)에서만 유효. 인계존에 내려놓고 접수하는 경로에서는 끌기가 이미 풀려 있어
    /// <see cref="FindEscorterOf"/>로는 인계자를 못 찾으므로, 줄이 이어져 있는 것을 근거로 주인을 찾는다.
    /// (끌기 여부를 보는 기존 호출자들의 판정을 바꾸지 않으려고 별도 메서드로 둔다)
    /// </summary>
    public static PlayerEscorter FindTetherOwnerOf(NpcController npc)
    {
        if (npc == null)
            return null;

        PlayerEscorter[] escorters = FindObjectsByType<PlayerEscorter>(FindObjectsSortMode.None);
        foreach (PlayerEscorter escorter in escorters)
            if (escorter.IsTetheredTo(npc)) // 큐 전체를 본다 — 머리만 보면 뒤쪽 대상의 인계자를 놓친다 (#414)
                return escorter;

        return null;
    }

    // 서버 채널링 생명주기(CTS 소유·재진입 가드)는 ServerChannel에 위임 (#109)
    private readonly ServerChannel m_channel = new();

    // 지금 도는 채널링이 '도주 제압 홀드'인지 — E 뗌 취소가 수갑 채널링(좌클릭 홀드)을 오발로 끊지 않게
    // 종류를 구분한다. 서버(또는 오프라인)에서만 유효. (#332)
    private bool m_subdueChanneling;

    // ---- 오너 클라 진입점 (아이템/상호작용이 호출) ----

    // 검거 진입점은 밧줄로 이관됐다 — RequestRopeDrag/RequestUnrope는 PlayerEscorter.RopeDrag.cs에 있다. (#269/#369)

    /// <summary>채널링 취소 — 오너가 호출(이동·뗌 등).</summary>
    public void CancelCapture()
    {
        if (!IsSpawned)
        {
            ServerCancelCapture();
            return;
        }
        if (!IsOwner)
            return;
        CancelCaptureRpc();
    }

    /// <summary>연행 놓기 — 오너가 호출.</summary>
    public void RequestRelease()
    {
        if (!IsSpawned)
        {
            Release();
            return;
        }
        if (!IsOwner)
            return;
        ReleaseRpc();
    }

    /// <summary>
    /// 본부 인계 요청 — 오너가 호출(인계 단말 E). 서버가 대상·구역을 재검증해 판정한다. (#414)
    /// 예전엔 인계존 콜라이더가 자동으로 판정을 냈다 — 트리거가 상호작용키로 옮겨진 진입점이다.
    /// </summary>
    public void RequestDeliver()
    {
        if (!IsSpawned)
        {
            ServerDeliver();
            return;
        }
        if (!IsOwner)
            return;
        DeliverRpc();
    }

    /// <summary>도주 NPC 근접 제압 홀드 시작 — 오너가 호출(E 누름). 3초 홀드를 채워야 잡힌다. (#332)</summary>
    public void RequestSubdueCapture(NpcController target)
    {
        if (target == null)
            return;
        if (!IsSpawned || IsServer)
        {
            ServerBeginSubdue(target);
            return;
        } // 서버/오프라인 즉시 실행
        if (!IsOwner)
            return;
        if (!IsTargetNetworkReady(target))
            return;
        SubdueCaptureRpc(new NetworkObjectReference(target.NetworkObject));
    }

    /// <summary>도주 제압 홀드 취소 — 오너가 호출(E 뗌). 수갑 채널링은 건드리지 않는다(서버가 종류로 가드). (#332)</summary>
    public void RequestCancelSubdue()
    {
        if (!IsSpawned)
        {
            ServerCancelSubdue();
            return;
        }
        if (!IsOwner)
            return;
        CancelSubdueRpc();
    }

    /// <summary>밧줄 풀기 시도 — 오너가 호출(Rope 좌클릭, 대상이 체포 상태일 때). 서버/오프라인 즉시 실행, 원격은 서버로 요청. (#290 → #369)</summary>
    public void RequestUnrope(NpcController target)
    {
        if (target == null)
            return;
        if (!IsSpawned || IsServer)
        {
            ServerBeginUnrope(target);
            return;
        } // 서버/오프라인 즉시 실행
        if (!IsOwner)
            return;
        if (!IsTargetNetworkReady(target))
            return;
        UnropeRequestRpc(new NetworkObjectReference(target.NetworkObject));
    }

    // 원격 클라 → 서버로 대상을 넘기려면 스폰돼 있어야 한다(NetworkObjectReference 제약).
    // 스폰 안 된 NPC(씬 배치 후 미스폰 등)면 참조 생성이 예외를 던지므로 미리 걸러 경고만 남긴다.
    private bool IsTargetNetworkReady(NpcController target)
    {
        if (target.NetworkObject != null && target.NetworkObject.IsSpawned)
            return true;
        Debug.LogWarning(
            $"검거/제압 요청 무시 — 대상 NPC가 네트워크 스폰되지 않음: {target.name}",
            this
        );
        return false;
    }

    // ---- 서버 RPC (오너 → 서버) ----
    // NGO 2.x 유니버설 RPC: 오너가 자기 플레이어 오브젝트에서 서버로 보내므로 소유권 문제 없음

    [Rpc(SendTo.Server)]
    private void CancelCaptureRpc() => ServerCancelCapture();

    [Rpc(SendTo.Server)]
    private void CancelSubdueRpc() => ServerCancelSubdue();

    [Rpc(SendTo.Server)]
    private void ReleaseRpc() => Release();

    [Rpc(SendTo.Server)]
    private void DeliverRpc() => ServerDeliver();

    // 밧줄 끌기 요청 RPC(RopeDragRequestRpc)는 PlayerEscorter.RopeDrag.cs에 있다. (#269)

    [Rpc(SendTo.Server)]
    private void SubdueCaptureRpc(NetworkObjectReference targetRef)
    {
        if (
            targetRef.TryGet(out NetworkObject targetObj)
            && targetObj.TryGetComponent(out NpcController target)
        )
        {
            ServerBeginSubdue(target);
        }
    }

    [Rpc(SendTo.Server)]
    private void UnropeRequestRpc(NetworkObjectReference targetRef)
    {
        if (
            targetRef.TryGet(out NetworkObject targetObj)
            && targetObj.TryGetComponent(out NpcController target)
        )
        {
            ServerBeginUnrope(target);
        }
    }

    // ---- 서버 실행 (권위) ----

    // 좌클릭 체포 채널링(ServerBeginCapture/ServerChannelAsync)은 밧줄 묶기로 대체되어 제거됐다. (#369)
    // 연행(StartEscort)은 밧줄 경로(PlayerEscorter.RopeDrag.cs)가 이어받는다.
    // 반응 판정은 검거에서 완전히 빠졌다 (#400) — 스캔·피격이 트리거이고 NpcController.ServerReactTo가 갖는다.

    private void ServerCancelCapture() => m_channel.Cancel();

    // 인계 실행 — 대상은 클라가 지정하지 않는다. 서버가 자기 권위 상태(TetheredNpc)에서 읽으므로
    // "남이 데려온 NPC를 인계했다"는 위조가 성립할 수 없다. 상태·구역 검증과 판정은 ArrestJudge가 한다 —
    // 연행 허브가 인계존을 알 필요는 없고, 판정 기준이 한 곳(#414)에 모여 있어야 하기 때문이다.
    private void ServerDeliver()
    {
        if (IsSpawned && !IsServer)
            return;

        // 끌기(DraggingNpc)가 아니라 밧줄이 기준이다 — 인계존에 내려놓고 접수하는 경로에서는
        // 끌기가 풀려 있다. 묶여 있는 동안은 끌든 놓든 같은 대상이라 이 하나로 두 경로가 모두 덮인다.
        if (TetheredCount == 0)
            return; // 묶어 둔 대상이 없으면 넘길 것이 없다

        ArrestJudge judge = App.Game.ArrestJudge;
        if (judge == null)
        {
            Debug.LogWarning("PlayerEscorter: ArrestJudge가 없어 인계 판정을 할 수 없다", this);
            return;
        }

        // 밧줄 큐를 앞에서부터 순서대로 판정한다 (#414 팀 확정 — 판정 기준이 NPC가 아니라 플레이어다).
        // 인계존 밖이거나 상태가 맞지 않는 대상은 ArrestJudge가 걸러 내고 큐에 그대로 남는다 —
        // 다시 데려와 E를 누르면 그때 판정된다(재판정 #358과 같은 취급).
        //
        // 큐를 복사해 도는 이유: 판정에 성공한 대상은 Jailed로 넘어가고 그 순간 TickRopeDrag가
        // 큐에서 빼므로, 원본을 그대로 순회하면 도중에 컬렉션이 바뀐다.
        List<NpcController> pending = new List<NpcController>(m_tetherQueue);
        for (int i = 0; i < pending.Count; i++)
            judge.TryDeliver(pending[i]);
    }

    /// <summary>
    /// 진행 중인 체포/제압/해제 채널링을 서버 권위로 즉시 중단한다 — 수갑을 채널링 중 버리는 등
    /// 아이템 소유권 이전 경로에서 서버가 직접 호출한다(Handcuffs.ServerCancelActiveUse).
    /// 오너에 묶인 CancelCapture와 달리 소유권과 무관하므로 데디케이티드 서버에서도 동작한다. 서버(또는 오프라인) 전용.
    /// </summary>
    public void ServerCancelChannel()
    {
        if (IsSpawned && !IsServer)
            return;
        m_channel.Cancel();
    }

    /// <summary>
    /// 도주 NPC 근접 제압 홀드 진입 — 검증 후 채널링 시작. 서버(또는 오프라인) 실행. (#332)
    /// 딸깍 한 번에 잡히던 것을 저항형 연타 제압과 균형을 맞춰 홀드로 바꿨다 — 붙어서
    /// m_subdueChannelSeconds를 채워야 하고, 뗌·사거리 이탈·대상 상태 변화면 무산된다.
    /// </summary>
    private void ServerBeginSubdue(NpcController target)
    {
        if (m_channel.IsActive)
            return; // 체포/해제/제압 채널링 중복 방지 (한 채널 공유)
        if (IsBusy)
            return; // 연행/끌기 중엔 제압 불가
        if (TetheredNpc != null)
            return; // 이미 밧줄로 묶어 둔 대상이 있으면 새로 확보 불가 — 놓아둔(끌기 중 아님) 대상도 포함, 한 번에 1명 (#369)
        if (!HasRope)
            return; // 밧줄 없으면 도주 제압(=체포)도 불가 (#369)
        if (target.CurrentState != NpcState.Run)
            return; // 도주 중일 때만 — 저항은 타격 연타, 배회는 수갑 채널링이 정식 경로
        if (!IsInRange(target))
            return; // 사거리 밖이면 시작조차 안 함

        ServerSubdueChannelAsync(target).Forget();
    }

    private async UniTaskVoid ServerSubdueChannelAsync(NpcController target)
    {
        m_subdueChanneling = true;
        NotifyOwner($"제압 홀드 시작: {target.name} ({m_subdueChannelSeconds}초)");
        NotifyChannelGaugeStart(m_subdueChannelSeconds);

        // 도주 대상은 계속 달아나는 중 — 사거리 유지가 곧 추격이고, 뿌리치거나(상태 변화) 놓치면 무산된다
        ServerChannel.Result result;
        try
        {
            result = await m_channel.RunAsync(
                m_subdueChannelSeconds,
                () => target != null && target.CurrentState == NpcState.Run && IsInRange(target)
            );
        }
        finally
        {
            m_subdueChanneling = false;
            NotifyChannelGaugeEnd(); // 어떤 경로로 끝나도 게이지 숨김 보장 (#184)
        }

        switch (result)
        {
            case ServerChannel.Result.OutOfRange:
                NotifyOwner($"제압 실패 — 대상을 놓침: {(target != null ? target.name : "?")}");
                return;

            case ServerChannel.Result.Canceled:
                NotifyOwner("제압 취소됨 (홀드 뗌)");
                return;
        }

        // 홀드 완주 — 아직 도주 중이면 그 자리에서 체포
        if (target != null && target.CurrentState == NpcState.Run)
            target.CaptureBySubdue();
    }

    // E 뗌 취소 — 도주 제압 홀드만 끊는다. 수갑 체포/해제 채널링(좌클릭 홀드)은 종류가 달라 건드리지 않는다 (#332)
    private void ServerCancelSubdue()
    {
        if (m_subdueChanneling)
            m_channel.Cancel();
    }

    // ---- 밧줄 풀기 채널링 (서버 권위, #290 → #369) ----
    // 묶기 채널링의 역방향 — 밧줄을 든 좌클릭으로 체포되어 멈춘 NPC를 풀어 배회로 돌려보낸다.
    // 묶기와 같은 m_channel·게이지·사거리 판정을 재사용한다(대상 상태가 갈라 주므로 채널 하나면 충분).

    /// <summary>밧줄 풀기 진입 — 체포된 대상만, 중복·연행중·사거리 검증 후 채널링 시작. 서버(또는 오프라인) 실행. (#369)</summary>
    private void ServerBeginUnrope(NpcController target)
    {
        if (m_channel.IsActive)
            return; // 묶기/풀기 채널링 중복 방지 (한 채널 공유)
        if (IsBusy)
            return; // 연행/끌기 중엔 풀기 불가 — 이미 다른 대상을 잡고 있다
        if (!NpcStateRules.CanRelease(target.CurrentState))
            return; // 체포(Captured)만 — 클라 조기검증(Rope.Use)과 단일 기준 (#184/#369)
        if (!HasRope)
            return; // 밧줄을 들고 있어야 풀 수 있다
        if (!IsInRange(target))
            return; // 사거리 밖이면 시작조차 안 함

        ServerUnropeChannelAsync(target).Forget();
    }

    private async UniTaskVoid ServerUnropeChannelAsync(NpcController target)
    {
        NotifyOwner($"밧줄 풀기 채널링 시작: {target.name} ({m_channelSeconds}초)");
        NotifyChannelGaugeStart(m_channelSeconds);

        // 체포 채널링과 동일한 keepAlive — 도중 거리 이탈은 즉시 실패시킨다.
        ServerChannel.Result result;
        try
        {
            result = await m_channel.RunAsync(
                m_channelSeconds,
                () => target != null && IsInRange(target)
            );
        }
        finally
        {
            NotifyChannelGaugeEnd(); // 어떤 경로로 끝나도 게이지 숨김 보장
        }

        if (result != ServerChannel.Result.Completed)
        {
            NotifyOwner("밧줄 풀기 중단 (홀드 뗌 / 거리 이탈)");
            return;
        }

        // 채널링 도중 상태가 바뀌었을 수 있다 — 완료 시점에 재확인(예: 그새 다른 플레이어가 끌기 재개).
        if (!NpcStateRules.CanRelease(target.CurrentState))
            return;

        // 밧줄은 소모되지 않아 대상에 남은 게 없다 — 회수할 자원 없이 배회로 돌려보내기만 한다 (#369).
        NotifyOwner($"밧줄 풀기 완료 — 배회 복귀: {target.name}");
        target.ReleaseFromCustody();
    }

    private bool IsInRange(NpcController target)
    {
        // 사거리 + 가시선 — 거리만 보면 위조 RPC로 벽 너머 제압·검거가 된다 (#360).
        // Interactor 없는 구성(테스트 등)은 종전대로 거리만 본다.
        return (target.transform.position - AimOriginPosition).sqrMagnitude
                <= CaptureRange * CaptureRange
            && (Interactor == null || Interactor.HasLineOfSightTo(target.transform));
    }

    // ---- 오너 로그 피드백 ----

    // 판정 로그는 서버에서 찍히므로 원격 클라 오너는 결과를 볼 수 없다 — 오너 콘솔에도 같은 로그를 전달한다 (#91).
    // 정식 UI 피드백(#65 계열)이 생기면 이 RPC를 그 이벤트 전달 경로로 확장한다.
    private void NotifyOwner(string message)
    {
        Debug.Log(message); // 서버(호스트)·오프라인 콘솔
        if (IsSpawned && IsServer && !IsOwner)
            OwnerLogRpc(message); // 원격 클라가 오너인 경우에만 전달 (호스트 오너는 위에서 이미 찍음)
    }

    [Rpc(SendTo.Owner)]
    private void OwnerLogRpc(string message) => Debug.Log($"[서버 판정] {message}");

    // 채널링 게이지 피드백(NotifyChannelGaugeStart/End)은 기반 ChanneledInteractionBehaviour가 제공한다. (#184)

    // ---- 서버 내부 연행 상태 조작 ----

    /// <summary>놓기(E) — 밧줄로 끌던 NPC를 그 자리에 체포 상태로 세운다(줄은 유지). 서버(또는 오프라인) 실행. (#269/#369)
    /// 좌클릭 수갑 연행이 밧줄로 이관되면서 연행 놓기는 밧줄 끌기 놓기(ReleaseDrag) 하나로 수렴했다.
    /// ArrestJudge가 인계 판정 직후 서버 권위로 호출하기도 한다(끌기 물리 해제).</summary>
    public void Release()
    {
        if (IsSpawned && !IsServer)
            return; // 서버 권위 방어 — 클라 직접 호출은 무시 (요청은 RequestRelease 경유)

        if (DraggingNpc != null)
            ReleaseDrag();
    }

    // 밧줄 끌기 서버 로직(ServerBeginRopeDrag·SetDragging·ServerUpdateDrag·ReleaseDrag·TickRopeDrag)은
    // PlayerEscorter.RopeDrag.cs로 분리돼 있다 (partial). (#269)

    private void Update()
    {
        // 참조 정리는 서버(또는 오프라인)에서만 — 끌기 상태 자체가 서버 권위다 (#56/#118).
        if (IsSpawned && !IsServer)
            return;

        // 대상이 파괴되면(라운드 종료 시 NPC가 씬과 함께 destroy) 아래 끌기 가드가 Unity 가짜 null에 걸려
        // 건너뛴다 — 참조는 사라졌는데 동기화 플래그만 남으면 원격 오너의 IsBusy가 영구 true가 되어
        // E 상호작용이 다음 라운드까지 죽는다(PlayerInteractor가 매 입력을 '놓기'로 소비). 참조가 비면 플래그도 내린다. (#356)
        if (IsSpawned && IsServer && DraggingNpc == null && m_isDraggingSynced.Value)
            SetDragging(null);
        // 밧줄 연결(TetheredNpc)의 같은 정리는 TickRopeDrag가 매 프레임 한다 — 대상 파괴로 참조가
        // 가짜 null이 되는 경우까지 거기서 함께 걸러진다.

        // 밧줄 끌기 매 프레임 처리 — 서버 로직은 partial(RopeDrag)로 위임. (#269)
        TickRopeDrag();
    }

    public override void OnNetworkDespawn()
    {
        ServerCancelCapture();
        ReleaseDrag();
    }

    public override void OnDestroy()
    {
        m_channel.Dispose();
        base.OnDestroy(); // NetworkBehaviour의 파괴 시 네트워크 정리 — 생략하면 정리 로직이 통째로 건너뛰어진다
    }
}
