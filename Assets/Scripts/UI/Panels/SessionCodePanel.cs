using TMPro;
using UnityEngine;

/// <summary>
/// 세션 코드 HUD — 인게임 좌측 상단에 현재 세션 코드만 표시한다. (#247)
/// 참가자에게 코드를 공유할 수 있게 상시 노출하고, 세션이 없으면(오프라인·테스트) 아무것도 그리지 않는다.
/// OnGUI 디버그 UI를 대체하는 정식 표시.
/// </summary>
public class SessionCodePanel : PanelBase
{
    public override bool CanCloseWithESC => false;
    public override bool IsStackable => false;
    protected override bool OpenOnAwake => true;

    [Header("UI 참조")]
    [SerializeField]
    private TMP_Text m_codeText;

    private SessionManager Session => App.Net.Session;

    private void OnEnable()
    {
        if (Session != null)
        {
            Session.OnSessionJoined += HandleSessionJoined;
            Session.OnSessionLeft += Refresh;
        }

        Refresh();
    }

    private void OnDisable()
    {
        if (Session != null)
        {
            Session.OnSessionJoined -= HandleSessionJoined;
            Session.OnSessionLeft -= Refresh;
        }
    }

    private void HandleSessionJoined(string sessionId) => Refresh();

    private void Refresh()
    {
        m_codeText.text =
            Session != null && Session.CurrentSession != null
                ? $"세션 코드: {Session.CurrentSession.Code}"
                : string.Empty;
    }
}
