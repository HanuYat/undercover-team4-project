using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;
using UnityEngine.UI;

/// <summary>
/// 설정 창 (#225) — 마우스 감도 · 시점 스무딩 · 시야각 · 화면 흔들림 · 속도 비네트(#665) ·
/// 마스터 음량 · 음성 음량 · 마이크 음소거(#430) · 언어(#374) · 그래픽(#796).
/// 설계 정본: docs/design/settings-ui.md · 언어는 docs/design/localization.md
///
/// <b>즉시 적용 모델</b> — 저장/취소 버튼이 없다. 슬라이더를 움직이면 그 순간 GameSettings에
/// 반영되고(메모리), 디스크 기록은 창을 닫을 때 한 번만 한다. 되돌리기는 [기본값 복원]이 맡는다.
///
/// <b>창 모드·해상도도 즉시 적용이되, 확인을 받는다 (#796).</b> 고르는 순간 화면에 걸고
/// <see cref="DisplayConfirmPanel"/>이 [유지]를 물어본다 — 확인하지 않으면 이전 값으로 되돌아간다.
/// 잘못 걸면 화면이 깨져 아무것도 누를 수 없으므로, 되돌림은 사람이 아니라 시간이 맡는다.
///
/// ESC 스택 패널이라(IsStackable) 일시정지 위에 겹쳐 열려도 ESC가 설정 → 일시정지 순으로 풀린다.
/// 커서·플레이어 입력 정지는 PausePanel이 이미 대칭으로 처리하므로 여기서 건드리지 않는다.
/// </summary>
public class SettingsPanel : PanelBase
{
    [Header("슬라이더")]
    [SerializeField] private Slider m_mouseSensitivitySlider;

    [Tooltip("시점 스무딩 강도 — 0이면 원시 입력. 멀미가 나면 낮춘다 (#665)")]
    [SerializeField] private Slider m_lookSmoothingSlider;

    [Tooltip("시야각(수직, 도). 좁을수록 멀미가 심해진다 (#665)")]
    [SerializeField] private Slider m_fovSlider;

    [SerializeField] private Slider m_masterVolumeSlider;
    [SerializeField] private Slider m_voiceVolumeSlider;

    [Header("값 표시")]
    [SerializeField] private TextMeshProUGUI m_mouseSensitivityValue;
    [SerializeField] private TextMeshProUGUI m_lookSmoothingValue;
    [SerializeField] private TextMeshProUGUI m_fovValue;
    [SerializeField] private TextMeshProUGUI m_masterVolumeValue;
    [SerializeField] private TextMeshProUGUI m_voiceVolumeValue;

    [Header("토글")]
    [SerializeField] private Toggle m_micMuteToggle; // 마이크 음소거 (#430)

    [Tooltip("마이크 음소거 단축키 안내 — Settings.Label.MicMuteHint ({0}=음소거 키)")]
    [SerializeField] private TextMeshProUGUI m_micMuteHintText;
    [SerializeField] private LocalizedString m_micMuteHintFormat;

    [SerializeField] private Toggle m_screenShakeToggle; // 화면 흔들림 (#665)
    [SerializeField] private Toggle m_speedVignetteToggle; // 속도 비네트 (#665)

    [Header("그래픽 (#796)")]
    [Tooltip("창 모드 — 항목은 EWindowMode 순서대로 런타임에 채운다. 인스펙터에 항목을 적지 말 것")]
    [SerializeField] private TMP_Dropdown m_windowModeDropdown;

    [Tooltip("해상도 — 항목은 Screen.resolutions를 폭x높이로 묶어 런타임에 채운다")]
    [SerializeField] private TMP_Dropdown m_resolutionDropdown;

    [Tooltip("수직동기화 — 끄면 프레임 상한이 사라진다")]
    [SerializeField] private Toggle m_vSyncToggle;

    [Tooltip("창 항목 문구 — Settings.WindowMode.Windowed")]
    [SerializeField] private LocalizedString m_windowModeWindowedLabel;

