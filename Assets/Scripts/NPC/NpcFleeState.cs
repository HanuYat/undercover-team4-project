using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// 도주(Run) 상태 — 수갑 채널링 성공 순간 뿌리치고 위협(플레이어) 반대 방향으로 달아난다. (GDD 6-1, #76)
/// 추적자와 충분히 멀어지면 도주 성공으로 보고 배회로 복귀한다.
/// 잡는 방법: 근접 제압 홀드(NpcSubdueInteractable) 또는 테이저(후속 아이템).
/// </summary>
public class NpcFleeState : NpcStateBase
{
    private const int k_maxSampleAttempts = 10;
    private const float k_arriveThreshold = 0.5f;
    // 도주 방향에 주는 ±각도 지터 — 일직선으로만 도망가다 벽·코너에 박히는 것을 완화한다
    private const float k_directionJitterDegrees = 50f;

    private float m_baseSpeed;

    public NpcFleeState(NpcController owner) : base(owner) { }

    public override void Enter()
    {
        m_owner.Agent.isStopped = false;
        m_baseSpeed = m_owner.Agent.speed;
        m_owner.Agent.speed = m_baseSpeed * m_owner.FleeSpeedMultiplier;
        SetFleePoint();
    }

    public override void Tick()
    {
        Transform threat = m_owner.ThreatTarget;
        if (threat == null)
        {
            // 위협 소실(플레이어 파괴 등) — 도망갈 이유가 없어졌으니 배회로 복귀
            m_owner.StateMachine.ChangeState(NpcState.Idle);
            return;
        }

        // 충분히 멀어지면 도주 성공 — 다시 배회한다 (여전히 범인이므로 재시도 가능)
        float distance = Vector3.Distance(m_owner.transform.position, threat.position);
        if (distance > m_owner.FleeEscapeDistance)
        {
            Debug.Log($"도주 성공 — 추적자와 {distance:F1}m: {m_owner.name}");
            m_owner.StateMachine.ChangeState(NpcState.Idle);
            return;
        }

        // 도주 지점에 도착했는데 아직 붙잡히지 않았다면 다음 지점으로 계속 도망친다
        if (!m_owner.Agent.pathPending &&
            m_owner.Agent.remainingDistance <= m_owner.Agent.stoppingDistance + k_arriveThreshold)
        {
            SetFleePoint();
        }
    }

    public override void Exit()
    {
        m_owner.Agent.speed = m_baseSpeed;
        if (m_owner.Agent.isOnNavMesh)
            m_owner.Agent.ResetPath();
        m_owner.ClearThreat();
    }

    /// <summary>위협 반대 방향 ±지터 안에서 NavMesh 위 도주 지점을 뽑는다.</summary>
    private void SetFleePoint()
    {
        Transform threat = m_owner.ThreatTarget;
        if (threat == null)
            return;

        Vector3 away = (m_owner.transform.position - threat.position).normalized;
        if (away.sqrMagnitude < 0.01f)
            away = Random.insideUnitSphere; // 완전히 겹쳐 있으면 방향이 없으므로 아무 방향으로
        away.y = 0f;

        for (int i = 0; i < k_maxSampleAttempts; i++)
        {
            // 시도가 거듭될수록 지터를 키운다 — 막다른 방향이면 점점 옆길을 찾게 된다
            float jitter = Random.Range(-k_directionJitterDegrees, k_directionJitterDegrees) * (1f + i * 0.5f);
            Vector3 direction = Quaternion.Euler(0f, jitter, 0f) * away;
            Vector3 candidate = m_owner.transform.position + direction * m_owner.FleeStepDistance;

            if (NavMesh.SamplePosition(candidate, out NavMeshHit hit, 2f, NavMesh.AllAreas))
            {
                m_owner.Agent.SetDestination(hit.position);
                return;
            }
        }
    }
}
