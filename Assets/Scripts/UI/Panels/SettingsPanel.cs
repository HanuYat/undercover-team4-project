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
/// <b>예외 — 창 모드·해상도는 [적용]으로 확정한다 (#796).</b> 잘못 걸면 화면이 깨져 되돌릴 수
/// 없으므로 드롭다운은 고르기만 하고, [적용] 뒤 <see cref="DisplayConfirmPanel"/>이 확인을 받는다.
/// 수직동기화는 화면이 깨지지 않아 다른 토글과 같이 즉시 적용이다.
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
    [SerializeField] private Toggle m_screenShakeToggle; // 화면 흔들림 (#665)
    [SerializeField] private Toggle m_speedVignetteToggle; // 속도 비네트 (#665)

    [Header("그래픽 (#796)")]
    [Tooltip("창 모드 — 항목은 EWindowMode 순서대로 런타임에 채운다. 인스펙터에 항목을 적지 말 것")]
    [SerializeField] private TMP_Dropdown m_windowModeDropdown;

    [Tooltip("해상도 — 항목은 Screen.resolutions를 폭x높이로 묶어 런타임에 채운다")]
    [SerializeField] private TMP_Dropdown m_resolutionDropdown;

    [Tooltip("수직동기화 — 끄면 프레임 상한이 사라진다")]
    [SerializeField] private Toggle m_vSyncToggle;

    [Tooltip("창 모드·해상도 확정 — 누르면 확인창이 뜬다. 고른 값이 지금과 같으면 꺼져 있다")]
    [SerializeField] private Button m_applyButton;

    [Tooltip("창 항목 문구 — Settings.WindowMode.Windowed")]
    [SerializeField] private LocalizedString m_windowModeWindowedLabel;

    [Tooltip("테두리 없는 전체 창 항목 문구 — Settings.WindowMode.Borderless")]
    [SerializeField] private LocalizedString m_windowModeBorderlessLabel;

    [Tooltip("전체화면 항목 문구 — Settings.WindowMode.Fullscreen")]
    [SerializeField] private LocalizedString m_windowModeFullscreenLabel;

    [Header("언어 (#374)")]
    [Tooltip("표시 언어 선택. 항목은 Localization Settings의 로케일 목록에서 런타임에 채운다 — 인스펙터에 항목을 적지 말 것")]
    [SerializeField] private TMP_Dropdown m_languageDropdown;

    [Header("버튼")]
    [SerializeField] private Button m_closeButton; // 닫기
    [SerializeField] private Button m_resetButton; // 기본값 복원

    // 아직 [적용]하지 않은 선택. 화면에 걸린 값은 GameSettings가 들고 있다. (#796)
    private EWindowMode m_pendingWindowMode;
    private Vector2Int m_pendingResolution;

    public override bool CanCloseWithESC => true;
    public override bool IsStackable => true;

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

        if (m_applyButton != null)
            m_applyButton.onClick.AddListener(HandleApplyClicked);

        // 창 모드 항목은 코드가 문구를 넣으므로 LocalizeStringEvent가 붙지 않는다 — 언어가 바뀌면
        // 여기서 다시 채운다 (ShopStand와 같은 방식). 해상도·언어 항목은 언어와 무관하다. (#796)
        LocalizationSettings.SelectedLocaleChanged += HandleLocaleChanged;

        // 음소거는 설정 창 밖(토글 키)에서도 바뀐다 — 창을 열어둔 채 키를 눌러도 체크박스가 따라오게
        // 구독한다. 값의 출처는 여전히 GameSettings 하나이고 여기서는 표시만 맞춘다. (#430)
        GameSettings.OnMicMutedChanged += HandleMicMutedExternally;

        if (m_closeButton != null) m_closeButton.onClick.AddListener(ClosePanel);
        if (m_resetButton != null) m_resetButton.onClick.AddListener(HandleResetClicked);
    }

    protected override void OnDestroy()
    {
        // 창이 열린 채 씬이 넘어가면 ClosePanel을 못 타므로 여기서도 기록한다
        if (IsOpened) GameSettings.Save();

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
        if (m_applyButton != null)
            m_applyButton.onClick.RemoveListener(HandleApplyClicked);

        GameSettings.OnMicMutedChanged -= HandleMicMutedExternally;

        // 종료 중에는 설정 에셋을 되살리지 않는다 — HasSettings로 먼저 확인한다 (ShopStand 관례).
        if (LocalizationSettings.HasSettings)
            LocalizationSettings.SelectedLocaleChanged -= HandleLocaleChanged;

        if (m_closeButton != null)
            m_closeButton.onClick.RemoveListener(ClosePanel);
        if (m_resetButton != null)
            m_resetButton.onClick.RemoveListener(HandleResetClicked);

        base.OnDestroy();
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

        base.OpenPanel();
    }

    public override void ClosePanel()
    {
        GameSettings.Save();    // 디스크 기록
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
        if (m_micMuteToggle != null)
            m_micMuteToggle.SetIsOnWithoutNotify(GameSettings.MicMuted);
        if (m_screenShakeToggle != null)
            m_screenShakeToggle.SetIsOnWithoutNotify(GameSettings.ScreenShake);
        if (m_speedVignetteToggle != null)
            m_speedVignetteToggle.SetIsOnWithoutNotify(GameSettings.SpeedVignette);
        if (m_vSyncToggle != null)
            m_vSyncToggle.SetIsOnWithoutNotify(GameSettings.VSync);

        SyncDisplayDropdowns();
        SyncLanguageDropdown();
        RefreshLabels();
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
    /// 되돌림 뒤에도 불린다 — 확인창이 화면을 되돌리면 여기 표시도 따라와야 한다.
    /// </summary>
    private void SyncDisplayDropdowns()
    {
        m_pendingWindowMode = GameSettings.WindowMode;
        m_pendingResolution = GameSettings.Resolution;

        SyncWindowModeDropdown();
        SyncResolutionDropdown();
        RefreshApplyButton();
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
        m_windowModeDropdown.SetValueWithoutNotify((int)m_pendingWindowMode);
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

            if (resolutions[i] == m_pendingResolution)
                current = i;
        }

        m_resolutionDropdown.ClearOptions();
        m_resolutionDropdown.AddOptions(labels);
        m_resolutionDropdown.SetValueWithoutNotify(current);
        m_resolutionDropdown.RefreshShownValue();
    }

    // 지금 화면과 같은 값을 고른 상태면 누를 것이 없다 — 헛되이 확인창이 뜨지 않게 꺼 둔다.
    private void RefreshApplyButton()
    {
        if (m_applyButton == null)
            return;

        m_applyButton.interactable =
            m_pendingWindowMode != GameSettings.WindowMode
            || m_pendingResolution != GameSettings.Resolution;
    }

    private void HandleWindowModeChanged(int index)
    {
        if (!System.Enum.IsDefined(typeof(EWindowMode), index))
            return;

        m_pendingWindowMode = (EWindowMode)index;
        RefreshApplyButton();
    }

    private void HandleResolutionChanged(int index)
    {
        IReadOnlyList<Vector2Int> resolutions = GameSettings.AvailableResolutions;
        if (index < 0 || index >= resolutions.Count)
            return;

        m_pendingResolution = resolutions[index];
        RefreshApplyButton();
    }

    private void HandleVSyncToggled(bool on) => GameSettings.VSync = on;

    /// <summary>
    /// 고른 창 모드·해상도를 화면에 걸고 확인을 받는다 (#796). 저장은 확인창의 [유지]가 한다.
    /// <b>확인창이 없으면 적용하지 않는다</b> — 되돌릴 길이 없는 채로 화면을 바꾸는 것이
    /// 이 기능에서 가장 나쁜 결과다(프리팹 배선 누락은 콘솔로 드러낸다).
    /// </summary>
    private void HandleApplyClicked()
    {
        if (App.UI.Current == null || !App.UI.Current.TryGetPanel(out DisplayConfirmPanel confirm))
        {
            Debug.LogError(
                $"[{nameof(SettingsPanel)}] 확인창({nameof(DisplayConfirmPanel)})이 없어 화면 설정을 적용하지 않았습니다 — 씬에 배치됐는지 확인하세요.",
                this
            );
            return;
        }

        // 이미 확인을 기다리는 중이면 겹쳐 적용하지 않는다 — 되돌릴 '이전 값'을 잃는다.
        if (confirm.IsOpened)
            return;

        EWindowMode previousMode = GameSettings.WindowMode;
        Vector2Int previousResolution = GameSettings.Resolution;

        GameSettings.ApplyDisplay(m_pendingWindowMode, m_pendingResolution);
        RefreshApplyButton();

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
    private void HandleMicMutedExternally(bool on)
    {
        if (m_micMuteToggle != null)
            m_micMuteToggle.SetIsOnWithoutNotify(on);
    }

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
