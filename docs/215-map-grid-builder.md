# 맵 제작 가이드

- **대상:** 새 게임 맵을 만들거나 기존 맵 배치를 고치려는 팀원
- **범위:** A장 격자 생성기 사용법 · B장 플레이 가능한 맵으로 만드는 체크리스트 · C장 함정 모음
- **코드:** [MapGridBuilder.cs](../Assets/Scripts/Editor/MapGridBuilder.cs) · [MapPalette.cs](../Assets/Scripts/Editor/MapPalette.cs) (둘 다 `Editor` 폴더 — 빌드에 안 들어간다)
- **최초 작성:** #215 (Map_Apocalypse 제작) · 파일명의 `215-` 접두사는 그때의 흔적이고 내용은 상설 참고용이다

> **먼저 읽을 것.** A장(격자 깔기)은 맵 제작의 **첫 10%**다. 나머지 90%는 B장 체크리스트다.
> 바닥과 벽만 깔고 끝내면 Play를 눌러도 라운드가 시작되지 않는다.

---

# A장 — 격자 생성기

## A-1. 한 줄 요약

ASCII 배치도 한 장과 **팔레트**(어떤 프리팹을 쓸지 담은 ScriptableObject)를 읽어, 맵의 **바닥과 경계벽만** 격자로 깔아준다. 건물·프롭·조명은 손으로 배치한다.

생성된 것들은 평범한 씬 오브젝트다. 런타임 시스템이 아니고 저장되는 생성기 상태도 없다 — 한 번 찍어내고 나면 이후엔 손으로 자유롭게 고치면 된다.

**스크립트를 고칠 일은 없다.** 무엇을 깔지는 전부 팔레트 에셋이 정한다.

**메뉴 2개**
```
Tools/맵 격자 생성          팔레트 선택 후 실행. 루트 전체 재생성
Tools/맵 경계만 다시 생성    경계벽만 갈아끼움 (건물·프롭 보존)
```

## A-2. 빠른 시작 — 새 맵 만들기

### 배치도 쓰기

`Assets/Scenes/Maps/` 에 `<맵이름>_Layout.txt` 생성. 한 글자가 한 칸이다.

```
####################
#.......CC.........#
#.BBBBB.RR.BBBBBBB.#
#.......CC.........#
#CRRRRRCRRCRRRRRRRC#
####################
```

### 팔레트 만들기

Project 창에서 **Create ▸ Map ▸ Map Palette**.

1. `Layout` 칸에 방금 만든 `.txt`를 끌어다 놓는다
2. `Cell Size`를 에셋 팩의 모듈 크기에 맞춘다 (PolygonApocalypse의 도로·보도는 실측 **5m**. 다른 팩은 직접 재볼 것)
3. `Entries`에 글자별 프리팹을 채운다

### 생성

**팔레트 에셋을 선택한 채로** `Tools/맵 격자 생성`.

씬에 루트 오브젝트 하나가 생긴다. 이름은 **레이아웃 파일명에서 `_Layout`을 뗀 것**이다.

### 이후

건물·프롭·조명은 손으로 얹는다. 그 다음은 B장으로.

