using UnityEngine;

/// <summary>
/// NPC 모션의 <b>단일 결정 지점</b> — 부품들의 제안 중 하나를 골라 Animator의 State(int)에 쓴다. (#502)
///
/// FSM 상태는 그대로 Animator 상태 번호가 되므로(Idle=0, Walk=1...) 새 상태가 생겨도 대체로
/// 손댈 일이 없다. <see cref="NpcController.OnStateChanged"/>는 네트워크 동기화를 거쳐 모든 피어에서
/// 발생하므로(#56) 서버·클라이언트 어디서든 같은 모션이 재생된다.
///
/// <b>이 클래스가 들고 있는 것</b> — Animator 참조, 컨트롤러 이벤트 구독, 기준 상태(base) 매핑,
/// 누움 신호(#363), 그리고 우선순위 중재. 표현 축은 부품이 나눠 갖는다:
/// <list type="bullet">
/// <item><see cref="NpcLocomotionMotion"/> — 속도로 갈리는 모션 (#97/#254/#261/#277~)</item>
/// <item><see cref="NpcOneShotMotion"/> — 순간 이벤트로 오는 단발 모션 (#220/#269/#332)</item>
/// </list>
///
/// <b>누움(#363)은 여기 남는다.</b> 판정 입력이 기준 상태·기상 모션·밧줄 세 곳에 걸쳐 있어
/// 어느 부품에도 온전히 속하지 않는다.
///
/// <b>SetInteger는 <see cref="Apply"/> 한 곳에서만 부른다.</b> 부품이 각자 쓰면 마지막에 쓴 쪽이
/// 이겨서 순서가 우연에 맡겨진다 — 그것이 이 구조 이전의 문제였다.
/// </summary>
[RequireComponent(typeof(NpcController))]
[RequireComponent(typeof(NpcLocomotionMotion))]
[RequireComponent(typeof(NpcOneShotMotion))]
public class NpcAnimationDriver : MonoBehaviour
{
    [SerializeField] private Animator m_animator;

    private NpcController m_controller;
    private NpcDutyAgent m_penalty; // 앵그리 마크 판정이 임무 종류를 읽는다 (#371/#503)
    private NpcReaction m_reaction; // 스윙 순간 이벤트가 이 부품에서 온다 (#220/#503)
    private NpcLocomotionMotion m_locomotion;
    private NpcOneShotMotion m_oneShot;

    // 우선순위가 높은 순으로 정렬해 둔 제안자들 — 결정은 이 순서대로 물어보는 것이 전부다
    private INpcMotionSource[] m_sources;

    // 스윙이 끝난 뒤 되돌아갈 FSM 기준 상태 — 저항(Attack)이면 버틴 자세(Idle)로 복귀한다 (#220)
    private NpcState m_baseState;

    // 마지막으로 Animator에 쓴 번호 — 매 프레임 같은 값을 다시 쓰지 않기 위한 캐시.
    // -1은 "아직 아무것도 안 썼다" (모션 번호는 전부 0 이상)
    private int m_appliedMotion = -1;

    // 직전 프레임의 묶임 여부 — 묶임/풀림이 바뀌는 순간에만 속도 추적과 콜라이더를 다시 시드한다 (#369/#513)
    private bool m_ropeBoundMotion;
    private bool m_ropeProneMotion;

    /// <summary>
    /// 지금 모델이 바닥에 누워 있는가 — 기절했거나 밧줄에 묶인 채로, 아직 일어나기 시작하지 않은 구간. (#363/#513)
    /// 몸통 콜라이더를 같이 눕히는 <see cref="NpcProneCollider"/>가 읽는다. FSM 상태만으로는 판별할 수 없다:
    /// 일어나는 모션(#269) 동안에도 상태는 Stunned라 상태만 보면 서 있는 몸에 누운 콜라이더가 남고,
    /// 반대로 묶인 채 놓인 대상은 상태가 Captured(기립 대기)라 상태만 보면 누운 몸이 서 있는 것으로 잡힌다.
    /// </summary>
    public bool IsProne { get; private set; }

    /// <summary>누움 여부가 바뀔 때 발행 — 표현(모션)과 콜라이더가 같은 순간에 움직이도록 한다. (#363)</summary>
    public event System.Action<bool> OnProneChanged;

