using UnityEngine;

/// <summary>
/// FSM 상태 변경을 Animator의 State(int) 파라미터로 전달한다.
/// NpcState enum 값이 그대로 Animator 상태 번호가 되므로(Idle=0, Walk=1...),
/// 새 상태가 생겨도 이 스크립트는 대체로 수정할 필요가 없다.
/// NpcController.OnStateChanged는 네트워크 동기화를 거쳐 모든 피어에서 발생하므로
/// 서버·클라이언트 어디서든 같은 모션이 재생된다. (#56)
///
/// 공격만 예외다(#220): 저항(Attack) 상태의 base 모션은 "버틴 자세"(Idle)이고,
/// Animator의 Attack 번호(3)는 <b>단발 스윙 클립</b> 전용이다. 스윙은 상태 전이가 아니라
/// <see cref="NpcController.OnAttackSwing"/> 순간 이벤트로 오며, State int를 잠깐 Attack으로
/// 펄스했다가 되돌리는 식으로 표현한다 — 로코모션이 전부 Any State(State==N) 전이라
/// 트리거 오버레이는 스윙을 매 프레임 끊어버리기 때문이다.
/// </summary>
[RequireComponent(typeof(NpcController))]
public class NpcAnimationDriver : MonoBehaviour
{
    private static readonly int s_stateHash = Animator.StringToHash("State");
    /// <summary>
    /// 스윙 1회에 재생할 단발 클립 번호를 고르는 Animator 파라미터 이름.
    /// Attack 상태의 블렌드 트리가 이 값으로 클립을 고른다 — 컨트롤러를 만드는
    /// NpcAnimatorControllerBuilder(Editor)가 이 상수를 참조하므로 이름이 어긋날 수 없다.
    /// </summary>
    public const string k_swingVariantParam = "SwingVariant";

    private static readonly int s_swingVariantHash = Animator.StringToHash(k_swingVariantParam);

    /// <summary>
    /// 자물쇠 해제 시작(Begin) 모션의 Animator 상태 번호. (#261)
    /// <b>NpcState enum 값이 아니다</b> — 해제는 FSM 상태가 아니라 침입(Intruding) 안의 한 페이즈이고,
    /// 그 구분은 서버 FSM 내부값이라 클라이언트가 모른다. 그래서 상태를 늘리는 대신
    /// enum이 앞으로 자라도 겹치지 않을 만큼 떨어진 번호를 모션 전용으로 쓴다.
    /// NpcAnimatorControllerBuilder(Editor)가 이 상수로 Any State 전이 조건을 만든다.
    /// </summary>
    public const int k_unlockingBeginAnimState = 100;

    /// <summary>
    /// 자물쇠 해제 반복(Loop) 모션의 Animator 상태 번호. (#261)
    /// Begin과 번호를 나눠 갖는 것이 핵심이다 — 하나로 두면 Loop에 들어간 뒤에도 Any State 조건이
    /// 계속 참이라 매번 Begin으로 되돌아가 동작이 무한히 다시 시작된다
    /// (canTransitionToSelf는 자기 자신으로의 재진입만 막는다).
    /// </summary>
    public const int k_unlockingLoopAnimState = 101;

    /// <summary>
    /// 기절에서 일어나는(StandUp) 모션의 Animator 상태 번호. (#269)
    /// 해제 모션과 같은 이유로 NpcState enum 값이 아니다 — 일어나는 동안 FSM 상태는 여전히 Stunned이고,
    /// 그 구분은 서버 FSM 내부값이라 클라이언트가 모른다. enum과 겹치지 않는 전용 번호를 쓴다.
    /// NpcAnimatorControllerBuilder(Editor)가 이 상수로 Any State 전이 조건을 만든다.
    /// </summary>
    public const int k_standUpAnimState = 102;

