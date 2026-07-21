using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// NpcController의 검거 반응 파트 — 도주(#76)·저항 전투(#79/#220)·제압 게이지·기절.
/// 수갑 채널링에 걸린 순간부터 체포되기까지의 "저항하는" 행동과 튜닝을 든다.
/// 이동만 했고 동작 변화는 없다 (도메인 partial 분리).
/// FSM 전이는 전부 서버 권위 — 클라이언트 호출은 StartEscort와 같은 방식으로 무시한다.
/// TODO: 아이템/상호작용 네트워크 전환(#55 계열) 시 클라 입력 → ServerRpc 경로로 연결
/// </summary>
public partial class NpcController
{
    // 위협 탐색 반경 배율 — 저항 패배 후 도주 대상을 찾을 때(#205)와 도주 방향 산출(#213)이 공유한다.
    // 공격 범위보다 넓게 잡아 멀리서 접근 중인 플레이어도 회피 대상에 들어온다.
    private const float k_threatSearchRadiusMultiplier = 5f;

    [Header("검거 반응 (#76)")]
    [Tooltip("도주 시 기본 이동 속도에 곱하는 배율")]
    [SerializeField] private float m_fleeSpeedMultiplier = 1.5f;
    [Tooltip("도주 목적지를 한 번에 이만큼(m) 앞으로 잡는다")]
    [SerializeField] private float m_fleeStepDistance = 10f;
    [Tooltip("추적자와 이 거리(m) 이상 벌어지면 도주 성공 — 배회로 복귀한다")]
    [SerializeField] private float m_fleeEscapeDistance = 25f;
    [Tooltip("도주 경로가 플레이어에게 이 거리(m)보다 가까이 스치면 그 방향은 버린다 — 체포 사거리(PlayerInteractor.Range, 3m) + 여유 마진")]
    [SerializeField] private float m_fleeClearanceRadius = 4f;
    [Tooltip("도주 진입 후 이 시간(초) 안에는 포위됐어도 저항으로 되돌아가지 않는다 — 저항↔도주 왕복 방지 (#213)")]
    [SerializeField] private float m_fleeResistCooldown = 2f;

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
    [Tooltip("스윙 시작→타격 프레임까지의 시간(초) — 클립별 오프셋(m_swingImpactOffsets)이 비었거나 범위 밖일 때만 쓰는 폴백값 (#220)")]
    [SerializeField] private float m_strikeOffsetSeconds = 0.45f;
    [Tooltip("스윙 변형(SwingVariant)별 타격 오프셋(초). 인덱스 = NpcAnimatorControllerBuilder의 클립 순서(attack02·03·04·05). 배열 길이가 곧 변형 개수 — 블렌드 트리 자식 수와 같아야 한다. 각 클립의 주먹 최대 신전 시점에 맞춤 (#220)")]
    [SerializeField] private float[] m_swingImpactOffsets = { 0.63f, 0.53f, 0.44f, 0.73f };
    [Tooltip("타격이 닿는 정면 부채꼴의 전체 각도(도). 이 각도 안(정면 기준 ±절반)에 있는 플레이어만 맞는다 — 등 뒤·측면은 빗나간다 (#220)")]
    [SerializeField] private float m_attackConeAngle = 120f;
    [Tooltip("저항 중 표적을 바라보도록 도는 회전 속도(도/초) — 부채꼴 기준 방향을 표적에 맞춘다 (#220)")]
    [SerializeField] private float m_attackTurnSpeed = 540f;

    // 서버 권위 제압 게이지 — 저항(Attack) 상태에서만 의미. 진행도 UI(후속)를 위해 동기화한다 (#76)
    private readonly NetworkVariable<float> m_syncedSubdueGauge = new NetworkVariable<float>(0f);
    private float m_subdueGauge; // 서버·오프라인의 진실값 — m_networkState와 같은 이중 구조

