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
/// </summary>
[RequireComponent(typeof(NavMeshAgent))]
public class NpcController : NetworkBehaviour
{
    [Header("상태별 튜닝 데이터 (ScriptableObject) — #259")]
    [Tooltip("각 FSM 상태가 자기 config를 주입받아 읽는다. 값 조정은 이 에셋들에서 한다.")]
    [SerializeField] private NpcIdleConfig m_idleConfig;
    [SerializeField] private NpcWalkConfig m_walkConfig;
    [SerializeField] private NpcEscortConfig m_escortConfig;
    [SerializeField] private NpcFleeConfig m_fleeConfig;
    [SerializeField] private NpcResistConfig m_resistConfig;
    [SerializeField] private NpcStunConfig m_stunConfig;
    [SerializeField] private NpcCapturedConfig m_capturedConfig;
    [SerializeField] private NpcChaseConfig m_chaseConfig;
    [SerializeField] private NpcPanicConfig m_panicConfig;
    [SerializeField] private NpcCommonConfig m_commonConfig;

    private NavMeshAgent m_agent;
    private NpcStateMachine m_stateMachine;

    // 넉백 비행 상태 — 서버(또는 오프라인)에서만 의미. 비행 중에는 FSM/NavMeshAgent가 정지한다. (#232)
    private Vector3 m_knockbackVelocity;
    private Vector3 m_knockbackLaunch;
    private float m_knockbackElapsed;
    private bool m_knockbackActive;
    private NpcState m_knockbackLandingState; // 착지 후 돌아갈 상태 — 검거 중이었으면 Captured, 그 외엔 Stunned

    // 라운드 종료 시 정지(freeze) 플래그 — 서버(또는 오프라인)에서만 의미. 켜지면 FSM/이동을 멈춘다. (라운드 종료 freeze)
    private bool m_frozen;

    // 서버 권위 FSM 상태 — 서버만 쓰고 모든 클라이언트가 읽는다 (#56)
    private readonly NetworkVariable<NpcState> m_networkState = new NetworkVariable<NpcState>(NpcState.Idle);

    // 서버 권위 제압 게이지 — 저항(Attack) 상태에서만 의미. 진행도 UI(후속)를 위해 동기화한다 (#76)
    private readonly NetworkVariable<float> m_syncedSubdueGauge = new NetworkVariable<float>(0f);
    private float m_subdueGauge; // 서버·오프라인의 진실값 — m_networkState와 같은 이중 구조

    public NavMeshAgent Agent => m_agent;
    public NpcStateMachine StateMachine => m_stateMachine;
    /// <summary>저항 제압 게이지 최대치 — HUD가 게이지 비율 계산에 읽는다. (#76/#79)</summary>
    public float SubdueGaugeMax => m_resistConfig.SubdueGaugeMax;

    /// <summary>기절 지속 시간(초) — 테이저가 명중 안내에 읽는다. (#269)</summary>
    public float StunSeconds => m_stunConfig.StunSeconds;

    /// <summary>
    /// 위협(플레이어)을 찾는 반경(m) — 저항 패배 후 도주 대상 탐색(#205)과 도주 방향 산출(#213)이 같은 값을 쓴다.
    /// 두 경로가 다른 반경을 쓰면 "도망칠 상대"와 "피할 상대"의 기준이 어긋난다.
    /// </summary>
    public float ThreatSearchRadius => m_resistConfig.AttackRange * m_resistConfig.ThreatSearchRadiusMultiplier;

    /// <summary>패닉의 원인이 된 소란 지점 — 이 반대 방향으로 달아난다. 서버에서만 유효. (#81)</summary>
    public Vector3 PanicSource { get; private set; }

    /// <summary>마지막으로 소란을 감지한 시각(Time.time) — 패닉 진정 타이머 기준. 서버에서만 유효. (#81)</summary>
    public float LastDisturbedTime { get; private set; }

    /// <summary>현재 제압 게이지. 네트워크 세션 중에는 동기화된 값이라 클라이언트에서도 읽을 수 있다. (#76)</summary>
    public float SubdueGauge => IsSpawned ? m_syncedSubdueGauge.Value : m_subdueGauge;

    /// <summary>저항·도주 중 피해 다니는 위협 대상(체포를 시도한 플레이어). 배회 등 반응 중이 아니면 null. 서버에서만 유효. (#76)</summary>
    public Transform ThreatTarget { get; private set; }

    /// <summary>
    /// 본부 인계 판정이 끝났는가 — <see cref="MarkDelivered"/>로 ArrestJudge가 세팅한다. (#230)
    /// 판정 완료분은 인계 방치 타이머에서 빠지고(본부에서 탈출하면 안 된다), 인계존 재진입 시 중복 판정도 막는다.
    /// 서버(또는 오프라인)에서만 유효 — 판정·인계존 게이트가 모두 서버 전용이라 동기화하지 않는다.
    /// 판정 후 본부에 남는 NPC의 처리는 유치장(#228)이 가져간다.
    /// </summary>
    public bool IsDelivered { get; private set; }

    /// <summary>인계 판정 완료로 표시 — ArrestJudge 전용. 서버(또는 오프라인)에서만 호출된다. (#230)</summary>
    public void MarkDelivered()
    {
        if (IsSpawned && !IsServer)
            return;

        IsDelivered = true;
    }

