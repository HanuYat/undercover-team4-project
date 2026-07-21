using System;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;
using Random = UnityEngine.Random;

/// <summary>
/// NPC 두뇌 — FSM/NavMesh 구동과 상태 동기화를 담당한다.
/// 이동·상태 판단은 서버 전용(서버 권위)이고, 클라이언트는
/// NetworkTransform(위치)과 NetworkVariable(상태)로 동기화된 결과만 표현한다. (이슈 #56)
/// 네트워크를 켜지 않은 로컬 Play 테스트에서는 기존처럼 단독으로 동작한다.
///
/// 도메인별 partial로 나뉘어 있다 — 이 파일은 코어(배회·FSM 구축·상태 동기화·freeze)만 든다:
/// - NpcController.Custody.cs  연행·수감·석방·수갑 (#59/#228/#229/#230)
/// - NpcController.Reaction.cs 검거 반응 — 도주·저항·제압·기절 (#76/#79/#220)
/// - NpcController.Panic.cs    패닉·소란 전파 (#81)
/// - NpcController.Intrude.cs  침입 — 범인 탈출 이벤트 (#231)
/// - NpcController.Penalty.cs  오검거 페널티 — 수용·추격·호송 (#277~#279)
/// </summary>
[RequireComponent(typeof(NavMeshAgent))]
public partial class NpcController : NetworkBehaviour
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

    // 라운드 종료 시 정지(freeze) 플래그 — 서버(또는 오프라인)에서만 의미. 켜지면 FSM/이동을 멈춘다. (라운드 종료 freeze)
    private bool m_frozen;

    // 서버 권위 FSM 상태 — 서버만 쓰고 모든 클라이언트가 읽는다 (#56)
    private readonly NetworkVariable<NpcState> m_networkState = new NetworkVariable<NpcState>(NpcState.Idle);

    public NavMeshAgent Agent => m_agent;
    public NpcStateMachine StateMachine => m_stateMachine;
    public float WanderRadius => m_wanderRadius;
    public float MinWanderDistance => m_minWanderDistance;
    public float IdleTimeMin => m_idleTimeMin;
    public float IdleTimeMax => m_idleTimeMax;
    public float LongIdleChance => m_longIdleChance;
    public float LongIdleTimeMin => m_longIdleTimeMin;
    public float LongIdleTimeMax => m_longIdleTimeMax;

    /// <summary>
    /// 현재 NPC 상태. 네트워크 세션 중에는 동기화된 값이라 클라이언트에서도 안전하게 읽을 수 있다.
    /// (StateMachine.CurrentState는 서버에서만 갱신되므로 외부 코드는 반드시 이 프로퍼티를 읽을 것)
    /// </summary>
    public NpcState CurrentState => IsSpawned ? m_networkState.Value : m_stateMachine.CurrentState;

    /// <summary>상태 변경 이벤트 — 서버·클라이언트 모든 피어에서 발생한다. 애니메이션 등 표현 계층이 구독. (#56)</summary>
    public event Action<NpcState> OnStateChanged;

    private void Awake()
    {
        m_agent = GetComponent<NavMeshAgent>();

        m_stateMachine = new NpcStateMachine();
        m_stateMachine.AddState(NpcState.Idle, new NpcIdleState(this));
        m_stateMachine.AddState(NpcState.Walk, new NpcWalkState(this));
        m_stateMachine.AddState(NpcState.Captured, new NpcCapturedState(this));
        m_stateMachine.AddState(NpcState.Escorted, new NpcEscortedState(this));
        m_stateMachine.AddState(NpcState.Run, new NpcFleeState(this));
        m_stateMachine.AddState(NpcState.Attack, new NpcResistState(this));
        m_stateMachine.AddState(NpcState.Stunned, new NpcStunnedState(this));
        m_stateMachine.AddState(NpcState.Panic, new NpcPanicState(this));
        m_stateMachine.AddState(NpcState.Jailed, new NpcJailedState(this));
        m_stateMachine.AddState(NpcState.Intruding, new NpcIntrudeState(this));
        m_stateMachine.AddState(NpcState.Detained, new NpcDetainedState(this));
        m_stateMachine.AddState(NpcState.Chasing, new NpcChaseState(this));
        m_stateMachine.AddState(NpcState.PenaltyEscorting, new NpcPenaltyEscortState(this));

        // FSM 전이(서버/오프라인에서만 발생)를 동기화 변수 또는 로컬 이벤트로 흘려보낸다
        m_stateMachine.OnStateChanged += HandleFsmStateChanged;
    }

    public override void OnNetworkSpawn()
    {
        m_networkState.OnValueChanged += HandleNetworkStateChanged;

        if (IsServer)
        {
            InitBehavior();
        }
        else
        {
            // 클라이언트의 이동은 NetworkTransform이 담당 — NavMeshAgent가 켜져 있으면
            // 동기화로 옮겨진 위치를 NavMesh 위로 되돌리려 해 서로 싸운다
            m_agent.enabled = false;
        }
    }

    public override void OnNetworkDespawn()
    {
        m_networkState.OnValueChanged -= HandleNetworkStateChanged;
    }

    private void Start()
    {
        // 오프라인 폴백 — 네트워크 세션 없이 Play한 로컬 테스트에서는 기존처럼 단독 구동한다.
        // (네트워크 스폰된 경우 OnNetworkSpawn이 Start보다 먼저 불리므로 여기는 건너뛴다)
        if (!IsSpawned)
            InitBehavior();
    }

    /// <summary>배회 파라미터 초기화 + FSM 시동. 서버(또는 오프라인)에서 1회 호출.</summary>
    private void InitBehavior()
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
        // FSM/NavMesh는 서버 전용 — 클라이언트는 동기화된 위치·상태만 표현한다 (#56)
        if (IsSpawned && !IsServer)
            return;

        // 라운드 종료 freeze — 서버에서 멈추면 NetworkTransform이 정지 위치를 복제해 전 피어에서 멈춘다
        if (m_frozen)
            return;

        m_stateMachine.Tick();
        EmitDisturbancePulse();
    }

    // 서버(또는 오프라인)의 FSM 전이를 밖으로 전파한다
    private void HandleFsmStateChanged(NpcState state)
    {
        if (!IsSpawned)
        {
            OnStateChanged?.Invoke(state); // 오프라인 — 동기화 없이 바로 로컬 이벤트
            return;
        }

        // 클라이언트에서 실수로 FSM을 전이시켜도 서버 권위 변수 쓰기 예외로 터지지 않게 막는다
        if (!IsServer)
        {
            Debug.LogWarning($"NpcController: 클라이언트에서 FSM 전이 시도({state}) — 서버 권위라 무시됨", this);
            return;
        }

        m_networkState.Value = state; // OnValueChanged를 거쳐 모든 피어에서 OnStateChanged가 발생한다
    }

    private void HandleNetworkStateChanged(NpcState previous, NpcState current)
    {
        OnStateChanged?.Invoke(current);
    }

    /// <summary>
    /// 라운드 종료 정지(freeze) — 서버(또는 오프라인)에서 호출. FSM 틱과 NavMesh 이동을 멈춘다. (라운드 종료 freeze)
    /// 서버에서 멈추면 NetworkTransform이 정지 위치를 복제하므로 모든 클라이언트에서도 멈춘 것으로 보인다.
    /// </summary>
    public void SetFrozen(bool frozen)
    {
        // FSM/이동은 서버 권위 — 클라이언트 호출은 다른 제어 메서드와 동일하게 무시한다
        if (IsSpawned && !IsServer)
            return;

        m_frozen = frozen;

        // 에이전트를 멈춘다 — 비활성/NavMesh 밖이면 isStopped 접근이 예외를 던지므로 가드
        if (m_agent != null && m_agent.enabled && m_agent.isOnNavMesh)
            m_agent.isStopped = frozen;
    }
}
