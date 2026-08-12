using UnityEngine;

/// <summary>
/// 오검거 호송(PenaltyEscorting) 상태 — 포획된 플레이어를 광장(PlazaPoint)까지 끌고 간다. (#279)
/// 대형은 WrongfulArrestPenalty가 짠다: 선두(leader=null)는 광장으로 직접 걷고,
/// 나머지는 선두 기준 로컬 오프셋 위치를 따라간다 — 선두 옆(양옆 끌기 2명)과 뒤(뒤따름).
/// 포획된 플레이어 본인은 오너 클라이언트가 끌기 담당 2명 사이를 추종한다(PlayerTowedMotion.BeginEscortFollow) —
/// NetworkTransform 오너 권한이라 서버(NPC)가 직접 못 끌기 때문. 광장 도착 판정은 매니저가 선두 거리로 한다.
/// </summary>
public class NpcPenaltyEscortState : NpcStateBase
{
    private const float k_leaderStopDistance = 1.2f; // 선두의 광장 앞 정지 거리(m)

    private float m_baseSpeed;

    private readonly NpcEscortConfig m_config;

    public NpcPenaltyEscortState(NpcController owner, NpcEscortConfig config)
        : base(owner)
    {
        m_config = config;
    }

    public override void Enter()
    {
        m_baseSpeed = m_owner.Agent.speed; // 호송은 걷는 속도 — 추격 가속을 쓰지 않는다 (질질 끌고 가는 그림)
        m_owner.Repath.ForceDue(NpcRepathChannel.Repath); // 진입 직후 1회는 바로 잡는다

        m_owner.Agent.isStopped = false;
        m_owner.Agent.stoppingDistance =
            m_owner.Penalty.PenaltyEscortLeader == null ? k_leaderStopDistance : 0.1f;
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
        if (!m_owner.Repath.Due(NpcRepathChannel.Repath))
            return;

        NpcController leader = m_owner.Penalty.PenaltyEscortLeader;

        // 선두가 소실(파괴)됐거나 <b>임무를 벗었으면</b> 스스로 목적지로 걷는다 — 호송이 길에서 멈추지 않게.
        // 임무 이탈까지 보는 이유는 납치 구조(#371) 때문이다: 선두만 맞아 떨어지면 그는 배회 시민으로
        // 돌아가는데, 파괴된 것은 아니라 예전 판정(null)에는 걸리지 않았다 — 남은 하나가 배회하는 시민을
        // 따라다니며 플레이어를 도시 여기저기로 끌고 다녔다.
        // EndPenaltyDuty가 지우는 PenaltyEscortGoal로 판별한다 (CarryEscortSequence의 해체 판정과 같은 기준).
        if (leader == null || leader.Penalty.PenaltyEscortGoal == null)
        {
            if (m_owner.Penalty.PenaltyEscortGoal != null)
                m_owner.Agent.SetDestination(m_owner.Penalty.PenaltyEscortGoal.position);
            return;
        }

        // 추종 — 선두 기준 로컬 오프셋 위치를 목표로 잡는다 (양옆 끌기·뒤따름 대형)
        Vector3 spot = leader.transform.TransformPoint(m_owner.Penalty.PenaltyEscortOffset);
        m_owner.Agent.SetDestination(spot);

        // 선두보다 뒤처지면 살짝 빠르게 따라잡는다 — 대형이 길게 늘어지는 것 방지 (연행 부스트 관례)
        float lag = Vector3.Distance(m_owner.transform.position, spot);
        m_owner.Agent.speed =
            lag > m_config.BoostDistance ? m_baseSpeed * m_config.BoostMultiplier : m_baseSpeed;
    }
}
