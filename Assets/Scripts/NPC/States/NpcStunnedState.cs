using UnityEngine;

/// <summary>
/// 기절(Stunned) 상태 — 테이저 및 체력 0 도달의 연결고리. (GDD 7-4/8-3, #76/#366)
/// 지속 시간 동안 완전 무방비로 멈추며, 이 동안 수갑을 채우면 반응 없이 즉시 연행된다.
/// 시간이 지나면 일어나(#269 StandUp 모션) 스스로 도주한다 — 무력화가 풀린 대상은 그대로 서 있지
/// 않는다(#269 확정). #366 결정 5로 배회 복귀에 잠시 바뀌었다가 원복됐다.
///
/// <b>이 상태로 들어오는 경로는 넉백 착지 KO 하나뿐이다</b> (#571) — 테이저와 체력 0 쓰러짐은
/// 상태를 바꾸지 않는 오버레이(<see cref="NpcStun"/>)를 쓴다.
/// </summary>
public class NpcStunnedState : NpcStateBase
{
    private float m_timer;

    // 일어나는 모션을 이미 시작했는가 — 기절 시간의 마지막 구간에서 한 번만 발행한다 (#269)
    private bool m_standingUp;

    private readonly NpcStunConfig m_config;

    public NpcStunnedState(NpcController owner, NpcStunConfig config)
        : base(owner)
    {
        m_config = config;
    }

    public override void Enter()
    {
        m_timer = 0f;
        m_standingUp = false;
        m_owner.Stun.SetRising(false);
        SetAgentStopped(true);
    }

    public override void Tick()
    {
        // 밧줄로 묶이면 커스터디(Escorted)로 넘어가므로 여기서 끌기를 볼 일이 없다 —
        // 기절 타이머 정지 분기는 그 전이와 함께 제거됐다. (#369)
        m_timer += Time.deltaTime;

        // <b>기상은 기절이 다 끝난 뒤에 덧붙는다</b> (#572 후속) — 오버레이 경로(<see cref="NpcStun"/>)와
        // 같은 구조다. 예전에는 마지막 구간을 잘라 썼는데, 그러면 <b>클립 길이가 곧 누워 있는 시간을
        // 깎는다</b>: 기상을 정상 속도로 늦추자(0.585 → 1.17초) 이 경로의 무력화 창도 함께 줄었다.
        // 이 구간에도 상태는 Stunned라 움직이지 않는다.
        if (!m_standingUp && m_timer >= m_config.StunSeconds)
        {
            m_standingUp = true;
            m_owner.Stun.SetRising(true); // #624 — 이 구간에도 CanRopeBind를 막는다
            m_owner.RaiseStandUp(); // 전 피어에 일어나는 모션 재생을 알린다
        }

        // 깨어나면 배회가 아니라 도주다 (#269 확정 — #366 결정 5의 배회 복귀에서 원복).
        // 위협은 기절시킨 상대(테이저 사수·진압봉 타격자)이거나 밧줄로 끌고 다닌 플레이어다.
        // 위협이 null로 남는 경우에도 NpcFleeState가 EscapeDistance 안 추격자를 스캔해 폴백하므로,
        // 때린 플레이어가 옆에 있으면 그쪽에서 도망친다.
        // 주변에 아무도 없으면 도주 상태가 스스로 배회로 돌려보낸다 — 아무도 없는 곳에 두고 온
        // NPC가 혼자 전력 질주하지 않는다.
        // 질주하는 개체(공연음란범)만 도주가 아니라 질주로 돌아간다 — 한 대 맞았다고 그만두지 않는다 (#106).
        if (m_timer >= m_config.StunSeconds + m_config.StandUpSeconds)
        {
            // 넉백을 부르는 폭발이 체력도 깎으므로 HP 0인 몸이 여기 들어올 수 있다 (#916)
            m_owner.Health.ServerRestoreToOne();
            m_owner.Reaction.ResumeReaction(m_owner.Reaction.ThreatTarget);
        }
    }

    public override void Exit()
    {
        SetAgentStopped(false);
        m_owner.Stun.SetRising(false); // #624 — 이 상태를 벗어나는 모든 경로에서 rising을 걷는다

        // 기상 회복은 여기가 아니라 Tick의 만료 지점이다 — Exit은 사망 전이도 태운다 (#916)
    }

    /// <summary>
    /// 에이전트를 만질 수 없으면 건너뛴다(<see cref="NpcController.AgentReady"/>).
    ///
    /// 예전에는 가드 없이 대입했다. 진입·이탈 시점에 에이전트가 늘 살아 있었기 때문인데, 사망(#571)이
    /// 그 전제를 깼다: 넉백 비행 중(에이전트 꺼짐 + 상태는 이미 Stunned)에 죽으면
    /// <see cref="NpcDeath.ServerEnterDead"/>의 사망 전이가 <b>꺼진 에이전트를 든 채로</b> 이 Exit을
    /// 부른다.
    /// </summary>
    private void SetAgentStopped(bool stopped)
    {
        if (!m_owner.AgentReady)
            return;

        UnityEngine.AI.NavMeshAgent agent = m_owner.Agent;
        agent.isStopped = stopped;
        if (stopped)
            agent.ResetPath();
    }
}
