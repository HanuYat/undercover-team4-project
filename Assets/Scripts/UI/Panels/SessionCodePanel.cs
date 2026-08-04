using TMPro;
using UnityEngine;
using UnityEngine.Localization;

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

    // 코드가 대입하는 자리라 라벨에 LocalizeStringEvent를 붙일 수 없다 — 서로 덮어쓴다. (#497)
    // CommonTable에 두는 이유는 이 패널이 Lobby·Shop 두 씬에 걸쳐 있어서다 (문서 §3).
    [Tooltip("세션 코드 표시 — Common.Session.Code ({0}=참가 코드)")]
    [SerializeField]
    private LocalizedString m_codeFormat;

    private SessionManager Session => App.Net.Session;

    private bool m_bound;

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

        Unbind(); // 꺼진 HUD가 언어 변경에 반응하지 않게 — 다시 켜질 때 OnEnable이 건다
    }

    private void HandleSessionJoined(string sessionId) => Refresh();

    /// <summary>
    /// 세션이 있으면 코드를 보이고, 없으면(오프라인·테스트) 아무것도 그리지 않는다.
    /// 한 번 읽어 대입하지 않고 구독하는 이유는 로비에서 설정 창을 열어 언어를 바꿀 수 있기 때문이다 (#374).
    /// </summary>
    private void Refresh()
    {
        if (m_codeText == null)
            return;

        if (Session == null || Session.CurrentSession == null)
        {
            Unbind();
            m_codeText.text = string.Empty;
            return;
        }

        if (m_codeFormat == null || m_codeFormat.IsEmpty)
        {
            Debug.LogWarning("SessionCodePanel: 세션 코드 문구가 연결되지 않았습니다.", this);
            return;
        }

        Unbind();

        // 인자를 먼저 넣어야 구독 시점의 첫 발화부터 코드가 들어간 문장이 나온다
        m_codeFormat.Arguments = new object[] { Session.CurrentSession.Code };
        m_codeFormat.StringChanged += HandleCodeChanged;
        m_bound = true;
    }

    private void HandleCodeChanged(string localized)
    {
        if (m_codeText != null)
            m_codeText.text = localized;
    }

    private void Unbind()
    {
        if (!m_bound)
            return;

        m_codeFormat.StringChanged -= HandleCodeChanged;
        m_bound = false;
    }
}
