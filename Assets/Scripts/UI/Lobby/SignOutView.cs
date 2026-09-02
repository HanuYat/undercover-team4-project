using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;
using UnityEngine.UI;

/// <summary>
/// 로그아웃 버튼 하나짜리 뷰. 타이틀 화면 하단의 [종료]·[설정] 옆에 나란히 선다. (#247 · #585)
///
/// <b>#585에서 계정 패널이 여기까지 줄었다.</b> 원래 이 클래스는 PlayerId 표시·로그인·로그아웃·
/// 계정 연동·계정 전환을 모두 들고 있는 <c>AuthPanel</c>이었다. 로그인과 회원가입이
/// <see cref="AuthGatePanel"/>로 옮겨 가면서 "이미 로그인해서 들어왔는데 또 아이디를 치라는"
/// 화면이 되어 차례로 걷어냈고, 마지막에 남은 로그아웃마저 창 하나를 띄울 일이 아니라
/// 판단해 접이식 계정 영역과 그것을 여는 [계정] 버튼까지 없앴다.
/// 그래서 <b>PanelBase가 아니다</b> — 열고 닫을 것이 없는 상시 노출 버튼이라
/// <see cref="NicknameView"/>와 같은 평범한 뷰다.
///
/// 관문을 통과하기 전에는 보이면 안 된다. 그 시점의 로그아웃은 자동 익명 로그인을 되돌려
/// 관문의 세 버튼이 모두 잠긴 채 풀리지 않는 막다른 길을 만든다(자동 로그인은 씬 시작 시
/// 한 번뿐이다). 그래서 이 버튼은 <see cref="SessionPanel"/> 아래에 둔다 — 관문을 통과해야
/// 열리는 화면이라 별도의 판정 없이 노출 시점이 맞아떨어진다.
///
/// <b>계정 전환(#444)은 UI에서 도달할 수 없다</b> — <see cref="AuthBootstrap"/>의 API는
/// 그대로 있으니 진입점이 필요해지면(설정 창 등) 거기서 부르면 된다. 다만 설정 창은 4개 씬
/// 전체에 있어 게임 중 계정 변경 진입점이 생기고 Vivox 로그인이 PlayerId에 묶여 있으므로,
/// 그 자리에 두려면 세션 중 잠금을 반드시 확인할 것 (#384 주석 참고).
/// </summary>
public class SignOutView : MonoBehaviour
{
    [Header("UI 참조")]
    [SerializeField]
    private Button m_signOutButton;

    private AuthBootstrap Auth => App.Net.Auth;

    private void OnEnable()
    {
        m_signOutButton.onClick.AddListener(HandleSignOutClicked);

        if (Auth != null)
        {
            Auth.OnSignedIn += Refresh;
            Auth.OnSignedOut += Refresh;
        }

        // 언어를 바꾸면 잠금 상태도 다시 계산한다 — 설정 창이 타이틀 씬에도 있다 (#497)
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

        // 세션에 참가한 채 PlayerId가 바뀌면 로비·Vivox가 옛 ID를 들고 어긋난다 (#384)
        m_signOutButton.interactable = signedIn && !Auth.IsNetworkConnected;
    }
}