> ⚠️ **`맵 격자 생성`은 루트 아래를 전부 지운다.** 손배치한 건물·프롭·스폰 포인트가 같이 날아간다.
> 벽/게이트 설정만 만질 때는 반드시 **`맵 경계만 다시 생성`** 을 쓸 것. (#215 제작 중 이 사고로 게이트 8개를 날린 적이 있다)

## A-3. 레이아웃 텍스트 형식

| 규칙 | 내용 |
|------|------|
| 한 글자 = 한 칸 | 칸의 한 변은 팔레트의 `Cell Size` |
| **첫 줄이 북쪽** (z 최대) | 에디터에서 위에서 내려다본 모양 그대로 읽힌다 |
| `//`로 시작하는 줄 | 주석. 무시된다 |
| 빈 줄 | 무시된다 |
| 공백 문자 `' '` | 아무것도 놓지 않는다 |
| 줄 길이 | **전부 같아야 한다.** 하나라도 다르면 생성을 중단하고 몇 번째 줄인지 알려준다 |

좌표 규약: 칸 `(col,row)`는 `x ∈ [col·c, (col+1)·c]`, `z ∈ [row·c, (row+1)·c]`를 차지한다 (`c` = `Cell Size`). 즉 맵 원점은 **남서쪽 모서리**이고 맵 전체가 +x/+z 사분면에 놓인다.

팔레트에 규칙이 없는 글자를 만나면 **건너뛰고 경고를 띄운다.** 오타로 맵에 구멍이 나도 Console에서 바로 보인다.

> **레이아웃을 늘릴 때는 북쪽(파일 위쪽)에 줄을 얹을 것.** 첫 줄이 북쪽이고 월드 원점은 남서 모서리라,
> 남쪽(파일 아래쪽)에 붙이면 기존 칸의 좌표가 전부 밀려 손으로 배치해 둔 건물·프롭이 어긋난다.

## A-4. 팔레트 레퍼런스

### 상단

| 필드 | 설명 |
|------|------|
| `Layout` | 배치도 `.txt`. 루트 이름도 여기서 나온다 |
| `Cell Size` | 한 칸의 한 변(m) |
| `Seed` | 무작위 선택·회전 시드. **같은 팔레트+레이아웃이면 몇 번을 돌려도 같은 결과** |
| `Edge Facing Symbols` | 연석이 바라볼 글자들. 보통 도로·횡단보도 (`"RC"`) |

### `Entries` — 글자별 배치 규칙

| 필드 | 설명 |
|------|------|
| `Symbol` | 이 규칙이 맡는 글자 한 자 |
| `Group` | 생성물을 담을 자식 오브젝트 이름. 같은 이름을 쓴 규칙끼리 한 곳에 모인다 |
| `Prefabs[]` | 기본 후보. 둘 이상이면 무작위로 하나 고른다 |
| `Underlay Prefabs[]` | **먼저 밑에 깔 것.** 비우면 안 깐다 |
| `Edge Prefabs[]` | `Edge Facing Symbols`에 **한 면** 접할 때. 비우면 방향 맞추기를 끈다 |
| `Corner Prefabs[]` | **두 면** 접할 때 (바깥 모서리) |
| `Y Offset` | 높이 보정(m). 두께 있는 타일이 도로면과 이가 안 맞을 때 |
| `Scale` | 배치 배율 |
| `Random Yaw` | 90도 단위 무작위 회전. 방향 맞추기가 걸린 칸에는 적용되지 않는다 |

### `Wall` — 경계벽

| 필드 | 설명 |
|------|------|
| `Symbols` | 경계로 쓸 글자들 (`"#"`) |
| `Bottom` / `Top` + `Top Y` / `Cap` + `Cap Y` | 아래부터 쌓을 조각과 높이. 비워도 된다 |
| `Column` | 한 짝 양 끝에 세울 기둥. 같은 자리에 두 번 서지 않게 걸러준다 |
| `Inset` | 벽면을 칸 경계보다 **안쪽으로 당기는 거리(m)**. 0이면 벽이 바닥 타일 끝선에 딱 붙는데, 바닥 타일이 얇아서 벽 밑을 내려다보면 타일 옆면 너머로 바닥 밑이 비친다. 조금 당겨 바닥 위로 물리면 가려진다 (기본 0.3) |
| `Gate` + `Gate Scale` / `Gate Inset` | 경계벽에 끼울 출입 게이트 |
| `Use Box Collider` | 켜면 조각들의 콜라이더를 끄고 BoxCollider가 대신 막는다 (A-6) |
| `Thickness` / `Height` / `Column Width` | 그 BoxCollider의 치수 |

## A-5. 배치 규칙이 실제로 하는 일

### 피봇을 믿지 않는다

에셋 팩마다 피봇 규칙이 제각각이고(모서리 피봇이 흔하다) 칸보다 큰 타일도 있다. 그래서 피봇이 아니라 **렌더러 경계의 중심**을 칸 중심에 맞춘다. 프리팹을 바꿔도 정렬이 깨지지 않는다.

### 밑깔개 (`Underlay Prefabs`)

흙 타일처럼 가장자리가 둥근 것은 칸에 딱 맞춰도 타일 사이가 뚫린다. 밑에 포장을 한 장 받쳐 구멍이 안 보이게 한다. `Scale`을 1보다 크게 줘서 서로 물리게 하는 것과 같이 쓴다.

> 현재 아포칼립스 팔레트의 `D`는 `Y Offset -0.15`, `Scale 1.3`. 이음매가 뜨거나 잠기면 이 둘을 건드린다.

### 연석 자동 회전

`Edge Prefabs`가 채워져 있으면, 그 칸에 접한 `Edge Facing Symbols` 방향을 세어 프리팹과 회전을 고른다.

| 접한 면 | 쓰는 것 | 회전 |
|---------|---------|------|
| 1면 | `Edge Prefabs` | 연석이 그쪽을 보도록 |
| 2면 (인접) | `Corner Prefabs` | 두 면이 도로를 보도록 |
| 그 외 | `Prefabs` | `Random Yaw` 설정에 따름 |

**프리팹 방향 규약:** `Edge Prefabs`는 연석이 **로컬 +Z**를 향한 것, `Corner Prefabs`는 **로컬 +Z와 +X** 두 면이 도로 쪽인 것을 넣어야 한다. (PolygonApocalypse의 `SM_Env_Sidewalk_Straight_01` / `SM_Env_Sidewalk_Corner_01`이 이 규약을 만족한다 — 실측 확인)

### 경계벽

경계 글자 칸 자체에는 아무것도 안 놓는다. **경계 글자 칸과 맞닿은 안쪽 칸 사이의 모서리**에 한 짝씩 세운다. 그래서 `#`을 어떤 모양으로 그려도(직사각형이 아니어도) 경계가 제대로 닫힌다.

**벽 조각 방향 규약:** 로컬 **+X가 두께**, 로컬 **−Z가 길이(한 칸)**인 프리팹을 넣어야 한다.

`Inset`만큼 안쪽(로컬 −X)으로 당겨 세운다. **기둥은 당기지 않는다** — 기둥은 원래 벽면보다 앞으로 튀어나와 이음매를 이미 덮고, 맵 모서리에서는 두 벽이 서로 다른 방향으로 당겨지므로 같이 당기면 기둥이 둘로 어긋나 겹쳐 선다.

## A-6. 경계벽 콜라이더 — 왜 BoxCollider인가

`Use Box Collider`를 켜면 벽 조각들의 MeshCollider를 끄고, 한 짝당 BoxCollider 하나 + 기둥당 하나로 대체한다.

**이유.** Synty 팩의 콜리전 껍질은 렌더 메시에서 구운 볼록 껍질이라 벽의 '평평한' 면조차 수직이 아니다. 격리벽 하단 패널은 실측 **88.0도**(법선 y = +0.036)로 2도 눕어 있다. 그 2도 때문에 `CharacterController.isGrounded`가 옆면 접촉을 지면으로 쳐서 **플레이어가 수직 벽면 위에 선다.** 낙하가 통째로 막히고, 접지로 판정되니 거기서 또 점프해 더 높이 얹힌다 — 이게 오래 있던 "벽 타기" 버그의 정체였다.

> 판정 자체는 [PlayerMovement.cs](../Assets/Scripts/Player/Movement/PlayerMovement.cs)의 `IsStablyGrounded`가 고쳤다(법선이 `slopeLimit`보다 가파르면 접지로 치지 않는다). 그래서 BoxCollider 교체는 **필수가 아니다.** 다만 맵 경계는 넘으면 안 되는 선이라 이중으로 막을 값어치가 있고, 상자는 물리 껍질이 눈에 보이는 면과 정확히 일치해 벽 너머 조준([AimOcclusion.cs](../Assets/Scripts/Interaction/AimOcclusion.cs))도 어긋나지 않는다.
>
> 참고: 이 성질은 격리벽만의 문제가 아니다. 표본 조사 결과 **PolygonApocalypse Buildings 81% / Props 94%, PolygonSciFiCity Buildings 80% / Props 95%** 가 수직이 아닌 옆면을 갖고 있다.

## A-7. 이 도구가 하지 않는 것

- **건물·프롭·조명** — 풋프린트와 회전이 제각각이라 ASCII로 표현하면 오히려 손이 더 간다. 손으로 얹는다.
- **NavMesh 베이크** — 배치가 끝난 뒤 따로 굽는다 (B-6).
- **게임플레이 오브젝트** — 본부·유치장·스폰 포인트·CCTV 등. 전부 B장.

## A-8. 격자 생성에서 자주 걸리는 것

| 증상 | 원인 / 조치 |
|------|-------------|
| "Project 창에서 MapPalette 에셋을 고른 뒤 실행할 것" | 팔레트가 아닌 걸 선택한 상태. 팔레트 에셋을 클릭하고 다시 실행 |
| "N번째 배치 줄 길이가 …" | 레이아웃 줄 길이가 안 맞는다. 그 줄을 고칠 것 |
| "규칙이 없는 글자를 건너뛰었다" | 팔레트 `Entries`에 그 글자 규칙이 없다. 오타이거나 규칙을 안 만든 것 |
| 타일 사이가 뚫려 보인다 | 그 규칙에 `Underlay Prefabs`를 넣거나 `Scale`을 조금 키운다 |
| 바닥이 도로면보다 뜨거나 잠긴다 | 그 규칙의 `Y Offset` 조정 |
| 연석이 엉뚱한 쪽을 본다 | `Edge Prefabs`의 연석이 로컬 +Z를 향하는지 확인 |
| 벽이 안 선다 | `Wall`의 조각이 전부 비어 있으면 경계를 만들지 않는다 |
| 벽 밑을 보면 바닥 밑이 살짝 비친다 | `Wall`의 `Inset`을 올린다 (기본 0.3) |
| 재생성했더니 스폰 포인트가 사라졌다 | 루트 아래 있으면 같이 지워진다 (A-2 주의) |

---

# B장 — 플레이 가능한 맵으로 만들기

격자를 깐 뒤 여기부터. 순서대로 하면 재작업이 없다.

## B-1. 씬 루트 구조

Main Scene의 관례를 따른다. `Map_Apocalypse` 기준 최종 형태:

```
Main Camera                 플레이어 스폰 전까지 화면을 그린다
Directional Light
Map_Apocalypse              ← 격자 생성기가 만든 루트 (지오메트리 전부)
   Ground · Roads · Sidewalks · Buildings · Props · Sky · Boundary
   HQ                       ← HQ.prefab 인스턴스
   Jail                     ← Jail.prefab 인스턴스
=== WORLD ===               맵 앵커
   PlazaPoint · DetentionPoint · BombSpawnPoint_1~3 · CCTVs · NavMesh_Apocalypse
=== SYSTEMS ===
   InGameManager · RoundTimerSync · === GameManagers === · PlayerSpawnManager · InGameUIManager
=== UI ===                  ScreenSpace 캔버스 8개
=== _TEST ===               DevAutoHost
AppBootstrap                루트 유지 (Main Scene도 동일)
NetworkManager              루트 유지
```

## B-2. 필수 시스템 오브젝트

없으면 Play가 안 되거나 에러가 쏟아진다.

| 오브젝트 | 없으면 |
|----------|--------|
| `NetworkManager` | 플레이어가 `NetworkObject`라 세션 없이는 스폰되지 않는다 |
| `AppBootstrap` | `PlayerNameTag`가 `App.Net.Auth`를 읽다 NRE |
| `InGameUIManager` | 플레이어 HUD 패널마다 등록 실패 에러 |
| `InGameManager` | **`App.CurrentScene`이 `EScene.Game`으로 안 잡힌다** (C-1) |
| `PlayerSpawnManager` | 플레이어가 스폰 포인트를 못 받고 원점에 생성된다 |
| `DevAutoHost` | Play 시 로컬 호스트가 안 떠서 아무것도 시작되지 않는다 |

## B-3. 매니저 묶음 · UI 캔버스 이식

Main Scene을 **추가 로드(Additive)** 해서 복사한다.

```
=== SYSTEMS === 에서   InGameManager · RoundTimerSync · === GameManagers ===
=== UI === 통째로       캔버스 8개 + EventSystem
```

`=== GameManagers ===` 12개: `NpcSpawner` `CriminalAssigner` `AppearanceAssigner` `ArrestJudge` `RoundManager` `WantedListManager` `CustodyRouter` `WrongfulArrestPenalty` `SuddenEvents` `DirectoryManager` `ArrestVerdictFeedback` `Gates`

> `Gates`는 서버 권위 게이트 둘을 한 `NetworkObject`에 얹은 오브젝트다 — `SceneReadyGate`(전원 준비, #410) · `SettlementConfirmGate`(전원 정산 확인, #509). 이름이 `SceneReadyGate`였다가 후자가 붙으며 바뀌었다.

> ⚠️ **한 덩어리로 복사할 것.** 임시 부모 하나에 모아 놓고 `Instantiate`를 **1회**만 해야 매니저↔UI 교차 참조가 자동 리맵된다. 따로따로 복사하면 참조가 원본(Main Scene)을 가리켜 씬을 닫는 순간 전부 null이 된다.
>
> 특히 `SuddenEventManager.m_eventEntries`(이벤트 풀 명시 리스트 — 자동수집을 쓰지 않는다, #291)가 이렇게 살아온다. 빠뜨리면 돌발 이벤트가 조용히 안 뜬다.

복사 후 **Main Scene은 저장하지 않고 닫는다.**

## B-4. 본부 · 유치장

둘 다 프리팹이라 드래그 한 번이면 된다.

```
Assets/Prefabs/HQ/HQ.prefab      내부 소품 19개 + 문 2개 + PlayerSpawnPoint 포함
Assets/Prefabs/HQ/Jail.prefab    컨테이너 외형 + 문·자물쇠·판정 버튼 · 격리된 감옥 방(배치 지점 8) 포함
```

두 프리팹은 **경계 참조가 0**이라 드래그만으로 내부가 전부 동작한다. 맵마다 채울 것은 B-5 표에 있는 것뿐.

`HQ.prefab` 에 들어 있는 것 중 놓치기 쉬운 것:
- `WantedListCanvas` · `MinimapCanvas` — **World Space 캔버스**다. BigMonitor에 붙어 프리팹 안에 들어 있으니 `=== UI ===` 에 따로 두지 말 것 (C-8)
- `Doors` 문짝 2쌍 — `DoubleDoor` E 토글 문 (C-7)
- `HqOccupancyZone` — **없으면 미니맵·CCTV가 아예 안 켜진다** (GDD 4장)

## B-5. 앵커 배치 + 배선

| 앵커 | 두는 곳 | 연결할 필드 |
|------|---------|-------------|
| NPC 스폰 포인트 | `NpcSpawner`의 **자식**으로 | 없음 — 배열을 비우면 자식을 자동으로 쓴다 |
| `BombSpawnPoint` × 3 | 맵 전역 분산 | `BombDefusalEvent.m_spawnPoints` |
| `PlazaPoint` | 광장. 오검거 매달기 집행 | `WrongfulArrestPenalty.m_plazaPoint` |
| `DetentionPoint` | 광장 근처 | `WrongfulArrestPenalty.m_detentionPoint` |
| `CCTVNode` × 4 | 감시 구역. `Camera` + `UniversalAdditionalCameraData` + `MinimapTarget` + `CCTVNode` | `CCTVSwitcher.m_cameras` |
| `Jail` | 본부 근처 | `RoundManager.m_jailZone` |
| `MinimapViewer` 값 | `HQ/Interior/MinimapCanvas/Minimap` | `worldCenter` / `worldSize` 를 맵 크기로 |

`CCTVNode` 카메라는 `enabled = false`로 두고 `farClipPlane`을 맵 크기에 맞춘다(100×180 맵에 120이면 충분). 켜고 RenderTexture 붙이는 것은 `CCTVSwitcher`가 런타임에 한다.

**연결하지 않아도 되는 것** (자동 폴백이 있다):
```
JailbreakEvent.m_jailZone / m_jailLock   FindFirstObjectByType<JailZone>()
JailIntake.m_jailZone                    GetComponentInParent<JailZone>()
JailDoor.m_jailLock                      GetComponentInParent<JailLock>()
DeviceBlackoutView.m_blackout            App.Game.SuddenEvent.GetEvent<>()
CCTVNode.m_marker                        같은 오브젝트에서 자동 탐색
```

## B-6. NavMesh

1. 빈 오브젝트에 `NavMeshSurface` 추가 (`Collect Objects = All Game Objects` 로 두면 볼륨 지정이 필요 없다)
2. **베이크에서 뺄 것을 먼저 표시한다** — C-3, C-4, C-5
3. Bake
4. 검증 (아래)

**검증은 눈으로 하지 말고 코드로 한다.** 앵커가 NavMesh에 얹혔는지, 스폰 지점에서 유치장까지 실제로 길이 있는지:

```csharp
// 앵커가 NavMesh 위에 있나
NavMesh.SamplePosition(anchor.position, out NavMeshHit h, 4f, NavMesh.AllAreas)

// 스폰 지점 -> 유치장 좌석 경로가 이어지나 (PathComplete 여야 한다)
var p = new NavMeshPath();
NavMesh.CalculatePath(from, seat.position, NavMesh.AllAreas, p);   // p.status

// 시민이 유치장에 못 들어가는지 (Walkable만 허용 → PathComplete 가 아니어야 정상)
NavMesh.CalculatePath(from, seat.position, 1 << 0, p);
```

`Map_Apocalypse` 실측 결과: 스폰 8곳 전부 → 유치장 좌석 `PathComplete`, Walkable만 허용하면 실패(= 시민 차단 정상 동작).

## B-7. 씬 등록

1. **Build Settings 에 씬을 추가하고 체크한다.** NGO 씬 동기화가 이름 기반이라 등록되지 않은 씬은 세션에서 로드되지 않는다.
2. `InGameManager`가 씬에 있는지 확인한다 — `App.CurrentScene`이 `EScene.Game`으로 잡히는 조건이다 (C-1).

## B-8. 완성 검사

```
[ ] Play 즉시 라운드 준비가 시작된다 (30초 기다리지 않는다 — C-9)
[ ] 플레이어가 기본 장비를 받는다 (C-1)
[ ] 본부 문을 E로 여닫고 드나들 수 있다 (C-7)
[ ] 몽타주·미니맵이 본부 모니터에 뜬다 (C-8)
[ ] CCTV 채널이 전환된다
[ ] NPC가 스폰돼 배회한다
[ ] 검거한 NPC를 유치장까지 끌고 갈 수 있다
[ ] 맵 네 방향 경계벽을 못 넘는다
[ ] Console 에 에러·경고가 없다
```

---

# C장 — 함정 모음

전부 #215 제작 중 실제로 밟은 것들이다.

## C-1. 씬 이름이 `EScene`에 없으면 게임 씬으로 인식되지 않는다

**증상:** 플레이어가 기본 장비를 못 받는다. HUD 씬 라벨이 안 뜬다.

**원인:** [AppHelper.cs](../Assets/Scripts/Core/AppHelper.cs)의 `FromSceneName`이 이름 스위치라 새 맵 이름은 `EScene.None`으로 떨어진다. 기본 장비 지급이 두 경로 모두 `App.CurrentScene == EScene.Game`으로 게이트돼 있다 (`PlayerItemSupply.OnNetworkSpawn` · `PlayerSpawnManager.HandleLoadComplete`).

**조치:** 이미 폴백이 들어가 있다 — **씬에 `InGameManager`가 있으면 게임 맵으로 인식**한다. 새 맵에 `InGameManager`를 넣기만 하면 된다.

**아직 남은 것:** `ToSceneName(EScene.Game)`은 여전히 `"Main Scene"`을 돌려준다. 즉 상점에서 나가면 새 맵이 아니라 Main Scene으로 간다. 맵을 정식 루프에 태우려면 "어느 맵을 쓸지" 고르는 장치가 따로 필요하다.

## C-2. 레이아웃의 공터(`D`)를 믿고 앵커를 놓으면 지붕에 앉는다

**증상:** 폭탄이 건물 지붕에서 터진다. NPC가 허공에 스폰된다.

**원인:** 레이아웃은 격자 생성 시점의 계획이고, 그 뒤 손으로 건물을 세우면 파일과 실제가 갈린다. #215에서는 `D` 공터 두 곳에 창고와 교회가 서 있었고, 앵커 4개가 y 8.1 / 8.8 에 앉았다.

**조치:** 앵커 좌표는 **레이캐스트로 확인한다.** 5m 격자로 훑어 실제 빈 지면 지도를 뽑는 게 가장 빠르다.

```csharp
Physics.Raycast(new Vector3(x, 40f, z), Vector3.down, out RaycastHit h, 80f, ~0, QueryTriggerInteraction.Ignore);
// h.point.y < 0.5f 이고 h.collider 가 건물이 아니면 빈 지면
```

## C-3. 스카이돔이 NavMesh에 구워진다

**증상:** 베이크가 느리고 NavMesh 데이터가 2배로 부푼다.

**원인:** `Collect Objects = All Game Objects`는 스카이돔 메시까지 수집한다. `SM_Generic_SkyDome_Apoco`는 scale 0.5에서도 반지름 856m라 맵 밖 800m까지 NavMesh가 깔린다. #215 실측 **정점 14,808개 중 6,804개(45.9%)가 맵 밖**이었다.

**조치:** 스카이돔에 `NavMeshModifier` + `Ignore From Build`. 조치 후 정점 14,808 → 9,266.

> 지면과 끊긴 섬이고 `Generate Links`도 꺼져 있어 NPC가 실제로 갈 수는 없다 — 버그가 아니라 낭비다. 그래도 베이크 시간·씬 용량·NavMesh 디버그 뷰 가독성이 걸려 있으니 끄는 게 맞다.

## C-4. 본부를 NPC 출입 금지로 만들려면 `Apply To Children`을 켜야 한다

본부 최상위에 `NavMeshModifier`를 붙이고 `Override Area = Not Walkable`로 두는 방식인데, **`Apply To Children`을 안 켜면 메시 없는 루트에만 적용돼 아무 효과가 없다.**

```
m_OverrideArea    = True
m_Area            = 1        (Not Walkable)
m_ApplyToChildren = True     ← 이거
```

플레이어는 `CharacterController`라 NavMesh와 무관하게 그대로 드나든다.

## C-5. 유치장 문짝과 벤치는 베이크에서 빼야 한다

| 대상 | 안 빼면 |
|------|---------|
| 유치장 문짝 (`CellGate`) | 닫힌 문이 베이크에 잡혀 문턱 NavMesh가 끊기고 안팎 경로가 사라진다 |
| 벤치 6개 | 바닥을 파내서(카빙) 좌석이 걸어갈 수 없는 곳이 된다 |
| 본부 문짝 (`DoubleDoor`) | 같은 이유 |

전부 `NavMeshModifier` + `Ignore From Build`. `Jail.prefab` · `HQ.prefab` 에는 이미 설정돼 있다.

시민이 유치장에 못 들어가는 것은 문이 아니라 `JailAreaVolume`(`NavMeshModifierVolume`)이 담당한다.

## C-6. 프리팹 피벗이 지오메트리에서 멀면 회전이 스윙한다

**증상:** 본부를 회전시키면 제자리에서 안 돌고 맵 밖으로 날아간다.

**원인:** `world = 부모world + local` 이라, 피벗이 건물에서 멀면 회전 반지름이 그 거리가 된다. #215의 본부는 피벗이 건물 중심에서 **163m** 떨어져 있었고, 90도 회전에 건물이 **231m** 이동했다.

**조치:** 프리팹으로 뽑기 **전에** 피벗을 지오메트리 중심 바닥으로 옮긴다. 루트만 옮기면 건물이 같이 끌려가므로, 자식의 월드 위치를 유지하며 local을 다시 계산해야 한다.

- **수동:** 자식들을 루트 밖으로 드래그 → 루트 Transform 입력 → 다시 안으로 드래그 (Hierarchy 드래그는 월드를 보존한다). 회전은 건드리지 말 것
- **결과 확인:** 자식 local이 `x ±10 / z ±5` 처럼 대칭 정수로 떨어지면 잘 맞은 것

## C-7. 문턱 콜라이더가 `stepOffset`보다 높으면 열어도 못 지나간다

**증상:** 문짝을 열어놨는데 플레이어가 통과하지 못한다.

**원인:** Synty 벙커 문틀 하단 콜라이더가 **0.32m**인데 `Player.prefab`의 `CharacterController.stepOffset`은 **0.3**이다. 2cm 차이로 막힌다.

**조치:** 문 프리팹 루트의 문턱 `BoxCollider`(낮고 개구부 전체를 덮는 것)를 끈다. `stepOffset`을 올리는 것은 전역 영향이라 하지 말 것.

본부 실내 바닥이 0.3m 올라와 있어 **문턱을 끄고 나서도 실외→실내 단차가 딱 0.30m**(= `stepOffset`)다. 경계값이라 새 맵에서는 반드시 걸어서 확인할 것.

## C-8. World Space 캔버스는 좌표가 원본 씬에 묶여 있다

**증상:** 몽타주·미니맵이 아무 데도 안 뜬다. 본부 모니터가 빈 화면이다.

**원인:** `WantedListCanvas` · `MinimapCanvas`는 `renderMode = WorldSpace`다. Main Scene에서 복사해 오면 **Main Scene 본부 벽 좌표에 그대로 남는다** — 새 맵에서는 맵 구석이나 경계벽 밖이다.

**조치:** 이제 `HQ.prefab` 안(`Interior`)에 들어 있어 본부를 따라다닌다. `=== UI ===` 에 따로 두지 말 것.

옮길 때는 모니터의 **비균일 스케일**을 주의한다. `BigMonitor`는 scale `(2.27, 1.10, 0.01)`이라 `InverseTransformPoint`로 로컬 오프셋을 구하면 z가 100배로 왜곡된다. 회전 프레임만 써야 한다:

```csharp
Vector3 offRot = Quaternion.Inverse(src.rotation) * (canvas.position - src.position);
canvas.position = dst.position + dst.rotation * offRot;
```

## C-9. 씬 직접 Play는 준비 게이트를 통과하지 못한다

**증상:** Play를 눌러도 30초 뒤에야 라운드가 시작된다. Console에 `[준비] 전원 준비 완료를 30초 내에 받지 못했다 (0/1명)`.

**원인:** `SceneReadyGate.ReportSelfReady()`는 `InGameManager.WaitUntilReadyAsync()` 안에 있고, 그것은 `App.LoadScene` 파이프라인만 호출한다. 씬에서 바로 Play하면 그 경로를 타지 않아 아무도 보고하지 않는다.

**조치:** [DevAutoHost.cs](../Assets/Scripts/Test/DevAutoHost.cs)가 호스트를 띄운 뒤 대신 보고한다. MPPM 클론은 보고하지 않아도 된다 — 게이트가 기다리는 대상은 스폰 시점의 접속자, 즉 호스트뿐이다.

## C-10. Main Scene에서 오브젝트를 복사할 때

| 함정 | 내용 |
|------|------|
| **부모 오프셋** | `Object.Instantiate(go)`는 **local** 트랜스폼을 복사한다. 부모가 원점이 아니면(Main Scene의 `Demo_Scene`은 `(-0.07, 5.01, 15.07)`) 복사본의 월드 좌표가 그만큼 어긋난다. 원본의 `position`/`rotation`을 따로 읽어 다시 세팅할 것 |
| **딸려오는 잔재** | Main Scene `NpcSpawner`의 자식 스폰 포인트 3개가 함께 복사된다. 좌표가 음수(`(-2,0,17)` 등)라 새 맵에서는 허공이다. 지울 것 |
| **프리팹 링크** | `Object.Instantiate`는 프리팹 연결을 끊는다. 프리팹이 있는 것은 `PrefabUtility.InstantiatePrefab`으로 새로 만들 것 |

## C-11. NGO — 프리팹 안의 씬 배치 NetworkObject

`HQ.prefab`에는 `NetworkObject`가 9개(소품 6 + 문 2 + …), `Jail.prefab`에는 루트에 1개 들어 있다. NGO 2.13은 이를 정식 지원한다 (`InScenePlacedSourceGlobalObjectIdHash` · `NetworkObjectRefreshTool`).

붙는 비용 두 개:
- 프리팹을 수정하면 NGO가 "이 프리팹을 쓰는 씬을 다시 저장하라" 대화상자를 띄운다. 무시하면 해시가 어긋난다
- **한 씬에 같은 프리팹 인스턴스를 2개 두지 말 것** — 씬 배치 NetworkObject 해시가 충돌한다

루트에 `NetworkObject`가 있는 프리팹(`Jail.prefab`)은 생성 시 `Assets/DefaultNetworkPrefabs.asset`에 자동 등록된다. 동작상 무해하고 git diff 한 줄이 생긴다.

---

# D장 — 현재 자산

| 파일 | 내용 |
|------|------|
| [Map_Apocalypse.unity](../Assets/Scenes/Maps/Map_Apocalypse.unity) | 아포칼립스 맵 씬 |
| [Map_Apocalypse_Layout.txt](../Assets/Scenes/Maps/Map_Apocalypse_Layout.txt) | 배치도 20 × 36칸 = 100 × 180m (벽 안쪽 플레이 영역 89.4 × 169.4m) |
| [MapPalette_Apocalypse.asset](../Assets/Scenes/Maps/MapPalette_Apocalypse.asset) | 팔레트 |
| `Assets/Prefabs/HQ/HQ.prefab` | 본부 — 실내 19.7 × 9.7m, 소품 19개 · 문 2개 · 월드 캔버스 2개 |
| `Assets/Prefabs/HQ/Jail.prefab` | 유치장 — 도시 쪽 컨테이너(문·자물쇠·판정 버튼·게시판) + 맵 밖으로 떼어낸 감옥 방(내부 7.2 × 7.2m, 배치 지점 8) (#537) |

컨셉은 **격리 구역** — 봉쇄된 도심. 팔레트 규칙 6개:

| 글자 | 뜻 | 그룹 |
|------|-----|------|
| `R` | 도로 | Roads |
| `C` | 횡단보도 | Roads |
| `.` | 인도 (연석 자동 회전) | Sidewalks |
| `B` | 건물 부지 — 포장만 깔고 비워둔다 | Sidewalks |
| `D` | 흙·잡초 패치 | Ground |
| `#` | 격리벽 경계 | Boundary |

지오메트리 1,292 오브젝트 (`Ground 45 · Roads 164 · Sidewalks 418 · Buildings 24 · Props 12 · Sky 1 · Boundary 616 · HQ · Jail`).
NavMesh 정점 9,266 / 삼각형 4,086.

**분위기** (흙먼지 대낮 — Synty 아포칼립스 데모값 기준, 안개 거리만 맵 크기에 맞춰 축소)

```
Fog       Linear · 25~200m · (0.879, 0.837, 0.715)
Ambient   Trilight 1.12  Sky (0.592,0.540,0.486) / Equator (0.468,0.588,0.668) / Ground (0.125,0.111,0.085)
Sun       (1.000, 0.945, 0.836) 강도 1.15 · 각도 (40, 325) · Soft Shadow
SkyDome   SM_Generic_SkyDome_Apoco @ scale 0.5
```

- 스카이돔 원본 반지름 1713m > 카메라 far clip 1000 → 게임 뷰에서 통째로 컬링된다. **scale 0.5로 줄여 해결.** 씬 뷰에만 보이고 게임 뷰엔 안 보이면 이걸 의심할 것
- 돔은 안개에 완전히 먹힌다(끝 200m < 돔 856m). 즉 **하늘색 = 안개색**이다. 하늘 톤을 바꾸려면 돔 머티리얼이 아니라 `fogColor`를 건드려야 한다
- **포스트프로세싱은 의도적으로 안 넣었다** — 로우폴리 캐주얼엔 대부분 해롭거나 중복이다(색보정은 안개·환경광이 이미 한다)

---

# E장 — 다른 에셋 팩으로 맵을 만들 때

1. **모듈 크기를 잰다.** 도로·보도 프리팹의 렌더러 경계를 재서 `Cell Size`에 넣는다 (PolygonApocalypse = 5m)
2. **연석 방향을 확인한다.** 연석이 로컬 +Z를 향하지 않으면 방향을 맞춘 프리팹 배리언트를 만든다
3. **벽 조각 축을 확인한다.** 로컬 +X 두께 / −Z 길이
4. **LODGroup 유무를 확인한다.** PolygonApocalypse·SciFiCity에는 **하나도 없다** — 정적 배칭과 Occlusion Culling에 기대야 한다. 생성기가 타일에 Batching/Occluder/Occludee Static 플래그를 미리 켜둔다
5. **콜리전 껍질 각도를 재본다.** 옆면이 수직이 아니면 A-6의 벽 타기 문제가 재현된다
