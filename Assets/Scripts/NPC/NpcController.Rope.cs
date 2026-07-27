using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

public partial class NpcController
{
    // ---- 밧줄 끌기 (#269) ----

    // 밧줄을 놓은 지점에서 NavMesh를 찾을 때의 탐색 반경(m).
    private const float k_ropeReleaseSnapRadius = 2f;

    // 끌리는 동안 매 프레임 바닥을 찾을 때의 탐색 반경(m) — 계단 한 칸을 넘길 만큼만.
    private const float k_dragGroundSnapRadius = 1f;

    private bool m_roped;

    // 끌리는 중인지를 클라이언트에도 알리는 동기화 플래그 — 서버만 기록한다(PlayerEscorter의 연행 플래그와 같은 관례).
    // 표현 계층(NpcAnimationDriver)이 이 값으로 누운 모션을 고르는데, 커스터디 상태는 Escorted(수갑 찬 걷기)라
    // 이게 없으면 원격 피어에서 NPC가 서서 끌려간다. (#369)
    private readonly NetworkVariable<bool> m_ropedSynced = new(false);

    /// <summary>밧줄로 묶여 끌리는 중인가. 서버·오프라인은 실제 값으로, 원격 피어는 동기화 플래그로 판정. (#269/#369)</summary>
    public bool IsRoped => IsSpawned && !IsServer ? m_ropedSynced.Value : m_roped;

    /// <summary>밧줄 끌기 시작 — PlayerEscorter가 서버에서 호출. 위치를 끄는 플레이어가 직접 제어하므로
    /// NavMeshAgent를 끈다(켜져 있으면 에이전트가 위치를 도로 잡아당긴다).
    /// 커스터디 상태 전이(Escorted)는 호출부가 <b>이 호출 앞에</b> 한다 — 에이전트를 끈 뒤에 전이하면
    /// 직전 상태의 Exit이 꺼진 에이전트를 건드린다 (넉백 ServerApplyKnockback과 같은 순서).
    /// 끈 플레이어를 위협으로 기억한다 — 놓아준 뒤 도주 상태가 되면 그 플레이어에게서 도망친다.</summary>
    public void StartRopeDrag(Transform dragger = null)
    {
        if (IsSpawned && !IsServer)
            return;

        if (dragger != null)
            ThreatTarget = dragger;
        SetRoped(true);
        if (m_agent != null && m_agent.enabled)
            m_agent.enabled = false;
    }

    // 끌기 플래그와 동기화 값을 함께 갱신 — 서버(또는 오프라인)에서만 호출된다.
    private void SetRoped(bool value)
    {
        m_roped = value;
        if (IsSpawned && IsServer)
            m_ropedSynced.Value = value;
    }

    /// <summary>밧줄 끌기 해제 — 에이전트를 되살려 NavMesh로 복귀(Warp)시킨다. 안 하면 이후 이동·상태가 깨진다.
    /// 커스터디 상태를 어디로 보낼지는 호출부가 정한다(놓기=Captured, 판정=유치장).
    /// releaser는 밧줄을 놓는 플레이어 — 놓은 자리가 NavMesh 밖일 때 대체 기준점으로 쓴다.</summary>
    public void StopRopeDrag(Transform releaser)
    {
        if (IsSpawned && !IsServer)
            return;

        SetRoped(false);
        if (m_agent == null)
            return;

        // 넉백 비행 중이면 에이전트는 넉백이 쥐고 있다 — 여기서 되살리면 날아가던 몸을 NavMesh로 도로
        // 끌어내린다. 착지할 때 EndKnockback이 붙이므로 그냥 넘긴다. (끌던 중 폭발에 맞은 경우)
        if (m_knockbackActive)
            return;

        m_agent.enabled = true;

        // NavMesh에 못 붙으면 이후 상태 전이의 isStopped·SetDestination이 조용히 실패해 NPC가 그 자리에
        // 굳는다(빌드 2 이슈 E). 놓은 자리 → 실패 시 놓는 플레이어 자리(거기까지 걸어왔으니 유효한 바닥) 순으로 시도.
        if (TryWarpNear(transform.position))
            return;
        if (releaser != null && TryWarpNear(releaser.position))
            return;

        Debug.LogWarning(
            $"NpcController: 밧줄을 놓은 지점을 NavMesh에 붙이지 못했다 — 이후 이동·상태 전이가 조용히 실패한다: {name}",
            this
        );
    }

    // 기준점 주변에서 NavMesh 위 지점을 찾아 에이전트를 붙인다 — 붙었으면 true.
    private bool TryWarpNear(Vector3 origin)
    {
        if (
            !NavMesh.SamplePosition(
                origin,
                out NavMeshHit hit,
                k_ropeReleaseSnapRadius,
                NavMesh.AllAreas
            )
        )
            return false;

        return m_agent.Warp(hit.position) && m_agent.isOnNavMesh;
    }

    /// <summary>끌리는 동안 위치·회전을 설정한다 — 끄는 플레이어(PlayerEscorter)가 매 서버 프레임 호출.
    /// NetworkTransform이 전 클라에 복제하므로 원격 피어에서도 끌리는 위치가 맞는다.</summary>
    public void ServerDragTo(Vector3 position, Quaternion rotation)
    {
        if (IsSpawned && !IsServer)
            return;
        if (!m_roped)
            return;

        transform.SetPositionAndRotation(ResolveDragPosition(position), rotation);
    }

    /// <summary>
    /// 끌리는 몸의 다음 위치를 지형에 맞춘다. (#369)
    /// 끌기는 에이전트를 끄고 위치를 직접 대입하므로 NavMesh가 대신 풀어 주던 벽·바닥을 스스로 처리해야 한다 —
    /// 밧줄이 기본 검거가 되면 본부 실내에서 유치장까지 문틀·좁은 통로·계단을 상시로 지난다.
    /// </summary>
    private Vector3 ResolveDragPosition(Vector3 desired)
    {
        Vector3 delta = desired - transform.position;
        delta.y = 0f;
        float distance = delta.magnitude;

        // 벽 스윕(넉백 판정 재사용, 사람은 안 침 — #313/#339). 넉백처럼 '전부 멈춤'을 쓰면 끌기는 상태가
        // 이어져서 벽을 따라 빠져나가는 방향까지 막혀 영구히 낀다 — 파고드는 성분만 버리고 미끄러뜨린다.
        if (distance > 0.001f && SweepHitsObstacle(delta / distance, distance, out RaycastHit wall))
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

        // 지면 스냅 — 끄는 플레이어의 발밑 높이를 그대로 쓰면 계단·경사에서 뜨거나 바닥에 박힌다.
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
