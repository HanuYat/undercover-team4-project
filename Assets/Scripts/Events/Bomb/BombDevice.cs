using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

/// <summary>폭탄 진행 상태 — 동기화는 int로(NetworkVariable&lt;enum&gt; 회피).</summary>
public enum BombState
{
    Idle,     // 아직 무장 전 (스폰 직후)
    Emerging, // 상자에서 나오는 중 — 아직 움직이지도 카운트다운하지도 않는다 (등장 예고)
    Dormant,  // 상자 앞에서 대기 — 사람이 다가올 때까지 시간이 흐르지 않는다
    Armed,    // 카운트다운 중 — 가장 가까운 현장 플레이어를 쫓는다
    Locked,   // 폭발 직전 — 그 자리에 멈춰 더는 쫓지 않는다 (폭심 확정)
    Exploded, // 시간 초과 폭발
}

/// <summary>
/// 추격 폭탄 — 스폰되는 폭탄 프리팹의 브레인. (GDD 6-4, #399)
///
/// <b>서버 권위 · 스폰형 이벤트 액터.</b> 무장하면 NavMesh 위를 굴러 가장 가까운 현장 플레이어를 쫓고,
/// 제한시간이 끝나면 그 자리에서 폭발한다. <b>해체도 밀어내기도 없다</b> — 대응 수단은
/// <b>달아나기</b> 하나뿐이다(추격 속도가 달리기보다 느리다 — 걷기보다는 빠르므로 뛰어야 벌어진다).
/// 폭발은 반드시 일어나므로 "막는 게임"이 아니라 "폭심에서 벗어나는 게임"이다.
///
/// <b>진압봉으로 때리면 그 자리에서 즉발한다</b> (<see cref="ServerDetonate"/>, #399). 밀어내기를
/// 대신하는 수단이 아니라 <b>오조작의 대가</b>다 — 때린 사람은 폭심 바로 옆이라 피해를 온전히 받는다.
/// 무기를 들면 무엇이든 때려 보게 되는데, 폭탄은 때려서 될 물건이 아니라는 것을 값으로 가르친다.
///
/// 이동은 서버의 <see cref="NavMeshAgent"/>가 쥐고 클라이언트는 NetworkTransform으로 결과만 받는다
/// (NPC와 같은 스택). 상태·폭발 시각은 NetworkVariable로 전 피어에 전파하고, 실제 표현(카운트다운 표시·
/// 폭발 넉백·VFX)은 이 장치의 이벤트를 구독하는 뷰 계층이 담당한다 (#56 패턴, DeviceBlackoutView와 동일).
///
/// 스폰·수명·정리는 <see cref="BombChaseEvent"/>가 쥐고, 이 장치는 "폭탄 자체"의 행동만 맡는다
/// (JailbreakEvent ↔ NpcController 관계와 동일).
/// </summary>
[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(NavMeshAgent))]
public class BombDevice : NetworkBehaviour
{
    [Header("등장 (인스펙터 조절)")]
    [Tooltip("상자에서 나오는 데 걸리는 시간(초) — 이 동안은 움직이지도 카운트다운하지도 않는다. " +
             "연출(BombEmergeView)이 아니라 이 값이 진짜 무장 시각을 정한다")]
    [SerializeField]
    private float m_emergeSeconds = 2f;

    [Header("타이머 (인스펙터 조절)")]
    [Tooltip("무장부터 폭발까지의 제한시간(초) — 쫓기는 시간과 직결")]
    [SerializeField]
    private float m_countdownSeconds = 30f;

    [Tooltip("남은 시간이 이 값(초) 이하가 되면 그 자리에 멈춘다 — 폭심이 확정돼 마지막으로 벗어날 기회가 된다")]
    [SerializeField]
    private float m_lockSeconds = 3f;

    [Header("추격 (인스펙터 조절)")]
    [Tooltip("추격 속도(m/s) — 플레이어 걷기 5·달리기 8 기준. 걷기보다 빠르게 둘 것: 걸어서 벌 수 있으면 " +
             "달아나는 데 아무 대가가 없어 추격이 성립하지 않는다. 달리기보다는 확실히 느려야 한다")]
    [SerializeField]
    private float m_chaseSpeed = 5.5f;

    [Tooltip("이 반경(m) 안에 현장 인원이 들어오면 잠에서 깨어 추격과 카운트다운을 함께 시작한다")]
    [SerializeField]
    private float m_wakeRadius = 14f;

