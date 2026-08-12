using System;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// 기절을 유발한 것 — <b>연출을 가르기 위한 구분</b>이다. (#477) 무력화 규칙(지속 시간·해제 조건)은
/// 이 값으로 갈리지 않는다 — 그건 <see cref="NpcStun.EnterStunned"/>의 seconds 인자가 담당한다.
/// 색 언어상 시안(전기)은 테이저 전용이라 같은 기절이어도 진압봉 KO에는 전기 연출을 붙이지 않는다.
/// </summary>
public enum NpcStunCause
{
    /// <summary>체력 0 쓰러짐(#366)·그 밖의 경로. 기본값 — 전기 연출 없음.</summary>
    Knockdown,

    /// <summary>테이저 피격(#292) — 감전 연출(NpcShockView)이 붙는 유일한 경로.</summary>
    Taser,
}

/// <summary>
/// 스턴 오버레이 도메인 부품 (#292/#503) — 기절을 FSM 전이가 아니라
/// <see cref="NpcController.CurrentState"/> 위에 얹는 동기화 플래그로 다룬다. 상태 enum이 바뀌지
/// 않으므로 호송·수감·페널티 링크와 상태별 타이머가 끊기지 않고, 풀리면 하던 일을 그대로 재개한다.
///
/// <b>아무 링크도 끊지 않는다</b> — 연행만 예외로 끊던 규칙은 밧줄 전환과 함께 걷어냈다(#562).
/// 넉백은 여전히 끊는다(<see cref="NpcKnockback"/>) — 폭발로 날아가는 것은 성격이 다르다.
///
/// <b>기절 경로는 둘이다.</b> 테이저와 체력 0(#366)은 이 오버레이를, 넉백 착지는
/// <see cref="NpcState.Stunned"/> 전이를 쓴다(전이여야 이전 상태의 Exit()이 에이전트를 정리한다).
/// <see cref="IsStunned"/>가 둘을 함께 답하므로 밖에서는 그것만 쓰면 된다 —
/// <see cref="HasStunOverlay"/>는 게이팅용이라 internal이다.
/// </summary>
public class NpcStun : NetworkBehaviour
{
    private NpcController m_owner;

    // 서버 권위 스턴 플래그 — m_stunned가 서버·오프라인의 진실값.
    private readonly NetworkVariable<bool> m_syncedStunned = new NetworkVariable<bool>();
    private bool m_stunned;

    // 기상 구간인가 — 오버레이는 아직 켜져 있지만 몸은 일어나는 중이다. 클라도 읽어야 한다:
    // 밧줄 조기검증·조준 피드백이 CanRopeBind를 통해 이 값을 본다 (m_standUpPendingSynced와 같은 이유).
    private readonly NetworkVariable<bool> m_syncedRising = new NetworkVariable<bool>();

    /// <summary>기절해 있는가 — <b>경로를 가리지 않는 일반 질문.</b> 오버레이와 넉백 KO를 함께 답한다.
    /// 세션 중에는 동기화 값이라 클라에서도 읽을 수 있다. (#292)</summary>
    public bool IsStunned => HasStunOverlay || m_owner.CurrentState == NpcState.Stunned;

    /// <summary>
    /// 일어나는 모션이 도는 중인가 — <b>기절이 다 끝난 뒤에 덧붙는 구간</b>이다. (#572 후속)
    ///
    /// 이 동안에도 오버레이는 켜져 있다(FSM을 계속 막아야 몸이 걸어 나가지 않는다). 대신
    /// <b>밧줄이 걸리지 않는다</b> — <see cref="NpcStateRules.CanRopeBind"/>가 이 값을 본다.
    /// 안 막으면 일어나던 몸을 묶어 도로 눕히게 되고, 그 그림이 어색하다는 것이 이 구간을
    /// 기절 뒤로 뺀 이유 중 하나다.
    /// </summary>
    public bool IsRising => IsSpawned ? m_syncedRising.Value : m_rising;

    private bool m_rising;

    /// <summary>오버레이만 — 넉백 KO는 제외한다. 코어 Update의 스턴 게이트가 이걸 봐야 넉백 KO일 때
    /// NpcStunnedState.Tick이 정상적으로 돈다. 코어와 기상 대기(<see cref="NpcStandUp"/>)가 읽는다.</summary>
    internal bool HasStunOverlay => IsSpawned ? m_syncedStunned.Value : m_stunned;

    /// <summary>기절 지속 시간(초) — 테이저가 명중 안내에 읽는다. (#269)</summary>
    public float StunSeconds => m_owner.StunConfig.StunSeconds;

