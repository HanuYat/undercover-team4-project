using Unity.Netcode;
using UnityEngine;

/// <summary>
/// PlayerEscorter의 밧줄 끌기(#269) 흐름 — 검거·연행 채널링과 독립된 상호작용이라 partial로 분리한다.
/// 기절한 NPC를 밧줄 장력으로 매 프레임 끌어당기는 서버 권위 로직과 그 상태·동기화 필드를 담는다.
/// (본체 PlayerEscorter.cs는 검거/제압/해제 채널링 + 연행/놓기를 담당.)
/// </summary>
public partial class PlayerEscorter
{
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

    /// <summary>지금 밧줄로 끌고 있는 NPC. 없으면 null. 서버(또는 오프라인)에서만 유효. (#269)</summary>
    public NpcController DraggingNpc { get; private set; }

    // 끌리는 대상을 클라이언트에도 알린다 — 서버만 기록한다(연행 플래그와 동일 관례).
    // 단순 bool이 아니라 대상 참조인 이유: 원격 피어의 밧줄 표시(RopeDragView)가 선의 양 끝점을
    // 알아야 하는데, DraggingNpc는 서버에서만 세팅되므로 누구를 끄는지 알 방법이 없다.
    private readonly NetworkVariable<NetworkObjectReference> m_draggedNpcSynced = new();

    // 끌기 추종 상태 (서버·오프라인 전용) — 매 프레임 이어지는 값이라 SetDragging에서 초기화한다.
    private Vector3 m_dragVelocity;   // SmoothDamp 관성
    private Quaternion m_dragFacing;  // 흔들림을 뺀 몸 방향 — 여기에 sway를 얹어 최종 회전을 만든다
    private float m_dragTravel;       // 끌린 누적 거리(m) — 흔들림 위상의 기준

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

    [Rpc(SendTo.Server)]
    private void RopeDragRequestRpc(NetworkObjectReference targetRef)
    {
        if (targetRef.TryGet(out NetworkObject targetObj) &&
            targetObj.TryGetComponent(out NpcController target))
        {
            ServerBeginRopeDrag(target);
        }
    }

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

    /// <summary>밧줄 끌기 매 프레임 처리 — 본체 Update가 서버(또는 오프라인)에서만 호출한다. (#269)</summary>
    private void TickRopeDrag()
    {
        if (DraggingNpc == null)
            return;

        // 외부 요인으로 기절에서 벗어났으면(예: 강제 상태 전이) 끌기를 정리한다.
        if (DraggingNpc.CurrentState != NpcState.Stunned)
        {
            ReleaseDrag();
            return;
        }

        ServerUpdateDrag();
    }

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
}
