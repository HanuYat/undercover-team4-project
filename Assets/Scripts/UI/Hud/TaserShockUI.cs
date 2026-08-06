using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 감전 화면 연출 — 테이저에 맞아 기절해 있는 동안 화면이 시안으로 지직거린다. (#477)
/// HUD 프리팹에 부착되며 App.UI.TaserShock으로 접근한다 — <see cref="DamageVignetteUI"/>와 동일 관례.
///
/// <b>저체력 글리치(<see cref="DamageVignetteUI"/>)와 장치는 같고 색·빈도가 다르다.</b>
/// 저체력은 붉고 성기게(0.5~2초 간격) 깜빡여 "몸이 상했다"를 알리고, 감전은 시안이고 훨씬 빠르게
/// (0.03~0.09초) 떨려 "지금 전류가 흐른다"를 알린다. 둘이 동시에 뜰 수 있지만 색이 갈려 구분된다.
///
/// 이 연출의 실질적 가치는 <b>다운과 기절을 화면에서 구분해 주는 것</b>이다 — 모션이 둘 다 같은
/// Knockdown이라(#252), 맞은 사람은 지금까지 "5초 뒤 스스로 일어나는지, 구조를 기다려야 하는지"를
/// 알 수 없었다.
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class TaserShockUI : CommonManagerBase
{
    [Tooltip("화면을 덮는 시안 전기 오버레이 이미지. 알파를 코드가 구동한다")]
    [SerializeField] private Image m_shockImage;

    [Tooltip("가장 강할 때의 알파 — 기절 중에도 주변은 보여야 하므로 낮게 잡는다")]
    [SerializeField] private float m_maxAlpha = 0.45f;

    [Tooltip("플리커의 약한 쪽 알파 배수 — 0이면 완전히 껐다 켜져 너무 거칠다")]
    [SerializeField] private float m_flickerLowScale = 0.35f;

    // 저체력 글리치(0.5~2초)보다 두 자릿수 빠르다 — 이 차이가 두 연출의 성격을 가른다.
    // 60fps 기준 1~3프레임마다 뒤집힌다. 더 빠르게 하면 프레임률에 묻혀 그냥 반투명해 보인다.
    private const float k_flickerMinSeconds = 0.02f;
    private const float k_flickerMaxSeconds = 0.055f;
    private const float k_jitterPixels = 9f;

    private float m_intensity;      // 0 = 꺼짐. 기절이 끝나갈수록 줄어든다
    private float m_nextFlickerTime;
    private bool m_flickerHigh = true;
    private Vector2 m_basePosition;

    protected override void Awake()
    {
        base.Awake(); // App.UI.TaserShock 등록

        if (m_shockImage != null)
            m_basePosition = m_shockImage.rectTransform.anchoredPosition;

        OverlayImage.SetAlpha(m_shockImage, 0f);
    }

    /// <summary>
    /// 감전 강도 갱신 — 오너가 매 프레임 알린다. 0이면 꺼진다. (#477)
    /// </summary>
    /// <param name="intensity">0~1. 기절이 끝나갈수록 낮춰 잦아드는 인상을 만든다.</param>
    public void SetShock(float intensity)
    {
        m_intensity = Mathf.Clamp01(intensity);
    }

    private void Update()
    {
        if (m_shockImage == null)
            return;

        if (m_intensity <= 0.001f)
        {
            if (m_shockImage.gameObject.activeSelf)
            {
                m_shockImage.rectTransform.anchoredPosition = m_basePosition;
                OverlayImage.SetAlpha(m_shockImage, 0f);
            }
            return;
        }

        // 무작위 간격으로 강/약을 오간다 — 켜질 때마다 수평으로 틀어 화면이 지직거리는 인상을 만든다
        // (NpcDespawnGhost의 플리커+지터와 같은 장치, #310).
        if (Time.time >= m_nextFlickerTime)
        {
            m_flickerHigh = !m_flickerHigh;
            m_nextFlickerTime = Time.time + Random.Range(k_flickerMinSeconds, k_flickerMaxSeconds);
            m_shockImage.rectTransform.anchoredPosition = m_basePosition
                + new Vector2(Random.Range(-k_jitterPixels, k_jitterPixels) * m_intensity, 0f);
        }

        float alpha = m_maxAlpha * m_intensity * (m_flickerHigh ? 1f : m_flickerLowScale);
        OverlayImage.SetAlpha(m_shockImage, alpha);
    }
}
