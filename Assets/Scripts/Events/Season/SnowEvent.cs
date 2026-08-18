using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 눈 이벤트 (#227) — 일정 시간 눈을 내리고, <b>오래 내리면 빙판이 깔린다</b>.
///
/// 빙판은 눈과 별개의 값이다: 눈은 켜짐/꺼짐이지만 빙판은 <see cref="IceRatio"/>로 자란다
/// (0 ~ <see cref="m_iceMaxRatio"/>).
/// 눈이 내리는 동안 쌓이고 그친 뒤에는 서서히 녹으므로, 눈이 그쳤다고 길이 곧바로 안전해지지 않는다 —
/// "한참 내렸다"는 사실이 길에 남는 것이 이 이벤트의 값이다.
///
/// <b>비율은 서버가 정하고 전 피어가 읽는다</b> (#56). 빙판은 두 곳이 함께 봐야 한다:
/// 시각 표현(각 피어의 화면)과 미끄러짐 판정. 서버 전용 필드로는 표현 계층이 읽을 수 없다.
/// 매 프레임 쓰지 않고 눈에 띄게 바뀔 때만 갱신한다(<see cref="k_ratioEpsilon"/>) — 라운드 내내 도는
/// 값이라 그대로 흘리면 대역폭만 먹는다.
///
/// ⚠ <b>초안 — 팀 검토 필요.</b> 빙판·미끄러짐은 GDD에 근거가 없다(6-4 이벤트 표·8장 어디에도 없음).
/// 수치는 밸런싱 보류로 인스펙터에 두었고, 규칙 자체를 GDD에 한 줄 올려야 한다.
///
/// <b>라운드 지속형이다</b> (#700) — 준비 단계에 뽑혀 라운드 끝까지 내리고, 빙판 누적은 InProgress부터
/// 시작된다(매니저가 그때부터 ServerTick을 돌린다). 근거는 <see cref="IRoundWeather"/>.
/// </summary>
[RequireComponent(typeof(SuddenEventManager))]
public class SnowEvent : NetworkBehaviour, IRoundWeather
{
    // 이만큼 달라져야 동기화 값을 다시 쓴다 — 0.02면 화면에서 구분되지 않는 차이다
    private const float k_ratioEpsilon = 0.02f;

    [Header("빙판 (초안 — 팀 검토 필요)")]
    [Tooltip("눈이 이만큼(초) 누적되면 빙판이 생기기 시작한다 — 이 전에는 미끄럽지 않다")]
    [Min(0f)]
    [SerializeField] private float m_iceOnsetSeconds = 20f;

    [Tooltip("누적이 이만큼(초)이면 빙판이 상한(아래 Ice Max Ratio)에 닿는다")]
    [Min(1f)]
    [SerializeField] private float m_iceFullSeconds = 50f;

    [Tooltip("빙판이 도달할 수 있는 최대치 — 1이면 완전 빙판. 눈이 라운드 내내 내리므로(#700) 여기서 민다")]
    [Range(0f, 1f)]
    [SerializeField] private float m_iceMaxRatio = 0.6f;

    [Tooltip("눈이 그친 뒤 빙판이 완전히 녹는 데 걸리는 시간(초) — 그쳐도 길은 한동안 미끄럽다")]
    [Min(1f)]
    [SerializeField] private float m_thawSeconds = 45f;

    [Header("실내 차단")]
    [Tooltip(
        "머리 위로 이 거리(m) 안에 지붕이 있으면 그 자리에는 빙판이 없다 — 0이면 실내에서도 미끄럽다.\n\n"
            + "눈 표현(SnowView)의 같은 이름 값과 맞춰 둘 것. 건물 높이보다 넉넉히"
    )]
    [Min(0f)]
    [SerializeField] private float m_shelterProbeHeight = 25f;

    [Tooltip("하늘을 막는 것으로 칠 레이어 — 건물은 Default다")]
    [SerializeField] private LayerMask m_shelterMask = 1;

    // 동기화 플래그 및 서버/오프라인용 진실값
    private readonly NetworkVariable<bool> m_snowSynced = new NetworkVariable<bool>(false);
    private bool m_snow;

    // 빙판 — 서버가 누적을 굴리고 비율만 전 피어에 흘린다
    private readonly NetworkVariable<float> m_iceRatioSynced = new NetworkVariable<float>(0f);
    private float m_snowSeconds; // 누적 강설 시간(초). 서버(또는 오프라인) 전용
    private float m_iceRatioLocal;

    public string DisplayName => "눈";

    public WeatherKind Kind => WeatherKind.Snow;

    /// <summary>
    /// 이벤트가 진행 중인가 — <b>눈이 내리는 동안만</b>이다. 라운드 지속형이라 이 값은 라운드 내내
    /// true이고, <see cref="ServerReset"/>으로 꺼진 뒤 남은 빙판이 녹는 구간은 활성이 아니다.
    /// 녹이는 것은 <see cref="ServerTickThaw"/>가 비활성 중에도 돌려 준다.
    /// </summary>
    public bool IsActive => m_snow;

    /// <summary>
    /// 조용히 시작한다 (팀 확정 2026-08-13) — 날씨는 <b>보면 안다</b>. 하늘이 바뀌고 시야가 줄어드는 것
    /// 자체가 알림이라, 토스트를 얹으면 같은 사실을 두 번 말하는 셈이다. 돌발 이벤트 토스트는
    /// "지금 대응할 일이 생겼다"를 위해 아껴 둔다 — 날씨까지 끼면 그 신호가 묽어진다.
    /// </summary>
    public bool AnnounceOnBegin => false;

    // 원격 클라이언트면 동기화 값을, 서버(또는 오프라인)면 진실값을 사용
    public bool IsSnow => (!IsSpawned || IsServer) ? m_snow : m_snowSynced.Value;

    /// <summary>
    /// 지금 깔린 빙판의 정도 (0=없음 ~ 1=최대). 전 피어에서 유효하다.
    /// 시각 표현과 미끄러짐 판정이 같은 값을 읽는다 — 보이는 것과 밟히는 것이 어긋나지 않게.
    /// </summary>
    public float IceRatio => (!IsSpawned || IsServer) ? m_iceRatioLocal : m_iceRatioSynced.Value;

    /// <summary>
    /// <b>그 자리의</b> 빙판 정도 — 지붕 아래면 0이다. 미끄러짐 판정은 <see cref="IceRatio"/> 대신 이쪽을 쓴다.
    ///
    /// 빙판은 하늘에서 내린 눈이 쌓인 것이라 실내 바닥에는 생길 이유가 없다. 눈 표현은 이미 지붕 아래에서
    /// 그치므로(<see cref="SnowView"/>), 판정만 전역 값을 보면 <b>눈 한 송이 안 오는 실내에서 바닥만 어는</b>
    /// 상태가 된다 — 보이는 것과 밟히는 것이 어긋난다. (#699)
    ///
    /// 실내 판정은 강수·낙뢰와 같은 규칙(<see cref="WeatherShelter"/>)을 쓴다. 두 곳이 다르게 답하면
    /// "눈은 안 오는데 길은 얼었다"가 된다.
    /// </summary>
    /// <param name="position">발밑이 아니라 <b>몸이 있는 높이</b>를 넘길 것 — 바닥에서 쏘면 자기가 선 바닥에 걸린다.</param>
    public float IceRatioAt(Vector3 position)
    {
        float ratio = IceRatio;
        if (ratio <= 0f)
            return 0f; // 깔린 빙판이 없으면 실내든 실외든 답이 같다 — 레이를 아낀다

        return WeatherShelter.IsSheltered(position, m_shelterMask, m_shelterProbeHeight) ? 0f : ratio;
    }

    public event Action<bool> OnSnowChanged;

    /// <summary>빙판 정도가 바뀐 순간 발행 — 전 피어. 인자는 새 <see cref="IceRatio"/>.</summary>
    public event Action<float> OnIceRatioChanged;

    public override void OnNetworkSpawn()
    {
        // ⚠ 구독/해제는 <b>같은 메서드 참조</b>여야 한다. 예전에는 양쪽에 람다를 따로 써서
        // -= 가 다른 델리게이트를 지우려 해 실제로는 해제되지 않았다.
        m_snowSynced.OnValueChanged += HandleSnowSynced;
        m_iceRatioSynced.OnValueChanged += HandleIceSynced;

        // Late-join — 이미 내리는 중이거나 빙판이 깔려 있으면 지금 반영한다
        if (m_snowSynced.Value)
            OnSnowChanged?.Invoke(true);
        if (m_iceRatioSynced.Value > 0f)
            OnIceRatioChanged?.Invoke(m_iceRatioSynced.Value);
    }

    public override void OnNetworkDespawn()
    {
        m_snowSynced.OnValueChanged -= HandleSnowSynced;
        m_iceRatioSynced.OnValueChanged -= HandleIceSynced;
    }

    private void HandleSnowSynced(bool previous, bool current) => OnSnowChanged?.Invoke(current);

    private void HandleIceSynced(float previous, float current) => OnIceRatioChanged?.Invoke(current);

    // 빙판은 이벤트가 끝난 뒤에도 녹아야 한다 — 매니저는 비활성 이벤트를 틱하지 않으므로
    // 녹이는 것만 자기 Update에서 돈다 (AbductionEvent가 Update를 직접 도는 것과 같은 관례).
    private void Update()
    {
        if (IsSpawned && !IsServer)
            return;
        if (m_snow)
            return; // 내리는 중의 누적은 ServerTick이 굴린다

        ServerTickThaw();
    }

    public bool CanTrigger() => true;

    public void ServerBegin() => SetSnow(true);

    public void ServerTick()
    {
        // 내리는 동안 누적 — 오래 내리면 빙판이 깔린다.
        // <b>최대치에서 멈춘다.</b> 상한이 없으면 누적이 라운드 내내 자라, 비율은 1에서 멈춰 있어도
        // 해빙이 그 초과분부터 되감아야 해서 녹는 데 m_thawSeconds의 몇 배가 걸린다.
        // 라운드 지속형이 되며 이 상한이 더 중요해졌다 (#700) — 눈이 그치지 않는다.
        m_snowSeconds = Mathf.Min(m_snowSeconds + Time.deltaTime, m_iceFullSeconds);
        RefreshIceRatio();
    }

    public void ServerReset()
    {
        SetSnow(false);

        // 라운드 경계에서는 빙판도 즉시 걷는다 — 다음 라운드가 미끄러운 길에서 시작하면 안 된다
        m_snowSeconds = 0f;
        RefreshIceRatio();
    }

    // 눈이 그친 뒤 서서히 녹인다 — 누적 시간을 되감는 방식이라 다시 내리면 이어서 쌓인다.
    private void ServerTickThaw()
    {
        if (m_snowSeconds <= 0f)
            return;

        // m_thawSeconds에 걸쳐 최대치(=m_iceFullSeconds)만큼 녹는 속도
        m_snowSeconds = Mathf.Max(0f, m_snowSeconds - Time.deltaTime * (m_iceFullSeconds / m_thawSeconds));
        RefreshIceRatio();
    }

    // 누적 시간 → 빙판 비율. 시작 시간 전에는 0이고 최대 시간에서 1이 된다.
    private void RefreshIceRatio()
    {
        float ratio = m_iceFullSeconds > m_iceOnsetSeconds
            ? Mathf.Clamp01((m_snowSeconds - m_iceOnsetSeconds) / (m_iceFullSeconds - m_iceOnsetSeconds))
            : (m_snowSeconds >= m_iceOnsetSeconds ? 1f : 0f);

        // 상한에서 멈춘다 (#700) — 눈이 그치지 않으므로 상한을 안 두면 라운드 내내 완전 빙판이 된다.
        // 누적(m_snowSeconds)은 그대로 두고 <b>결과만</b> 자른다: 해빙이 되감을 거리가 달라지지 않는다.
        ratio = Mathf.Min(ratio, m_iceMaxRatio);

        // 0과 상한은 경계라 반드시 도달시킨다 — epsilon으로 걸러 상한 직전에 멈추면 "다 깔렸다"가 안 된다
        bool boundary =
            (ratio <= 0f || ratio >= m_iceMaxRatio) && !Mathf.Approximately(ratio, m_iceRatioLocal);
        if (!boundary && Mathf.Abs(ratio - m_iceRatioLocal) < k_ratioEpsilon)
            return;

        m_iceRatioLocal = ratio;
        if (IsSpawned && IsServer)
            m_iceRatioSynced.Value = ratio; // OnValueChanged가 전 피어에서 OnIceRatioChanged로 이어진다
        else if (!IsSpawned)
            OnIceRatioChanged?.Invoke(ratio); // 오프라인 폴백 — NetworkVariable이 안 돈다
    }

    private void SetSnow(bool value)
    {
        if (m_snow == value)
            return;

        m_snow = value;
        if (IsSpawned && IsServer)
            m_snowSynced.Value = value;
        else if (!IsSpawned)
            OnSnowChanged?.Invoke(value); // 오프라인 폴백 — 세션에서는 위 OnValueChanged가 발행한다
    }
}
