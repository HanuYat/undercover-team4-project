using UnityEngine;

/// <summary>
/// 기절(Stunned) 상태 — 테이저 및 체력 0 도달의 연결고리. (GDD 7-4/8-3, #76/#366)
/// 지속 시간 동안 완전 무방비로 멈추며, 이 동안 수갑을 채우면 반응 없이 즉시 연행된다.
/// 시간이 지나면 일어나(#269 StandUp 모션) 스스로 도주한다 — 무력화가 풀린 대상은 그대로 서 있지
/// 않는다(#269 확정). #366 결정 5로 배회 복귀에 잠시 바뀌었다가 원복됐다.
///
/// <b>이제 이 상태로 들어오는 경로는 넉백 착지 KO 하나뿐이다</b> (#571) — 테이저와 체력 임계
/// 넉다운은 상태를 바꾸지 않는 오버레이(<see cref="NpcStun"/>)를 쓰고, 체력 0은 사망
/// (<see cref="NpcState.Dead"/>)으로 간다. 체력 회복은 제거됐다 — 이유는 <see cref="Exit"/> 참고.
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
            m_owner.RaiseStandUp(); // 전 피어에 일어나는 모션 재생을 알린다
        }

        // 깨어나면 배회가 아니라 도주다 (#269 확정 — #366 결정 5의 배회 복귀에서 원복).
        // 위협은 기절시킨 상대(테이저 사수·진압봉 타격자)이거나 밧줄로 끌고 다닌 플레이어다.
        // 위협이 null로 남는 경우에도 NpcFleeState가 EscapeDistance 안 추격자를 스캔해 폴백하므로,
        // 때린 플레이어가 옆에 있으면 그쪽에서 도망친다.
        // 주변에 아무도 없으면 도주 상태가 스스로 배회로 돌려보낸다 — 아무도 없는 곳에 두고 온
        // NPC가 혼자 전력 질주하지 않는다.
        if (m_timer >= m_config.StunSeconds + m_config.StandUpSeconds)
            m_owner.Reaction.StartFlee(m_owner.Reaction.ThreatTarget);
    }

    public override void Exit()
    {
        SetAgentStopped(false);

        // <b>체력을 회복하지 않는다</b> (#571). 여기는 원래 회복 지점이었고, 근거는 "이 상태를
        // 벗어나는 경로가 시간 만료만이 아니다(수갑 채포·석방·방치 만료) — 어딘가에서 회복하지
        // 않으면 HP 0인 채로 빠져나가고, 0 도달 엣지가 이미 0인 값에는 다시 걸리지 않아 그 NPC가
        // 라운드 내내 무적이 된다"였다.
        //
        // <b>그 근거가 사라졌다.</b> HP 0은 이제 깨어나는 상태가 아니라 사망(NpcState.Dead)이라
        // 애초에 이 상태로 들어오지 않는다. 여기 남는 것은 넉백 착지 KO뿐인데 그쪽은 HP를 깎지도
        // 않으므로 회복할 것이 없다.
    }

    /// <summary>
    /// 에이전트 정지를 <b>꺼져 있거나 NavMesh 밖일 때는 건너뛴다</b> — 그 상태에서 <c>isStopped</c>를
    /// 만지면 Unity가 에러를 뱉는다(<see cref="NpcStun.EnterStunned"/>·<c>NpcController.SetFrozen</c>과
    /// 같은 가드, #557).
    ///
    /// 예전에는 가드 없이 대입했다. 진입·이탈 시점에 에이전트가 늘 살아 있었기 때문인데, 사망(#571)이
    /// 그 전제를 깼다: 넉백 비행 중(에이전트 꺼짐 + 상태는 이미 Stunned)에 죽으면
    /// <see cref="NpcDeath.ServerEnterDead"/>의 사망 전이가 <b>꺼진 에이전트를 든 채로</b> 이 Exit을
    /// 부른다.
    /// </summary>
    private void SetAgentStopped(bool stopped)
    {
        UnityEngine.AI.NavMeshAgent agent = m_owner.Agent;
        if (agent == null || !agent.enabled || !agent.isOnNavMesh)
            return;

        agent.isStopped = stopped;
        if (stopped)
            agent.ResetPath();
    }
}