    /// <summary>
    /// 저항형 제압 전환(그로기) 모션의 Animator 상태 번호. (#332)
    /// 제압 순간의 전환은 FSM 상태가 아니라 Captured 진입 위에 얹는 연출이라 전용 번호를 쓴다 —
    /// 두들겨 맞아 제압된 저항형은 곧바로 고개 숙인 대기 자세로 스냅되는 대신 서서 헤롱거리다 가라앉는다.
    /// NpcAnimatorControllerBuilder(Editor)가 이 상수로 Any State 전이 조건을 만든다.
    /// <b>번호 주의:</b> 같은 컨트롤러의 Any State 조건은 번호가 겹치면 우선순위에 밀려 엉뚱한 모션이
    /// 재생되므로 새 모션 전용 번호는 기존과 겹치지 않게 고를 것. (103은 과거 괴한 윈드업이 쓰다 제거돼 현재는 빈 번호)
    /// </summary>
    public const int k_subdueGroggyAnimState = 105;

    /// <summary>
    /// 도주형 제압 전환(구르기) 모션의 Animator 상태 번호. (#332)
    /// 달리다 붙잡힌 관성을 표현한다 — 태클당해 구르고 스스로 일어난 뒤 대기 자세로 이어진다
    /// (클립 끝 프레임이 완전 기립이라 Captured 자세와 크로스페이드로 자연 연결).
    /// </summary>
    public const int k_subdueRollAnimState = 106;

    // 연행 근접 정지(#97) 모션 전환 임계값 — 실제 이동 속도(m/s) 기준.
    // 켜짐/꺼짐 경계를 다르게 둬(히스테리시스) 정지 직전 감속 구간에서 모션이 떨리는 것을 막는다.
    // 꺼짐 임계값은 걷기 최저 속도(~1.6m/s)보다 충분히 낮게 — 추종 중 순간 감속에 오작동하지 않는 선
    private const float k_escortMoveOnSpeed = 0.5f;
    private const float k_escortMoveOffSpeed = 0.25f;
    // 프레임 노이즈 완화용 지수 평활 계수 — 클수록 정지 반응이 빨라진다.
    // 근접 정지가 velocity를 즉시 0으로 끊으므로(#97) 평활이 식는 시간이 곧 모션 전환 지연이다
    private const float k_speedSmoothing = 25f;

    // 오검거 페널티 상태(#277~#279)의 걷기↔달리기 전환 임계값(m/s) — 켜짐/꺼짐을 다르게 둔 히스테리시스.
    // 추격 가속(걷기 속도→7)이 이 경계를 지나는 순간 달리기 모션으로 넘어간다
    private const float k_penaltyRunOnSpeed = 4f;
    private const float k_penaltyRunOffSpeed = 3.4f;

    [Header("공격 스윙 (#220)")]
    [Tooltip("스윙 1회당 Attack(단발) 모션을 유지하는 시간(초) — 이후 버틴 자세로 복귀한다. 저항 공격 주기보다 짧고 타격 오프셋보다 길게")]
    [SerializeField] private float m_swingAnimSeconds = 0.9f;

    [Header("자물쇠 해제 (#261)")]
    [Tooltip("해제 시작(Begin) 모션을 유지하는 시간(초) — 이후 반복(Loop)으로 넘어간다. Begin 클립 길이(0.63초)에 맞춘 값")]
    [SerializeField] private float m_unlockBeginSeconds = 0.63f;

    [Header("제압 전환 (#332)")]
    [Tooltip("도주형 제압 시 구르기 모션을 유지하는 시간(초) — 클립(Roll01) 길이 1.3초에 맞춘 값. 이후 그로기로 넘어간다")]
    [SerializeField] private float m_subdueRollSeconds = 1.3f;

    [SerializeField] private Animator m_animator;

