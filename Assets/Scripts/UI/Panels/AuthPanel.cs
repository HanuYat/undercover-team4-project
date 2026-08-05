using System;
using Cysharp.Threading.Tasks;
using TMPro;
using Unity.Services.Core;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;
using UnityEngine.UI;

/// <summary>
/// 계정 패널의 상태 줄에 뜨는 결과 문구. 값 이름이 곧 키다 — <c>Title.AuthStatus.</c> + 이름 (#497).
/// 한 라벨의 배타적인 상태들이라 인스펙터에서 고를 것이 없다 — 로비 음성 상태(<c>Lobby.Voice.</c>)와 같은 자리.
/// </summary>
[LocalizedEnum("TitleTable", "Title.AuthStatus.")]
public enum EAuthStatus
{
    Busy = 0,
    LinkSucceeded = 1,
    SignInSucceeded = 2,
    NewAnonymousStarted = 3,
    ConfirmUnavailableForLink = 4, // 확인창이 없어 연동 중단 — 편도 결정을 경고 없이 실행하지 않는다 (#444)
    ConfirmUnavailableForSwitch = 5,
}

/// <summary>
/// 계정 상태 패널 — 우측 상단에 로그인된 PlayerId, 우측 하단에 로그인/로그아웃 버튼. (#247)
/// 익명 로그인 자체는 AuthBootstrap이 씬 시작 시 자동 수행(m_signInOnStart)하고,
/// 이 패널은 상태 표시와 수동 로그인/로그아웃 진입점만 제공한다.
/// 계정 연동(#384)도 여기서만 다룬다 — 설정 창은 4개 씬 전체에 있어 게임 중
/// 계정 변경 진입점이 생기고, Vivox 로그인이 PlayerId에 묶여 있어 그건 곧 버그다.
/// </summary>
public class AuthPanel : PanelBase
{
    public override bool CanCloseWithESC => false;
    public override bool IsStackable => false;
    protected override bool OpenOnAwake => true;

    [Header("UI 참조")]
    [SerializeField]
    private TMP_Text m_playerIdText; // 우측 상단

    [SerializeField]
    private Button m_signInButton; // 우측 하단

    [SerializeField]
    private Button m_signOutButton;

    [Header("닉네임 (#249)")]
    [SerializeField]
    private TMP_InputField m_nicknameInput;

    [SerializeField]
    private Button m_applyNicknameButton;

    [SerializeField]
    private TMP_Text m_nicknameStatusText;

    [Header("계정 연동 (#384)")]
    [SerializeField]
    private TMP_InputField m_usernameInput;

    [SerializeField]
    private TMP_InputField m_passwordInput;

    [SerializeField]
    private Button m_linkButton; // 익명 → 정식 승격

    [SerializeField]
    private Button m_accountSignInButton; // 다른 기기에서 로그인

    [SerializeField]
    private TMP_Text m_accountStatusText;

    [Header("계정 전환 (#444)")]
    [SerializeField]
    private Button m_newAnonymousButton; // 이 기기에서 분리 → 새 익명 계정

    [SerializeField]
    private TMP_Text m_linkWarningText; // 연동 전 상시 경고 (결정 (e))

    private bool m_isApplyingNickname;
    private bool m_isAccountBusy; // 연동/로그인 요청 겹침 방지 래치

    // 문구는 전부 코드가 상태에 따라 고르는 자리라 인스펙터에서 고를 것이 없다 — 규약 키(enum)와
    // 아래 고정 키를 쓴다 (#497 결정 (h)). 확인창 본문 둘도 그 조작에 붙박이라 고를 대상이 아니다.
    private const string k_table = "TitleTable";
    private const string k_playerIdKey = "Title.Auth.PlayerId";
    private const string k_signedOutKey = "Title.Auth.SignedOut";
    private const string k_linkConfirmKey = "Title.Auth.LinkConfirm";
    private const string k_newAnonymousConfirmKey = "Title.Auth.NewAnonymousConfirm";
    private const string k_statusPrefix = "Title.AuthStatus.";

    // 마지막으로 띄운 상태 문구 — 문장이 아니라 키로 들고 있어야 언어가 바뀔 때 다시 읽을 수 있다 (#497)
    private LocalizedMessage m_accountStatus;
    private LocalizedMessage m_nicknameStatus;

    private AuthBootstrap Auth => App.Net.Auth;

    private bool m_isSigningIn; // 로그인 요청 겹침 방지 래치

    /// <summary>
    /// 인증 요청이 진행 중인가 — 두 래치를 **함께** 본다. (#444)
    /// 따로 보면 서로의 창이 열린다: 전환은 중간에 OnSignedOut을 발생시켜 재로그인 대기 중
    /// signedIn=false로 UI를 갱신하므로 [로그인]이 되살아나고, 반대로 로그인 대기 중에는
    /// 계정 로그인 버튼이 열려 있다. 어느 쪽이든 InitializeAndSignInAsync가 두 번 돈다.
    /// </summary>
    private bool AuthBusy => m_isSigningIn || m_isAccountBusy;