    [Tooltip("테두리 없는 전체 창 항목 문구 — Settings.WindowMode.Borderless")]
    [SerializeField] private LocalizedString m_windowModeBorderlessLabel;

    [Tooltip("전체화면 항목 문구 — Settings.WindowMode.Fullscreen")]
    [SerializeField] private LocalizedString m_windowModeFullscreenLabel;

    [Header("언어 (#374)")]
    [Tooltip("표시 언어 선택. 항목은 Localization Settings의 로케일 목록에서 런타임에 채운다 — 인스펙터에 항목을 적지 말 것")]
    [SerializeField] private TMP_Dropdown m_languageDropdown;

    [Header("탭 (#796 후속)")]
    [Tooltip("탭 버튼 — 배열 순서가 곧 페이지 순서다 (m_tabPages와 짝을 맞출 것)")]
    [SerializeField] private Button[] m_tabButtons;

    [Tooltip("탭 내용 — m_tabButtons와 같은 순서. 고른 하나만 켜진다")]
    [SerializeField] private GameObject[] m_tabPages;

    [Tooltip("고른 탭에서만 켜지는 그림 묶음 — m_tabButtons와 같은 순서 (#894)")]
    [SerializeField] private GameObject[] m_tabSelectedMarks;

    [Tooltip("고르지 않은 탭에서만 켜지는 그림 묶음 — m_tabButtons와 같은 순서 (#894)")]
    [SerializeField] private GameObject[] m_tabNormalMarks;

    [Tooltip("고른 탭 / 고르지 않은 탭의 글자색 — 고른 탭은 바탕이 밝아지므로 글자가 어두워진다")]
    [SerializeField] private Color m_tabSelectedTextColor = new Color(0.110f, 0.137f, 0.165f);
    [SerializeField] private Color m_tabNormalTextColor = new Color(0.894f, 0.918f, 0.945f);

    [Header("버튼")]
    [SerializeField] private Button m_closeButton; // 닫기
    [SerializeField] private Button m_resetButton; // 기본값 복원

    [Tooltip("개발진 창을 여는 버튼 — [일반] 탭에 있다")]
    [SerializeField] private Button m_creditsButton;

    public override bool CanCloseWithESC => true;
    public override bool IsStackable => true;

    private bool m_micMuteHintBound;

    private VivoxManager Vivox => App.Net.Vivox;

    protected override void Awake()
    {
        base.Awake();

        SetupSlider(
            m_mouseSensitivitySlider,
            GameSettings.k_minMouseSensitivity,
            GameSettings.k_maxMouseSensitivity,
            HandleMouseSensitivityChanged
        );
        SetupSlider(
            m_lookSmoothingSlider,
            GameSettings.k_minLookSmoothing,
            GameSettings.k_maxLookSmoothing,
            HandleLookSmoothingChanged
        );
        SetupSlider(m_fovSlider, GameSettings.k_minFov, GameSettings.k_maxFov, HandleFovChanged);
        SetupSlider(m_masterVolumeSlider, 0f, 1f, HandleMasterVolumeChanged);
        SetupSlider(m_voiceVolumeSlider, 0f, 1f, HandleVoiceVolumeChanged);

        if (m_micMuteToggle != null)
            m_micMuteToggle.onValueChanged.AddListener(HandleMicMuteToggled);

        if (m_screenShakeToggle != null)
            m_screenShakeToggle.onValueChanged.AddListener(HandleScreenShakeToggled);

        if (m_speedVignetteToggle != null)
            m_speedVignetteToggle.onValueChanged.AddListener(HandleSpeedVignetteToggled);

        if (m_languageDropdown != null)
            m_languageDropdown.onValueChanged.AddListener(HandleLanguageChanged);

        if (m_vSyncToggle != null)
            m_vSyncToggle.onValueChanged.AddListener(HandleVSyncToggled);

        if (m_windowModeDropdown != null)
            m_windowModeDropdown.onValueChanged.AddListener(HandleWindowModeChanged);

        if (m_resolutionDropdown != null)
            m_resolutionDropdown.onValueChanged.AddListener(HandleResolutionChanged);

        SetupTabs();

        // 창 모드 항목은 코드가 문구를 넣으므로 LocalizeStringEvent가 붙지 않는다 — 언어가 바뀌면
        // 여기서 다시 채운다 (ShopStand와 같은 방식). 해상도·언어 항목은 언어와 무관하다. (#796)
        LocalizationSettings.SelectedLocaleChanged += HandleLocaleChanged;

        // 음소거는 설정 창 밖(토글 키)에서도 바뀐다 — 창을 열어둔 채 키를 눌러도 체크박스가 따라오게
        // 구독한다. 값의 출처는 여전히 GameSettings 하나이고 여기서는 표시만 맞춘다. (#430)
        GameSettings.OnMicMutedChanged += HandleMicMutedExternally;

        if (m_closeButton != null) m_closeButton.onClick.AddListener(ClosePanel);
        if (m_resetButton != null) m_resetButton.onClick.AddListener(HandleResetClicked);
        if (m_creditsButton != null) m_creditsButton.onClick.AddListener(HandleCreditsClicked);
    }

