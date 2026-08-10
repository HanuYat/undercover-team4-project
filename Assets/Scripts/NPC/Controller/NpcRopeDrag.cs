using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// 밧줄 도메인 부품 (#269/#369/#398/#503) — 묶임·끌기·무게를 들고 있다.
/// 끌리는 동안에는 NavMeshAgent를 끄고 위치를 직접 대입한다(NetworkTransform이 결과를 복제).
/// 장력 틱은 코어 Update가 넉백·스턴 게이트보다 <b>먼저</b> 돌린다 — 계획서 § 4-1.
/// </summary>
public class NpcRopeDrag : NetworkBehaviour
{
    private NpcController m_owner;

    // ---- 밧줄 끌기 (#269) ----

    // 끌리는 동안 매 프레임 바닥을 찾을 때의 탐색 반경(m) — 계단 한 칸을 넘길 만큼만.
    private const float k_dragGroundSnapRadius = 1f;

    private bool m_roped;

    // 끌리는 중인지의 클라 사본 — 표현 계층이 이 값으로 누운 모션을 고른다. 커스터디 상태는
    // Escorted(수갑 찬 걷기)라 이게 없으면 원격 피어에서 NPC가 서서 끌려간다. (#369)
    private readonly NetworkVariable<bool> m_ropedSynced = new(false);

    // 장력을 거는 쪽(끄는 플레이어들) — 여러 명이 함께 끌 수 있어(줄다리기) 목록이다.
    // E로 놓은 참가자는 여기서 빠지고 줄만 남는다. 서버(또는 오프라인) 전용.
    private readonly List<Transform> m_dragAnchors = new List<Transform>();

    // 끌기 추종 상태 — 끄는 쪽이 아니라 끌리는 쪽이 갖는다(한 명이 여럿을 끌면 NPC 수만큼 필요하다).
    private Vector3 m_dragVelocity; // SmoothDamp 관성
    private Quaternion m_dragFacing; // 흔들림을 뺀 몸 방향 — 여기에 sway를 얹어 최종 회전을 만든다
    private float m_dragTravel; // 끌린 누적 거리(m) — 흔들림 위상의 기준

    private void Awake()
    {
        m_owner = GetComponent<NpcController>();
    }

    /// <summary>밧줄로 묶여 <b>누군가에게</b> 끌리는 중인가 — 참가자별 판정은 <see cref="IsDraggedBy"/>.
    /// 서버·오프라인은 실제 값으로, 원격 피어는 동기화 플래그로 판정. (#269/#369)</summary>
    public bool IsRoped => IsSpawned && !IsServer ? m_ropedSynced.Value : m_roped;

    // ---- 묶임 (#513) ----
    // 끌림(IsRoped)과 묶임(IsTethered)은 다르다. E 놓기는 끌기만 멈추고 줄은 남으며, 실제로 푸는 건
    // E 풀기·인계 완료·방치 탈주·거리 초과 끊김뿐이다(GDD 8-2). 표현이 끌림에만 매달려 있으면
    // 놓는 순간 묶인 몸이 벌떡 일어선다.

    // 이 NPC에 걸린 줄 수 — 한 명이 자기 줄만 풀어도(#390 규칙 8) 남은 줄이 있으면 여전히 묶여 있다.
    private int m_tetherCount;

    // 묶임의 클라 사본 — 묶임 목록 자체는 PlayerEscorter에만 있어 표현 계층이 물을 수 없다.
    private readonly NetworkVariable<bool> m_tetheredSynced = new(false);

    /// <summary>밧줄이 묶여 있는가 — <b>끌리는 중이 아니어도</b> 참이다(E로 놓아둔 대상).
    /// 서버·오프라인은 실제 값으로, 원격 피어는 동기화 플래그로 판정. (#513)</summary>
    public bool IsTethered => IsSpawned && !IsServer ? m_tetheredSynced.Value : m_tetherCount > 0;

