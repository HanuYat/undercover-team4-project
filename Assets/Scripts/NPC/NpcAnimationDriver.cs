using UnityEngine;

/// <summary>
/// NPC 모션의 단일 결정 지점 — 부품들의 제안 중 하나를 골라 Animator의 State(int)에 쓴다. (#502)
/// 표현 축은 <see cref="NpcLocomotionMotion"/>(속도)과 <see cref="NpcOneShotMotion"/>(단발)이 나눠 갖고,
/// 코어는 Animator·이벤트 구독·기준 상태 매핑·누움 신호(#363)·우선순위 중재를 맡는다.
/// 누움이 코어에 남는 것은 판정 입력이 기준 상태·기상·밧줄 세 곳에 걸쳐 있어서다.
///
/// FSM 상태는 그대로 Animator 번호가 된다(Idle=0, Walk=1...). 컨트롤러의 통합 이벤트는 동기화를
/// 거쳐 모든 피어에서 발생하므로 서버·클라 어디서든 같은 모션이 나온다. (#56)
/// </summary>
[RequireComponent(typeof(NpcController))]
[RequireComponent(typeof(NpcLocomotionMotion))]
[RequireComponent(typeof(NpcOneShotMotion))]
public class NpcAnimationDriver : MonoBehaviour
{
    [SerializeField] private Animator m_animator;

    private NpcController m_controller;
    private NpcDutyAgent m_penalty; // 앵그리 마크 판정이 임무 종류를 읽는다 (#371)
    private NpcReaction m_reaction; // 스윙 순간 이벤트가 이 부품에서 온다 (#220)
    private NpcLocomotionMotion m_locomotion;
    private NpcOneShotMotion m_oneShot;
    private INpcMotionSource[] m_sources; // 우선순위 내림차순
    private NpcState m_baseState;

    // 묶임/풀림 엣지 — 바뀌는 순간에만 속도 추적·콜라이더를 다시 시드한다 (#369/#513)
    private bool m_ropeBoundMotion;
    private bool m_ropeProneMotion;

    /// <summary>
    /// 바닥에 누워 있는가 — 기절·묶임 상태로 아직 일어나기 시작하지 않은 구간. (#363/#513)
    /// <see cref="NpcProneCollider"/>가 몸통 캡슐을, <see cref="NpcRagdoll"/>이 기상 시점을 여기 맞춘다.
    /// FSM 상태로는 판별할 수 없다 — 기상 중에도 Stunned이고, 묶여 놓인 대상은 Captured다.
    /// </summary>
    public bool IsProne { get; private set; }

    /// <summary>누움 여부가 바뀔 때 발행 — 모션과 콜라이더가 같은 순간에 움직이도록. (#363)</summary>
    public event System.Action<bool> OnProneChanged;

    /// <summary>지금의 FSM 기준 상태 — 부품들이 자기 판별의 기준으로 읽는다.</summary>
    public NpcState BaseState => m_baseState;

    /// <summary>로코모션을 멈출 구간인가 — 안 막으면 누운 자세가 걷기/정지로 덮인다. (#292/#369)</summary>
    public bool SuppressLocomotion => IsRopeProne || m_controller.Stun.IsStunned;

    /// <summary>기상 모션이 물러나도 되는가 — 누운 기준 상태거나 서버 표시가 살아 있으면 버틴다. (#513/#564)</summary>
    public bool CanLeaveStandUp =>
        !IsRopeProne && m_baseState != NpcState.Stunned && !m_controller.StandUp.IsStandingUp;

    /// <summary>밧줄이 걸려 있는가 — 갈라 보면 E로 놓는 순간 묶인 몸이 벌떡 일어선다. (#513)</summary>
    private bool IsRopeBound => m_controller.Rope.IsRoped || m_controller.Rope.IsTethered;

    /// <summary>
    /// 밧줄 때문에 바닥에 있는가 — 누운 모션·콜라이더의 기준. 기상 예약을 함께 보고 기상 모션
    /// 재생 여부를 빼는 것은 둘 다 표시들의 도착 순서 때문이다. (#513)
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

