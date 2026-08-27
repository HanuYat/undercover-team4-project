/// <summary>씬 식별자. 실제 씬 이름 매핑은 AppHelper.ToSceneName — 빌드 인덱스에 결합하지 않는다.</summary>
public enum EScene
{
    None,
    Title, // 메인메뉴 — 세션 생성/참가
    Lobby, // 최초 대기 화면
    Shop, // 상점 = 인게임 허브 (라운드 사이 준비, 루프 진입점)
    Game, // 게임맵 - 라운드 진행 (Assets/Scenes/Maps/*)
    Tutorial, // 튜토리얼 전용 맵 (Tutorial.unity) — 로드 후에는 Game으로 분류된다 (AppHelper.FromSceneName)
}

/// <summary>
/// 설치형 아이템 식별자 — 상점 구매 목록(ShopPurchases)이 담고, 배달(ShopDelivery)이 본부 씬 인스턴스로 되돌린다. (#182, GDD 8-4)
/// 설치형은 프리팹 스폰이 아니라 <b>본부에 이미 배치된 씬 인스턴스</b>를 켜는 방식이라(#108 SetInstalled),
/// 지목 대상이 프리팹 자산이 아니다 — 그래서 프리팹 참조가 아닌 enum으로 가리킨다.
/// </summary>
// 표시 이름·설명은 ItemTable이 주인이다 — 소지형(ItemBase.ItemName)과 같은 네임스페이스를 쓴다.
// 값을 추가하면 두 키(Item.Name.<이름> · Item.Description.<이름>)를 함께 넣을 것. (#497)
[LocalizedEnum("ItemTable", "Item.Name.", nameof(EInstallable.None))]
[LocalizedEnum("ItemTable", "Item.Description.", nameof(EInstallable.None))]
public enum EInstallable
{
    None, // 이 칸은 소지형 — 프리팹 참조로 판다
    SignalDecoder, // 신호 해석기 (#108)
    JailSirenButton, // 유치장 사이렌 버튼 — 본부에서 탈옥을 원격 제지한다 (#488)
}

/// <summary>상점 주문창 칸의 판매 상태 (#814, #843).</summary>
public enum EShopSlotStatus
{
    Available,
    SoldOut, // 이번 라운드 한정
    Owned, // 설치형 세션 내 이미 구매
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
/// 아래 <see cref="LocalizedEnumAttribute"/>가 그 규약의 선언이고, 에디터 메뉴
/// <i>Tools ▸ Localization ▸ 규약 키 검증</i>이 값 전부에 키가 있는지 확인해 준다.
/// `VivoxManager.ToLabel`은 디버그 GUI 전용이므로 그쪽만 고쳐도 플레이어 화면은 바뀌지 않는다. (#497)
/// </summary>
[LocalizedEnum("LobbyTable", "Lobby.Voice.")]
public enum EVoiceState
{
    Idle, // 음성 연결 전 / 정리 후
    LoggingIn, // Vivox 초기화·로그인 중
    Joining, // 채널 참가 중
    Connected, // 무전 + 근접 채널 참가 완료
    Failed, // 로그인·참가 실패 (사유는 디버그 패널에만)
}

/// <summary>비자발 세션 끊김 사유 (#764) — 타이틀 복귀 토스트가 원인별 문구를 고르는 데 쓴다.</summary>
[LocalizedEnum("TitleTable", "Title.ConnectionLost.", nameof(EConnectionLostReason.None))]
public enum EConnectionLostReason
{
    None,
    NetworkDropped, // NGO 본인 드롭 — 호스트 종료 시 클라가 세션 삭제보다 먼저 받는 신호이기도 하다
    SessionClosed, // 세션 삭제 / 호스트 종료 이벤트
}

/// <summary>
/// 일회성 이펙트 식별자 — EffectManager가 EffectLibrary에서 프리팹·수명을 찾는 키. (#478)
/// 프리팹 참조가 아닌 enum으로 가리키는 이유는 EInstallable과 같다: 사용처(무기·NPC)가
/// 런타임 스폰물이라 인스펙터로 프리팹을 배선할 수 없는 경우가 있고, 어떤 연출이 존재하는지
/// 한곳에서 보이는 편이 사전 생성 개수를 조율하기도 쉽다.
/// </summary>
public enum EEffect
{
    None, // 연출 없음 — 배선 누락과 구분되는 '의도적으로 안 냄'
    ImpactDust, // 근접 타격 먼지 — 맞은 자리에 1회 (#478)
}

/// <summary>
/// 효과음 식별자 — SoundManager가 AudioLibrary에서 클립·볼륨·감쇠 거리를 찾는 키. (#478)
/// 클립 파일명을 문자열로 쓰지 않는 이유는 이름을 바꿔도 조용히 깨지지 않게 하기 위함이다 (#483).
/// 값이 AudioLibrary·FxManager에 정수로 직렬화돼 있어, 새 항목은 <b>항상 맨 뒤에 붙인다</b> —
/// 중간에 끼우면 기존 배선이 통째로 한 칸씩 밀린다.
/// </summary>
public enum EAudioClip
{
    None, // 소리 없음
    BatonSwing, // 휙 — 진압봉을 휘두르는 순간 (명중 여부와 무관)
    BatonHitMetal, // 깡 — 로봇(안드로이드 NPC · 동료 경찰)을 맞혔다
    BatonHitFlesh, // 퍽 — 인간 NPC를 맞혔다
    BatonHitWorld, // 둔탁 — 벽·소품을 맞혔다
    TaserHit, // 지직 — 테이저 명중 (대상이 로봇이든 사람이든 같다. 전기는 몸체를 가리지 않는다)

