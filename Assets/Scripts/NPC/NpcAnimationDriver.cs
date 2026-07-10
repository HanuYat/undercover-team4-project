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

    private void HandleStateChanged(NpcState state)
    {
        // enum 값을 int로 변환해 전달 → Animator의 Any State 전이(State == N)가 해당 모션으로 전환한다
        if (m_animator != null)
            m_animator.SetInteger(s_stateHash, (int)state);
    }
}
