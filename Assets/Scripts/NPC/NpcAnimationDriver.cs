using UnityEngine;

/// <summary>
/// FSM 상태 변경을 Animator의 State(int) 파라미터로 전달한다.
/// NpcState enum 값이 그대로 Animator 상태 번호가 되므로(Idle=0, Walk=1...),
/// 새 상태가 생겨도 이 스크립트는 수정할 필요가 없다.
/// NpcController.OnStateChanged는 네트워크 동기화를 거쳐 모든 피어에서 발생하므로
/// 서버·클라이언트 어디서든 같은 모션이 재생된다. (#56)
/// </summary>
[RequireComponent(typeof(NpcController))]
public class NpcAnimationDriver : MonoBehaviour
{
    private static readonly int s_stateHash = Animator.StringToHash("State");
    // 패닉 시 다리(LowerBodyRun 레이어) 달리기 클립의 재생속도 배율 — 실제 이동 속도에 맞춰 발 미끄러짐을 줄인다 (#81)
    private static readonly int s_legRunSpeedHash = Animator.StringToHash("LegRunSpeedMul");

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

    [SerializeField] private Animator m_animator;

    private NpcController m_controller;
    private Vector3 m_lastPosition;
    private float m_smoothedSpeed;
    private bool m_escortMoving;

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
        HandleStateChanged(m_controller.CurrentState);
    }

    private void OnDestroy()
    {
        if (m_controller != null)
            m_controller.OnStateChanged -= HandleStateChanged;
    }

    private void Update()
    {
        // 연행(Escorted)·패닉(Panic) 모두 실제 이동 속도가 필요하다. transform 이동량 기준이라
        // 클라이언트에서도 NetworkTransform이 움직여 주는 값을 그대로 쓸 수 있다 — 별도 동기화 불필요.
        if (m_animator == null || Time.deltaTime <= 0f)
            return;

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
        // enum 값을 int로 변환해 전달 → Animator의 Any State 전이(State == N)가 해당 모션으로 전환한다.
        // 수감(Jailed)은 Animator 상태가 없으므로 int를 그대로 넘기지 않는다 — 아래에서 연행 모션으로 시드한다.
        if (m_animator != null && state != NpcState.Jailed)
            m_animator.SetInteger(s_stateHash, (int)state);

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
