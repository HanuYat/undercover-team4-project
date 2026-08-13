using System;
using TMPro;
using UnityEngine;

/// <summary>
/// 계정 조작 확인창 (#444) — AuthGatePanel의 회원가입(익명 → 정식 승격)이 띄운다.
/// UGS는 아이디/비번 제거를 지원하지 않아 연동은 편도 결정이다 (account-link.md 결정 (e)).
/// 되돌릴 수 없다는 사실과 **실제로 보낼 아이디**를 확정 전에 보여주는 것이 이 창의 전부다 —
/// 연동·전환 자체는 다시 구현하지 않고 넘겨받은 콜백을 부른다 (LeaveConfirmPanel과 같은 관례).
/// 문구를 인수로 받아 연동 확인과 전환 확인 둘 다 이 하나로 쓴다.
/// Title의 ESC 진입 메뉴는 QuitConfirmPanel이므로 IsEscMenu는 켜지 않는다 — 씬당 하나만 허용된다.
/// </summary>
public class AccountConfirmPanel : ConfirmPanelBase
{
    [Header("문구")]
    [SerializeField]
    private TMP_Text m_messageText;

    private Action m_onConfirm;

    /// <summary>열기 **전에** 문구와 확정 시 실행할 동작을 넘긴다.</summary>
    public void Prepare(string message, Action onConfirm)
    {
        m_onConfirm = onConfirm;
        if (m_messageText != null)
            m_messageText.text = message;
    }

    public override void ClosePanel()
    {
        m_onConfirm = null; // 취소(ESC 포함)로 닫혔을 때 콜백이 남지 않게
        base.ClosePanel();
    }

    protected override void OnConfirm()
    {
        var confirmed = m_onConfirm; // ClosePanel이 m_onConfirm을 비우므로 먼저 받아둔다
        ClosePanel();
        confirmed?.Invoke();
    }
}