    /// <summary>오버레이가 켜지거나 꺼질 때 전 피어에서 발행 — 표현(NpcAnimationDriver)용.
    /// 오버레이는 m_networkState를 바꾸지 않으므로 OnStateChanged로는 알 수 없다. (#292)</summary>
    public event Action<bool> OnStunnedChanged;

    /// <summary>
    /// 테이저 기절이 <b>시작될 때</b> 전 피어에서 1회 발행 — 감전 연출(NpcShockView)용. 인자는 지속 시간(초). (#477)
    ///
    /// 원인을 NetworkVariable로 두지 않는 이유: 스턴 플래그와의 도착 순서에 기대게 되는데 컴포넌트가
    /// 갈리면 그 보장이 더 약하다(계획서 § 4-3). 일회성 알림은 RPC가 맞다.
    /// <b>종료는 알리지 않는다</b> — <see cref="OnStunnedChanged"/>(false)가 그 몫이다. 기절이 밖에서
    /// 먼저 풀리는 경로가 있어(밧줄 묶기·수감·넉백) 지속 시간만으로 종료 시점을 계산하면 어긋난다.
    /// </summary>
    public event Action<float> OnTaserStunStarted;

    /// <summary>
    /// 이 NPC가 <b>무력화된 순간</b> 발행 — 서버(또는 오프라인) 전용. 인자는 (무력화된 NPC, 위협). (#554)
    /// 납치(<see cref="AbductionEvent"/>)가 구독해 맞은 납치범을 호송에서 떼어낸다 —
    /// <see cref="NpcHealth.OnDamaged"/>와 <b>대칭</b>이다: 타격이 격퇴로 이어지듯 무력화도 이어진다.
    /// 테이저가 AbductionEvent를 알 필요가 없는 것도 같은 이유다(진압봉과 같은 방침) —
    /// 무력화를 거는 다른 수단이 생겨도 배선 없이 함께 동작한다.
    ///
    /// <b>오버레이 경로 전용이다</b> — 넉백 착지 KO(<see cref="NpcState.Stunned"/>)는 여기를 지나지 않는다.
    /// 그쪽은 데미지를 동반하므로 격퇴는 <see cref="NpcHealth.OnDamaged"/>가 이미 낸다.
    ///
    /// 연출용인 <see cref="OnStunnedChanged"/>·<see cref="OnTaserStunStarted"/>와 성격이 다르다:
    /// 저 둘은 전 피어에서 발행되는 표현 훅이고, 이것은 서버가 게임플레이 판정을 넘기는 훅이다.
    /// </summary>
    public event Action<NpcController, Transform> OnStunned;

    private float m_stunElapsed;
    private float m_stunDuration; // 이번 기절의 지속 시간 — 경로마다 다르다 (테이저 vs 타격, #400)
    private bool m_standingUp; // 일어나는 모션을 이미 발행했는가 — 마지막 구간에서 1회만 (#269)
    private bool m_agentStoppedBefore; // 스턴 직전의 isStopped — 해제 시 그대로 되돌린다

    private void Awake()
    {
        m_owner = GetComponent<NpcController>();
    }

    public override void OnNetworkSpawn()
    {
        m_syncedStunned.OnValueChanged += HandleSyncedStunnedChanged; // 오버레이 표현 전파 (#292)
    }

    public override void OnNetworkDespawn()
    {
        m_syncedStunned.OnValueChanged -= HandleSyncedStunnedChanged;
    }

    // 기상 구간 표시 — 서버 진실값과 동기화 변수에 함께 기록한다 (SetStunned와 같은 구조).
    // 표현 이벤트가 없는 것이 저쪽과 다른 점이다: 자세는 이미 RaiseStandUp이 알린다.
    private void SetRising(bool value)
    {
        m_rising = value;
        if (IsSpawned && IsServer)
            m_syncedRising.Value = value;
    }

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

    // 서버 자신도 여기를 거치므로 발행 지점이 하나로 모인다.
    private void HandleSyncedStunnedChanged(bool previous, bool current)
    {
        OnStunnedChanged?.Invoke(current);
    }

    // 감전 연출 알림을 전 피어에 돌린다 (오프라인은 RPC 경로가 없어 로컬 발행). (#477)
    private void BroadcastTaserStun(float seconds)
    {
        if (!IsSpawned)
        {
            OnTaserStunStarted?.Invoke(seconds); // 오프라인 폴백
            return;
        }

        TaserStunStartedRpc(seconds);
    }

    [Rpc(SendTo.Everyone)]
    private void TaserStunStartedRpc(float seconds) => OnTaserStunStarted?.Invoke(seconds);

