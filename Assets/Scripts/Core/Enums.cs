/// <summary>씬 식별자. 실제 씬 이름 매핑은 AppHelper.ToSceneName — 빌드 인덱스에 결합하지 않는다.</summary>
public enum EScene
{
    None,
    Title,      // 로비 — 세션 생성/참가 (3단계에서 신설)
    InGame,     // Main Scene
}

/// <summary>
/// Awake 실행 순서 — [DefaultExecutionOrder((int)EExecutionOrder.X)]로 사용.
/// 음수 = 일반 스크립트(0)보다 먼저. 매니저 → UI매니저 → 패널 → 일반 스크립트 순서를 보장한다.
/// </summary>
public enum EExecutionOrder
{
    Bootstrap = -400,       // AppHelper (DontDestroyOnLoad 루트)
    BaseManagement = -300,  // App 등록 매니저 전부 (Session·Round·SuddenEvent 등)
    UIManagement = -200,    // UIManagerBase 파생 — 패널 등록을 받아야 하므로 패널보다 먼저
    UIPanel = -100,         // PanelBase 파생 — 매니저 뒤, 일반 스크립트 앞
}