    private void OnEnable()
    {
        m_signInButton.onClick.AddListener(HandleSignInClicked);
        m_signOutButton.onClick.AddListener(HandleSignOutClicked);
        m_applyNicknameButton.onClick.AddListener(HandleApplyNicknameClicked);
        m_linkButton.onClick.AddListener(HandleLinkClicked);
        m_accountSignInButton.onClick.AddListener(HandleAccountSignInClicked);
        m_newAnonymousButton.onClick.AddListener(HandleNewAnonymousClicked);

        // 상한을 인스펙터에 중복 입력하지 않는다 — 각 규칙의 상수가 단일 출처. (#249 · #384)
        m_nicknameInput.characterLimit = NicknameRules.MaxLength;
        m_usernameInput.characterLimit = AccountCredentials.MaxUsernameLength;
        m_passwordInput.characterLimit = AccountCredentials.MaxPasswordLength;
        m_passwordInput.contentType = TMP_InputField.ContentType.Password;
        m_passwordInput.ForceLabelUpdate();

        if (Auth != null)
        {
            Auth.OnSignedIn += Refresh;
            Auth.OnSignedOut += Refresh;
            Auth.OnNicknameChanged += Refresh;
        }

        // 언어를 바꾸면 이 패널의 문구도 즉시 따라가야 한다 — 설정 창이 타이틀 씬에도 있다.
        // 상태 줄까지 되살리려면 마지막 문구를 키로 들고 있어야 한다(위 m_accountStatus) — 결정 (d). (#497)
        LocalizationSettings.SelectedLocaleChanged += HandleLocaleChanged;

        Refresh();
    }

    private void OnDisable()
    {
        m_signInButton.onClick.RemoveListener(HandleSignInClicked);
        m_signOutButton.onClick.RemoveListener(HandleSignOutClicked);
        m_applyNicknameButton.onClick.RemoveListener(HandleApplyNicknameClicked);
        m_linkButton.onClick.RemoveListener(HandleLinkClicked);
        m_accountSignInButton.onClick.RemoveListener(HandleAccountSignInClicked);
        m_newAnonymousButton.onClick.RemoveListener(HandleNewAnonymousClicked);

        if (Auth != null)
        {
            Auth.OnSignedIn -= Refresh;
            Auth.OnSignedOut -= Refresh;
            Auth.OnNicknameChanged -= Refresh;
        }

        // 종료 중에는 설정 에셋을 되살리지 않는다 — HasSettings로 먼저 확인한다 (ShopStand 관례)
        if (LocalizationSettings.HasSettings)
            LocalizationSettings.SelectedLocaleChanged -= HandleLocaleChanged;
    }

    // 언어가 바뀌면 상태 줄 둘을 다시 읽고 나머지는 Refresh가 다시 채운다
    private void HandleLocaleChanged(Locale locale)
    {
        RenderStatusTexts();
        Refresh();
    }

    private void SetAccountStatus(in LocalizedMessage message)
    {
        m_accountStatus = message;
        RenderStatusTexts();
    }

    private void SetNicknameStatus(in LocalizedMessage message)
    {
        m_nicknameStatus = message;
        RenderStatusTexts();
    }

    private void RenderStatusTexts()
    {
        if (m_accountStatusText != null)
            m_accountStatusText.text = m_accountStatus.Resolve();

        if (m_nicknameStatusText != null)
            m_nicknameStatusText.text = m_nicknameStatus.Resolve();
    }

    private static LocalizedMessage Status(EAuthStatus status) =>
        LocalizedMessage.Of(k_table, k_statusPrefix + status);

    private void HandleSignInClicked() => SignInAsync().Forget();

    private async UniTaskVoid SignInAsync()
    {
        if (AuthBusy || Auth == null)
            return;
        m_isSigningIn = true;
        try
        {
            await Auth.InitializeAndSignInAsync();
        }
        finally
        {
            m_isSigningIn = false;
            Refresh();
        }
    }

    // 세션 참가 중·전환 중 로그아웃 거부는 AuthBootstrap.SignOut 내부 가드가 처리한다
    private void HandleSignOutClicked()
    {
        if (Auth != null)
            Auth.SignOut();
        Refresh();
    }

    private void HandleApplyNicknameClicked() => ApplyNicknameAsync().Forget();

