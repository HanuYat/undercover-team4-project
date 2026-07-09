using UnityEngine;
using UnityEngine.AI;

public class NpcWalkState : NpcStateBase
{
    private const int k_maxSampleAttempts = 10;
    private const float k_arriveThreshold = 0.5f;

    public NpcWalkState(NpcController owner) : base(owner) { }

    public override void Enter()
    {
        SetNextWanderPoint();
    }

    public override void Tick()
    {
        // 목적지에 도착하면 Idle로 전환해 잠깐 쉬었다가 다시 걷는다
        if (!m_owner.Agent.pathPending &&
            m_owner.Agent.remainingDistance <= m_owner.Agent.stoppingDistance + k_arriveThreshold)
        {
            m_owner.StateMachine.ChangeState(NpcState.Idle);
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
            Vector2 randomCircle = Random.insideUnitCircle * m_owner.WanderRadius;
            Vector3 candidate = m_owner.transform.position + new Vector3(randomCircle.x, 0f, randomCircle.y);

            if (NavMesh.SamplePosition(candidate, out NavMeshHit hit, 2f, NavMesh.AllAreas))
            {
                m_owner.Agent.SetDestination(hit.position);
                return;
            }
        }
    }
}
