using UnityEngine;

/// <summary>
/// 기절(Stunned) 상태 — 테이저 등 무력화 수단의 연결고리. (GDD 7-4/8-3, #76)
/// 지속 시간 동안 완전 무방비로 멈추며, 이 동안 수갑을 채우면 반응 없이 즉시 연행된다.
/// 시간이 지나면 일어나(#269 StandUp 모션) 스스로 도주한다. 진입은 NpcController.EnterStunned() —
/// 테이저 아이템(후속 이슈)이 호출한다.
/// </summary>
public class NpcStunnedState : NpcStateBase
{
    private float m_timer;

    // 일어나는 모션을 이미 시작했는가 — 기절 시간의 마지막 구간에서 한 번만 발행한다 (#269)
    private bool m_standingUp;

    public NpcStunnedState(NpcController owner) : base(owner) { }

    public override void Enter()
    {
        m_timer = 0f;
        m_standingUp = false;
        m_owner.Agent.isStopped = true;
        if (m_owner.Agent.isOnNavMesh)
            m_owner.Agent.ResetPath();
    }

    public override void Tick()
    {
        // 밧줄로 묶여 있는 동안엔 기절 타이머를 멈춘다 — 끌려가는 내내 깨어나지 않는다 (#269)
        if (m_owner.IsRoped)
        {
            // 일어나려던 참에 묶였으면 다시 누운 것으로 되돌린다 — 표현(드라이버)도 끌기를 보고 누운 모션으로 복귀한다
            m_standingUp = false;
            return;
        }

        m_timer += Time.deltaTime;

        // 기절 시간의 마지막 구간을 일어나는 모션에 쓴다 — 총 무력화 시간(StunSeconds)은 그대로 두고
        // "누워 있다 → 일어난다 → 배회"가 이어지게 한다. 이 구간에도 상태는 Stunned라 움직이지 않는다.
        float standUpAt = Mathf.Max(0f, m_owner.StunSeconds - m_owner.StandUpSeconds);
        if (!m_standingUp && m_timer >= standUpAt)
        {
            m_standingUp = true;
            m_owner.RaiseStandUp(); // 전 피어에 일어나는 모션 재생을 알린다
        }

        // 깨어나면 배회가 아니라 도주다 — 무력화가 풀린 용의자는 그대로 서 있지 않는다 (#269 확정).
        // 위협은 기절시킨 상대(테이저 사수)이거나, 밧줄로 끌고 다닌 플레이어다.
        // 주변에 추격자가 아무도 없으면 도주 상태가 스스로 배회로 돌려보낸다(NpcFleeState) —
        // 아무도 없는 곳에 두고 온 NPC가 혼자 전력 질주하지 않는다.
        if (m_timer >= m_owner.StunSeconds)
            m_owner.StartFlee(m_owner.ThreatTarget);
    }

    public override void Exit()
    {
        m_owner.Agent.isStopped = false;
    }
}
