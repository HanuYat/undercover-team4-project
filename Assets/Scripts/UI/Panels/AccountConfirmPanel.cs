using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 계정 조작 확인창 (#444) — AuthPanel의 연동/계정 전환이 띄운다.
/// UGS는 아이디/비번 제거를 지원하지 않아 연동은 편도 결정이다 (account-link.md 결정 (e)).
/// 되돌릴 수 없다는 사실과 **실제로 보낼 아이디**를 확정 전에 보여주는 것이 이 창의 전부다 —
/// 연동·전환 자체는 다시 구현하지 않고 넘겨받은 콜백을 부른다 (LeaveConfirmPanel과 같은 관례).
/// 문구를 인수로 받아 연동 확인과 전환 확인 둘 다 이 하나로 쓴다.
/// Title의 ESC 진입 메뉴는 QuitConfirmPanel이므로 IsEscMenu는 켜지 않는다 — 씬당 하나만 허용된다.
/// </summary>
public class AccountConfirmPanel : PanelBase
{
    [Header("문구")]
    [SerializeField]
    private TMP_Text m_messageText;

    [Header("버튼")]
    [SerializeField]
    private Button m_confirmButton; // 예 — 넘겨받은 동작 실행

    [SerializeField]
    private Button m_cancelButton; // 아니오 — 창 닫기

    [Header("배경 딤 (패널과 함께 켜고 끔)")]
    [SerializeField]
    private GameObject m_background;

    public override bool CanCloseWithESC => true;
    public override bool IsStackable => true;

    private Action m_onConfirm;

    protected override void Awake()
    {
        base.Awake();
        if (m_background != null)
            m_background.SetActive(false);
        if (m_confirmButton != null)
            m_confirmButton.onClick.AddListener(HandleConfirm);
        if (m_cancelButton != null)
            m_cancelButton.onClick.AddListener(ClosePanel);
    }

    protected override void OnDestroy()
    {
        if (m_confirmButton != null)
            m_confirmButton.onClick.RemoveListener(HandleConfirm);
        if (m_cancelButton != null)
            m_cancelButton.onClick.RemoveListener(ClosePanel);
        base.OnDestroy();
    }

    /// <summary>열기 **전에** 문구와 확정 시 실행할 동작을 넘긴다.</summary>
    public void Prepare(string message, Action onConfirm)
    {
        m_onConfirm = onConfirm;
        if (m_messageText != null)
            m_messageText.text = message;
    }

    public override void OpenPanel()
    {
        // 취소 후 다시 열었을 때 이전 연타 방어가 남아 있지 않게 되돌린다
        if (m_confirmButton != null)
            m_confirmButton.interactable = true;

        if (m_background != null)
            m_background.SetActive(true);

        base.OpenPanel();
    }

    public override void ClosePanel()
    {
        if (m_background != null)
            m_background.SetActive(false);

        m_onConfirm = null; // 취소(ESC 포함)로 닫혔을 때 콜백이 남지 않게
        base.ClosePanel();
    }

    private void HandleConfirm()
    {
        // 확정은 한 번만 — 곧 네트워크 요청이 나간다
        if (m_confirmButton != null)
            m_confirmButton.interactable = false;

        var confirmed = m_onConfirm; // ClosePanel이 m_onConfirm을 비우므로 먼저 받아둔다
        ClosePanel();
        confirmed?.Invoke();
    }
}