    protected override void OnDestroy()
    {
        // 창이 열린 채 씬이 넘어가면 ClosePanel을 못 타므로 여기서도 기록한다
        if (IsOpened) GameSettings.Save();

        UnbindMicMuteHint();

        if (m_mouseSensitivitySlider != null)
            m_mouseSensitivitySlider.onValueChanged.RemoveListener(HandleMouseSensitivityChanged);
        if (m_lookSmoothingSlider != null)
            m_lookSmoothingSlider.onValueChanged.RemoveListener(HandleLookSmoothingChanged);
        if (m_fovSlider != null)
            m_fovSlider.onValueChanged.RemoveListener(HandleFovChanged);
        if (m_masterVolumeSlider != null)
            m_masterVolumeSlider.onValueChanged.RemoveListener(HandleMasterVolumeChanged);
        if (m_voiceVolumeSlider != null)
            m_voiceVolumeSlider.onValueChanged.RemoveListener(HandleVoiceVolumeChanged);
        if (m_micMuteToggle != null)
            m_micMuteToggle.onValueChanged.RemoveListener(HandleMicMuteToggled);
        if (m_screenShakeToggle != null)
            m_screenShakeToggle.onValueChanged.RemoveListener(HandleScreenShakeToggled);
        if (m_speedVignetteToggle != null)
            m_speedVignetteToggle.onValueChanged.RemoveListener(HandleSpeedVignetteToggled);
        if (m_languageDropdown != null)
            m_languageDropdown.onValueChanged.RemoveListener(HandleLanguageChanged);
        if (m_vSyncToggle != null)
            m_vSyncToggle.onValueChanged.RemoveListener(HandleVSyncToggled);
        if (m_windowModeDropdown != null)
            m_windowModeDropdown.onValueChanged.RemoveListener(HandleWindowModeChanged);
        if (m_resolutionDropdown != null)
            m_resolutionDropdown.onValueChanged.RemoveListener(HandleResolutionChanged);
        if (m_tabButtons != null)
            foreach (Button tab in m_tabButtons)
                if (tab != null)
                    tab.onClick.RemoveAllListeners();

        GameSettings.OnMicMutedChanged -= HandleMicMutedExternally;

        // 종료 중에는 설정 에셋을 되살리지 않는다 — HasSettings로 먼저 확인한다 (ShopStand 관례).
        if (LocalizationSettings.HasSettings)
            LocalizationSettings.SelectedLocaleChanged -= HandleLocaleChanged;

        if (m_closeButton != null)
            m_closeButton.onClick.RemoveListener(ClosePanel);
        if (m_resetButton != null)
            m_resetButton.onClick.RemoveListener(HandleResetClicked);
        if (m_creditsButton != null)
            m_creditsButton.onClick.RemoveListener(HandleCreditsClicked);

        base.OnDestroy();
    }

