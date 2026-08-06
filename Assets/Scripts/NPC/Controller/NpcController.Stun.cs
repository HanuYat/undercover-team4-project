using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 기절을 유발한 것 — <b>연출을 가르기 위한 구분</b>이다. (#477)
/// 무력화 자체의 규칙(지속 시간·해제 조건)은 이 값으로 갈리지 않는다 — 그건 이미
/// <see cref="NpcController.EnterStunned"/>의 seconds 인자가 담당한다.
///
/// 나눈 이유: 3부작 색 언어에서 <b>시안(전기)은 테이저 전용</b>이다. "시안이 튀면 저건 쓰러져
/// 있고 지금 밧줄로 묶을 수 있다"가 한눈에 읽혀야 하므로, 같은 기절이어도 진압봉 KO에는
/// 전기 연출을 붙이지 않는다.
/// </summary>
public enum NpcStunCause
{
    /// <summary>체력 0 쓰러짐(#366)·그 밖의 경로. 기본값 — 전기 연출 없음.</summary>
    Knockdown,

    /// <summary>테이저 피격(#292) — 감전 연출(NpcShockView)이 붙는 유일한 경로.</summary>
    Taser,
}

/// <summary>
/// 스턴 오버레이 (#292) — 기절을 FSM 상태 전이가 아니라 <see cref="CurrentState"/> 위에 얹는
/// 동기화 플래그로 다룬다. 상태 enum이 바뀌지 않으므로 호송·수감·페널티 링크와 각 상태의
/// 타이머가 스턴에 끊기지 않고, 풀리면 하던 일을 그대로 재개한다.
///
/// <b>예외 하나 — 연행은 끊는다.</b> 링크를 지키는 게 목적이지만 연행(Escorted)만은 예외로 끊어
/// Captured로 떨군다(수갑은 유지). 그래야 연행 중인 대상을 쏘는 행동에 의미가 생긴다.
/// 넉백이 이미 같은 처리를 한다(NpcController.Knockback).
///
/// 서버 권위 + 오프라인 폴백 — 서버(또는 오프라인)만 값을 바꾸고 클라이언트는 동기화 값을 읽는다.
/// m_networkState·m_syncedHp와 같은 이중 구조다 (#56 패턴).
///
/// <b>기절 경로는 둘이다.</b> 테이저와 체력 0(#366)은 이 오버레이를 쓰고, 넉백 착지는
/// <see cref="NpcState.Stunned"/> 상태 전이를 그대로 쓴다 — 넉백은 비행 전 상태 전이로 이전
/// 상태의 Exit()이 에이전트를 정리하게 만드는 구조라 오버레이로 옮기면 그 정리가 빠진다.
/// 그래서 <see cref="IsStunned"/>가 두 경로를 함께 답한다 — 밖에서는 이것만 쓰면 된다.
/// 오버레이만 따로 봐야 하는 건 이 파일 안의 게이팅뿐이라 <c>HasStunOverlay</c>를 private로 둔다.
/// </summary>
public partial class NpcController
{
    // 서버 권위 스턴 플래그 — 서버만 쓰고 모든 클라이언트가 읽는다. m_stunned가 서버·오프라인의 진실값.
    private readonly NetworkVariable<bool> m_syncedStunned = new NetworkVariable<bool>();
    private bool m_stunned;

    /// <summary>기절해 있는가 — <b>경로를 가리지 않는 일반 질문.</b> 오버레이(테이저·체력 0)와
    /// 넉백 KO(<see cref="NpcState.Stunned"/>)를 함께 답한다. 세션 중에는 동기화 값이라
    /// 클라이언트에서도 읽을 수 있다. (#292)
    ///
    /// 밖에서 "이 NPC 기절했나?"를 물을 일이 있으면 <b>항상 이것</b>이다 — 경로별로 갈라 물을 이유가
    /// 없도록 여기서 합쳐 둔다.</summary>
    public bool IsStunned => HasStunOverlay || CurrentState == NpcState.Stunned;

    // 오버레이만 — 넉백 KO는 제외한다. Update의 스턴 게이트가 이걸 봐야 넉백 KO일 때
    // NpcStunnedState.Tick이 정상적으로 돌고, 해제 경로도 오버레이가 켜진 경우에만 동작한다.
    private bool HasStunOverlay => IsSpawned ? m_syncedStunned.Value : m_stunned;

    /// <summary>스턴 오버레이가 켜지거나 꺼질 때 전 피어에서 발행된다 — 표현(NpcAnimationDriver)용. (#292)
    /// 오버레이는 m_networkState를 바꾸지 않으므로 OnStateChanged로는 알 수 없다.</summary>
    public event System.Action<bool> OnStunnedChanged;

    /// <summary>
    /// 테이저 기절이 <b>시작될 때</b> 전 피어에서 1회 발행된다 — 감전 연출(NpcShockView)용. (#477)
    /// 인자는 이번 기절의 지속 시간(초)으로, "언제부터 잦아들지"의 힌트다.
    ///
    /// <b>동기화 변수를 하나 더 두지 않는 이유:</b> 원인과 스턴 플래그를 각각 NetworkVariable로 두면
    /// 도착 순서에 기대게 되는데, NpcController는 partial로 여러 파일에 쪼개져 있어 필드 선언 순서가
    /// 컴파일러에 달렸다. 일회성 알림은 RPC가 맞다 (Baton.PlaySwingRpc·NpcDespawnVfx와 같은 판단).
    ///
    /// <b>종료는 이 이벤트가 알리지 않는다</b> — 기존 <see cref="OnStunnedChanged"/>(false)가 그 몫이다.
    /// 밧줄에 묶이면 기절 타이머가 멈추므로(#269) 지속 시간만으로 종료 시점을 계산하면 어긋난다.
    /// </summary>
    public event System.Action<float> OnTaserStunStarted;

    // 서버 진실값과 동기화 변수에 함께 기록한다 — HandleFsmStateChanged와 같은 구조 (#56)
    private void SetStunned(bool value)
    {
        m_stunned = value;

        if (!IsSpawned)
        {
            OnStunnedChanged?.Invoke(value); // 오프라인 — 동기화 없이 바로 로컬 이벤트
            return;
        }

        if (!IsServer)
            return; // 서버 권위 — 클라이언트 쓰기는 무시

        m_syncedStunned.Value = value; // OnValueChanged를 거쳐 모든 피어에서 OnStunnedChanged가 발생한다
    }

    // 동기화 변수 변경 수신 — 서버 자신도 여기를 거치므로 발행 지점이 하나로 모인다.
    private void HandleSyncedStunnedChanged(bool previous, bool current)
    {
        OnStunnedChanged?.Invoke(current);
    }

    // 감전 연출 알림을 전 피어에 돌린다 — Baton.PlaySwing과 같은 구조(오프라인은 RPC 경로가 없어 로컬 발행). (#477)
    private void BroadcastTaserStun(float seconds)
    {
        if (!IsSpawned)
        {
            OnTaserStunStarted?.Invoke(seconds); // 오프라인 — 비네트워크 Play 테스트 폴백
            return;
        }

        TaserStunStartedRpc(seconds);
    }

    [Rpc(SendTo.Everyone)]
    private void TaserStunStartedRpc(float seconds) => OnTaserStunStarted?.Invoke(seconds);

    // 경과 시간으로 센다 — 밧줄에 묶이면 멈춰야 해서 "끝나는 시각" 방식으로는 계산이 지저분해진다.
    private float m_stunElapsed;
    private float m_stunDuration;       // 이번 기절의 지속 시간 — 경로마다 다르다 (테이저 vs 타격, #400)
    private bool m_standingUp;          // 일어나는 모션을 이미 발행했는가 — 마지막 구간에서 1회만 (#269)
    private bool m_agentStoppedBefore;  // 스턴 직전의 isStopped — 해제 시 그대로 되돌린다

    /// <summary>
    /// 스턴 진입 — 상태를 바꾸지 않고 플래그만 켠다. 테이저와 체력 0 도달(#366)이 부른다.
    /// 넉백 착지는 이 경로가 아니라 NpcState.Stunned 전이를 쓴다 (NpcController.Knockback).
    /// </summary>
    /// <param name="threat">기절시킨 상대 — 깨어날 때 이 대상에게서 도주한다. null 허용.</param>
    /// <param name="seconds">지속 시간(초). 생략하면 <see cref="NpcStunConfig.StunSeconds"/>(테이저 기준)를
    /// 쓴다. 체력 0으로 쓰러진 경우는 KnockdownStunSeconds를 넘겨 테이저와 따로 튜닝한다 (#400).</param>
    /// <param name="cause">연출 분기용 (#477). 기본값 Knockdown이라 테이저 호출부만 명시하면 된다 —
    /// 무력화 규칙 자체는 이 값으로 갈리지 않는다.</param>
    public void EnterStunned(
        Transform threat = null,
        float? seconds = null,
        NpcStunCause cause = NpcStunCause.Knockdown
    )
    {
        if (IsSpawned && !IsServer)
            return;
        // 이미 무력화돼 있으면 무시한다 — 추가 타격이 기절 시간을 리셋하지 못하게 하고(#366의 엣지
        // 성질), 넉백 KO(enum Stunned) 위에 오버레이가 덧씌워지는 이중 기절도 막는다 (#292).
        if (IsStunned)
            return;

        // 연행 중이면 연행을 끊는다 — 넉백과 같은 처리다(Escorted → Captured, 수갑은 유지).
        // 오버레이의 목적은 "링크를 의도치 않게 끊지 않는 것"이지 "무엇도 끊지 않는 것"이 아니다.
        // 호송을 그대로 재개시키면 연행 중인 대상을 쏠 이유가 없어져 상호작용 자체가 죽는다 —
        // 놓친 쪽은 E로 재연행해야 하고, 그동안 다른 플레이어가 가로챌 여지가 생긴다.
        // 유치장·오검거 페널티(Jailed/Detained/Chasing/PenaltyEscorting)는 플레이어가 쥔 링크가
        // 아니라 시스템이 진행 중인 절차라 건드리지 않는다 — 끊으면 이중 집계·타이머 리셋·
        // 매니저 desync가 그대로 돌아온다(#292가 오버레이를 택한 이유).
        if (CurrentState == NpcState.Escorted)
            StopEscort();

        ThreatTarget = threat;
        m_stunDuration = seconds ?? m_stunConfig.StunSeconds;
        m_stunElapsed = 0f;
        m_standingUp = false;
        SetStunned(true);

        // 감전 연출 알림 — 위 재진입 가드(IsStunned)를 통과한 '새 기절'에서만 나가므로 중복되지 않는다 (#477)
        if (cause == NpcStunCause.Taser)
            BroadcastTaserStun(m_stunDuration);

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
        float standUpAt = Mathf.Max(0f, m_stunDuration - m_stunConfig.StandUpSeconds);
        if (!m_standingUp && m_stunElapsed >= standUpAt)
        {
            m_standingUp = true;
            RaiseStandUp(); // 전 피어에 일어나는 모션 재생을 알린다
        }

        if (m_stunElapsed >= m_stunDuration)
            ExitStun(resumeReaction: true);
    }

    /// <summary>
    /// 오버레이만 조용히 걷어낸다 — 체력 회복도, 상태 전이도 하지 않는다.
    /// 넉백이 오버레이 위에 겹칠 때 쓴다: 넉백이 이기고 기절 시간은 넉백 기준으로 새로 흐른다.
    /// 회복은 착지 후 NpcStunnedState.Exit이 한 번만 하면 된다 (#292).
    /// </summary>
    private void ClearStunOverlay()
    {
        if (!HasStunOverlay)
            return;

        SetStunned(false);
        m_stunElapsed = 0f;
        m_standingUp = false;
        // isStopped는 되돌리지 않는다 — 넉백이 곧 에이전트를 통째로 끄고,
        // 착지 상태(NpcStunnedState.Enter)가 다시 정한다.
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
        if (!HasStunOverlay)
            return; // 오버레이가 없으면 풀 것도 없다 — 넉백 KO는 NpcStunnedState가 스스로 빠져나간다

        ServerRestoreHp(); // 오버레이 경로의 회복 지점 — 넉백 KO는 NpcStunnedState.Exit이 담당 (#366)
        SetStunned(false);

        if (m_agent.enabled && m_agent.isOnNavMesh)
            m_agent.isStopped = m_agentStoppedBefore;

        if (resumeReaction && NpcStateRules.IsReactive(CurrentState))
            StartFlee(ThreatTarget);
    }
}
