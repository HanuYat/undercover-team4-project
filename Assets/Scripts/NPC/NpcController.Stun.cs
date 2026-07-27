using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 스턴 오버레이 (#292) — 기절을 FSM 상태 전이가 아니라 <see cref="CurrentState"/> 위에 얹는
/// 동기화 플래그로 다룬다. 상태 enum이 바뀌지 않으므로 호송·수감·페널티 링크와 각 상태의
/// 타이머가 스턴에 끊기지 않고, 풀리면 하던 일을 그대로 재개한다.
///
/// 서버 권위 + 오프라인 폴백 — 서버(또는 오프라인)만 값을 바꾸고 클라이언트는 동기화 값을 읽는다.
/// m_networkState·m_syncedHp와 같은 이중 구조다 (#56 패턴).
///
/// <b>기절 경로는 둘이다.</b> 테이저와 체력 0(#366)은 이 오버레이를 쓰고, 넉백 착지는
/// <see cref="NpcState.Stunned"/> 상태 전이를 그대로 쓴다 — 넉백은 비행 전 상태 전이로 이전
/// 상태의 Exit()이 에이전트를 정리하게 만드는 구조라 오버레이로 옮기면 그 정리가 빠진다.
/// 그래서 "기절인가?"를 묻는 판정은 <see cref="NpcStateRules.IsIncapacitated"/>로 모은다.
/// </summary>
public partial class NpcController
{
    // 서버 권위 스턴 플래그 — 서버만 쓰고 모든 클라이언트가 읽는다. m_stunned가 서버·오프라인의 진실값.
    private readonly NetworkVariable<bool> m_syncedStunned = new NetworkVariable<bool>();
    private bool m_stunned;

    /// <summary>스턴 오버레이가 걸려 있는가. 세션 중에는 동기화 값이라 클라이언트에서도 읽을 수 있다. (#292)</summary>
    public bool IsStunned => IsSpawned ? m_syncedStunned.Value : m_stunned;

    // 서버 진실값과 동기화 변수에 함께 기록한다 — 오프라인에서는 NetworkVariable에 쓰지 않는다 (#56 상태 패턴과 동일)
    private void SetStunned(bool value)
    {
        m_stunned = value;
        if (IsSpawned && IsServer)
            m_syncedStunned.Value = value;
    }

    // 경과 시간으로 센다 — 밧줄에 묶이면 멈춰야 해서 "끝나는 시각" 방식으로는 계산이 지저분해진다.
    private float m_stunElapsed;
    private bool m_standingUp;          // 일어나는 모션을 이미 발행했는가 — 마지막 구간에서 1회만 (#269)
    private bool m_agentStoppedBefore;  // 스턴 직전의 isStopped — 해제 시 그대로 되돌린다

    /// <summary>
    /// 스턴 진입 — 상태를 바꾸지 않고 플래그만 켠다. 테이저와 체력 0 도달(#366)이 부른다.
    /// 넉백 착지는 이 경로가 아니라 NpcState.Stunned 전이를 쓴다 (NpcController.Knockback).
    /// </summary>
    /// <param name="threat">기절시킨 상대 — 깨어날 때 이 대상에게서 도주한다. null 허용.</param>
    public void EnterStunned(Transform threat = null)
    {
        if (IsSpawned && !IsServer)
            return;
        if (IsStunned)
            return; // 이미 스턴 — 추가 타격이 기절 시간을 리셋하지 못한다 (#366의 엣지 성질 유지)

        ThreatTarget = threat;
        m_stunElapsed = 0f;
        m_standingUp = false;
        SetStunned(true);

        // 원래 값을 기억했다가 되돌린다 — 밑에 깔린 상태가 스스로 멈춰 있었을 수 있다(체포·연행 등).
        // 무조건 false로 풀면 멈춰 있어야 할 NPC가 스턴 해제와 함께 걷기 시작한다.
        m_agentStoppedBefore = m_agent.enabled && m_agent.isStopped;
        if (m_agent.enabled && m_agent.isOnNavMesh)
        {
            m_agent.isStopped = true;
            m_agent.ResetPath();
        }
    }

    // 스턴 중 매 프레임 — Update의 스턴 게이트가 FSM Tick 대신 이걸 돌린다.
    private void TickStun()
    {
        // 밧줄로 묶여 있는 동안엔 타이머를 멈춘다 — 끌려가는 내내 깨어나지 않는다 (#269)
        if (IsRoped)
        {
            m_standingUp = false; // 일어나려던 참에 묶였으면 다시 누운 것으로 되돌린다
            return;
        }

        m_stunElapsed += Time.deltaTime;

        // 기절 시간의 마지막 구간을 일어나는 모션에 쓴다 — 총 무력화 시간은 그대로 두고
        // "누워 있다 → 일어난다 → 행동 재개"가 이어지게 한다.
        float standUpAt = Mathf.Max(0f, m_stunConfig.StunSeconds - m_stunConfig.StandUpSeconds);
        if (!m_standingUp && m_stunElapsed >= standUpAt)
        {
            m_standingUp = true;
            RaiseStandUp(); // 전 피어에 일어나는 모션 재생을 알린다
        }

        if (m_stunElapsed >= m_stunConfig.StunSeconds)
            ExitStun(resumeReaction: true);
    }

    /// <summary>
    /// 스턴 해제. 체력을 회복하고 플래그를 내린다 — 상태 enum은 애초에 바뀐 적이 없으므로
    /// 확보·페널티군은 다음 Tick부터 하던 일을 그대로 재개한다.
    /// </summary>
    /// <param name="resumeReaction">
    /// 기절 시간이 다 되어 스스로 깨어난 경우 true — 반응·배회군은 도주로 전환한다(#269/#366).
    /// 수갑 채포처럼 <b>바깥에서 강제로</b> 푸는 경우 false — 여기서 도주를 걸면 수갑을 채우자마자
    /// 도망친다. 확보·페널티군은 두 경우 모두 아무 전이도 걸지 않는다.
    /// </param>
    public void ExitStun(bool resumeReaction)
    {
        if (IsSpawned && !IsServer)
            return;
        if (!IsStunned)
            return;

        ServerRestoreHp(); // 오버레이 경로의 회복 지점 — 넉백 KO는 NpcStunnedState.Exit이 담당 (#366)
        SetStunned(false);

        if (m_agent.enabled && m_agent.isOnNavMesh)
            m_agent.isStopped = m_agentStoppedBefore;

        if (resumeReaction && NpcStateRules.IsReactive(CurrentState))
            StartFlee(ThreatTarget);
    }
}