    /// <summary>
    /// 탭 버튼에 자기 번호를 물려 배선한다 (#796 후속). 항목이 늘수록 창이 길어져
    /// 12행이 한 화면에 들어가지 않게 된 것이 탭을 넣은 이유다.
    /// </summary>
    private void SetupTabs()
    {
        if (m_tabButtons == null)
            return;

        for (int i = 0; i < m_tabButtons.Length; i++)
        {
            if (m_tabButtons[i] == null)
                continue;

            int index = i; // 반복 변수를 그대로 넘기면 네 버튼이 모두 마지막 번호를 가리킨다
            m_tabButtons[i].onClick.AddListener(() => SelectTab(index));
        }
    }

    /// <summary>그 탭만 켜고 나머지를 끈다. 꺼진 페이지는 레이아웃에서도 빠져 창 높이가 그 탭에 맞는다.</summary>
    private void SelectTab(int index)
    {
        if (m_tabPages != null)
            for (int i = 0; i < m_tabPages.Length; i++)
                if (m_tabPages[i] != null)
                    m_tabPages[i].SetActive(i == index);

        if (m_tabButtons == null)
            return;

        for (int i = 0; i < m_tabButtons.Length; i++)
            ApplyTabVisual(i, i == index);
    }

    // 탭 모양은 그림 두 벌(고름/안 고름)을 갈아 켜서 낸다 — 바탕색만 바꾸면 어느 탭이 열려 있는지
    // 눈에 덜 띈다. 글자색도 함께 뒤집지 않으면 고른 탭에서 밝은 글자가 밝은 바탕에 얹혀 안 읽힌다.
    private void ApplyTabVisual(int index, bool selected)
    {
        SetTabMark(m_tabSelectedMarks, index, selected);
        SetTabMark(m_tabNormalMarks, index, !selected);

        Button tab = m_tabButtons[index];
        if (tab == null)
            return;

        TMP_Text label = tab.GetComponentInChildren<TMP_Text>(true);
        if (label != null)
            label.color = selected ? m_tabSelectedTextColor : m_tabNormalTextColor;
    }

    private static void SetTabMark(GameObject[] marks, int index, bool on)
    {
        if (marks == null || index < 0 || index >= marks.Length || marks[index] == null)
            return;

        marks[index].SetActive(on);
    }

    // SetIsOnWithoutNotify는 onValueChanged를 깨우지 않아 ToggleTint가 색을 따라오지 못한다 — 여기서 같이 맞춘다 (#894).
    private static void SetToggle(Toggle toggle, bool on)
    {
        if (toggle == null)
            return;

        toggle.SetIsOnWithoutNotify(on);

        ToggleTint tint = toggle.GetComponent<ToggleTint>();
        if (tint != null)
            tint.Refresh();
    }

    private static void SetupSlider(Slider slider, float min, float max, UnityAction<float> handler)
    {
        if (slider == null) return;

        slider.minValue = min;
        slider.maxValue = max;
        slider.wholeNumbers = false;
        slider.onValueChanged.AddListener(handler);
    }

    public override void OpenPanel()
    {
        // 열 때마다 현재 설정값으로 맞춘다 — 다른 경로(기본값 복원·다음 실행 로드)로 값이 바뀌어 있을 수 있다
        SyncFromSettings();
        SelectTab(0); // 늘 첫 탭에서 시작한다 — 지난번 자리를 기억하면 어디가 열릴지 예측이 안 된다

        base.OpenPanel();
    }

    public override void ClosePanel()
    {
        GameSettings.Save();    // 디스크 기록
        UnbindMicMuteHint();
        base.ClosePanel();
    }

