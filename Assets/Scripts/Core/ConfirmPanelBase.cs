using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 예/아니오 확인창 베이스 (#598 리뷰) — 확인창 셋(QuitConfirm·LeaveConfirm·AccountConfirm)이
/// 버튼 배선과 연타 방어를 각자 갖고 있던 것을 여기로 모았다. 파생 클래스는 "확정하면 무엇을 하는가"
/// (<see cref="OnConfirm"/>)만 답하면 된다.
///
/// 취소는 언제나 닫기다 — 따로 물을 것이 없어 파생에 열어두지 않는다. 다르게 굴어야 하는 창이
/// 생기면 그때 훅을 뺀다.
/// 딤 배경 토글은 <see cref="PanelBase"/>가 맡는다.
/// </summary>
public abstract class ConfirmPanelBase : PanelBase
{
    [Header("버튼")]
    [SerializeField]
    protected Button m_confirmButton; // 예

    [SerializeField]
    protected Button m_cancelButton; // 아니오 — 창 닫기

    // 확인창은 모두 모달이다 — ESC로 취소되고 스택에 쌓인다.
    // ESC 진입 메뉴 여부(IsEscMenu)는 씬당 하나뿐이라 파생이 정한다.
    public override bool CanCloseWithESC => true;
    public override bool IsStackable => true;

    protected override void Awake()
    {
        base.Awake();
        if (m_confirmButton != null)
            m_confirmButton.onClick.AddListener(HandleConfirmClicked);
        if (m_cancelButton != null)
            m_cancelButton.onClick.AddListener(ClosePanel);
    }

    protected override void OnDestroy()
    {
        if (m_confirmButton != null)
            m_confirmButton.onClick.RemoveListener(HandleConfirmClicked);
        if (m_cancelButton != null)
            m_cancelButton.onClick.RemoveListener(ClosePanel);
        base.OnDestroy();
    }

    public override void OpenPanel()
    {
        // 취소 후 다시 열었을 때 이전 연타 방어가 남아 있지 않게 되돌린다
        if (m_confirmButton != null)
            m_confirmButton.interactable = true;

        base.OpenPanel();
    }

    // 확정은 한 번만 — 확인 뒤에는 네트워크 요청이나 씬 전환이 따라온다.
    private void HandleConfirmClicked()
    {
        if (m_confirmButton != null)
            m_confirmButton.interactable = false;

        OnConfirm();
    }

    /// <summary>'예'를 눌렀을 때 할 일. 창을 닫을지는 파생이 정한다 — 씬이 넘어가는 창은 닫을 필요가 없다.</summary>
    protected abstract void OnConfirm();
}