    /// <summary>지금의 FSM 기준 상태 — 부품들이 자기 판별의 기준으로 읽는다.</summary>
    public NpcState BaseState => m_baseState;

    /// <summary>
    /// 속도 기반 로코모션을 멈춰야 하는 구간인가 — 끌려 누워 있거나 스턴 오버레이 중.
    /// 둘 다 FSM 상태가 그대로라(연행·저항·페널티 상태에서 기절할 수 있다) 막지 않으면
    /// 로코모션이 매 프레임 누운 자세를 걷기/정지로 갈아치운다. (#292/#369/#513)
    /// </summary>
    public bool SuppressLocomotion => IsRopeProne || m_controller.Stun.IsStunned;

    /// <summary>
    /// 기상 모션이 물러나도 되는가 — 물러난 자리를 기준 상태 모션이 받는다. (#513/#564)
    /// 아직 누운 기준 상태이거나 서버가 "일어나는 중" 표시를 들고 있으면 버틴다.
    /// </summary>
    public bool CanLeaveStandUp =>
        !IsRopeProne && m_baseState != NpcState.Stunned && !m_controller.StandUp.IsStandingUp;

    /// <summary>
    /// 밧줄이 걸려 있는가 — 끌리는 중(<c>IsRoped</c>)과 놓아둔 채 묶여만 있는 것(<c>IsTethered</c>)을
    /// 함께 본다. (#513)
    ///
    /// 둘을 갈라 보면 E로 놓는 순간 묶인 몸이 벌떡 일어선다: 놓기는 <b>끌기만</b> 멈추고 줄은 그대로이며
    /// (GDD 7-5), 밧줄은 애초에 무력화된 대상만 묶으므로(#446) 방금까지 누워 끌려온 몸이다.
    /// </summary>
    private bool IsRopeBound => m_controller.Rope.IsRoped || m_controller.Rope.IsTethered;

    /// <summary>
    /// 밧줄 때문에 <b>바닥에 있는가</b> — 줄이 걸려 있거나 풀린 뒤 아직 쓰러져 있고, 기상 모션이 아직
    /// 시작되지 않았다. 누운 모션·콜라이더의 기준. (#513)
    ///
    /// <b>예약 구간</b>(<see cref="NpcStandUp.IsStandingUp"/>)을 함께 보는 이유: 풀기는 예약을 걸자마자
    /// 줄을 빼므로 묶임만 보면 <b>쓰러져 기다리는 몇 초 동안 몸을 눕혀 둘 근거가 사라진다</b> — 푸는 즉시
    /// 벌떡 서고 뒤늦게 이미 서 있는 몸에 기상 모션이 나왔다. 예약이 곧 "아직 바닥"이다.
    ///
    /// 반대로 기상 모션 재생 여부를 빼는 이유는 풀리는 순간의 순서 때문이다: 실제로 푸는 경로는
    /// "일어나기 → 후속 전이(도주·배회)" 순인데, 묶임 표시를 걷는 것은 <see cref="PlayerEscorter"/>의
    /// 매 프레임 정리라 한 박자 늦게 온다. 그것만 보면 이미 일어나 걷기 시작한 몸이 그 사이 도로 눕는다.
    /// </summary>
    private bool IsRopeProne =>
        (IsRopeBound || m_controller.StandUp.IsStandingUp) && !m_oneShot.IsStandingUp;

    private void Awake()
    {
        m_controller = GetComponent<NpcController>();
        m_penalty = GetComponent<NpcDutyAgent>();
        m_reaction = GetComponent<NpcReaction>();
        m_locomotion = GetComponent<NpcLocomotionMotion>();
        m_oneShot = GetComponent<NpcOneShotMotion>();

        if (m_animator == null)
            m_animator = GetComponentInChildren<Animator>();

        // 부품을 나열만 하고 순서는 우선순위 값이 정한다 — 나열 순서를 잘못 적어도 결과가 바뀌지 않게.
        // (그 순서가 코드 배치에 숨어 있던 것이 이 구조 이전의 문제였다)
        m_sources = new INpcMotionSource[] { m_locomotion, m_oneShot };
        System.Array.Sort(m_sources, (a, b) => b.Priority.CompareTo(a.Priority));
    }