    /// <summary>
    /// 슬라이더 위치와 토글 상태를 현재 설정값으로 맞춘다.
    ///
    /// <b>SetValueWithoutNotify여야 한다</b> — Slider.value에 대입하면 onValueChanged가 깨어나
    /// 'UI 갱신 → 설정 대입 → UI 갱신' 되돌이가 돈다. 클램프까지 끼면 사용자가 만지지도 않은
    /// 값이 슬라이더로 튀어 올라온다. 토글도 같은 이유로 SetIsOnWithoutNotify를 쓴다 (#430).
    /// </summary>
    private void SyncFromSettings()
    {
        if (m_mouseSensitivitySlider != null)
            m_mouseSensitivitySlider.SetValueWithoutNotify(GameSettings.MouseSensitivity);
        if (m_lookSmoothingSlider != null)
            m_lookSmoothingSlider.SetValueWithoutNotify(GameSettings.LookSmoothing);
        if (m_fovSlider != null)
            m_fovSlider.SetValueWithoutNotify(GameSettings.Fov);
        if (m_masterVolumeSlider != null)
            m_masterVolumeSlider.SetValueWithoutNotify(GameSettings.MasterVolume);
        if (m_voiceVolumeSlider != null)
            m_voiceVolumeSlider.SetValueWithoutNotify(GameSettings.VoiceVolume);
        SetToggle(m_micMuteToggle, GameSettings.MicMuted);
        SetToggle(m_screenShakeToggle, GameSettings.ScreenShake);
        SetToggle(m_speedVignetteToggle, GameSettings.SpeedVignette);
        SetToggle(m_vSyncToggle, GameSettings.VSync);

        SyncDisplayDropdowns();
        SyncLanguageDropdown();
        RefreshLabels();
        RefreshMicMuteHint();
    }

    /// <summary>
    /// 언어 항목을 채우고 현재 언어에 커서를 맞춘다. 항목은 인스펙터가 아니라 여기서 만든다 —
    /// 로케일이 늘면 Localization Settings만 고치면 되게 하려는 것이다.
    ///
    /// 표시는 <b>각 언어의 자기 이름</b>(NativeName)이다. 현재 언어로 번역해 적으면, 읽을 수 없는
    /// 언어에 갇힌 사람이 자기 언어를 찾지 못한다 — 언어 드롭다운은 번역하지 않는 것이 맞다.
    /// </summary>
    private void SyncLanguageDropdown()
    {
        if (m_languageDropdown == null)
            return;

        IList<Locale> locales = GameSettings.AvailableLocales;
        var labels = new List<string>(locales.Count);
        int current = 0;

        for (int i = 0; i < locales.Count; i++)
        {
            Locale locale = locales[i];
            var culture = locale.Identifier.CultureInfo;
            labels.Add(culture != null ? culture.NativeName : locale.LocaleName);

            if (locale == GameSettings.Locale)
                current = i;
        }

        m_languageDropdown.ClearOptions();
        m_languageDropdown.AddOptions(labels);
        // ClearOptions/AddOptions가 값을 0으로 되돌려 놓으므로 커서를 마지막에 맞춘다.
        // 알림 없이 넣는 이유는 슬라이더·토글과 같다 — 표시 갱신이 설정 대입을 깨우면 되돌이가 돈다.
        m_languageDropdown.SetValueWithoutNotify(current);
        m_languageDropdown.RefreshShownValue();
    }

    /// <summary>
    /// 창 모드·해상도 드롭다운을 <b>지금 화면에 걸린 값</b>으로 맞춘다 (#796).
    /// 되돌림 뒤에도, 적용을 거부한 뒤에도 불린다 — 표시가 실제 화면과 어긋나면 안 된다.
    /// </summary>
    private void SyncDisplayDropdowns()
    {
        SyncWindowModeDropdown();
        SyncResolutionDropdown();
    }

    // 항목 순서 = EWindowMode 순서. 문구는 인스펙터에 연결한 LocalizedString에서 온다.
    private void SyncWindowModeDropdown()
    {
        if (m_windowModeDropdown == null)
            return;

        var labels = new List<string>
        {
            WindowModeLabel(m_windowModeWindowedLabel, EWindowMode.Windowed),
            WindowModeLabel(m_windowModeBorderlessLabel, EWindowMode.Borderless),
            WindowModeLabel(m_windowModeFullscreenLabel, EWindowMode.Fullscreen),
        };

        m_windowModeDropdown.ClearOptions();
        m_windowModeDropdown.AddOptions(labels);
        // 언어 드롭다운과 같은 이유로 알림 없이 커서를 맞춘다 — 표시 갱신이 선택 변경을 깨우면 안 된다.
        m_windowModeDropdown.SetValueWithoutNotify((int)GameSettings.WindowMode);
        m_windowModeDropdown.RefreshShownValue();
    }