    /// <summary>줄 하나가 걸렸다 — <see cref="PlayerEscorter"/>의 연결 목록이 실제로 늘어날 때만 호출한다.
    /// 서버(또는 오프라인) 전용. (#513)</summary>
    internal void AddTether()
    {
        if (IsSpawned && !IsServer)
            return;

        m_tetherCount++;
        SyncTethered();
    }

    /// <summary>줄 하나가 풀렸다 — 연결 목록에서 실제로 빠질 때만 호출한다. 서버(또는 오프라인). (#513)</summary>
    internal void RemoveTether()
    {
        if (IsSpawned && !IsServer)
            return;

        m_tetherCount = Mathf.Max(0, m_tetherCount - 1);
        SyncTethered();
    }

    private void SyncTethered()
    {
        if (IsSpawned && IsServer)
            m_tetheredSynced.Value = m_tetherCount > 0;
    }

    /// <summary>묶임 표시를 통째로 내린다 — 커스터디를 벗어나는 FSM 전이(코어)가 부른다. (#513)
    ///
    /// <see cref="PlayerEscorter"/>의 매 프레임 정리와 평소엔 중복이지만, 끌던 플레이어가 접속을 끊으면
    /// 그 정리가 아예 돌지 않아 배회로 돌아간 몸이 누운 모션으로 걸어 다닌다.</summary>
    internal void ClearTethers()
    {
        if (m_tetherCount == 0)
            return;

        m_tetherCount = 0;
        SyncTethered();
    }

    /// <summary>밧줄 길이(m) — 표시(늘어짐 정도)와 서버 장력 판정이 같은 값을 쓴다.</summary>
    public float RopeLength => m_owner.RopeDragConfig.RopeLength;

    // ---- 무게 (#398) ----

    // 동기화하지 않는다 — 읽는 건 서버의 페널티 계산뿐이고 결과 배율만 동기화된다(RopeDragLoad).
    private float m_dragWeight = 1f;

    /// <summary>이 NPC의 무게 — 끄는 플레이어의 속도 페널티 기준. 서버(또는 오프라인)에서만 유효. (#398)</summary>
    public float DragWeight => m_dragWeight;

    // 참가자 수의 클라 사본 — 목줄 반경(끊김거리 ÷ 참가자 수)을 오너가 계산해야 해서 클라도 알아야 한다.
    private readonly NetworkVariable<byte> m_draggerCountSynced = new(0);

    /// <summary>지금 이 NPC에 장력을 걸고 있는 인원 수 — 페널티를 이 수로 나눈다. 전 피어에서 유효.
    /// E로 놓아 장력에서 빠진 참가자는 포함되지 않는다. (#398)</summary>
    public int DraggerCount =>
        IsSpawned && !IsServer ? m_draggerCountSynced.Value : m_dragAnchors.Count;

    /// <summary>무게 배정 — 코어의 InitBehavior에서 서버(또는 오프라인) 1회 호출된다. (#398)</summary>
    internal void InitDragWeight() => m_dragWeight = m_owner.CommonConfig.PickWeight();

    /// <summary>밧줄 끌기 시작 — PlayerEscorter가 서버에서 호출. 위치를 끄는 플레이어가 제어하므로
    /// NavMeshAgent를 끈다. 커스터디 전이(Escorted)는 호출부가 <b>이 호출 앞에</b> 한다 — 뒤에 하면
    /// 직전 상태의 Exit이 꺼진 에이전트를 건드린다. 끈 플레이어는 위협으로 기억한다.</summary>
    public void StartRopeDrag(Transform dragger = null)
    {
        if (IsSpawned && !IsServer)
            return;

        // 첫 참가자일 때만 추종 상태를 새로 잡는다 — 합류할 때 리셋하면 끌려가던 몸이 멈칫한다.
        if (!m_roped)
        {
            m_dragVelocity = Vector3.zero;
            m_dragFacing = transform.rotation;
            m_dragTravel = 0f;
        }

        if (dragger != null)
        {
            m_owner.Reaction.ThreatTarget = dragger;
            if (!m_dragAnchors.Contains(dragger))
                m_dragAnchors.Add(dragger);
        }

        // 일어나던 중이었으면 되돌린다 — 방치 만료로 일어나는 도중에 달려와 E를 누른 재포획이 이 경로다.
        // 예약된 후속 동작(도주 등)도 함께 버려진다. (#513)
        m_owner.StandUp.CancelStandUp();

        // 줄이 걸리는 순간 반출 흐름은 끝난다 — 이제 밧줄 신병이라 E는 놓기/재개로 갈린다 (#517)
        m_owner.Custody.SetJailExtracted(false);

        // 걸어가던 대상을 잡았다 — 반출은 여기서 무산된다 (#548). 상태 전이는 호출부가 이미
        // Escorted로 해 두었으므로 목적지만 지운다. 풀어 주더라도 인도 지점으로 다시 걷지 않는다.
        m_owner.Custody.ClearRelease();

        SetRoped(true);
        SyncDraggerCount();

        NavMeshAgent agent = m_owner.Agent;
        if (agent != null && agent.enabled)
            agent.enabled = false;
    }

