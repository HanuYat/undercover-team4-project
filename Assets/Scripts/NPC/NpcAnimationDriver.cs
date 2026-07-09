using UnityEngine;

/// <summary>
/// FSM 상태 변경을 Animator 파라미터로 전달한다 (NpcState.Walk → IsWalking).
/// </summary>
[RequireComponent(typeof(NpcController))]
public class NpcAnimationDriver : MonoBehaviour
{
    private static readonly int s_isWalkingHash = Animator.StringToHash("IsWalking");

    [SerializeField] private Animator m_animator;

    private NpcController m_controller;

    private void Awake()
    {
        m_controller = GetComponent<NpcController>();

        if (m_animator == null)
            m_animator = GetComponentInChildren<Animator>();
    }

    private void Start()
    {
        m_controller.StateMachine.OnStateChanged += HandleStateChanged;
        HandleStateChanged(m_controller.StateMachine.CurrentState);
    }

    private void OnDestroy()
    {
        if (m_controller != null && m_controller.StateMachine != null)
            m_controller.StateMachine.OnStateChanged -= HandleStateChanged;
    }

    private void HandleStateChanged(NpcState state)
    {
        if (m_animator != null)
            m_animator.SetBool(s_isWalkingHash, state == NpcState.Walk);
    }
}