    [Tooltip("표적을 다시 고르고 목적지를 갱신하는 주기(초)")]
    [SerializeField]
    private float m_retargetInterval = 0.5f;

    [Tooltip("현재 표적보다 이만큼(m) 더 가까워야 표적을 바꾼다 — 두 사람 사이에서 갈팡질팡하지 않게")]
    [SerializeField]
    private float m_retargetHysteresis = 1.5f;

    [Tooltip("이 반경(m) 안의 현장 플레이어만 표적이 된다 — 맵 전역을 덮을 만큼 크게 둘 것")]
    [SerializeField]
    private float m_targetSearchRadius = 300f;

    [Header("폭발 (인스펙터 조절)")]
    [Tooltip("이 반경(m) 안의 플레이어가 피해·넉백을 받는다")]
    [SerializeField]
    private float m_explosionRadius = 8f;

    [Tooltip("폭심에서의 피해량 — 반경 끝까지 m_damageEdgeFalloff 비율로 선형 감쇠한다")]
    [SerializeField]
    private int m_explosionDamage = 150;

    [Tooltip("반경 끝에서 남는 피해 비율 — 폭심(1.0)에서 반경 끝까지 선형 감쇠. " +
             "즉사 반경 = 반경 × (1 − 최대HP/피해) / (1 − 이 값). 기본값(150·0.2·8m)이면 약 3.3m")]
    [Range(0f, 1f)]
    [SerializeField]
    private float m_damageEdgeFalloff = 0.2f;

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

    [Tooltip("사망자 래그돌의 수평 세기 = 넉백 수평 세기 × 이 값. <b>연출 노브다</b> — 크게 잡으면 " +
             "시원하게 날아간다. 예전 주석이 말하던 '캡슐이 못 따라온다'는 제약은 이미 사라졌다 " +
             "(EvaluateRagdollImpulse 주석)")]
    [Range(0f, 1f)]
    [SerializeField]
    private float m_ragdollImpulseScale = 0.22f;

    [Tooltip("사망자 래그돌의 상승 세기 = 위 수평 세기 × 이 값. <b>곧 발사각이다</b> — 1.0이 45°로 " +
             "사거리 최대이고, 크면 높이 뜨는 대신 가까이 떨어진다. 예전 값 1.8은 61°라 속도를 " +
             "높이에 낭비했다. 정점(m) ≈ (수평세기 × 이 값)² / 19.6")]
    [Range(0f, 3f)]
    [SerializeField]
    private float m_ragdollLiftRatio = 1.2f;


    [Header("테스트")]
    [Tooltip("켜면 스폰/시작 시 스스로 무장한다 — 돌발 이벤트 없이 폭탄을 씬에 놓고 바로 등장·추격·폭발을 테스트할 때. " +
             "실전 배선(BombChaseEvent) 전까지의 임시 스위치 (SignalDecoder.m_installedOnStart 관례)")]
    [SerializeField]
    private bool m_armOnStart;

    // ---- 동기화 상태 (서버 쓰기 / 전원 읽기) ----
    private readonly NetworkVariable<int> m_stateSynced = new NetworkVariable<int>((int)BombState.Idle);
    // NGO 서버시간 기준 폭발 시각 — 클라 카운트다운 UI가 남은 시간을 계산한다
    private readonly NetworkVariable<double> m_explodeTimeSynced = new NetworkVariable<double>();

    // ---- 서버·오프라인 진실값 ----
    private BombState m_state = BombState.Idle;
    private float m_armAtLocal;          // Time.time 기준 무장(등장 완료) 시각 (서버·오프라인)
    private float m_explodeAtLocal;      // Time.time 기준 폭발 시각 (서버·오프라인)
    private NavMeshAgent m_agent;

    private PlayerHealth m_target;       // 지금 쫓는 상대 — 히스테리시스로만 바뀐다
    private float m_nextRetargetTime;

    private readonly List<Transform> m_blastBuffer = new List<Transform>();

    // 이 폭발로 죽은 플레이어 — 래그돌 임펄스 대상(#506). 서버·오프라인에서만 채운다.
    private readonly List<NetworkObject> m_deathBuffer = new List<NetworkObject>();

    // 폭발 대상 수집용 공유 버퍼 — 서버(또는 오프라인)에서만 쓰므로 정적으로 공유해도 안전하다.
    // 사람 하나가 래그돌 뼈 콜라이더 여러 개로 잡혀 64칸은 대여섯 명이면 포화된다 (#768).
    private static readonly Collider[] s_blastColliders = new Collider[256];