    private async UniTaskVoid ApplyNicknameAsync()
    {
        if (m_isApplyingNickname || Auth == null)
            return;

        m_isApplyingNickname = true;
        string attempted = m_nicknameInput.text;
        bool failed = false;
        try
        {
            await Auth.SetPlayerNameAsync(attempted);
            SetNicknameStatus(LocalizedMessage.None);
        }
        catch (LocalizedMessageException ex)
        {
            // 규칙 위반 — 사유가 키로 온다 (#497)
            failed = true;
            SetNicknameStatus(ex.Reason);
        }
        catch (Exception ex)
        {
            // UGS가 준 문구는 우리 테이블에 없다 — 그대로 띄우고 번역 대상에서 뺀다
            failed = true;
            SetNicknameStatus(LocalizedMessage.Literal(ex.Message));
        }
        finally
        {
            m_isApplyingNickname = false;
            Refresh();
            if (failed)
                m_nicknameInput.text = attempted; // 거절 사유를 보며 고칠 수 있게 남긴다
        }
    }

    /// <summary>
    /// 연동은 편도 결정이라 확인창을 거친다 — 되돌릴 수 없음 경고 + 아이디 재확인. (#444)
    /// </summary>
    private void HandleLinkClicked()
    {
        if (AuthBusy || Auth == null)
            return;

        // 형식 위반은 확인창 **전에** 걸러낸다 — "되돌릴 수 없습니다"를 읽고 확정했는데
        // 형식 오류로 실패하면 그 경고가 무슨 뜻이었는지 흐려진다.
        string id = m_usernameInput.text?.Trim() ?? string.Empty;
        string pw = m_passwordInput.text ?? string.Empty;
        EAccountValidation result = AccountCredentials.Validate(id, pw);
        if (result != EAccountValidation.Ok)
        {
            SetAccountStatus(AccountCredentials.Describe(result));
            return;
        }

        if (!TryGetConfirmPanel(out AccountConfirmPanel confirm))
        {
            // 편도 결정을 경고 없이 실행하는 것이 #444가 없애려는 상태다 — 조용히 진행하지 않는다.
            SetAccountStatus(Status(EAuthStatus.ConfirmUnavailableForLink));
            return;
        }

        // 확정된 값을 인수로 넘긴다 — 확인창이 떠 있는 동안 입력이 바뀌어도
        // 사용자가 재확인한 그 아이디가 전송된다.
        confirm.Prepare(
            LocalizedStrings.Get(k_table, k_linkConfirmKey, id),
            () => LinkAsync(id, pw).Forget()
        );
        confirm.OpenPanel();
    }

    /// <summary>익명 → 정식 승격. 성공하면 이 기기 밖에서도 같은 닉네임으로 접속된다. (#384)</summary>
    private async UniTaskVoid LinkAsync(string username, string password)
    {
        if (AuthBusy || Auth == null)
            return;

        m_isAccountBusy = true;
        Refresh(); // 요청 중 버튼 잠금
        try
        {
            await Auth.LinkAccountAsync(username, password);
            SetAccountStatus(Status(EAuthStatus.LinkSucceeded));
            m_passwordInput.text = string.Empty; // 성공했으면 화면에 남겨둘 이유가 없다
        }
        catch (RequestFailedException ex)
        {
            // UGS 응답은 불친절하다 — 실측한 코드로 사유를 가른다 (AuthenticationException도 여기)
            SetAccountStatus(AccountCredentials.DescribeError(ex));
        }
        catch (LocalizedMessageException ex)
        {
            // 형식·상태 위반은 AuthBootstrap이 키로 던진다 (#497)
            SetAccountStatus(ex.Reason);
        }
        catch (Exception ex)
        {
            SetAccountStatus(LocalizedMessage.Literal(ex.Message));
        }
        finally
        {
            m_isAccountBusy = false;
            Refresh();
        }
    }

    private void HandleAccountSignInClicked() => AccountSignInAsync().Forget();

    /// <summary>아이디로 로그인 — 다른 기기(또는 토큰이 지워진 기기)의 경로. (#384)</summary>
    private async UniTaskVoid AccountSignInAsync()
    {
        if (AuthBusy || Auth == null)
            return;

        m_isAccountBusy = true;
        Refresh();
        try
        {
            await Auth.SignInWithAccountAsync(m_usernameInput.text, m_passwordInput.text);
            SetAccountStatus(Status(EAuthStatus.SignInSucceeded));
            m_passwordInput.text = string.Empty;
        }
        catch (RequestFailedException ex)
        {
            SetAccountStatus(AccountCredentials.DescribeError(ex));
        }
        catch (LocalizedMessageException ex)
        {
            SetAccountStatus(ex.Reason);
        }
        catch (Exception ex)
        {
            SetAccountStatus(LocalizedMessage.Literal(ex.Message));
        }
        finally
        {
            m_isAccountBusy = false;
            Refresh();
        }
    }

