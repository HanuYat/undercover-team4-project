using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using TMPro;
using Unity.Services.Core;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;
using UnityEngine.UI;

/// <summary>
/// 계정 조작 결과 문구. 값 이름이 곧 키다 — <c>Title.AuthStatus.</c> + 이름 (#497).
/// 한 라벨의 배타적인 상태들이라 인스펙터에서 고를 것이 없다 — 로비 음성 상태(<c>Lobby.Voice.</c>)와 같은 자리.
///
/// <b>#585에서 <c>AuthPanel</c>이 <see cref="SignOutView"/>로 줄어들며 이리로 옮겨 왔다</b> —
/// 이제 쓰는 곳은 <see cref="AuthGatePanel"/> 하나뿐이다.
///
/// <c>NewAnonymousStarted</c>·<c>ConfirmUnavailableForSwitch</c>는 #585에서 계정 전환 UI가
/// 사라지며 쓰이지 않게 됐다. <see cref="AuthBootstrap.StartNewAnonymousAccountAsync"/>는 남아
/// 있으므로(도달 경로만 없다) 값과 문구도 함께 남긴다 — 진입점이 다시 생기면 그대로 쓴다.
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
    SigningIn = 6, // 자동 익명 로그인이 끝나기를 기다리는 중 — 관문의 초기 상태 (#585)
}

/// <summary>
/// 타이틀 첫 화면 — 로그인 관문. (#585)
/// 세 갈래로 갈라진 뒤 통과하면 자기를 닫고 <see cref="SessionPanel"/>을 연다.
///
/// <b>UGS 구조상 "회원가입"은 독립 경로가 아니다</b> — 익명 계정에 자격증명을 붙이는 승격
/// (<see cref="AuthBootstrap.LinkAccountAsync"/>)이다. 새 SignUp으로 처리하면 새 PlayerId가
/// 발급돼 닉네임이 유실된다 (#384). 그래서 [게스트로 시작]은 사실상 통과 버튼이고,
/// 익명 로그인 자체는 AuthBootstrap이 씬 시작 시 자동으로 끝내 둔다.
///
/// 승격은 되돌릴 수 없으므로 <see cref="AccountConfirmPanel"/> 확인창을 거친다 — #444 방침 유지.
///
/// <b>OpenOnAwake를 쓰지 않고, 이 창을 열지 말지는 <see cref="TitleUIManager"/>가 정한다.</b>
/// 이미 통과한 뒤 세션에서 돌아온 경우에는 아예 띄우지 않아야 하는데, 그 판정을 여기 Start에
/// 두면 <b>영원히 실행되지 않는다</b> — PanelBase.Awake가 루트를 비활성화하고 비활성 오브젝트의
/// Start는 호출되지 않기 때문이다(실측: 빈 화면이 떴다). 매니저는 상시 활성이라 그 자리가 맞다.
/// </summary>
public class AuthGatePanel : PanelBase
{
    public override bool CanCloseWithESC => false; // 통과 전에는 닫을 수단이 없다
    public override bool IsStackable => false;

    [Header("계정 입력")]
    [SerializeField]
    private TMP_InputField m_usernameInput;

    [SerializeField]
    private TMP_InputField m_passwordInput;

    [Header("버튼")]
    [SerializeField]
    private Button m_signInButton; // 아이디로 로그인

    [SerializeField]
    private Button m_signUpButton; // 회원가입 = 익명 계정 승격

    [SerializeField]
    private Button m_guestButton; // 게스트로 시작 = 그대로 통과

    [Header("상태 문구")]
    [SerializeField]
    private TMP_Text m_statusText;

    [Header("연출")]
    [Tooltip("관문 배경(Curtain) 페이드 인 — 비우면 페이드 없이 바로 로그인 폼을 보여준다")]
    [SerializeField]
    private CanvasGroup m_curtainCanvasGroup;

    [Tooltip("로그인 폼 — 배경이 페이드 인된 뒤에만 활성화한다")]
    [SerializeField]
    private GameObject m_windowRoot;

    [Tooltip("배경 페이드 인 시간(초)")]
    [SerializeField]
    private float m_curtainFadeSeconds = 0.5f;

    private const string k_table = "TitleTable";
    private const string k_statusPrefix = "Title.AuthStatus.";
    private const string k_linkConfirmKey = "Title.Auth.LinkConfirm";

    // 이미 아이디가 붙어 있을 때의 확인 문구 — 회원가입이 '부계정 만들기'가 되므로 경고가 달라진다.
    // 지금 계정에서 로그아웃된다는 것과, 그 아이디로 돌아올 수 있다는 것을 함께 알린다.
    private const string k_linkConfirmSwitchKey = "Title.Auth.LinkConfirmSwitch";

    // 요청 겹침 방지 래치 — 세 버튼이 모두 같은 AuthBootstrap을 건드린다
    private bool m_isBusy;

