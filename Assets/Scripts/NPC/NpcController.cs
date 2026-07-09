using UnityEngine;
using UnityEngine.AI;

[RequireComponent(typeof(NavMeshAgent))]
public class NpcController : MonoBehaviour
{
    [Header("배회 반경")]
    [SerializeField] private float m_wanderRadius = 10f;

    [Header("Idle 유지 시간 (초)")]
    [SerializeField] private float m_idleTimeMin = 1f;
    [SerializeField] private float m_idleTimeMax = 3f;

    private NavMeshAgent m_agent;
    private NpcStateMachine m_stateMachine;

    public NavMeshAgent Agent => m_agent;
    public NpcStateMachine StateMachine => m_stateMachine;
    public float WanderRadius => m_wanderRadius;
    public float IdleTimeMin => m_idleTimeMin;
    public float IdleTimeMax => m_idleTimeMax;

    private void Awake()
    {
        m_agent = GetComponent<NavMeshAgent>();

        m_stateMachine = new NpcStateMachine();
        m_stateMachine.AddState(NpcState.Idle, new NpcIdleState(this));
        m_stateMachine.AddState(NpcState.Walk, new NpcWalkState(this));
    }

    private void Start()
    {
        m_stateMachine.ChangeState(NpcState.Idle);
    }

    private void Update()
    {
        m_stateMachine.Tick();
    }
}
