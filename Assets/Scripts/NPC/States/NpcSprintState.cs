using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// 질주(Sprinting) 상태 — 도착할 때마다 새 목적지를 뽑아 <b>멈추지 않고</b> 도심을 뛰어다닌다.
/// 공연음란범(<see cref="StreakerEvent"/>)이 쓴다. (GDD 6-4, #106)
///
/// 도주(<see cref="NpcFleeState"/>)와 갈라져 나온 이유는 <b>끝나는 조건이 없다</b>는 것이다:
/// 도주는 위협에게서 멀어지면 배회로 복귀하지만, 이쪽은 위협도 이탈 판정도 없고 잡히거나 라운드가
/// 끝날 때까지 달린다. 그래서 배회(<see cref="NpcWalkState"/>)처럼 지점을 뽑되 Idle로 쉬러 가지 않는다.
///
/// 속도·거리는 도주 값(<see cref="NpcFleeConfig"/>)을 빌려 쓴다 — 새 SO를 만들면 NPC 프리팹을
/// 전부 다시 배선해야 하고, 튜닝 축("얼마나 빠르게 · 얼마나 멀리")이 도주와 같다.
/// </summary>
public class NpcSprintState : NpcStateBase
{
    private const float k_arriveThreshold = 0.5f;

    // 후보 지점 추첨 시도 횟수 — 배회와 같은 값
    private const int k_maxSampleAttempts = 10;

    // 도달 가능성 확인 횟수 상한 — 배회·도주와 같은 이유로 묶는다(후보마다 경로를 계산하면 비싸다)
    private const int k_maxReachabilityProbes = 4;

    // 후보 지점을 NavMesh 위로 끌어당길 때 허용하는 최대 거리(m) — 멀리 잡는 지점이라 도주의 먼 지점과 같이 넉넉히
    private const float k_navSampleMaxDistance = 4f;

    // 한 판정 구간에 이만큼(m)도 못 갔으면 막힘 — 질주 속도 6m/s면 0.5초에 3m는 간다 (도주와 같은 기준)
    private const float k_stuckMinProgress = 0.5f;

    // 막힘이 이 횟수 이어지면 목적지를 재추첨한다 — StuckCheck 주기 0.5초 × 2 = 1초
    private const int k_stuckStrikesToRepick = 2;

    // 지점 추첨 최소 간격(초) — 도달 가능한 지점이 없을 때 매 프레임 경로 계산을 도는 것을 막는다
    private const float k_repickInterval = 0.5f;

    // 서버에서만 Tick되므로 공유 안전 — 재추첨마다의 할당 방지 (배회·도주와 같은 방식)
    private static NavMeshPath s_pathProbe;

    private readonly NpcFleeConfig m_config;

    private float m_baseSpeed;
    private float m_nextPickTime;
    private Vector3 m_lastProgressPosition;
    private int m_stuckStrikes;

    public NpcSprintState(NpcController owner, NpcFleeConfig config)
        : base(owner)
    {
        m_config = config;
    }

    public override void Enter()
    {
        m_owner.Agent.isStopped = false;
        m_baseSpeed = m_owner.Agent.speed;
        m_owner.Agent.speed = m_baseSpeed * m_config.SpeedMultiplier;

        m_owner.Repath.MarkDone(NpcRepathChannel.StuckCheck);
        m_lastProgressPosition = m_owner.transform.position;
        m_stuckStrikes = 0;

        SetNextPoint();
    }

    public override void Tick()
    {
        // 도착하면 쉬지 않고 다음 지점을 뽑는다 — 배회가 Idle로 빠지는 자리다.
        // 경로가 없는 경우(기절에서 깨어나 경로가 비었을 때 등)도 여기서 다시 잡힌다.
        if (
            !m_owner.Agent.pathPending
            && (!m_owner.Agent.hasPath
                || m_owner.Agent.remainingDistance <= m_owner.Agent.stoppingDistance + k_arriveThreshold)
        )
        {
            // 지점을 못 뽑았을 때(NavMesh 조각에 갇힘 등) 매 프레임 경로를 다시 계산하지 않게 텀을 둔다
            if (Time.time < m_nextPickTime)
                return;

            m_nextPickTime = Time.time + k_repickInterval;
            SetNextPoint();
            return;
        }

        TickStuckWatch();
    }

    public override void Exit()
    {
        m_owner.Agent.speed = m_baseSpeed;
        if (m_owner.Agent.isOnNavMesh)
            m_owner.Agent.ResetPath();
    }

    // 막힘 감시 — 실제 이동 거리를 보고 제자리면 목적지를 재추첨한다 (배회 #661과 같은 안전망).
    // 플레이어가 몸으로 막아도 그쪽은 회피가 표면을 따라 미끄러져 속도로는 잡히지 않는다.
    private void TickStuckWatch()
    {
        if (!m_owner.Repath.Due(NpcRepathChannel.StuckCheck))
            return;

        Vector3 position = m_owner.transform.position;
        float progress = Vector3.Distance(position, m_lastProgressPosition);
        m_lastProgressPosition = position;

        if (progress >= k_stuckMinProgress)
        {
            m_stuckStrikes = 0;
            return;
        }

        if (++m_stuckStrikes < k_stuckStrikesToRepick)
            return;

        m_stuckStrikes = 0;
        SetNextPoint();
    }

    // 멀리 있는 다음 목적지를 뽑는다 — 방향은 무작위다. "누구에게서" 달아나는 것이 아니라
    // 그냥 뛰는 것이라 도주처럼 위협을 등지는 방향 점수를 매기지 않는다.
    private void SetNextPoint()
    {
        Vector3 origin = m_owner.transform.position;

        // 도달 가능한 지점이 하나도 없을 때의 차선 — 목적지를 안 주면 그 자리에 굳어 제자리 질주가 된다
        Vector3? fallback = null;
        int probes = 0;

        for (int i = 0; i < k_maxSampleAttempts && probes < k_maxReachabilityProbes; i++)
        {
            float angle = Random.Range(0f, Mathf.PI * 2f);
            float distance = Random.Range(m_config.StepDistance, m_config.FarPointDistance);
            Vector3 direction = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
            Vector3 candidate = origin + direction * distance;

            // 도로는 목적지에서만 뺀다 — 도로 한복판을 목적지로 잡으면 거기 멈춰 서서 치인다.
            // 지나가는 경로는 그대로 도로를 건넌다 (배회와 같은 규칙, #634).
            if (
                !NavMesh.SamplePosition(
                    candidate,
                    out NavMeshHit hit,
                    k_navSampleMaxDistance,
                    NpcNavAreas.ExcludeRoad(m_owner.Agent.areaMask)
                )
            )
                continue;

            fallback ??= hit.position;

            probes++;
            if (!IsReachable(origin, hit.position))
                continue;

            m_owner.Agent.SetDestination(hit.position);
            return;
        }

        if (fallback.HasValue)
            m_owner.Agent.SetDestination(fallback.Value);
    }

    /// <summary>origin에서 point까지 <b>끊기지 않는</b> 경로가 있는가 — 부분 경로는 도달 불가로 본다.</summary>
    private bool IsReachable(Vector3 origin, Vector3 point)
    {
        s_pathProbe ??= new NavMeshPath();

        return NavMesh.CalculatePath(origin, point, m_owner.Agent.areaMask, s_pathProbe)
            && s_pathProbe.status == NavMeshPathStatus.PathComplete;
    }
}
