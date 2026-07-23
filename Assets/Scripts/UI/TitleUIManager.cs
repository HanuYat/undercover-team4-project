using UnityEngine;
using UnityEngine.UI;

/// <summary>Title 씬 UI 매니저 — 패널 등록·ESC 스택은 베이스가 전부 담당. (#247)</summary>
[DefaultExecutionOrder((int)EExecutionOrder.UIManagement)]
public class TitleUIManager : UIManagerBase
{
    [SerializeField] private Button m_quitBtn;

    private void OnEnable() => m_quitBtn.onClick.AddListener(QuitGame);
    private void OnDisable() => m_quitBtn.onClick.RemoveListener(QuitGame);

    // 빌드에선 앱 종료, 에디터에선 플레이 모드 종료. (#210 OnGUI 종료 버튼 역할을 정식 메뉴로 부활 — #224)
    private static void QuitGame()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }
}
