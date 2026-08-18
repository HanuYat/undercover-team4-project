using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 빠르게 움직일 때 화면 가장자리를 좁히는 비네트 — 3D 멀미를 줄이는 장치다. (#665)
/// 멀미를 만드는 것이 주변시로 흘러가는 배경이라, 그 가장자리를 가려 정보를 줄인다.
///
/// 설정에서 끌 수 있다(<see cref="GameSettings.SpeedVignette"/>) — 시야가 좁아지는 것을
/// 싫어하는 사람도 있어서, 켜고 끄는 것이 기본 전제인 연출이다.
///
/// <b>표현만 한다</b> — 속도를 스스로 찾지 않고 <see cref="PlayerMovement"/>가 매 프레임
/// 밀어 넣는다 (<see cref="DamageVignetteUI.UpdateHealthState"/>와 같은 관례).
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class SpeedVignetteUI : CommonManagerBase
{
    [Header("속도 비네트")]
    [Tooltip("화면을 덮는 비네트 이미지 — 가장자리만 어두운 텍스처. 알파는 코드가 구동한다")]
    [SerializeField] private Image m_vignetteImage;

    [Tooltip("비네트가 들어오기 시작하는 속도 비율(0~1) — 걷기 비율(5/8=0.625)보다 커야 걷는 동안 뜨지 않는다")]
    [SerializeField] private float m_startRatio = 0.7f;

    [Tooltip("가장 빠를 때의 알파. 1로 두면 터널을 보게 된다")]
    [SerializeField] private float m_maxAlpha = 0.4f;

    [Tooltip("알파가 목표를 따라가는 속도 — 낮으면 늦게 붙고 높으면 툭툭 끊긴다")]
    [SerializeField] private float m_fadeRate = 5f;

    private float m_alpha;

    protected override void Awake()
    {
        base.Awake(); // App.UI.SpeedVignette 등록
        OverlayImage.SetAlpha(m_vignetteImage, 0f);
    }

    /// <summary>
    /// 지금 속도를 알린다 — 오너의 <see cref="PlayerMovement"/>가 매 프레임 부른다. (#665)
    /// </summary>
    /// <param name="speed">수평 속도(m/s).</param>
    /// <param name="maxSpeed">지금 낼 수 있는 최고 속도 — 버프로 빨라지면 기준도 함께 올라간다.</param>
    public void UpdateSpeed(float speed, float maxSpeed)
    {
        float target = 0f;
        if (GameSettings.SpeedVignette && maxSpeed > 0.01f)
        {
            float ratio = Mathf.Clamp01(speed / maxSpeed);
            target = Mathf.InverseLerp(m_startRatio, 1f, ratio) * m_maxAlpha;
        }

        // 들고 날 때 모두 시간을 쓴다 — 즉발이면 달리기 시작·정지마다 화면이 툭 끊긴다.
        m_alpha = Mathf.Lerp(m_alpha, target, 1f - Mathf.Exp(-m_fadeRate * Time.deltaTime));
        OverlayImage.SetAlpha(m_vignetteImage, m_alpha);
    }

    /// <summary>연출을 즉시 걷어낸다 — 라운드 사이 리셋 지점. (DamageVignetteUI.ClearAll과 같은 관례)</summary>
    public void ClearAll()
    {
        m_alpha = 0f;
        OverlayImage.SetAlpha(m_vignetteImage, 0f);
    }
}
