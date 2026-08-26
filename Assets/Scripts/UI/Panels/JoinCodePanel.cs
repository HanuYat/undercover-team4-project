using System;
using TMPro;
using UnityEngine;

// 코드 입력 모달 — SessionPanel의 [코드 입력으로 참가] 버튼이 연다. 실제 참가 처리는 SessionPanel이 맡는다.
public class JoinCodePanel : ConfirmPanelBase
{
    [Header("입력")]
    [SerializeField]
    private TMP_InputField m_codeInput;

    private Action<string> m_onConfirm;

    public void Prepare(Action<string> onConfirm) => m_onConfirm = onConfirm;

    public override void OpenPanel()
    {
        if (m_codeInput != null)
            m_codeInput.SetTextWithoutNotify(string.Empty);

        base.OpenPanel();

        if (m_codeInput != null)
            m_codeInput.ActivateInputField();
    }

    public override void ClosePanel()
    {
        m_onConfirm = null;

        if (m_codeInput != null)
            m_codeInput.DeactivateInputField();

        base.ClosePanel();
    }

    protected override void OnConfirm()
    {
        Action<string> confirmed = m_onConfirm;
        string code = m_codeInput != null ? m_codeInput.text.Trim() : string.Empty;

        ClosePanel();
        confirmed?.Invoke(code);
    }
}
