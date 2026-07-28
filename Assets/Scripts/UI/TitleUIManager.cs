using UnityEngine;
using UnityEngine.UI;

/// <summary>Title 씬 UI 매니저 — 패널 등록·ESC 스택은 베이스가 전부 담당. (#247)</summary>
[DefaultExecutionOrder((int)EExecutionOrder.UIManagement)]
public class TitleUIManager : UIManagerBase
{
    [SerializeField]
    private Button m_quitBtn;

    [SerializeField]
    private Button m_settingsBtn;

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
