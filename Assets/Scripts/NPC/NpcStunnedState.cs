using UnityEngine;

/// <summary>
/// 기절(Stunned) 상태 — 테이저 등 무력화 수단의 연결고리. (GDD 7-4/8-3, #76)
/// 지속 시간 동안 완전 무방비로 멈추며, 이 동안 수갑을 채우면 반응 없이 즉시 연행된다.
/// 시간이 지나면 스스로 배회로 복귀한다. 진입은 NpcController.EnterStunned() —
/// 테이저 아이템(후속 이슈)이 호출한다.
/// </summary>
public class NpcStunnedState : NpcStateBase
{
    private float m_timer;

    public NpcStunnedState(NpcController owner) : base(owner) { }

    public override void Enter()
    {
        m_timer = 0f;
        m_owner.Agent.isStopped = true;
        if (m_owner.Agent.isOnNavMesh)
            m_owner.Agent.ResetPath();
    }

    public override void Tick()
    {
        m_timer += Time.deltaTime;
        if (m_timer >= m_owner.StunSeconds)
            m_owner.StateMachine.ChangeState(NpcState.Idle);
    }

    public override void Exit()
    {
        m_owner.Agent.isStopped = false;
    }
}