        // 나열 순서가 아니라 우선순위 값이 순서를 정한다 — 잘못 적어도 결과가 바뀌지 않게
        m_sources = new INpcMotionSource[] { m_locomotion, m_oneShot };
        System.Array.Sort(m_sources, (a, b) => b.Priority.CompareTo(a.Priority));
    }

    private void Start()
    {
        // 로컬 FSM이 아니라 컨트롤러의 통합 이벤트를 구독한다 — 클라에서는 동기화가 이 이벤트를 낸다 (#56)
        m_controller.OnStateChanged += HandleStateChanged;
        m_penalty.OnPenaltyDutyChanged += HandlePenaltyDutyChanged;
        m_reaction.OnAttackSwing += HandleAttackSwing; // 스윙은 전이가 아닌 순간 이벤트 (#220)
        m_controller.OnStandUp += HandleStandUp; // 기상도 마찬가지 (#269)
        m_controller.Stun.OnStunnedChanged += HandleStunnedChanged; // 스턴은 오버레이라 따로 온다 (#292)
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

    /// <summary>우선순위 순으로 물어보고 처음 응답한 것이 이긴다. 아무도 없으면 기준 상태 모션. (#502)</summary>
    private int ResolveMotion()
    {
        for (int i = 0; i < m_sources.Length; i++)
        {
            if (m_sources[i].TryGetMotion(out int motion))
                return motion;
        }

        return AnimatorBaseState(m_baseState);
    }

    // Animator에 쓰는 유일한 지점. 같은 값을 거르는 것은 안전 때문이다(Any State 전이는 조건이 참인
    // 동안 계속 성립). 캐시가 아니라 실제 값과 견준다 — 래그돌(#571)이 Animator를 껐다 켜기 때문이다.
    private void Apply(int motion)
    {
        if (motion == m_animator.GetInteger(NpcAnimStates.s_stateHash))
            return;

        m_animator.SetInteger(NpcAnimStates.s_stateHash, motion);
    }

    /// <summary>스윙 클립 index 지정 — <see cref="NpcOneShotMotion"/>이 스윙 직전에 부른다. (#220)</summary>
    public void SetSwingVariant(int variant)
    {
        if (m_animator != null)
            m_animator.SetFloat(NpcAnimStates.s_swingVariantHash, variant);
    }

    // 상태 전이 훅만으론 놓친다(별개 NetworkVariable이라 도착 순서 무보장). 묶임과 누움을 따로 보는
    // 것은 풀기가 기상 예약을 걸자마자 줄을 빼기 때문이다. (#269/#369/#513)
    private void TrackRopeEdges()
    {
        bool bound = IsRopeBound;
        if (m_ropeBoundMotion != bound)
        {
            m_ropeBoundMotion = bound;
            if (bound)
                m_oneShot.CancelStandUp(); // 다시 묶였으면 누운 자세로 되돌아간다
            RefreshProne();
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

    /// <summary>누움을 다시 판정해 바뀌었으면 알린다 — 기준 상태·기상·묶임을 건드린 직후에. (#363)</summary>
    public void RefreshProne()
    {
        // 사망(#571)은 기상과 무관하게 항상 누움이다 — 시체가 다시 서는 일은 없다
        bool prone =
            m_baseState == NpcState.Dead
            || IsRopeProne
            || (m_baseState == NpcState.Stunned && !m_oneShot.IsStandingUp);
        if (prone == IsProne)
            return;

        IsProne = prone;
        OnProneChanged?.Invoke(prone);
    }

    // FSM 기준 상태 → Animator base 번호. 대응 상태가 없는 것들은 가까운 모션을 빌려 쓴다.
    private int AnimatorBaseState(NpcState state)
    {
        // 묶여 누우면 FSM 상태와 무관하게 누운 모션 — 없으면 서서 끌려간다 (#369/#513)
        if (IsRopeProne)
            return (int)NpcState.Stunned;

        return state switch
        {
            // 저항 base는 이동 여부로 갈린다 (#254) — 보통은 로코모션이 먼저 답한다
            NpcState.Attack => m_locomotion.IsResistMoving ? (int)NpcState.Run : (int)NpcState.Idle,
            // 사망(#571)은 누운 기절 모션을 빌린다 — 래그돌이 붙으면 Animator가 꺼져 쓰이지 않는다
            NpcState.Dead => (int)NpcState.Stunned,
            NpcState.Intruding => (int)NpcState.Walk, // 수갑 없이 걸어 들어온다 (#231)
            NpcState.Jailed => (int)NpcState.Escorted, // 수갑 찬 걷기를 빌린다 (#228)
            NpcState.Detained => (int)NpcState.Walk, // 아래 셋은 페널티 (#277~#279)
            NpcState.Chasing => (int)NpcState.Run,
            NpcState.PenaltyEscorting => (int)NpcState.Walk,
            NpcState.Sprinting => (int)NpcState.Run, // 도주와 같은 배율로 달린다 (#106)
            _ => (int)state,
        };
    }

    private void HandleAttackSwing(int variant) => m_oneShot.PlaySwing(variant);

    private void HandleStandUp() => m_oneShot.PlayStandUp();

    // 스턴 오버레이(#292)는 FSM 상태를 안 바꾸므로 "Stunned로 전이한 것처럼" 먹인다
    private void HandleStunnedChanged(bool stunned) =>
        HandleStateChanged(stunned ? NpcState.Stunned : m_controller.CurrentState);

    // 임무 종류가 전파된 순간 마크 판정을 다시 태운다 — 상태와 임무 플래그가 별개 값이라 순서 무보장 (#371)
    private void HandlePenaltyDutyChanged() => HandleStateChanged(m_controller.CurrentState);

    private void HandleStateChanged(NpcState state)
    {
        // 스턴 중에는 계속 누워 있어야 한다 — 오버레이는 CurrentState를 얼리지 않아 외부에서
        // 걸린 전이가 그대로 들어온다 (#292)
        if (m_controller.Stun.IsStunned)
            state = NpcState.Stunned;

        m_baseState = state;
        m_oneShot.OnBaseStateChanged(state);
        m_locomotion.OnBaseStateChanged(state);
        RefreshProne(); // 기준 상태와 기상 표시가 모두 확정된 뒤다 (#363)

        // 앵그리 마크(#280) — 납치범(#371)은 제외한다: 시민과 구분되지 않는 것이 그 이벤트의 재미다
        NpcPenaltyMark.SetVisible(
            m_controller,
            NpcLocomotionMotion.IsPenaltyLocomotion(state) && !m_penalty.IsUndercoverDuty
        );

        // 이 프레임에 바로 반영한다 — 다음 Update를 기다리면 한 프레임 늦는다
        if (m_animator != null)
            Apply(ResolveMotion());
    }
}
