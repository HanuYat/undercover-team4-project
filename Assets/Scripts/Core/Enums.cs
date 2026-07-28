/// <summary>씬 식별자. 실제 씬 이름 매핑은 AppHelper.ToSceneName — 빌드 인덱스에 결합하지 않는다.</summary>
public enum EScene
{
    None,
    Title,      // 메인메뉴 — 세션 생성/참가
    Lobby,      // 최초 대기 화면
    Shop,       // 상점 = 인게임 허브 (라운드 사이 준비, 루프 진입점)
    Game,       // 게임맵 - 라운드 진행 (구 InGame / "Main Scene")
}

/// <summary>
/// 설치형 아이템 식별자 — 상점 구매 목록(ShopPurchases)이 담고, 배달(ShopDelivery)이 본부 씬 인스턴스로 되돌린다. (#182, GDD 8-4)
/// 설치형은 프리팹 스폰이 아니라 <b>본부에 이미 배치된 씬 인스턴스</b>를 켜는 방식이라(#108 SetInstalled),
/// 지목 대상이 프리팹 자산이 아니다 — 그래서 프리팹 참조가 아닌 enum으로 가리킨다.
/// </summary>
public enum EInstallable
{
    None,          // 이 진열대는 소지형 — 프리팹 참조로 판다
    SignalDecoder, // 신호 해석기 (#108)
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