    private NpcController m_controller;
    private Vector3 m_lastPosition;
    private float m_smoothedSpeed;
    private bool m_escortMoving;
    private bool m_resistMoving; // 저항(Attack) 추격 중 이동/정지 판별 — 걷기 ↔ 버틴 자세 전환 (#254)
    // 침입(Intruding) 이동/해제 판별 — 자물쇠까지 걷기 ↔ 도착 후 해제 모션 전환 (#261)
    private bool m_intrudeMoving;
    // 오검거 페널티 상태의 현재 로코모션 모션 번호(Idle/Walk/Run) — 속도 히스테리시스 전환용 (#277~#279)
    private int m_penaltyMotion;
    // 해제 시작(Begin) 모션을 반복(Loop)으로 넘길 시각. 0 이하면 대기 중 아님 (#261)
    private float m_unlockBeginUntil;
    // 현재 스윙 모션을 유지할 종료 시각. 0 이하면 스윙 중 아님. 스윙이 끝나면 base 상태로 되돌린다 (#220)
    private float m_swingUntil;
    // 제압 전환(그로기/구르기) 모션을 유지할 종료 시각. 0 이하면 전환 중 아님 — 끝나면 Captured 대기 자세로 (#332)
    private float m_subdueUntil;
    // 구르기가 끝나면 곧바로 대기 자세가 아니라 짧은 그로기를 한 번 더 거친다 — 그 예약 플래그 (#332)
    private bool m_subdueRollThenGroggy;
    // 일어나는 모션 재생 중인가 (#269). 스윙과 달리 시간으로 끊지 않는다 — 클립이 1회 재생이라
    // 마지막 프레임(선 자세)에서 멈추고, 곧 도착하는 Idle 전이가 배회 모션으로 이어받는다.
    // 시간으로 끊으면 그 사이 한 프레임 동안 누운 자세(base)가 스쳐 지나가 툭 끊겨 보인다.
    private bool m_standingUp;
    // 스윙이 끝난 뒤 되돌아갈 FSM 기준 상태 — 저항(Attack)이면 버틴 자세(Idle)로 복귀한다 (#220)
    private NpcState m_baseState;

    private void Awake()
    {
        m_controller = GetComponent<NpcController>();

        if (m_animator == null)
            m_animator = GetComponentInChildren<Animator>();
    }

    private void Start()
    {
        // 로컬 FSM 이벤트가 아닌 컨트롤러의 통합 이벤트를 구독한다 — 클라이언트에서는
        // NetworkVariable 동기화가, 오프라인에서는 로컬 FSM이 이 이벤트를 발생시킨다 (#56)
        m_controller.OnStateChanged += HandleStateChanged;
        // 스윙은 상태 전이가 아니라 순간 이벤트 — 저항 상태를 유지한 채 매 타격마다 단발 스윙을 얹는다 (#220)
        m_controller.OnAttackSwing += HandleAttackSwing;
        // 일어나기도 상태 전이가 아닌 순간 이벤트 — 기절 상태를 유지한 채 마지막 구간에만 얹는다 (#269)
        m_controller.OnStandUp += HandleStandUp;
        // 스턴은 상태 전이가 아니라 오버레이라 OnStateChanged로 안 온다 — 따로 구독한다 (#292)
        m_controller.OnStunnedChanged += HandleStunnedChanged;
        HandleStateChanged(m_controller.CurrentState);
    }

    private void OnDestroy()
    {
        if (m_controller != null)
        {
            m_controller.OnStateChanged -= HandleStateChanged;
            m_controller.OnAttackSwing -= HandleAttackSwing;
            m_controller.OnStandUp -= HandleStandUp;
            m_controller.OnStunnedChanged -= HandleStunnedChanged;
        }
    }

