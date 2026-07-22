using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// 패닉(Panic) 상태 — 소란(저항 전투·도주·돌발 이벤트)을 목격한 시민이 소란 반대 방향으로 달아난다. (GDD 6-5, #81)
/// 도주(Run)가 특정 추적자(Transform)에게서 도망치는 것과 달리 패닉은 소란 '지점'에서 멀어지며,
/// 소란이 멎고 진정 시간이 지나면 배회로 복귀한다. 패닉 중 재감지되면 진정 타이머·소란 지점이 갱신된다.
/// 패닉 중에도 수갑 채널링은 기존 경로 그대로 — 잡으면 반응 유형대로 처리된다.
/// </summary>
public class NpcPanicState : NpcStateBase
{
    private const int k_maxSampleAttempts = 10;
    private const float k_arriveThreshold = 0.5f;
    // 도주 방향에 주는 ±각도 지터 — 일직선으로만 도망가다 벽·코너에 박히는 것을 완화한다 (NpcFleeState와 동일 기법)
    private const float k_directionJitterDegrees = 50f;

    private float m_baseSpeed;

    private readonly NpcPanicConfig m_config;

    public NpcPanicState(NpcController owner, NpcPanicConfig config) : base(owner)
    {
        m_config = config;
    }

    public override void Enter()
    {
        m_owner.Agent.isStopped = false;
        m_baseSpeed = m_owner.Agent.speed;
        m_owner.Agent.speed = m_baseSpeed * m_config.SpeedMultiplier;
        SetEscapePoint();
    }

    public override void Tick()
    {
        // 소란이 멎은 뒤 진정 시간이 지나면 배회로 복귀
        if (Time.time - m_owner.LastDisturbedTime > m_config.CalmSeconds)
        {
            Debug.Log($"패닉 진정 — 배회 복귀: {m_owner.name}");
            m_owner.StateMachine.ChangeState(NpcState.Idle);
            return;
        }

        // 도주 지점에 도착했는데 아직 진정하지 않았다면 계속 달아난다
        if (!m_owner.Agent.pathPending &&
            m_owner.Agent.remainingDistance <= m_owner.Agent.stoppingDistance + k_arriveThreshold)
        {
            SetEscapePoint();
        }
    }

    public override void Exit()
    {
        m_owner.Agent.speed = m_baseSpeed;
        if (m_owner.Agent.isOnNavMesh)
            m_owner.Agent.ResetPath();
    }

    /// <summary>소란 지점 반대 방향 ±지터 안에서 NavMesh 위 도주 지점을 뽑는다.</summary>
    private void SetEscapePoint()
    {
        Vector3 away = (m_owner.transform.position - m_owner.PanicSource).normalized;
        if (away.sqrMagnitude < 0.01f)
            away = Random.insideUnitSphere; // 소란 지점과 완전히 겹치면 방향이 없으므로 아무 방향으로
        away.y = 0f;

        for (int i = 0; i < k_maxSampleAttempts; i++)
        {
            // 시도가 거듭될수록 지터를 키운다 — 막다른 방향이면 점점 옆길을 찾게 된다
            float jitter = Random.Range(-k_directionJitterDegrees, k_directionJitterDegrees) * (1f + i * 0.5f);
            Vector3 direction = Quaternion.Euler(0f, jitter, 0f) * away;
            Vector3 candidate = m_owner.transform.position + direction * m_config.StepDistance;

            if (NavMesh.SamplePosition(candidate, out NavMeshHit hit, 2f, NavMesh.AllAreas))
            {
                m_owner.Agent.SetDestination(hit.position);
                return;
            }
        }
    }
}