    // 한 폭발에서 이미 처리한 NPC — 위 버퍼가 같은 사람을 여러 번 담기 때문이다 (#768).
    private static readonly HashSet<NpcController> s_blastNpcs = new HashSet<NpcController>();

    // 라운드당 폭탄 1개 — 씬에 놓인 상자(BombCrate)가 "내 상자에서 나오는 폭탄인가"를 묻는 단일 참조.
    // 매니저가 아니라 스폰물이므로 App 파사드가 아닌 이 정적 참조로 노출한다(단일 인스턴스 보장은 이벤트가 한다).
    private static BombDevice s_active;

    /// <summary>현재 씬에 살아 있는 폭탄 — 없으면 null. 상자가 여는 시점을 판단하는 진입점.</summary>
    public static BombDevice Active => s_active;

    /// <summary>현재 상태 — 서버·오프라인은 실참조, 원격 피어는 동기화값.</summary>
    public BombState State => IsSpawned && !IsServer ? (BombState)m_stateSynced.Value : m_state;

    /// <summary>카운트다운이 도는 중인가 — 추격 중(Armed)과 폭심 확정 후(Locked)를 함께 묶는다.</summary>
    public bool IsCountingDown => State == BombState.Armed || State == BombState.Locked;

    /// <summary>지금 때리면 터지는 상태인가 — <see cref="Baton"/>이 타격 판정에 쓴다.
    /// 카운트다운 전(등장·대기)과 이미 터진 뒤는 그냥 소품이라 빗나감으로 둔다.</summary>
    public bool CanBeStruck => IsCountingDown;

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

    /// <summary>
    /// 폭심에서 <paramref name="targetPosition"/>이 받는 피해량 — 반경 밖이면 0.
    ///
    /// <b>피해 세기의 단일 지점</b> — <see cref="EvaluateKnockback"/>과 같은 모양으로 둔다.
    ///
    /// 감쇠가 있어야 사망 래그돌이 성립한다(#506 §4). 균일 피해로는 값을 어떻게 잡아도 "반경 안
    /// 전원 생존" 아니면 "전원 즉사"뿐이라, 폭심은 날아가고 가장자리는 밀리는 그림이 나오지 않는다.
    ///
    /// <b>거리는 3차원으로 잰다</b> — 넉백이 y를 지우는 것은 밀리는 <b>방향</b>이 수평이어야 하기
    /// 때문이고, 피해에는 방향이 없다. 대상 수집(<see cref="SuddenEventUtil.CollectFieldPlayers"/>)도
    /// 3차원 거리를 쓰므로 여기서 수평 거리를 쓰면 수집은 됐는데 피해가 0인 대상이 생긴다.
    /// </summary>
    public int EvaluateDamage(Vector3 targetPosition)
    {
        if (m_explosionRadius <= 0f || m_explosionDamage <= 0)
            return 0;

        float distance = (targetPosition - transform.position).magnitude;
        if (distance > m_explosionRadius)
            return 0;

        float scaled = m_explosionDamage
            * Mathf.Lerp(1f, m_damageEdgeFalloff, distance / m_explosionRadius);
        return Mathf.RoundToInt(scaled);
    }

    /// <summary>남은 시간(초) — 카운트다운 UI용. 카운트다운 중이 아니면 0.</summary>
    public float RemainingSeconds
    {
        get
        {
            if (!IsCountingDown)
                return 0f;
            if (IsSpawned && !IsServer)
            {
                double now = NetworkManager.Singleton != null ? NetworkManager.Singleton.ServerTime.Time : 0d;
                return Mathf.Max(0f, (float)(m_explodeTimeSynced.Value - now));
            }
            return Mathf.Max(0f, m_explodeAtLocal - Time.time);
        }
    }

    /// <summary>폭발했다 — 전 피어. 폭발 넉백·VFX가 구독한다.</summary>
    public event Action OnExploded;

    // 서버·오프라인에서만 권위. 스폰 전(오프라인 Play)이면 항상 권위.
    private bool IsAuthority => !IsSpawned || IsServer;

