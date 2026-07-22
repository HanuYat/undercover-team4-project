using UnityEngine;

/// <summary>
/// 차저(<see cref="ThugAttacker"/>) 전용 애니메이션 구동 — Animator의 State(int) 파라미터를 갱신한다. (#106, #291)
/// FSM/상태 동기화가 없으므로 표현에 필요한 최소한만 로컬에서 유도한다:
///  · <b>이동</b> — transform 이동량으로 속도를 추정해 Run/Idle을 전환하고, 달리기 클립의 재생속도를
///    실제 이동 속도에 비례시켜 발 미끄러짐을 줄인다. 서버는 NavMeshAgent가,
///    클라이언트는 NetworkTransform이 transform을 움직이므로 별도 동기화 없이 전 피어에서 동작한다.
///  · <b>윈드업</b> — 돌진 준비 중(<see cref="ThugAttacker.IsWindingUp"/>)엔 숨고르는 전용 모션을 낸다.
/// Animator 상태 번호는 <see cref="NpcState"/> 값 규약을 그대로 따른다(NPC.controller 공용). (NpcAnimationDriver와 동일)
/// </summary>
[RequireComponent(typeof(ThugAttacker))]
public class ThugAnimationDriver : MonoBehaviour
{
    private static readonly int s_stateHash = Animator.StringToHash("State");

    // Base Layer 달리기 클립의 재생속도 배율 — 실제 이동 속도에 맞춰 발 미끄러짐을 줄인다.
    private static readonly int s_runSpeedHash = Animator.StringToHash("RunSpeedMul");

    // 프레임 노이즈 완화용 지수 평활 계수 — 클수록 정지/이동 반응이 빨라진다.
    private const float k_speedSmoothing = 12f;

    // 재생속도 배율 허용 범위 — 과하게 늘리거나 줄이면 슬로모션/과속처럼 보인다.
    private const float k_runSpeedMulMin = 0.2f;
    private const float k_runSpeedMulMax = 2f;

    /// <summary>윈드업(숨고르는 준비) 모션의 Animator 상태 번호 — NpcState enum 밖 전용 번호(해제/기상 모션과 같은 규약, #291).
    /// NPC.controller에 이 번호로 CombatIdle01 클립 상태를 만들고 Any State 전이(State==103)를 건다.</summary>
    public const int k_windupAnimState = 103;

    [Header("이동 판별")]
    [Tooltip("이 추정 속도(m/s) 이상이면 달리기(Run), 미만이면 정지(Idle)로 본다")]
    [SerializeField]
    private float m_moveSpeedThreshold = 0.3f;

    // 클립이 in-place(루트모션 없음)라 자동 계산이 불가능한 값 — 눈으로 보며 미세 튜닝할 것.
    [Tooltip("달리기 클립이 미끄럼 없이 보이는 기준 지상 속도(m/s). 발이 앞으로 밀리면 값을 낮추고, 뒤로 끌리면 높인다")]
    [SerializeField]
    private float m_runReferenceSpeed = 4.5f;

    [SerializeField]
    private Animator m_animator;

    private ThugAttacker m_thug;
    private Vector3 m_lastPosition;
    private float m_smoothedSpeed;
    private int m_appliedState = -1; // 마지막으로 Animator에 쓴 값 — 매 프레임 중복 SetInteger 방지

    private void Awake()
    {
        m_thug = GetComponent<ThugAttacker>();
        if (m_animator == null)
            m_animator = GetComponentInChildren<Animator>();
    }

    private void Start()
    {
        m_lastPosition = transform.position;

        // 차저는 스폰 직후 바로 추격에 들어간다 — 평활 속도를 기준 속도로 시드해 두지 않으면
        // 0에서 올라오는 동안 첫 걸음이 슬로모션으로 보인다.
        m_smoothedSpeed = m_runReferenceSpeed;
        if (m_animator != null)
            m_animator.SetFloat(s_runSpeedHash, 1f);
    }

    private void Update()
    {
        if (m_animator == null || Time.deltaTime <= 0f)
            return;

        // transform 이동량 기반 속도 추정 — 서버(NavMeshAgent)·클라(NetworkTransform) 모두에서 유효
        float rawSpeed = (transform.position - m_lastPosition).magnitude / Time.deltaTime;
        m_lastPosition = transform.position;
        m_smoothedSpeed = Mathf.Lerp(m_smoothedSpeed, rawSpeed, Time.deltaTime * k_speedSmoothing);

        // 윈드업(숨고르기)이면 전용 상태, 그 외엔 이동 속도로 Run/Idle (#291)
        int desired;
        if (m_thug.IsWindingUp)
            desired = k_windupAnimState;
        else
            desired =
                m_smoothedSpeed >= m_moveSpeedThreshold ? (int)NpcState.Run : (int)NpcState.Idle;

        if (desired != m_appliedState)
        {
            m_appliedState = desired;
            m_animator.SetInteger(s_stateHash, desired);
        }

        // 달리는 동안에만 배율을 갱신한다 — 멈춰 있을 때의 0에 가까운 속도로 배율을 눌러두면
        // 다시 달리기 시작하는 첫 프레임이 슬로모션으로 보인다.
        if (desired == (int)NpcState.Run)
        {
            float mul = Mathf.Clamp(
                m_smoothedSpeed / m_runReferenceSpeed,
                k_runSpeedMulMin,
                k_runSpeedMulMax
            );
            m_animator.SetFloat(s_runSpeedHash, mul);
        }
    }
}