    /// <summary>
    /// 인계 판정 완료 표시를 되돌린다 — 범인 탈출 이벤트(#231) 전용. 서버(또는 오프라인)에서만 호출된다.
    ///
    /// <b>재검거의 핵심이다.</b> <see cref="HqDropoffZone"/>은 <see cref="IsDelivered"/>가 켜진 NPC를
    /// 인계존에서 통째로 무시하므로(중복 판정 방지, #230), 이 플래그를 되돌리지 않으면 탈출한 범인을
    /// 다시 잡아 와도 판정이 아예 나지 않는다.
    /// </summary>
    public void ClearDelivered()
    {
        if (IsSpawned && !IsServer)
            return;

        IsDelivered = false;
    }

    /// <summary>
    /// 현재 NPC 상태. 네트워크 세션 중에는 동기화된 값이라 클라이언트에서도 안전하게 읽을 수 있다.
    /// (StateMachine.CurrentState는 서버에서만 갱신되므로 외부 코드는 반드시 이 프로퍼티를 읽을 것)
    /// </summary>
    public NpcState CurrentState => IsSpawned ? m_networkState.Value : m_stateMachine.CurrentState;

    /// <summary>상태 변경 이벤트 — 서버·클라이언트 모든 피어에서 발생한다. 애니메이션 등 표현 계층이 구독. (#56)</summary>
    public event Action<NpcState> OnStateChanged;

    /// <summary>공격 스윙 1회를 휘두를 때 발행 — 전 피어에서 발생한다(서버는 로컬 발행 + ClientRpc 중계).
    /// 인자는 재생할 스윙 변형 index — 서버가 뽑아 전 피어가 같은 클립을 재생하므로, HP 감소 순간(서버가
    /// 그 클립의 타격 오프셋으로 판정)과 화면 속 주먹이 닿는 순간이 일치한다.
    /// 애니메이션 표현(<see cref="NpcAnimationDriver"/>)이 구독해 단발 스윙 모션을 트리거한다.
    /// FSM 상태와 독립한 순간 이벤트라 State 동기화와 별개로 스윙 타이밍을 정확히 맞춘다. (#220, ThugAttacker.OnAttack과 동일 패턴)</summary>
    public event Action<int> OnAttackSwing;

    /// <summary>연행 중 따라갈 대상(체포한 플레이어). 연행 중이 아니면 null. 서버에서만 유효.</summary>
    public Transform EscortTarget { get; private set; }

    /// <summary>수감 중 걸어갈 유치장 수용 지점. 수감 중이 아니면 null. 서버에서만 유효. (#228)</summary>
    public Transform JailCell { get; private set; }

    /// <summary>유치장 수용 지점 도달 — 유치장(JailZone)이 구독해 수용 인원을 올린다. 서버에서만 발생. (#228)</summary>
    public event Action<NpcController> OnJailed;

    /// <summary>침입 중 걸어갈 목표 지점(유치장 자물쇠). 침입 중이 아니면 null. 서버에서만 유효. (#231)</summary>
    public Transform IntrudeTarget { get; private set; }

    /// <summary>자물쇠에 도달해 해제를 시작하기까지 걸리는 시간(초). 탈출 이벤트가 StartIntrude로 넘겨준다. (#231)</summary>
    public float IntrudeUnlockSeconds { get; private set; }

    /// <summary>침입 이동 종료 — reached=true 도달, false 경로 실패. 탈출 이벤트가 구독한다. 서버에서만 발생. (#231)</summary>
    public event Action<NpcController, bool> OnIntrudeFinished;

    /// <summary>
    /// 자물쇠 해제 착수 — 목표에 도달해 해제 채널링을 시작한 순간. 탈출 이벤트가 구독해 본부 경보를 울린다.
    /// 도달과 해제 완료(<see cref="OnIntrudeFinished"/>) 사이의 대응 구간을 여는 신호다. 서버에서만 발생. (#231)
    /// </summary>
    public event Action<NpcController> OnIntrudeUnlockStarted;

    private void Awake()
    {
        m_agent = GetComponent<NavMeshAgent>();

        m_stateMachine = new NpcStateMachine();
        m_stateMachine.AddState(NpcState.Idle, new NpcIdleState(this, m_idleConfig));
        m_stateMachine.AddState(NpcState.Walk, new NpcWalkState(this, m_walkConfig));
        m_stateMachine.AddState(NpcState.Captured, new NpcCapturedState(this, m_capturedConfig));
        m_stateMachine.AddState(NpcState.Escorted, new NpcEscortedState(this, m_escortConfig));
        m_stateMachine.AddState(NpcState.Run, new NpcFleeState(this, m_fleeConfig));
        m_stateMachine.AddState(NpcState.Attack, new NpcResistState(this, m_resistConfig, m_fleeConfig));
        m_stateMachine.AddState(NpcState.Stunned, new NpcStunnedState(this, m_stunConfig));
        m_stateMachine.AddState(NpcState.Panic, new NpcPanicState(this, m_panicConfig));
        m_stateMachine.AddState(NpcState.Jailed, new NpcJailedState(this));
        m_stateMachine.AddState(NpcState.Intruding, new NpcIntrudeState(this));
        m_stateMachine.AddState(NpcState.Detained, new NpcDetainedState(this));
        m_stateMachine.AddState(NpcState.Chasing, new NpcChaseState(this, m_chaseConfig, m_walkConfig, m_fleeConfig));
        m_stateMachine.AddState(NpcState.PenaltyEscorting, new NpcPenaltyEscortState(this, m_escortConfig));

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
        m_agent.speed *= Random.Range(m_commonConfig.SpawnSpeedMultiplierMin, m_commonConfig.SpawnSpeedMultiplierMax);

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

        // 넉백 비행 중에는 FSM을 돌리지 않는다 — NavMeshAgent를 꺼 둔 채라 상태 클래스가
        // SetDestination/isStopped를 부르면 "agent not on NavMesh" 에러가 쏟아진다 (#232)
        if (m_knockbackActive)
        {
            TickKnockback();
            return;
        }

        m_stateMachine.Tick();
        EmitDisturbancePulse();
    }

