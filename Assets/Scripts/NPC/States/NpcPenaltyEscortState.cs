using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// 오검거 호송(PenaltyEscorting) 상태 — 포획된 플레이어를 광장(PlazaPoint)까지 끌고 간다. (#279)
/// 대형은 WrongfulArrestPenalty가 짠다: 선두(leader=null)는 광장으로 직접 걷고,
/// 나머지는 선두 기준 로컬 오프셋 위치를 따라간다 — 선두 옆(양옆 끌기 2명)과 뒤(뒤따름).
/// 포획된 플레이어 본인은 오너 클라이언트가 끌기 담당 2명 사이를 추종한다(PlayerTowedMotion.BeginEscortFollow) —
/// NetworkTransform 오너 권한이라 서버(NPC)가 직접 못 끌기 때문. 광장 도착 판정은 매니저가 선두 거리로 한다.
///
/// <b>납치(#371)만</b> 오프셋 목적지를 NavMesh에 스냅한다(<see cref="ResolveFollowSpot"/>) — 좁은 통로에서
/// 벽 안쪽으로 떨어진 오프셋을 향해 벽을 밀며 거의 정지하는 문제 때문이다 (#902). 오검거 광장 경로는
/// 이 함정을 밟은 적이 없어 그대로 둔다 — 같은 클래스를 공유하지만 임무 종류로 갈린다.
/// </summary>
public class NpcPenaltyEscortState : NpcStateBase
{
    private const float k_leaderStopDistance = 1.2f; // 선두의 광장 앞 정지 거리(m)

    // 오프셋이 벽 안쪽으로 떨어졌을 때 되돌아올 NavMesh 탐색 반경(m) — 납치 전용 (#902)
    private const float k_offsetSnapRadius = 2f;

    // 도달 가능성 프로브 — 서버(또는 오프라인) 전용 FSM에서만 도므로 공유해도 안전하다
    // (NpcMovePoint.s_pathProbe와 같은 관례).
    private static NavMeshPath s_reachabilityProbe;

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

        m_owner.SetAgentStopped(false);
        m_owner.Agent.stoppingDistance =
            m_owner.Penalty.PenaltyEscortLeader == null ? k_leaderStopDistance : 0.1f;
    }

    public override void Exit()
    {
        m_owner.Agent.speed = m_baseSpeed;
        if (m_owner.Agent.isOnNavMesh)
        {
            m_owner.SetAgentStopped(false);
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
        Vector3 spot = ResolveFollowSpot(leader, out bool reachable);
        m_owner.Agent.SetDestination(spot);

        // 선두보다 뒤처지면 살짝 빠르게 따라잡는다 — 대형이 길게 늘어지는 것 방지 (연행 부스트 관례)
        // 도달 불가 목표(아래 폴백)에는 부스트를 걸지 않는다 — lag이 줄어들 수 없는 목표라 부스트
        // 조건이 계속 참으로 남아 벽만 더 세게 밀게 된다 (#902).
        float lag = Vector3.Distance(m_owner.transform.position, spot);
        m_owner.Agent.speed =
            reachable && lag > m_config.BoostDistance ? m_baseSpeed * m_config.BoostMultiplier : m_baseSpeed;
    }

    // 대형 오프셋의 실제 목적지 — 납치(#371)만 NavMesh 위인지, 그리고 지금 위치에서 <b>끊기지 않는
    // 경로</b>로 갈 수 있는지 확인한다(NpcMovePoint의 "부분 경로는 도달 불가로 본다" 규칙과 같다,
    // #661). 좁은 통로에서 오프셋이 벽 안쪽에 떨어지면 도달 불가로 보고 선두 위치로 물러난다 —
    // 폴백이 선두 자리라 자연히 한 줄 대형이 된다. (#902)
    //
    // 오검거는 광장까지 넓은 길만 다녀 이 함정을 밟은 적이 없으므로 손대지 않는다 — 오프셋을
    // 그대로 쓴다(지금까지 동작 그대로).
    private Vector3 ResolveFollowSpot(NpcController leader, out bool reachable)
    {
        reachable = true;
        Vector3 offset = m_owner.Penalty.PenaltyEscortOffset;
        Vector3 spot = leader.transform.TransformPoint(offset);

        if (!m_owner.Penalty.IsAbductionDuty || offset == Vector3.zero)
            return spot; // 오검거이거나 선두 본인(오프셋 없음) — 지금까지 그대로

        if (NavMesh.SamplePosition(spot, out NavMeshHit hit, k_offsetSnapRadius, m_owner.Agent.areaMask)
            && HasCompletePath(m_owner.Agent, hit.position))
        {
            return hit.position;
        }

        reachable = false;
        return leader.transform.position;
    }

    private static bool HasCompletePath(NavMeshAgent agent, Vector3 destination)
    {
        s_reachabilityProbe ??= new NavMeshPath();
        return NavMesh.CalculatePath(agent.transform.position, destination, agent.areaMask, s_reachabilityProbe)
            && s_reachabilityProbe.status == NavMeshPathStatus.PathComplete;
    }
}
