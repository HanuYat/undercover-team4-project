using System;
using Unity.Netcode;
using UnityEngine;
using Random = UnityEngine.Random;

// GDD 6-4 날씨 이벤트: 번개
[RequireComponent(typeof(SuddenEventManager))]
public class LightningEvent : NetworkBehaviour, ISuddenEvent
{
    // --- 인스펙터 노출 수치 ---
    [Header("Settings")]
    [SerializeField]
    private float m_durationSeconds = 15f; // 이벤트 총 지속 시간

    [Header("Strike Interval (Seconds)")]
    [SerializeField]
    private float m_strikeIntervalMin = 2f; // 낙뢰 최소 주기

    [SerializeField]
    private float m_strikeIntervalMax = 5f; // 낙뢰 최대 주기

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
    private float m_endTime;
    private float m_nextStrikeTime;

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

    // --- ISuddenEvent 구현 ---
    public string DisplayName => "번개";
    public bool IsActive => m_lightning;

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
        m_endTime = Time.time + m_durationSeconds;
        SetLightning(true);

        // 첫 낙뢰 시간 예약
        ScheduleNextStrike();
        Debug.Log($"[LightningEvent] ServerBegin. Ends at {m_endTime}s.");
    }

    public void ServerTick()
    {
        if (!m_lightning)
            return;

        // 지속 시간 종료 체크
        if (Time.time >= m_endTime)
        {
            SetLightning(false);
            Debug.Log("[LightningEvent] ServerEnd by duration.");
            return;
        }

        // 주기적 낙뢰 발생 처리
        if (Time.time >= m_nextStrikeTime)
        {
            PerformLightningStrike();
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
    private void PerformLightningStrike()
    {
        if (!IsServer)
            return;

        // 1. 대상 선정: 씬의 PlayerHealth 중 무작위 1명
        PlayerHealth target = GetRandomPlayerField();

        if (target == null)
            return; // 대상 없으면 패스

        Vector3 strikePosition = target.transform.position;

        // 2. 효과 롤 (Random.value < m_damageChance)
        bool isDamage = Random.value < m_damageChance;

        if (isDamage)
        {
            ApplyDamage(target);
            Debug.Log($"[LightningEvent] Strike DAMAGE at {strikePosition}");
        }
        else
        {
            ApplySpeedBuff(target);
            Debug.Log($"[LightningEvent] Strike BUFF at {strikePosition}");
        }

        // 3. VFX 표현: 전 클라에 RPC 전송
        PlayStrikeVFXClientRpc(strikePosition);
    }

    // 씬의 플레이어 중 <b>하늘이 뚫린 곳에 있는</b> 사람만 후보로 두고 무작위로 하나 고른다.
    //
    // 지붕 아래를 빼는 이유는 그림이 말이 안 되기 때문이다 — 건물 안에 서 있는데 벼락을 맞는다.
    // 비·눈이 그치는 판정과 <b>같은 규칙</b>을 쓴다(WeatherShelter): 한쪽만 고치면 "비는 그쳤는데
    // 벼락은 떨어진다"가 된다.
    //
    // 전원이 실내면 이번 낙뢰는 거른다 — 밖에 있는 사람이 없으면 떨어질 곳도 없다. 주기는 그대로
    // 흐르므로(ScheduleNextStrike) 누군가 나오면 다음 주기에 다시 후보가 된다.
    private PlayerHealth GetRandomPlayerField()
    {
        PlayerHealth[] players = FindObjectsByType<PlayerHealth>(FindObjectsSortMode.None);

        if (players == null || players.Length == 0)
            return null;

        s_exposed.Clear();
        for (int i = 0; i < players.Length; i++)
        {
            PlayerHealth player = players[i];
            if (player == null)
                continue;

            // 발밑이 아니라 몸 높이에서 쏜다 — 바닥에서 쏘면 자기가 선 바닥에 걸리는 맵이 있다.
            Vector3 origin = player.transform.position + Vector3.up * k_shelterProbeOriginHeight;
            if (WeatherShelter.IsSheltered(origin, m_shelterMask, m_shelterProbeHeight))
                continue;

            s_exposed.Add(player);
        }

        if (s_exposed.Count == 0)
            return null;

        return s_exposed[Random.Range(0, s_exposed.Count)];
    }

    // 실내 판정 레이의 시작 높이(m) — 사람 가슴께. 발밑에서 쏘면 자기 바닥에 걸린다.
    private const float k_shelterProbeOriginHeight = 1f;

    // 후보 버퍼 — 서버에서만 도는 경로라 공유해도 안전하다(프레임마다의 할당 방지, NpcFleeState와 같은 수법)
    private static readonly System.Collections.Generic.List<PlayerHealth> s_exposed =
        new System.Collections.Generic.List<PlayerHealth>();

    // 가해자를 null로 넘긴다 — 하늘에서 떨어진 것이라 피격 방향 표시(PlayerHitView)가 가리킬 곳이 없다.
    // TakeDamage가 null 가해자를 이미 다루므로(BroadcastDamaged의 hasAttacker) 별도 분기가 필요 없다.
    private void ApplyDamage(PlayerHealth healthComponent)
    {
        if (healthComponent == null)
            return;

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
    private void PlayStrikeVFXClientRpc(Vector3 position)
    {
        // 서버는 perform 로직에서 이미 로그를 찍었으므로, 호스트/클라 표현만 처리
        if (IsServer && !IsHost)
            return;

        // 구독자(LightningView)가 섬광·파티클을 낸다 — 뷰를 직접 알지 않는다 (위 OnStrike 주석)
        OnStrike?.Invoke(position);
    }
}
