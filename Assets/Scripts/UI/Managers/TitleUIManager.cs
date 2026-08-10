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
        if (auth != null && auth.HasPassedAuthGate)
            OpenPanel<SessionPanel>();
        else
            OpenPanel<AuthGatePanel>();
    }

    private void OnEnable()
    {
        m_quitBtn.onClick.AddListener(QuitGame);
        m_settingsBtn.onClick.AddListener(OpenSettings);
    }

    private void OnDisable()
    {
        m_quitBtn.onClick.RemoveListener(QuitGame);
        m_settingsBtn.onClick.RemoveListener(OpenSettings);
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
