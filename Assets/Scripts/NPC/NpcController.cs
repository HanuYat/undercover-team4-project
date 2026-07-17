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

    [Header("검거 반응 (#76)")]
    [Tooltip("도주 시 기본 이동 속도에 곱하는 배율")]
    [SerializeField] private float m_fleeSpeedMultiplier = 1.5f;
    [Tooltip("도주 목적지를 한 번에 이만큼(m) 앞으로 잡는다")]
    [SerializeField] private float m_fleeStepDistance = 10f;
    [Tooltip("추적자와 이 거리(m) 이상 벌어지면 도주 성공 — 배회로 복귀한다")]
    [SerializeField] private float m_fleeEscapeDistance = 25f;
    [Tooltip("저항 제압 게이지 최대치 — ApplySubdueHit로 깎여 0이 되면 체포된다")]
    [SerializeField] private float m_subdueGaugeMax = 100f;
    [Tooltip("기절(테이저 등) 지속 시간(초)")]
    [SerializeField] private float m_stunSeconds = 3f;

    [Header("저항 전투 (#79)")]
    [Tooltip("저항 중 범위 타격을 휘두르는 주기(초)")]
    [SerializeField] private float m_resistAttackInterval = 1.5f;
    [Tooltip("범위 타격이 닿는 반경(m)")]
    [SerializeField] private float m_resistAttackRange = 2f;
    [Tooltip("범위 타격 1회당 플레이어 HP 감소량")]
    [SerializeField] private int m_resistAttackDamage = 10;
    [Tooltip("제압 홀드 성공 1회가 깎는 제압 게이지량")]
    [SerializeField] private float m_subdueHitPower = 34f;
    [Tooltip("저항 시작 후 이 시간(초) 안에 제압당하지 않으면 플레이어 패배 — 도주형으로 전환된다 (GDD 7-4)")]
    [SerializeField] private float m_resistDefeatSeconds = 15f;
    [Tooltip("스윙 시작→타격이 닿는 프레임까지의 시간(초). 이 만큼 뒤에 데미지가 들어가므로 준비 동작이 곧 회피 창이 된다 (#220)")]
    [SerializeField] private float m_strikeOffsetSeconds = 0.45f;

    [Header("패닉 (#81)")]
    [Tooltip("소란(저항 전투·도주)이 주변 시민을 패닉시키는 전파 반경(m)")]
    [SerializeField] private float m_disturbanceRadius = 8f;
    [Tooltip("저항·도주 중 소란 펄스를 발산하는 주기(초) — 소란이 계속되면 지나가던 시민도 놀란다")]
    [SerializeField] private float m_disturbancePulseInterval = 1f;
    [Tooltip("패닉 시 기본 이동 속도에 곱하는 배율")]
    [SerializeField] private float m_panicSpeedMultiplier = 1.8f;
    [Tooltip("패닉 도주 지점을 한 번에 이만큼(m) 앞으로 잡는다")]
    [SerializeField] private float m_panicStepDistance = 8f;
    [Tooltip("마지막 소란 감지 후 이 시간(초)이 지나면 진정하고 배회로 복귀")]
    [SerializeField] private float m_panicCalmSeconds = 5f;

    private NavMeshAgent m_agent;
    private NpcStateMachine m_stateMachine;

    // 라운드 종료 시 정지(freeze) 플래그 — 서버(또는 오프라인)에서만 의미. 켜지면 FSM/이동을 멈춘다. (라운드 종료 freeze)
    private bool m_frozen;

    // 서버 권위 FSM 상태 — 서버만 쓰고 모든 클라이언트가 읽는다 (#56)
    private readonly NetworkVariable<NpcState> m_networkState = new NetworkVariable<NpcState>(NpcState.Idle);

    // 서버 권위 제압 게이지 — 저항(Attack) 상태에서만 의미. 진행도 UI(후속)를 위해 동기화한다 (#76)
    private readonly NetworkVariable<float> m_syncedSubdueGauge = new NetworkVariable<float>(0f);
    private float m_subdueGauge; // 서버·오프라인의 진실값 — m_networkState와 같은 이중 구조

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
    public float FleeSpeedMultiplier => m_fleeSpeedMultiplier;
    public float FleeStepDistance => m_fleeStepDistance;
    public float FleeEscapeDistance => m_fleeEscapeDistance;
    public float SubdueGaugeMax => m_subdueGaugeMax;
    public float StunSeconds => m_stunSeconds;
    public float ResistAttackInterval => m_resistAttackInterval;
    public float ResistAttackRange => m_resistAttackRange;
    public int ResistAttackDamage => m_resistAttackDamage;
    public float ResistDefeatSeconds => m_resistDefeatSeconds;
    public float StrikeOffsetSeconds => m_strikeOffsetSeconds;
    public float PanicSpeedMultiplier => m_panicSpeedMultiplier;
    public float PanicStepDistance => m_panicStepDistance;
    public float PanicCalmSeconds => m_panicCalmSeconds;

    /// <summary>패닉의 원인이 된 소란 지점 — 이 반대 방향으로 달아난다. 서버에서만 유효. (#81)</summary>
    public Vector3 PanicSource { get; private set; }

    /// <summary>마지막으로 소란을 감지한 시각(Time.time) — 패닉 진정 타이머 기준. 서버에서만 유효. (#81)</summary>
    public float LastDisturbedTime { get; private set; }

    /// <summary>현재 제압 게이지. 네트워크 세션 중에는 동기화된 값이라 클라이언트에서도 읽을 수 있다. (#76)</summary>
    public float SubdueGauge => IsSpawned ? m_syncedSubdueGauge.Value : m_subdueGauge;

    /// <summary>저항·도주 중 피해 다니는 위협 대상(체포를 시도한 플레이어). 배회 등 반응 중이 아니면 null. 서버에서만 유효. (#76)</summary>
    public Transform ThreatTarget { get; private set; }

    /// <summary>
    /// 현재 NPC 상태. 네트워크 세션 중에는 동기화된 값이라 클라이언트에서도 안전하게 읽을 수 있다.
    /// (StateMachine.CurrentState는 서버에서만 갱신되므로 외부 코드는 반드시 이 프로퍼티를 읽을 것)
    /// </summary>
    public NpcState CurrentState => IsSpawned ? m_networkState.Value : m_stateMachine.CurrentState;

    /// <summary>상태 변경 이벤트 — 서버·클라이언트 모든 피어에서 발생한다. 애니메이션 등 표현 계층이 구독. (#56)</summary>
    public event Action<NpcState> OnStateChanged;

    /// <summary>공격 스윙 1회를 휘두를 때 발행 — 전 피어에서 발생한다(서버는 로컬 발행 + ClientRpc 중계).
    /// 애니메이션 표현(<see cref="NpcAnimationDriver"/>)이 구독해 단발 스윙 모션을 트리거한다.
    /// FSM 상태와 독립한 순간 이벤트라 State 동기화와 별개로 스윙 타이밍을 정확히 맞춘다. (#220, ThugAttacker.OnAttack과 동일 패턴)</summary>
    public event Action OnAttackSwing;

    /// <summary>연행 중 따라갈 대상(체포한 플레이어). 연행 중이 아니면 null. 서버에서만 유효.</summary>
    public Transform EscortTarget { get; private set; }

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

    // 저항·도주 중인 NPC는 그 자체가 소란의 원천 — 주기적으로 주변 시민을 패닉시킨다 (#81)
    private void EmitDisturbancePulse()
    {
        NpcState state = m_stateMachine.CurrentState;
        if (state != NpcState.Run && state != NpcState.Attack)
            return;
        if (Time.time < m_nextDisturbancePulseTime)
            return;

        m_nextDisturbancePulseTime = Time.time + m_disturbancePulseInterval;
        BroadcastDisturbance(transform.position, m_disturbanceRadius);
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

    /// <summary>공격 스윙 1회를 전 피어에 알린다 — 애니메이션 표현용. 서버(또는 오프라인) FSM Tick에서만 호출한다.
    /// 서버는 로컬 발행 + ClientRpc로 원격 클라에 중계한다. (#220, ThugAttacker.NotifyAttack과 동일 패턴)</summary>
    public void RaiseAttackSwing()
    {
        OnAttackSwing?.Invoke(); // 서버·오프라인 로컬 발행
        if (IsSpawned && IsServer)
            PlayAttackSwingClientRpc();
    }

    [ClientRpc]
    private void PlayAttackSwingClientRpc()
    {
        // 서버(호스트)는 위에서 이미 발행했으므로 원격 클라에서만 중계
        if (IsServer)
            return;
        OnAttackSwing?.Invoke();
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
        SetSubdueGauge(m_subdueGaugeMax);
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
    /// 타격량은 서버가 자기 인스펙터 값(m_subdueHitPower)을 쓴다 — 클라이언트가 수치를 보낼 수 없다.
    /// </summary>
    // TODO: 상호작용 네트워크 전환(#55 계열)에서 거리·조준 서버 검증 추가 (지금은 요청 자체는 신뢰)
    public void RequestSubdueHit()
    {
        if (IsSpawned && !IsServer)
        {
            SubdueHitRpc();
            return;
        }

        ApplySubdueHit(m_subdueHitPower);
    }

    [Rpc(SendTo.Server)]
    private void SubdueHitRpc()
    {
        ApplySubdueHit(m_subdueHitPower);
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

    /// <summary>기절 진입 — 테이저(후속 아이템 이슈)의 연결고리. 지속 시간 후 스스로 배회로 복귀한다.</summary>
    public void EnterStunned()
    {
        if (IsSpawned && !IsServer)
            return;

        m_stateMachine.ChangeState(NpcState.Stunned);
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