    private void Awake()
    {
        m_agent = GetComponent<NavMeshAgent>();
        m_agent.speed = m_chaseSpeed;

        s_active = this; // 라운드당 1개 전제 — 상자가 이 폭탄을 보고 열린다
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
        m_stateSynced.OnValueChanged += HandleStateSyncedChanged;

        // 이동 권한은 서버 하나뿐이다 — 원격 피어의 에이전트가 함께 돌면 NetworkTransform이 보내오는
        // 위치와 싸워 폭탄이 덜덜 떤다. 권위는 스폰 뒤에야 확정되므로 Awake가 아니라 여기서 끈다.
        if (!IsServer)
        {
            m_agent.enabled = false;
            m_state = (BombState)m_stateSynced.Value; // 늦게 접속한 클라: 진행 중인 폭탄의 현재 상태 반영
            return;
        }

        // 테스트 자동 무장(네트워크 세션) — 서버에서만
        if (m_armOnStart && m_state == BombState.Idle)
            ServerDeploy();
    }

    private void Start()
    {
        // 테스트 자동 무장(오프라인) — 세션이 없으면 OnNetworkSpawn이 안 오므로 여기서 무장한다.
        if (m_armOnStart && !SuddenEventUtil.IsNetworkSessionActive && m_state == BombState.Idle)
            ServerDeploy();
    }

    public override void OnNetworkDespawn()
    {
        m_stateSynced.OnValueChanged -= HandleStateSyncedChanged;
    }

    private void Update()
    {
        // 추격·카운트다운·폭발 판정은 서버 권위 (오프라인 폴백 포함). 클라는 표현만.
        if (!IsAuthority)
            return;

        // 등장 중 — 상자에서 나오는 동안은 가만히 있는다. 다 나오면 대기 상태로 넘어간다.
        if (m_state == BombState.Emerging)
        {
            if (Time.time >= m_armAtLocal)
                SetState(BombState.Dormant);
            return;
        }

        // 대기 — 상자 앞에 서서 사람을 기다린다. 카운트다운은 아직 돌지 않는다.
        //
        // 시간을 여기서 흘리지 않는 이유: 아무도 없는 골목에서 30초가 지나면 폭탄은 누구도 위협하지
        // 못한 채 혼자 터진다. "언제 터지는가"가 아니라 "누가 걸리는가"가 이 이벤트의 내용이므로,
        // 시계는 표적이 생기는 순간부터 돈다.
        if (m_state == BombState.Dormant)
        {
            if (Time.time < m_nextRetargetTime)
                return; // 매 프레임 전수 검색하지 않는다 — 추격 재타겟과 같은 주기로 본다

            m_nextRetargetTime = Time.time + m_retargetInterval;
            if (SuddenEventUtil.FindNearestFieldPlayer(transform.position, m_wakeRadius) != null)
                ServerArm();
            return;
        }

        if (m_state != BombState.Armed && m_state != BombState.Locked)
            return;

        // 폭심 확정 — 남은 시간이 얼마 없으면 멈춰서 "여기서 터진다"를 보여준다.
        if (m_state == BombState.Armed && m_explodeAtLocal - Time.time <= m_lockSeconds)
        {
            StopAgent();
            SetState(BombState.Locked);
        }
        else if (m_state == BombState.Armed)
        {
            TickChase();
        }

        if (Time.time >= m_explodeAtLocal)
            ServerExplode();
    }

    /// <summary>
    /// 폭탄을 배치한다 — <see cref="BombChaseEvent"/>가 스폰 직후 서버(또는 오프라인)에서 호출.
    /// 상자에서 나오는 동안(<see cref="BombState.Emerging"/>)은 가만히 있다가 대기(<see cref="BombState.Dormant"/>)로
    /// 넘어가고, 사람이 다가오면 그때 <see cref="ServerArm"/>이 걸린다.
    ///
    /// 등장 시간을 두는 이유는 연출 때문만이 아니다 — <b>예고</b>다. 상자가 들썩이는 동안 근처 인원이
    /// 달아날 채비를 할 수 있어야, 30초 카운트다운이 "도망칠 수 있었는데 못 갔다"가 된다.
    /// </summary>
    public void ServerDeploy()
    {
        if (!IsAuthority)
            return;

        if (m_emergeSeconds <= 0f)
        {
            ServerArm();
            return;
        }

        m_armAtLocal = Time.time + m_emergeSeconds;
        SetState(BombState.Emerging);
    }

    /// <summary>
    /// 폭탄을 무장한다 — 대기 중 현장 인원이 <see cref="m_wakeRadius"/> 안에 들어오면 스스로 호출한다.
    /// <b>카운트다운은 여기서 시작한다</b> — 등장 시점이 아니라 표적이 생긴 시점이 기준이다.
    /// </summary>
    public void ServerArm()
    {
        if (!IsAuthority)
            return;

        m_explodeAtLocal = Time.time + m_countdownSeconds;
        m_nextRetargetTime = 0f; // 다음 Update에서 곧바로 표적을 고른다

        if (IsSpawned && IsServer)
        {
            double now = NetworkManager.Singleton != null ? NetworkManager.Singleton.ServerTime.Time : 0d;
            m_explodeTimeSynced.Value = now + m_countdownSeconds;
        }

        SetState(BombState.Armed);
    }

