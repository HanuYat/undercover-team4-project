# 라운드 맵 선택 (#578)

Shop(출동 전 허브)에서 **호스트가 다음 라운드 맵을 고르고**, '출동'이 그 맵으로 간다.
이전에는 [`AppHelper.ToSceneName`](../Assets/Scripts/Core/AppHelper.cs)의 `EScene.Game` arm에 씬 이름 한 장이 박혀 있어,
맵을 바꾸려면 소스를 고쳐 다시 빌드해야 했다.

## 구조

| 조각 | 자리 | 역할 |
|------|------|------|
| `MapSelection` | 상주 프리팹 (`SessionObjectSpawner`가 세션 시작 시 스폰) | 맵 목록 + 선택 인덱스 보유·복제 |
| `MapSelectButton` | Shop 씬 콘솔 | 이전/다음 — 호스트 전용 |
| `MapSelectionView` | Shop 씬 콘솔 | 다음 출동지 표시 — 전원 |
| `AppHelper.ToSceneName` | — | 로드 직전 홀더에게 씬 이름을 묻는다 |

### 왜 `EScene`을 맵마다 늘리지 않나

`App.CurrentScene == EScene.Game` 비교가 [`PlayerSpawnManager`](../Assets/Scripts/Network/PlayerSpawnManager.cs) ·
[`PlayerItemSupply`](../Assets/Scripts/Player/PlayerItemSupply.cs) · [`SceneIndicatorHud`](../Assets/Scripts/UI/Hud/SceneIndicatorHud.cs)에 있다.
맵마다 enum 값을 늘리면 이 셋이 전부 깨진다. `EScene.Game`은 "게임 맵"이라는 뜻 그대로 두고 **이름만 바꿔치운다**.

### 왜 Shop 씬이 아니라 상주 프리팹인가

전환이 일어나는 순간이 곧 Shop이 언로드되는 순간이다. 선택값을 Shop 씬에 두면
`ToSceneName`이 이름을 물으려는 그때 홀더가 이미 죽어 있다. 선택값은 씬보다 오래 살아야 한다 —
`TeamFund` · `ShopPurchases`와 같은 계층(#214 §6 이월 구조).

### 복제하는 이유는 표시뿐

NGO 씬 동기화는 서버가 이름으로 로드하고 클라는 끌려온다(클라의 전환 요청은 `AppHelper`가 거부한다).
**전환만 보면 서버만 정확하면 된다.** 그래도 인덱스를 복제하는 건 "다음에 어디로 가는지"가
전 클라 화면에 같게 떠야 하기 때문이다.

### 선택 잠금

출동 뒤에 선택이 바뀌면 실제 로드되는 맵과 화면에 뜬 맵이 어긋난다.
잠금은 별도 동기화 변수가 아니라 `ShopManager.IsDispatched`다 — Shop을 벗어나면 `App.SceneFlow.Shop`이
null이 되고, 라운드가 끝나 Shop으로 돌아오면 `ShopManager`가 새 인스턴스로 뜬다.
**잠금과 해제가 씬 수명에서 저절로 따라온다.**

## 맵을 추가하려면

1. 씬을 만들고 아래 **씬 요건**을 채운다.
2. `EditorBuildSettings`(Build Profiles ▸ Scene List)에 등록한다 — NGO 씬 동기화가 이름으로 로드하므로 필수다.
3. 상주 프리팹의 `MapSelection` 인스펙터 배열에 항목을 추가한다 (씬 이름 · 표시 이름).

코드는 한 줄도 고치지 않는다. `AppHelper.FromSceneName`도 이름표에 없으면
"`InGameManager`가 있는 씬이면 `EScene.Game`"으로 떨어지므로 그대로 둔다.

### 씬 요건

만족하지 못하는 맵은 목록에서 뺀다.

- [ ] `InGameManager` 배치 — 이게 곧 "게임 맵"의 정의다 (`FromSceneName` 폴백 기준)
- [ ] `PlayerSpawnManager` + 스폰 포인트
- [ ] `NpcSpawner` + NPC 스폰 포인트
- [ ] 본부 구역 — `HQ.prefab` 인스턴스를 쓰면 미니맵 · CCTV · `HqOccupancyZone`이 한 번에 딸려온다
- [ ] `MinimapViewer`의 월드 중심·크기를 그 맵에 맞춘다 (맵별 값이다 — 미니맵 이미지도 맵별)
- [ ] NavMesh 베이크
- [ ] `EditorBuildSettings` 등록

### 현재 목록

| 씬 | 본부 | 미니맵 월드 |
|----|------|------------|
| `Map_Apocalypse` | `HQ.prefab` 인스턴스 | center(50, 90) size 89.4×169.4 |
| `Main Scene` | 인라인 사본 (프리팹 미사용) | center(0, 17.5) size 66×125 |

`Main Scene`은 `HQ.prefab`을 쓰지 않는 구 개발 맵이라, **프리팹에 나중에 붙은 설비가 빠져 있을 수 있다.**
빠진 것이 드러나면 프리팹으로 교체하거나 목록에서 뺀다.

호스트 단독 전환 확인에서 `Main Scene`은 정상 로드됐지만, 그 씬 자체의 기존 문제가 함께 찍혔다
(#578 범위 밖 — 목록에 넣은 결과 드러난 것일 뿐이다):

- `BoxCollider does not support negative scale or size.` ×14 — Synty 인도 모서리 프롭이 미러링(스케일 -1)돼 있다
- `RemainingCriminalsHud: WantedListManager를 찾지 못해 표시할 수 없다` — 매니저는 씬에 있으니 조회 시점 문제
- `DeviceBlackoutView: DeviceBlackoutEvent를 찾지 못해 먹통 표현이 동작하지 않는다` — 돌발 이벤트 미구성

`Map_Apocalypse`에서는 셋 다 나지 않는다.

## 범위 밖

새 맵 제작 · 맵별 밸런스(NPC 밀도 · 라운드 시간 · 수배 인원) · 맵 투표 / 랜덤 선택.

관련: #215(씬 흐름·`AppHelper` 매핑) · #326(출동 콘솔) · #403(로딩 화면) · #410(준비 게이트)