    /// <summary>이 플레이어가 지금 이 NPC에 장력을 걸고 있는가 — 서버(또는 오프라인) 전용.
    /// 끄는 쪽(PlayerEscorter)의 "내가 이걸 끌고 있나"가 이 값을 그대로 쓴다 — 따로 두면 어긋난다.</summary>
    public bool IsDraggedBy(Transform dragger) =>
        dragger != null && m_dragAnchors.Contains(dragger);

    // 부채꼴 배치용 자리 번호 — 끄는 쪽이 매 프레임 알려준다.
    private int m_dragSlot;
    private int m_dragSlotCount = 1;

    /// <summary>끌리는 자리(부채꼴 배치용) 지정 — 끄는 플레이어가 매 프레임 갱신한다. 서버(또는 오프라인) 전용.</summary>
    public void SetDragSlot(int slot, int slotCount)
    {
        m_dragSlot = slot;
        m_dragSlotCount = Mathf.Max(1, slotCount);
    }

    private void SetRoped(bool value)
    {
        m_roped = value;
        if (IsSpawned && IsServer)
            m_ropedSynced.Value = value;
    }

    /// <summary>밧줄 끌기 해제 — releaser를 장력에서 뺀다. 남은 참가자가 있으면 <b>true</b>(끌기 계속).
    /// 마지막 한 명이 놓았을 때만 에이전트를 되살려 NavMesh로 복귀(Warp)시키고 false를 돌려준다 —
    /// 안 하면 이후 이동·상태 전이가 조용히 실패한다. 커스터디 행선지는 호출부가 정한다.
    /// releaser는 목록에서 뺄 키이자, 놓은 자리가 NavMesh 밖일 때 대체 기준점이다.</summary>
    public bool StopRopeDrag(Transform releaser)
    {
        if (IsSpawned && !IsServer)
            return false;

        if (releaser != null)
            m_dragAnchors.Remove(releaser);
        PruneDeadAnchors();

        // 아직 잡고 있는 사람이 남아 있으면 끌기는 이어진다 — 멈추면 줄다리기가 성립하지 않는다
        if (m_dragAnchors.Count > 0)
            return true;

        SetRoped(false);

        NavMeshAgent agent = m_owner.Agent;
        if (agent == null)
            return false;

        // 넉백 비행 중이면 에이전트는 넉백이 쥐고 있다 — 여기서 되살리면 날아가던 몸을 NavMesh로 도로
        // 끌어내린다. 착지할 때 EndKnockback이 붙인다. (끌던 중 폭발에 맞은 경우)
        if (m_owner.Knockback.IsKnockedBack)
            return false;

        agent.enabled = true;

        // NavMesh에 못 붙으면 이후 isStopped·SetDestination이 조용히 실패해 NPC가 굳는다(빌드 2 이슈 E).
        // 놓은 자리 → 놓는 플레이어 자리(거기까지 걸어왔으니 유효한 바닥) 순으로 시도.
        if (m_owner.TryWarpNear(transform.position))
            return false;
        if (releaser != null && m_owner.TryWarpNear(releaser.position))
            return false;

        // 포기해도 굳지는 않는다 — TickNavMeshRecovery가 1초 뒤 더 넓게 다시 붙인다 (#557).
        // 위치를 남기는 것은 회수가 원인 지점을 조용히 덮지 않게 하기 위해서다.
        Debug.LogWarning(
            "NpcRopeDrag: 밧줄을 놓은 지점을 NavMesh에 붙이지 못했다 — 회수 대기: "
                + $"{name} @{transform.position.ToString("F1")}",
            this
        );
        return false;
    }

