using UnityEngine;
using UnityEngine.AI;

[RequireComponent(typeof(NavMeshAgent))]
public class NpcController : MonoBehaviour
{
    [Header("배회 반경")]
    [SerializeField] private float m_wanderRadius = 10f;

    [Header("배회 지점 최소 거리")]
    [Tooltip("다음 배회 지점이 이 거리보다 가까우면 다시 뽑는다 — 한두 걸음 걷고 마는 어색한 이동 방지")]
    [SerializeField] private float m_minWanderDistance = 3f;

    [Header("Idle 유지 시간 (초)")]
    [SerializeField] private float m_idleTimeMin = 1f;
    [SerializeField] private float m_idleTimeMax = 3f;

    [Header("긴 대기 (가끔 구경하듯 오래 멈춰 서 있기)")]
    [Tooltip("Idle 진입 시 이 확률로 아래의 긴 대기 시간을 대신 사용한다")]
    [SerializeField, Range(0f, 1f)] private float m_longIdleChance = 0.15f;
    [SerializeField] private float m_longIdleTimeMin = 5f;
    [SerializeField] private float m_longIdleTimeMax = 10f;

    [Header("개체별 이동 속도 편차 (배율)")]
    [Tooltip("스폰 시 NavMeshAgent 속도에 이 범위의 랜덤 배율을 곱한다 — 군중이 전부 같은 속도로 걷는 것 방지")]
    [SerializeField] private float m_speedMultiplierMin = 0.8f;
    [SerializeField] private float m_speedMultiplierMax = 1.2f;

    private NavMeshAgent m_agent;
    private NpcStateMachine m_stateMachine;

    public NavMeshAgent Agent => m_agent;
    public NpcStateMachine StateMachine => m_stateMachine;
    public float WanderRadius => m_wanderRadius;
    public float MinWanderDistance => m_minWanderDistance;
    public float IdleTimeMin => m_idleTimeMin;
    public float IdleTimeMax => m_idleTimeMax;
    public float LongIdleChance => m_longIdleChance;
    public float LongIdleTimeMin => m_longIdleTimeMin;
    public float LongIdleTimeMax => m_longIdleTimeMax;

    private void Awake()
    {
        m_agent = GetComponent<NavMeshAgent>();

        m_stateMachine = new NpcStateMachine();
        m_stateMachine.AddState(NpcState.Idle, new NpcIdleState(this));
        m_stateMachine.AddState(NpcState.Walk, new NpcWalkState(this));
        m_stateMachine.AddState(NpcState.Captured, new NpcCapturedState(this));
    }

    private void Start()
    {
        // 개체마다 걷는 속도를 다르게 해 군중이 같은 리듬으로 움직이는 것을 깨준다
        m_agent.speed *= Random.Range(m_speedMultiplierMin, m_speedMultiplierMax);

        m_stateMachine.ChangeState(NpcState.Idle);
    }

    private void Update()
    {
        m_stateMachine.Tick();
    }
}