    private void Start()
    {
        // 로컬 FSM 이벤트가 아닌 컨트롤러의 통합 이벤트를 구독한다 — 클라이언트에서는
        // NetworkVariable 동기화가, 오프라인에서는 로컬 FSM이 이 이벤트를 발생시킨다 (#56)
        m_controller.OnStateChanged += HandleStateChanged;
        m_penalty.OnPenaltyDutyChanged += HandlePenaltyDutyChanged;
        // 스윙은 상태 전이가 아니라 순간 이벤트 — 저항 상태를 유지한 채 매 타격마다 단발 스윙을 얹는다 (#220)
        m_reaction.OnAttackSwing += HandleAttackSwing;
        // 일어나기도 상태 전이가 아닌 순간 이벤트 — 기절 상태를 유지한 채 마지막 구간에만 얹는다 (#269)
        m_controller.OnStandUp += HandleStandUp;
        // 스턴은 상태 전이가 아니라 오버레이라 OnStateChanged로 안 온다 — 따로 구독한다 (#292)
        m_controller.Stun.OnStunnedChanged += HandleStunnedChanged;
        HandleStateChanged(m_controller.CurrentState);
    }

    private void OnDestroy()
    {
        if (m_controller != null)
        {
            m_controller.OnStateChanged -= HandleStateChanged;
            m_controller.OnStandUp -= HandleStandUp;
            m_controller.Stun.OnStunnedChanged -= HandleStunnedChanged;
        }

        if (m_penalty != null)
            m_penalty.OnPenaltyDutyChanged -= HandlePenaltyDutyChanged;

        if (m_reaction != null)
            m_reaction.OnAttackSwing -= HandleAttackSwing;
    }

    private void Update()
    {
        if (m_animator == null || Time.deltaTime <= 0f)
            return;

        TrackRopeEdges();

        m_oneShot.Tick();
        m_locomotion.Tick(Time.deltaTime);

        Apply(ResolveMotion());
    }

    /// <summary>
    /// 이번 프레임에 재생할 번호를 고른다 — <b>우선순위 순으로 물어보고 처음 응답한 것</b>이 이긴다.
    /// 아무도 원하지 않으면 기준 상태 모션이다. (#502)
    /// </summary>
    private int ResolveMotion()
    {
        for (int i = 0; i < m_sources.Length; i++)
        {
            if (m_sources[i].TryGetMotion(out int motion))
                return motion;
        }

        return AnimatorBaseState(m_baseState);
    }

    // Animator에 쓰는 유일한 지점. 같은 값을 다시 쓰지 않는 이유는 성능이 아니라 안전이다 —
    // Any State 전이는 조건이 참인 동안 계속 성립하므로, 같은 번호를 반복해서 넣어도 재진입하지
    // 않도록 컨트롤러가 짜여 있어야 한다. 캐시를 두면 그 가정에 기대지 않아도 된다.
    private void Apply(int motion)
    {
        if (motion == m_appliedMotion)
            return;

        m_appliedMotion = motion;
        m_animator.SetInteger(NpcAnimStates.s_stateHash, motion);
    }

    /// <summary>스윙 클립 index를 지정한다 — <see cref="NpcOneShotMotion"/>이 스윙 직전에 부른다. (#220)</summary>
    public void SetSwingVariant(int variant)
    {
        if (m_animator != null)
            m_animator.SetFloat(NpcAnimStates.s_swingVariantHash, variant);
    }