    // 저항·도주 중인 NPC는 그 자체가 소란의 원천 — 주기적으로 주변 시민을 패닉시킨다 (#81)
    private void EmitDisturbancePulse()
    {
        NpcState state = m_stateMachine.CurrentState;
        if (state != NpcState.Run && state != NpcState.Attack)
            return;

        RequestDisturbancePulse();
    }

    /// <summary>
    /// 소란 펄스를 1회 요청한다 — 상태 클래스가 자기 사정으로 소란을 낼 때 쓴다.
    /// 주기 스로틀은 자동 펄스(<see cref="EmitDisturbancePulse"/>)와 공유하므로 펄스 타이머가 둘로 갈라지지 않는다.
    /// 서버(또는 오프라인) 전용 — FSM Tick 안에서만 불린다. (#81, #230)
    /// </summary>
    public void RequestDisturbancePulse()
    {
        if (Time.time < m_nextDisturbancePulseTime)
            return;

        m_nextDisturbancePulseTime = Time.time + m_commonConfig.DisturbancePulseInterval;
        BroadcastDisturbance(transform.position, m_commonConfig.DisturbanceRadius);
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

    /// <summary>기절에서 일어나기 시작할 때 발행 — 전 피어에서 발생한다(서버는 로컬 발행 + ClientRpc 중계).
    /// 일어나는 구간은 FSM 상태가 여전히 Stunned라(그 동안 움직이지 않는다) 상태 동기화만으로는
    /// 클라이언트가 알 수 없다 — 스윙(OnAttackSwing)과 같은 순간 이벤트로 전달한다. (#269)</summary>
    public event Action OnStandUp;

    /// <summary>기절 해제 직전 일어나는 모션을 전 피어에 알린다 — 서버(또는 오프라인) FSM Tick에서만 호출한다. (#269)</summary>
    public void RaiseStandUp()
    {
        OnStandUp?.Invoke(); // 서버·오프라인 로컬 발행
        if (IsSpawned && IsServer)
            PlayStandUpClientRpc();
    }

    [ClientRpc]
    private void PlayStandUpClientRpc()
    {
        // 서버(호스트)는 위에서 이미 발행했으므로 원격 클라에서만 중계
        if (IsServer)
            return;
        OnStandUp?.Invoke();
    }

    /// <summary>공격 스윙 1회를 전 피어에 알린다 — 애니메이션 표현용. 서버(또는 오프라인) FSM Tick에서만 호출한다.
    /// 서버는 로컬 발행 + ClientRpc로 원격 클라에 중계한다. (#220, ThugAttacker.NotifyAttack과 동일 패턴)</summary>
    public void RaiseAttackSwing(int variant)
    {
        OnAttackSwing?.Invoke(variant); // 서버·오프라인 로컬 발행
        if (IsSpawned && IsServer)
            PlayAttackSwingClientRpc(variant);
    }

    [ClientRpc]
    private void PlayAttackSwingClientRpc(int variant)
    {
        // 서버(호스트)는 위에서 이미 발행했으므로 원격 클라에서만 중계
        if (IsServer)
            return;
        OnAttackSwing?.Invoke(variant);
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

    /// <summary>연행 시작 — 체포 성공 직후 호출. NPC가 target(플레이어)을 따라 이동한다. (#59)</summary>
    // TODO: 아이템(수갑) 네트워크화 시 클라 입력 → ServerRpc 경로로 호출되도록 연결 (#56에서는 서버 가드만)
    public void StartEscort(Transform target)
    {
        // FSM 전이는 서버 권위 — 클라이언트에서 직접 부르면 무시한다
        if (IsSpawned && !IsServer)
            return;

        EscortTarget = target;
        m_stateMachine.ChangeState(NpcState.Escorted);
    }

    /// <summary>연행 중단 — 그 자리에서 체포(Captured) 상태로 멈춘다. (#59)</summary>
    public void StopEscort()
    {
        if (IsSpawned && !IsServer)
            return;

        EscortTarget = null;
        m_stateMachine.ChangeState(NpcState.Captured);
    }

    // ---- 유치장 (#228) ----

    /// <summary>
    /// 수감 — 인계존 판정에서 진범·경범죄로 확정된 NPC를 유치장으로 보낸다. (CustodyRouter 경유, GDD 7-2)
    /// cell(수용 지점)까지 스스로 걸어가 그 자리에 수용된다. cell이 null이면 그 자리에서 수용된 것으로 처리한다.
    /// </summary>
    public void SendToJail(Transform cell)
    {
        // FSM 전이는 서버 권위 — StartEscort와 동일하게 클라이언트 호출은 무시한다
        if (IsSpawned && !IsServer)
            return;

        EscortTarget = null; // 판정 시점에 연행은 이미 풀렸지만, 참조가 남아 있으면 여기서 끊는다
        JailCell = cell;
        m_stateMachine.ChangeState(NpcState.Jailed);
    }

    /// <summary>수용 지점 도달 통보 — NpcJailedState 전용. 유치장이 이 이벤트로 수용 인원을 센다.</summary>
    public void NotifyJailed() => OnJailed?.Invoke(this);

    // ---- 오검거 페널티: 수용·추격·호송 (#277/#278/#279) ----
    // FSM 전이는 전부 서버 권위 — WrongfulArrestPenalty(서버)만 호출한다. 클라 호출은 StartEscort와 같은 방식으로 무시.

    /// <summary>원한 구역 수용 지점. 수용(Detained) 중이 아니면 null. 서버에서만 유효. (#277)</summary>
    public Transform DetentionSpot { get; private set; }

    /// <summary>추격 대상 플레이어. 추격 중이 아니면 null. 서버에서만 유효. (#278)</summary>
    public Transform ChaseTarget { get; private set; }

    /// <summary>수렴 대상(포획된 플레이어) — 설정되면 추격 상태가 일반 추격 대신 이 대상에게 모인다. (#279)</summary>
    public Transform PenaltyConvergeTarget { get; private set; }

    /// <summary>격퇴를 건 플레이어 — 추격 상태가 이 대상 반대로 도주하고 재추격 쿨다운을 건다. (#278)</summary>
    public Transform ChaseRepelBy { get; private set; }

    /// <summary>격퇴 도주가 끝나는 시각(Time.time). 이 시각 전에는 추격 대신 도주한다. (#278)</summary>
    public float ChaseRepelUntil { get; private set; }

    /// <summary>호송 선두 NPC — null이면 자신이 선두(광장으로 직접 걷는다). (#279)</summary>
    public NpcController PenaltyEscortLeader { get; private set; }

    /// <summary>호송 대형에서 선두 기준 로컬 오프셋 — 선두는 무시. (#279)</summary>
    public Vector3 PenaltyEscortOffset { get; private set; }

    /// <summary>호송 목적지(광장). 호송 중이 아니면 null. (#279)</summary>
    public Transform PenaltyEscortGoal { get; private set; }

    /// <summary>추격 NPC가 대상을 포획한 순간 발행 — WrongfulArrestPenalty가 구독해 수렴·호송을 개시한다. 서버에서만 발생. (#278)</summary>
    public event Action<NpcController, Transform> OnPenaltyCaught;

    /// <summary>포획 통보 — NpcChaseState 전용. (#278)</summary>
    public void NotifyPenaltyCaught(Transform caught) => OnPenaltyCaught?.Invoke(this, caught);

    /// <summary>
    /// 원한 구역 수용 — 오검거당한 시민을 석방 대신 전용 구역으로 보낸다. (#277)
    /// spot이 null이면(구역 미배선 씬) 그 자리에서 수용된 것으로 처리한다 — SendToJail의 null cell과 동일 관례.
    /// </summary>
    public void SendToDetention(Transform spot)
    {
        if (IsSpawned && !IsServer)
            return;

        EscortTarget = null;
        DetentionSpot = spot;
        m_stateMachine.ChangeState(NpcState.Detained);
    }

    /// <summary>추격 출동 — 임계치를 넘긴 플레이어를 초기 타겟으로 쫓기 시작한다. (#278)</summary>
    public void StartPenaltyChase(Transform target)
    {
        if (IsSpawned && !IsServer)
            return;

        DetentionSpot = null;
        ChaseTarget = target;
        m_stateMachine.ChangeState(NpcState.Chasing);
    }

    /// <summary>추격 타겟 교체 — 범위 이탈 재타겟(NpcChaseState)·수렴 지시(매니저)가 호출한다. (#278)</summary>
    public void SetChaseTarget(Transform target)
    {
        if (IsSpawned && !IsServer)
            return;

        ChaseTarget = target;
    }

    /// <summary>
    /// 수렴 개시 — 포획된 플레이어에게 모인다. 추격 중이 아니었어도(막 수용된 NPC 등) 추격 상태로 끌어와 모은다. (#279)
    /// </summary>
    public void StartPenaltyConverge(Transform caught)
    {
        if (IsSpawned && !IsServer)
            return;

        PenaltyConvergeTarget = caught;
        if (m_stateMachine.CurrentState != NpcState.Chasing)
            m_stateMachine.ChangeState(NpcState.Chasing);
    }

    /// <summary>
    /// 격퇴 — 호루라기(#250 후속)의 연결고리. by에게서 잠시 도주하고, 재추격 쿨다운 동안 그 플레이어를 노리지 않는다. (#278)
    /// 수렴 중(포획 확정 후)에는 무시한다 — 유예 창은 잡히기 전까지다.
    /// </summary>
    public void ApplyChaseRepel(Transform by)
    {
        if (IsSpawned && !IsServer)
            return;
        if (PenaltyConvergeTarget != null)
            return;

        ChaseRepelBy = by;
        ChaseRepelUntil = Time.time + m_chaseConfig.RepelFleeSeconds;
    }

    /// <summary>호송 시작 — goal(광장)으로 이동. leader가 null이면 자신이 선두, 아니면 선두 기준 offset 위치를 따라간다. (#279)</summary>
    public void StartPenaltyEscort(Transform goal, NpcController leader, Vector3 offset)
    {
        if (IsSpawned && !IsServer)
            return;

        PenaltyEscortGoal = goal;
        PenaltyEscortLeader = leader;
        PenaltyEscortOffset = offset;
        m_stateMachine.ChangeState(NpcState.PenaltyEscorting);
    }

    /// <summary>
    /// 페널티 임무 종료 — 추격·수렴·호송 참조를 정리하고 배회(Idle)로 복귀한다(시민 복귀). (#279)
    /// 격퇴 잔여값도 지운다 — 다음 페널티 발동 때 이전 도주가 이어지지 않게.
    /// </summary>
    public void EndPenaltyDuty()
    {
        if (IsSpawned && !IsServer)
            return;

        DetentionSpot = null;
        ChaseTarget = null;
        PenaltyConvergeTarget = null;
        ChaseRepelBy = null;
        ChaseRepelUntil = 0f;
        PenaltyEscortLeader = null;
        PenaltyEscortGoal = null;
        m_stateMachine.ChangeState(NpcState.Idle);
    }

    // ---- 침입 (#231) ----

    /// <summary>
    /// 침입 시작 — 돌발 이벤트가 스폰한 침입자를 target(유치장 자물쇠)까지 걸어가게 한다. (GDD 6-4)
    /// 도달하면 unlockSeconds 동안 그 자리에서 해제 채널링을 하고, 다 채워야 <see cref="OnIntrudeFinished"/>가
    /// reached=true로 통보된다 — 그 사이가 플레이어의 대응 구간이다.
    /// </summary>
    public void StartIntrude(Transform target, float unlockSeconds)
    {
        // FSM 전이는 서버 권위 — StartEscort와 동일하게 클라이언트 호출은 무시한다
        if (IsSpawned && !IsServer)
            return;

        IntrudeTarget = target;
        IntrudeUnlockSeconds = unlockSeconds;
        m_stateMachine.ChangeState(NpcState.Intruding);
    }

    /// <summary>침입 이동 종료 통보 — NpcIntrudeState 전용.</summary>
    public void NotifyIntrudeFinished(bool reached) => OnIntrudeFinished?.Invoke(this, reached);

    /// <summary>자물쇠 해제 착수 통보 — NpcIntrudeState 전용.</summary>
    public void NotifyIntrudeUnlockStarted() => OnIntrudeUnlockStarted?.Invoke(this);

    /// <summary>
    /// 수갑 해제 — 오검거로 판정된 무고한 시민을 풀어준다. 배회(Idle)로 복귀한다. (GDD 7-2/7-3, #228)
    /// 체포(Captured) 상태에서만 유효 — 판정 직후 ArrestJudge가 연행을 풀어 Captured로 만들어 둔 상태를 이어받는다.
    /// 오검거 카운트·페널티는 여기서 다루지 않는다 (ArrestJudge.OnArrestJudged를 구독하는 #101 담당).
    /// </summary>
    public void ReleaseFromCustody()
    {
        if (IsSpawned && !IsServer)
            return;
        if (m_stateMachine.CurrentState != NpcState.Captured)
            return;

        JailCell = null;
        m_stateMachine.ChangeState(NpcState.Idle);
    }

    // ---- 수갑 소모·반환 (#229) ----

    /// <summary>
    /// 이 NPC에 수갑이 채워져 있는가 — 체포 성공 시 잡은 플레이어에게서 옮겨온 수갑(PlayerLoadout.ConsumeHandcuffsTo).
    /// 재연행 시 수갑을 또 소모하지 않도록 PlayerEscorter가 이걸로 첫 연행 여부를 가른다. 부착 자식 기준.
    /// </summary>
    public bool HasHandcuffs => FindHeldHandcuffs() != null;

    // 채워진 수갑을 찾는다 — 없으면 null. 소모 시 NPC 루트 직속 자식으로 붙으므로(ConsumeHandcuffsTo)
    // 깊은 캐릭터 리그를 통째로 훑는 GetComponentInChildren 대신 루트 직속 자식만 본다(PlayerLoadout과 같은 패턴).
    private Handcuffs FindHeldHandcuffs()
    {
        for (int i = 0; i < transform.childCount; i++)
        {
            if (transform.GetChild(i).TryGetComponent(out Handcuffs cuffs))
            {
                return cuffs;
            }
        }

        return null;
    }

    /// <summary>
    /// 채워진 수갑을 발밑 바닥에 떨어뜨려 반환한다 — 인계존 판정 후 ArrestJudge.Judge가 호출. (#229)
    /// 판정 위치(현재 NPC 위치)에 놓이며, 부모에서 분리되는 순간 WorldItemPickup이 다시 월드 표시·줍기를 켠다.
    /// 서버(또는 오프라인)에서만. 수갑이 없으면(오검거 아닌 직접 스폰 테스트 등) 무동작.
    /// </summary>
    public void DropHandcuffs()
    {
        if (IsSpawned && !IsServer)
        {
            return;
        }

        Handcuffs cuffs = FindHeldHandcuffs();
        if (cuffs == null)
        {
            return;
        }

        NetworkObject cuffsNetworkObject = cuffs.NetworkObject;
        if (cuffsNetworkObject == null)
        {
            return;
        }

        // 부모(NPC)에서 분리 — 소유권은 커스터디 진입 때 이미 서버로 돌아와 있어 월드 상태 그대로다.
        cuffsNetworkObject.TrySetParent((Transform)null, true);
        cuffsNetworkObject.transform.position = transform.position;
    }

    // ---- 검거 반응 (#76) ----
    // FSM 전이는 전부 서버 권위 — 클라이언트 호출은 StartEscort와 같은 방식으로 무시한다.
    // TODO: 아이템/상호작용 네트워크 전환(#55 계열) 시 클라 입력 → ServerRpc 경로로 연결

    /// <summary>도주 시작 — 수갑 채널링 성공 순간 도주형의 반응. threat(플레이어) 반대 방향으로 달아난다.</summary>
    public void StartFlee(Transform threat)
    {
        if (IsSpawned && !IsServer)
            return;

        ThreatTarget = threat;
        m_stateMachine.ChangeState(NpcState.Run);
    }

    /// <summary>위협 참조 정리 — 반응(도주·저항)이 끝나는 지점에서 호출한다.</summary>
    public void ClearThreat() => ThreatTarget = null;

    /// <summary>저항 시작 — 수갑 채널링 성공 순간 저항형의 반응. 그 자리에서 버틴다.</summary>
    public void StartResist(Transform subduer = null)
    {
        if (IsSpawned && !IsServer)
            return;

        // 저항을 유발한(수갑 채우려던) 플레이어를 위협으로 기억한다 — 제압 실패 시 이 대상에게서 도주한다.
        // (도주형이 StartFlee(subduer)로 위협을 받는 것과 대칭 — #205)
        ThreatTarget = subduer;
        m_stateMachine.ChangeState(NpcState.Attack);
    }

    /// <summary>저항 진입 시 게이지를 최대로 리셋한다 — NpcResistState.Enter 전용.</summary>
    public void ResetSubdueGauge()
    {
        SetSubdueGauge(m_resistConfig.SubdueGaugeMax);
    }

    /// <summary>
    /// 제압 타격 — 저항 게이지를 깎는다. 진압봉 등 타격 수단(후속 아이템 이슈)이 호출.
    /// 여러 명이 함께 때리면 그만큼 빨리 깎인다 (GDD 7-4 협동 인센티브).
    /// </summary>
    public void ApplySubdueHit(float amount)
    {
        if (IsSpawned && !IsServer)
            return;
        if (CurrentState != NpcState.Attack)
            return; // 저항 중이 아닐 때의 타격은 무시 — 배회 NPC 폭행 방지

        SetSubdueGauge(Mathf.Max(0f, SubdueGauge - amount));
    }

    /// <summary>
    /// 제압 홀드 타격 요청 — 상호작용 경로(NpcSubdueInteractable)가 호출. (#79)
    /// 클라이언트에서 불리면 서버로 전달되므로 비호스트 플레이어의 타격도 게이지에 반영된다.
    /// 타격량은 서버가 자기 인스펙터 값(m_resistConfig.SubdueHitPower)을 쓴다 — 클라이언트가 수치를 보낼 수 없다.
    /// </summary>
    // TODO: 상호작용 네트워크 전환(#55 계열)에서 거리·조준 서버 검증 추가 (지금은 요청 자체는 신뢰)
    public void RequestSubdueHit()
    {
        if (IsSpawned && !IsServer)
        {
            SubdueHitRpc();
            return;
        }

        ApplySubdueHit(m_resistConfig.SubdueHitPower);
    }

    [Rpc(SendTo.Server)]
    private void SubdueHitRpc()
    {
        ApplySubdueHit(m_resistConfig.SubdueHitPower);
    }

    /// <summary>도주 중인 NPC 근접 제압 — 상호작용 홀드 성공 시 그 자리에서 체포. (NpcSubdueInteractable 경유)</summary>
    public void CaptureBySubdue()
    {
        if (IsSpawned && !IsServer)
            return;
        if (CurrentState != NpcState.Run)
            return;

        m_stateMachine.ChangeState(NpcState.Captured);
    }

    /// <summary>
    /// 기절 진입 — 테이저의 연결고리. 지속 시간이 끝나면 스스로 일어나 <b>도주</b>한다(#269 확정).
    /// </summary>
    /// <param name="threat">
    /// 기절시킨 상대(테이저 사수). 깨어났을 때 이 대상에게서 도망친다 — 없으면(null) 도주 상태가
    /// 그 시점의 가장 가까운 추격자를 폴백으로 잡고, 주변에 아무도 없으면 배회로 돌아간다(NpcFleeState).
    /// </param>
    public void EnterStunned(Transform threat = null)
    {
        if (IsSpawned && !IsServer)
            return;

        ThreatTarget = threat;
        m_stateMachine.ChangeState(NpcState.Stunned);
    }

    // ---- 밧줄 끌기 (#269) ----

    private bool m_roped;

    /// <summary>밧줄로 묶여 끌리는 중인가 — 서버 권위. 묶인 동안 기절 타이머가 정지된다(NpcStunnedState).</summary>
    public bool IsRoped => m_roped;

    /// <summary>밧줄 끌기 시작 — 기절한 대상을 PlayerEscorter가 서버에서 호출. 위치를 끄는 플레이어가 직접 제어하므로
    /// NavMeshAgent를 끈다(켜져 있으면 에이전트가 위치를 도로 잡아당긴다). 상태는 Stunned를 그대로 유지한다.
    /// 끈 플레이어를 위협으로 기억한다 — 놓아준 뒤 깨어나면 그 플레이어에게서 도망친다.</summary>
    public void StartRopeDrag(Transform dragger = null)
    {
        if (IsSpawned && !IsServer)
            return;

        if (dragger != null)
            ThreatTarget = dragger;
        m_roped = true;
        if (m_agent != null && m_agent.enabled)
            m_agent.enabled = false;
    }

    /// <summary>밧줄 끌기 해제 — 에이전트를 되살려 NavMesh로 복귀(Warp)시킨다. 안 하면 이후 이동·상태가 깨진다.
    /// NPC는 기절 상태를 이어가다 스스로 깨어난다.</summary>
    public void StopRopeDrag()
    {
        if (IsSpawned && !IsServer)
            return;

        m_roped = false;
        if (m_agent != null)
        {
            m_agent.enabled = true;
            if (UnityEngine.AI.NavMesh.SamplePosition(transform.position, out UnityEngine.AI.NavMeshHit hit, 2f, UnityEngine.AI.NavMesh.AllAreas))
                m_agent.Warp(hit.position);
        }
    }

    /// <summary>끌리는 동안 위치·회전을 설정한다 — 끄는 플레이어(PlayerEscorter)가 매 서버 프레임 호출.
    /// NetworkTransform이 전 클라에 복제하므로 원격 피어에서도 끌리는 위치가 맞는다.</summary>
    public void ServerDragTo(Vector3 position, Quaternion rotation)
    {
        if (IsSpawned && !IsServer)
            return;
        if (!m_roped)
            return;

        transform.SetPositionAndRotation(position, rotation);
    }

    // ---- 넉백 (#232) ----

    /// <summary>폭발 등으로 날아가는 중인가 — 이 동안 FSM·NavMesh는 멈춘다. 서버(또는 오프라인)에서만 유효.</summary>
    public bool IsKnockedBack => m_knockbackActive;

    /// <summary>
    /// 외력으로 날려보낸다 — 폭발 넉백(<see cref="BombDevice"/>) 등. 세기는 m/s 단위 초기 속도로 준다.
    ///
    /// <b>서버(또는 오프라인) 전용.</b> 플레이어 넉백은 각 피어가 자기 오너 캐릭터에 적용하지만
    /// (<see cref="PlayerMovement.AddKnockback"/>), NPC는 이동 권한이 서버의 NavMeshAgent에 있고
    /// 클라이언트는 NetworkTransform으로 결과만 받으므로 서버가 직접 민다.
    ///
    /// 수감(<see cref="NpcState.Jailed"/>)·침입(<see cref="NpcState.Intruding"/>)은 제외한다 —
    /// 이벤트가 그 NPC의 진행(수용·자물쇠 해제)을 쥐고 있어서, 중간에 날아가면 판정 경로가 끊긴다.
    /// 체포·연행 중인 NPC는 <b>수갑을 찬 채</b> 날아가고 착지 후에도 체포 상태로 남는다
    /// (연행만 풀린다 — <see cref="PlayerEscorter"/>가 Escorted 이탈을 보고 스스로 참조를 정리한다).
    /// </summary>
    public void ServerApplyKnockback(Vector3 velocity)
    {
        if (IsSpawned && !IsServer)
            return;
        if (m_knockbackActive)
            return; // 같은 폭발이 콜라이더 여러 개로 잡힌 중복 호출 — 처음 것만 받는다
        if (velocity.sqrMagnitude < 0.01f)
            return;

        NpcState state = m_stateMachine.CurrentState; // 서버 진실값 — 동기화 지연 없이 판정
        if (state == NpcState.Jailed || state == NpcState.Intruding)
            return;

        // 상태 전이가 먼저다 — 에이전트를 끄기 전에 넣어야 상태 클래스가 에이전트를 정상적으로 정리한다.
        // 비행 중에는 FSM Tick을 건너뛰므로 상태별 타이머(기절 해제·인계 방치)는 착지 후부터 흐른다.
        m_knockbackLandingState = state == NpcState.Captured || state == NpcState.Escorted
            ? NpcState.Captured // 검거 유지 — 폭발로 수갑이 풀리지는 않는다
            : NpcState.Stunned; // 그 외엔 축 늘어져 날아가 기절 상태로 착지한다

        if (state == NpcState.Escorted)
            StopEscort(); // 연행만 해제(Captured 전이) — 에이전트 정리는 Escorted.Exit이 맡는다
        else
            m_stateMachine.ChangeState(m_knockbackLandingState);

        m_knockbackActive = true;
        m_knockbackVelocity = velocity;
        m_knockbackLaunch = transform.position;
        m_knockbackElapsed = 0f;

        // 에이전트가 켜져 있으면 매 프레임 NavMesh 위로 끌어내려 애초에 뜨지 못한다
        if (m_agent.enabled)
        {
            if (m_agent.isOnNavMesh)
                m_agent.ResetPath();
            m_agent.enabled = false;
        }
    }

    // 포물선 비행 1프레임. 착지하면 NavMesh 위로 되돌리고 발사 시점에 정한 상태로 넘긴다.
    private void TickKnockback()
    {
        m_knockbackElapsed += Time.deltaTime;
        m_knockbackVelocity.y += m_commonConfig.KnockbackGravity * Time.deltaTime;

        Vector3 next = transform.position + m_knockbackVelocity * Time.deltaTime;

        // 벽을 뚫고 날아가지 않게 실제 콜라이더를 훑는다 — 지금 위치에서 이번 프레임 수평 이동분만큼
        // 몸통 굵기로 스윕한다.
        //
        // NavMesh를 충돌 프록시로 쓰면 안 된다: NavMesh는 실제 벽보다 에이전트 반지름만큼 물러나 끝나고
        // 연석·차도 경계에서도 끊긴다. 실측(Test Scene)에서 벽이 11.8m 밖인 방향이 NavMesh 기준으로는
        // 2.0m에서 "막힘"으로 나왔고, 그걸 벽으로 치면 수평 속도가 비행 첫 프레임에 0이 되어
        // 넉백이 그대로 제자리 점프가 된다 (#232).
        Vector3 horizontalStep = new Vector3(next.x - transform.position.x, 0f, next.z - transform.position.z);
        float stepDistance = horizontalStep.magnitude;
        if (stepDistance > 0.0001f && SweepHitsObstacle(horizontalStep / stepDistance, stepDistance))
        {
            // 진짜 벽에 닿았다 — 수평 성분을 버리고 그 자리에서 떨어진다
            next.x = transform.position.x;
            next.z = transform.position.z;
            m_knockbackVelocity.x = 0f;
            m_knockbackVelocity.z = 0f;
        }

        transform.position = next;

        bool timedOut = m_knockbackElapsed >= m_commonConfig.KnockbackMaxFlightSeconds;
        if (!timedOut && m_knockbackVelocity.y > 0f)
            return; // 아직 상승 중 — 착지 판정은 내려올 때부터

        if (NavMesh.SamplePosition(transform.position, out NavMeshHit ground, m_commonConfig.KnockbackLandSampleDistance, NavMesh.AllAreas))
        {
            if (!timedOut && transform.position.y > ground.position.y + 0.05f)
                return; // 아직 공중

            EndKnockback(ground.position);
            return;
        }

        // NavMesh를 아예 벗어난 곳까지 날아갔다 — 시간이 다 되면 출발점으로 회수한다(맵 밖 유실 방지)
        if (timedOut)
            EndKnockback(m_knockbackLaunch);
    }

    // 이번 프레임 수평 이동 구간에 벽이 있는지 — 몸통 굵기로 훑는다.
    // 프레임이 튀어 한 번에 몇 미터씩 움직여도 구간 전체를 검사하므로 벽을 지나쳐 버리지 않는다.
    private bool SweepHitsObstacle(Vector3 direction, float distance)
    {
        float radius = m_agent.radius;
        Vector3 origin = transform.position + Vector3.up * Mathf.Max(radius, m_agent.height * 0.5f);
        int mask = m_commonConfig.KnockbackObstacleMask & ~(1 << gameObject.layer); // 자기 콜라이더에 걸리지 않게

        return Physics.SphereCast(origin, radius, direction, out RaycastHit _, distance, mask,
                                  QueryTriggerInteraction.Ignore);
    }

    private void EndKnockback(Vector3 landing)
    {
        m_knockbackActive = false;
        m_knockbackVelocity = Vector3.zero;

        m_agent.enabled = true;
        m_agent.Warp(landing); // 에이전트를 NavMesh 위 착지점에 다시 붙인다

        // Warp가 실패했으면(착지점이 NavMesh 밖) 상태 전이를 시키지 않는다 —
        // 상태 클래스들이 곧바로 에이전트를 건드려 에러가 난다. 다음 프레임 이후 스스로 복구되진 않으므로 남긴다.
        if (!m_agent.isOnNavMesh)
        {
            Debug.LogWarning("NpcController: 넉백 착지 지점을 NavMesh에 붙이지 못했다", this);
            return;
        }

        // 이미 발사 시점에 전이해 뒀다 — 여기 호출은 그 사이 상태가 바뀐 경우를 위한 보정이다.
        // (같은 상태면 StateMachine이 무시하므로 상태별 타이머가 착지 시점에 리셋되지도 않는다)
        m_stateMachine.ChangeState(m_knockbackLandingState);
    }

    // ---- 패닉 (#81) ----

    // 소란 전파용 공유 버퍼 — 서버(또는 오프라인)에서만 쓰므로 공유해도 안전하다
    private static readonly Collider[] s_disturbanceBuffer = new Collider[64];

    private float m_nextDisturbancePulseTime;

    /// <summary>
    /// 소란 발생 — position 반경 radius 안의 배회 NPC를 전부 패닉시킨다. (#81)
    /// 저항·도주 NPC의 펄스가 호출하며, 돌발 이벤트(GDD 6-4 후속 이슈)도 이 API로 소란을 일으킨다.
    /// 서버(또는 오프라인)에서 호출할 것 — 클라이언트에서 불려도 각 NPC의 EnterPanic이 무시한다.
    /// </summary>
    public static void BroadcastDisturbance(Vector3 position, float radius)
    {
        int hitCount = Physics.OverlapSphereNonAlloc(position, radius, s_disturbanceBuffer);
        for (int i = 0; i < hitCount; i++)
        {
            NpcController npc = s_disturbanceBuffer[i].GetComponentInParent<NpcController>();
            if (npc != null)
                npc.EnterPanic(position);
        }
    }

    /// <summary>
    /// 패닉 진입/갱신 — 배회 중(Idle/Walk)일 때만 전이한다.
    /// 검거(채널링·체포·연행)는 소란이 아니므로 이 메서드를 부르지 않고,
    /// 체포·연행·기절·반응 중(도주/저항)인 NPC는 여기서 걸러진다.
    /// 이미 패닉 중이면 소란 지점·진정 타이머만 갱신한다 (도주 방향은 다음 지점 갱신 때 반영).
    /// </summary>
    public void EnterPanic(Vector3 disturbancePosition)
    {
        if (IsSpawned && !IsServer)
            return;

        NpcState state = m_stateMachine.CurrentState; // 서버 진실값 — 동기화 지연 없이 판정
        if (state == NpcState.Panic)
        {
            PanicSource = disturbancePosition;
            LastDisturbedTime = Time.time;
            return;
        }

        if (state != NpcState.Idle && state != NpcState.Walk)
            return;

        PanicSource = disturbancePosition;
        LastDisturbedTime = Time.time;
        m_stateMachine.ChangeState(NpcState.Panic);
    }

    // 게이지는 서버 진실값과 동기화 변수에 함께 기록한다 — 오프라인에서는 NetworkVariable에 쓰지 않는다 (#56 상태 패턴과 동일)
    private void SetSubdueGauge(float value)
    {
        m_subdueGauge = value;
        if (IsSpawned && IsServer)
            m_syncedSubdueGauge.Value = value;
    }
}
