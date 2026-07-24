using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 플레이어의 검거·연행 서버 권위 허브. (#59, #56/#118 네트워크 전환)
/// 오너 클라의 아이템/상호작용(Handcuffs·NpcSubdueInteractable)이 이 컴포넌트의 요청 API를 호출하면,
/// 요청을 서버로 넘겨(ServerRpc) 서버가 채널링·사거리·반응 판정을 실행한다.
/// 그 결과 NpcController 상태 변경은 서버에서 일어나고 NetworkVariable로 전 피어에 동기화된다.
/// 체포 채널링: Handcuffs가 좌클릭 누름에 RequestCapture, 뗌에 CancelCapture를 요청 (#91).
/// 놓기·재연행은 상호작용키(E) — PlayerInteractor가 RequestRelease, NpcSubdueInteractable이 RequestEscort (#91).
/// 한 번에 1명만 연행 가능 (동시 1명 제약).
/// </summary>
public class PlayerEscorter : NetworkBehaviour
{
    [Header("수갑 채널링 (서버 권위)")]
    [Tooltip("체포 채널링 시간(초)")]
    [SerializeField] private float m_channelSeconds = 3f;

    [Tooltip("도주 NPC 근접 제압(E 홀드) 채널링 시간(초) — 딸깍 한 번이 아니라 붙어서 홀드를 유지해야 잡힌다 (#332)")]
    [SerializeField] private float m_subdueChannelSeconds = 3f;

    [Header("밧줄 끌기 (#269)")]
    [Tooltip("밧줄 길이(m) — 이 거리를 넘어야 NPC가 끌려온다. 안쪽이면 밧줄이 늘어져 당기지 않는다")]
    [SerializeField] private float m_ropeLength = 1.6f;

    [Tooltip("끌리는 몸이 목표 위치를 따라잡는 데 걸리는 시간(초) — 클수록 늦게, 크게 휘며 따라온다")]
    [SerializeField] private float m_dragSmoothTime = 0.14f;

    [Tooltip("몸이 밧줄 방향으로 도는 민감도(1/초) — 클수록 즉각 방향을 맞춘다")]
    [SerializeField] private float m_dragTurnSharpness = 6f;

    [Tooltip("끌리며 좌우로 흔들리는 최대 각(도) — 0이면 흔들리지 않는다")]
    [SerializeField] private float m_dragSwayAngle = 7f;

    [Tooltip("흔들림 주기 — 끌린 거리 1m당 위상(라디안)")]
    [SerializeField] private float m_dragSwayFrequency = 1.6f;

    // 사거리는 조준·윤곽선과 같은 기준을 쓴다 — PlayerInteractor.Range 재사용 (#147 패턴, #184).
    // "윤곽선은 뜨는데 체포가 안 되는" 거리 불일치를 구조적으로 차단한다.
    private const float k_fallbackRange = 3f; // 테스트 구성 등 PlayerInteractor가 없을 때

    private PlayerInteractor m_interactor;

    private PlayerInteractor Interactor
    {
        get
        {
            if (m_interactor == null) m_interactor = GetComponent<PlayerInteractor>();
            return m_interactor;
        }
    }

    // 수갑 자원 게이트·소모용 로드아웃 (#229). 테스트 구성 등 없을 수 있어 null 허용.
    private PlayerLoadout m_loadout;

    private PlayerLoadout Loadout
    {
        get
        {
            if (m_loadout == null) m_loadout = GetComponent<PlayerLoadout>();
            return m_loadout;
        }
    }

    // 수갑을 들고 있는가 — 새 체포의 자원 게이트(#229). 로드아웃이 없으면(테스트 구성) 통과시킨다.
    private bool HasHandcuffs => Loadout == null || Loadout.HasHandcuffs;

    // 밧줄을 들고 있는가 — 새 끌기의 자원 게이트(#269). 로드아웃이 없으면(테스트 구성) 통과.
    private bool HasRope => Loadout == null || Loadout.HasRope;

    private float CaptureRange => Interactor != null ? Interactor.Range : k_fallbackRange;

    // 거리 기준점 — 조준 레이캐스트·윤곽선 게이트와 동일한 AimOrigin(카메라).
    // 루트(발밑) 기준이면 카메라 오프셋만큼 사거리 경계에서 판정이 어긋난다 (#147 관례, #184)
    private Vector3 AimOriginPosition =>
        Interactor != null ? Interactor.AimOrigin.position : transform.position;

    /// <summary>지금 연행 중인 NPC. 없으면 null. 서버(또는 오프라인)에서만 유효.</summary>
    public NpcController EscortingNpc { get; private set; }

    // 연행 여부를 클라이언트에도 알리는 동기화 플래그 — 서버만 기록한다.
    // 오너 클라의 Handcuffs가 "놓기/체포" 분기를 하려면 자기가 연행 중인지 알아야 하는데,
    // EscortingNpc는 서버에서만 세팅되므로 이 플래그가 없으면 클라에서 놓기가 안 된다 (#118 리뷰).
    private readonly NetworkVariable<bool> m_isEscortingSynced = new(false);

    /// <summary>연행 중 여부. 서버·오프라인은 실제 참조로, 원격 피어는 동기화 플래그로 판정.</summary>
    public bool IsEscorting => IsSpawned && !IsServer ? m_isEscortingSynced.Value : EscortingNpc != null;

    /// <summary>지금 밧줄로 끌고 있는 NPC. 없으면 null. 서버(또는 오프라인)에서만 유효. (#269)</summary>
    public NpcController DraggingNpc { get; private set; }

    // 끌리는 대상을 클라이언트에도 알린다 — 서버만 기록한다(연행 플래그와 동일 관례).
    // 단순 bool이 아니라 대상 참조인 이유: 원격 피어의 밧줄 표시(RopeDragView)가 선의 양 끝점을
    // 알아야 하는데, DraggingNpc는 서버에서만 세팅되므로 누구를 끄는지 알 방법이 없다.
    private readonly NetworkVariable<NetworkObjectReference> m_draggedNpcSynced = new();

    /// <summary>밧줄 끌기 중 여부. 서버·오프라인은 실제 참조로, 원격 피어는 동기화 참조로 판정. (#269)</summary>
    public bool IsDragging =>
        IsSpawned && !IsServer ? m_draggedNpcSynced.Value.NetworkObjectId != 0 : DraggingNpc != null;

    /// <summary>끌리는 NPC의 트랜스폼 — 전 피어에서 유효한 표현 계층용 접근자. 없으면 null. (#269)</summary>
    public Transform DraggedNpcTransform
    {
        get
        {
            if (DraggingNpc != null) return DraggingNpc.transform;
            if (!IsSpawned) return null;
            return m_draggedNpcSynced.Value.TryGet(out NetworkObject npcObject) ? npcObject.transform : null;
        }
    }

    /// <summary>밧줄 길이(m) — 표시(늘어짐 정도)와 서버 장력 판정이 같은 값을 쓴다. (#269)</summary>
    public float RopeLength => m_ropeLength;

    /// <summary>연행 중이거나 밧줄로 끌기 중 — 새 검거/연행/끌기를 막는 '한 번에 1명' 통합 게이트. (#269)</summary>
    public bool IsBusy => IsEscorting || IsDragging;

    /// <summary>
    /// 해당 NPC를 연행 중이거나 밧줄로 끌고 있는 플레이어를 찾는다 — 없으면 null.
    /// EscortingNpc·DraggingNpc가 서버 권위 참조이므로 서버(또는 오프라인)에서만 유효하다.
    /// 끌기도 함께 보는 이유: 인계 판정(ArrestJudge)이 이 결과로 연행을 물리적으로 풀기 때문에,
    /// 밧줄 경로를 빼면 인계자가 "알 수 없음"이 되고 끌기가 안 풀린 채(에이전트 꺼진 채) 상태 전이가
    /// 일어나 NavMeshAgent 예외가 난다. (#269)
    /// </summary>
    public static PlayerEscorter FindEscorterOf(NpcController npc)
    {
        if (npc == null)
            return null;

        PlayerEscorter[] escorters = FindObjectsByType<PlayerEscorter>(FindObjectsSortMode.None);
        foreach (PlayerEscorter escorter in escorters)
            if (escorter.EscortingNpc == npc || escorter.DraggingNpc == npc)
                return escorter;

        return null;
    }

    // 서버 채널링 생명주기(CTS 소유·재진입 가드)는 ServerChannel에 위임 (#109)
    private readonly ServerChannel m_channel = new();

    // 지금 도는 채널링이 '도주 제압 홀드'인지 — E 뗌 취소가 수갑 채널링(좌클릭 홀드)을 오발로 끊지 않게
    // 종류를 구분한다. 서버(또는 오프라인)에서만 유효. (#332)
    private bool m_subdueChanneling;

    // ---- 오너 클라 진입점 (아이템/상호작용이 호출) ----

    /// <summary>체포 시도 — 오너가 호출. 서버/오프라인은 즉시 실행, 원격 클라는 서버로 요청을 넘긴다.</summary>
    public void RequestCapture(NpcController target)
    {
        if (target == null)
            return;

        // 서버(호스트 포함)·오프라인은 로컬 NpcController 참조로 바로 실행 — 네트워크 직렬화 불필요.
        // (호스트는 이미 대상을 들고 있어 RPC가 불필요하고, NetworkObjectReference는 스폰된 대상에서만
        //  생성 가능해 스폰 안 된 NPC를 넘기면 예외가 난다 — 이 우회가 호스트 검거 회귀를 막는다, #118)
        if (!IsSpawned || IsServer)
        {
            ServerBeginCapture(target);
            return;
        }
        if (!IsOwner)
            return; // 남의 플레이어 오브젝트에서 온 호출 방지

        if (!IsTargetNetworkReady(target))
            return;
        CaptureRequestRpc(new NetworkObjectReference(target.NetworkObject));
    }

    /// <summary>밧줄 끌기 시도 — 오너가 호출(Rope 아이템). 서버/오프라인 즉시 실행, 원격은 서버로 요청. (#269)</summary>
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

    /// <summary>채널링 취소 — 오너가 호출(이동·뗌 등).</summary>
    public void CancelCapture()
    {
        if (!IsSpawned) { ServerCancelCapture(); return; }
        if (!IsOwner) return;
        CancelCaptureRpc();
    }

    /// <summary>연행 놓기 — 오너가 호출.</summary>
    public void RequestRelease()
    {
        if (!IsSpawned) { Release(); return; }
        if (!IsOwner) return;
        ReleaseRpc();
    }

    /// <summary>도주 NPC 근접 제압 홀드 시작 — 오너가 호출(E 누름). 3초 홀드를 채워야 잡힌다. (#332)</summary>
    public void RequestSubdueCapture(NpcController target)
    {
        if (target == null) return;
        if (!IsSpawned || IsServer) { ServerBeginSubdue(target); return; } // 서버/오프라인 즉시 실행
        if (!IsOwner) return;
        if (!IsTargetNetworkReady(target)) return;
        SubdueCaptureRpc(new NetworkObjectReference(target.NetworkObject));
    }

    /// <summary>도주 제압 홀드 취소 — 오너가 호출(E 뗌). 수갑 채널링은 건드리지 않는다(서버가 종류로 가드). (#332)</summary>
    public void RequestCancelSubdue()
    {
        if (!IsSpawned) { ServerCancelSubdue(); return; }
        if (!IsOwner) return;
        CancelSubdueRpc();
    }

    /// <summary>체포되어 멈춘 NPC 재연행 — 오너가 호출(E, NpcSubdueInteractable). (#91)</summary>
    public void RequestEscort(NpcController target)
    {
        if (target == null) return;
        if (!IsSpawned || IsServer) { ServerEscort(target); return; } // 서버/오프라인 즉시 실행
        if (!IsOwner) return;
        if (!IsTargetNetworkReady(target)) return;
        EscortRequestRpc(new NetworkObjectReference(target.NetworkObject));
    }

    /// <summary>수갑 해제 시도 — 빈손 오너가 호출(빈손 좌클릭, PlayerItemUser). 서버/오프라인 즉시 실행, 원격은 서버로 요청. (#290)</summary>
    public void RequestUncuff(NpcController target)
    {
        if (target == null) return;
        if (!IsSpawned || IsServer) { ServerBeginUncuff(target); return; } // 서버/오프라인 즉시 실행
        if (!IsOwner) return;
        if (!IsTargetNetworkReady(target)) return;
        UncuffRequestRpc(new NetworkObjectReference(target.NetworkObject));
    }

    /// <summary>수갑 해제 채널링 취소 — 오너가 호출(빈손 좌클릭 뗌). 체포와 같은 채널(m_channel)을 공유하므로 CancelCapture로 위임한다. (#290)</summary>
    public void CancelUncuff() => CancelCapture();

    // 원격 클라 → 서버로 대상을 넘기려면 스폰돼 있어야 한다(NetworkObjectReference 제약).
    // 스폰 안 된 NPC(씬 배치 후 미스폰 등)면 참조 생성이 예외를 던지므로 미리 걸러 경고만 남긴다.
    private bool IsTargetNetworkReady(NpcController target)
    {
        if (target.NetworkObject != null && target.NetworkObject.IsSpawned)
            return true;
        Debug.LogWarning($"검거/제압 요청 무시 — 대상 NPC가 네트워크 스폰되지 않음: {target.name}", this);
        return false;
    }

    // ---- 서버 RPC (오너 → 서버) ----
    // NGO 2.x 유니버설 RPC: 오너가 자기 플레이어 오브젝트에서 서버로 보내므로 소유권 문제 없음

    [Rpc(SendTo.Server)]
    private void CaptureRequestRpc(NetworkObjectReference targetRef)
    {
        if (targetRef.TryGet(out NetworkObject targetObj) &&
            targetObj.TryGetComponent(out NpcController target))
        {
            ServerBeginCapture(target);
        }
    }

    [Rpc(SendTo.Server)]
    private void CancelCaptureRpc() => ServerCancelCapture();

    [Rpc(SendTo.Server)]
    private void CancelSubdueRpc() => ServerCancelSubdue();

    [Rpc(SendTo.Server)]
    private void ReleaseRpc() => Release();

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
    private void SubdueCaptureRpc(NetworkObjectReference targetRef)
    {
        if (targetRef.TryGet(out NetworkObject targetObj) &&
            targetObj.TryGetComponent(out NpcController target))
        {
            ServerBeginSubdue(target);
        }
    }

    [Rpc(SendTo.Server)]
    private void EscortRequestRpc(NetworkObjectReference targetRef)
    {
        if (targetRef.TryGet(out NetworkObject targetObj) &&
            targetObj.TryGetComponent(out NpcController target))
        {
            ServerEscort(target);
        }
    }

    [Rpc(SendTo.Server)]
    private void UncuffRequestRpc(NetworkObjectReference targetRef)
    {
        if (targetRef.TryGet(out NetworkObject targetObj) &&
            targetObj.TryGetComponent(out NpcController target))
        {
            ServerBeginUncuff(target);
        }
    }

    // ---- 서버 실행 (권위) ----

    /// <summary>체포 진입 — 대상 검증 후 채널링 시작. 서버(또는 오프라인)에서만 실행.</summary>
    private void ServerBeginCapture(NpcController target)
    {
        if (m_channel.IsActive)
            return; // 중복 채널링 방지
        if (IsBusy)
            return; // 연행/끌기 중엔 체포 불가 — 놓기는 상호작용키(E)의 RequestRelease 전용 (#91/#269)
        if (!HasHandcuffs)
            return; // 수갑 없으면 체포 시도 불가 — 연행 중 소모돼 사라진 상태 포함 (#229)
        if (!NpcStateRules.IsCapturable(target.CurrentState))
            return; // 연행 중(가로채기 방지 #59)·체포됨(재연행은 E 경로 #91) — 클라 검증·윤곽선과 단일 기준 (#184)
        if (!IsInRange(target))
            return; // 사거리 밖이면 시작조차 안 함

        ServerChannelAsync(target).Forget();
    }

    private async UniTaskVoid ServerChannelAsync(NpcController target)
    {
        NotifyOwner($"구속 채널링 시작: {target.name} ({m_channelSeconds}초)");
        NotifyChannelGaugeStart(m_channelSeconds);

        // 프레임 루프 기반 keepAlive — 채널링 도중 거리 이탈을 즉시 실패시킨다 (#91, 도주형 NPC 대응 GDD 6장)
        ServerChannel.Result result;
        try
        {
            result = await m_channel.RunAsync(
                m_channelSeconds, () => target != null && IsInRange(target));
        }
        finally
        {
            // 완료·뗌·거리이탈·예외 어떤 경로로 끝나도 게이지 숨김을 보장한다 (#184)
            NotifyChannelGaugeEnd();
        }

        switch (result)
        {
            case ServerChannel.Result.OutOfRange:
                NotifyOwner("구속 실패 — 대상이 범위를 벗어남");
                return;

            case ServerChannel.Result.Canceled:
                NotifyOwner("구속 취소됨 (홀드 뗌)");
                return;

            case ServerChannel.Result.Completed:
                break; // 아래 반응 판정으로 진행
        }

        // 채널링 성공 순간 반응 판정 (GDD 6-1, #76)
        ReactionType reaction = ResolveReaction(target);
        switch (reaction)
        {
            case ReactionType.Flee:
                // 뿌리치고 도주 — 근접 제압 홀드 또는 테이저(후속)로만 잡힌다
                NotifyOwner($"체포 실패 — 뿌리치고 도주: {target.name}");
                target.StartFlee(transform); // 이 플레이어(서버측 transform)로부터 도주
                break;

            case ReactionType.Resist:
                // 그 자리에서 저항 — 제압 게이지를 깎아야 체포된다
                NotifyOwner($"체포 실패 — 저항 시작: {target.name}");
                target.StartResist(transform); // 이 플레이어(서버측 transform)를 위협으로 — 제압 실패 시 여기서 도주 (#205)
                break;

            default:
                // 체포 성공 → 이 플레이어를 따라 연행 (#59)
                NotifyOwner($"NPC 구속됨: {target.name}");
                StartEscort(target);
                break;
        }
    }

    private void ServerCancelCapture() => m_channel.Cancel();

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
        if (!HasHandcuffs)
            return; // 수갑 없으면 도주 제압(=체포)도 불가 (#229)
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
                () => target != null && target.CurrentState == NpcState.Run && IsInRange(target));
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

    /// <summary>재연행 — 서버 실행. 체포되어 멈춘 대상만 연행 시작(동시 1명 가드는 StartEscort). (#91)</summary>
    private void ServerEscort(NpcController target)
    {
        if (target.CurrentState == NpcState.Captured)
            StartEscort(target);
    }

    // ---- 수갑 해제 채널링 (서버 권위, #290) ----
    // 체포 채널링(ServerBeginCapture)의 역방향 — 빈손 좌클릭으로 체포된 NPC의 수갑을 풀어 회수한다.
    // 체포와 같은 m_channel·게이지·사거리 판정을 재사용한다(빈손↔수갑 든 상태는 상호배타라 채널 하나면 충분).

    /// <summary>수갑 해제 진입 — 체포된 대상만, 중복·연행중·사거리 검증 후 채널링 시작. 서버(또는 오프라인) 실행. (#290)</summary>
    private void ServerBeginUncuff(NpcController target)
    {
        if (m_channel.IsActive)
            return; // 체포/해제 채널링 중복 방지 (한 채널 공유)
        if (IsBusy)
            return; // 연행/끌기 중엔 해제 불가 — 빈손 상태가 아니다
        if (!NpcStateRules.IsUncuffable(target.CurrentState))
            return; // 체포(Captured)만 — 클라 조기검증(PlayerItemUser)과 단일 기준 (#184/#290)
        if (!target.HasHandcuffs)
            return; // 실제로 수갑이 채워진 NPC만 해제 대상 — cuffless Captured(제압만)는 타이머로 탈출 (#290)
        if (!IsInRange(target))
            return; // 사거리 밖이면 시작조차 안 함

        ServerUncuffChannelAsync(target).Forget();
    }

    private async UniTaskVoid ServerUncuffChannelAsync(NpcController target)
    {
        NotifyOwner($"수갑 해제 채널링 시작: {target.name} ({m_channelSeconds}초)");
        NotifyChannelGaugeStart(m_channelSeconds);

        // 체포 채널링과 동일한 keepAlive — 도중 거리 이탈은 즉시 실패시킨다.
        ServerChannel.Result result;
        try
        {
            result = await m_channel.RunAsync(
                m_channelSeconds, () => target != null && IsInRange(target));
        }
        finally
        {
            NotifyChannelGaugeEnd(); // 어떤 경로로 끝나도 게이지 숨김 보장
        }

        if (result != ServerChannel.Result.Completed)
        {
            NotifyOwner("수갑 해제 중단 (홀드 뗌 / 거리 이탈)");
            return;
        }

        // 채널링 도중 상태가 바뀌었을 수 있다 — 완료 시점에 재확인(예: 그새 다른 플레이어가 재연행).
        if (!NpcStateRules.IsUncuffable(target.CurrentState))
            return;

        // 채운 수갑을 이 플레이어 인벤토리로 회수하고(꽉 차면 발밑 반환) 배회로 복귀시킨다 — 판정 funnel 밖의 회수 경로 (#290/#307).
        NotifyOwner($"수갑 해제 완료 — 수갑 회수 후 배회 복귀: {target.name}");
        if (Loadout == null || !Loadout.TryRecoverHandcuffs(target))
            target.DropHandcuffs();
        target.ReleaseFromCustody();
    }

    private bool IsInRange(NpcController target)
    {
        return (target.transform.position - AimOriginPosition).sqrMagnitude
            <= CaptureRange * CaptureRange;
    }

    /// <summary>채널링 성공 순간의 반응. 기절 중이거나 신원이 없으면 순응(즉시 연행) 취급. (#76)</summary>
    private static ReactionType ResolveReaction(NpcController target)
    {
        if (target.CurrentState == NpcState.Stunned)
            return ReactionType.Compliant;

        CitizenIdentity identity = target.GetComponent<CitizenIdentity>();
        return identity != null ? identity.Reaction : ReactionType.Compliant;
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

    // ---- 채널링 게이지 피드백 (#184) ----
    // NotifyOwner와 동일 분기 — 호스트 오너·오프라인은 직접 호출, 원격 오너에게만 RPC.
    // 데디케이티드 서버 등 HUD가 없는 환경에선 Instance가 null이라 무동작(안전).

    private void NotifyChannelGaugeStart(float seconds)
    {
        if (IsSpawned && IsServer && !IsOwner)
        {
            ChannelGaugeStartRpc(seconds);
            return;
        }
        App.UI.Gauge?.Show(seconds);
    }

    private void NotifyChannelGaugeEnd()
    {
        if (IsSpawned && IsServer && !IsOwner)
        {
            ChannelGaugeEndRpc();
            return;
        }
        App.UI.Gauge?.Hide();
    }

    [Rpc(SendTo.Owner)]
    private void ChannelGaugeStartRpc(float seconds) => App.UI.Gauge?.Show(seconds);

    [Rpc(SendTo.Owner)]
    private void ChannelGaugeEndRpc() => App.UI.Gauge?.Hide();

    // ---- 서버 내부 연행 상태 조작 ----

    /// <summary>연행 시작. 이미 다른 NPC를 연행 중이면 무시된다 (동시 1명 제약). 서버(또는 오프라인) 실행.</summary>
    public void StartEscort(NpcController npc)
    {
        if (IsSpawned && !IsServer)
            return; // 연행 상태는 서버 권위 — NpcController 상태 메서드와 동일한 방어 컨벤션 (#118 리뷰)
        if (IsBusy || npc == null)
            return;

        // 연행은 반드시 NPC에 수갑이 채워져야 성립한다 — 소모 관문을 여기 하나로 둔다 (#290 모델B).
        // 제압(도주/저항)은 여러 명·제압봉으로 가능해 '누구 수갑을 채우나'가 모호하므로 소모하지 않고,
        // 소유자가 명확한 연행 시점에 첫 연행자의 수갑을 NPC로 옮긴다 (판정 후 반환까지 NPC가 들고 감, #229).
        // 재연행(놓았다 다시 잡기)·이미 수갑 찬 NPC면 또 소모하지 않는다.
        if (!npc.HasHandcuffs)
        {
            // 플레이어도 NPC도 수갑이 없으면 연행 불가 — '수갑 없이 연행' 구멍을 막는다 (#290).
            if (!HasHandcuffs)
            {
                NotifyOwner($"수갑 없음 — 연행 불가: {npc.name}");
                return;
            }
            Loadout?.ConsumeHandcuffsTo(npc.transform);
        }

        SetEscorting(npc);
        npc.StartEscort(transform);
        NotifyOwner($"연행 시작: {npc.name}");
    }

    /// <summary>연행 놓기 — NPC는 그 자리에서 체포 상태로 멈춘다. 다시 다가가 재연행 가능. 서버(또는 오프라인) 실행.</summary>
    public void Release()
    {
        if (IsSpawned && !IsServer)
            return; // 서버 권위 방어 — 클라 직접 호출은 무시 (요청은 RequestRelease 경유)

        // 밧줄 끌기 중이면 끌기 놓기로 처리 (E 키 공유) (#269)
        if (DraggingNpc != null)
        {
            ReleaseDrag();
            return;
        }

        if (!IsEscorting)
            return;

        NotifyOwner($"연행 놓기: {EscortingNpc.name} — 그 자리에서 체포 상태로 정지");
        EscortingNpc.StopEscort();
        SetEscorting(null);
    }

    // EscortingNpc와 동기화 플래그를 함께 갱신 — 서버(또는 오프라인)에서만 호출된다
    private void SetEscorting(NpcController npc)
    {
        EscortingNpc = npc;
        if (IsSpawned && IsServer)
            m_isEscortingSynced.Value = npc != null;
    }

    // ---- 밧줄 끌기 (서버 권위, #269) ----

    /// <summary>밧줄 끌기 진입 — 기절한 대상만, 사거리·중복 검증 후 시작. 서버(또는 오프라인) 실행.</summary>
    private void ServerBeginRopeDrag(NpcController target)
    {
        if (m_channel.IsActive)
            return; // 체포 채널링 중엔 시작 안 함
        if (IsBusy)
            return; // 연행/끌기 중엔 새 끌기 불가 (한 번에 1명)
        if (!NpcStateRules.IsRopeable(target.CurrentState))
            return; // 기절한 대상만 — 클라 검증·윤곽선과 단일 기준 (#184)
        if (!HasRope)
            return; // 밧줄을 들고 있어야 끌기 시작 가능 (#269)
        if (!IsInRange(target))
            return;

        SetDragging(target);
        target.StartRopeDrag(transform); // 끈 플레이어를 위협으로 기억 — 놓아준 뒤 깨어나면 이쪽에서 도망친다
        NotifyOwner($"밧줄로 묶어 끌기 시작: {target.name}");
    }

    // DraggingNpc와 동기화 플래그를 함께 갱신 — 서버(또는 오프라인)에서만 호출된다(SetEscorting과 동일 관례).
    private void SetDragging(NpcController npc)
    {
        DraggingNpc = npc;

        if (npc != null)
        {
            // 새 끌기의 추종 상태를 초기화한다 — 이전 끌기의 관성·위상이 남으면 첫 프레임에 튄다
            m_dragVelocity = Vector3.zero;
            m_dragFacing = npc.transform.rotation;
            m_dragTravel = 0f;
        }

        if (IsSpawned && IsServer)
        {
            // 스폰된 대상만 참조로 넘길 수 있다(NetworkObjectReference 제약) — 아니면 표시 없이 끌기만 진행된다
            bool syncable = npc != null && npc.NetworkObject != null && npc.NetworkObject.IsSpawned;
            m_draggedNpcSynced.Value = syncable ? new NetworkObjectReference(npc.NetworkObject) : default;
        }
    }

    // 끌기 추종 상태 (서버·오프라인 전용) — 매 프레임 이어지는 값이라 SetDragging에서 초기화한다.
    private Vector3 m_dragVelocity;   // SmoothDamp 관성
    private Quaternion m_dragFacing;  // 흔들림을 뺀 몸 방향 — 여기에 sway를 얹어 최종 회전을 만든다
    private float m_dragTravel;       // 끌린 누적 거리(m) — 흔들림 위상의 기준

    /// <summary>
    /// 끌리는 NPC를 밧줄 장력으로 끌어당긴다 — 서버(또는 오프라인) 매 프레임. (#269)
    /// 뒤 고정점에 강체로 붙이지 않는다: (1) 밧줄 길이를 넘을 때만 당기고 (2) 늦게 따라오게 해서
    /// 코너를 돌면 몸이 바깥으로 끌려나오는 궤적이 생긴다.
    /// </summary>
    private void ServerUpdateDrag()
    {
        Vector3 anchor = transform.position;
        Vector3 npcPosition = DraggingNpc.transform.position;

        Vector3 toNpc = npcPosition - anchor;
        toNpc.y = 0f;
        float distance = toNpc.magnitude;

        // 밧줄이 늘어져 있으면(길이 안쪽) 당기지 않는다 — 제자리에서 돌기만 하면 NPC는 가만히 있다
        Vector3 target = npcPosition;
        if (distance > m_ropeLength)
            target = anchor + toNpc / distance * m_ropeLength;

        // 바닥 높이는 끄는 플레이어 기준을 그대로 쓴다 — 경사·계단 지면 스냅은 후속 (기존 동작 유지)
        target.y = anchor.y;

        Vector3 next = Vector3.SmoothDamp(npcPosition, target, ref m_dragVelocity, m_dragSmoothTime);

        // 몸 방향은 플레이어 회전이 아니라 밧줄 방향 — 제자리에서 마우스만 돌려도 NPC가 같이 돌지 않는다
        Vector3 ropeDirection = anchor - next;
        ropeDirection.y = 0f;
        if (ropeDirection.sqrMagnitude > 0.0001f)
        {
            Quaternion facing = Quaternion.LookRotation(ropeDirection);
            m_dragFacing = Quaternion.Slerp(
                m_dragFacing, facing, 1f - Mathf.Exp(-m_dragTurnSharpness * Time.deltaTime));
        }

        // 끌린 거리에 비례해 좌우로 흔들린다 — 시간이 아니라 거리 기준이라 멈추면 흔들림도 멈춘다
        m_dragTravel += (next - npcPosition).magnitude;
        float sway = Mathf.Sin(m_dragTravel * m_dragSwayFrequency) * m_dragSwayAngle;

        DraggingNpc.ServerDragTo(next, m_dragFacing * Quaternion.Euler(0f, sway, 0f));
    }

    /// <summary>밧줄 끌기 놓기 — NPC를 그 자리에 풀어 기절 상태를 잇게 한다(에이전트 복구). 서버(또는 오프라인) 실행. (#269)</summary>
    public void ReleaseDrag()
    {
        if (IsSpawned && !IsServer)
            return;
        if (DraggingNpc == null)
            return;

        NotifyOwner($"밧줄 끌기 놓기: {DraggingNpc.name}");
        DraggingNpc.StopRopeDrag();
        SetDragging(null);
    }

    private void Update()
    {
        // 참조 정리는 서버(또는 오프라인)에서만 — 연행 상태 자체가 서버 권위다 (#56/#118).
        // 클라이언트에서는 EscortingNpc가 서버 로직으로만 세팅되므로 여기서 건드리지 않는다.
        if (IsSpawned && !IsServer)
            return;

        // 거리 이탈 등으로 NPC 쪽에서 연행이 스스로 풀린 경우 참조를 정리한다
        if (EscortingNpc != null && EscortingNpc.CurrentState != NpcState.Escorted)
            SetEscorting(null);

        // 대상이 파괴되면(라운드 종료 시 NPC가 씬과 함께 destroy) 위 가드와 아래 끌기 가드가 Unity 가짜 null에
        // 걸려 통째로 건너뛴다 — 참조는 사라졌는데 동기화 플래그만 남으면 원격 오너의 IsBusy가 영구 true가 되어
        // E 상호작용이 다음 라운드까지 죽는다(PlayerInteractor가 매 입력을 '놓기'로 소비). 참조가 비면 플래그도 내린다. (#356)
        if (IsSpawned && IsServer)
        {
            if (EscortingNpc == null && m_isEscortingSynced.Value)
                SetEscorting(null);
            if (DraggingNpc == null && m_draggedNpcSynced.Value.NetworkObjectId != 0)
                SetDragging(null);
        }

        // 밧줄 끌기: 끌리는 NPC를 매 프레임 밧줄 장력으로 끌어당긴다 — 위치 종속(플레이어 속도 그대로). (#269)
        if (DraggingNpc != null)
        {
            if (DraggingNpc.CurrentState != NpcState.Stunned)
            {
                // 외부 요인으로 기절에서 벗어났으면(예: 강제 상태 전이) 끌기를 정리한다.
                ReleaseDrag();
            }
            else
            {
                ServerUpdateDrag();
            }
        }
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
