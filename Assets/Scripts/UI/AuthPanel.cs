using TMPro;
using System;
using UnityEngine;
using UnityEngine.UI;
using Cysharp.Threading.Tasks;
using Unity.Services.Core;

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
    [SerializeField] private TMP_Text m_playerIdText; // 우측 상단
    [SerializeField] private Button m_signInButton;   // 우측 하단
    [SerializeField] private Button m_signOutButton;

    [Header("닉네임 (#249)")]
    [SerializeField] private TMP_InputField m_nicknameInput;
    [SerializeField] private Button m_applyNicknameButton;
    [SerializeField] private TMP_Text m_nicknameStatusText;

    [Header("계정 연동 (#384)")]
    [SerializeField] private TMP_InputField m_usernameInput;
    [SerializeField] private TMP_InputField m_passwordInput;
    [SerializeField] private Button m_linkButton;          // 익명 → 정식 승격
    [SerializeField] private Button m_accountSignInButton;  // 다른 기기에서 로그인
    [SerializeField] private TMP_Text m_accountStatusText;

    private bool m_isApplyingNickname;
    private bool m_isAccountBusy; // 연동/로그인 요청 겹침 방지 래치

    private AuthBootstrap Auth => App.Net.Auth;

    private bool m_isSigningIn; // 로그인 요청 겹침 방지 래치

    private void OnEnable()
    {
        m_signInButton.onClick.AddListener(HandleSignInClicked);
        m_signOutButton.onClick.AddListener(HandleSignOutClicked);
        m_applyNicknameButton.onClick.AddListener(HandleApplyNicknameClicked);
        m_linkButton.onClick.AddListener(HandleLinkClicked);
        m_accountSignInButton.onClick.AddListener(HandleAccountSignInClicked);

        // 상한을 인스펙터에 중복 입력하지 않는다 — 각 규칙의 상수가 단일 출처. (#249 · #384)
        m_nicknameInput.characterLimit = AuthBootstrap.MaxNicknameLength;
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

        Refresh();
    }

    private void OnDisable()
    {
        m_signInButton.onClick.RemoveListener(HandleSignInClicked);
        m_signOutButton.onClick.RemoveListener(HandleSignOutClicked);
        m_applyNicknameButton.onClick.RemoveListener(HandleApplyNicknameClicked);
        m_linkButton.onClick.RemoveListener(HandleLinkClicked);
        m_accountSignInButton.onClick.RemoveListener(HandleAccountSignInClicked);

        if (Auth != null)
        {
            Auth.OnSignedIn -= Refresh;
            Auth.OnSignedOut -= Refresh;
            Auth.OnNicknameChanged -= Refresh;
        }
    }

    private void HandleSignInClicked() => SignInAsync().Forget();

    private async UniTaskVoid SignInAsync()
    {
        if (m_isSigningIn || Auth == null)
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
            m_nicknameStatusText.text = string.Empty;
        }
        catch (Exception ex)
        {
            failed = true;
            m_nicknameStatusText.text = ex.Message;
        }
        finally
        {
            m_isApplyingNickname = false;
            Refresh();
            if (failed)
                m_nicknameInput.text = attempted; // 거절 사유를 보며 고칠 수 있게 남긴다
        }
    }

    private void HandleLinkClicked() => LinkAsync().Forget();

    /// <summary>익명 → 정식 승격. 성공하면 이 기기 밖에서도 같은 닉네임으로 접속된다. (#384)</summary>
    private async UniTaskVoid LinkAsync()
    {
        if (m_isAccountBusy || Auth == null)
            return;

        m_isAccountBusy = true;
        Refresh(); // 요청 중 버튼 잠금
        try
        {
            await Auth.LinkAccountAsync(m_usernameInput.text, m_passwordInput.text);
            m_accountStatusText.text = "계정 연동 완료";
            m_passwordInput.text = string.Empty; // 성공했으면 화면에 남겨둘 이유가 없다
        }
        catch (RequestFailedException ex)
        {
            // UGS 응답은 불친절하다 — 실측한 코드로 문장을 가른다 (AuthenticationException도 여기)
            m_accountStatusText.text = AccountCredentials.DescribeError(ex);
        }
        catch (Exception ex)
        {
            // 형식 위반(ArgumentException)·상태 위반(InvalidOperationException)은 메시지가 이미 사용자용
            m_accountStatusText.text = ex.Message;
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
        if (m_isAccountBusy || Auth == null)
            return;

        m_isAccountBusy = true;
        Refresh();
        try
        {
            await Auth.SignInWithAccountAsync(m_usernameInput.text, m_passwordInput.text);
            m_accountStatusText.text = "로그인 완료";
            m_passwordInput.text = string.Empty;
        }
        catch (RequestFailedException ex)
        {
            m_accountStatusText.text = AccountCredentials.DescribeError(ex);
        }
        catch (Exception ex)
        {
            m_accountStatusText.text = ex.Message;
        }
        finally
        {
            m_isAccountBusy = false;
            Refresh();
        }
    }

    private void Refresh()
    {
        bool signedIn = Auth != null && Auth.IsSignedIn;
        m_playerIdText.text = signedIn ? $"ID: {Auth.PlayerId}" : "로그인 안 됨";
        m_signInButton.interactable = !signedIn;
        m_signOutButton.interactable = signedIn;

        bool canEdit = signedIn && !Auth.IsNetworkConnected && !m_isApplyingNickname;
        m_nicknameInput.interactable = canEdit;
        m_applyNicknameButton.interactable = canEdit;

        // 입력 중 덮어쓰지 않음.
        if (!m_nicknameInput.isFocused)
            m_nicknameInput.text = signedIn ? Auth.Nickname : string.Empty;

        // ── 계정 연동 (#384) ──
        bool linked = signedIn && Auth.IsLinked;
        bool canUseAccount = Auth != null && !Auth.IsNetworkConnected && !m_isAccountBusy;

        m_usernameInput.interactable = canUseAccount && !linked;
        m_passwordInput.interactable = canUseAccount;
        // 연동은 미연동 상태에서만. 로그인은 계정을 갈아타는 경로라 연동 여부와 무관하게 열어둔다.
        m_linkButton.interactable = canUseAccount && signedIn && !linked;
        m_accountSignInButton.interactable = canUseAccount;

        if (!m_usernameInput.isFocused && linked)
            m_usernameInput.text = Auth.AccountUsername;

        // 요청 결과 문장(성공/실패)은 덮어쓰지 않는다 — 다음 조작 때까지 남겨 읽게 한다.
        if (m_isAccountBusy)
            m_accountStatusText.text = "처리 중...";
    }
}