    // 저항 NPC의 공격 스윙 1회 — Animator State를 Attack(단발 스윙 클립)으로 잠깐 펄스한다.
    // 이 컨트롤러의 로코모션이 전부 Any State(State==N) 전이라, State int 하나만 참이어야 스윙이
    // 중간에 끊기지 않는다 — 그래서 트리거 오버레이가 아니라 int 펄스를 쓴다. 스윙 종료는 Update가 처리.
    // variant는 서버가 뽑아 전 피어에 넘긴 클립 index — 데미지는 NpcResistState가 그 클립의 타격
    // 오프셋에 맞춰 넣으므로, 여기선 같은 클립을 재생만 하면 주먹 닿는 순간과 HP 감소가 일치한다. (#220)
    private void HandleAttackSwing(int variant)
    {
        if (m_animator == null)
            return;

        // 상태 변경(NetworkVariable)과 스윙 알림(ClientRpc)은 서로 다른 네트워크 경로라
        // 도착 순서가 보장되지 않는다. 제압 직전에 발사된 스윙이 Captured 전이보다 늦게 도착하면
        // 이미 제압된 NPC가 원격 클라에서 m_swingAnimSeconds 동안 헛스윙을 한다. (리뷰 반영)
        //
        // '스윙이 성립할 수 없는 상태'에서만 막는다 — 이 상태들은 저항으로 되돌아가지 않으므로
        // 늦게 온 스윙은 무조건 유령이다. 반대로 배회(Idle/Walk/Run)는 막지 않는다:
        // 상태 동기화가 늦어 아직 Attack을 못 받았을 뿐일 수 있고, 그때 스윙을 버리면
        // 예고 동작이 통째로 사라져 "언제 맞는지 모른다"는 #220의 목적이 깨진다.
        if (!CanSwingIn(m_baseState))
            return;

        // 이번 스윙에 쓸 단발 클립을 지정한다 — 반드시 State 펄스보다 먼저다.
        // Attack 상태에 들어간 뒤에 바꾸면 재생 중인 클립이 도중에 갈아끼워져 모션이 튄다.
        // index는 서버가 뽑아 전 피어에 넘긴 값이라 화면마다 같은 클립이 나오고, 서버가 그 클립의
        // 타격 오프셋으로 데미지를 넣으므로 주먹 닿는 순간과 HP 감소가 일치한다. (#220)
        m_animator.SetFloat(s_swingVariantHash, variant);

        m_animator.SetInteger(s_stateHash, (int)NpcState.Attack);
        m_swingUntil = Time.time + m_swingAnimSeconds;
    }

    // 스턴 오버레이 온/오프 (#292) — 기존 기절 표현을 그대로 재사용한다.
    // 오버레이는 FSM 상태를 바꾸지 않으므로, 드라이버에는 "Stunned로 전이한 것처럼" 먹여
    // 누운 자세·일어나기·스윙 취소 로직이 손대지 않고 그대로 동작하게 한다.
    // 풀릴 때는 진짜 현재 상태로 되돌린다 — 반응군이면 곧이어 도주 전이가 덮어쓴다.
    private void HandleStunnedChanged(bool stunned)
    {
        HandleStateChanged(stunned ? NpcState.Stunned : m_controller.CurrentState);
    }

    // 기절이 풀리기 직전 일어나는 모션 — 스윙과 같은 int 펄스 방식이다(트리거 오버레이는 Any State
    // 전이에 매 프레임 끊긴다). FSM 상태는 아직 Stunned라 NPC는 제자리에 있고, 모션만 누운 자세에서
    // 일어나는 자세로 바뀐다. 복귀는 Update가 처리한다 — 유지 시간이 끝나거나 도중에 다시 묶이면 누운 자세로.
    private void HandleStandUp()
    {
        if (m_animator == null)
            return;
        if (m_baseState != NpcState.Stunned)
            return; // 늦게 도착한 알림 — 이미 다른 상태면 유령 모션이 된다 (스윙과 같은 방어)

        m_animator.SetInteger(s_stateHash, k_standUpAnimState);
        m_standingUp = true;
    }

    /// <summary>스윙 모션이 성립할 수 있는 기준 상태인가 — 구속·무력화 상태에서는 공격이 나올 수 없다. (#220)
    /// 오검거 페널티 상태(#277~#279)도 저항으로 되돌아가지 않으므로 늦게 도착한 스윙은 유령이다 — 차단.</summary>
    private static bool CanSwingIn(NpcState state) =>
        state is not (
            NpcState.Captured or NpcState.Escorted or NpcState.Stunned
            or NpcState.Detained or NpcState.Chasing or NpcState.PenaltyEscorting
        );