    // 묶임/풀림이 바뀌는 순간을 잡는다 (#269/#369/#513). 상태 전이 훅만으론 놓친다 —
    // 커스터디 전이와 묶임 플래그가 별개 NetworkVariable이라 원격 피어 도착 순서가 안 보장된다.
    //
    // 누움 판정에는 기상 예약(쓰러져 대기)도 들어가므로 엣지를 둘로 나눠 본다: 풀기는 예약을 걸자마자
    // 줄을 빼고 두 표시의 도착 순서가 보장되지 않아, 묶임만 보면 예약이 늦게 도착하는 순서에서
    // 자세가 도로 눕는다.
    private void TrackRopeEdges()
    {
        bool bound = IsRopeBound;
        if (m_ropeBoundMotion != bound)
        {
            m_ropeBoundMotion = bound;

            // 일어나던 중에 다시 묶였으면 누운 자세로 되돌아간다 — 끌려가는데 서 있으면 안 된다.
            // (방치 만료로 일어나는 도중의 재포획은 커스터디 전이가 먼저 와서 HandleStateChanged가 처리한다)
            if (bound)
                m_oneShot.CancelStandUp();

            RefreshProne(); // 눕/서에 맞춰 콜라이더도 되돌린다 (#363)
            m_locomotion.ResetSpeedTracking();
        }

        bool prone = IsRopeProne;
        if (m_ropeProneMotion != prone)
        {
            m_ropeProneMotion = prone;
            RefreshProne();
            m_locomotion.ResetSpeedTracking();
        }
    }

    /// <summary>
    /// 누움 여부를 다시 판정해 바뀌었으면 알린다 — 기준 상태·기상 모션·묶임을 건드린 직후에 부른다. (#363/#369/#513)
    /// </summary>
    public void RefreshProne()
    {
        // 밧줄에 묶여 있으면(끌리는 중이든 놓아둔 채든) 콜라이더도 눕는다 — 커스터디 상태는 Escorted·Captured라
        // 상태만 보면 서 있게 된다. 일어나기 시작하면 그 순간 함께 선다.
        // 사망(#571)은 <b>일어나기와 무관하게</b> 항상 누움이다 — 기상 여부를 함께 보는 기절과 다른 점이다.
        // 죽는 순간 진행 중이던 기상 모션은 취소되어야 하고, 시체가 다시 서는 일은 없다.
        bool prone =
            m_baseState == NpcState.Dead
            || IsRopeProne
            || (m_baseState == NpcState.Stunned && !m_oneShot.IsStandingUp);
        if (prone == IsProne)
            return;

        IsProne = prone;
        OnProneChanged?.Invoke(prone);
    }

    // FSM 기준 상태에 대응하는 Animator base 번호. Attack 번호(3)는 단발 스윙 전용이라 base로 쓰지 않는다 (#220).
    private int AnimatorBaseState(NpcState state)
    {
        // 밧줄에 묶여 누워 있으면 FSM 상태와 무관하게 누운 모션이다 (#369/#513) — 커스터디 상태는
        // 끌리는 중이면 수갑 연행과 같은 Escorted(수갑 찬 걷기)이고 놓아두면 Captured(수갑 찬 기립 대기)라,
        // 이 분기가 없으면 서서 끌려가거나 놓는 순간 벌떡 일어선다.
        if (IsRopeProne)
            return (int)NpcState.Stunned;

        return state switch
        {
            // 저항(Attack)의 base는 이동 여부로 갈린다 — 추격 중이면 달리기, 사거리 안에서 멈추면 버틴 자세 (#254).
            // 보통은 로코모션 부품이 먼저 답하므로 여기까지 오지 않지만, 스턴 등으로 그쪽이 멈춘 프레임의 답이다.
            NpcState.Attack => m_locomotion.IsResistMoving ? (int)NpcState.Run : (int)NpcState.Idle,
            // 사망(#571)도 대응 Animator 상태가 없다 — 누운 기절 모션을 빌려 쓴다. <b>임시다</b>:
            // 래그돌(NpcRagdoll)이 붙으면 Animator를 통째로 끄므로 이 값은 쓰이지 않는다.
            // 그때까지도 시체가 서 있으면 안 되니 남겨 둔다.
            NpcState.Dead => (int)NpcState.Stunned,
            // 침입(Intruding)은 대응 Animator 상태가 없어 평범한 걷기(Walk)를 빌려 쓴다 —
            // 수갑을 차지 않은 채 본부로 걸어 들어오는 그림이라 Escorted가 아니라 Walk다. (#231)
            NpcState.Intruding => (int)NpcState.Walk,
            // 수감(Jailed)도 대응 Animator 상태가 없다 — 수갑 찬 걷기를 빌려 쓴다.
            NpcState.Jailed => (int)NpcState.Escorted,
            // 오검거 페널티 상태들도 대응 Animator 상태가 없다 — 속도 기반 로코모션을 빌려 쓴다 (#277~#279).
            NpcState.Detained => (int)NpcState.Walk,
            NpcState.Chasing => (int)NpcState.Run,
            NpcState.PenaltyEscorting => (int)NpcState.Walk,
            // 질주(Sprinting)도 대응 Animator 상태가 없다 (#106) — 도주와 같은 배율로 달리므로
            // 달리기(Run)를 빌려 쓴다. 멈추는 구간이 없어 이동 여부로 가르지 않는다.
            NpcState.Sprinting => (int)NpcState.Run,
            _ => (int)state,
        };
    }

