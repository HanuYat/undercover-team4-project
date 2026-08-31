using System;
using Unity.Netcode;
using UnityEngine;

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
/// 추격 폭탄의 브레인 — 서버 권위. 무장하면 가장 가까운 현장 플레이어를 쫓고 제한시간이 끝나면
/// 터진다. <b>해체도 밀어내기도 없다</b> — 대응은 달아나기뿐이고, 진압봉으로 때리면 즉발한다
/// (<see cref="ServerDetonate"/> — 오조작의 대가다). 스폰·수명은 <see cref="BombChaseEvent"/>가 쥔다.
/// (GDD 6-4, #399)
/// <b>파사드다 (#768 분할).</b> 상태 기계·수명 타이머·권위 판정만 직접 들고, 추격은
/// <see cref="BombChaseDriver"/>, 폭발은 <see cref="BombBlast"/>에 맡긴다. <b>상태 주인은 여기
/// 하나다</b> — 전이가 갈리면 원격 동기화가 어느 쪽을 믿을지 모호해진다. 소비자는 전부 이 파사드에
/// 붙으므로 분할 뒤에도 붙는 자리가 바뀌지 않는다.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(BombChaseDriver))]
[RequireComponent(typeof(BombBlast))]
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
    private float m_armAtLocal;     // Time.time 기준 무장(등장 완료) 시각 (서버·오프라인)
    private float m_explodeAtLocal; // Time.time 기준 폭발 시각 (서버·오프라인)

    private BombChaseDriver m_chase;
    private BombBlast m_blast;

    // 폭발 이벤트를 이미 발행했는가 — 상태 동기화와 RPC가 겹쳐도 연출이 두 번 나지 않게 한다 (#936)
    private bool m_explodedRaised;

    // 라운드당 폭탄 1개 — 씬에 놓인 상자(BombCrate)가 "내 상자에서 나오는 폭탄인가"를 묻는 단일 참조.
    // 매니저가 아니라 스폰물이므로 App 파사드가 아닌 이 정적 참조로 노출한다(단일 인스턴스 보장은 이벤트가 한다).
    private static BombDevice s_active;

    /// <summary>현재 씬에 살아 있는 폭탄 — 없으면 null. 상자가 여는 시점을 판단하는 진입점.</summary>
    public static BombDevice Active => s_active;

    /// <summary>현재 상태 — 서버·오프라인은 실참조, 원격 피어는 동기화값.
    /// 폭발만 예외로 실참조를 먼저 본다 — 클라는 RPC로 먼저 알 수 있고 그때 동기화값은 아직 Locked다 (#936).</summary>
    public BombState State =>
        m_state == BombState.Exploded || !IsSpawned || IsServer ? m_state : (BombState)m_stateSynced.Value;

    /// <summary>카운트다운이 도는 중인가 — 추격 중(Armed)과 폭심 확정 후(Locked)를 함께 묶는다.</summary>
    public bool IsCountingDown => State == BombState.Armed || State == BombState.Locked;

    /// <summary>지금 때리면 터지는 상태인가 — <see cref="Baton"/>이 타격 판정에 쓴다.
    /// 카운트다운 전(등장·대기)과 이미 터진 뒤는 그냥 소품이라 빗나감으로 둔다.</summary>
    public bool CanBeStruck => IsCountingDown;

    /// <summary>피해·넉백이 닿는 반경(m) — <see cref="BombBlast"/>가 든 값을 그대로 내보낸다.</summary>
    public float ExplosionRadius => m_blast.ExplosionRadius;

    /// <summary>넉백 세기(m/s) — <see cref="BombBlast"/>가 든 값을 그대로 내보낸다.</summary>
    public float KnockbackForce => m_blast.KnockbackForce;

    /// <summary>
    /// 폭심에서 <paramref name="targetPosition"/>이 받는 넉백 속도(m/s) — 반경 밖이면 <see cref="Vector3.zero"/>.
    /// 뷰(<see cref="BombExplosionView"/>)가 자기 오너 캐릭터를 밀 때 쓴다. 식은
    /// <see cref="BombBlastProfile"/> 한 곳이 쥐고 이것은 파사드 위임이다.
    /// </summary>
    public Vector3 EvaluateKnockback(Vector3 targetPosition) => m_blast.EvaluateKnockback(targetPosition);

    /// <summary>대상이 벽 등에 가려졌는가 — 넉백 연출(<see cref="BombExplosionView"/>)이 서버 피해 판정과 같은 기준을 쓴다.</summary>
    public bool IsOccluded(Vector3 targetPosition, Transform targetRoot) => m_blast.IsOccluded(targetPosition, targetRoot);

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
        m_chase = GetComponent<BombChaseDriver>();
        m_blast = GetComponent<BombBlast>();

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

        // 이동 권한은 서버 하나뿐이다 — 끄는 것은 에이전트 소유자(BombChaseDriver)에게 맡긴다.
        // 권위는 스폰 뒤에야 확정되므로 Awake가 아니라 여기서 끈다.
        if (!IsServer)
        {
            m_chase.DisableAgent();
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

        // 대기 — 상자 앞에서 사람을 기다린다. 카운트다운은 아직 돌지 않는다.
        // 시계는 표적이 생기는 순간부터 돈다 — 빈 골목에서 혼자 터지면 누구도 위협하지 못한다.
        if (m_state == BombState.Dormant)
        {
            if (m_chase.PollWakeTrigger())
                ServerArm();
            return;
        }

        if (m_state != BombState.Armed && m_state != BombState.Locked)
            return;

        // 폭심 확정 — 남은 시간이 얼마 없으면 멈춰서 "여기서 터진다"를 보여준다.
        if (m_state == BombState.Armed && m_explodeAtLocal - Time.time <= m_lockSeconds)
        {
            m_chase.Stop();
            SetState(BombState.Locked);
        }
        else if (m_state == BombState.Armed)
        {
            m_chase.Tick();
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
    /// 폭탄을 무장한다 — 대기 중 현장 인원이 깨우기 반경 안에 들어오면 스스로 호출한다.
    /// <b>카운트다운은 여기서 시작한다</b> — 등장 시점이 아니라 표적이 생긴 시점이 기준이다.
    /// </summary>
    public void ServerArm()
    {
        if (!IsAuthority)
            return;

        m_explodeAtLocal = Time.time + m_countdownSeconds;
        m_chase.ResetRetargetClock(); // 다음 Update에서 곧바로 표적을 고른다

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

    // 폭발 오케스트레이션 — 중복 가드와 상태 전이가 여기 있는 이유는 상태 주인이 여기이기 때문이다.
    private void ServerExplode()
    {
        if (m_state == BombState.Exploded)
            return;

        m_chase.Stop();
        m_blast.ServerExplode();
        SetState(BombState.Exploded);

        // 폭발만 RPC로 한 번 더 알린다 (#936) — 폭탄은 폭발 직후 디스폰되므로 상태 동기화에만
        // 기대면 델타가 틱 전에 사라져 원격 피어에 영영 도달하지 않는다(연출이 통째로 빠진다).
        if (IsSpawned && IsServer)
            ExplodedClientRpc();
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
            RaiseExplodedOnce();
    }

    // 폭발 알림 경로가 둘(상태 동기화·RPC)이라 한 번만 발행되도록 잠근다 — 호스트는 로컬 발행과
    // 자기에게도 오는 ClientRpc를 둘 다 받고, 클라도 델타가 제때 나가면 양쪽을 받는다.
    private void RaiseExplodedOnce()
    {
        if (m_explodedRaised)
            return;

        m_explodedRaised = true;
        OnExploded?.Invoke();
    }

    [ClientRpc]
    private void ExplodedClientRpc()
    {
        m_state = BombState.Exploded; // 델타가 못 나갔을 수 있다 — 상태도 여기서 맞춘다
        RaiseExplodedOnce();
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
