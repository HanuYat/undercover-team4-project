# 세션 입퇴장 토스트 (#598 확장)

작성일: 2026-08-12 · 이슈 #598 · 브랜치 `feature/598-lobby-screen`

## 배경

이슈 #598은 로비 퇴장 토스트를 범위에 넣었고 코드도 들어가 있다. 그런데 두 가지가 걸린다.

**로비 토스트는 실제로 뜨지 않는다.** `ToastView`는 `Assets/Prefabs/UI/HUD.prefab`에만 있고, 그 HUD는
`InteractionFeedback.EnsureHud`가 Title·Lobby를 명시적으로 제외하고 생성한다. 그래서 로비에서
`App.UI.Toast`는 null이고, `LobbyRosterPanel.HandlePlayerLeft`의 `App.UI.Toast?.Show(...)`가 `?.`에 걸려
조용히 무동작한다. RPC는 정상적으로 도는데 그릴 곳이 없다.

**알림이 로비 밖으로 못 나간다.** 닉네임은 각 클라의 로컬 값이라 서버가 모르고, `ReportSelfRpc`로 올라온
것을 `LobbyRoster`가 모아둘 뿐이다. 그 로스터는 로비 씬 오브젝트라 상점으로 넘어가는 순간 despawn 된다.
인게임에는 `PlayerNameTag`가 닉네임을 들고 있지만 로비엔 플레이어 오브젝트가 없어(#214), 어느 한쪽만으로는
세 씬을 다 덮지 못한다.

이 스펙은 입퇴장 알림을 **세션 전체(로비·상점·게임맵)** 로 확장한다.

## 결정 사항

- **입장의 정의는 "세션에 처음 붙을 때 1회"** 다. 씬 전환은 재입장으로 치지 않는다. 씬마다 전원분 토스트가
  몰려 뜨는 것을 막기 위한 것이다.
- **명부는 하나만 둔다.** `LobbyRoster`를 세션 상주로 승격해 단일 출처로 쓴다. 상주 알림자를 따로 만들어
  닉네임 보고 경로를 둘로 가르는 안은 버렸다.
- 이슈 #598 본문의 "입장 토스트는 이번 범위 밖"은 이번 작업으로 뒤집는다. 이슈 본문을 수정한다.

## 설계

### 1. `LobbyRoster` → `SessionRoster` 승격

`Assets/Scripts/Scene/LobbyRoster.cs` → `Assets/Scripts/Network/SessionRoster.cs`.

씬 배치를 걷어내고 **기존 `Assets/Prefabs/SessionState.prefab`에 컴포넌트로 얹는다.** 그 프리팹이
이미 `TeamFund`·`ShopPurchases`·`RoundProgress`·`MapSelection`을 한 NetworkObject에 모아 두고
Title 씬의 `SessionObjectSpawner.m_persistentPrefabs`로 스폰되고 있다 — 새 프리팹을 만들 이유가 없다.
`NetworkedManagerBase` 상속, 서버가 세션 시작 시 `destroyWithScene:false`로 1회 스폰.

참조자가 로비 패널 한 곳이 아니게 되므로 App에 올린다(R3): `App.Game.Roster`. 세션 없이 씬을 직접
Play하면 null이므로 사용처는 `?.` 가드 필수 — `TeamFund`·`MapSelection`이 쓰는 방침 그대로다.

기존 `ReportSelfRpc` · `m_players` · `OnListReady` · `HandleClientDisconnected` · 음소거 재보고는 그대로
올라간다. 상주가 되면서 `OnNetworkSpawn`이 세션당 1회만 돌기 때문에 씬을 넘어도 재보고가 없다 —
입장 판정의 근거가 여기서 나온다.

기존 프리팹에 얹었으므로 `DefaultNetworkPrefabs.asset` 등록도 스포너 목록 수정도 필요 없다.

### 2. 입장/퇴장 이벤트

`OnPlayerLeft`에 짝을 맞춰 `OnPlayerJoined`를 추가한다. 발행 지점이 서로 다르다.

**입장** — `ReportSelfRpc`에서 명부에 없던 clientId가 **새로 추가되는 분기**에서만 발행한다.
`OnClientConnectedCallback`을 쓰지 않는 이유는 그 시점에 서버가 닉네임을 모르기 때문이다. 음소거 변경
등의 재보고는 기존 항목 갱신 분기로 빠지므로 토스트가 뜨지 않는다.

**퇴장** — 현행 유지. `HandleClientDisconnected`에서 항목을 지우기 전에 닉네임을 챙겨 발행한다.

두 RPC(`AnnounceJoinedRpc` / `AnnounceLeftRpc`)는 `SendTo.ClientsAndHost`로 **닉네임과 clientId를 함께**
보내고, 받는 쪽에서 자기 clientId면 거른다. 자기 입장 토스트를 자기가 보는 것을 막기 위해서다. 퇴장은
나간 본인이 이미 없어 지금도 문제가 없지만, 필터를 한 곳에 두는 편이 읽기 쉽다.

"입장 이후에 발생한 일만 보인다"는 성질은 RPC 특성에서 그대로 나온다 — 나중에 들어온 사람은 이전
RPC를 받지 않는다.

### 3. 표시 — `PlayerPresenceToastView`

토스트를 띄우는 책임이 `LobbyRosterPanel`에서 나온다. 그 패널은 로비에만 있어 상점·게임맵을 덮지 못한다.

`PlayerPresenceToastView`를 새로 만들어 **AppBootstrap 프리팹**(DontDestroyOnLoad)에 붙인다. 입장/퇴장
`LocalizedString` 두 개와 지속시간을 여기서 들고, `App.Game.Roster`의 두 이벤트를 구독해
`App.UI.Toast?.Show`를 부른다. 씬을 넘어 살아 있으므로 로비·상점·게임맵이 자동으로 덮인다.

`LobbyRosterPanel`에서는 `m_playerLeftToast` · `m_playerLeftToastSeconds` · `HandlePlayerLeft`가 통째로
빠진다.

로스터가 세션 도중에 스폰되므로, 이 뷰는 App 등록 시점에 로스터가 아직 없을 수 있다. 구독은
`App.Game.Roster`가 생긴 뒤에 걸어야 한다.

### 4. 문구

키를 옮긴다 — `Lobby.Roster.Left`는 더 이상 로비 전용이 아니다.

| 키 | 문구 |
|---|---|
| `Hud.Presence.Joined` | `{0} 님이 들어왔습니다` |
| `Hud.Presence.Left` | `{0} 님이 떠났습니다` |

`HudTable`에 넣는다 — 이 저장소는 키 접두사를 테이블 이름에 맞추므로 `Session.*`가 아니라 `Hud.*`다.

한국어 조사(이/가)가 받침에 따라 달라지므로 `님이`로 회피한다 — #598에서 정한 방침 그대로다.

### 5. 로비 표시 수단

로비 캔버스에 `ToastView`를 **직접 하나 배치**한다. App 등록이 타입당 하나라 HUD와 동시에 존재하면 안
되는데, HUD가 로비에서 생성되지 않으므로 겹치지 않는다.

`InteractionFeedback`의 Lobby 제외를 푸는 방법도 있으나, 조준점·타이머·목표 금액까지 대기 화면에 딸려
와서 택하지 않는다.

Title은 그대로 둔다 — 토스트가 없어 `?.`로 무동작이고, 세션 생성 직후 로비로 넘어가므로 놓치는 알림이
사실상 없다.

### 6. 구독 타이밍

이 설계에서 가장 깨지기 쉬운 지점이다.

상주 로스터는 **Title에서 세션이 시작될 때 스폰**되므로, 로비 패널이 깨어날 무렵엔 `OnListReady`가 이미
지나가 있다. 패널이 구독만 하면 빈 화면이 된다.

패널은 `App.Game.Roster`를 잡는 즉시 **현재 명부를 한 번 그린다**(잡을 때까지는 `Update`로 기다린다 —
`TeamFundBalanceView`와 같은 방식). 라운드 실패로 로비에 되돌아오는 경우(#395)도 같은 경로다.

당초 `IsReady` 플래그를 두려 했으나 넣지 않았다 — `Rebuild`가 이미 `IsSpawned`로 가드하고 있어
구독 직후 한 번 그리는 것으로 충분했다.

### 7. 초상 실행 순서 (작업 중 드러난 회귀)

명부를 상주로 올리자 로비 카드에 얼굴이 안 나오는 증상이 생겼다. `LobbyPortraitStage`가 `Awake`에서
초상을 굽는데 실행 순서가 기본값(0)이라, `PanelBase`(-100)인 패널의 첫 `Rebuild`보다 늦다. 혼자
있으면 명단이 바뀌지 않아 다시 그릴 계기가 없어 얼굴 없는 카드로 굳는다.

승격 전에는 명부가 패널보다 늦게 스폰돼 `OnListReady`가 늦은 `Rebuild`를 한 번 더 돌려 줬고, 그때는
이미 구워져 있어서 드러나지 않았다.

`EExecutionOrder.UIContent`(-150)를 새로 두어 무대를 패널보다 앞세우고, 순서 하나에만 기대지 않도록
패널 `Start`에서 한 번 더 그린다.

## 범위 밖

- 정상 퇴장과 접속 끊김을 문구로 구분하는 것 — 둘 다 같은 문구를 쓴다.

원래 범위 밖이었으나 이번에 함께 처리한 것:

- 코르크판 배경·명단 카드 배경·로비 버튼/레이아웃 (#598 항목 ①)
- 이슈 코멘트의 `ConfirmPanelBase` 리팩터 — 딤 배경은 `PanelBase`로, 예/아니오 배선과 연타 방어는
  `ConfirmPanelBase`로. 딤 배경 패턴이 확인창 셋 외에 Pause·Settings·Settlement에도 있어 여섯 곳을
  함께 걷어냈다(남기면 같은 이름 필드가 겹쳐 직렬화가 깨진다).

## 검증

컴파일 확인까지가 이 작업의 범위다. Play 모드 검증은 사용자가 직접 한다.

Multiplayer Play Mode로 확인할 항목:

1. 클라 2가 로비에 들어올 때 클라 1 화면에만 입장 토스트가 뜬다 (본인 화면에는 안 뜬다).
2. 로비 → 상점 → 게임맵으로 넘어가도 입장 토스트가 다시 뜨지 않는다.
3. 상점에서 클라 2가 나가면 남은 사람에게 퇴장 토스트가 뜬다.
4. 게임맵에서도 같은 동작.
5. 로비 명단 카드가 여전히 정상적으로 그려진다 (late-join 포함).
6. 씬을 직접 Play해도(세션 없음) NRE 없이 넘어간다.
7. 혼자 있어도 자기 카드에 얼굴이 바로 뜬다 (§7 회귀).
8. 확인창 셋이 ESC로 취소되고, 딤 배경이 창과 함께 켜지고 꺼진다. 정산 창은 닫아도 카운트다운이
   남는 기존 동작이 유지된다.
