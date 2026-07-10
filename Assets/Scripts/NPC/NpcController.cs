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

    [Header("연행 (#59)")]
    [Tooltip("연행 중 플레이어와 유지하는 추종 거리(m)")]
    [SerializeField] private float m_escortFollowDistance = 1.2f;
    [Tooltip("이 거리(m)보다 뒤처지면 속도를 올려 따라잡는다")]
    [SerializeField] private float m_escortBoostDistance = 4f;
    [SerializeField] private float m_escortBoostMultiplier = 1.5f;
    [Tooltip("이 거리(m)를 넘으면 연행이 풀리고 그 자리에서 체포 상태로 멈춘다")]
    [SerializeField] private float m_escortBreakDistance = 8f;

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
    public float EscortFollowDistance => m_escortFollowDistance;
    public float EscortBoostDistance => m_escortBoostDistance;
    public float EscortBoostMultiplier => m_escortBoostMultiplier;
    public float EscortBreakDistance => m_escortBreakDistance;

    /// <summary>연행 중 따라갈 대상(체포한 플레이어). 연행 중이 아니면 null.</summary>
    public Transform EscortTarget { get; private set; }

    private void Awake()
    {
        m_agent = GetComponent<NavMeshAgent>();

        m_stateMachine = new NpcStateMachine();
        m_stateMachine.AddState(NpcState.Idle, new NpcIdleState(this));
        m_stateMachine.AddState(NpcState.Walk, new NpcWalkState(this));
        m_stateMachine.AddState(NpcState.Captured, new NpcCapturedState(this));
        m_stateMachine.AddState(NpcState.Escorted, new NpcEscortedState(this));
    }

    /// <summary>연행 시작 — 체포 성공 직후 호출. NPC가 target(플레이어)을 따라 이동한다. (#59)</summary>
    public void StartEscort(Transform target)
    {
        EscortTarget = target;
        m_stateMachine.ChangeState(NpcState.Escorted);
    }

    /// <summary>연행 중단 — 그 자리에서 체포(Captured) 상태로 멈춘다. (#59)</summary>
    public void StopEscort()
    {
        EscortTarget = null;
        m_stateMachine.ChangeState(NpcState.Captured);
    }

    private void Start()
    {
        // 개체마다 걷는 속도를 다르게 해 군중이 같은 리듬으로 움직이는 것을 깨준다
        m_agent.speed *= Random.Range(m_speedMultiplierMin, m_speedMultiplierMax);

        // 회피 우선순위도 개체마다 다르게 — 전원이 같은 값이면 정면으로 마주친 둘이
        // 대칭적으로 서로 양보하다가 교착에 빠진다 (값이 낮은 쪽이 우선권을 가진다)
        m_agent.avoidancePriority = Random.Range(30, 71);

        m_stateMachine.ChangeState(NpcState.Idle);
    }

    private void Update()
    {
        m_stateMachine.Tick();
    }
}