    /// <summary>
    /// 진압봉 타격 진입점 — 서버(또는 오프라인)에서만 호출한다. <b>그 자리에서 즉발한다.</b> (#399)
    ///
    /// 남은 시간을 앞당기는 것이 아니라 폭발 자체를 지금 일으킨다 — 때린 순간과 터지는 순간 사이에
    /// 틈이 있으면 "때렸더니 잠시 뒤에 터졌다"가 되어 원인이 흐려진다.
    ///
    /// 카운트다운 중(<see cref="CanBeStruck"/>)에만 받는다. 등장·대기 중인 폭탄까지 때려서 터뜨릴 수
    /// 있으면 <b>아무도 쫓기지 않은 채 이벤트가 끝나</b>, 도망치는 30초라는 이벤트의 알맹이가 사라진다.
    /// </summary>
    public void ServerDetonate()
    {
        if (!IsAuthority)
            return;
        if (!IsCountingDown)
            return;

        Debug.Log("[폭탄] 진압봉에 맞음 — 즉발");
        ServerExplode();
    }

    // 표적 재선정 + 목적지 갱신. 표적도 움직이므로 같은 주기로 목적지를 다시 찍는다.
    private void TickChase()
    {
        if (Time.time < m_nextRetargetTime)
            return;

        m_nextRetargetTime = Time.time + m_retargetInterval;

        PlayerHealth nearest = SuddenEventUtil.FindNearestFieldPlayer(transform.position, m_targetSearchRadius);
        if (nearest == null)
        {
            // 쫓을 사람이 없다(전원 다운) — 그 자리에서 남은 시간을 마저 센다. 이벤트는 폭발로 끝난다.
            m_target = null;
            StopAgent();
            return;
        }

        if (m_target == null || !m_target.IsTargetable)
        {
            m_target = nearest;
        }
        else if (nearest != m_target)
        {
            // 히스테리시스 — 근소한 차이로 표적이 바뀌면 두 사람 사이에서 방향만 바꾸다 제자리에 선다
            float current = Vector3.Distance(transform.position, m_target.transform.position);
            float candidate = Vector3.Distance(transform.position, nearest.transform.position);
            if (candidate + m_retargetHysteresis < current)
                m_target = nearest;
        }

        if (!m_agent.enabled || !m_agent.isOnNavMesh)
            return;

        m_agent.isStopped = false;
        m_agent.SetDestination(m_target.transform.position);
    }

    private void StopAgent()
    {
        if (!m_agent.enabled || !m_agent.isOnNavMesh)
            return;

        m_agent.ResetPath();
        m_agent.isStopped = true;
    }