    private void HandleAttackSwing(int variant) => m_oneShot.PlaySwing(variant);

    private void HandleStandUp() => m_oneShot.PlayStandUp();

    // 스턴 오버레이 온/오프 (#292) — 기존 기절 표현을 그대로 재사용한다.
    // 오버레이는 FSM 상태를 바꾸지 않으므로 "Stunned로 전이한 것처럼" 먹여 누운 자세·일어나기(#269)·
    // 스윙 취소(#220)·누움 콜라이더(#363)가 손대지 않고 그대로 동작하게 한다.
    // 풀릴 때는 진짜 현재 상태로 되돌린다 — 반응군이면 곧이어 도주 전이가 덮어쓴다.
    private void HandleStunnedChanged(bool stunned) =>
        HandleStateChanged(stunned ? NpcState.Stunned : m_controller.CurrentState);

    // 임무 종류(오검거/납치)가 전파된 순간 — 마크 판정을 다시 태운다. 상태 전이와 임무 플래그는 각각
    // 다른 NetworkVariable이라 클라이언트 도착 순서가 보장되지 않는다: 상태가 먼저 오면 마크가 잠깐
    // 켜졌다가 여기서 꺼진다. 스턴 오버레이(#292)가 같은 방식으로 재판정을 태운다.
    private void HandlePenaltyDutyChanged() => HandleStateChanged(m_controller.CurrentState);

    private void HandleStateChanged(NpcState state)
    {
        // 스턴 오버레이 중에는 밑에서 상태가 바뀌어도 화면은 계속 누워 있어야 한다 (#292).
        // 오버레이는 CurrentState를 얼리지 않는다 — FSM Tick만 멈출 뿐이라 외부(오검거 페널티
        // 매니저의 Detained→Chasing 전이 등)에서 걸린 전이는 그대로 들어온다. 그걸 그대로 받으면
        // 기준 상태가 Stunned에서 벗어나 기절 중에 벌떡 서는 그림이 나오고, 누움 판정(#363)이
        // 풀려 콜라이더까지 같이 선다.
        if (m_controller.Stun.IsStunned)
            state = NpcState.Stunned;

        // 제압 전환 분기용 직전 상태 — base를 덮어쓰기 전에 읽는다 (#332)
        NpcState previous = m_baseState;
        m_baseState = state;

        m_oneShot.OnBaseStateChanged(state, previous);
        m_locomotion.OnBaseStateChanged(state);

        // 누움 판정은 여기서 끝난다 — 기준 상태와 기상 표시가 모두 확정된 뒤다 (#363)
        RefreshProne();

        // 앵그리 마크(#280) — 페널티 상태(수용~호송) 동안 머리 위에 표시한다. 이 이벤트는 동기화를 거쳐
        // 모든 피어에서 발생하므로(#56) 원격 클라·CCTV 화면에서도 같은 시점에 켜지고 꺼진다.
        // 납치범(#371)은 제외한다 — 시민과 구분되지 않는 것이 그 이벤트의 재미인데, 마크를 띄우면
        // 머리 위 표시 하나로 정체가 새어 나가고 심지어 오검거 추격대로 오인된다.
        NpcPenaltyMark.SetVisible(
            m_controller,
            NpcLocomotionMotion.IsPenaltyLocomotion(state) && !m_penalty.IsUndercoverDuty
        );

        // 새 모션을 이 프레임에 바로 반영한다 — 다음 Update를 기다리면 한 프레임 늦는다.
        if (m_animator != null)
            Apply(ResolveMotion());
    }
}