    // 파괴된 참가자(접속 종료 등)를 걷어낸다 — 남겨두면 장력 계산이 가짜 null을 만진다.
    private void PruneDeadAnchors()
    {
        for (int i = m_dragAnchors.Count - 1; i >= 0; i--)
            if (m_dragAnchors[i] == null)
                m_dragAnchors.RemoveAt(i);

        SyncDraggerCount(); // 놓기·이탈이 전부 이 경로를 지난다
    }

    // 매 프레임 불려도 대역폭을 안 먹는다 — NetworkVariable 세터가 같은 값이면 스스로 조기 반환한다.
    private void SyncDraggerCount()
    {
        if (IsSpawned && IsServer)
            m_draggerCountSynced.Value = (byte)m_dragAnchors.Count;
    }

    /// <summary>
    /// 밧줄 장력으로 끌리는 몸을 끌어당긴다 — 코어 Update가 매 프레임 돌린다. 서버(또는 오프라인). (#269)
    ///
    /// 뒤 고정점에 강체로 붙이지 않는다: 밧줄 길이를 넘을 때만 당기고, 늦게 따라오게 해서 코너를 돌면
    /// 몸이 바깥으로 끌려나오는 궤적이 생긴다.
    ///
    /// <b>호출 위치는 넉백·스턴 게이트보다 앞이다</b> — 묶인 채 기절한 대상도 끌려가야 하므로
    /// 게이트 뒤로 내리면 테이저→밧줄 콤보로 잡은 대상이 그 자리에 멈춘다. (§ 5-1)
    /// </summary>
    internal void Tick()
    {
        if (!m_roped)
            return;

        // 앵커가 사라지면(끌던 플레이어 파괴 등) 멈추기만 한다 — 상태 정리는 PlayerEscorter 몫이다.
        PruneDeadAnchors();
        if (m_dragAnchors.Count == 0)
            return;

        NpcRopeDragConfig config = m_owner.RopeDragConfig;
        Vector3 npcPosition = transform.position;
        float ropeLength = config.RopeLength;

        // 여러 명을 함께 끌 때 자리마다 옆으로 벌린다(혼자면 0이라 궤적이 예전과 같다).
        // 단 경합(줄다리기) 중에는 벌리지 않는다 — 자리 번호를 각자 자기 기준으로 매겨 같은 대상에
        // 다른 번호가 들어오고, 매 프레임 나중에 도는 쪽이 이겨 오프셋이 좌우로 떨린다.
        float lateral =
            m_dragAnchors.Count > 1
                ? 0f
                : (m_dragSlot - (m_dragSlotCount - 1) * 0.5f) * config.DragSpacing;

        // 참가자마다 "자기 밧줄이 허용하는 위치"를 내고 그 평균으로 간다 — 합력. 서로 반대로 당기면
        // 두 목표가 상쇄돼 가운데서 멈춘다(줄다리기). 늘어져 있으면 그 참가자는 당기지 않는다.
        Vector3 targetSum = Vector3.zero;
        Vector3 anchorSum = Vector3.zero;
        for (int i = 0; i < m_dragAnchors.Count; i++)
        {
            Vector3 anchorPoint = m_dragAnchors[i].position;
            anchorSum += anchorPoint;

            Vector3 toNpc = npcPosition - anchorPoint;
            toNpc.y = 0f;
            float distance = toNpc.magnitude;

            Vector3 pull = npcPosition;
            if (distance > ropeLength)
            {
                Vector3 direction = toNpc / distance;

                // 자리 오프셋을 태워도 앵커와의 거리는 밧줄 길이로 유지한다 — 벌린 만큼 늘어나면
                // 뒤로 갈수록 줄이 길어져 끊김 판정에 먼저 걸린다.
                Vector3 right = Vector3.Cross(Vector3.up, direction);
                pull =
                    anchorPoint
                    + (direction * ropeLength + right * lateral).normalized * ropeLength;
            }

            // 높이는 시드만 한다 — 지면 스냅·벽 판정은 ResolveDragPosition이 확정한다 (#369)
            pull.y = anchorPoint.y;
            targetSum += pull;
        }

        Vector3 target = targetSum / m_dragAnchors.Count;
        Vector3 anchor = anchorSum / m_dragAnchors.Count; // 몸 방향 기준점 — 참가자들의 중점

        Vector3 next = Vector3.SmoothDamp(
            npcPosition,
            target,
            ref m_dragVelocity,
            config.DragSmoothTime
        );

        // 몸 방향은 플레이어 회전이 아니라 밧줄 방향 — 제자리에서 마우스만 돌려도 NPC는 안 돈다
        Vector3 ropeDirection = anchor - next;
        ropeDirection.y = 0f;
        if (ropeDirection.sqrMagnitude > 0.0001f)
        {
            Quaternion facing = Quaternion.LookRotation(ropeDirection);
            m_dragFacing = Quaternion.Slerp(
                m_dragFacing,
                facing,
                1f - Mathf.Exp(-config.DragTurnSharpness * Time.deltaTime)
            );
        }

        // 흔들림은 시간이 아니라 끌린 거리 기준이라 멈추면 함께 멈춘다
        m_dragTravel += (next - npcPosition).magnitude;
        float sway = Mathf.Sin(m_dragTravel * config.DragSwayFrequency) * config.DragSwayAngle;

        transform.SetPositionAndRotation(
            ResolveDragPosition(next),
            m_dragFacing * Quaternion.Euler(0f, sway, 0f)
        );
    }

