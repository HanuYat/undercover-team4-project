using Cysharp.Threading.Tasks;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 세션 이탈 확인창 (#429) — 로비의 상시 '나가기' 버튼이 띄운다.
/// 이탈 순서를 다시 구현하지 않는다 — SessionFlow.LeaveToMainAsync() 단일 진입점을 부른다
/// (network-lifecycle.md 원칙 2). 스택 패널이라 ESC로 취소된다.
/// 로비의 ESC 진입 메뉴는 PausePanel이므로 IsEscMenu는 켜지 않는다 — 씬당 하나만 허용된다.
/// </summary>
public class LeaveConfirmPanel : PanelBase
{
    [Header("문구")]
    [SerializeField] private TMP_Text m_messageText;

    [Header("버튼")]
    [SerializeField] private Button m_confirmButton; // 예 — 세션 이탈

    [SerializeField] private Button m_cancelButton; // 아니오 — 창 닫기

    [Header("배경 딤 (패널과 함께 켜고 끔)")]
    [SerializeField] private GameObject m_background;

    public override bool CanCloseWithESC => true;
    public override bool IsStackable => true;

    private static bool IsServer =>
        NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;

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

    public override void OpenPanel()
    {
        // 호스트가 나가면 세션이 닫혀 전원이 튕긴다 — 결과가 다르니 문구를 갈라 쓴다
        if (m_messageText != null)
            m_messageText.text = IsServer
                ? "방이 닫히고 전원이 나갑니다.\n나가시겠습니까?"
                : "세션에서 나가시겠습니까?";

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
        base.ClosePanel();
    }

    private void HandleConfirm()
    {
        // 곧 타이틀로 넘어간다 — 연타를 끊는다 (SessionFlow.s_busy가 이중 방어)
        if (m_confirmButton != null)
            m_confirmButton.interactable = false;
        SessionFlow.LeaveToMainAsync().Forget();
    }
}
