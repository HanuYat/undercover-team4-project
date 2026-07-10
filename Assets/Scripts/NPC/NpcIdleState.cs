using UnityEngine;

public class NpcIdleState : NpcStateBase
{
    private float m_waitTimer;

    public NpcIdleState(NpcController owner) : base(owner) { }

    public override void Enter()
    {
        m_owner.Agent.isStopped = true;

        // 가끔은 구경하듯 오래 멈춰 서 있는다 — 걷다 서다 리듬이 단조로워지는 것을 방지
        bool isLongIdle = Random.value < m_owner.LongIdleChance;
        m_waitTimer = isLongIdle
            ? Random.Range(m_owner.LongIdleTimeMin, m_owner.LongIdleTimeMax)
            : Random.Range(m_owner.IdleTimeMin, m_owner.IdleTimeMax);
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
