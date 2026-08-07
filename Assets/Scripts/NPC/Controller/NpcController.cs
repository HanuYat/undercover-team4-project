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
[RequireComponent(typeof(NpcCustody))] // 도메인 부품 — 누락 시 연행·수감 경로가 NRE로 죽는다 (#503)
[RequireComponent(typeof(NpcHealth))] // 도메인 부품 — 누락 시 체력·피해 경로가 NRE로 죽는다 (#503)
[RequireComponent(typeof(NpcIntruder))] // 도메인 부품 — 누락 시 침입 경로가 NRE로 죽는다 (#503)
[RequireComponent(typeof(NpcPenaltyAgent))] // 도메인 부품 — 누락 시 오검거·납치 경로가 NRE로 죽는다 (#503)
[RequireComponent(typeof(NpcReaction))] // 도메인 부품 — 누락 시 도주·저항 경로가 NRE로 죽는다 (#503)
[RequireComponent(typeof(NpcStun))] // 도메인 부품 — 누락 시 기절 경로가 NRE로 죽는다 (#503)
public partial class NpcController : NetworkBehaviour
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
    [SerializeField] private NpcCommonConfig m_commonConfig;
    [SerializeField] private NpcRopeDragConfig m_ropeDragConfig;

    private NavMeshAgent m_agent;
    private NpcStateMachine m_stateMachine;

    // 도메인 부품 — 같은 GameObject에 붙는다. [RequireComponent]로 누락을 막는다. (#503)
    private NpcCustody m_custody;
    private NpcHealth m_health;
    private NpcIntruder m_intruder;
    private NpcPenaltyAgent m_penalty;
    private NpcReaction m_reaction;
    private NpcStun m_stun;

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

    public NavMeshAgent Agent => m_agent;
    public NpcStateMachine StateMachine => m_stateMachine;

    /// <summary>추격 튜닝 SO — 부품이 코어에서 읽는다(튜닝 SO는 코어가 계속 들고 있다, 계획서 § 4-3).
    /// <see cref="NpcPenaltyAgent"/>가 격퇴 도주 시간을 읽는 용도다. 부품은 같은 어셈블리라 internal로 족하다. (#503)</summary>
    internal NpcChaseConfig ChaseConfig => m_chaseConfig;

    /// <summary>저항 튜닝 SO — <see cref="NpcReaction.ThreatSearchRadius"/>가 위협 탐색 반경을 산출하는 용도다.
    /// 튜닝 SO는 코어가 계속 들고 부품이 읽는다(계획서 § 4-3). (#503)</summary>
    internal NpcResistConfig ResistConfig => m_resistConfig;

    /// <summary>기절 튜닝 SO — <see cref="NpcStun"/>이 지속 시간·기상 클립 길이를,
    /// <see cref="NpcHealth"/>가 쓰러짐 기절 시간을 읽는다. (#503)</summary>
    internal NpcStunConfig StunConfig => m_stunConfig;

    /// <summary>공통 튜닝 SO — <see cref="NpcHealth.MaxHp"/>가 읽는다. 넉백 계열도 코어에서 직접 쓴다. (#503)</summary>
    internal NpcCommonConfig CommonConfig => m_commonConfig;

    /// <summary>
    /// 현재 NPC 상태. 네트워크 세션 중에는 동기화된 값이라 클라이언트에서도 안전하게 읽을 수 있다.
    /// (StateMachine.CurrentState는 서버에서만 갱신되므로 외부 코드는 반드시 이 프로퍼티를 읽을 것)
    /// </summary>
    public NpcState CurrentState => IsSpawned ? m_networkState.Value : m_stateMachine.CurrentState;

    /// <summary>상태 변경 이벤트 — 서버·클라이언트 모든 피어에서 발생한다. 애니메이션 등 표현 계층이 구독. (#56)</summary>
    public event Action<NpcState> OnStateChanged;

    /// <summary>신병 도메인 부품 — 연행·인계 표식·수감·감옥 퇴장·반출 표식을 들고 있다. (#59/#228/#537/#503)</summary>
    public NpcCustody Custody => m_custody;

    /// <summary>체력 도메인 부품 — HP·피해 적용·회복과 <see cref="IDamageable"/> 구현을 들고 있다. (#366/#503)</summary>
    public NpcHealth Health => m_health;

    /// <summary>침입 도메인 부품 — 목표·해제 시간·진행 이벤트를 들고 있다. (#231/#503)</summary>
    public NpcIntruder Intruder => m_intruder;

    /// <summary>페널티 임무 도메인 부품 — 오검거(#277~#279)·납치(#371)의 수용·추격·수렴·호송을 들고 있다. (#503)</summary>
    public NpcPenaltyAgent Penalty => m_penalty;

    /// <summary>검거 반응 도메인 부품 — 위협 대상·도주·저항·스윙을 들고 있다. (#76/#205/#213/#220/#503)</summary>
    public NpcReaction Reaction => m_reaction;

    /// <summary>기절 도메인 부품 — 스턴 오버레이·진입·해제를 들고 있다. (#292/#503)</summary>
    public NpcStun Stun => m_stun;

    private void Awake()
    {
        m_agent = GetComponent<NavMeshAgent>();
        m_custody = GetComponent<NpcCustody>();
        m_health = GetComponent<NpcHealth>();
        m_intruder = GetComponent<NpcIntruder>();
        m_penalty = GetComponent<NpcPenaltyAgent>();
        m_reaction = GetComponent<NpcReaction>();
        m_stun = GetComponent<NpcStun>();

        m_stateMachine = new NpcStateMachine();
        m_stateMachine.AddState(NpcState.Idle, new NpcIdleState(this, m_idleConfig));
        m_stateMachine.AddState(NpcState.Walk, new NpcWalkState(this, m_walkConfig));
        m_stateMachine.AddState(NpcState.Captured, new NpcCapturedState(this, m_capturedConfig));
        m_stateMachine.AddState(NpcState.Escorted, new NpcEscortedState(this, m_escortConfig));
        m_stateMachine.AddState(NpcState.Run, new NpcFleeState(this, m_fleeConfig));
        m_stateMachine.AddState(NpcState.Attack, new NpcResistState(this, m_resistConfig, m_fleeConfig));
        m_stateMachine.AddState(NpcState.Stunned, new NpcStunnedState(this, m_stunConfig));
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

        // 체력은 FSM 시동 전에 채운다 — 첫 틱부터 CurrentHp가 유효해야 한다 (#366)
        m_health.InitHealth();

        // 무게 추첨 — 라운드 내내 유지된다(재검거·탈옥 후에도 같은 값). (#398)
        InitDragWeight();

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

        // NavMesh 밖에서 굳은 몸의 회수 — 아래 모든 게이트보다 **먼저** 돈다 (#557).
        // 뒤로 내리면 스턴 게이트에 가려 기절한 채 굳은 NPC(=신고된 증상 그대로)에 영영 닿지 못한다.
        TickNavMeshRecovery();

        // 밧줄 장력 — 아래 넉백·스턴 게이트보다 **먼저** 돈다 (#390). 묶인 채 기절한 대상은 스턴
        // 오버레이를 단 채로 끌려가야 하므로(기절 중에도 장력이 돌아야 한다) 게이트 뒤로
        // 내리면 테이저→밧줄 콤보로 잡은 대상이 그 자리에 멈춘다. 끌기가 아니면 즉시 반환한다.
        // (넉백은 서로 배타적이다 — Escorted 대상이 넉백을 맞으면 StopEscort로 커스터디가 풀리고
        //  PlayerEscorter가 그것을 보고 끌기를 정리한다.)
        TickRopeDrag();

        // 줄이 풀리며 일어나는 구간 — 밧줄 장력과 같은 이유로 아래 게이트보다 **먼저** 돈다 (#513).
        // 뒤로 내리면 일어나는 도중 기절·넉백을 맞은 대상의 예약이 영원히 남는다.
        TickStandUp();

        // 넉백 비행 중에는 FSM을 돌리지 않는다 — NavMeshAgent를 꺼 둔 채라 상태 클래스가
        // SetDestination/isStopped를 부르면 "agent not on NavMesh" 에러가 쏟아진다 (#232)
        if (m_knockbackActive)
        {
            TickKnockback();
            return;
        }

        // 스턴 오버레이 중에는 FSM을 돌리지 않는다 — 상태는 그대로 둔 채 제자리에 얼린다.
        // 넉백 게이트 뒤에 두는 게 중요하다: 둘이 겹치면 넉백이 이긴다 (#292)
        if (m_stun.HasStunOverlay)
        {
            m_stun.Tick();
            return;
        }

        m_stateMachine.Tick();
    }

    // 서버(또는 오프라인)의 FSM 전이를 밖으로 전파한다
    private void HandleFsmStateChanged(NpcState state)
    {
        // 커스터디를 벗어나면 신병에 매달린 표식부터 내린다 — 전이와 같은 프레임에 맞아야 한다.
        // 묶임(#513)은 표현(누운 자세)이, 반출(#517)은 E 분기가 이 값을 본다.
        if (state != NpcState.Escorted && state != NpcState.Captured)
        {
            ClearTethers();
            m_custody.SetJailExtracted(false);
        }

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
    /// 클라이언트가 알 수 없다 — 스윙(<see cref="NpcReaction.OnAttackSwing"/>)과 같은 순간 이벤트로 전달한다. (#269)</summary>
    public event Action OnStandUp;

    /// <summary>일어나는 모션을 전 피어에 알린다 — 서버(또는 오프라인)에서만 호출한다.
    /// 기절 해제(#269)와 밧줄 풀림(#513) 두 경로가 쓴다.</summary>
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

    // 워프 기준점 주변에서 NavMesh를 찾을 때의 기본 탐색 반경(m).
    private const float k_warpSnapRadius = 2f;

    /// <summary>
    /// 기준점 주변에서 NavMesh 위 지점을 찾아 에이전트를 붙인다 — 붙었으면 true. (#503)
    ///
    /// 특정 도메인의 것이 아니라 <see cref="Agent"/>를 다루는 공용 유틸이라 코어에 둔다(계획서 § 4-6) —
    /// 밧줄 놓기(#369)와 감옥 방출(#537)이 함께 쓴다. 부품에 딸려 보내면 "Custody가 Rope를 참조한다"는
    /// 가짜 의존이 생긴다. 부품은 같은 어셈블리라 internal로 족하다.
    /// 실패하면 <b>호출부가</b> 대응한다 — 대안 지점을 시도할지 제자리에 둘지는 도메인마다 다르다.
    /// </summary>
    /// <param name="snapRadius">탐색 반경(m) — 생략하면 <see cref="k_warpSnapRadius"/>.
    /// 넓히는 건 최후 수단인 회수(<see cref="TickNavMeshRecovery"/>)뿐이다.</param>
    internal bool TryWarpNear(Vector3 origin, float snapRadius = k_warpSnapRadius)
    {
        if (!NavMesh.SamplePosition(origin, out NavMeshHit hit, snapRadius, NavMesh.AllAreas))
            return false;

        return m_agent.Warp(hit.position) && m_agent.isOnNavMesh;
    }

    // ---- 굳은 몸 회수 (#557) ----

    // 회수를 걸기까지의 유예(초) — 기절 시간(NpcStunConfig.StunSeconds)보다 짧아야 ExitStun보다 먼저
    // 붙어 깨어나는 경로(isStopped 복구 → StartFlee)가 이어진다. 한 프레임짜리 이탈까지 잡으면
    // 정상 경로의 워프와 겹쳐 몸이 두 번 튄다.
    private const float k_stuckGraceSeconds = 1f;

    // 회수용 탐색 반경(m) — 기본 반경으로 못 붙였을 때의 최후 수단. 8m은 실측이다: 맵 <b>안쪽</b>에서
    // 설 수 있는 지면 중 NavMesh가 2m 안에 없는 곳은 HQ 실내뿐이고(최대 5.5m) 야외는 전 구간 2m 안이다.
    // 더 넓혀도 맵 밖으로 나간 몸(#559)에는 어차피 닿지 않고, 엉뚱한 곳으로 튕겨 나갈 위험만 커진다.
    private const float k_stuckRecoverRadius = 8f;

    private float m_offNavMeshSeconds;

    // 회수 실패를 이미 알렸는가 — 재시도는 계속하되 로그는 굳은 구간당 한 번만 낸다.
    // 반경 밖까지 밀려나는 경로가 실제로 있어서다(#559 — 납치 반출이 NPC를 맵 밖 25m까지 끌고 나간다).
    // 그 경우 회수는 라운드 내내 실패하므로, 매초 LogError면 스택트레이스가 콘솔을 덮는다.
    private bool m_stuckReported;

    /// <summary>
    /// <b>에이전트가 켜져 있는데 NavMesh 밖</b>인 상태를 서버가 스스로 회수한다. 서버(또는 오프라인) 전용. (#557)
    ///
    /// 이 상태를 만드는 곳은 셋인데(밧줄 놓기·넉백 착지·기절 해제) 셋 다 붙이기에 실패하면 경고만 남기고
    /// 포기해서, 이후 <c>isStopped</c>·<c>SetDestination</c>이 조용히 실패하며 NPC가 그 자리에 굳었다
    /// (빌드 2 이슈 E의 재발). 호출부마다 폴백을 다는 대신 <b>결과 상태 하나</b>를 여기서 보면
    /// 앞으로 늘어날 호출부까지 함께 덮인다.
    ///
    /// 에이전트를 꺼 둔 구간(넉백 비행·밧줄 끌기)은 위치를 그쪽이 쥐고 있어 굳은 것이 아니다 — 건너뛴다.
    /// </summary>
    private void TickNavMeshRecovery()
    {
        if (!m_agent.enabled || m_agent.isOnNavMesh)
        {
            m_offNavMeshSeconds = 0f;
            m_stuckReported = false; // 다음에 또 굳으면 그때는 다시 알린다
            return;
        }

        m_offNavMeshSeconds += Time.deltaTime;
        if (m_offNavMeshSeconds < k_stuckGraceSeconds)
            return;

        m_offNavMeshSeconds = 0f; // 실패해도 유예를 다시 채워 매 프레임이 아니라 매 1초로 재시도한다

        Vector3 from = transform.position;
        if (!TryWarpNear(from, k_stuckRecoverRadius))
        {
            // 재시도는 이어진다 — 몸이 다시 끌려 들어오면(밧줄 등) 그때 붙는다. 알림만 한 번이다.
            if (!m_stuckReported)
            {
                m_stuckReported = true;
                Debug.LogError(
                    $"NpcController: NavMesh 밖에서 굳은 NPC를 {k_stuckRecoverRadius}m 안에서 회수하지 "
                        + $"못했다 — 재시도는 계속한다: {name} @{from.ToString("F1")}",
                    this
                );
            }

            return;
        }

        // 회수 사실 자체가 원인 추적의 유일한 단서다 — "무엇이 밖으로 밀어냈는가"는 아직 미확인이다
        Debug.LogWarning(
            $"NpcController: NavMesh 밖에서 굳은 NPC를 회수했다 — {name} "
                + $"{from.ToString("F1")} → {transform.position.ToString("F1")}",
            this
        );
    }
}
