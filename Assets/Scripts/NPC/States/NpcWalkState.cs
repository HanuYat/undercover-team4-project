using UnityEngine;
using UnityEngine.AI;

public class NpcWalkState : NpcStateBase
{
    private const int k_maxSampleAttempts = 10;
    private const float k_arriveThreshold = 0.5f;

    // 막힘 감지 — 군중 교착 등으로 이 시간(초) 이상 제자리면 목적지를 다시 뽑는다
    private const float k_stuckSpeedThreshold = 0.05f;
    private const float k_stuckTimeout = 2f;

    private float m_stuckTimer;

    private readonly NpcWalkConfig m_config;

    public NpcWalkState(NpcController owner, NpcWalkConfig config) : base(owner)
    {
        m_config = config;
    }

    public override void Enter()
    {
        m_stuckTimer = 0f;
        SetNextWanderPoint();
    }

    public override void Tick()
    {
        // 목적지에 도착하면 Idle로 전환해 잠깐 쉬었다가 다시 걷는다
        if (!m_owner.Agent.pathPending &&
            m_owner.Agent.remainingDistance <= m_owner.Agent.stoppingDistance + k_arriveThreshold)
        {
            m_owner.StateMachine.ChangeState(NpcState.Idle);
            return;
        }

        // 막힘 감지 안전망 — 좁은 길목에서 여럿이 엉켜 회피로도 못 풀 때,
        // 목적지를 새로 뽑아 교착이 스스로 풀리게 한다
        if (!m_owner.Agent.pathPending && m_owner.Agent.velocity.magnitude < k_stuckSpeedThreshold)
        {
            m_stuckTimer += Time.deltaTime;
            if (m_stuckTimer >= k_stuckTimeout)
            {
                m_stuckTimer = 0f;
                SetNextWanderPoint();
            }
        }
        else
        {
            m_stuckTimer = 0f;
        }
    }

    public override void Exit()
    {
        if (m_owner.Agent.isOnNavMesh)
            m_owner.Agent.ResetPath();
    }

    private void SetNextWanderPoint()
    {
        for (int i = 0; i < k_maxSampleAttempts; i++)
        {
            // 최소~최대 거리 사이의 랜덤 방향 지점을 뽑는다 — 너무 가까운 지점을 배제해 한두 걸음 걷고 마는 이동을 방지
            float angle = Random.Range(0f, Mathf.PI * 2f);
            float distance = Random.Range(m_config.MinWanderDistance, m_config.WanderRadius);
            Vector3 direction = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
            Vector3 candidate = m_owner.transform.position + direction * distance;

            // 통행 마스크로 샘플 — 에이전트가 못 가는 영역(Jail)을 뽑으면 경로가 문 앞에서 끊긴다 (#415).
            // 도로는 여기서만 뺀다 — 배회하다 도로 한복판을 목적지로 잡으면 거기 멈춰 서서 치인다.
            // 에이전트 마스크 자체는 그대로라 <b>건너가는 경로는 여전히 도로를 지난다</b> (#634).
            if (NavMesh.SamplePosition(candidate, out NavMeshHit hit, 2f,
                    NpcNavAreas.ExcludeRoad(m_owner.Agent.areaMask)))
            {
                m_owner.Agent.SetDestination(hit.position);
                return;
            }
        }
    }
}