    public float FleeSpeedMultiplier => m_fleeSpeedMultiplier;
    public float FleeStepDistance => m_fleeStepDistance;
    public float FleeEscapeDistance => m_fleeEscapeDistance;
    public float FleeClearanceRadius => m_fleeClearanceRadius;
    public float FleeResistCooldown => m_fleeResistCooldown;
    public float SubdueGaugeMax => m_subdueGaugeMax;
    public float StunSeconds => m_stunSeconds;
    public float ResistAttackInterval => m_resistAttackInterval;
    public float ResistAttackRange => m_resistAttackRange;
    public int ResistAttackDamage => m_resistAttackDamage;
    public float ResistDefeatSeconds => m_resistDefeatSeconds;
    public float StrikeOffsetSeconds => m_strikeOffsetSeconds;

    /// <summary>스윙 변형 개수 — 오프셋 배열 길이(=블렌드 트리 클립 수). 비어 있으면 단일 변형(0)으로 폴백. (#220)</summary>
    public int SwingVariantCount =>
        m_swingImpactOffsets != null && m_swingImpactOffsets.Length > 0 ? m_swingImpactOffsets.Length : 1;

    /// <summary>이번 스윙에 쓸 변형 index를 서버에서 뽑는다 — 데미지 타이밍(클립별 오프셋)과 시각(클라 동기화)이 같은 값을 공유한다. (#220)</summary>
    public int NextSwingVariant() => UnityEngine.Random.Range(0, SwingVariantCount);

    /// <summary>변형 index에 해당하는 타격 오프셋(초). 범위 밖이면 고정 폴백값. (#220)</summary>
    public float SwingImpactOffset(int variant) =>
        m_swingImpactOffsets != null && variant >= 0 && variant < m_swingImpactOffsets.Length
            ? m_swingImpactOffsets[variant]
            : m_strikeOffsetSeconds;

    public float AttackConeAngle => m_attackConeAngle;
    public float AttackTurnSpeed => m_attackTurnSpeed;

    /// <summary>
    /// 위협(플레이어)을 찾는 반경(m) — 저항 패배 후 도주 대상 탐색(#205)과 도주 방향 산출(#213)이 같은 값을 쓴다.
    /// 두 경로가 다른 반경을 쓰면 "도망칠 상대"와 "피할 상대"의 기준이 어긋난다.
    /// </summary>
    public float ThreatSearchRadius => m_resistAttackRange * k_threatSearchRadiusMultiplier;

    /// <summary>현재 제압 게이지. 네트워크 세션 중에는 동기화된 값이라 클라이언트에서도 읽을 수 있다. (#76)</summary>
    public float SubdueGauge => IsSpawned ? m_syncedSubdueGauge.Value : m_subdueGauge;

    /// <summary>저항·도주 중 피해 다니는 위협 대상(체포를 시도한 플레이어). 배회 등 반응 중이 아니면 null. 서버에서만 유효. (#76)</summary>
    public Transform ThreatTarget { get; private set; }

    /// <summary>공격 스윙 1회를 휘두를 때 발행 — 전 피어에서 발생한다(서버는 로컬 발행 + ClientRpc 중계).
    /// 인자는 재생할 스윙 변형 index — 서버가 뽑아 전 피어가 같은 클립을 재생하므로, HP 감소 순간(서버가
    /// 그 클립의 타격 오프셋으로 판정)과 화면 속 주먹이 닿는 순간이 일치한다.
    /// 애니메이션 표현(<see cref="NpcAnimationDriver"/>)이 구독해 단발 스윙 모션을 트리거한다.
    /// FSM 상태와 독립한 순간 이벤트라 State 동기화와 별개로 스윙 타이밍을 정확히 맞춘다. (#220, ThugAttacker.OnAttack과 동일 패턴)</summary>
    public event Action<int> OnAttackSwing;

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

    // 게이지는 서버 진실값과 동기화 변수에 함께 기록한다 — 오프라인에서는 NetworkVariable에 쓰지 않는다 (#56 상태 패턴과 동일)
    private void SetSubdueGauge(float value)
    {
        m_subdueGauge = value;
        if (IsSpawned && IsServer)
            m_syncedSubdueGauge.Value = value;
    }
}
