/// <summary>씬 식별자. 실제 씬 이름 매핑은 AppHelper.ToSceneName — 빌드 인덱스에 결합하지 않는다.</summary>
public enum EScene
{
    None,
    Title, // 메인메뉴 — 세션 생성/참가
    Lobby, // 최초 대기 화면
    Shop, // 상점 = 인게임 허브 (라운드 사이 준비, 루프 진입점)
    Game, // 게임맵 - 라운드 진행 (구 InGame / "Main Scene")
}

/// <summary>
/// 설치형 아이템 식별자 — 상점 구매 목록(ShopPurchases)이 담고, 배달(ShopDelivery)이 본부 씬 인스턴스로 되돌린다. (#182, GDD 8-4)
/// 설치형은 프리팹 스폰이 아니라 <b>본부에 이미 배치된 씬 인스턴스</b>를 켜는 방식이라(#108 SetInstalled),
/// 지목 대상이 프리팹 자산이 아니다 — 그래서 프리팹 참조가 아닌 enum으로 가리킨다.
/// </summary>
public enum EInstallable
{
    None, // 이 진열대는 소지형 — 프리팹 참조로 판다
    SignalDecoder, // 신호 해석기 (#108)
    JailSirenButton, // 유치장 사이렌 버튼 — 본부에서 탈옥을 원격 제지한다 (#488)
}

/// <summary>
/// 로컬 음성(Vivox) 연결 상태. (#430)
/// 지금까지는 상태가 private 문자열뿐이어서 Vivox 로그인이 실패해도 플레이어가 알 방법이 없었다.
/// 실패해도 재시도 경로는 두지 않는다(표시까지) — 어느 단계에서 다시 붙어도 안전한지가 별건이다.
/// 먹통 음성 왜곡(#372)은 여기 넣지 않는다 — 연결 여부와 직교한 축이라 섞으면 둘 다 못 읽는다.
///
/// <b>값을 추가하면 `LobbyTable`에 <c>Lobby.Voice.&lt;값 이름&gt;</c> 키도 함께 추가할 것.</b>
/// 플레이어에게 보이는 문구는 그 테이블이 주인이고, 표시 측이 enum 이름으로 키를 만들어 조회한다
/// (규약 기반 매핑 — docs/design/localization.md §2 결정 (h)). 여기 arm만 늘리면 그 상태에서만
/// 키가 없어 화면에 키 문자열이 뜨거나 빈칸이 되고, 컴파일러는 막아 주지 않는다.
/// `VivoxManager.ToLabel`은 디버그 GUI 전용이므로 그쪽만 고쳐도 플레이어 화면은 바뀌지 않는다. (#497)
/// </summary>
public enum EVoiceState
{
    Idle, // 음성 연결 전 / 정리 후
    LoggingIn, // Vivox 초기화·로그인 중
    Joining, // 채널 참가 중
    Connected, // 무전 + 근접 채널 참가 완료
    Failed, // 로그인·참가 실패 (사유는 디버그 패널에만)
}

/// <summary>
/// Awake 실행 순서 — [DefaultExecutionOrder((int)EExecutionOrder.X)]로 사용.
/// 음수 = 일반 스크립트(0)보다 먼저. 매니저 → UI매니저 → 패널 → 일반 스크립트 순서를 보장한다.
/// </summary>
public enum EExecutionOrder
{
    Bootstrap = -400, // AppHelper (DontDestroyOnLoad 루트)
    BaseManagement = -300, // App 등록 매니저 전부 (Session·Round·SuddenEvent 등)
    UIManagement = -200, // UIManagerBase 파생 — 패널 등록을 받아야 하므로 패널보다 먼저
    UIPanel = -100, // PanelBase 파생 — 매니저 뒤, 일반 스크립트 앞
}
