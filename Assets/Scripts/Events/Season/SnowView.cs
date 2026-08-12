using UnityEngine;

/// <summary>
/// 눈 표현 계층 — <see cref="SnowEvent"/>의 on/off를 받아 먹구름과 눈을 띄운다. (#227)
/// 판정은 이벤트가 하고 여기는 보이는 것만 맡는다 (DeviceBlackoutView와 같은 가름).
///
/// 하늘에 매다는 방식은 <see cref="WeatherSkyRig"/>가 쥔다 — 카메라 위치만 따라가고 회전은 물려받지
/// 않아 눈이 월드 -Y로 떨어지며, 구름과 눈이 한 리그에 매달려 <b>구름 아래에서 내리는</b> 그림이 된다.
/// 예전에는 둘을 각각 카메라의 자식으로 붙여, 고개를 들면 눈이 옆으로 흐르고 구름이 앞쪽으로 휘었다.
///
/// 각 피어가 자기 화면에 자기 리그를 만든다 — 표현이라 복제하지 않는다.
/// </summary>
public class SnowView : MonoBehaviour
{
    [Header("FX 프리팹")]
    [Tooltip("내리는 눈 파티클")]
    [SerializeField] private GameObject m_snowParticlePrefab;

    [Tooltip("머리 위 먹구름 파티클")]
    [SerializeField] private GameObject m_cloudPrefab;

    [Header("하늘 배치")]
    [Tooltip("카메라 기준 먹구름층 높이(m) — 낮으면 하늘이 아니라 '거리 위 연기'로 보인다")]
    [SerializeField] private float m_cloudHeight = 80f;

    [Tooltip("눈이 방출되는 높이(m) — 구름 높이와 따로 준다. 너무 높으면 화면에 닿기까지 오래 걸려 안 내리는 것처럼 보인다")]
    [SerializeField] private float m_precipitationHeight = 14f;

    [Tooltip(
        "켜면 눈을 <b>보는 쪽</b>에만 뿌린다 — 맵을 넓게 채우는 대신 시야 앞 한 덩이로 해결한다.\n\n"
            + "날씨는 각 피어가 자기 화면에만 만드는 표현이라 월드를 채울 필요가 없고, 파티클 수가 훨씬 적다. "
            + "수평 방향만 따라가므로 낙하는 그대로 아래다"
    )]
    [SerializeField] private bool m_snowFollowsView = true;

    [Tooltip("시야 앞으로 밀 거리(m) — 0이면 카메라 위에서 뿌려 눈앞에 붙는다")]
    [Min(0f)]
    [SerializeField] private float m_snowForwardOffset = 8f;

    [Header("크기")]
    [SerializeField] private float m_snowScale = 1f;
    [SerializeField] private float m_cloudScale = 10f;

    [Header("먹구름 — 하늘 덮기")]
    [Tooltip("구름을 한 변 몇 장으로 깔 것인가 — 9면 81장. 하늘을 끝까지 덮으려면 넓어야 한다")]
    [Range(1, 11)]
    [SerializeField] private int m_cloudTiles = 9;

    [Tooltip("구름 장 사이 간격(m) — 구름 크기보다 좁게 둬야 겹쳐서 틈이 안 보인다")]
    [Min(1f)]
    [SerializeField] private float m_cloudSpacing = 70f;

    [Header("눈 진하기")]
    [Tooltip("눈송이 크기 배율 — Synty FX는 근거리 기준이라 하늘용으로는 작다")]
    [Min(0.1f)]
    [SerializeField] private float m_snowSizeBoost = 2.5f;

    [Tooltip("눈 방출량 배율 — 화면이 눈으로 차게. 상한(maxParticles)도 함께 올라간다")]
    [Min(1f)]
    [SerializeField] private float m_snowRateBoost = 6f;

    [Header("그치는 연출")]
    [Tooltip("눈이 그칠 때 방출만 멈추고 이 시간(초) 뒤에 리그를 없앤다 — 공중의 눈이 끝까지 떨어지게")]
    [Min(0f)]
    [SerializeField] private float m_stopFadeSeconds = 6f;

    private SnowEvent m_snowEvent;
    private WeatherSkyRig m_rig;

    private void Start()
    {
        m_snowEvent = App.Game.SuddenEvent?.GetEvent<SnowEvent>();
        if (m_snowEvent == null)
            return;

        m_snowEvent.OnSnowChanged += HandleSnowChanged;
        HandleSnowChanged(m_snowEvent.IsSnow); // 늦게 들어온 클라 — 이미 내리는 중이면 지금 띄운다
    }

    private void OnDestroy()
    {
        if (m_snowEvent != null)
            m_snowEvent.OnSnowChanged -= HandleSnowChanged;
    }

    private void HandleSnowChanged(bool isSnowing)
    {
        if (isSnowing)
            ShowSnow();
        else
            HideSnow();
    }

    private void ShowSnow()
    {
        if (m_rig != null)
            return; // 이미 내리는 중 — 중복 통보 무시

        // 카메라가 아직 없어도 만든다 — 리그가 매 프레임 카메라를 다시 보므로 늦게 생겨도 따라잡는다.
        // 예전에는 여기서 Camera.main이 null이면 return해, 그 이벤트 내내 눈이 한 송이도 안 내렸다.
        m_rig = WeatherSkyRig.Create("WeatherSky_Snow", m_cloudHeight, m_precipitationHeight);
        m_rig.SetCloudSnap(m_cloudSpacing); // 하늘은 월드에 고정 — 구름 한 덩이가 따라오는 그림을 막는다
        m_rig.SetPrecipitationFacesView(m_snowFollowsView, m_snowForwardOffset); // 눈은 보는 쪽에만

        // 구름은 넓게 깔아야 "하늘이 덮였다"로 읽힌다 — 한 장을 키우면 덩이가 부풀 뿐이다
        if (m_cloudPrefab != null)
            WeatherSkyRig.AttachTiled(m_cloudPrefab, m_rig.CloudAnchor, m_cloudScale, m_cloudTiles, m_cloudSpacing);

        if (m_snowParticlePrefab != null)
        {
            GameObject snow = Instantiate(m_snowParticlePrefab);
            WeatherSkyRig.Attach(snow, m_rig.PrecipitationAnchor, m_snowScale);
            WeatherSkyRig.Boost(snow, m_snowSizeBoost, m_snowRateBoost); // 눈이 잘 안 보이던 원인
        }
    }

    private void HideSnow()
    {
        if (m_rig == null)
            return;

        m_rig.StopAndDispose(m_stopFadeSeconds);
        m_rig = null;
    }
}
