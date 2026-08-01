using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

/// <summary>
/// 설정 창 (#225) — 마우스 감도 · 마스터 음량 · 음성 음량 · 마이크 음소거(#430).
/// 설계 정본: docs/design/settings-ui.md
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

    [Header("버튼")]
    [SerializeField] private Button m_closeButton; // 닫기
    [SerializeField] private Button m_resetButton; // 기본값 복원

    [SerializeField] private GameObject m_background;

    public override bool CanCloseWithESC => true;
    public override bool IsStackable => true;

    protected override void Awake()
    {
        base.Awake();

        if (m_background != null) m_background.SetActive(false);

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

        if (m_background != null) m_background.SetActive(true);

        base.OpenPanel();
    }

    public override void ClosePanel()
    {
        if (m_background != null) m_background.SetActive(false);

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

        RefreshLabels();
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
