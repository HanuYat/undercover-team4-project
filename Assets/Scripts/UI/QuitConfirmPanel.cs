using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Title 씬 ESC 종료 확인창 (#326) — 타이틀엔 이탈할 세션이 없으므로, ESC는 일시정지 대신 게임 종료를 묻는다.
/// 씬의 ESC 진입 메뉴(IsEscMenu)라 스택이 비었을 때 ESC로 열리고 다시 ESC(또는 '아니오')로 닫힌다.
/// </summary>
public class QuitConfirmPanel : PanelBase
{
    [Header("버튼")]
    [SerializeField]
    private Button m_confirmButton; // 예 — 게임 종료

    [SerializeField]
    private Button m_cancelButton; // 아니오 — 창 닫기

    [Header("배경 딤 (패널과 함께 켜고 끔)")]
    [SerializeField]
    private GameObject m_background;

    public override bool CanCloseWithESC => true;
    public override bool IsStackable => true;
    public override bool IsEscMenu => true;

    protected override void Awake()
    {
        base.Awake();
        if (m_background != null)
            m_background.SetActive(false);
        if (m_confirmButton != null)
            m_confirmButton.onClick.AddListener(TitleUIManager.QuitGame);
        if (m_cancelButton != null)
            m_cancelButton.onClick.AddListener(ClosePanel);
    }

    protected override void OnDestroy()
    {
        if (m_confirmButton != null)
            m_confirmButton.onClick.RemoveListener(TitleUIManager.QuitGame);
        if (m_cancelButton != null)
            m_cancelButton.onClick.RemoveListener(ClosePanel);
        base.OnDestroy();
    }

    public override void OpenPanel()
    {
        if (m_background != null)
            m_background.SetActive(true);
        base.OpenPanel();
    }

    public override void ClosePanel()
    {
        if (m_background != null)
            m_background.SetActive(false);
        base.ClosePanel();
    }
}