    private void ServerExplode()
    {
        if (m_state == BombState.Exploded)
            return;

        StopAgent();

        // 반경 내 행동 가능한 플레이어에게 거리 감쇠 피해.
        // NPC는 이제 체력이 있지만(#366) 폭발 피해는 아직 연결하지 않았다 — 넉백 착지가 이미
        // Stunned로 보내고 있어 중복 정리가 필요하다(후속 이슈). 지금은 넉백만 받는다.
        m_deathBuffer.Clear();
        SuddenEventUtil.CollectFieldPlayers(transform.position, m_explosionRadius, m_blastBuffer);
        for (int i = 0; i < m_blastBuffer.Count; i++)
        {
            Transform target = m_blastBuffer[i];
            IDamageable damageable = target.GetComponent<IDamageable>();
            damageable?.TakeDamage(EvaluateDamage(target.position), gameObject);

            // CollectFieldPlayers는 행동 가능한(HP>0) 플레이어만 담으므로, 지금 0이면 이 폭발로 죽은 것이다.
            if (target.TryGetComponent(out PlayerHealth health)
                && health.CurrentHp == 0
                && target.TryGetComponent(out NetworkObject victim))
                m_deathBuffer.Add(victim);

            // 휘말린 사람은 밧줄에서 손을 뗀다 (#559). 맞은 쪽이 NPC일 때 연행이 풀리는 것과 같은
            // 규칙을 사람 쪽에도 맞춘 것이다(ServerApplyKnockback — 수갑은 남고 연행만 풀린다).
            // 빠져 있던 것은 <b>사람만 반경에 든 경우</b>다: NPC는 밧줄 길이만큼 떨어져 끌려오므로
            // 폭심이 둘 사이를 가르면 사람만 날아가고, 그러면 끌기가 날아가는 몸을 따라간다.
            //
            // 서버에서 하는 이유는 넉백이 오너 로컬이라(PlayerMovement.AddKnockback) 서버가 볼 수
            // 없기 때문이다 — 반경을 아는 것은 이 자리뿐이다. 피해로 HP가 0이 된 경우는 무력화
            // 진입에서 이미 놓았고, 두 번 불려도 무해하다(끌고 있지 않으면 그대로 돌아간다).
            target.GetComponent<PlayerEscorter>()?.ReleaseAllDrags();
        }

        NotifyBlastDeaths();

        // 반경 내 NPC 피해·넉백 — 서버 권위. 플레이어와 달리 RPC가 없다: 시체 자세는
        // RagdollPoseStreamer가 서버에서만 굴려 원격에 흘린다(PoseAuthority.Server).
        ServerBlastNpcs();

        // 진압봉 즉발도 여기로 오므로 main의 '시간 초과' 문구는 쓰지 않는다 (#399 추격 폭탄).
        Debug.Log(
            $"[폭탄] 폭발 (반경 {m_explosionRadius}m, 폭심 피해 {m_explosionDamage},"
                + $" 가장자리 비율 {m_damageEdgeFalloff}, 사망 {m_deathBuffer.Count}명)"
        );
        SetState(BombState.Exploded);
    }

    // ---- 폭발 사망자 → 래그돌 임펄스 (#506) ----

    /// <summary>
    /// 이 폭발로 죽은 사람과 <b>그에게 줄 임펄스</b>를 전 피어에 알린다.
    ///
    /// <b>임펄스를 서버가 계산해 함께 보낸다.</b> 예전에는 사망자 목록만 보내고 임펄스는 각 피어가
    /// <see cref="EvaluateKnockback"/>으로 직접 계산했는데, 그게 <b>피어마다 다른 값을 냈다</b>:
    /// 감쇠식은 대상 위치까지의 거리를 쓰고, 원격 피어가 보는 <c>victim.transform.position</c>은
    /// NetworkTransform 보간값이라 오너가 보는 값과 다르다. 결과적으로 <b>세기와 방향이 갈린 채</b>
    /// 각 피어가 자기 래그돌을 날려서, 같은 물리를 돌려도 궤적이 처음부터 벌어졌다.
    ///
    /// 래그돌은 뼈를 동기화하지 않고 각 피어가 로컬로 시뮬레이션하므로(#506 결정 4),
    /// <b>입력이 같아야 결과가 대체로 같다</b> — 그 입력 중 하나가 이 임펄스다. (§10-3)
    ///
    /// "넉백 식은 장치 한 곳"은 그대로다 — 식은 여전히 <see cref="EvaluateRagdollImpulse"/> 하나이고,
    /// 그것을 <b>서버에서만</b> 부른다는 점이 달라졌다.
    ///
    /// <b>왜 RPC가 필요한가.</b> 사망 자체는 <see cref="PlayerRagdoll"/>이 동기화값 폴링으로 잡아
    /// 래그돌에 들어간다 — 그것만으로 진압봉·린치 사망은 전부 처리된다. 폭발이 다른 점은
    /// <b>임펄스</b> 하나뿐인데, "누가 이 폭발로 죽었나"는 피해 계산을 한 서버만 안다.
    /// 각 피어가 스스로 판정하려 들면 반경 경계에 선 사람에서 갈린다.
    ///
    /// 호스트 중복 발행은 <see cref="WrongCutClientRpc"/>와 같은 관례로 막는다 — 서버는 로컬에서
    /// 직접 발행하고, ClientRpc 쪽이 <c>IsServer</c>면 물러난다.
    /// </summary>
    private void NotifyBlastDeaths()
    {
        if (m_deathBuffer.Count == 0)
            return;

        // 서버(또는 오프라인)가 임펄스를 확정한다 — 이 값이 전 피어의 공통 입력이 된다.
        Vector3[] impulses = new Vector3[m_deathBuffer.Count];
        for (int i = 0; i < impulses.Length; i++)
            impulses[i] = EvaluateRagdollImpulse(m_deathBuffer[i].transform.position);

        for (int i = 0; i < m_deathBuffer.Count; i++)
            ApplyBlastRagdoll(m_deathBuffer[i], impulses[i]); // 서버·오프라인 로컬 발행

        if (!IsSpawned || !IsServer)
            return;

        NetworkObjectReference[] victims = new NetworkObjectReference[m_deathBuffer.Count];
        for (int i = 0; i < victims.Length; i++)
            victims[i] = m_deathBuffer[i];

        BlastDeathsClientRpc(victims, impulses);
    }