    // FSM 기준 상태에 대응하는 Animator base 번호. Attack 번호(3)는 단발 스윙 전용이라 base로 쓰지 않는다 (#220).
    // 저항(Attack)의 base는 이동 여부로 갈린다 — 추격 중이면 달리기(Run), 사거리 안에서 멈추면 버틴 자세(Idle) (#254).
    // 침입(Intruding)은 대응 Animator 상태가 없어 평범한 걷기(Walk)를 빌려 쓴다 —
    // 수갑을 차지 않은 채 본부로 걸어 들어오는 그림이라 Escorted가 아니라 Walk다. (#231)
    private int AnimatorBaseState(NpcState state)
    {
        return state switch
        {
            NpcState.Attack => m_resistMoving ? (int)NpcState.Run : (int)NpcState.Idle,
            NpcState.Intruding => (int)NpcState.Walk,
            // 오검거 페널티 상태들도 대응 Animator 상태가 없다 — 속도 기반 로코모션을 빌려 쓴다 (#277~#279).
            // 여기 값은 진입 시드일 뿐이고, 이후 Update의 UpdatePenaltyLocomotion이 속도로 갈아탄다
            NpcState.Detained => (int)NpcState.Walk,
            NpcState.Chasing => (int)NpcState.Run,
            NpcState.PenaltyEscorting => (int)NpcState.Walk,
            // 임시 거처 이송도 대응 Animator 상태가 없다 — 걷기 모션을 빌려 쓴다 (#291)
            NpcState.Holding => (int)NpcState.Walk,
            _ => (int)state,
        };
    }

