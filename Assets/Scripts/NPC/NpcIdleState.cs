using UnityEngine;

public class NpcIdleState : NpcStateBase
{
    private float m_waitTimer;

    public NpcIdleState(NpcController owner) : base(owner) { }

    public override void Enter()
    {
        m_owner.Agent.isStopped = true;
        m_waitTimer = Random.Range(m_owner.IdleTimeMin, m_owner.IdleTimeMax);
    }

    public override void Tick()
    {
        m_waitTimer -= Time.deltaTime;
        if (m_waitTimer <= 0f)
            m_owner.StateMachine.ChangeState(NpcState.Walk);
    }

    public override void Exit()
    {
        m_owner.Agent.isStopped = false;
    }
}
