using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Title 씬 UI 매니저 — 패널 등록·ESC 스택은 베이스가 전부 담당. (#247)
/// 첫 화면(로그인 관문 / 세션 화면)을 고르는 것도 여기다 — 아래 Start 주석 참고. (#585)
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.UIManagement)]
public class TitleUIManager : UIManagerBase
{
    [SerializeField]
    private Button m_quitBtn;

    [SerializeField]
    private Button m_settingsBtn;

    [Tooltip("튜토리얼 — 로그인 없이도 눌린다 (#663). 배선하지 않으면 버튼이 없는 것으로 취급한다")]
    [SerializeField]
    private Button m_tutorialBtn;

    /// <summary>
    /// 첫 화면을 고른다 — 관문을 이미 넘었으면 세션 화면으로 바로 간다. (#585)
    ///
    /// <b>이 판정을 패널이 스스로 할 수 없다.</b> 두 패널 모두 OpenOnAwake가 꺼져 있어
    /// <see cref="PanelBase.Awake"/>가 루트를 비활성화하고, <b>비활성 오브젝트에서는 Start가
    /// 아예 실행되지 않는다</b> — 패널 쪽 Start에 두면 영원히 안 불려 빈 화면이 남는다.
    /// 매니저는 상시 활성이고 Start는 모든 Awake(=패널 등록 완료) 뒤라 이 자리가 맞다.
    /// </summary>
    private void Start()
    {
        AuthBootstrap auth = App.Net.Auth;

        // 다른 매니저 구독은 Start에서 — 모든 Awake(=매니저 등록)가 끝난 뒤라야 안전하다 (R6)
        if (auth != null)
            auth.OnSignedOut += ReturnToAuthGate;

        // 이번 실행에서 이미 통과했거나(세션에서 복귀), 지난 실행에서 통과한 기억이 있으면 관문을 건너뛴다. (#585)
        // 단 건너뛰려면 세션 화면이 기댈 로그인이 있어야 한다 — 이미 로그인됐거나 자동 익명 로그인이
        // 진행 중이어야 한다. 그렇지 않으면(자동 로그인 꺼짐 등) 빈 세션 화면이 남으므로 관문을 띄운다.
        bool remembered = auth != null && (auth.HasPassedAuthGate || auth.RememberedAuthGate);
        bool canRestoreSession =
            auth != null && (auth.HasPassedAuthGate || auth.IsSignedIn || auth.IsSigningIn);

        if (remembered && canRestoreSession)
        {
            OpenPanel<SessionPanel>();

            // 기억으로 건너뛴 경우, 자동 익명 로그인이 끝내 실패하면 세션 화면이 "로그인 중"에서
            // 멈춘 채 버튼이 잠긴다 — 그 막다른 길 대신 관문으로 되돌린다. (#585)
            if (!auth.HasPassedAuthGate && !auth.IsSignedIn && auth.IsSigningIn)
                auth.OnSigningInChanged += HandleRestoreSigningChanged;
        }
        else
        {
            OpenPanel<AuthGatePanel>();
        }
    }

    /// <summary>
    /// 기억으로 관문을 건너뛴 뒤 자동 익명 로그인의 결과를 지켜본다. (#585)
    /// 성공했으면 <see cref="SessionPanel"/>이 스스로 버튼을 푸므로 할 일이 없고,
    /// 실패로 로그인 안 된 채 끝났으면 관문으로 되돌린다.
    /// </summary>
    private void HandleRestoreSigningChanged()
    {
        AuthBootstrap auth = App.Net.Auth;
        if (auth == null || auth.IsSigningIn)
            return; // 아직 진행 중 — 끝나면 다시 불린다

        auth.OnSigningInChanged -= HandleRestoreSigningChanged;

        if (!auth.IsSignedIn)
            ReturnToAuthGate();
    }

    // 구독을 Start에서 걸었으므로 해제도 OnDisable이 아니라 여기다 — 짝이 어긋나면 다시 켜질 때
    // 구독이 살아나지 않는다. 매니저는 씬 도중 비활성화되지 않는다(R6).
    protected override void OnDestroy()
    {
        if (App.Net.Auth != null)
        {
            App.Net.Auth.OnSignedOut -= ReturnToAuthGate;
            App.Net.Auth.OnSigningInChanged -= HandleRestoreSigningChanged;
        }

        base.OnDestroy(); // App 등록 해제 (R5)
    }

    private void OnEnable()
    {
        m_quitBtn.onClick.AddListener(QuitGame);
        m_settingsBtn.onClick.AddListener(OpenSettings);

        // 튜토리얼은 UGS를 타지 않으므로 로그인 관문 앞에서도 눌려도 된다 (#663)
        if (m_tutorialBtn != null)
            m_tutorialBtn.onClick.AddListener(TutorialFlow.Enter);
    }

    private void OnDisable()
    {
        m_quitBtn.onClick.RemoveListener(QuitGame);
        m_settingsBtn.onClick.RemoveListener(OpenSettings);

        if (m_tutorialBtn != null)
            m_tutorialBtn.onClick.RemoveListener(TutorialFlow.Enter);
    }

    /// <summary>
    /// 로그아웃하면 관문으로 돌려보낸다. (#585)
    ///
    /// 없으면 세션 화면에 그대로 남는다 — 로그아웃 버튼만 회색이 되고 나머지는 그대로라
    /// "눌러도 아무 일도 없는" 화면이 된다(실측). 실제로는 [세션 생성]·[코드로 참가]가
    /// 여전히 눌리는데 계정이 없어 실패하고, 관문으로 돌아갈 길도 없다.
    ///
    /// 첫 화면을 고르는 것과 같은 판단이라 여기 둔다 — 위 Start와 한 쌍이다.
    /// </summary>
    private void ReturnToAuthGate()
    {
        if (TryGetPanel(out SessionPanel session))
            session.ClosePanel();

        OpenPanel<AuthGatePanel>();
    }

    // 빌드에선 앱 종료, 에디터에선 플레이 모드 종료. (#210 OnGUI 종료 버튼 역할을 정식 메뉴로 부활 — #224)
    // ESC 종료 확인창(QuitConfirmPanel, #326)도 이 단일 경로를 재사용한다.
    public static void QuitGame()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    // 자기 자신이 씬의 UI 매니저라 App을 거칠 필요가 없다 (베이스의 패널 등록부를 그대로 쓴다)
    private void OpenSettings() => OpenPanel<SettingsPanel>();
}
