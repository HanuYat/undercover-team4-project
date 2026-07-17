using UnityEngine;

/// <summary>
/// 수감(Jailed) 상태 — 인계존 판정에서 진범·경범죄로 확정된 NPC가 유치장까지 걸어가 수용된다. (GDD 7-2, #228)
/// 이송("걸어가는 중")과 수용("도착해 멈춤")을 한 상태 안에서 처리한다 —
/// 연행(NpcEscortedState)이 추종과 근접 정지를 한 상태로 다루는 것과 같은 구조다 (#97).
/// 수용된 뒤에는 스스로 다른 상태로 전이하지 않는 최종 상태다 (탈출 이벤트는 별도 이슈).
/// </summary>
public class NpcJailedState : NpcStateBase
{
    // 수용 지점에 이만큼(m) 다가오면 도착으로 본다 — NavMesh 경로의 끝점 오차와 발 위치 차이를 흡수한다
    private const float k_arriveDistance = 0.5f;

    private bool m_admitted; // 수용 완료 — 도착 통보를 한 번만 보내기 위한 래치

    public NpcJailedState(NpcController owner) : base(owner) { }

    public override void Enter()
    {
        m_admitted = false;
        m_owner.Agent.isStopped = false;
        m_owner.Agent.stoppingDistance = 0f;

        // 유치장이 없는 테스트 씬 — 그 자리에서 수용된 것으로 처리한다 (멍하니 서 있지 않게)
        if (m_owner.JailCell == null)
        {
            Admit();
            return;
        }

        // 경로를 못 잡으면(수용 지점이 NavMesh 밖 등) 영원히 걷는 자세로 남는다 — 그 자리에서 수용 처리
        if (!m_owner.Agent.SetDestination(m_owner.JailCell.position))
        {
            Debug.LogWarning($"NpcJailedState: 유치장 경로 실패 — 그 자리에서 수용 처리: {m_owner.name}", m_owner);
            Admit();
        }
    }

    public override void Tick()
    {
        if (m_admitted)
            return; // 수용 완료 — 최종 상태

        if (m_owner.Agent.pathPending)
            return;

        if (m_owner.Agent.remainingDistance > k_arriveDistance)
            return;

        Admit();
    }

    public override void Exit()
    {
        // 탈출(별도 이슈) 등으로 풀려날 경우를 대비해 이동을 복구한다
        if (m_owner.Agent.isOnNavMesh)
        {
            m_owner.Agent.isStopped = false;
            m_owner.Agent.ResetPath();
        }
    }

    // 수용 확정 — 그 자리에 세우고 유치장에 도착을 알린다 (JailZone이 수용 인원을 올린다)
    private void Admit()
    {
        m_admitted = true;

        m_owner.Agent.isStopped = true;
        m_owner.Agent.velocity = Vector3.zero; // 감속 관성까지 끊어 수용 지점을 지나쳐 밀리지 않게
        if (m_owner.Agent.isOnNavMesh)
            m_owner.Agent.ResetPath();

        m_owner.NotifyJailed();
    }
}
