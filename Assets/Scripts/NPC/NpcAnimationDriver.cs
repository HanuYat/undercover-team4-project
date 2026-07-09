using UnityEngine;

/// <summary>
/// FSM 상태 변경을 Animator의 State(int) 파라미터로 전달한다.
/// NpcState enum 값이 그대로 Animator 상태 번호가 되므로(Idle=0, Walk=1...),
/// 새 상태가 생겨도 이 스크립트는 수정할 필요가 없다.
/// </summary>
[RequireComponent(typeof(NpcController))]
public class NpcAnimationDriver : MonoBehaviour
{
    private static readonly int s_stateHash = Animator.StringToHash("State");

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
        // enum 값을 int로 변환해 전달 → Animator의 Any State 전이(State == N)가 해당 모션으로 전환한다
        if (m_animator != null)
            m_animator.SetInteger(s_stateHash, (int)state);
    }
}