    // 마지막으로 띄운 사유. 문장이 아니라 키로 들고 있어야 언어가 바뀔 때 다시 읽을 수 있다 (#497)
    private LocalizedMessage m_status;

    // 지금 떠 있는 것이 진행 문구(로그인 중·처리 중)인가. 진행 문구는 상황이 끝나면 스스로
    // 비워져야 하지만 실패 사유는 다음 조작 때까지 남아야 해서, 지울 대상을 구분해 둔다.
    private bool m_statusIsProgress;

    private AuthBootstrap Auth => App.Net.Auth;

    private void OnEnable()
    {
        m_signInButton.onClick.AddListener(HandleSignInClicked);
        m_signUpButton.onClick.AddListener(HandleSignUpClicked);
        m_guestButton.onClick.AddListener(HandleGuestClicked);

        // 상한을 인스펙터에 중복 입력하지 않는다 — AccountCredentials의 상수가 단일 출처 (#384)
        m_usernameInput.characterLimit = AccountCredentials.MaxUsernameLength;
        m_passwordInput.characterLimit = AccountCredentials.MaxPasswordLength;
        m_passwordInput.contentType = TMP_InputField.ContentType.Password;
        m_passwordInput.ForceLabelUpdate();

        if (Auth != null)
        {
            Auth.OnSignedIn += Refresh;
            Auth.OnSignedOut += Refresh;
            Auth.OnSigningInChanged += Refresh;
        }

        LocalizationSettings.SelectedLocaleChanged += HandleLocaleChanged;
        Refresh();
    }

    private void OnDisable()
    {
        m_signInButton.onClick.RemoveListener(HandleSignInClicked);
        m_signUpButton.onClick.RemoveListener(HandleSignUpClicked);
        m_guestButton.onClick.RemoveListener(HandleGuestClicked);

        if (Auth != null)
        {
            Auth.OnSignedIn -= Refresh;
            Auth.OnSignedOut -= Refresh;
            Auth.OnSigningInChanged -= Refresh;
        }

        // 종료 중에는 설정 에셋을 되살리지 않는다 — HasSettings로 먼저 확인한다 (ShopStand 관례)
        if (LocalizationSettings.HasSettings)
            LocalizationSettings.SelectedLocaleChanged -= HandleLocaleChanged;
    }

    private void HandleLocaleChanged(Locale locale)
    {
        RenderStatus();
        Refresh();
    }

    #region 연출
    // 통과 연출은 커튼과 로그인 폼을 한 덩어리로 걷어내야 해서 루트 CanvasGroup이 필요하다.
    // 디자이너가 만질 값이 아니라 연출 내부 사정이라 인스펙터 배선을 요구하지 않고 여기서 확보한다.
    private CanvasGroup m_rootGroup;

    // 통과 연출이 도는 중인가 — 도중에 관문이 다시 열리면(로그아웃 등) 뒤늦게 닫지 않기 위한 표시.
    private bool m_isPassing;

    protected override void Awake()
    {
        base.Awake(); // m_panelRoot이 여기서 정해진다 (R5)

        if (!m_panelRoot.TryGetComponent(out m_rootGroup))
            m_rootGroup = m_panelRoot.AddComponent<CanvasGroup>();
    }

    // Window를 먼저 숨겨야 같은 프레임에 Curtain과 함께 반짝이지 않는다 — base.OpenPanel()보다 앞.
    public override void OpenPanel()
    {
        m_isPassing = false;
        SetWindowVisible(false);

        // 통과 연출이 남겨 둔 상태를 되돌린다 — 로그아웃으로 다시 열릴 때 투명한 채 열리지 않게.
        m_rootGroup.alpha = 1f;
        m_rootGroup.blocksRaycasts = true;

        base.OpenPanel();
        FadeInCurtainAsync(this.GetCancellationTokenOnDestroy()).Forget();
    }

    // 커튼이 다 차오른 뒤에 로그인 폼을 내놓는다.
    private async UniTaskVoid FadeInCurtainAsync(CancellationToken token)
    {
        await UIFade.ToAsync(m_curtainCanvasGroup, 0f, 1f, m_curtainFadeSeconds, token);
        SetWindowVisible(true);
    }

    private void SetWindowVisible(bool visible)
    {
        if (m_windowRoot != null)
            m_windowRoot.SetActive(visible);
    }
    #endregion

    #region 통과
    /// <summary>관문을 넘긴다 — 다시 오지 않도록 표시하고 세션 화면으로 넘어간다.</summary>
    private void Pass()
    {
        if (Auth != null)
            Auth.MarkAuthGatePassed();

        if (App.UI.Current != null && App.UI.Current.TryGetPanel(out SessionPanel session))
            session.OpenPanel();
        else
            // 넘어갈 화면이 없으면 관문만 닫혀 빈 화면이 남는다 — 조용히 넘기지 않는다
            Debug.LogError("[AuthGatePanel] SessionPanel이 씬에 없어 세션 화면을 열지 못했습니다.", this);

        FadeOutThenCloseAsync(this.GetCancellationTokenOnDestroy()).Forget();
    }