    /// <summary>
    /// 스턴 진입 — 상태를 바꾸지 않고 플래그만 켠다. 테이저와 체력 0 도달(#366)이 부른다.
    /// 넉백 착지는 이 경로가 아니라 NpcState.Stunned 전이를 쓴다 (<see cref="NpcKnockback"/>).
    /// </summary>
    /// <param name="threat">기절시킨 상대 — 깨어날 때 이 대상에게서 도주한다. null 허용.</param>
    /// <param name="seconds">지속 시간(초). 생략하면 <see cref="NpcStunConfig.StunSeconds"/>(테이저 기준).
    /// 체력 0으로 쓰러진 경우는 KnockdownStunSeconds를 넘겨 따로 튜닝한다 (#400).</param>
    /// <param name="cause">연출 분기용 — 무력화 규칙 자체는 이 값으로 갈리지 않는다. (#477)</param>
    public void EnterStunned(
        Transform threat = null,
        float? seconds = null,
        NpcStunCause cause = NpcStunCause.Knockdown
    )
    {
        if (IsSpawned && !IsServer)
            return;
        // 이미 무력화돼 있으면 무시 — 추가 타격의 타이머 리셋(#366 엣지 성질)과 넉백 KO 위에
        // 오버레이가 덧씌워지는 이중 기절을 함께 막는다 (#292).
        if (IsStunned)
            return;

        // 아무 링크도 끊지 않는다 — 오버레이의 목적 그대로다. 연행(Escorted)만은 예외로 끊던
        // 규칙이 있었지만(#292) 그건 수갑 연행을 전제로 한 것이고, 밧줄로 바뀐 뒤로는 '묶어 둔 것'과
        // '끌던 것'이 같은 확보 상태라 갈라 다룰 이유가 없다. #390이 막기로 한 탈취의 뒷문이기도
        // 했다 — 정면 탈취는 CanArrest가 Escorted를 빼서 막아 뒀는데 테이저 한 방이면 같은 결과가
        // 났다. 기절한 채로도 밧줄 장력은 계속 돈다(게이트 순서상 TickRopeDrag가 앞이다). (#562)
        m_owner.Reaction.ThreatTarget = threat;
        m_stunDuration = seconds ?? m_owner.StunConfig.StunSeconds;
        m_stunElapsed = 0f;
        m_standingUp = false;
        SetRising(false);
        SetStunned(true);

        // 위 재진입 가드를 통과한 '새 기절'에서만 나가므로 중복되지 않는다 (#477)
        if (cause == NpcStunCause.Taser)
            BroadcastTaserStun(m_stunDuration);

        // 원래 값을 기억했다가 되돌린다 — 무조건 false로 풀면 밑에 깔린 상태가 스스로 멈춰 있었던
        // NPC(체포·연행 등)가 스턴 해제와 함께 걷기 시작한다.
        // isOnNavMesh까지 보는 이유: NavMesh 밖에서 isStopped를 읽으면 Unity가 에러를 뱉는다 (#557)
        NavMeshAgent agent = m_owner.Agent;
        m_agentStoppedBefore = agent.enabled && agent.isOnNavMesh && agent.isStopped;
        if (agent.enabled && agent.isOnNavMesh)
        {
            agent.isStopped = true;
            agent.ResetPath();
        }

        // 무력화 통보는 <b>마지막</b>이다 (#554) — 구독자(납치 격퇴)가 상태를 바꾸므로 플래그·에이전트
        // 정리가 끝난 뒤여야 그 전이가 스턴 <b>위에</b> 얹힌다. OnDamaged가 HP 반영 '전'에 나가는 것과
        // 순서가 반대인데 이유는 같다: 상태를 바꾸는 쪽과 무력화를 거는 쪽이 서로를 덮지 않게 하는 것.
        // (스턴은 상태 enum을 바꾸지 않는 오버레이라 뒤에 오는 전이에 지워지지 않는다, #292)
        OnStunned?.Invoke(m_owner, threat);
    }

