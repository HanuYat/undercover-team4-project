using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>폭탄 진행 상태 — 동기화는 int로(NetworkVariable&lt;enum&gt; 회피).</summary>
public enum BombState
{
    Idle,     // 아직 무장 전 (스폰 직후)
    Armed,    // 카운트다운 중 — 해체 시도 가능
    Defused,  // 해체 성공
    Exploded, // 시간 초과 폭발
}

/// <summary>
/// 시한폭탄 장치 — 스폰되는 폭탄 프리팹의 브레인. (GDD 6-4, #232)
///
/// <b>서버 권위 · 스폰형 이벤트 액터.</b> 퍼즐 시드 하나만 동기화하고(<see cref="BombPuzzle"/>),
/// 모든 피어가 같은 빌더로 동일 퍼즐을 재구성한다 — 본부 매뉴얼(<see cref="BombManual"/>)이 읽는 규칙과
/// 서버가 검증하는 정답이 어긋날 수 없다. 선 클릭은 <see cref="BombWire"/>가 이 장치로 라우팅한다.
///
/// 상태(무장/절단/해제/폭발)와 카운트다운은 NetworkVariable로 전 클라에 전파하고, 실제 표현
/// (선 색·폭발 넉백·VFX)은 이 장치의 이벤트를 구독하는 뷰 계층이 담당한다 (#56 패턴, DeviceBlackoutView와 동일).
///
/// 스폰·수명·정리는 <see cref="BombDefusalEvent"/>가 쥐고, 이 장치는 "폭탄 자체"의 행동만 맡는다
/// (JailbreakEvent ↔ NpcController 관계와 동일).
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class BombDevice : NetworkBehaviour
{
    [Header("퍼즐 규칙 (라운드마다 시드로 재배치)")]
    [SerializeField]
    private BombPuzzleConfig m_puzzleConfig = new BombPuzzleConfig();

    [Header("타이머 (인스펙터 조절)")]
    [Tooltip("무장부터 폭발까지의 제한시간(초) — '현장 다녀올 수 있는 시간'과 직결")]
    [SerializeField]
    private float m_countdownSeconds = 90f;

    [Tooltip("정답이 아닌 선을 자를 때마다 깎이는 남은 시간(초)")]
    [SerializeField]
    private float m_wrongCutPenaltySeconds = 20f;

    [Header("폭발 (인스펙터 조절)")]
    [Tooltip("이 반경(m) 안의 플레이어가 피해·넉백을 받는다")]
    [SerializeField]
    private float m_explosionRadius = 8f;

    [Tooltip("반경 내 플레이어 1인당 폭발 피해량")]
    [SerializeField]
    private int m_explosionDamage = 60;

    [Tooltip("넉백 세기(m/s) — 폭심에서 밀려나는 초기 속도")]
    [SerializeField]
    private float m_knockbackForce = 12f;

    [Tooltip("수평 세기 대비 위로 띄우는 비율 — 0이면 순수 수평으로만 밀린다")]
    [Range(0f, 1f)]
    [SerializeField]
    private float m_knockbackUpwardRatio = 0.45f;

    [Tooltip("반경 끝에서 남는 세기 비율 — 폭심(1.0)에서 반경 끝까지 선형 감쇠한다")]
    [Range(0f, 1f)]
    [SerializeField]
    private float m_knockbackEdgeFalloff = 0.25f;

    [Header("테스트")]
    [Tooltip("켜면 스폰/시작 시 스스로 무장한다 — 돌발 이벤트 없이 폭탄을 씬에 놓고 바로 해체·매뉴얼을 테스트할 때. " +
             "실전 배선(BombDefusalEvent) 전까지의 임시 스위치 (SignalDecoder.m_installedOnStart 관례)")]
    [SerializeField]
    private bool m_armOnStart;

    // ---- 동기화 상태 (서버 쓰기 / 전원 읽기) ----
    private readonly NetworkVariable<int> m_seedSynced = new NetworkVariable<int>();
    private readonly NetworkVariable<int> m_cutMaskSynced = new NetworkVariable<int>();
    private readonly NetworkVariable<int> m_stateSynced = new NetworkVariable<int>((int)BombState.Idle);
    // NGO 서버시간 기준 폭발 시각 — 클라 카운트다운 UI가 남은 시간을 계산한다(#232 타이머 표시 후속)
    private readonly NetworkVariable<double> m_explodeTimeSynced = new NetworkVariable<double>();

    // ---- 서버·오프라인 진실값 ----
    private BombState m_state = BombState.Idle;
    private int m_cutMask;               // 비트 i = i번 선이 잘렸다
    private float m_explodeAtLocal;      // Time.time 기준 폭발 시각 (서버·오프라인)
    private BombPuzzle m_puzzle;         // 시드로 재구성한 퍼즐 (전 피어가 동일)
    private BombWire[] m_wires = Array.Empty<BombWire>();

    private readonly List<Transform> m_blastBuffer = new List<Transform>();

    // NPC 넉백 대상 수집용 공유 버퍼 — 서버(또는 오프라인)에서만 쓰므로 정적으로 공유해도 안전하다
    private static readonly Collider[] s_blastColliders = new Collider[64];

    // 라운드당 폭탄 1개 — 본부 매뉴얼(BombManual)이 "현재 폭탄"의 규칙표를 읽기 위한 단일 참조.
    // 매니저가 아니라 스폰물이므로 App 파사드가 아닌 이 정적 참조로 노출한다(단일 인스턴스 보장은 이벤트가 한다).
    private static BombDevice s_active;

    /// <summary>현재 씬에 살아 있는 폭탄 — 없으면 null. 본부 매뉴얼이 규칙을 읽는 진입점.</summary>
    public static BombDevice Active => s_active;

    /// <summary>선 개수 — 프리팹 자식 BombWire 수에서 온다. 퍼즐 빌더의 기준.</summary>
    public int WireCount => m_wires.Length;

    /// <summary>현재 상태 — 서버·오프라인은 실참조, 원격 피어는 동기화값.</summary>
    public BombState State => IsSpawned && !IsServer ? (BombState)m_stateSynced.Value : m_state;

    public bool IsArmed => State == BombState.Armed;

    /// <summary>재구성된 퍼즐 — 선(<see cref="BombWire"/>)·매뉴얼이 색·규칙을 여기서 읽는다. 무장 전엔 null.</summary>
    public BombPuzzle Puzzle => m_puzzle;

    public float ExplosionRadius => m_explosionRadius;
    public float KnockbackForce => m_knockbackForce;

    /// <summary>
    /// 폭심에서 <paramref name="targetPosition"/>이 받는 넉백 속도(m/s) — 반경 밖이면 <see cref="Vector3.zero"/>.
    ///
    /// <b>넉백 세기의 단일 지점.</b> 플레이어는 각 피어가 자기 오너 캐릭터에 적용하고
    /// (<see cref="BombExplosionView"/>), NPC는 서버가 직접 민다(<see cref="ServerExplode"/>) —
    /// 두 경로가 각자 감쇠식을 들면 같은 폭발인데 사람과 시민이 다르게 날아간다.
    /// </summary>
    public Vector3 EvaluateKnockback(Vector3 targetPosition)
    {
        if (m_explosionRadius <= 0f || m_knockbackForce <= 0f)
            return Vector3.zero;

        Vector3 delta = targetPosition - transform.position;
        delta.y = 0f; // 밀리는 방향은 수평 — 띄우는 성분은 아래에서 따로 더한다
        float distance = delta.magnitude;
        if (distance > m_explosionRadius)
            return Vector3.zero;

        // 폭탄을 정확히 밟고 선 경우(거리 0)엔 방향이 없다 — 폭탄 정면으로 밀어 예외 없이 날아가게 한다
        Vector3 direction = distance > 0.01f ? delta / distance : transform.forward;
        float scaled = m_knockbackForce * Mathf.Lerp(1f, m_knockbackEdgeFalloff, distance / m_explosionRadius);

        return direction * scaled + Vector3.up * (scaled * m_knockbackUpwardRatio);
    }

    /// <summary>남은 시간(초) — 카운트다운 UI용. Armed가 아니면 0.</summary>
    public float RemainingSeconds
    {
        get
        {
            if (State != BombState.Armed)
                return 0f;
            if (IsSpawned && !IsServer)
            {
                double now = NetworkManager.Singleton != null ? NetworkManager.Singleton.ServerTime.Time : 0d;
                return Mathf.Max(0f, (float)(m_explodeTimeSynced.Value - now));
            }
            return Mathf.Max(0f, m_explodeAtLocal - Time.time);
        }
    }

    /// <summary>퍼즐이 준비됨(시드 확정) — 선이 색을 입히고 매뉴얼이 규칙을 읽는다.</summary>
    public event Action OnPuzzleReady;

    /// <summary>선 절단 상태가 바뀜 — 선 시각화가 구독.</summary>
    public event Action OnCutStateChanged;

    /// <summary>정답이 아닌 선을 잘랐다 — 스파크·경보 등 순간 피드백용(전 피어).</summary>
    public event Action OnWrongCut;

    /// <summary>해체(true) 또는 폭발(false)로 종료됨 — 전 피어. 폭발 넉백·VFX가 구독한다.</summary>
    public event Action<bool> OnResolved;

    // 서버·오프라인에서만 권위. 스폰 전(오프라인 Play)이면 항상 권위.
    private bool IsAuthority => !IsSpawned || IsServer;

    private void Awake()
    {
        // 자식의 선을 모아 인덱스를 매긴다 — 퍼즐 색 배열·정답 인덱스가 이 순서를 기준으로 한다.
        m_wires = GetComponentsInChildren<BombWire>(true);
        for (int i = 0; i < m_wires.Length; i++)
            m_wires[i].Bind(this, i);

        s_active = this; // 라운드당 1개 전제 — 매뉴얼이 이 폭탄의 규칙을 읽는다
    }

    // NetworkBehaviour.OnDestroy를 가리지 않도록 override + base 호출 (csc.rsp가 CS0114를 에러로 승격)
    public override void OnDestroy()
    {
        if (s_active == this)
            s_active = null;
        base.OnDestroy();
    }

    public override void OnNetworkSpawn()
    {
        m_seedSynced.OnValueChanged += HandleSeedSyncedChanged;
        m_cutMaskSynced.OnValueChanged += HandleCutMaskSyncedChanged;
        m_stateSynced.OnValueChanged += HandleStateSyncedChanged;

        // 늦게 접속한 클라: 이미 진행 중인 폭탄의 현재 상태를 즉시 반영한다.
        if (!IsServer && (BombState)m_stateSynced.Value != BombState.Idle)
        {
            m_state = (BombState)m_stateSynced.Value;
            m_cutMask = m_cutMaskSynced.Value;
            RebuildPuzzle(m_seedSynced.Value);
            OnCutStateChanged?.Invoke();
        }

        // 테스트 자동 무장(네트워크 세션) — 서버에서만
        if (IsServer && m_armOnStart && m_state == BombState.Idle)
            ServerArm(UnityEngine.Random.Range(1, int.MaxValue));
    }

    private void Start()
    {
        // 테스트 자동 무장(오프라인) — 세션이 없으면 OnNetworkSpawn이 안 오므로 여기서 무장한다.
        if (m_armOnStart && !SuddenEventUtil.IsNetworkSessionActive && m_state == BombState.Idle)
            ServerArm(UnityEngine.Random.Range(1, int.MaxValue));
    }

    public override void OnNetworkDespawn()
    {
        m_seedSynced.OnValueChanged -= HandleSeedSyncedChanged;
        m_cutMaskSynced.OnValueChanged -= HandleCutMaskSyncedChanged;
        m_stateSynced.OnValueChanged -= HandleStateSyncedChanged;
    }

    private void Update()
    {
        // 카운트다운·폭발 판정은 서버 권위 (오프라인 폴백 포함). 클라는 표현만.
        if (!IsAuthority)
            return;
        if (m_state != BombState.Armed)
            return;

        if (Time.time >= m_explodeAtLocal)
            ServerExplode();
    }

    /// <summary>
    /// 폭탄을 무장한다 — <see cref="BombDefusalEvent"/>가 스폰 직후 서버(또는 오프라인)에서 호출.
    /// 시드로 퍼즐을 확정하고 카운트다운을 시작한다.
    /// </summary>
    public void ServerArm(int seed)
    {
        if (!IsAuthority)
            return;

        RebuildPuzzle(seed); // 서버 로컬 퍼즐 (클라는 시드 동기화로 각자 재구성)
        m_cutMask = 0;
        m_explodeAtLocal = Time.time + m_countdownSeconds;

        if (IsSpawned && IsServer)
        {
            m_seedSynced.Value = seed;
            m_cutMaskSynced.Value = 0;
            double now = NetworkManager.Singleton != null ? NetworkManager.Singleton.ServerTime.Time : 0d;
            m_explodeTimeSynced.Value = now + m_countdownSeconds;
        }

        SetState(BombState.Armed);
    }

    /// <summary>선 클릭 진입점 — <see cref="BombWire"/>가 호출. 클라면 서버로 요청을 넘긴다.</summary>
    public void RequestCut(int wireIndex, GameObject cutter)
    {
        if (!IsArmed)
            return;

        if (IsAuthority)
        {
            ServerHandleCut(wireIndex);
            return;
        }

        RequestCutRpc(wireIndex);
    }

    // 씬에 놓인 서버 소유 오브젝트라 어떤 플레이어도 오너가 아니다 — Everyone 권한 (SignalDecoder와 동일).
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void RequestCutRpc(int wireIndex)
    {
        ServerHandleCut(wireIndex);
    }

    // 서버 검증 — 클라 요청은 신뢰하지 않고 상태·인덱스·중복을 다시 확인한다.
    private void ServerHandleCut(int wireIndex)
    {
        if (m_state != BombState.Armed)
            return;
        if (wireIndex < 0 || wireIndex >= m_wires.Length)
            return;
        if (IsWireCut(wireIndex))
            return; // 이미 잘린 선

        SetCutMask(m_cutMask | (1 << wireIndex));

        // 단일 모듈 — 정답 선을 자르면 즉시 해체, 그 외엔 시간 페널티. (#232 MVP)
        if (m_puzzle != null && wireIndex == m_puzzle.CorrectWireIndex)
        {
            Debug.Log($"[폭탄] 정답 선({wireIndex}) 절단 — 해체 성공");
            SetState(BombState.Defused);
        }
        else
        {
            ServerWrongCut(wireIndex);
        }
    }

    private void ServerWrongCut(int wireIndex)
    {
        m_explodeAtLocal -= m_wrongCutPenaltySeconds;
        if (IsSpawned && IsServer)
            m_explodeTimeSynced.Value -= m_wrongCutPenaltySeconds;

        Debug.Log($"[폭탄] 오답 선({wireIndex}) 절단 — 남은 시간 {m_wrongCutPenaltySeconds}s 감소");
        NotifyWrongCut();

        // 페널티로 시간이 다 소진됐으면 즉시 폭발 (다음 프레임 Update를 기다리지 않는다)
        if (Time.time >= m_explodeAtLocal)
            ServerExplode();
    }

    private void ServerExplode()
    {
        if (m_state == BombState.Exploded)
            return;

        // 반경 내 행동 가능한 플레이어에게 피해.
        // NPC는 이제 체력이 있지만(#366) 폭발 피해는 아직 연결하지 않았다 — 넉백 착지가 이미
        // Stunned로 보내고 있어 중복 정리가 필요하다(후속 이슈). 지금은 넉백만 받는다.
        SuddenEventUtil.CollectFieldPlayers(transform.position, m_explosionRadius, m_blastBuffer);
        for (int i = 0; i < m_blastBuffer.Count; i++)
        {
            IDamageable damageable = m_blastBuffer[i].GetComponent<IDamageable>();
            damageable?.TakeDamage(m_explosionDamage, gameObject);
        }

        // 반경 내 NPC 넉백 — 서버 권위. 플레이어와 달리 NPC 이동은 서버의 NavMeshAgent가 쥐고
        // 클라는 NetworkTransform으로 결과만 받으므로, 뷰가 아니라 여기서 직접 날린다.
        ServerKnockbackNpcs();

        Debug.Log($"[폭탄] 시간 초과 — 폭발 (반경 {m_explosionRadius}m, 피해 {m_explosionDamage})");
        SetState(BombState.Exploded);
    }

    // 반경 내 NPC를 폭심 반대쪽으로 날린다. NPC 하나가 콜라이더 여러 개로 잡혀도
    // ServerApplyKnockback이 비행 중 중복 호출을 무시하므로 별도 중복 제거가 필요 없다.
    private void ServerKnockbackNpcs()
    {
        int hitCount = Physics.OverlapSphereNonAlloc(transform.position, m_explosionRadius, s_blastColliders);
        for (int i = 0; i < hitCount; i++)
        {
            NpcController npc = s_blastColliders[i].GetComponentInParent<NpcController>();
            if (npc == null)
                continue;

            npc.ServerApplyKnockback(EvaluateKnockback(npc.transform.position));
        }
    }

    // ---- 상태·절단 전파 ----

    private void SetState(BombState state)
    {
        if (m_state == state)
            return;

        m_state = state;
        if (IsSpawned && IsServer)
            m_stateSynced.Value = (int)state;

        HandleStateEntered(state); // 서버·오프라인 로컬 발행 (원격은 동기화 콜백이 담당)
    }

    private void SetCutMask(int mask)
    {
        m_cutMask = mask;
        if (IsSpawned && IsServer)
            m_cutMaskSynced.Value = mask;
        OnCutStateChanged?.Invoke(); // 서버·오프라인 로컬 발행
    }

    // 종료 상태 진입 시 표현 이벤트 발행 — 해체/폭발 연출은 전 피어에서 일어난다.
    private void HandleStateEntered(BombState state)
    {
        if (state == BombState.Defused)
            OnResolved?.Invoke(true);
        else if (state == BombState.Exploded)
            OnResolved?.Invoke(false);
    }

    private void NotifyWrongCut()
    {
        OnWrongCut?.Invoke(); // 서버·오프라인 로컬 발행
        if (IsSpawned && IsServer)
            WrongCutClientRpc();
    }

    [ClientRpc]
    private void WrongCutClientRpc()
    {
        if (IsServer)
            return; // 호스트는 위에서 이미 발행
        OnWrongCut?.Invoke();
    }

    // 시드로 퍼즐을 재구성한다 — 전 피어가 동일 결과를 얻는다(단일 진실 소스).
    private void RebuildPuzzle(int seed)
    {
        m_puzzle = BombPuzzle.Build(m_puzzleConfig, m_wires.Length, seed);
        OnPuzzleReady?.Invoke();
    }

    /// <summary>i번 선이 잘렸는지 — 서버·오프라인은 실참조, 원격은 동기화 마스크.</summary>
    public bool IsWireCut(int wireIndex)
    {
        int mask = IsSpawned && !IsServer ? m_cutMaskSynced.Value : m_cutMask;
        return (mask & (1 << wireIndex)) != 0;
    }

    /// <summary>지금 이 선을 자를 수 있는지 — <see cref="BombWire.CanInteract"/>가 윤곽선 판정에 쓴다.</summary>
    public bool CanCutWire(int wireIndex) => IsArmed && !IsWireCut(wireIndex);

    // ---- 동기화 콜백 (원격 클라 전용) ----

    private void HandleSeedSyncedChanged(int previous, int current)
    {
        if (IsServer)
            return; // 호스트는 ServerArm에서 이미 재구성
        RebuildPuzzle(current);
    }

    private void HandleCutMaskSyncedChanged(int previous, int current)
    {
        if (IsServer)
            return;
        m_cutMask = current;
        OnCutStateChanged?.Invoke();
    }

    private void HandleStateSyncedChanged(int previous, int current)
    {
        if (IsServer)
            return;
        m_state = (BombState)current;
        HandleStateEntered((BombState)current);
    }
}
