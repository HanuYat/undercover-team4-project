using UnityEngine;

/// <summary>
/// 기절(Stunned) 상태 — 테이저 등 무력화 수단의 연결고리. (GDD 7-4/8-3, #76)
/// 지속 시간 동안 완전 무방비로 멈추며, 이 동안 수갑을 채우면 반응 없이 즉시 연행된다.
/// 시간이 지나면 일어나(#269 StandUp 모션) 체력을 회복하고 배회로 돌아간다 (#366 — 도주 폐지).
/// 진입은 NpcController.EnterStunned() — 테이저와 체력 0 도달(NpcController.SetHp)이 호출한다.
/// </summary>
public class NpcStunnedState : NpcStateBase
{
    private float m_timer;

    // 일어나는 모션을 이미 시작했는가 — 기절 시간의 마지막 구간에서 한 번만 발행한다 (#269)
    private bool m_standingUp;

    private readonly NpcStunConfig m_config;

    public NpcStunnedState(NpcController owner, NpcStunConfig config) : base(owner)
    {
        m_config = config;
    }

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
        float standUpAt = Mathf.Max(0f, m_config.StunSeconds - m_config.StandUpSeconds);
        if (!m_standingUp && m_timer >= standUpAt)
        {
            m_standingUp = true;
            m_owner.RaiseStandUp(); // 전 피어에 일어나는 모션 재생을 알린다
        }

        // 깨어나면 배회로 돌아간다 — 도주하지 않는다 (#366 결정 5, #269의 도주 복귀를 대체).
        //
        // 체력 회복이 이 지점의 핵심이다: HP 0 → 기절은 '0에 도달하는 순간'만 걸리는 엣지
        // 트리거라(NpcController.SetHp), 0인 채로 깨어나면 두 번 다시 기절하지 않는 무적이 된다.
        // 회복을 상태 진입(Enter)이 아니라 여기 두는 이유는, 기절해 있는 동안에는 HP 0이
        // 유지돼야 "기절 중 추가 타격이 타이머를 리셋하지 않는다"는 성질이 성립하기 때문이다.
        if (m_timer >= m_config.StunSeconds)
        {
            m_owner.ServerRestoreHp();
            m_owner.ClearThreat(); // 도주하지 않으므로 위협 참조를 남길 이유가 없다
            m_owner.StateMachine.ChangeState(NpcState.Idle);
        }
    }

    public override void Exit()
    {
        m_owner.Agent.isStopped = false;
    }
}