    private void Update()
    {
        // 연행(Escorted)은 실제 이동 속도가 필요하다. transform 이동량 기준이라
        // 클라이언트에서도 NetworkTransform이 움직여 주는 값을 그대로 쓸 수 있다 — 별도 동기화 불필요.
        if (m_animator == null || Time.deltaTime <= 0f)
            return;

        // 스윙 모션 유지 시간이 끝나면 버틴 자세(base)로 되돌린다 — 저항 상태를 유지한 채 스윙만 단발로 얹는 방식 (#220)
        if (m_swingUntil > 0f && Time.time >= m_swingUntil)
        {
            m_swingUntil = 0f;
            m_animator.SetInteger(s_stateHash, AnimatorBaseState(m_baseState));
        }

        // 구르기 유지 시간이 끝나면 그로기로 넘어간다 (#332) — 역동적으로 굴러 일어난 몸이 곧바로
        // 얌전해지는 낙차가 어색해서(팀 피드백) 그로기를 거친다. 그로기는 시간으로 끝나지 않는다:
        // 플레이어가 연행(E)하러 올 때까지 헤롱거리며 유지되고, 상태 전이(Escorted·방치 풀림 등)가
        // 오면 HandleStateChanged가 새 모션으로 갈아탄다. 타이머는 구르기에만 쓰인다.
        if (m_subdueUntil > 0f && Time.time >= m_subdueUntil)
        {
            m_subdueUntil = 0f;
            if (m_subdueRollThenGroggy)
            {
                m_subdueRollThenGroggy = false;
                m_animator.SetInteger(s_stateHash, k_subdueGroggyAnimState);
            }
        }

        // 일어나던 중에 다시 밧줄로 묶이면 취소하고 누운 자세로 되돌린다 — 끌려가는데 서 있으면 안 된다.
        // 정상 종료(기절 해제)는 여기가 아니라 Idle 상태 전이가 처리한다 (#269)
        if (m_standingUp && m_controller.IsRoped)
        {
            m_standingUp = false;
            m_animator.SetInteger(s_stateHash, AnimatorBaseState(m_baseState));
        }

        // 스턴 오버레이 중에는 속도 기반 로코모션을 돌리지 않는다 (#292) — 상태 enum이 그대로라
        // 연행·저항·페널티 상태에서 기절하면 아래 블록이 매 프레임 기절 포즈를 덮어쓴다.
        if (m_controller.IsStunned)
            return;

        NpcState state = m_controller.CurrentState;
        if (
            !IsHandcuffedMotion(state)
            && !IsPenaltyLocomotion(state)
            && state != NpcState.Attack
            && state != NpcState.Intruding
        )
            return;

        float rawSpeed = (transform.position - m_lastPosition).magnitude / Time.deltaTime;
        m_lastPosition = transform.position;
        m_smoothedSpeed = Mathf.Lerp(m_smoothedSpeed, rawSpeed, Time.deltaTime * k_speedSmoothing);

        // 저항(Attack): 추격 중이면 달리기, 사거리 안에서 멈추면 버틴 자세 — 이동/정지를 속도로 구분한다 (#254)
        if (state == NpcState.Attack)
        {
            UpdateResistMotion();
            return;
        }

        // 오검거 페널티(수용·추격·호송): 대응 Animator 상태가 없어 속도로 Idle/Walk/Run을 가른다 (#277~#279)
        if (IsPenaltyLocomotion(state))
        {
            UpdatePenaltyLocomotion();
            return;
        }

        // 침입(Intruding): "자물쇠까지 걷기 ↔ 도착 후 해제"가 한 FSM 상태 안에서 일어난다 (#231/#261).
        // 해제 페이즈는 NpcIntrudeState 내부값이라 클라이언트가 알 수 없으므로, 연행·수감과 같은
        // 속도 기준으로 가른다 — 해제 중엔 그 자리에 완전히 멈추므로 이 판별이 정확하다.
        // (이 분기가 없으면 자물쇠를 따는 내내 제자리에서 걷기 모션이 돈다 — 대응 구간이 안 보인다)
        if (state == NpcState.Intruding)
        {
            if (m_intrudeMoving && m_smoothedSpeed < k_escortMoveOffSpeed)
            {
                m_intrudeMoving = false;
                m_animator.SetInteger(s_stateHash, k_unlockingBeginAnimState);
                m_unlockBeginUntil = Time.time + m_unlockBeginSeconds;
            }
            else if (!m_intrudeMoving && m_smoothedSpeed > k_escortMoveOnSpeed)
            {
                m_intrudeMoving = true;
                m_unlockBeginUntil = 0f;
                m_animator.SetInteger(s_stateHash, (int)NpcState.Walk);
            }
            // 시작 동작이 끝나면 반복으로 넘긴다. 이 전환을 Animator의 exit time에 맡기지 않는 이유는,
            // 번호가 Begin에 머물러 있으면 Loop로 넘어간 뒤에도 Any State 조건이 참이라
            // 다시 Begin으로 끌려가 동작이 무한 반복되기 때문이다.
            else if (m_unlockBeginUntil > 0f && Time.time >= m_unlockBeginUntil)
            {
                m_unlockBeginUntil = 0f;
                m_animator.SetInteger(s_stateHash, k_unlockingLoopAnimState);
            }
            return;
        }

        // 연행(Escorted)은 "따라 걷기 ↔ 근접 정지"가, 수감(Jailed)은 "유치장까지 걷기 ↔ 수용 정지"가
        // 각각 한 FSM 상태 안에서 일어나므로(#97/#228) 속도로 모션만 구분한다.
        if (m_escortMoving && m_smoothedSpeed < k_escortMoveOffSpeed)
        {
            m_escortMoving = false;
            // 정지 중에는 수갑 찬 대기 자세(Captured 모션)를 빌려 쓴다 — FSM 상태는 Escorted 유지
            m_animator.SetInteger(s_stateHash, (int)NpcState.Captured);
        }
        else if (!m_escortMoving && m_smoothedSpeed > k_escortMoveOnSpeed)
        {
            m_escortMoving = true;
            m_animator.SetInteger(s_stateHash, (int)NpcState.Escorted);
        }
    }

    // 저항(Attack) 이동/정지 모션 전환 — 추격 중이면 달리기, 사거리 안에서 멈추면 버틴 자세(Idle). (#254)
    // 스윙 중(m_swingUntil>0)에는 단발 스윙 클립이 State를 점유하므로 base를 건드리지 않는다 —
    // 플래그만 갱신하고, 스윙이 끝나면 Update 상단이 올바른 base(걷기/버틴 자세)로 되돌린다. (#220)
    private void UpdateResistMotion()
    {
        bool swinging = m_swingUntil > 0f;

        if (m_resistMoving && m_smoothedSpeed < k_escortMoveOffSpeed)
        {
            m_resistMoving = false;
            if (!swinging)
                m_animator.SetInteger(s_stateHash, (int)NpcState.Idle);
        }
        else if (!m_resistMoving && m_smoothedSpeed > k_escortMoveOnSpeed)
        {
            m_resistMoving = true;
            if (!swinging)
                m_animator.SetInteger(s_stateHash, (int)NpcState.Run);
        }
    }

