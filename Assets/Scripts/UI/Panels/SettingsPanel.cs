using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Localization;
using UnityEngine.UI;

/// <summary>
/// 설정 창 (#225) — 마우스 감도 · 마스터 음량 · 음성 음량 · 마이크 음소거(#430) · 언어(#374).
/// 설계 정본: docs/design/settings-ui.md · 언어는 docs/design/localization.md
///
/// <b>즉시 적용 모델</b> — 저장/취소 버튼이 없다. 슬라이더를 움직이면 그 순간 GameSettings에
/// 반영되고(메모리), 디스크 기록은 창을 닫을 때 한 번만 한다. 되돌리기는 [기본값 복원]이 맡는다.
///
/// ESC 스택 패널이라(IsStackable) 일시정지 위에 겹쳐 열려도 ESC가 설정 → 일시정지 순으로 풀린다.
/// 커서·플레이어 입력 정지는 PausePanel이 이미 대칭으로 처리하므로 여기서 건드리지 않는다.
/// </summary>
public class SettingsPanel : PanelBase
{
    [Header("슬라이더")]
    [SerializeField] private Slider m_mouseSensitivitySlider;
    [SerializeField] private Slider m_masterVolumeSlider;
    [SerializeField] private Slider m_voiceVolumeSlider;

    [Header("값 표시")]
    [SerializeField] private TextMeshProUGUI m_mouseSensitivityValue;
    [SerializeField] private TextMeshProUGUI m_masterVolumeValue;
    [SerializeField] private TextMeshProUGUI m_voiceVolumeValue;

    [Header("토글")]
    [SerializeField] private Toggle m_micMuteToggle; // 마이크 음소거 (#430)

    [Header("언어 (#374)")]
    [Tooltip("표시 언어 선택. 항목은 Localization Settings의 로케일 목록에서 런타임에 채운다 — 인스펙터에 항목을 적지 말 것")]
    [SerializeField] private TMP_Dropdown m_languageDropdown;

    [Header("버튼")]
    [SerializeField] private Button m_closeButton; // 닫기
    [SerializeField] private Button m_resetButton; // 기본값 복원

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
        SetupSlider(m_masterVolumeSlider, 0f, 1f, HandleMasterVolumeChanged);
        SetupSlider(m_voiceVolumeSlider, 0f, 1f, HandleVoiceVolumeChanged);

        if (m_micMuteToggle != null)
            m_micMuteToggle.onValueChanged.AddListener(HandleMicMuteToggled);

        if (m_languageDropdown != null)
            m_languageDropdown.onValueChanged.AddListener(HandleLanguageChanged);

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
        if (m_masterVolumeSlider != null)
            m_masterVolumeSlider.onValueChanged.RemoveListener(HandleMasterVolumeChanged);
        if (m_voiceVolumeSlider != null)
            m_voiceVolumeSlider.onValueChanged.RemoveListener(HandleVoiceVolumeChanged);
        if (m_micMuteToggle != null)
            m_micMuteToggle.onValueChanged.RemoveListener(HandleMicMuteToggled);
        if (m_languageDropdown != null)
            m_languageDropdown.onValueChanged.RemoveListener(HandleLanguageChanged);

        GameSettings.OnMicMutedChanged -= HandleMicMutedExternally;

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
        if (m_masterVolumeSlider != null)
            m_masterVolumeSlider.SetValueWithoutNotify(GameSettings.MasterVolume);
        if (m_voiceVolumeSlider != null)
            m_voiceVolumeSlider.SetValueWithoutNotify(GameSettings.VoiceVolume);
        if (m_micMuteToggle != null)
            m_micMuteToggle.SetIsOnWithoutNotify(GameSettings.MicMuted);

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

    private void HandleMouseSensitivityChanged(float value)
    {
        GameSettings.MouseSensitivity = value;
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

        if (m_masterVolumeValue != null)
            m_masterVolumeValue.text = $"{GameSettings.MasterVolume * 100f:0}%";

        if (m_voiceVolumeValue != null)
            m_voiceVolumeValue.text = $"{GameSettings.VoiceVolume * 100f:0}%";
    }
}
