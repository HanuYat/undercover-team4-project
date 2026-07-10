using UnityEngine;

/// <summary>
/// 플레이어의 이동 속도를 Animator의 MoveX/MoveZ(float) 파라미터로 전달한다.
/// (MoveX = 좌우 strafe, MoveZ = 전후 — 플레이어 로컬 기준)
/// 위치 변화량으로 속도를 계산하므로, NetworkTransform으로 위치가 동기화되는
/// 원격 플레이어에서도 올바른 이동 애니메이션이 재생된다. (소유자 가드 불필요)
/// Animator 쪽에서 2D 블렌드 트리(Idle 중앙 / Walk 8방향 / Run 8방향)로 연결한다.
/// </summary>
public class PlayerAnimationDriver : MonoBehaviour
{
    private static readonly int s_moveXHash = Animator.StringToHash("MoveX");
    private static readonly int s_moveZHash = Animator.StringToHash("MoveZ");

    [SerializeField] private Animator m_animator;
    [SerializeField] private float m_damping = 0.1f; // 전환 부드럽게

    private Vector3 m_lastPosition;

    private void Awake()
    {
        if (m_animator == null)
        {
            m_animator = GetComponentInChildren<Animator>();
        }

        m_lastPosition = transform.position;
    }

    private void Update()
    {
        if (m_animator == null || Time.deltaTime <= 0f) return;

        Vector3 worldDelta = transform.position - m_lastPosition;
        worldDelta.y = 0f; // 수평 이동만
        m_lastPosition = transform.position;

        // 월드 이동량 → 플레이어 로컬 방향 (x = 좌우, z = 전후)
        Vector3 localVelocity = transform.InverseTransformDirection(worldDelta / Time.deltaTime);

        m_animator.SetFloat(s_moveXHash, localVelocity.x, m_damping, Time.deltaTime);
        m_animator.SetFloat(s_moveZHash, localVelocity.z, m_damping, Time.deltaTime);
    }
}