    // 문구가 비어 있으면 enum 이름이라도 띄운다 — 빈 항목은 원인을 찾기 어렵다.
    private static string WindowModeLabel(LocalizedString text, EWindowMode mode) =>
        text != null && !text.IsEmpty ? text.GetLocalizedString() : mode.ToString();

    // 항목 순서 = GameSettings.AvailableResolutions 순서(중복은 그쪽에서 이미 걷어냈다).
    private void SyncResolutionDropdown()
    {
        if (m_resolutionDropdown == null)
            return;

        IReadOnlyList<Vector2Int> resolutions = GameSettings.AvailableResolutions;
        var labels = new List<string>(resolutions.Count);
        int current = 0;

        for (int i = 0; i < resolutions.Count; i++)
        {
            labels.Add($"{resolutions[i].x} x {resolutions[i].y}");

            if (resolutions[i] == GameSettings.Resolution)
                current = i;
        }

        m_resolutionDropdown.ClearOptions();
        m_resolutionDropdown.AddOptions(labels);
        m_resolutionDropdown.SetValueWithoutNotify(current);
        m_resolutionDropdown.RefreshShownValue();
    }

    private void HandleWindowModeChanged(int index)
    {
        if (!System.Enum.IsDefined(typeof(EWindowMode), index))
            return;

        ApplyDisplay((EWindowMode)index, GameSettings.Resolution);
    }

    private void HandleResolutionChanged(int index)
    {
        IReadOnlyList<Vector2Int> resolutions = GameSettings.AvailableResolutions;
        if (index < 0 || index >= resolutions.Count)
            return;

        ApplyDisplay(GameSettings.WindowMode, resolutions[index]);
    }

    private void HandleVSyncToggled(bool on) => GameSettings.VSync = on;

    /// <summary>
    /// 고른 창 모드·해상도를 화면에 걸고 확인을 받는다 (#796). 저장은 확인창의 [유지]가 한다.
    /// <b>확인창이 없으면 적용하지 않는다</b> — 되돌릴 길이 없는 채로 화면을 바꾸는 것이
    /// 이 기능에서 가장 나쁜 결과다(프리팹 배선 누락은 콘솔로 드러낸다).
    /// 적용하지 않은 경우 드롭다운을 되돌려 놓는다 — 표시가 실제 화면과 어긋나면 안 된다.
    /// </summary>
    private void ApplyDisplay(EWindowMode mode, Vector2Int resolution)
    {
        if (App.UI.Current == null || !App.UI.Current.TryGetPanel(out DisplayConfirmPanel confirm))
        {
            Debug.LogError(
                $"[{nameof(SettingsPanel)}] 확인창({nameof(DisplayConfirmPanel)})이 없어 화면 설정을 적용하지 않았습니다 — 씬에 배치됐는지 확인하세요.",
                this
            );
            SyncDisplayDropdowns();
            return;
        }

        // 확인을 기다리는 중에는 겹쳐 바꾸지 않는다 — 되돌릴 '이전 값'을 잃는다.
        // (확인창이 뒤쪽 설정 창을 가리지 않아 드롭다운을 또 만질 수 있다)
        if (confirm.IsOpened)
        {
            SyncDisplayDropdowns();
            return;
        }

        EWindowMode previousMode = GameSettings.WindowMode;
        Vector2Int previousResolution = GameSettings.Resolution;

        GameSettings.ApplyDisplay(mode, resolution);
        confirm.Begin(previousMode, previousResolution, SyncDisplayDropdowns);
    }

    // 언어가 바뀌면 창 모드 항목 문구를 다시 채운다 — 코드가 넣은 문구라 저절로 갱신되지 않는다. (#796)
    private void HandleLocaleChanged(Locale locale) => SyncWindowModeDropdown();