    // 이동음 (#483) — 전부 3D다. 남의 발소리가 어디서 나는지가 곧 정보이기 때문이다
    // (본부가 못 보는 구역에서 누가 움직이는지, 뒤에 누가 붙었는지).
    FootstepWalk, // 저벅 — 걷는 중 한 걸음
    FootstepRun, // 타닥 — 뛰는 중 한 걸음
    JumpTakeoff, // 탁 — 발을 떼는 순간
    JumpLand, // 쿵 — 착지

    ScannerScan, // 삐릭 — 스캐너 판독
    UiSuccess, // 성공 확인음
    UiFail, // 실패 확인음

    // 아이템 조작음 (#549)
    TaserFire, // 파팟 — 테이저를 쏜 순간 (명중 여부와 무관)
    RopeBind, // 촥 — 밧줄이 대상에 걸려 조여진 순간

    // 설비음 — 아이템이 아니라 본부 설비가 낸다.
    JailSiren, // 웨엥 — 유치장 경보 (#488 사이렌 버튼)

    // UI 조작음
    UiClick, // 톡 — UI 버튼을 누른 순간 (버튼마다 배선하지 않고 UiClickSound가 전역으로 낸다)

    // 폭주 차량 (#304)
    VehicleEngine, // 부우우 — 엔진음(루프). 차량 프리팹의 AudioSource가 직접 튼다
    VehicleHorn, // 빠았 — 통과 직전 경적

    // 날씨 (#647)
    LightningStrike, // 콰르릉 — 벼락이 떨어진 순간
    RainLoop, // 쏴아 — 비가 오는 동안 계속 (환경음 루프)

    // 피격 신음
    NpcHurtHuman, // 윽 — 인간 NPC가 피해를 입은 순간 (가해 수단과 무관)

    // 폭탄 (#232)
    BombCountdownSlow, // 삐 … 삐 — 카운트다운 평상시 (루프)
    BombCountdownFast, // 삐삐삐 — 폭발 임박 (루프)
    BombExplosion, // 쾅 — 터진 순간

    // 상점
    ShopPurchase, // 짤랑 — 구매가 성립한 순간 (산 사람에게만)

    // 다운 유예 구조 (#725)
    ReviveLoop, // 웅 — 동료를 구조 채널링하는 동안 계속 (루프)

    // 유치장 경보등 (#311/#493)
    JailAlarm, // 웨엥 — 경보등이 점멸하는 동안 계속 (루프)

    // 구역 스캔 (#490)
    AreaScanHit, // 삐빅 — 반경 안에 진범이 있을 때의 판독음 (오너 화면 전용, 2D)
    AreaScanMiss, // 뚜 — 반경 안에 진범이 없을 때의 판독음 (오너 화면 전용, 2D)

    // NPC 근접 공격 (#817)
    NpcAttackSwing, // 휙 — NPC가 공격을 휘두른 순간 (명중 여부와 무관, 맨손이든 무기든 같다)
    NpcAttackHitRobot, // 깡 — NPC의 공격이 로봇 경찰을 맞혔다 (플레이어는 전원 로봇이다)

    // 치장 뽑기 (#818 D)
    GachaSpin, // 드르르 — 릴이 도는 동안 (뽑은 사람 화면 전용, 2D). 릴이 멈추면 끊긴다
    GachaReveal, // 짜잔 — 당첨이 가운데 멈춘 순간

    // 거대 뿅망치 (#816)
    HammerHit, // 뿅 — 뿅망치 평타 (맞은 대상 종류를 가리지 않는다)
    HammerCrit, // 콰광 — 1% 대박이 터져 대상이 그 자리에서 죽었다

    // 배달 드론 (#824)
    DroneApproach, // 위잉 — 드론이 하강을 시작하는 순간
    CrateLand, // 쿵 — 상자가 착지하는 순간
    CrateOpen, // 철컹 — 상자를 여는 순간