    [ClientRpc]
    private void BlastDeathsClientRpc(NetworkObjectReference[] victims, Vector3[] impulses)
    {
        if (IsServer)
            return; // 호스트는 위에서 이미 발행

        // 길이는 서버가 맞춰 보내지만, 직렬화 경계를 믿지 않고 짧은 쪽까지만 돈다.
        int count = Mathf.Min(victims.Length, impulses.Length);
        for (int i = 0; i < count; i++)
        {
            if (victims[i].TryGet(out NetworkObject victim))
                ApplyBlastRagdoll(victim, impulses[i]);
        }
    }

    // 래그돌 진입은 <b>멱등이다</b> — 사망 폴링이 먼저 걸려 이미 물리 중이면 임펄스만 누적되고,
    // 이쪽이 먼저 와도 사망 폴링이 뒤늦게 무동작이 된다. 사망 사실(PlayerIncapacitation의
    // NetworkVariable)과 이 RPC는 서로 다른 오브젝트에서 오므로 도착 순서를 맞출 수 없다 —
    // 순서와 무관하게 결과가 같게 만드는 쪽이 항상 옳다. (#506 §3-1)
    //
    private void ApplyBlastRagdoll(NetworkObject victim, Vector3 impulse)
    {
        if (victim == null || !victim.TryGetComponent(out PlayerRagdoll ragdoll))
            return;

        ragdoll.EnterRagdoll(impulse);
    }

    /// <summary>
    /// 사망자 래그돌에 줄 임펄스 — <see cref="EvaluateKnockback"/>의 <b>수평 성분과 방향</b>만 가져와
    /// 래그돌용 세기·들어올림으로 다시 세운다. 감쇠식·방향은 여전히 그쪽 한 곳이 쥔다.
    ///
    /// <b>비행 거리는 연출 노브다.</b> 예전 주석은 여기를 "설계 제약"이라 못박고 0.1을 정당화했는데,
    /// <b>그 근거는 사라졌다.</b> 근거는 "캡슐이 <c>Move()</c> 스윕으로만 움직여 지형에 막힌다"
    /// (실측: 시체 38.8m / 캡슐 0.23m, §9-11)였다. 그 뒤 <c>PlayerRagdoll.TickCapsuleFollow</c>가
    /// 스윕을 버리고 <c>m_root.position = target</c> 직접 대입으로 바뀌었으므로(§10-0 — 대리값은
    /// 지형을 존중할 이유가 없다) <b>캡슐은 원리적으로 뒤처질 수 없다.</b> 값을 다시 내리려거든
    /// 이 문단부터 다시 읽을 것.
    ///
    /// 남은 실질 상한은 <c>PlayerRagdoll.m_flightAlignPullSpeed</c> 하나다 — 원격이 비행 중 시체를
    /// 스트리밍된 루트에 맞추는 속도라 시체가 그보다 빠르면 잔차가 벌어진다. 여기를 올리면 그쪽도
    /// 같이 올려야 한다(둘은 짝이다).
    ///
    /// <b>균일하게 곱하면 안 된다</b>(그렇게 고쳤다가 되돌렸다). 상승 비율 0.45가 그대로 남아 수직만
    /// 죽고 시체가 바닥을 훑는다 — 탄도에는 0.45가 너무 납작하다. 세기와 들어올림을 따로 잡는다.
    /// 반대로 들어올림이 과하면 위로만 뜨고 가까이 떨어진다 — <b>비율이 곧 발사각이라 1.0(45°)이
    /// 사거리 최대</b>이고, 세기만 올리고 비율을 그대로 두면 높이만 자란다. 예전 1.8은 61°였다.
    ///
    /// <b>탄도 공식을 그대로 믿어도 된다 (2026-08-06 실측).</b> 구르는 사지의 지면 마찰이 사거리를
    /// 크게 먹을 것이라 의심했지만 <b>아니었다</b> — 착지 후 구르는 거리가 짧아진 공중 구간을 메운다.
    /// <code>
    ///   체공 t = (vy + √(vy² + 2·9.81·0.9)) / 9.81      // 골반이 지면 0.9m에서 출발
    ///   수평 거리 ≈ vx · t · k
    /// </code>
    /// <b>k는 세기에 따라 커진다</b> — 세게 칠수록 착지 후 더 구르기 때문이다. 실측 두 점:
    /// <list type="bullet">
    ///   <item>수평 2.50 · 상승 4.50 → 실측 2.56m / 이론 2.71m = <b>94%</b></item>
    ///   <item>수평 5.71 · 상승 6.85 → 실측 9.1m / 이론 8.66m = <b>105%</b> (오너·원격 5cm 차)</item>
    /// </list>
    /// 지금 쓰는 구간(기본값)에서는 <b>k ≈ 1.05</b>로 잡으면 맞다.
    ///
    /// <b>사망 사거리는 3.3m다</b>(피해 150·가장자리 0.2·반경 8 · HP 100) — 임펄스가 실제로 쓰이는
    /// 구간은 폭심~3.3m뿐이고 그 밖은 살아서 넉백만 받는다. 튜닝은 이 좁은 띠만 보면 된다.
    ///
    /// 기본값(0.22 / 1.2)이면 <b>폭심 약 12m(정점 3.2m) · 사망 경계 약 6m(정점 1.5m)</b>다
    /// (실측: 폭탄 1.45m에서 9.1m). tumbleBias가 상체에 더 얹어 회전을 만든다.
    /// </summary>
    private Vector3 EvaluateRagdollImpulse(Vector3 targetPosition)
    {
        Vector3 knockback = EvaluateKnockback(targetPosition);
        Vector3 horizontal = new Vector3(knockback.x, 0f, knockback.z) * m_ragdollImpulseScale;
        if (horizontal == Vector3.zero)
            return Vector3.zero;

        return horizontal + Vector3.up * (horizontal.magnitude * m_ragdollLiftRatio);
    }

