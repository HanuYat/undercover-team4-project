using UnityEngine;

/// <summary>
/// 플레이어의 이동 속도를 Animator의 MoveX/MoveZ(float) 파라미터로 전달한다.
/// (MoveX = 좌우 strafe, MoveZ = 전후 — 플레이어 로컬 기준)
/// 측정한 속도(m/s)는 PlayerMovement의 걷기/달리기 속도로 정규화해서 넣는다:
/// 걷기 속도 = k_walkParam(1), 달리기 속도 = k_runParam(2).
/// 덕분에 이동 속도를 튜닝해도 블렌드 트리(.controller)를 다시 만들 필요가 없다.
/// 위치 변화량으로 속도를 계산하므로, NetworkTransform으로 위치가 동기화되는
/// 원격 플레이어에서도 올바른 이동 애니메이션이 재생된다. (소유자 가드 불필요)
/// </summary>
public class PlayerAnimationDriver : MonoBehaviour
{
    // 블렌드 트리 좌표 (PlayerAnimatorControllerBuilder가 클립 배치에 같은 상수를 사용)
    public const float k_walkParam = 1f;
    public const float k_runParam = 2f;

    private static readonly int s_moveXHash = Animator.StringToHash("MoveX");
    private static readonly int s_moveZHash = Animator.StringToHash("MoveZ");
    private static readonly int s_downHash = Animator.StringToHash("Down"); // 다운(무력화) 상태 머신 구동 (#105)
    private static readonly int s_crouchHash = Animator.StringToHash("Crouch"); // 서기↔앉기 상태 전환 (#236)
    private static readonly int s_airborneHash = Animator.StringToHash("Airborne"); // 점프 상태 머신 구동 (#189)

    [SerializeField]
    private Animator m_animator;

    [SerializeField]
    private PlayerMovement m_movement; // 정규화 기준 속도를 읽어옴

    [SerializeField]
    private float m_damping = 0.1f; // 전환 부드럽게

    private PlayerIncapacitation m_incapacitation; // 다운 애니메이션 구동용 (#105)
    private PlayerCrouch m_crouch; // 앉기 애니메이션 구동용 (#236)
    private PlayerJump m_jump; // 점프 애니메이션 구동용 (#189)
    private Vector3 m_lastPosition;

    private void Awake()
    {
        if (m_animator == null)
        {
            m_animator = GetComponentInChildren<Animator>();
        }

        if (m_movement == null)
        {
            m_movement = GetComponentInParent<PlayerMovement>();
        }

        m_incapacitation = GetComponentInParent<PlayerIncapacitation>();
        m_crouch = GetComponentInParent<PlayerCrouch>();
        m_jump = GetComponentInParent<PlayerJump>();
        m_lastPosition = transform.position;
    }

    private void Update()
    {
        if (m_animator == null)
            return;

        // 다운(무력화) 상태를 애니메이터에 반영 — 모든 인스턴스가 IsIncapacitated(동기화값)를 폴링해
        // Down 상태 머신(Knockdown Fall→Ground→StandUp)을 구동하므로 원격 뷰도 동일하게 재생된다.
        if (m_incapacitation != null)
        {
            m_animator.SetBool(s_downHash, m_incapacitation.IsIncapacitated);
        }

        // 앉기도 같은 방식 — 서버 권위 동기화값을 폴링해 Crouch 상태(Crouch Idle/Walk 블렌드 트리)를 구동한다. (#236)
        // 실제 앉기(IsCrouching)가 아니라 '눌렀는지'(IsCrouchRequested)를 넘기는 이유: 공중에서는
        // 실제 앉기가 착지까지 보류되지만 자세는 웅크려야 한다. 지상에서는 두 값이 같다. (#189)
        if (m_crouch != null)
        {
            m_animator.SetBool(s_crouchHash, m_crouch.IsCrouchRequested);
        }

        // 점프도 같은 방식 — 공중 여부만 bool로 넘기고 이륙/체공/착지 3단계는 애니메이터가 나눈다. (#189)
        // 원격 피어의 CharacterController는 isGrounded가 안 도므로 서버 권위 동기화값을 폴링한다.
        // (트리거 대신 bool을 쓰는 이유 — 트리거는 원격에서 유실·중복되기 쉽다)
        if (m_jump != null)
        {
            m_animator.SetBool(s_airborneHash, m_jump.IsAirborne);
        }

        if (m_movement == null || Time.deltaTime <= 0f)
            return;

        Vector3 worldDelta = transform.position - m_lastPosition;
        worldDelta.y = 0f; // 수평 이동만
        m_lastPosition = transform.position;

        // 월드 이동량 → 플레이어 로컬 방향 (x = 좌우, z = 전후)
        Vector3 localVelocity = transform.InverseTransformDirection(worldDelta / Time.deltaTime);
        Vector2 param = NormalizeToBlendSpace(new Vector2(localVelocity.x, localVelocity.z));

        m_animator.SetFloat(s_moveXHash, param.x, m_damping, Time.deltaTime);
        m_animator.SetFloat(s_moveZHash, param.y, m_damping, Time.deltaTime);
    }

    /// <summary>
    /// 실제 속도(m/s)를 블렌드 트리 좌표로 구간별 매핑한다.
    /// [0, 걷기속도] → [0, k_walkParam], [걷기속도, 달리기속도] → [k_walkParam, k_runParam]
    /// 앉기 중에는 앉기 속도가 '걷기속도' 기준이 된다 — Crouch 블렌드 트리는 반경 k_walkParam에
    /// CrouchWalk 8방향만 있고 달리기 단계가 없으므로(에셋에 없음), 앉기 속도가 그대로 1.0에 대응해야
    /// 앉은 채 이동할 때 Idle 쪽으로 블렌딩되지 않는다. (#236)
    /// </summary>
    private Vector2 NormalizeToBlendSpace(Vector2 velocity)
    {
        float speed = velocity.magnitude;
        if (speed < 0.01f)
            return Vector2.zero;

        bool crouching = m_crouch != null && m_crouch.IsCrouching;
        float walkSpeed = Mathf.Max(
            crouching ? m_movement.CrouchSpeed : m_movement.MoveSpeed,
            0.01f
        );
        float runSpeed = Mathf.Max(m_movement.SprintSpeed, walkSpeed + 0.01f);

        float t =
            speed <= walkSpeed
                ? speed / walkSpeed * k_walkParam
                : k_walkParam
                    + (speed - walkSpeed) / (runSpeed - walkSpeed) * (k_runParam - k_walkParam);

        return velocity / speed * t; // 방향은 유지, 크기만 좌표계로 환산
    }
}
