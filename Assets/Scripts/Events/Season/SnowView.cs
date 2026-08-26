using UnityEngine;

/// <summary>
/// 눈 표현 계층 — <see cref="SnowEvent"/>의 on/off를 받아 먹구름과 눈을 띄운다. (#227)
/// 판정은 이벤트가 하고 여기는 보이는 것만 맡는다 (DeviceBlackoutView와 같은 가름).
///
/// 눈 자체는 <see cref="PrecipitationScreen"/>이 화면 셰이더로 그린다 (#782) — 이 클래스는 on/off와
/// 먹구름(하늘 밝기)만 쥔다. 파티클 리그로 하늘에 매달던 방식은 폐기했다.
///
/// 각 피어가 자기 화면에 자기 리그를 만든다 — 표현이라 복제하지 않는다.
/// </summary>
public class SnowView : MonoBehaviour
{

    [Tooltip("눈이 오는 동안 씬 태양을 이 배율로 어둡게 한다 — 먹구름 표현의 본체다")]
    [Range(0.1f, 1f)]
    [SerializeField] private float m_overcastIntensityScale = 0.6f;

    [Tooltip("어두워지고 밝아지는 데 걸리는 시간(초)")]
    [Min(0f)]
    [SerializeField] private float m_overcastFadeSeconds = 3f;

    private SnowEvent m_snowEvent;
    private bool m_overcastPushed; // 내가 먹구름을 요청해 둔 상태인가 — Push/Pop 짝을 뷰가 직접 센다

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

        // 눈이 켜진 채 파괴되면(호스트 종료·씬 전환 강제 정리) 요청이 영원히 남는다 — 여기서 짝을 맞춘다
        PopOvercast(0f);
    }

    // 먹구름 요청을 뺀다 — 요청해 둔 적이 있을 때만. 짝 없는 Pop은 같이 오는 비의 어둠까지 걷어 버린다.
    private void PopOvercast(float fadeSeconds)
    {
        if (!m_overcastPushed)
            return;

        m_overcastPushed = false;
        WeatherOvercast.Pop(fadeSeconds);
    }

    private void HandleSnowChanged(bool isSnowing)
    {
        if (isSnowing)
            ShowSnow();
        else
            HideSnow();
    }

    // 강수는 화면 셰이더가 그린다 (#782) — 파티클 리그를 만들지 않는다. 어디에 뿌릴지를 배치로
    // 풀던 예외 3종(#647 기둥·#733 창밖·#734 잔여 입자)이 마스크 한 겹으로 대체됐다.
    // 먹구름(밝기)은 그대로 여기가 쥔다.
    private void ShowSnow()
    {
        Screen?.Show(PrecipitationScreen.EKind.Snow);

        if (!m_overcastPushed)
        {
            m_overcastPushed = true;
            WeatherOvercast.Push(m_overcastIntensityScale, m_overcastFadeSeconds);
        }
    }

    private void HideSnow()
    {
        Screen?.Hide(PrecipitationScreen.EKind.Snow);
        PopOvercast(m_overcastFadeSeconds);
    }

    // 같은 오브젝트(SuddenEvents)에 붙어 있다 — 인스펙터로 물릴 것이 없다.
    private PrecipitationScreen Screen =>
        m_screen != null ? m_screen : m_screen = GetComponent<PrecipitationScreen>();

    private PrecipitationScreen m_screen;
}
