using UnityEngine;

public class NpcIdleState : NpcStateBase
{
    private float m_waitTimer;

    private readonly NpcIdleConfig m_config;

    public NpcIdleState(NpcController owner, NpcIdleConfig config) : base(owner)
    {
        m_config = config;
    }

    public override void Enter()
    {
        // 넉백 비행 등으로 에이전트가 꺼진 채 Idle로 강제 전이될 수 있다 — 다른 State와 같은 가드.
        if (m_owner.Agent.isOnNavMesh)
            m_owner.SetAgentStopped(true);

        // 가끔은 구경하듯 오래 멈춰 서 있는다 — 걷다 서다 리듬이 단조로워지는 것을 방지
        bool isLongIdle = Random.value < m_config.LongIdleChance;
        m_waitTimer = isLongIdle
            ? Random.Range(m_config.LongIdleTimeMin, m_config.LongIdleTimeMax)
            : Random.Range(m_config.IdleTimeMin, m_config.IdleTimeMax);
    }

    public override void Tick()
    {
        m_waitTimer -= Time.deltaTime;
        if (m_waitTimer <= 0f)
            m_owner.StateMachine.ChangeState(NpcState.Walk);
    }

    public override void Exit()
    {
        if (m_owner.Agent.isOnNavMesh)
            m_owner.SetAgentStopped(false);
    }
}
