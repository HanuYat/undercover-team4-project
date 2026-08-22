using TMPro;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;
using UnityEngine.UI;

/// <summary>
/// 개발진 창 — 설정 창 [일반] 탭의 [개발진] 버튼이 연다. 명단 한 덩어리를 띄우고 [닫기]로 닫는다.
/// ESC 스택 패널이라(IsStackable) 설정 창 위에 겹쳐 열려도 ESC가 개발진 → 설정 순으로 풀린다.
///
/// 명단은 <see cref="m_roster"/> 하나(Settings.Credits.Roster)에 줄바꿈으로 적혀 있다 —
/// 이름은 늘 같고 역할 이름만 언어를 타므로 사람마다 항목을 두지 않는다.
/// </summary>
public class CreditsPanel : PanelBase
{
    [Header("문구")]
    [SerializeField]
    private TMP_Text m_rosterText;

    [Tooltip("개발진 명단 — Settings.Credits.Roster (줄바꿈으로 한 사람씩)")]
    [SerializeField]
    private LocalizedString m_roster;

    [Header("버튼")]
    [SerializeField]
    private Button m_closeButton;

    public override bool CanCloseWithESC => true;
    public override bool IsStackable => true;

    protected override void Awake()
    {
        base.Awake();

        if (m_closeButton != null)
            m_closeButton.onClick.AddListener(ClosePanel);

        // 코드가 넣은 문구라 저절로 갱신되지 않는다 — 창을 열어둔 채 언어가 바뀌어도 따라오게 구독한다.
        LocalizationSettings.SelectedLocaleChanged += HandleLocaleChanged;
    }

    protected override void OnDestroy()
    {
        if (m_closeButton != null)
            m_closeButton.onClick.RemoveListener(ClosePanel);

        // 종료 중에는 설정 에셋을 되살리지 않는다 (SettingsPanel과 같은 관례).
        if (LocalizationSettings.HasSettings)
            LocalizationSettings.SelectedLocaleChanged -= HandleLocaleChanged;

        base.OnDestroy();
    }

    public override void OpenPanel()
    {
        RefreshRoster();
        base.OpenPanel();
    }

    private void HandleLocaleChanged(Locale locale) => RefreshRoster();

    private void RefreshRoster()
    {
        if (m_rosterText == null || m_roster == null || m_roster.IsEmpty)
            return;

        m_rosterText.text = m_roster.GetLocalizedString();
    }
}
