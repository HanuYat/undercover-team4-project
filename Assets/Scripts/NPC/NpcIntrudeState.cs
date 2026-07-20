using UnityEngine;

/// <summary>
/// 침입(Intruding) 상태 — 돌발 이벤트가 스폰한 침입자가 목표 지점까지 걸어가 멈춘다. (GDD 6-4, #231)
/// 이동과 도착을 한 상태 안에서 처리하는 구조는 수감(NpcJailedState, #228)과 같다.
///
/// 수감과 다른 점은 <b>경로 실패를 도착으로 처리하지 않는다</b>는 것이다 — 수감은 그 자리에서 수용해도
/// 결과가 같지만, 침입은 자물쇠에 닿지 못했는데 도착으로 치면 열리면 안 될 자물쇠가 열린다.
/// 실패는 실패로 통보하고 이벤트가 불발로 정리한다.
/// </summary>
public class NpcIntrudeState : NpcStateBase
{
    // 목표 지점에 이만큼(m) 다가오면 도착으로 본다 — NpcJailedState와 같은 기준
    private const float k_arriveDistance = 0.5f;

    private bool m_finished; // 도착·실패 통보를 한 번만 보내기 위한 래치

    public NpcIntrudeState(NpcController owner) : base(owner) { }

    public override void Enter()
    {
        m_finished = false;
        m_owner.Agent.isStopped = false;
        m_owner.Agent.stoppingDistance = 0f;

        if (m_owner.IntrudeTarget == null)
        {
            Debug.LogWarning($"NpcIntrudeState: 침입 목표가 없음 — 불발 처리: {m_owner.name}", m_owner);
            Finish(false);
            return;
        }

        // 경로를 못 잡으면(목표가 NavMesh 밖 등) 영원히 걷는 자세로 남는다 — 불발로 통보한다
        if (!m_owner.Agent.SetDestination(m_owner.IntrudeTarget.position))
        {
            Debug.LogWarning($"NpcIntrudeState: 침입 경로 실패 — 불발 처리: {m_owner.name}", m_owner);
            Finish(false);
        }
    }

    public override void Tick()
    {
        if (m_finished)
            return;

        if (m_owner.Agent.pathPending)
            return;

        if (m_owner.Agent.remainingDistance > k_arriveDistance)
            return;

        Finish(true);
    }

    public override void Exit()
    {
        // 도주(StartFlee) 등으로 이 상태를 벗어날 때 이동을 복구한다 (NpcJailedState.Exit과 동일)
        if (m_owner.Agent.isOnNavMesh)
        {
            m_owner.Agent.isStopped = false;
            m_owner.Agent.ResetPath();
        }
    }

    // 도착·실패 확정 — 그 자리에 세우고 이벤트에 결과를 알린다
    private void Finish(bool reached)
    {
        m_finished = true;

        m_owner.Agent.isStopped = true;
        m_owner.Agent.velocity = Vector3.zero;
        if (m_owner.Agent.isOnNavMesh)
            m_owner.Agent.ResetPath();

        m_owner.NotifyIntrudeFinished(reached);
    }
}
