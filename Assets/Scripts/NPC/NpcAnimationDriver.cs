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
    // 패닉 시 다리(LowerBodyRun 레이어) 달리기 클립의 재생속도 배율 — 실제 이동 속도에 맞춰 발 미끄러짐을 줄인다 (#81)
    private static readonly int s_legRunSpeedHash = Animator.StringToHash("LegRunSpeedMul");
    /// <summary>
    /// 스윙 1회에 재생할 단발 클립 번호를 고르는 Animator 파라미터 이름.
    /// Attack 상태의 블렌드 트리가 이 값으로 클립을 고른다 — 컨트롤러를 만드는
    /// NpcAnimatorControllerBuilder(Editor)가 이 상수를 참조하므로 이름이 어긋날 수 없다.
    /// </summary>
    public const string k_swingVariantParam = "SwingVariant";

    private static readonly int s_swingVariantHash = Animator.StringToHash(k_swingVariantParam);

    // 연행 근접 정지(#97) 모션 전환 임계값 — 실제 이동 속도(m/s) 기준.
    // 켜짐/꺼짐 경계를 다르게 둬(히스테리시스) 정지 직전 감속 구간에서 모션이 떨리는 것을 막는다.
    // 꺼짐 임계값은 걷기 최저 속도(~1.6m/s)보다 충분히 낮게 — 추종 중 순간 감속에 오작동하지 않는 선
    private const float k_escortMoveOnSpeed = 0.5f;
    private const float k_escortMoveOffSpeed = 0.25f;
    // 프레임 노이즈 완화용 지수 평활 계수 — 클수록 정지 반응이 빨라진다.
    // 근접 정지가 velocity를 즉시 0으로 끊으므로(#97) 평활이 식는 시간이 곧 모션 전환 지연이다
    private const float k_speedSmoothing = 25f;

    // 다리 달리기 클립(HumanM@Run01_Forward)이 발 미끄러짐 없이 자연스러워 보이는 기준 지상 속도(m/s).
    // 이 속도일 때 배율 1배로 재생되고, 실제 이동 속도가 다르면 그 비율로 재생속도를 늘리거나 줄인다.
    // 클립이 in-place(루트모션 없음)라 자동 계산이 불가능한 값 — 눈으로 보며 미세 튜닝할 것.
    [Header("패닉 다리 달리기 (#81)")]
    [Tooltip("다리 달리기 클립이 미끄럼 없이 보이는 기준 지상 속도(m/s). 발이 앞으로 밀리면 값을 낮추고, 뒤로 끌리면 높인다")]
    [SerializeField] private float m_panicRunReferenceSpeed = 4.5f;

    [Header("공격 스윙 (#220)")]
    [Tooltip("스윙 1회당 Attack(단발) 모션을 유지하는 시간(초) — 이후 버틴 자세로 복귀한다. 저항 공격 주기보다 짧고 타격 오프셋보다 길게")]
    [SerializeField] private float m_swingAnimSeconds = 0.9f;
    [Tooltip("스윙 클립 종류 수 — NpcAnimatorControllerBuilder가 Attack 블렌드 트리에 넣은 클립 개수와 같아야 한다. 클립을 빼거나 더하면 이 값도 함께 고칠 것")]
    [SerializeField] private int m_swingVariantCount = 7;

    [SerializeField] private Animator m_animator;

    private NpcController m_controller;
    private Vector3 m_lastPosition;
    private float m_smoothedSpeed;
    private bool m_escortMoving;
    // 현재 스윙 모션을 유지할 종료 시각. 0 이하면 스윙 중 아님. 스윙이 끝나면 base 상태로 되돌린다 (#220)
    private float m_swingUntil;
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
        HandleStateChanged(m_controller.CurrentState);
    }

    private void OnDestroy()
    {
        if (m_controller != null)
        {
            m_controller.OnStateChanged -= HandleStateChanged;
            m_controller.OnAttackSwing -= HandleAttackSwing;
        }
    }

    // 저항 NPC의 공격 스윙 1회 — Animator State를 Attack(단발 스윙 클립)으로 잠깐 펄스한다.
    // 이 컨트롤러의 로코모션이 전부 Any State(State==N) 전이라, State int 하나만 참이어야 스윙이
    // 중간에 끊기지 않는다 — 그래서 트리거 오버레이가 아니라 int 펄스를 쓴다. 스윙 종료는 Update가 처리.
    // 데미지 타이밍은 NpcResistState가 타격 오프셋으로 맞추므로 여기선 모션만 얹는다. (#220)
    private void HandleAttackSwing()
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

        // 이번 스윙에 쓸 단발 클립을 뽑는다 — 반드시 State 펄스보다 먼저다.
        // Attack 상태에 들어간 뒤에 바꾸면 재생 중인 클립이 도중에 갈아끼워져 모션이 튄다.
        // 각 피어가 따로 뽑으므로 화면마다 다른 클립이 나올 수 있다. 연출 전용 값이고
        // 데미지는 서버가 타격 오프셋으로 판정하므로(#220) 동기화하지 않는다.
        m_animator.SetFloat(s_swingVariantHash, Random.Range(0, m_swingVariantCount));

        m_animator.SetInteger(s_stateHash, (int)NpcState.Attack);
        m_swingUntil = Time.time + m_swingAnimSeconds;
    }

    /// <summary>스윙 모션이 성립할 수 있는 기준 상태인가 — 구속·무력화 상태에서는 공격이 나올 수 없다. (#220)</summary>
    private static bool CanSwingIn(NpcState state) =>
        state is not (NpcState.Captured or NpcState.Escorted or NpcState.Stunned);

    // FSM 기준 상태에 대응하는 Animator base 번호. 저항(Attack) 중의 base는 버틴 자세(Idle)이고,
    // Attack 번호(3)는 이제 단발 스윙 전용이라 base로 쓰지 않는다. (#220)
    // 침입(Intruding)은 대응 Animator 상태가 없어 평범한 걷기(Walk)를 빌려 쓴다 —
    // 수갑을 차지 않은 채 본부로 걸어 들어오는 그림이라 Escorted가 아니라 Walk다. (#231)
    private int AnimatorBaseState(NpcState state)
    {
        return state switch
        {
            NpcState.Attack => (int)NpcState.Idle,
            NpcState.Intruding => (int)NpcState.Walk,
            _ => (int)state,
        };
    }

    private void Update()
    {
        // 연행(Escorted)·패닉(Panic) 모두 실제 이동 속도가 필요하다. transform 이동량 기준이라
        // 클라이언트에서도 NetworkTransform이 움직여 주는 값을 그대로 쓸 수 있다 — 별도 동기화 불필요.
        if (m_animator == null || Time.deltaTime <= 0f)
            return;

        // 스윙 모션 유지 시간이 끝나면 버틴 자세(base)로 되돌린다 — 저항 상태를 유지한 채 스윙만 단발로 얹는 방식 (#220)
        if (m_swingUntil > 0f && Time.time >= m_swingUntil)
        {
            m_swingUntil = 0f;
            m_animator.SetInteger(s_stateHash, AnimatorBaseState(m_baseState));
        }

        NpcState state = m_controller.CurrentState;
        if (!IsHandcuffedMotion(state) && state != NpcState.Panic)
            return;

        float rawSpeed = (transform.position - m_lastPosition).magnitude / Time.deltaTime;
        m_lastPosition = transform.position;
        m_smoothedSpeed = Mathf.Lerp(m_smoothedSpeed, rawSpeed, Time.deltaTime * k_speedSmoothing);

        // 패닉: 하체 달리기 클립 재생속도를 실제 이동 속도에 비례시켜 발 미끄러짐을 줄인다 (#81)
        if (state == NpcState.Panic)
        {
            float mul = Mathf.Clamp(m_smoothedSpeed / m_panicRunReferenceSpeed, 0.2f, 2f);
            m_animator.SetFloat(s_legRunSpeedHash, mul);
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

    /// <summary>
    /// 수갑 찬 채 이동하는 상태인가 — 걷기(Escorted 모션) ↔ 정지(Captured 모션)를 속도로 구분해야 하는 상태들.
    /// 수감(Jailed)은 대응하는 Animator 상태가 없어 연행 모션을 빌려 쓴다 (#228).
    /// </summary>
    private static bool IsHandcuffedMotion(NpcState state) =>
        state is NpcState.Escorted or NpcState.Jailed;

    private void HandleStateChanged(NpcState state)
    {
        // 상태 전이는 스윙보다 우선한다 — 진행 중이던 스윙을 취소하고 새 base 모션을 즉시 적용한다
        // (예: 저항 중 스윙하다 제압되면 그 프레임에 Captured로 넘어가야 한다) (#220)
        m_baseState = state;
        m_swingUntil = 0f;

        // enum 값을 int로 변환해 전달 → Animator의 Any State 전이(State == N)가 해당 모션으로 전환한다.
        // 단, 저항(Attack)의 base는 버틴 자세(Idle) — Attack 번호는 단발 스윙 전용이다 (#220)
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
        // 패닉 진입 시에도 이동 판별을 초기화. 첫 프레임 배율이 0으로 튀지 않도록 기준 속도로 시드한다 (#81)
        else if (state == NpcState.Panic)
        {
            m_lastPosition = transform.position;
            m_smoothedSpeed = m_panicRunReferenceSpeed;
            if (m_animator != null)
                m_animator.SetFloat(s_legRunSpeedHash, 1f);
        }
    }
}
