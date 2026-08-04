using Cysharp.Threading.Tasks;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.UI;

/// <summary>
/// 세션 이탈 확인창 (#429) — 로비의 상시 '나가기' 버튼과 인게임·상점의 PausePanel '나가기'가 함께 띄운다 (#441).
/// 이탈 순서를 다시 구현하지 않는다 — SessionFlow.LeaveToMainAsync() 단일 진입점을 부른다
/// (network-lifecycle.md 원칙 2). 스택 패널이라 ESC로 취소된다.
/// ESC 진입 메뉴는 씬당 하나(PausePanel/QuitConfirmPanel)뿐이므로 IsEscMenu는 켜지 않는다.
/// 커서 해제·입력 정지는 부르는 쪽(PausePanel)이 이미 잡고 있어 이 패널은 아무것도 건드리지 않는다.
/// </summary>
public class LeaveConfirmPanel : PanelBase
{
    [Header("문구")]
    [SerializeField]
    private TMP_Text m_messageText;

    // 문구는 코드가 골라 넣으므로 라벨에 LocalizeStringEvent를 붙이지 않는다 —
    // 붙이면 아래 대입과 서로 덮어쓴다. 씬 라벨과 코드 문구를 가르는 기준이 그것이다. (#497)
    [Tooltip("호스트 — Common.LeaveConfirm.MessageHost")]
    [SerializeField]
    private LocalizedString m_messageHost;

    [Tooltip("클라이언트 — Common.LeaveConfirm.MessageClient")]
    [SerializeField]
    private LocalizedString m_messageClient;

    [Header("버튼")]
    [SerializeField]
    private Button m_confirmButton; // 예 — 세션 이탈

    [SerializeField]
    private Button m_cancelButton; // 아니오 — 창 닫기

    [Header("배경 딤 (패널과 함께 켜고 끔)")]
    [SerializeField]
    private GameObject m_background;

    public override bool CanCloseWithESC => true;
    public override bool IsStackable => true;

    // 지금 표시 중인 문구 — 구독 해제 기준. 호스트/클라로 갈리므로 어느 쪽을 걸었는지 들고 있어야 한다.
    private LocalizedString m_boundMessage;

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
        UnbindMessage(); // 확인을 눌러 씬이 넘어가는 경로는 ClosePanel을 타지 않는다

        if (m_confirmButton != null)
            m_confirmButton.onClick.RemoveListener(HandleConfirm);
        if (m_cancelButton != null)
            m_cancelButton.onClick.RemoveListener(ClosePanel);
        base.OnDestroy();
    }

    public override void OpenPanel()
    {
        // 호스트가 나가면 세션이 닫혀 전원이 튕긴다 — 결과가 다르니 문구를 갈라 쓴다
        BindMessage(IsServer ? m_messageHost : m_messageClient);

        // 취소 후 다시 열었을 때 이전 연타 방어가 남아 있지 않게 되돌린다
        if (m_confirmButton != null)
            m_confirmButton.interactable = true;

        if (m_background != null)
            m_background.SetActive(true);

        base.OpenPanel();
    }

    public override void ClosePanel()
    {
        // 닫힌 창이 언어 변경에 반응해 갱신을 돌리지 않게 끊는다 — 다시 열 때 OpenPanel이 건다.
        // OnDisable에 두지 않는 이유는 PanelBase가 m_panelRoot만 토글해서 이 컴포넌트는 계속 활성이기 때문이다.
        UnbindMessage();

        if (m_background != null)
            m_background.SetActive(false);
        base.ClosePanel();
    }

    /// <summary>
    /// 문구를 걸어 준다 — 이전 문구의 구독을 끊고 새 문구를 구독한다.
    /// 한 번 읽어 대입하지 않고 구독하는 이유는, 이 창이 떠 있는 동안 그 위로 설정 창을 겹쳐 열어
    /// 언어를 바꿀 수 있기 때문이다 (#374). 대입만 하면 그때 뜬 문구가 옛 언어로 굳는다.
    /// </summary>
    private void BindMessage(LocalizedString message)
    {
        if (m_messageText == null)
            return;

        if (message == null || message.IsEmpty)
        {
            // 조용히 넘기면 자리표시자(클라 문구)가 그대로 남아 호스트가 틀린 안내를 읽는다
            Debug.LogWarning($"[{nameof(LeaveConfirmPanel)}] 이탈 확인 문구가 연결되지 않았습니다.", this);
            return;
        }

        UnbindMessage();

        m_boundMessage = message;
        m_boundMessage.StringChanged += HandleMessageChanged; // 구독 즉시 현재 언어로 1회 발화
    }

    private void HandleMessageChanged(string localized)
    {
        if (m_messageText != null)
            m_messageText.text = localized;
    }

    private void UnbindMessage()
    {
        if (m_boundMessage == null)
            return;

        m_boundMessage.StringChanged -= HandleMessageChanged;
        m_boundMessage = null;
    }

    private void HandleConfirm()
    {
        // 곧 타이틀로 넘어간다 — 연타를 끊는다 (SessionFlow.s_busy가 이중 방어)
        if (m_confirmButton != null)
            m_confirmButton.interactable = false;
        SessionFlow.LeaveToMainAsync().Forget();
    }
}
