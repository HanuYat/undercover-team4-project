using System;
using Unity.Netcode;
using UnityEngine;
using Random = UnityEngine.Random;

// GDD 6-4 날씨 이벤트: 번개
// 라운드 지속형이다 (#700) — 준비 단계에 뽑혀 라운드 끝까지 이어지고, 낙뢰는 InProgress부터 떨어진다
// (매니저가 그때부터 ServerTick을 돌린다). 근거는 IRoundWeather.
[RequireComponent(typeof(SuddenEventManager))]
public class LightningEvent : NetworkBehaviour, IRoundWeather
{
    // --- 인스펙터 노출 수치 ---
    [Header("Strike Interval (Seconds)")]
    [SerializeField]
    private float m_strikeIntervalMin = 2f; // 낙뢰 최소 주기

    [SerializeField]
    private float m_strikeIntervalMax = 5f; // 낙뢰 최대 주기

    [Header("예고 낙하 (#647)")]
    [Tooltip("지점을 알린 뒤 벼락이 떨어지기까지의 시간(초) — 0이면 예고 없이 즉발이다")]
    [Min(0f)]
    [SerializeField]
    private float m_warningSeconds = 0.6f;

    [Tooltip(
        "낙뢰 지점에서 이 거리(m) 안에 있으면 맞는다 — 수평 거리다.\n\n"
            + "예고 시간 × 걷기 속도(5m/s)보다 작아야 움직여서 빠져나갈 수 있다"
    )]
    [Min(0.1f)]
    [SerializeField]
    private float m_strikeRadius = 2.5f;

    [Header("Strike Effects")]
    [Range(0f, 1f)]
    [SerializeField]
    private float m_damageChance = 0.5f; // 피해 발생 확률 (나머지는 버프)

    [SerializeField]
    private int m_damageAmount = 1; // 피해량

    [SerializeField]
    private float m_buffMultiplier = 1.5f; // 이속 버프 배수

    [SerializeField]
    private float m_buffDuration = 5f; // 버프 지속 시간

    [Header("실내 차단")]
    [Tooltip(
        "머리 위로 이 거리(m) 안에 지붕이 있는 플레이어에게는 낙뢰가 떨어지지 않는다 — 0이면 실내에도 떨어진다.\n\n"
            + "건물 높이보다 넉넉히 잡을 것. 비·눈이 그치는 판정과 같은 규칙을 쓴다(WeatherShelter)"
    )]
    [Min(0f)]
    [SerializeField]
    private float m_shelterProbeHeight = 25f;

    [Tooltip("하늘을 막는 것으로 칠 레이어 — 건물은 Default다")]
    [SerializeField]
    private LayerMask m_shelterMask = 1;

