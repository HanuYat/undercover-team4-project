using UnityEngine;
using TMPro;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;
using UnityEngine.UI;

/// <summary>
/// 계정 조작 결과 문구. 값 이름이 곧 키다 — <c>Title.AuthStatus.</c> + 이름 (#497).
/// 한 라벨의 배타적인 상태들이라 인스펙터에서 고를 것이 없다 — 로비 음성 상태(<c>Lobby.Voice.</c>)와 같은 자리.
/// 지금 쓰는 곳은 <see cref="AuthGatePanel"/>이다.
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
/// 계정 정보 패널 — 로그인된 PlayerId 표시와 로그아웃. 세션 화면의 접이식 '계정' 영역이다. (#247 · #585)
///
/// <b>#585에서 대부분이 여기서 빠졌다.</b> 로그인·회원가입(연동)은 타이틀 첫 화면인
/// <see cref="AuthGatePanel"/>이 담당하므로, 같은 입력칸과 버튼을 이 패널에 또 두면
/// "이미 로그인해서 들어왔는데 또 아이디를 치라는" 화면이 된다. 그래서 아이디/비밀번호 입력,
/// 아이디 만들기(연동), 계정 로그인, 익명 로그인, 계정 전환을 모두 걷어내고 로그아웃만 남겼다.
///
/// <b>계정 전환(#444)은 UI에서 도달할 수 없게 됐다</b> — <see cref="AuthBootstrap"/>의 API는
/// 그대로 있으니 진입점이 필요해지면(설정 창 등) 거기서 부르면 된다. 다만 설정 창은 4개 씬
/// 전체에 있어 게임 중 계정 변경 진입점이 생기고 Vivox 로그인이 PlayerId에 묶여 있으므로,
/// 그 자리에 두려면 세션 중 잠금을 반드시 확인할 것 (#384 주석 참고).
/// </summary>
public class AuthPanel : PanelBase
{
    public override bool CanCloseWithESC => true;
    public override bool IsStackable => true;

    [Header("UI 참조")]
    [SerializeField]
    private TMP_Text m_playerIdText;

    [SerializeField]
    private Button m_signOutButton;

    // 문구는 코드가 상태에 따라 고르는 자리라 인스펙터에서 고를 것이 없다 (#497 결정 (h))
    private const string k_table = "TitleTable";
    private const string k_playerIdKey = "Title.Auth.PlayerId";
    private const string k_signedOutKey = "Title.Auth.SignedOut";

    private AuthBootstrap Auth => App.Net.Auth;

    private void OnEnable()
    {
        m_signOutButton.onClick.AddListener(HandleSignOutClicked);

        if (Auth != null)
        {
            Auth.OnSignedIn += Refresh;
            Auth.OnSignedOut += Refresh;
        }

        // 언어를 바꾸면 이 패널의 문구도 즉시 따라가야 한다 — 설정 창이 타이틀 씬에도 있다 (#497)
        LocalizationSettings.SelectedLocaleChanged += HandleLocaleChanged;

        Refresh();
    }

    private void OnDisable()
    {
        m_signOutButton.onClick.RemoveListener(HandleSignOutClicked);

        if (Auth != null)
        {
            Auth.OnSignedIn -= Refresh;
            Auth.OnSignedOut -= Refresh;
        }

        // 종료 중에는 설정 에셋을 되살리지 않는다 — HasSettings로 먼저 확인한다 (ShopStand 관례)
        if (LocalizationSettings.HasSettings)
            LocalizationSettings.SelectedLocaleChanged -= HandleLocaleChanged;
    }

    private void HandleLocaleChanged(Locale locale) => Refresh();

    // 세션 참가 중 로그아웃 거부는 AuthBootstrap.SignOut 내부 가드가 처리한다.
    // 다만 그쪽은 콘솔 경고만 남기므로, 눌리지 않게 아래 Refresh에서 미리 잠근다.
    private void HandleSignOutClicked()
    {
        if (Auth != null)
            Auth.SignOut();

        Refresh();
    }

    private void Refresh()
    {
        bool signedIn = Auth != null && Auth.IsSignedIn;

        m_playerIdText.text = signedIn
            ? LocalizedStrings.Get(k_table, k_playerIdKey, Auth.PlayerId)
            : LocalizedStrings.Get(k_table, k_signedOutKey);

        // 세션에 참가한 채 PlayerId가 바뀌면 로비·Vivox가 옛 ID를 들고 어긋난다 (#384)
        m_signOutButton.interactable = signedIn && !Auth.IsNetworkConnected;
    }
}
