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
/// 제한시간이 끝나면 그 자리에서 폭발한다. <b>해체는 없다</b> — 대응 수단은 두 가지뿐이다:
/// <b>달아나기</b>(추격 속도가 달리기보다 느리다 — 걷기보다는 빠르므로 뛰어야 벌어진다)와
/// <b>진압봉으로 밀어내기</b>(<see cref="ServerPush"/>).
/// 폭발은 반드시 일어나므로 "막는 게임"이 아니라 "폭심에서 벗어나는 게임"이다.
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

    [Header("밀어내기 (진압봉 대응)")]
    [Tooltip("진압봉에 맞았을 때 밀려나는 초기 속도(m/s)")]
    [SerializeField]
    private float m_pushForce = 9f;

    [Tooltip("밀린 속도가 잦아드는 감속(m/s²)")]
    [SerializeField]
    private float m_pushDrag = 12f;

    [Tooltip("맞은 뒤 추격을 다시 시작하기까지의 시간(초) — 진압봉 쿨다운(0.9초)보다 짧게 둘 것, 길면 혼자서 무한 저지가 된다")]
    [SerializeField]
    private float m_pushRecoverSeconds = 0.6f;

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

    private Vector3 m_pushVelocity;      // 진압봉에 맞아 밀려나는 중의 수평 속도
    private float m_pushRecoverAt;       // 이 시각까지는 추격을 멈춘다

    private readonly List<Transform> m_blastBuffer = new List<Transform>();

    // NPC 넉백 대상 수집용 공유 버퍼 — 서버(또는 오프라인)에서만 쓰므로 정적으로 공유해도 안전하다
    private static readonly Collider[] s_blastColliders = new Collider[64];

    // 라운드당 폭탄 1개 — 씬에 놓인 상자(BombCrate)가 "내 상자에서 나오는 폭탄인가"를 묻는 단일 참조.
    // 매니저가 아니라 스폰물이므로 App 파사드가 아닌 이 정적 참조로 노출한다(단일 인스턴스 보장은 이벤트가 한다).
    private static BombDevice s_active;

    /// <summary>현재 씬에 살아 있는 폭탄 — 없으면 null. 상자가 여는 시점을 판단하는 진입점.</summary>
    public static BombDevice Active => s_active;

    /// <summary>현재 상태 — 서버·오프라인은 실참조, 원격 피어는 동기화값.</summary>
    public BombState State => IsSpawned && !IsServer ? (BombState)m_stateSynced.Value : m_state;

    /// <summary>카운트다운이 도는 중인가 — 추격 중(Armed)과 폭심 확정 후(Locked)를 함께 묶는다.</summary>
    public bool IsCountingDown => State == BombState.Armed || State == BombState.Locked;

    /// <summary>지금 밀어낼 수 있는 상태인가 — <see cref="Baton"/>이 타격 판정에 쓴다.</summary>
    public bool CanBePushed => IsCountingDown;

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

    /// <summary>진압봉에 맞아 밀려났다 — 전 피어. 타격 연출용 순간 이벤트.</summary>
    public event Action OnPushed;

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

        TickPush();

        // 폭심 확정 — 남은 시간이 얼마 없으면 멈춰서 "여기서 터진다"를 보여준다.
        // 멈춘 뒤에도 밀어내기는 통한다(TickPush가 앞에서 돈다) — 마지막에 몸으로 막는 선택이 남는다.
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
    /// 진압봉 타격 진입점 — 서버(또는 오프라인)에서만 호출한다. 폭탄을 <paramref name="direction"/> 쪽으로
    /// 밀어내고 잠시 추격을 끊는다. 남은 시간은 건드리지 않는다 — 밀어내기는 <b>거리를 버는</b> 수단이지
    /// 폭발을 미루는 수단이 아니다.
    /// </summary>
    public void ServerPush(Vector3 direction)
    {
        if (!IsAuthority)
            return;
        if (m_state != BombState.Armed && m_state != BombState.Locked)
            return;

        direction.y = 0f; // 굴러가는 물건이라 띄우지 않는다 — 뜨면 NavMesh에서 떨어진다
        if (direction.sqrMagnitude < 0.0001f)
            return;

        m_pushVelocity = direction.normalized * m_pushForce;
        m_pushRecoverAt = Time.time + m_pushRecoverSeconds;
        StopAgent(); // 밀리는 동안은 경로를 버린다 — 남겨두면 에이전트가 제자리로 되끌어당긴다

        NotifyPushed();
    }

    // 밀려나는 1프레임 — NavMeshAgent.Move로 옮겨 벽을 뚫거나 NavMesh 밖으로 나가지 않게 한다.
    // NPC 넉백(NpcController.TickKnockback)처럼 에이전트를 떼어내 포물선으로 날리지 않는 이유는,
    // 폭탄은 굴러가는 물건이라 뜰 필요가 없고 그러면 착지·복귀 경로가 통째로 필요해지기 때문이다.
    private void TickPush()
    {
        if (m_pushVelocity.sqrMagnitude < 0.01f)
            return;

        if (m_agent.enabled && m_agent.isOnNavMesh)
            m_agent.Move(m_pushVelocity * Time.deltaTime);

        m_pushVelocity = Vector3.MoveTowards(m_pushVelocity, Vector3.zero, m_pushDrag * Time.deltaTime);
    }

    // 표적 재선정 + 목적지 갱신. 표적도 움직이므로 같은 주기로 목적지를 다시 찍는다.
    private void TickChase()
    {
        if (Time.time < m_pushRecoverAt)
            return; // 맞고 밀려나는 중 — 이 시간이 곧 벌어준 거리다
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

    private void NotifyPushed()
    {
        OnPushed?.Invoke(); // 서버·오프라인 로컬 발행
        if (IsSpawned && IsServer)
            PushedClientRpc();
    }

    [ClientRpc]
    private void PushedClientRpc()
    {
        if (IsServer)
            return; // 호스트는 위에서 이미 발행
        OnPushed?.Invoke();
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