    /// <summary>끌리는 몸의 다음 위치를 지형에 맞춘다 (#369) — 에이전트를 끈 채 위치를 직접 대입하므로
    /// NavMesh가 대신 풀어 주던 벽·바닥을 스스로 처리해야 한다.</summary>
    private Vector3 ResolveDragPosition(Vector3 desired)
    {
        Vector3 delta = desired - transform.position;
        delta.y = 0f;
        float distance = delta.magnitude;

        // 벽 스윕(코어 공용 판정). 넉백처럼 '전부 멈춤'을 쓰면 끌기는 상태가 이어져서 벽을 따라
        // 빠져나가는 방향까지 막혀 영구히 낀다 — 파고드는 성분만 버리고 미끄러뜨린다. (#313/#339)
        if (
            distance > 0.001f
            && m_owner.SweepHitsObstacle(delta / distance, distance, out RaycastHit wall)
        )
        {
            Vector3 normal = wall.normal;
            normal.y = 0f;
            if (normal.sqrMagnitude > 0.0001f)
            {
                normal.Normalize();
                float into = Vector3.Dot(delta, normal);
                if (into < 0f)
                {
                    Vector3 slide = delta - normal * into;
                    desired.x = transform.position.x + slide.x;
                    desired.z = transform.position.z + slide.z;
                }
            }
        }

        // 지면 스냅 — 끄는 플레이어의 발밑 높이를 그대로 쓰면 계단·경사에서 뜨거나 박힌다.
        // 찾지 못하면(NavMesh 밖으로 끌려나간 순간 등) 넘겨받은 높이를 그대로 둔다.
        if (
            NavMesh.SamplePosition(
                desired,
                out NavMeshHit ground,
                k_dragGroundSnapRadius,
                NavMesh.AllAreas
            )
        )
            desired.y = ground.position.y;

        return desired;
    }
}
