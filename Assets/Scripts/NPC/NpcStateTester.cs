using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// 테스트 전용 — Play 모드에서 인스펙터로 NPC 상태를 강제 변경해 애니메이션을 확인한다.
/// 사용법: Play 시작 → NPC 선택 → "수동 모드" 체크 → "미리볼 상태" 드롭다운에서 선택.
/// 수동 모드를 끄면 FSM이 다시 이어서 동작한다.
/// </summary>
[RequireComponent(typeof(NpcController))]
public class NpcStateTester : MonoBehaviour
{
    private static readonly int s_stateHash = Animator.StringToHash("State");

    [Header("수동 테스트 (Play 모드 전용)")]
    [Tooltip("켜면 FSM이 멈추고 아래에서 고른 상태의 모션이 강제 재생된다")]
    [SerializeField] private bool m_manualMode;
    [SerializeField] private NpcState m_previewState = NpcState.Idle;

    private NpcController m_controller;
    private NpcAnimationDriver m_driver;
    private NavMeshAgent m_agent;
    private Animator m_animator;
    private bool m_lastManualMode;

    private void Awake()
    {
        m_controller = GetComponent<NpcController>();
        m_driver = GetComponent<NpcAnimationDriver>();
        m_agent = GetComponent<NavMeshAgent>();
        m_animator = GetComponentInChildren<Animator>();
    }

    // 인스펙터 값이 바뀔 때마다 에디터가 호출해준다 → 별도 버튼 없이 즉시 적용됨
    private void OnValidate()
    {
        if (Application.isPlaying && m_animator != null)
            Apply();
    }

    private void Apply()
    {
        // 네트워크 세션에서 FSM은 서버 전용 — 클라이언트 인스턴스에서는 강제 전환하지 않는다 (#56)
        if (m_controller.IsSpawned && !m_controller.IsServer)
            return;

        // 수동 모드를 켜고 끌 때 FSM(NpcController)과 드라이버를 함께 정지/재개한다
        if (m_manualMode != m_lastManualMode)
        {
            m_controller.enabled = !m_manualMode;
            if (m_driver != null) m_driver.enabled = !m_manualMode;

            if (m_manualMode)
            {
                // 이동 중이었다면 멈추고 제자리에서 모션만 확인
                if (m_agent.isOnNavMesh) m_agent.ResetPath();
            }
            else
            {
                // 수동 모드 해제: Animator를 FSM의 실제 현재 상태와 다시 맞춘다
                m_animator.SetInteger(s_stateHash, (int)m_controller.StateMachine.CurrentState);
            }

            m_lastManualMode = m_manualMode;
        }

        if (m_manualMode)
            m_animator.SetInteger(s_stateHash, (int)m_previewState);
    }
}