    /// <summary>
    /// 이 기기에서 계정 분리 — "연동 해제"가 아니다. (#444)
    /// 문구에서도 그렇게 쓰지 않는다: 서버의 계정과 아이디는 남고 이 기기만 떨어져 나온다.
    /// </summary>
    private void HandleNewAnonymousClicked()
    {
        if (AuthBusy || Auth == null)
            return;

        if (!TryGetConfirmPanel(out AccountConfirmPanel confirm))
        {
            SetAccountStatus(Status(EAuthStatus.ConfirmUnavailableForSwitch));
            return;
        }

        confirm.Prepare(
            LocalizedStrings.Get(k_table, k_newAnonymousConfirmKey, Auth.AccountUsername),
            () => StartNewAnonymousAsync().Forget()
        );
        confirm.OpenPanel();
    }

    private async UniTaskVoid StartNewAnonymousAsync()
    {
        if (AuthBusy || Auth == null)
            return;

        m_isAccountBusy = true;
        Refresh();
        try
        {
            await Auth.StartNewAnonymousAccountAsync();
            // 옛 아이디가 입력칸에 남아 있으면 새 익명 계정이 연동된 것처럼 보인다
            m_usernameInput.text = string.Empty;
            m_passwordInput.text = string.Empty;
            SetAccountStatus(Status(EAuthStatus.NewAnonymousStarted));
        }
        catch (LocalizedMessageException ex)
        {
            SetAccountStatus(ex.Reason);
        }
        catch (Exception ex)
        {
            // 재로그인까지 실패하면 로그아웃 상태로 남는다 — 복구는 [로그인] 버튼(위)이 담당한다
            SetAccountStatus(LocalizedMessage.Literal(ex.Message));
        }
        finally
        {
            m_isAccountBusy = false;
            Refresh();
        }
    }

    private static bool TryGetConfirmPanel(out AccountConfirmPanel panel)
    {
        if (App.UI.Current != null && App.UI.Current.TryGetPanel(out panel))
            return true;

        Debug.LogError("[AuthPanel] AccountConfirmPanel이 씬에 없어 계정 조작을 중단했습니다.");
        panel = null;
        return false;
    }

    private void Refresh()
    {
        bool signedIn = Auth != null && Auth.IsSignedIn;
        m_playerIdText.text = signedIn
            ? LocalizedStrings.Get(k_table, k_playerIdKey, Auth.PlayerId)
            : LocalizedStrings.Get(k_table, k_signedOutKey);
        // 요청 중에는 둘 다 잠근다 — 전환(#444)의 중간 로그아웃 상태에서 [로그인]이 눌리면
        // 같은 AuthBootstrap에서 InitializeAndSignInAsync가 두 번 돈다.
        m_signInButton.interactable = !signedIn && !AuthBusy;
        m_signOutButton.interactable = signedIn && !AuthBusy;

        bool canEdit = signedIn && !Auth.IsNetworkConnected && !m_isApplyingNickname && !AuthBusy;
        m_nicknameInput.interactable = canEdit;
        m_applyNicknameButton.interactable = canEdit;

        // 입력 중 덮어쓰지 않음.
        if (!m_nicknameInput.isFocused)
            m_nicknameInput.text = signedIn ? Auth.Nickname : string.Empty;

        // ── 계정 연동 (#384) ──
        bool linked = signedIn && Auth.IsLinked;
        bool canUseAccount = Auth != null && !Auth.IsNetworkConnected && !AuthBusy;

        m_usernameInput.interactable = canUseAccount && !linked;
        m_passwordInput.interactable = canUseAccount;
        // 연동은 미연동 상태에서만. 로그인은 계정을 갈아타는 경로라 연동 여부와 무관하게 열어둔다.
        m_linkButton.interactable = canUseAccount && signedIn && !linked;
        m_accountSignInButton.interactable = canUseAccount;

        // ── 계정 전환 (#444) ──
        // 전환은 **연동된 계정에서만.** 익명 계정에서 누르면 되찾을 수단 없이 PlayerId·닉네임을
        // 버리는 것이지만, 연동 계정은 아이디로 다시 로그인해 회수할 수 있다.
        m_newAnonymousButton.interactable = canUseAccount && linked;

        // 이미 연동된 뒤에는 경고할 대상이 없다
        if (m_linkWarningText != null)
            m_linkWarningText.gameObject.SetActive(!linked);

        if (!m_usernameInput.isFocused && linked)
            m_usernameInput.text = Auth.AccountUsername;

        // 요청 결과 문장(성공/실패)은 덮어쓰지 않는다 — 다음 조작 때까지 남겨 읽게 한다.
        if (m_isAccountBusy)
            SetAccountStatus(Status(EAuthStatus.Busy));
    }
}
