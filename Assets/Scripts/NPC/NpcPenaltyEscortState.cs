using UnityEngine;

/// <summary>
/// 오검거 호송(PenaltyEscorting) 상태 — 포획된 플레이어를 광장(PlazaPoint)까지 끌고 간다. (#279)
/// 대형은 WrongfulArrestPenalty가 짠다: 선두(leader=null)는 광장으로 직접 걷고,
/// 나머지는 선두 기준 로컬 오프셋 위치를 따라간다 — 선두 옆(양옆 끌기 2명)과 뒤(뒤따름).
/// 포획된 플레이어 본인은 오너 클라이언트가 끌기 담당 2명 사이를 추종한다(PlayerMovement.BeginCarriedFollow) —
/// NetworkTransform 오너 권한이라 서버(NPC)가 직접 못 끌기 때문. 광장 도착 판정은 매니저가 선두 거리로 한다.
/// </summary>
public class NpcPenaltyEscortState : NpcStateBase
{
    private const float k_repathInterval = 0.15f; // 추종 오프셋 갱신 간격(초) — 선두가 움직이므로 연행보다 촘촘히
    private const float k_leaderStopDistance = 1.2f; // 선두의 광장 앞 정지 거리(m)

    private float m_baseSpeed;
    private float m_repathTimer;

    public NpcPenaltyEscortState(NpcController owner)
        : base(owner) { }

    public override void Enter()
    {
        m_baseSpeed = m_owner.Agent.speed; // 호송은 걷는 속도 — 추격 가속을 쓰지 않는다 (질질 끌고 가는 그림)
        m_repathTimer = 0f;

        m_owner.Agent.isStopped = false;
        m_owner.Agent.stoppingDistance =
            m_owner.PenaltyEscortLeader == null ? k_leaderStopDistance : 0.1f;
    }

    public override void Exit()
    {
        m_owner.Agent.speed = m_baseSpeed;
        if (m_owner.Agent.isOnNavMesh)
        {
            m_owner.Agent.isStopped = false;
            m_owner.Agent.ResetPath();
        }
    }

    public override void Tick()
    {
        m_repathTimer -= Time.deltaTime;
        if (m_repathTimer > 0f)
            return;
        m_repathTimer = k_repathInterval;

        NpcController leader = m_owner.PenaltyEscortLeader;

        // 선두가 없거나 소실(파괴)됐으면 스스로 광장으로 걷는다 — 호송이 길에서 멈추지 않게
        if (leader == null)
        {
            if (m_owner.PenaltyEscortGoal != null)
                m_owner.Agent.SetDestination(m_owner.PenaltyEscortGoal.position);
            return;
        }

        // 추종 — 선두 기준 로컬 오프셋 위치를 목표로 잡는다 (양옆 끌기·뒤따름 대형)
        Vector3 spot = leader.transform.TransformPoint(m_owner.PenaltyEscortOffset);
        m_owner.Agent.SetDestination(spot);

        // 선두보다 뒤처지면 살짝 빠르게 따라잡는다 — 대형이 길게 늘어지는 것 방지 (연행 부스트 관례)
        float lag = Vector3.Distance(m_owner.transform.position, spot);
        m_owner.Agent.speed =
            lag > m_owner.EscortBoostDistance
                ? m_baseSpeed * m_owner.EscortBoostMultiplier
                : m_baseSpeed;
    }
}
