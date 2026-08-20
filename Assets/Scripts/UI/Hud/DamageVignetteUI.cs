using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 피격 화면 연출 — 붉은 비네트 · 피격 방향 아크 · 저체력 글리치 · 다운 유예 어두워짐. (#476, #725)
/// HUD 프리팹에 부착되며 App.UI.DamageVignette로 접근한다 — <see cref="CrosshairUI"/>·
/// <see cref="ChannelingGaugeUI"/>와 동일 관례. 네트워크 무관한 로컬 UI다.
///
/// <b>순수 표현 컴포넌트로 유지할 것.</b> "누가 언제 맞았나"는 <see cref="PlayerHitView"/>가 알고,
/// 여기는 화면 좌표계의 일만 한다 — 방향도 월드 좌표가 아니라 <b>이미 환산된 각도</b>로 받는다.
/// 그래야 HUD가 플레이어 오브젝트의 구조(카메라·조준 원점)를 알 필요가 없다.
///
/// 플레이어는 로봇이라 피 대신 <b>시스템 손상</b> 언어를 쓴다 (GDD 7-5 — HP 0은 사망이 아니라 다운).
/// 저체력 글리치는 새 언어가 아니라 <see cref="NpcDespawnGhost"/>(#310)가 확립한 홀로그램
/// 플리커·지터를 화면 오버레이로 옮긴 것이다.
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class DamageVignetteUI : CommonManagerBase
{
    [Header("붉은 비네트")]
    [Tooltip(
        "화면을 덮는 비네트 이미지 — 가장자리만 붉은 텍스처. 알파를 코드가 구동하므로 씬에서는 비활성으로 둬도 된다"
    )]
    [SerializeField]
    private Image m_vignetteImage;

    [Tooltip("피격 후 비네트가 완전히 빠질 때까지의 시간(초)")]
    [SerializeField]
    private float m_vignetteSeconds = 0.4f;

    [Tooltip("가장 강한 피격의 비네트 알파 — 1로 두면 화면이 통째로 붉어져 앞이 안 보인다")]
    [SerializeField]
    private float m_vignetteMaxAlpha = 0.55f;

    // 진압봉 1대(Baton.m_damage 기본 34)에서 최대 강도가 나오도록 잡은 기준값.
    // 참조하지 않고 숫자로 두는 이유: 저건 '그 무기의 위력'이고 이건 '연출이 포화되는 피해량'이라,
    // 무기 밸런스를 만지려고 이 값을 따라가면 NPC 주먹·폭발의 체감까지 함께 흔들린다.
    [Tooltip("이 데미지 이상이면 비네트가 최대 강도로 나온다. 그 미만은 비례해서 옅어진다")]
    [SerializeField]
    private int m_damageForMaxAlpha = 34;

    [Header("피격 방향 아크")]
    [Tooltip(
        "화면 중앙을 축으로 회전시킬 부채꼴 이미지 — 기본 회전(0도)이 화면 위쪽(정면)을 가리키도록 배치할 것"
    )]
    [SerializeField]
    private RectTransform m_directionArc;

    [Tooltip(
        "방향 아크가 표시되는 시간(초) — 비네트보다 길게. '어디서 맞았나'가 비네트보다 오래 필요하다"
    )]
    [SerializeField]
    private float m_arcSeconds = 0.8f;

    [SerializeField]
    private float m_arcMaxAlpha = 0.85f;

    [Header("다운 유예 어두워짐 (#725)")]
    [Tooltip("다운 유예 잔여에 따라 어두워지는 전체 화면 오버레이 — 알파를 코드가 직접 구동한다")]
    [SerializeField]
    private Image m_downDarknessImage;

    [Tooltip("완전히 어두워졌을 때(유예 만료 직전)의 알파 — 1이면 화면이 통째로 캄캄해진다")]
    [SerializeField]
    private float m_downDarknessMaxAlpha = 0.85f;

    [Tooltip("화면 중앙에 크게 뜨는 다운 유예 잔여 초 — 부가 설명 없이 숫자만 (#725)")]
    [SerializeField]
    private TextMeshProUGUI m_downCountdownText;

    [Tooltip("이 초 이하로 남으면 숫자를 경고색으로 바꾼다")]
    [SerializeField]
    private int m_downCountdownWarningSeconds = 10;

    [Tooltip("경고 구간 숫자 색상")]
    [SerializeField]
    private Color m_downCountdownWarningColor = Color.red;

    // 평상시 색 — 프리팹에 설정된 값을 Awake에서 그대로 기억한다(하드코딩 안 함)
    private Color m_downCountdownNormalColor = Color.white;

    [Header("저체력 글리치")]
    [Tooltip(
        "화면 가장자리 스캔라인·노이즈 오버레이 — 순간 연출이 아니라 저체력 동안 상시 표시된다"
    )]
    [SerializeField]
    private Image m_glitchImage;

    [Tooltip("이 체력 비율 이하에서 글리치가 켜진다")]
    [SerializeField]
    private float m_lowHealthThreshold = 0.3f;

    [Tooltip("글리치 상시 알파 — 노이즈 버스트가 아닐 때의 기본값")]
    [SerializeField]
    private float m_glitchBaseAlpha = 0.18f;

    [Tooltip("간헐 노이즈 버스트 시의 알파")]
    [SerializeField]
    private float m_glitchBurstAlpha = 0.5f;

    // 버스트 간격·길이. NpcDespawnGhost의 플리커(0.02~0.07초)보다 훨씬 성기게 잡는다 —
    // 저건 0.45초짜리 소멸 연출이고 이건 저체력 내내 떠 있는 상태 표시라, 같은 빈도면 눈이 아프다.
    private const float k_glitchBurstMinInterval = 0.5f;
    private const float k_glitchBurstMaxInterval = 2f;
    private const float k_glitchBurstMinSeconds = 0.05f;
    private const float k_glitchBurstMaxSeconds = 0.12f;
    private const float k_glitchJitterPixels = 6f;

    private float m_vignetteElapsed = -1f; // 음수 = 진행 중 아님 (PlayerHandView.m_swingTime 관례)
    private float m_vignetteStartAlpha;

    private float m_arcElapsed = -1f;

    private bool m_glitchActive;
    private float m_glitchNextBurstTime;
    private float m_glitchBurstUntil;
    private Vector2 m_glitchBasePosition;

    protected override void Awake()
    {
        base.Awake(); // App.UI.DamageVignette 등록

        if (m_glitchImage != null)
            m_glitchBasePosition = m_glitchImage.rectTransform.anchoredPosition;

        if (m_downCountdownText != null)
            m_downCountdownNormalColor = m_downCountdownText.color;

        OverlayImage.SetAlpha(m_vignetteImage, 0f);
        SetArcAlpha(0f);
        OverlayImage.SetAlpha(m_glitchImage, 0f);
        OverlayImage.SetAlpha(m_downDarknessImage, 0f);
        HideDownCountdown();
    }

    /// <summary>
    /// 다운 유예 어두워짐 — 오너가 매 프레임 잔여 비율을 알린다. (#725)
    /// 페이드 타이머 없이 값을 그대로 반영한다 — 구조 채널링 중에는 잔여 자체가 얼어붙으므로
    /// (PlayerIncapacitation.RemainingUntilDie) 화면도 저절로 멈춘다. 여기서 따로 보간하면
    /// "화면은 밝아지는데 시계는 이미 멈췄다"는 어긋남이 생긴다.
    /// </summary>
    /// <param name="darknessRatio">0(다운 진입 직후) ~ 1(완전 사망 직전).</param>
    public void SetDownDarkness(float darknessRatio)
    {
        OverlayImage.SetAlpha(
            m_downDarknessImage,
            Mathf.Clamp01(darknessRatio) * m_downDarknessMaxAlpha
        );
    }

    /// <summary>다운 유예 잔여 초를 화면 중앙에 큰 숫자로 띄운다 — 부가 설명 없이 숫자만. (#725)</summary>
    public void ShowDownCountdown(int remainingSeconds)
    {
        if (m_downCountdownText == null)
            return;

        m_downCountdownText.text = remainingSeconds.ToString();
        m_downCountdownText.color =
            remainingSeconds <= m_downCountdownWarningSeconds
                ? m_downCountdownWarningColor
                : m_downCountdownNormalColor;
        m_downCountdownText.gameObject.SetActive(true);
    }

    public void HideDownCountdown()
    {
        if (m_downCountdownText != null)
            m_downCountdownText.gameObject.SetActive(false);
    }

    /// <summary>
    /// 피격 연출 — 비네트만 띄운다. 가해자를 모르는 피해(출처 불명 환경 피해)에 쓴다. (#476)
    /// </summary>
    /// <param name="damage">실제로 깎인 체력. 강도(알파)가 여기에 비례한다.</param>
    public void PlayHit(int damage)
    {
        if (damage <= 0)
            return;

        float alpha =
            Mathf.Clamp01((float)damage / Mathf.Max(1, m_damageForMaxAlpha)) * m_vignetteMaxAlpha;

        // 연속 피격이 서로를 약화시키지 않게 한다 — 새 연출을 0부터 다시 그리면 앞선 강한 피격이
        // 진행 중일 때 들어온 약한 피격이 화면을 오히려 옅게 만든다.
        m_vignetteStartAlpha = Mathf.Max(alpha, CurrentVignetteAlpha());
        m_vignetteElapsed = 0f;
    }

    /// <summary>
    /// 피격 연출 — 비네트 + 방향 아크. (#476)
    /// </summary>
    /// <param name="damage">실제로 깎인 체력.</param>
    /// <param name="directionAngleDegrees">
    /// 화면 정면(0도) 기준 가해자 방향. 시계 방향이 양수 — 오른쪽에서 맞으면 +90, 뒤는 ±180이다.
    /// 월드→화면 환산은 호출부(<see cref="PlayerHitView"/>)가 자기 카메라 기준으로 끝내서 넘긴다.
    /// </param>
    public void PlayHit(int damage, float directionAngleDegrees)
    {
        PlayHit(damage);

        if (damage <= 0 || m_directionArc == null)
            return;

        // 아크는 화면 중앙을 축으로 도는 부채꼴 — Z 회전 부호가 UI 좌표계에서 반대라 음수로 넣는다
        // (UI의 +Z 회전은 반시계, 우리 각도 규약은 시계 방향이 양수).
        m_directionArc.localRotation = Quaternion.Euler(0f, 0f, -directionAngleDegrees);
        m_arcElapsed = 0f;
    }

    /// <summary>
    /// 체력 상태 갱신 — 저체력 글리치의 온/오프를 결정한다. 오너가 매 프레임 알린다. (#476)
    /// 판단을 여기서 하는 이유: 임계값이 연출 튜닝값이라 표현 쪽에 있어야 한다.
    /// </summary>
    /// <param name="healthRatio">현재 체력 비율(0~1).</param>
    /// <param name="incapacitated">다운·기절 등 무력화 상태인가 — 그동안은 글리치를 끈다.</param>
    public void UpdateHealthState(float healthRatio, bool incapacitated)
    {
        // 무력화 중에는 끈다 — 쓰러진 상태의 표현(Knockdown 모션·구조 UI)과 화면에서 싸운다.
        // HP 0이면 어차피 비율이 임계값 아래라, 이 게이트가 없으면 다운된 내내 글리치가 떠 있다.
        bool shouldGlitch = !incapacitated && healthRatio <= m_lowHealthThreshold;
        if (shouldGlitch == m_glitchActive)
            return;

        m_glitchActive = shouldGlitch;
        if (!shouldGlitch)
        {
            m_glitchBurstUntil = 0f;
            OverlayImage.SetAlpha(m_glitchImage, 0f);
            if (m_glitchImage != null)
                m_glitchImage.rectTransform.anchoredPosition = m_glitchBasePosition;
            return;
        }

        m_glitchNextBurstTime =
            Time.time + Random.Range(k_glitchBurstMinInterval, k_glitchBurstMaxInterval);
    }

    /// <summary>연출을 즉시 걷어낸다 — 라운드 사이 리셋·오너 교체 지점에서 부른다.</summary>
    public void ClearAll()
    {
        m_vignetteElapsed = -1f;
        m_arcElapsed = -1f;
        UpdateHealthState(1f, false);
        OverlayImage.SetAlpha(m_vignetteImage, 0f);
        SetArcAlpha(0f);
        OverlayImage.SetAlpha(m_downDarknessImage, 0f);
        HideDownCountdown();
    }

    private void Update()
    {
        TickVignette();
        TickArc();
        TickGlitch();
    }

    private void TickVignette()
    {
        if (m_vignetteElapsed < 0f)
            return;

        m_vignetteElapsed += Time.deltaTime;
        if (m_vignetteElapsed >= m_vignetteSeconds)
        {
            m_vignetteElapsed = -1f;
            OverlayImage.SetAlpha(m_vignetteImage, 0f);
            return;
        }

        OverlayImage.SetAlpha(m_vignetteImage, CurrentVignetteAlpha());
    }

    // 들어올 때는 즉발, 빠질 때만 시간을 쓴다 — 페이드 인을 주면 맞은 순간이 흐려진다.
    private float CurrentVignetteAlpha()
    {
        if (m_vignetteElapsed < 0f || m_vignetteSeconds <= 0f)
            return 0f;

        return m_vignetteStartAlpha * (1f - m_vignetteElapsed / m_vignetteSeconds);
    }

    private void TickArc()
    {
        if (m_arcElapsed < 0f)
            return;

        m_arcElapsed += Time.deltaTime;
        if (m_arcElapsed >= m_arcSeconds)
        {
            m_arcElapsed = -1f;
            SetArcAlpha(0f);
            return;
        }

        // 마지막 30%에서만 사라진다 — 방향 정보는 끝까지 또렷해야 읽힌다
        float remaining = 1f - m_arcElapsed / m_arcSeconds;
        SetArcAlpha(m_arcMaxAlpha * Mathf.Clamp01(remaining / 0.3f));
    }

    private void TickGlitch()
    {
        if (!m_glitchActive || m_glitchImage == null)
            return;

        if (Time.time >= m_glitchNextBurstTime)
        {
            m_glitchBurstUntil =
                Time.time + Random.Range(k_glitchBurstMinSeconds, k_glitchBurstMaxSeconds);
            m_glitchNextBurstTime =
                m_glitchBurstUntil
                + Random.Range(k_glitchBurstMinInterval, k_glitchBurstMaxInterval);

            // 버스트마다 수평으로 살짝 튼다 — 화면이 지직거리는 인상 (NpcDespawnGhost의 지터와 같은 장치)
            m_glitchImage.rectTransform.anchoredPosition =
                m_glitchBasePosition
                + new Vector2(Random.Range(-k_glitchJitterPixels, k_glitchJitterPixels), 0f);
        }

        bool bursting = Time.time < m_glitchBurstUntil;
        if (!bursting)
            m_glitchImage.rectTransform.anchoredPosition = m_glitchBasePosition;

        OverlayImage.SetAlpha(m_glitchImage, bursting ? m_glitchBurstAlpha : m_glitchBaseAlpha);
    }

    private void SetArcAlpha(float alpha)
    {
        if (m_directionArc != null && m_directionArc.TryGetComponent(out Image image))
            OverlayImage.SetAlpha(image, alpha);
    }
}