    /// <summary>
    /// 관문 전체(커튼 + 로그인 폼)를 한 덩어리로 걷어 그 아래 준비된 세션 화면을 드러낸다.
    ///
    /// <b>폼만 먼저 끄면 그 순간이 깜박임으로 보인다</b>(실측). 커튼만 페이드하고 폼은 즉시 껐더니
    /// 폼이 사라지는 프레임이 눈에 띄었다 — 둘은 같이 사라져야 한 번의 전환으로 읽힌다.
    /// 세션 화면은 위 <see cref="Pass"/>에서 이미 커튼 아래에 다 켜 두었으므로 덮개만 벗기면 된다.
    /// </summary>
    private async UniTaskVoid FadeOutThenCloseAsync(CancellationToken token)
    {
        m_isPassing = true;

        // 드러나는 도중에 눌러도 아래 버튼이 먹게 한다 — 걷히는 중인 관문이 클릭을 삼키면
        // "눌렀는데 아무 일도 없는" 구간이 생긴다.
        m_rootGroup.blocksRaycasts = false;

        await UIFade.ToAsync(m_rootGroup, 1f, 0f, m_curtainFadeSeconds, token);

        // 도중에 관문이 다시 열렸으면(로그아웃) 그쪽이 주인이다 — 닫지 않는다.
        if (m_isPassing)
            ClosePanel();
    }
    #endregion

    #region 게스트
    private void HandleGuestClicked() => GuestAsync().Forget();

    // 익명 로그인은 AuthBootstrap이 씬 시작 시 이미 끝냈다. 실패했을 때만 여기서 한 번 더 시도한다.
    private async UniTaskVoid GuestAsync()
    {
        if (m_isBusy || Auth == null)
            return;

        if (Auth.IsSignedIn)
        {
            Pass();
            return;
        }

        m_isBusy = true;
        Refresh();
        try
        {
            await Auth.InitializeAndSignInAsync();
            Pass();
        }
        catch (Exception ex)
        {
            SetStatus(LocalizedMessage.Literal(ex.Message));
        }
        finally
        {
            m_isBusy = false;
            Refresh();
        }
    }
    #endregion

    #region 로그인
    private void HandleSignInClicked() => SignInAsync().Forget();

    private async UniTaskVoid SignInAsync()
    {
        if (m_isBusy || Auth == null)
            return;

        m_isBusy = true;
        Refresh();
        try
        {
            await Auth.SignInWithAccountAsync(m_usernameInput.text, m_passwordInput.text);
            m_passwordInput.text = string.Empty; // 성공했으면 화면에 남겨둘 이유가 없다
            SetStatus(Status(EAuthStatus.SignInSucceeded));
            Pass();
        }
        catch (RequestFailedException ex)
        {
            // UGS 응답은 불친절하다 — 실측한 코드로 사유를 가른다 (#384)
            SetStatus(AccountCredentials.DescribeError(ex));
        }
        catch (LocalizedMessageException ex)
        {
            SetStatus(ex.Reason);
        }
        catch (Exception ex)
        {
            SetStatus(LocalizedMessage.Literal(ex.Message));
        }
        finally
        {
            m_isBusy = false;
            Refresh();
        }
    }
    #endregion

    #region 회원가입 (익명 → 정식 승격)
    private void HandleSignUpClicked()
    {
        if (m_isBusy || Auth == null)
            return;

        // 형식 위반은 확인창 **전에** 걸러낸다 — 되돌릴 수 없다는 경고를 읽고 확정했는데
        // 형식 오류로 실패하면 그 경고가 무슨 뜻이었는지 흐려진다. (#444와 같은 순서)
        string id = m_usernameInput.text?.Trim() ?? string.Empty;
        string pw = m_passwordInput.text ?? string.Empty;
        EAccountValidation result = AccountCredentials.Validate(id, pw);
        if (result != EAccountValidation.Ok)
        {
            SetStatus(AccountCredentials.Describe(result));
            return;
        }

        if (!TryGetConfirmPanel(out AccountConfirmPanel confirm))
        {
            SetStatus(Status(EAuthStatus.ConfirmUnavailableForLink));
            return;
        }

        // 확정된 값을 인수로 넘긴다 — 확인창이 떠 있는 동안 입력이 바뀌어도
        // 사용자가 재확인한 그 아이디가 전송된다.
        // 이미 연동돼 있으면 이 가입은 '부계정 만들기'라 경고가 달라진다 (AuthBootstrap.LinkAccountAsync).
        string previous = Auth.AccountUsername;
        string message = string.IsNullOrEmpty(previous)
            ? LocalizedStrings.Get(k_table, k_linkConfirmKey, id)
            : LocalizedStrings.Get(k_table, k_linkConfirmSwitchKey, id, previous);

        confirm.Prepare(message, () => SignUpAsync(id, pw).Forget());
        confirm.OpenPanel();
    }