    /// <summary>스턴 중 매 프레임 — 코어 Update의 스턴 게이트가 FSM Tick 대신 이걸 돌린다.
    /// 부품이 자기 Update를 갖지 않는 이유는 게이트 순서가 사양이기 때문이다 (계획서 § 4-1).</summary>
    internal void Tick()
    {
        // 타이머는 밧줄과 무관하게 흐른다 — 묶여 있든 끌려가든 기절은 제 시간에 풀린다.
        // 예전에는 끌리는 동안 멈췄지만(#269) 그러면 IsStunned가 안 내려가 감전 연출이 끝나지 않고
        // 그 대상에 테이저가 다시 걸리지도 않는다. 깨어나도 잃는 것은 없다 — 확보 상태(Escorted·
        // Captured)는 ExitStun의 도주 전이 대상이 아니고(NpcStateRules.IsReactive) 밧줄도 그대로다.
        m_stunElapsed += Time.deltaTime;

        // <b>기상은 기절이 다 끝난 뒤에 덧붙는다</b> (#572 후속). 예전에는 기절 시간의 마지막
        // 구간을 잘라 썼는데("총 무력화 시간은 그대로 둔다"), 그러면 <b>클립 길이가 곧 검거 창을
        // 깎는다</b> — 기상을 정상 속도로 늦추자 테이저의 누운 시간이 2.09초에서 1.50초로 줄었다.
        // 무력화 시간은 난이도 손잡이인데(NpcStunConfig 주석) 연출 튜닝이 그걸 건드리면 안 된다.
        //
        // <see cref="NpcStandUp"/>이 이미 이 구조다 — 누워 버티기 → 기상 재생 → 후속 동작 순서로,
        // 기상 시간이 <b>덧붙는</b> 구간이다. 기절 경로만 예외였던 것을 맞춘다.
        if (!m_standingUp && m_stunElapsed >= m_stunDuration)
        {
            m_standingUp = true;
            SetRising(true);

            // 밧줄이 걸려 있으면 모션을 내지 않는다 — 줄에 눕혀진 몸은 기절이 풀려도 일어날 수 없다.
            // 알림만 건너뛴다: 기절은 아래에서 제 시간에 풀리고 대상은 묶인 채 남는다.
            // 여기서 알리면 벌떡 섰다가 곧바로 묶임 자세로 되돌아간다.
            if (!m_owner.Rope.IsRoped && !m_owner.Rope.IsTethered)
                m_owner.RaiseStandUp(); // 전 피어에 일어나는 모션 재생을 알린다
        }

        // 기상 구간이 끝나야 오버레이를 걷는다 — <b>오버레이를 유지하는 것이 핵심이다.</b>
        // 여기서 먼저 걷으면 코어 Update의 스턴 게이트가 풀려 FSM이 되살아나고, 일어나는 클립이
        // 도는 동안 몸이 걸어 나간다. 대신 그 구간에는 밧줄이 걸리지 않는다 — <see cref="IsRising"/>.
        if (m_stunElapsed >= m_stunDuration + m_owner.StunConfig.StandUpSeconds)
            ExitStun(resumeReaction: true);
    }

    /// <summary>
    /// 오버레이만 조용히 걷어낸다 — 체력 회복도, 상태 전이도 하지 않는다. 넉백이 오버레이 위에
    /// 겹칠 때 쓴다: 넉백이 이기고 회복은 착지 후 NpcStunnedState.Exit이 한 번만 한다 (#292).
    /// isStopped를 되돌리지 않는 것은 넉백이 곧 에이전트를 통째로 끄기 때문이다.
    /// </summary>
    internal void ClearStunOverlay()
    {
        if (!HasStunOverlay)
            return;

        SetStunned(false);
        SetRising(false);
        m_stunElapsed = 0f;
        m_standingUp = false;
    }

    /// <summary>
    /// 스턴 해제. 플래그를 내린다 — 상태 enum은 애초에 바뀐 적이 없으므로 확보·페널티군은 다음
    /// Tick부터 하던 일을 그대로 재개한다.
    ///
    /// <b>체력은 회복하지 않는다</b> (#571). 예전에는 여기가 회복 지점이었고, 근거는 "HP 0인 채로
    /// 깨어나면 <c>SetHp</c>의 0 도달 엣지가 다시 안 걸려 두 번 다시 기절하지 않는 무적이 된다"였다.
    /// <b>그 근거가 통째로 사라졌다</b> — 이제 HP 0은 깨어나는 상태가 아니라 사망이라 그 경로 자체가
    /// 없다. 넉다운도 임계를 <b>내려가는</b> 순간의 한 방향 엣지라 개체당 한 번인 것이 의도다:
    /// 임계 아래로 내려간 몸이 다음에 맞으면 다시 눕는 것이 아니라 죽는다.
    /// </summary>
    /// <param name="resumeReaction">
    /// 스스로 깨어난 경우 true — 반응·배회군은 도주로 전환한다(#269/#366). 수갑 채포처럼 <b>바깥에서
    /// 강제로</b> 푸는 경우 false — 여기서 도주를 걸면 수갑을 채우자마자 도망친다.
    /// </param>
    public void ExitStun(bool resumeReaction)
    {
        if (IsSpawned && !IsServer)
            return;
        if (!HasStunOverlay)
            return; // 넉백 KO는 NpcStunnedState가 스스로 빠져나간다

        SetStunned(false);
        SetRising(false);

        NavMeshAgent agent = m_owner.Agent;
        if (agent.enabled && agent.isOnNavMesh)
            agent.isStopped = m_agentStoppedBefore;

        if (resumeReaction && NpcStateRules.IsReactive(m_owner.CurrentState))
            m_owner.Reaction.StartFlee(m_owner.Reaction.ThreatTarget);
    }
}