    private void HandleMouseSensitivityChanged(float value)
    {
        GameSettings.MouseSensitivity = value;
        RefreshLabels();
    }

    private void HandleLookSmoothingChanged(float value)
    {
        GameSettings.LookSmoothing = value;
        RefreshLabels();
    }

    private void HandleFovChanged(float value)
    {
        GameSettings.Fov = value;
        RefreshLabels();
    }

    private void HandleMasterVolumeChanged(float value)
    {
        GameSettings.MasterVolume = value;
        RefreshLabels();
    }

    private void HandleVoiceVolumeChanged(float value)
    {
        GameSettings.VoiceVolume = value;
        RefreshLabels();
    }

    private void HandleMicMuteToggled(bool on) => GameSettings.MicMuted = on;

    private void RefreshMicMuteHint()
    {
        if (m_micMuteHintText == null)
            return;

        if (Vivox == null || m_micMuteHintFormat == null || m_micMuteHintFormat.IsEmpty)
        {
            m_micMuteHintText.text = string.Empty;
            return;
        }

        UnbindMicMuteHint();

        m_micMuteHintFormat.Arguments = new object[] { Vivox.MicMuteBinding };
        m_micMuteHintFormat.StringChanged += HandleMicMuteHintChanged;
        m_micMuteHintBound = true;
    }

    private void HandleMicMuteHintChanged(string localized)
    {
        if (m_micMuteHintText != null)
            m_micMuteHintText.text = localized;
    }

    private void UnbindMicMuteHint()
    {
        if (!m_micMuteHintBound)
            return;

        m_micMuteHintFormat.StringChanged -= HandleMicMuteHintChanged;
        m_micMuteHintBound = false;
    }

    private void HandleScreenShakeToggled(bool on) => GameSettings.ScreenShake = on;

    private void HandleSpeedVignetteToggled(bool on) => GameSettings.SpeedVignette = on;

    // 드롭다운 항목 순서 = GameSettings.AvailableLocales 순서 (SyncLanguageDropdown이 그대로 만든다).
    private void HandleLanguageChanged(int index)
    {
        IList<Locale> locales = GameSettings.AvailableLocales;
        if (index < 0 || index >= locales.Count)
            return;

        GameSettings.Locale = locales[index];
    }

    // 창 밖(토글 키)에서 바뀐 값을 표시에만 반영한다 — SetIsOnWithoutNotify가 아니면 onValueChanged가
    // 깨어나 '표시 갱신 → 설정 대입 → 표시 갱신' 되돌이가 돈다 (슬라이더와 같은 이유). (#430)
    private void HandleMicMutedExternally(bool on) => SetToggle(m_micMuteToggle, on);

    // 개발진 창은 설정 창 위에 겹쳐 열린다 (배치 누락은 UI 매니저가 콘솔로 드러낸다).
    private void HandleCreditsClicked() => App.UI.Current?.OpenPanel<CreditsPanel>();

    private void HandleResetClicked()
    {
        GameSettings.ResetToDefaults();
        SyncFromSettings(); // 슬라이더 위치도 리셋
    }

    private void RefreshLabels()
    {
        if (m_mouseSensitivityValue != null) 
            m_mouseSensitivityValue.text = $"x{GameSettings.MouseSensitivity:0.00}";

        // 0%가 곧 '스무딩 끔'이다 — 음량과 같은 표기라 문구를 따로 두지 않는다.
        if (m_lookSmoothingValue != null)
            m_lookSmoothingValue.text = $"{GameSettings.LookSmoothing * 100f:0}%";

        if (m_fovValue != null)
            m_fovValue.text = $"{GameSettings.Fov:0}°";

        if (m_masterVolumeValue != null)
            m_masterVolumeValue.text = $"{GameSettings.MasterVolume * 100f:0}%";

        if (m_voiceVolumeValue != null)
            m_voiceVolumeValue.text = $"{GameSettings.VoiceVolume * 100f:0}%";
    }
}
