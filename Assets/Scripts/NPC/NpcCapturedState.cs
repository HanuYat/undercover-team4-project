/// <summary>
/// 체포(Captured) 상태 — 수갑 채널링 완료 시 진입한다. (GDD 7-4, 이슈 #36)
/// 이동·배회를 완전히 멈추며, 스스로 다른 상태로 전이하지 않는 최종 상태다.
/// </summary>
public class NpcCapturedState : NpcStateBase
{
    public NpcCapturedState(NpcController owner) : base(owner) { }

    public override void Enter()
    {
        // 이동을 멈추고 진행 중이던 배회 경로도 제거한다
        m_owner.Agent.isStopped = true;
        if (m_owner.Agent.isOnNavMesh)
            m_owner.Agent.ResetPath();
    }

    public override void Tick()
    {
        // 체포는 최종 상태 — Idle처럼 타이머로 빠져나가지 않는다
    }

    public override void Exit()
    {
        // 향후 이송·석방 등으로 풀릴 경우를 대비해 이동을 복구한다
        m_owner.Agent.isStopped = false;
    }
}