    // 오검거 페널티 로코모션 — 속도 기준 Idle/Walk/Run 3단 전환. 히스테리시스는 걷기 경계(연행 상수 공유)와
    // 달리기 경계(k_penaltyRun*) 두 겹이다. 경계 사이 속도에서는 현재 모션을 유지해 떨림을 막는다 (#277~#279)
    private void UpdatePenaltyLocomotion()
    {
        int desired = m_penaltyMotion;
        if (m_smoothedSpeed < k_escortMoveOffSpeed)
            desired = (int)NpcState.Idle;
        else if (m_smoothedSpeed > k_penaltyRunOnSpeed)
            desired = (int)NpcState.Run;
        else if (m_smoothedSpeed > k_escortMoveOnSpeed && m_smoothedSpeed < k_penaltyRunOffSpeed)
            desired = (int)NpcState.Walk;

        if (desired == m_penaltyMotion)
            return;

        m_penaltyMotion = desired;
        m_animator.SetInteger(s_stateHash, desired);
    }

    /// <summary>
    /// 수갑 찬 채 이동하는 상태인가 — 걷기(Escorted 모션) ↔ 정지(Captured 모션)를 속도로 구분해야 하는 상태들.
    /// 수감(Jailed)은 대응하는 Animator 상태가 없어 연행 모션을 빌려 쓴다 (#228).
    /// </summary>
    private static bool IsHandcuffedMotion(NpcState state) =>
        state is NpcState.Escorted or NpcState.Jailed;

    /// <summary>
    /// 오검거 페널티 상태인가 — 수갑 없이 걷기/달리기 로코모션을 속도로 가르는 상태들 (#277~#279).
    /// 앵그리 마크(#280) 표시 조건과 같은 집합이다 — 페널티에 얽힌 동안 계속 표시된다.
    /// </summary>
    private static bool IsPenaltyLocomotion(NpcState state) =>
        state is NpcState.Detained or NpcState.Chasing or NpcState.PenaltyEscorting;

    /// <summary>
    /// 제압 전환 모션을 시드한다 — 직전 상태가 저항(Attack)이면 그로기, 도주(Run)면 구르기. (#332)
    /// 그 외(수갑 채널링 체포·기절 후 재제압 등)는 저항 없이 잡히는 그림이라 전환 없이 false를 돌려주고,
    /// 호출부가 기존대로 대기 자세를 직접 시드한다.
    /// </summary>
    private bool TryBeginSubdueTransition(NpcState previous)
    {
        switch (previous)
        {
            case NpcState.Attack:
                // 그로기는 시간으로 끝나지 않는다 — 플레이어가 연행하러 올 때까지 헤롱거리며 유지 (팀 확정)
                m_animator.SetInteger(s_stateHash, k_subdueGroggyAnimState);
                return true;

            case NpcState.Run:
                m_animator.SetInteger(s_stateHash, k_subdueRollAnimState);
                m_subdueUntil = Time.time + m_subdueRollSeconds;
                m_subdueRollThenGroggy = true; // 굴러 일어난 뒤 그로기로 넘어가 유지된다
                return true;

            default:
                return false;
        }
    }

