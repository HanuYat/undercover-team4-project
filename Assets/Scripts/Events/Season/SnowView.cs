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
    [Tooltip("카메라 기준 먹구름 높이(m)")]
    [SerializeField] private float m_cloudHeight = 20f;

    [Tooltip("구름에서 눈이 방출되기 시작하는 지점까지의 낙차(m) — 구름 두께만큼 내려 뿌린다")]
    [SerializeField] private float m_precipitationDrop = 4f;

    [Header("크기")]
    [SerializeField] private float m_snowScale = 1f;
    [SerializeField] private float m_cloudScale = 10f;

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
        m_rig = WeatherSkyRig.Create("WeatherSky_Snow", m_cloudHeight, m_precipitationDrop);

        if (m_cloudPrefab != null)
            WeatherSkyRig.Attach(Instantiate(m_cloudPrefab), m_rig.CloudAnchor, m_cloudScale);

        if (m_snowParticlePrefab != null)
            WeatherSkyRig.Attach(Instantiate(m_snowParticlePrefab), m_rig.PrecipitationAnchor, m_snowScale);
    }

    private void HideSnow()
    {
        if (m_rig == null)
            return;

        m_rig.StopAndDispose(m_stopFadeSeconds);
        m_rig = null;
    }
}