    // --- 상태 및 동기화 ---
    // 클라이언트 표현용 동기화 변수 (Server 권한 최신 NGO 문법 적용 완료)
    private NetworkVariable<bool> m_lightningSynced = new NetworkVariable<bool>(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    // 서버/오프라인 진실값
    private bool m_lightning = false;
    private float m_nextStrikeTime;
    private bool m_strikeScheduled; // 첫 예약을 InProgress의 첫 틱으로 미루는 래치 (#700 — ServerBegin 주석)

    // 예고를 걸어 둔 낙뢰 — 지점은 예고 때 굳고, 맞을 사람은 떨어지는 순간에 정해진다 (#647)
    private bool m_hasPendingStrike;
    private Vector3 m_pendingStrikePosition;
    private float m_pendingStrikeTime;

    // 클라이언트 표현 컴포넌트 구독용 이벤트
    public event Action<bool> OnLightningChanged;

    /// <summary>
    /// 낙뢰가 떨어진 순간 발행 — 인자는 떨어진 지점. <b>전 피어</b>에서 발생한다
    /// (<see cref="PlayStrikeVFXClientRpc"/>가 중계한다). <see cref="LightningView"/>가 구독해 섬광·파티클을 낸다.
    ///
    /// 뷰를 직접 부르지 않고 이벤트로 돌리는 이유: 예전에는 <c>LightningView.Instance</c>를 거쳤는데
    /// 새 <c>static Instance</c> 싱글톤은 아키텍처 규칙 R2가 금지한다(docs/architecture.md).
    /// 이벤트로 두면 뷰가 없어도(전용 서버·연출 끈 구성) 이벤트가 그대로 돌고, 구독자를 더 붙일 수도 있다.
    /// </summary>
    public event Action<Vector3> OnStrike;

    /// <summary>
    /// 벼락이 떨어질 지점을 미리 알린다 — <see cref="OnStrike"/>보다 예고 시간만큼 앞선다. 전 피어에서 발생한다. (#647)
    /// 판정은 떨어지는 순간에 이 지점 반경으로 하므로, 이 사이에 자리를 뜨면 맞지 않는다.
    /// </summary>
    public event Action<Vector3> OnStrikeWarning;

    // --- ISuddenEvent 구현 ---
    public string DisplayName => "번개";
    public bool IsActive => m_lightning;
    public WeatherKind Kind => WeatherKind.Lightning;

    /// <summary>
    /// 조용히 시작한다 (팀 확정 2026-08-13) — 날씨는 <b>보면 안다</b>. 하늘이 바뀌고 시야가 줄어드는 것
    /// 자체가 알림이라, 토스트를 얹으면 같은 사실을 두 번 말하는 셈이다. 돌발 이벤트 토스트는
    /// "지금 대응할 일이 생겼다"를 위해 아껴 둔다 — 날씨까지 끼면 그 신호가 묽어진다.
    /// </summary>
    public bool AnnounceOnBegin => false;

    // 클라이언트에서 현재 번개 상태 조회
    public bool IsLightningActive =>
        (!IsSpawned || IsServer) ? m_lightning : m_lightningSynced.Value;

    public override void OnNetworkSpawn()
    {
        m_lightningSynced.OnValueChanged += OnSyncValueChanged;

        // Late-join 처리
        if (m_lightningSynced.Value)
        {
            OnLightningChanged?.Invoke(true);
        }
    }

    public override void OnNetworkDespawn()
    {
        m_lightningSynced.OnValueChanged -= OnSyncValueChanged;
    }

    private void OnSyncValueChanged(bool previousValue, bool newValue)
    {
        OnLightningChanged?.Invoke(newValue);
    }

    // --- 서버 로직 (ISuddenEvent) ---

    public bool CanTrigger() => true;

    public void ServerBegin()
    {
        SetLightning(true);

        // ⚠ 여기서 첫 낙뢰를 예약하지 않는다 (#700) — 시작이 준비 단계라 Time.time은 계속 흐르는데
        // ServerTick은 InProgress부터라, 예약해 두면 그 사이에 시각이 지나 라운드 시작과 동시에 떨어진다.
        m_strikeScheduled = false;
    }

    public void ServerTick()
    {
        if (!m_lightning)
            return;

        // 첫 틱 = InProgress 시작 — 낙뢰 주기는 여기서부터 흐른다
        if (!m_strikeScheduled)
        {
            ScheduleNextStrike();
            m_strikeScheduled = true;
            return;
        }

        // 예고해 둔 벼락이 먼저다 — 뒤로 미루면 예고만 하고 안 떨어지는 프레임이 생긴다
        if (m_hasPendingStrike && Time.time >= m_pendingStrikeTime)
            ResolvePendingStrike();

        // 주기적 낙뢰 예고 처리
        if (Time.time >= m_nextStrikeTime)
        {
            BeginStrikeWarning();
            ScheduleNextStrike();
        }
    }

    // 라운드 종료 강제 정리
    public void ServerReset()
    {
        SetLightning(false);
    }

    private void SetLightning(bool value)
    {
        if (m_lightning == value)
            return;

        m_lightning = value;

        if (!value)
            m_hasPendingStrike = false; // 이벤트가 끝났다 — 예고해 둔 벼락은 취소한다

        // 서버 전용: NetworkVariable 갱신 -> 클라 동기화
        if (IsServer)
        {
            m_lightningSynced.Value = value;
        }

        // 로컬(서버 or 오프라인) 콜백
        OnLightningChanged?.Invoke(value);
    }

    private void ScheduleNextStrike()
    {
        m_nextStrikeTime = Time.time + Random.Range(m_strikeIntervalMin, m_strikeIntervalMax);
    }

    // --- 핵심 낙뢰 로직 (서버 권위) ---
    //
    // 예고 → 낙하 두 단계다 (#647). 예전에는 대상을 뽑아 같은 프레임에 판정하고 연출을 나중에 보냈다 —
    // 화면이 번쩍일 땐 이미 맞은 뒤라 구조적으로 못 피했다. 지금은 지점을 먼저 알리고, 떨어지는 순간에
    // 그 자리에 아직 남아 있는 사람을 친다.

    // 떨어질 지점을 정해 전 피어에 알린다 — 여기서는 아무도 맞지 않는다.
    private void BeginStrikeWarning()
    {
        if (!IsServer || m_hasPendingStrike)
            return;

        PlayerHealth aim = PickExposedPlayer();
        if (aim == null)
            return; // 밖에 아무도 없다 — 겨눌 자리가 없으니 이번 주기는 거른다

        m_pendingStrikePosition = aim.transform.position;
        m_pendingStrikeTime = Time.time + m_warningSeconds;
        m_hasPendingStrike = true;

        PlayStrikeWarningClientRpc(m_pendingStrikePosition);
    }

    // 예고한 지점에 실제로 떨어뜨린다 — 반경 안에 남아 있는 사람만 맞는다.
    private void ResolvePendingStrike()
    {
        m_hasPendingStrike = false;

        Vector3 strikePosition = m_pendingStrikePosition;

        // 효과 롤은 벼락 한 번에 한 번 — 같이 맞은 사람은 같은 결과다
        bool isDamage = Random.value < m_damageChance;

        CollectExposedPlayers(strikePosition, m_strikeRadius);
        for (int i = 0; i < s_exposed.Count; i++)
        {
            if (isDamage)
                ApplyDamage(s_exposed[i]);
            else
                ApplySpeedBuff(s_exposed[i]);
        }

        Debug.Log(
            $"[LightningEvent] Strike {(isDamage ? "DAMAGE" : "BUFF")} at {strikePosition} — hit {s_exposed.Count}"
        );

        // 아무도 안 맞았어도 연출은 떨어뜨린다 — 빗나가는 그림이 보여야 회피가 성립한다
        PlayStrikeVFXClientRpc(strikePosition);
    }

    // 겨눌 사람을 고른다 — 실외에 있는 사람 중 무작위 1명. 없으면 이번 주기는 거른다
    // (주기는 그대로 흐르므로 누군가 나오면 다음에 다시 후보가 된다).
    private PlayerHealth PickExposedPlayer()
    {
        CollectExposedPlayers(Vector3.zero, -1f);
        return s_exposed.Count == 0 ? null : s_exposed[Random.Range(0, s_exposed.Count)];
    }

    // 씬의 플레이어 중 <b>하늘이 뚫린 곳에 있는</b> 사람을 s_exposed에 모은다.
    // <paramref name="radius"/>가 양수면 <paramref name="center"/>에서 그 <b>수평</b> 거리 안만 남긴다
    // (음수면 거리 제한 없음). 벼락은 지면에 떨어지므로 높이 차는 보지 않는다.
    //
    // 지붕 아래를 빼는 이유는 그림이 말이 안 되기 때문이다 — 건물 안에 서 있는데 벼락을 맞는다.
    // 비·눈이 그치는 판정과 <b>같은 규칙</b>을 쓴다(WeatherShelter): 한쪽만 고치면 "비는 그쳤는데
    // 벼락은 떨어진다"가 된다. 실내 판정을 낙하 순간에 다시 하므로 예고를 보고 뛰어든 사람도 산다.
    private void CollectExposedPlayers(Vector3 center, float radius)
    {
        s_exposed.Clear();

        System.Collections.Generic.IReadOnlyList<PlayerHealth> players = PlayerHealth.All;

        float sqrRadius = radius * radius;
        for (int i = 0; i < players.Count; i++)
        {
            PlayerHealth player = players[i];
            if (player == null)
                continue;

            Vector3 position = player.transform.position;

            if (radius > 0f)
            {
                Vector2 flat = new Vector2(position.x - center.x, position.z - center.z);
                if (flat.sqrMagnitude > sqrRadius)
                    continue;
            }

            // 발밑이 아니라 몸 높이에서 쏜다 — 바닥에서 쏘면 자기가 선 바닥에 걸리는 맵이 있다.
            Vector3 origin = position + Vector3.up * WeatherShelter.k_bodyProbeHeight;
            if (WeatherShelter.IsSheltered(origin, m_shelterMask, m_shelterProbeHeight))
                continue;

            s_exposed.Add(player);
        }
    }

    // 후보 버퍼 — 서버에서만 도는 경로라 공유해도 안전하다(프레임마다의 할당 방지, NpcFleeState와 같은 수법)
    private static readonly System.Collections.Generic.List<PlayerHealth> s_exposed =
        new System.Collections.Generic.List<PlayerHealth>();

    // 가해자를 null로 넘긴다 — 하늘에서 떨어진 것이라 피격 방향 표시(PlayerHitView)가 가리킬 곳이 없다.
    // TakeDamage가 null 가해자를 이미 다루므로(BroadcastDamaged의 hasAttacker) 별도 분기가 필요 없다.
    private void ApplyDamage(PlayerHealth healthComponent)
    {
        if (healthComponent == null)
            return;

        // ⚠ 차량·폭탄과 달리 낙뢰는 <b>TakeLethalDamage를 쓰지 않는다</b> — HP가 0이 되면 다운
        // 유예(60초)를 준다. 환경 피해라서가 아니라 <b>피해원별 결정</b>이므로, m_damageAmount를
        // 올려도 이 선택은 유지된다. "환경 피해를 통일"하지 말 것.
        healthComponent.TakeDamage(m_damageAmount, null);
    }

    private void ApplySpeedBuff(PlayerHealth healthComponent)
    {
        // PlayerHealth와 같은 오브젝트에 붙은 PlayerMovement가 배율을 든다 (#227).
        PlayerMovement movement = healthComponent.GetComponent<PlayerMovement>();
        if (movement == null)
            return;

        movement.ServerApplySpeedBuff(m_buffMultiplier, m_buffDuration);
    }

    // --- 표현 RPC ---
    [ClientRpc]
    private void PlayStrikeWarningClientRpc(Vector3 position)
    {
        // 전용 서버는 뷰가 없다 — 아래 VFX RPC와 같은 가드다
        if (IsServer && !IsHost)
            return;

        OnStrikeWarning?.Invoke(position);
    }

    [ClientRpc]
    private void PlayStrikeVFXClientRpc(Vector3 position)
    {
        // 서버는 perform 로직에서 이미 로그를 찍었으므로, 호스트/클라 표현만 처리
        if (IsServer && !IsHost)
            return;

        // 구독자(LightningView)가 섬광·파티클을 낸다 — 뷰를 직접 알지 않는다 (위 OnStrike 주석)
        OnStrike?.Invoke(position);
    }
}
