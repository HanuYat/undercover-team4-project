using UnityEngine;

/// <summary>
/// 저항(Attack) 상태 — 수갑 채널링 성공 순간 그 자리에서 몸부림치며 버틴다. (GDD 6-1/7-4, #76)
/// ApplySubdueHit로 제압 게이지가 0이 되면 체포(Captured)된다 — 여럿이 때리면 빨리 끝난다(협동 인센티브).
/// 일정 시간 아무도 제압을 시도하지 않으면 진정하고 배회로 복귀한다.
/// 타격 수단(진압봉)은 후속 아이템 이슈 — 여기는 게이지·전이만 담당한다.
/// </summary>
public class NpcResistState : NpcStateBase
{
    public NpcResistState(NpcController owner) : base(owner) { }

    public override void Enter()
    {
        // 이동을 멈추고 그 자리에서 버틴다
        m_owner.Agent.isStopped = true;
        if (m_owner.Agent.isOnNavMesh)
            m_owner.Agent.ResetPath();

        m_owner.ResetSubdueGauge();
    }

    public override void Tick()
    {
        // 게이지가 다 깎이면 제압 성공 — 체포
        if (m_owner.SubdueGauge <= 0f)
        {
            Debug.Log($"저항 제압됨: {m_owner.name}");
            m_owner.StateMachine.ChangeState(NpcState.Captured);
            return;
        }

        // 아무도 제압을 시도하지 않으면 진정하고 배회로 복귀 (여전히 범인이므로 재시도 가능)
        if (Time.time - m_owner.LastSubdueHitTime > m_owner.ResistCalmSeconds)
        {
            Debug.Log($"저항 진정 — 배회 복귀: {m_owner.name}");
            m_owner.StateMachine.ChangeState(NpcState.Idle);
        }
    }

    public override void Exit()
    {
        m_owner.Agent.isStopped = false;
    }
}
