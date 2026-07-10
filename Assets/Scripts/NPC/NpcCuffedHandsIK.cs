using UnityEngine;

/// <summary>
/// 연행 중 양손을 몸 앞 한 지점에 모아 "수갑 찬" 손 모양을 만드는 IK. (이슈 #59)
/// 상체 포즈 레이어(UpperBodyCuffed)가 팔을 앞으로 가져오고, 이 IK가 손목을 정확히 모은다.
/// OnAnimatorIK 콜백을 받아야 하므로 Animator가 있는 모델 오브젝트에 붙이고,
/// Animator 레이어의 IK Pass가 켜져 있어야 동작한다.
/// </summary>
[RequireComponent(typeof(Animator))]
public class NpcCuffedHandsIK : MonoBehaviour
{
    [Header("손 모으는 위치 (몸 기준 오프셋)")]
    [Tooltip("몸 앞쪽으로 얼마나(m)")]
    [SerializeField] private float m_forwardOffset = 0.28f;
    [Tooltip("바닥에서 높이(m) — 허리춤")]
    [SerializeField] private float m_heightOffset = 0.95f;
    [Tooltip("양 손목 사이 간격(m) — 수갑 폭")]
    [SerializeField] private float m_handGap = 0.06f;

    [Header("전환 속도")]
    [Tooltip("연행 시작/해제 시 손이 자연스럽게 모이고 풀리는 블렌드 속도")]
    [SerializeField] private float m_blendSpeed = 6f;

    private Animator m_animator;
    private NpcController m_controller;
    private float m_weight;

    private void Awake()
    {
        m_animator = GetComponent<Animator>();
        m_controller = GetComponentInParent<NpcController>();
    }

    // Animator의 IK 패스에서 매 프레임 호출된다 (레이어의 IK Pass 필요)
    private void OnAnimatorIK(int layerIndex)
    {
        bool cuffed = m_controller != null &&
                      m_controller.StateMachine != null &&
                      m_controller.StateMachine.CurrentState == NpcState.Escorted;

        // 켜고 끌 때 손이 툭 튀지 않도록 가중치를 부드럽게 블렌드
        m_weight = Mathf.MoveTowards(m_weight, cuffed ? 1f : 0f, m_blendSpeed * Time.deltaTime);
        if (m_weight <= 0f)
            return;

        Transform root = m_controller != null ? m_controller.transform : transform;
        Vector3 center = root.position + root.forward * m_forwardOffset + Vector3.up * m_heightOffset;
        Vector3 leftPos = center - root.right * (m_handGap * 0.5f);
        Vector3 rightPos = center + root.right * (m_handGap * 0.5f);

        m_animator.SetIKPositionWeight(AvatarIKGoal.LeftHand, m_weight);
        m_animator.SetIKPositionWeight(AvatarIKGoal.RightHand, m_weight);
        m_animator.SetIKPosition(AvatarIKGoal.LeftHand, leftPos);
        m_animator.SetIKPosition(AvatarIKGoal.RightHand, rightPos);
    }
}