    private void HandleStateChanged(NpcState state)
    {
        // 제압 전환 분기용 직전 상태 — base를 덮어쓰기 전에 읽는다 (#332)
        NpcState previous = m_baseState;

        // 상태 전이는 스윙보다 우선한다 — 진행 중이던 스윙을 취소하고 새 base 모션을 즉시 적용한다
        // (예: 저항 중 스윙하다 제압되면 그 프레임에 Captured로 넘어가야 한다) (#220)
        m_baseState = state;
        m_swingUntil = 0f;
        m_subdueUntil = 0f; // 전환 중 다른 상태로 바뀌면(재연행 등) 전환도 끝난다
        m_subdueRollThenGroggy = false;
        m_standingUp = false; // 상태가 바뀌면 일어나기도 끝난다 — 새 base 모션이 즉시 적용된다

        // 앵그리 마크(#280) — 페널티 상태(수용~호송) 동안 머리 위에 표시한다. 이 이벤트는 동기화를 거쳐
        // 모든 피어에서 발생하므로(#56) 원격 클라·CCTV 화면에서도 같은 시점에 켜지고 꺼진다
        NpcPenaltyMark.SetVisible(m_controller, IsPenaltyLocomotion(state));

        // 저항(Attack) 진입은 추격으로 시작하는 것이 일반적이라 달리기로 시드하고 이동 판별을 초기화한다 —
        // AnimatorBaseState(Attack)가 m_resistMoving을 읽으므로 반드시 아래 SetInteger 이전에 정한다.
        // 표적이 이미 사거리 안이면 다음 몇 프레임 안에 Update가 버틴 자세로 낮춘다. (#254)
        if (state == NpcState.Attack)
        {
            m_resistMoving = true;
            m_lastPosition = transform.position;
            m_smoothedSpeed = k_escortMoveOnSpeed;
        }

        // 제압 순간의 전환 연출 (#332) — 도주·저항 중이던 NPC가 Captured로 넘어오면 곧바로 대기 자세로
        // 스냅하지 않고 유형별 전환 모션(저항=그로기, 도주=구르기)을 거친다. 상태는 동기화 값이라
        // 직전 상태 추적도 모든 피어에서 같게 흐른다 — 전 화면에서 같은 전환이 보인다.
        if (m_animator != null && state == NpcState.Captured && TryBeginSubdueTransition(previous))
            return; // 전환 모션이 시드됐다 — 유지 시간이 끝나면 Update가 대기 자세로 넘긴다

        // enum 값을 int로 변환해 전달 → Animator의 Any State 전이(State == N)가 해당 모션으로 전환한다.
        // 단, Attack 번호(3)는 단발 스윙 전용이라 base로 쓰지 않고, 저항 base는 걷기/버틴 자세로 갈린다 (#220·#254).
        // 수감(Jailed)은 대응하는 Animator 상태가 없으므로 여기서 넘기지 않는다 — 아래에서 연행 모션으로 시드한다 (#228)
        if (m_animator != null && state != NpcState.Jailed)
            m_animator.SetInteger(s_stateHash, AnimatorBaseState(state));

        // 연행·수감 진입 시 이동 판별을 초기화 — 직전 상태의 잔여 속도 값이 첫 판정을 오염시키지 않게 (#97/#228)
        if (IsHandcuffedMotion(state))
        {
            if (m_animator != null)
                m_animator.SetInteger(s_stateHash, (int)NpcState.Escorted);

            m_escortMoving = true;
            m_lastPosition = transform.position;
            m_smoothedSpeed = k_escortMoveOnSpeed;
        }
        // 오검거 페널티 진입 — AnimatorBaseState가 시드한 모션(수용·호송=걷기, 추격=달리기)에 판별 상태를 맞추고
        // 속도 평활을 초기화한다. 직전 상태의 잔여 속도가 첫 전환 판정을 오염시키지 않게 (#277~#279)
        else if (IsPenaltyLocomotion(state))
        {
            m_penaltyMotion = AnimatorBaseState(state);
            m_lastPosition = transform.position;
            m_smoothedSpeed = state == NpcState.Chasing ? k_penaltyRunOnSpeed : k_escortMoveOnSpeed;
        }
        // 침입 진입은 언제나 걷기로 시작한다(자물쇠까지 이동) — 걷기로 시드하고 이동 판별을 초기화한다.
        // 직전 상태의 잔여 속도가 첫 판정을 오염시켜 도착도 전에 해제 모션이 나오는 것을 막는다 (#261)
        else if (state == NpcState.Intruding)
        {
            m_intrudeMoving = true;
            m_unlockBeginUntil = 0f;
            m_lastPosition = transform.position;
            m_smoothedSpeed = k_escortMoveOnSpeed;
        }
    }
}
