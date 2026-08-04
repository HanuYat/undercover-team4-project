using System;
using Cysharp.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.UI;

/// <summary>
/// 세션 관문 패널 — Title 씬에서 세션 생성(호스트) / 코드 참가(클라이언트)만 담당한다. (#247)
/// 대기 로비가 아니다: 생성 성공 → TitleManager.StartGame()으로 호스트가 InGame을 열고,
/// 참가 성공 → 서버가 이미 InGame이므로 NGO 씬 동기화가 곧바로 끌고 간다.
/// 대기 공간·게임 시작은 InGame(본부)의 LobbyManager 담당 (#154).
/// </summary>
public class SessionPanel : PanelBase
{
    public override bool CanCloseWithESC => false; // 로비의 기본 화면 — 닫을 수 없다
    public override bool IsStackable => false;
    protected override bool OpenOnAwake => true;

    [Header("UI 참조")]
    [SerializeField]
    private Button m_createButton;

    [SerializeField]
    private Button m_joinButton;

    [SerializeField]
    private TMP_InputField m_codeInput;

    [SerializeField]
    private TMP_Text m_statusText;

    // 상태 문구는 코드가 대입하므로 씬 라벨(LocalizeStringEvent)이 아니라 여기서 테이블을 참조한다 —
    // 컴포넌트를 붙이면 SetStatus의 대입과 서로 덮어쓴다. (#497)
    [Header("상태 문구 (TitleTable)")]
    [Tooltip("익명 로그인 진행 중 — Title.Session.Status.SigningIn")]
    [SerializeField]
    private LocalizedString m_statusSigningIn;

    [Tooltip("로그인 완료 — Title.Session.Status.SignedIn ({0}=플레이어 ID)")]
    [SerializeField]
    private LocalizedString m_statusSignedIn;

    [Tooltip("세션 생성 중 — Title.Session.Status.Creating")]
    [SerializeField]
    private LocalizedString m_statusCreating;

    [Tooltip("세션 생성됨 — Title.Session.Status.Created ({0}=참가 코드)")]
    [SerializeField]
    private LocalizedString m_statusCreated;

    [Tooltip("생성 실패 — Title.Session.Status.CreateFailed ({0}=예외 메시지)")]
    [SerializeField]
    private LocalizedString m_statusCreateFailed;

    [Tooltip("세션 참가 중 — Title.Session.Status.Joining")]
    [SerializeField]
    private LocalizedString m_statusJoining;

    [Tooltip("접속 중 — Title.Session.Status.Connecting")]
    [SerializeField]
    private LocalizedString m_statusConnecting;

    [Tooltip("참가 실패 — Title.Session.Status.JoinFailed ({0}=예외 메시지)")]
    [SerializeField]
    private LocalizedString m_statusJoinFailed;

    private bool m_isBusy; // 생성/참가 요청 겹침 방지 래치 (SessionManager m_isBusy와 같은 방침)

    // 지금 표시 중인 문구 — 구독 해제 기준. 언어를 바꿔도 떠 있는 상태 문구가 따라오게 한다 (#251 관례).
    private LocalizedString m_boundStatus;

    private void OnEnable()
    {
        m_createButton.onClick.AddListener(HandleCreateClicked);
        m_joinButton.onClick.AddListener(HandleJoinClicked);

        // 익명 로그인 완료 전에는 버튼을 잠근다 — 로그인은 AuthBootstrap이 씬 시작 시 자동 수행(m_signInOnStart)
        AuthBootstrap auth = App.Net.Auth;
        if (auth != null && !auth.IsSignedIn)
        {
            SetButtonsInteractable(false);
            SetStatus(m_statusSigningIn);
            auth.OnSignedIn += HandleSignedIn;
        }
    }

    private void OnDisable()
    {
        m_createButton.onClick.RemoveListener(HandleCreateClicked);
        m_joinButton.onClick.RemoveListener(HandleJoinClicked);

        if (App.Net.Auth != null)
            App.Net.Auth.OnSignedIn -= HandleSignedIn;

        UnbindStatus(); // 남은 구독이 나중에 발화해 파괴된 라벨을 건드리지 않게
    }

    private void HandleSignedIn()
    {
        SetButtonsInteractable(true);
        SetStatus(m_statusSignedIn, App.Net.Auth.PlayerId);
    }

    private void SetButtonsInteractable(bool interactable)
    {
        m_createButton.interactable = interactable;
        m_joinButton.interactable = interactable;
    }

    private void HandleCreateClicked() => CreateAsync().Forget();

    private void HandleJoinClicked() => JoinAsync().Forget();

    private async UniTaskVoid CreateAsync()
    {
        if (m_isBusy)
            return;
        m_isBusy = true;
        SetStatus(m_statusCreating);
        try
        {
            string code = await App.Net.Session.CreateSessionAsync();
            SetStatus(m_statusCreated, code);
            App.SceneFlow.Title.StartGame(); // 호스트: 인게임(본부 대기) 진입
        }
        catch (Exception e)
        {
            SetStatus(m_statusCreateFailed, e.Message);
            m_isBusy = false; // 실패 시에만 해제 — 성공하면 씬이 넘어간다
        }
    }

    private async UniTaskVoid JoinAsync()
    {
        if (m_isBusy)
            return;
        m_isBusy = true;
        SetStatus(m_statusJoining);
        try
        {
            await App.Net.Session.JoinByCodeAsync(m_codeInput.text.Trim());
            SetStatus(m_statusConnecting);
            // 씬 전환은 하지 않는다 — 서버 권위. NGO 씬 동기화가 InGame으로 끌고 간다.
        }
        catch (Exception e)
        {
            SetStatus(m_statusJoinFailed, e.Message);
            m_isBusy = false;
        }
    }

    /// <summary>
    /// 상태 문구를 바꾼다 — 이전 문구의 구독을 끊고 새 문구를 구독한다.
    /// 구독하는 이유는 이 화면에서 설정 창을 열어 언어를 바꿀 수 있기 때문이다 (#374).
    /// 한 번만 읽어 대입하면 그때 떠 있던 문구가 옛 언어로 굳는다.
    /// </summary>
    /// <param name="args">Smart String 인자. 인자를 먼저 넣어야 구독 시점의 첫 발화부터 올바른 문장이 나온다.</param>
    private void SetStatus(LocalizedString message, params object[] args)
    {
        if (m_statusText == null || message == null || message.IsEmpty)
            return;

        UnbindStatus();

        // 인자가 없으면 비운다 — 남겨 두면 이전 문구의 인자가 다음 문구에 딸려 간다
        message.Arguments = (args != null && args.Length > 0) ? args : null;

        m_boundStatus = message;
        m_boundStatus.StringChanged += HandleStatusChanged; // 구독 즉시 현재 언어로 1회 발화
    }

    private void HandleStatusChanged(string localized)
    {
        if (m_statusText != null)
            m_statusText.text = localized;
    }

    private void UnbindStatus()
    {
        if (m_boundStatus == null)
            return;

        m_boundStatus.StringChanged -= HandleStatusChanged;
        m_boundStatus = null;
    }
}
