using UnityEngine;

/// <summary>
/// 체포(Captured) 상태 — 밧줄 끌기를 놓거나(E) 도주 제압 완료 시 진입한다. (GDD 7-4, 이슈 #36/#369)
/// 이동·배회를 완전히 멈춘다.
///
/// 인계 방치 타이머를 든다 (GDD 7-6, #230): 이 상태로 <see cref="NpcCapturedConfig.EscapeSeconds"/>가
/// 지나도록 인계되지 않으면 밧줄을 풀고 도주한다 — "일단 다 잡아놓고 나중에 인계" 전략 차단.
/// 판정이 끝난(<see cref="NpcController.IsDelivered"/>) NPC는 제외한다 — 본부에서 탈출하면 안 되고,
/// 그 뒤 처리는 유치장(#228) 몫이다.
/// </summary>
public class NpcCapturedState : NpcStateBase
{
    // 도주 예정 시각(Time.time 기준). Enter마다 다시 잡히므로 "Captured 진입할 때마다 리셋"이 공짜로 성립한다 —
    // 타이머를 미루려면 직접 걸어와 재연행(E)해야 하니 꼼수 가치가 낮다.
    private float m_escapeTime;

    private readonly NpcCapturedConfig m_config;

    public NpcCapturedState(NpcController owner, NpcCapturedConfig config)
        : base(owner)
    {
        m_config = config;
    }

    public override void Enter()
    {
        // 이동을 멈추고 진행 중이던 배회 경로도 제거한다
        m_owner.Agent.isStopped = true;
        if (m_owner.Agent.isOnNavMesh)
            m_owner.Agent.ResetPath();

        m_escapeTime = Time.time + m_config.EscapeSeconds;
    }

    public override void Tick()
    {
        // 판정 완료 = 인계 성공. 본부에 얌전히 남는다 (#230)
        if (m_owner.IsDelivered)
            return;

        float remaining = m_escapeTime - Time.time;

        if (remaining <= 0f)
        {
            Escape();
            return;
        }
    }

    public override void Exit()
    {
        // 향후 이송·석방 등으로 풀릴 경우를 대비해 이동을 복구한다
        m_owner.Agent.isStopped = false;
    }

    /// <summary>방치 타이머 만료 — 밧줄을 풀고 달아난다.</summary>
    private void Escape()
    {
        // 밧줄은 소모형이 아니라 반환할 자원이 없다 — 상태 전이만으로 풀려난다. (#369)

        // 가장 가까운 플레이어를 위협 삼아 도주한다 — 반경은 저항 폴백(#205)·도주 회피(#213)와 같은
        // ThreatSearchRadius를 쓴다. 기준이 어긋나면 "도망칠 상대"와 "피할 상대"가 달라진다.
        PlayerHealth nearest = SuddenEventUtil.FindNearestFieldPlayer(
            m_owner.transform.position,
            m_owner.ThreatSearchRadius
        );

        if (nearest != null)
        {
            Debug.Log($"인계 방치 — 밧줄 풀고 도주: {m_owner.name}");
            m_owner.StartFlee(nearest.transform);
            return;
        }

        // 근처에 아무도 없으면 도망칠 이유도 없다 — 조용히 밧줄 풀고 배회로 복귀(사실상 탈출).
        // NpcResistState.Defeat의 폴백과 같은 패턴.
        Debug.Log($"인계 방치 — 밧줄 풀고 배회 복귀(주변에 플레이어 없음): {m_owner.name}");
        m_owner.StateMachine.ChangeState(NpcState.Idle);
    }
}