    // 반경 내 NPC에 피해를 주고, 죽으면 시체를 날리고 살아 있으면 넉백만 건다.
    // 한 사람이 콜라이더 여러 개로 잡히므로 집합으로 한 번만 처리한다.
    private void ServerBlastNpcs()
    {
        int hitCount = Physics.OverlapSphereNonAlloc(transform.position, m_explosionRadius, s_blastColliders);

        // 포화는 조용히 틀린다 — 넘친 대상은 아무 일도 겪지 않는다
        if (hitCount == s_blastColliders.Length)
            Debug.LogWarning($"[폭탄] 대상 버퍼({s_blastColliders.Length}) 포화 — 일부 NPC가 누락됐을 수 있다", this);

        s_blastNpcs.Clear();
        for (int i = 0; i < hitCount; i++)
        {
            NpcController npc = s_blastColliders[i].GetComponentInParent<NpcController>();
            if (npc == null || !s_blastNpcs.Add(npc))
                continue;

            Vector3 position = npc.transform.position;
            int damage = EvaluateDamage(position);
            if (damage <= 0)
                continue;

            // 환경 피해로 넣는다 — TakeDamage의 게이트는 연행 중을 막는다 (차량과 같은 이유, #690)
            npc.Health.TakeEnvironmentalDamage(damage, gameObject);

            // ⚠ 죽은 NPC에 넉백을 걸면 시체가 Stunned로 되살아난다 — 이 분기가 그 방지다
            if (npc.Death.IsDead)
            {
                if (npc.Ragdoll != null)
                    npc.Ragdoll.EnterRagdoll(EvaluateRagdollImpulse(position));
            }
            else
            {
                npc.Knockback.ServerApplyKnockback(EvaluateKnockback(position));
            }
        }
    }

    // ---- 상태 전파 ----

    private void SetState(BombState state)
    {
        if (m_state == state)
            return;

        m_state = state;
        if (IsSpawned && IsServer)
            m_stateSynced.Value = (int)state;

        HandleStateEntered(state); // 서버·오프라인 로컬 발행 (원격은 동기화 콜백이 담당)
    }

    // 종료 상태 진입 시 표현 이벤트 발행 — 폭발 연출은 전 피어에서 일어난다.
    private void HandleStateEntered(BombState state)
    {
        if (state == BombState.Exploded)
            OnExploded?.Invoke();
    }

    // ---- 동기화 콜백 (원격 클라 전용) ----

    private void HandleStateSyncedChanged(int previous, int current)
    {
        if (IsServer)
            return;
        m_state = (BombState)current;
        HandleStateEntered((BombState)current);
    }
}