    private async UniTaskVoid SignUpAsync(string username, string password)
    {
        if (m_isBusy || Auth == null)
            return;

        m_isBusy = true;
        Refresh();
        try
        {
            // 승격시킬 익명 계정이 없으면 여기서 만든다 — 로그아웃하고 돌아온 자리에서도
            // 회원가입이 그대로 되게 한다. 토큰이 남아 있으면 원래 PlayerId로 돌아오므로,
            // 그 계정에 이미 아이디가 붙어 있으면 아래 Link가 부계정 경로로 간다.
            if (!Auth.IsSignedIn)
                await Auth.InitializeAndSignInAsync();

            await Auth.LinkAccountAsync(username, password);
            m_passwordInput.text = string.Empty;
            SetStatus(Status(EAuthStatus.LinkSucceeded));
            Pass();
        }
        catch (RequestFailedException ex)
        {
            SetStatus(AccountCredentials.DescribeError(ex));
        }
        catch (LocalizedMessageException ex)
        {
            SetStatus(ex.Reason);
        }
        catch (Exception ex)
        {
            SetStatus(LocalizedMessage.Literal(ex.Message));
        }
        finally
        {
            m_isBusy = false;
            Refresh();
        }
    }

    private static bool TryGetConfirmPanel(out AccountConfirmPanel panel)
    {
        if (App.UI.Current != null && App.UI.Current.TryGetPanel(out panel))
            return true;

        Debug.LogError("[AuthGatePanel] AccountConfirmPanel이 씬에 없어 회원가입을 중단했습니다.");
        panel = null;
        return false;
    }
    #endregion

    #region 표시
    private static LocalizedMessage Status(EAuthStatus status) =>
        LocalizedMessage.Of(k_table, k_statusPrefix + status);

    /// <summary>결과 문구 — 다음 조작 때까지 남는다.</summary>
    private void SetStatus(in LocalizedMessage message)
    {
        m_status = message;
        m_statusIsProgress = false;
        RenderStatus();
    }

    /// <summary>진행 문구 — 상황이 끝나면 Refresh가 지운다.</summary>
    private void SetProgressStatus(in LocalizedMessage message)
    {
        m_status = message;
        m_statusIsProgress = true;
        RenderStatus();
    }

    private void RenderStatus()
    {
        if (m_statusText != null)
            m_statusText.text = m_status.Resolve();
    }

    private void Refresh()
    {
        bool signedIn = Auth != null && Auth.IsSignedIn;
        bool signingIn = Auth != null && Auth.IsSigningIn;

        // 로그인이 **되는 중**일 때만 잠근다. 그 상태에서 시작하면 InitializeAndSignInAsync가
        // 두 번 도는 창이 열리기 때문이다 (#444와 같은 사유).
        //
        // 처음에는 "로그인 안 됨"까지 잠갔는데, 로그아웃하고 관문으로 돌아오면 세 버튼이 모두
        // 잠긴 채 풀리지 않았다 — 자동 익명 로그인은 앱 시작 시 한 번뿐이라 다시 로그인될 일이
        // 없기 때문이다(실측). [게스트로 시작]과 [로그인]은 스스로 로그인하므로 잠글 이유가 없다.
        bool ready = !signingIn && !m_isBusy;
        m_signInButton.interactable = ready;
        m_guestButton.interactable = ready;

        // 회원가입도 다른 둘과 같이 푼다. 승격시킬 익명 계정이 없으면 SignUpAsync가 먼저 만든다 —
        // 예전에는 여기서 signedIn까지 요구해, 로그아웃하고 관문으로 돌아오면 <b>회원가입만 회색</b>이
        // 됐다. [게스트로 시작]을 먼저 눌러야 풀린다는 것을 화면 어디에서도 알 수 없었다.
        m_signUpButton.interactable = ready;

        m_usernameInput.interactable = ready;
        m_passwordInput.interactable = ready;

        if (m_isBusy)
            SetProgressStatus(Status(EAuthStatus.Busy));
        else if (signingIn)
            SetProgressStatus(Status(EAuthStatus.SigningIn));
        else if (m_statusIsProgress)
            // 자동 익명 로그인이 끝났다. 이걸 빼면 버튼은 풀리는데 문구는 "로그인 중"에
            // 멈춰 있다(실측) — Refresh의 위 두 분기 어디에도 걸리지 않기 때문이다.
            SetStatus(LocalizedMessage.None);
    }
    #endregion
}