    // 처치 확인 (#869) — 막타를 친 사람 화면 전용, 2D
    KillConfirm, // 처치했다
    KillFriendly, // 동료를 처치했다(오사) — 확인음이지 축하음이 아니다
}

/// <summary>
/// BGM 식별자 — SoundManager가 AudioLibrary에서 곡·볼륨을 찾는 키. (#483)
///
/// <b>씬과 1:1이 아니다.</b> 여러 씬이 같은 곡을 쓰면 그 사이 전환에서 곡이
/// 끊기거나 처음부터 다시 시작하지 않아야 하고, 반대로 한 씬 안에서 곡이 바뀔 수도 있다.
/// 씬↔곡 대응은 AudioLibrary의 씬 표에 있고, 이 enum은 '어느 곡인가'만 가리킨다.
/// </summary>
public enum EBgm
{
    None, // 무음 — 배선 누락과 구분되는 '의도적으로 안 틂'
    Title, // 타이틀 화면 — 로비부터는 끈다 (#585)
    Shop, // 상점(인게임 허브) — 라운드 사이 준비
    Round, // 라운드 진행 중
}

/// <summary>
/// 일회성 연출의 '순간' 식별자 — FxManager가 파티클(EEffect)·소리(EAudioClip) 조합을 찾는 키. (#532)
/// 사용처는 무엇을 재생할지가 아니라 <b>무슨 일이 일어났는지</b>만 고른다 — 조합은 FxManager 인스펙터에 있다.
/// 그래서 먼지를 바꾸거나 소리를 갈아도 아이템 코드는 그대로다.
/// </summary>
public enum EFx
{
    None, // 연출 없음 — 배선 누락과 구분되는 '의도적으로 안 냄'
    BatonSwing, // 진압봉을 휘두른 순간 (명중 여부와 무관)
    BatonHitMetal, // 진압봉이 로봇을 맞혔다 (동료 경찰 · 안드로이드 NPC)
    BatonHitFlesh, // 진압봉이 사람을 맞혔다
    BatonHitWorld, // 진압봉이 벽·소품을 맞혔다
    TaserHit, // 테이저 명중

    // 아이템 조작음 (#549) — EAudioClip과 같은 이유로 뒤에 붙인다(씬의 조합표에 정수로 저장된다).
    TaserFire, // 테이저를 쏜 순간 (명중 여부와 무관)
    RopeBind, // 밧줄로 묶었다 (새로 묶기·합류·끌기 재개 · 동료 운반)

    // 폭주 차량 (#304)
    VehicleHorn, // 통과 직전 경적 — 보이지 않는 방향에서 와도 알 수 있게 하는 예고

    // 구역 스캔 (#490). 파티클은 없고 소리만 배선한다(FxManager.Entry.Effect = None) — 링은 별도 뷰가 그린다.
    AreaScanHit, // 반경 안에 진범이 있다 — 판독음만
    AreaScanMiss, // 반경 안에 진범이 없다 — 판독음만

    // 거대 뿅망치 (#816)
    HammerHit, // 뿅망치 평타
    HammerCrit, // 뿅망치 1% 대박 — 대상이 그 자리에서 죽었다
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
    UIContent = -150, // 패널이 첫 그리기에서 읽어가는 UI 재료(초상 무대 등) — 패널보다 먼저 준비돼야 한다
    UIPanel = -100, // PanelBase 파생 — 매니저 뒤, 일반 스크립트 앞
}

/// <summary>
/// 로봇 색을 나눠 칠하는 몸 부위 (#432). <b>값의 순서 = 구운 메시의 서브메시 순서</b>이므로
/// 중간에 끼워 넣지 말 것 — RobotPartMeshBaker가 이 순서대로 삼각형을 나눠 담는다.
/// 팔은 상체에, 골반은 하체에 붙는다.
/// </summary>
public enum EBodyPart
{
    Head, // 머리 — Head·Neck·Eyes·Eyebrows
    Torso, // 상체 — Spine·어깨·팔·손
    Legs, // 하체 — Hips·다리·발
}

/// <summary>
/// 플레이어 치장 부위 (#818). 저장·전파 배열의 길이가 곧 이 enum의 크기다.
/// <b>순서를 바꾸지 말 것</b> — 계정에 인덱스로 저장된다. 추가는 뒤에만.
/// </summary>
public enum EAccessorySlot
{
    Headwear, // 모자·헬멧
    FacialHair, // 수염·콧수염
    Hair, // 머리카락 — 모자와 함께 쓸 수 있게 따로 둔다
    Eyewear, // 안경·고글·안대
    Facewear, // 마스크
    Earwear, // 이어피스·헤드셋·피어싱
}

/// <summary>
/// 치장 슬롯 묶음 (#818) — 한 아이템이 <b>가리는</b> 슬롯을 표시하는 데 쓴다.
/// 값은 <see cref="EAccessorySlot"/>의 비트 자리이므로 그쪽 순서를 따라간다.
/// </summary>
[System.Flags]
public enum EAccessorySlotMask
{
    None = 0,
    Headwear = 1 << EAccessorySlot.Headwear,
    FacialHair = 1 << EAccessorySlot.FacialHair,
    Hair = 1 << EAccessorySlot.Hair,
    Eyewear = 1 << EAccessorySlot.Eyewear,
    Facewear = 1 << EAccessorySlot.Facewear,
    Earwear = 1 << EAccessorySlot.Earwear,
}

/// <summary>
/// 창 모드 (#796). Unity의 <c>FullScreenMode</c> 중 PC에서 쓰는 셋만 골라 둔 것 —
/// 값은 PlayerPrefs에 저장되므로 순서를 바꾸지 말 것. 변환은 GameSettings가 한다.
/// </summary>
public enum EWindowMode
{
    Windowed, // 창
    Borderless, // 테두리 없는 전체 창
    Fullscreen, // 전체화면(전용)
}
