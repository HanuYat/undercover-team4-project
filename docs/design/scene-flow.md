# 씬 전환 흐름 & 게임 루프 골격 (#214)

> 설계 정본. 씬 로드·전환 경로와 그 사이 네트워크 세션 유지, 씬 간 이월 데이터 구조를 정의한다.
> 게임 규칙은 [GDD.md](../GDD.md), 코드 구조 규칙은 [architecture.md](../architecture.md)가 정본 — 이 문서는 **씬 흐름**만 다룬다.
> 관련 선행 설계: [network-lifecycle.md](network-lifecycle.md) (세션 소유권·teardown).

## 1. 목표 & 범위

**다음 빌드 목표(게임 루프 완성)의 골격.** 메인메뉴 → 로비(+상점) → 게임맵 → 정산 → 로비 복귀로 이어지는 씬 전환 경로를, **세션을 유지한 채 반복 가능**하게 만든다. (GDD 3-2 라운드 흐름)

### 범위 안 (이 이슈)
- 씬 세트 확정 + `EScene` 확장 + `AppHelper` 매핑 + **플레이스홀더 씬** 생성
- 4개 전환 경로의 **트리거·주체·세션 동작** 배선 (호스트 권위, `App.LoadScene` 단일 경로)
- **세션 유지**: 루프가 세션을 내리지 않는다 (현재 `RoundEndResetter`의 teardown을 씬 전환으로 교체)
- **이월 데이터 구조 확정**: 씬을 넘어 살아남는 상주 홀더 (팀 자금으로 실증)

### 범위 밖 (별도 이슈)
- 로비 대기 공간 콘텐츠 #154 · 상점 UI #182 · 정산 UI 내용 #107/#183 · 메인메뉴 폴리싱 · 게임맵 레벨 구성
- 본부 대기/카운트다운 연출 상세 (골격은 "게임 씬 진입 → 라운드 시작"까지만)

이 이슈는 **경로와 뼈대**만 깐다 — 각 씬의 내용물은 위 담당 이슈가 채운다. (최소핵심)

## 2. 확정 씬 모델

`EScene`: `None / Title / Lobby / Shop / Game` (5개). 기존 `InGame` → `Game`으로 개명, `Lobby`·`Shop` 신설. `Title`은 세션 관문=메인메뉴이므로 유지.

- **Lobby** = 세션 생성 후 **최초 플레이어 대기** 공간 (세션당 1회). 호스트 '시작'으로 상점으로 넘어간다.
- **Shop** = **인게임 허브** — 라운드 사이 아이템 준비 공간(상점). 호스트 '출동'으로 게임에 진입하고, 라운드가 끝나면 **다시 이 씬으로 복귀**한다. 반복 루프의 진입점.
- **Game** = 라운드 진행. **정산(Settlement)은 별도 씬이 아니라 게임 씬 내 UI 오버레이 단계** — 라운드 종료 후 게임 씬에서 정산 UI를 잠깐 띄운 뒤 상점으로 전환한다.

```
              세션 생성            호스트 '시작'         호스트 '출동'
   Title ───────────────▶ Lobby ───────────────▶ Shop ───────────────▶ Game
  (메인메뉴)  클라 코드참가  (최초 대기)            (상점=허브)             (라운드 진행)
     ▲       → NGO 동기 입장                          ▲                     │
     │ 로그아웃(SessionTeardown)                       └─ 라운드 종료(정산 UI)┘
     └── 세션 내림 = 루프 이탈 ────────────────         세션 유지 · Shop ↔ Game 반복
```

- **루프 안(Shop ↔ Game)**: 세션·NGO 유지. 씬만 `App.LoadScene`(서버) → NGO Single 모드 동기화. 로비는 최초 1회만 거친다.
- **루프 밖(→ Title)**: 세션을 내리는 유일한 경로 = 로그아웃(`SessionTeardown.LeaveToMainAsync`, 기존 그대로). 게임 루프 종료가 아니라 사용자의 명시적 이탈이다.
- **상점 UI 내용**(아이템 구매)은 #182 — 이 문서/#214는 **상점을 하나의 씬(허브)으로 두는 흐름**만 정하고, 그 안의 상점 UI는 #182가 채운다.

## 3. 전환 경로 (트리거 · 주체 · 세션 동작)

| # | 전환 | 트리거 | 주체 | 세션 동작 |
|---|------|--------|------|-----------|
| T1 | Title → Lobby | 세션 생성 완료 | 호스트 (`TitleManager`) | NGO start 직후 서버가 Lobby 로드. 이후 참가 클라는 NGO 씬 동기화로 자동 진입. 세션 **잠금 해제**(접속 허용) |
| T2 | Lobby → Shop | "시작" 버튼 | 호스트 (`LobbyManager`) | 세션 유지, 서버가 Shop 로드. 상점도 **접속 허용**(잠금 해제 유지) |
| T3 | Shop → Game | "출동" 버튼 | 호스트 (`ShopManager`) | 세션 유지, 서버가 Game 로드. **세션 잠금**(게임 진행 중 신규 접속 차단, B안) |
| T4 | Game → (정산) | 라운드 종료 | 서버 (`RoundManager.OnRoundEnded`) | 씬 전환 없음 — 게임 씬에서 정산 UI 오버레이 표시 |
| T5 | (정산) → Shop | 정산 표시 시간 경과 | 서버 (`RoundEndResetter`) | 세션 유지, 서버가 Shop 로드. Shop 진입 시 **잠금 해제**(다시 접속 허용) |

루프 = **Shop ↔ Game** (T3·T4·T5 반복). 로비는 T1·T2로 최초 1회만.

- **모든 씬 전환은 서버(호스트)만** 개시한다 — `AppHelper`가 세션 중이면 서버 외 호출을 거부(R7 단일 경로). 클라이언트는 NGO 동기화로 따라온다.
- **세션 잠금(접속 차단)**: 로비·상점에선 해제(접속 허용), 게임 씬에서만 잠금(B안). 잠금은 T3(출동)에서, 해제는 각 Lobby/Shop 진입(Start)에서 — UGS 세션 `IHostSession.IsLocked` + `SavePropertiesAsync`.
- **라운드 시작**: 로비/상점과 게임맵이 다른 씬이므로, 라운드 시작은 **게임 씬 진입 시점**에 건다(§5).
- **RoundEndResetter 변경점**: 기존엔 라운드 종료 시 **세션을 내리고(LeaveAsync) Title 복귀** → **세션 유지 + 정산 표시 후 Shop 복귀**로 교체(§4).

## 4. 세션 유지 원칙 — RoundEndResetter 재정의

**현재 루프는 가짜다**: 라운드가 끝나면 `RoundEndResetter`가 세션을 통째로 내리고 Title로 돌아간다. 이는 #214 완료기준 2(세션 유지·전원 동반 이동)와 정면 충돌한다. `RoundEndResetter`는 스스로 "정식 순환(#107/#183)이 붙기 전까지의 자리표시"라고 명시돼 있으므로, #214가 그 정식 전환을 제공한다.

**교체 방침:**
- 서버: `RoundManager.OnRoundEnded` 수신 → 정산 표시 시간 대기(placeholder) → `App.LoadScene(Shop)`. **세션·NGO·Vivox 손대지 않음.** (로비가 아니라 상점=허브로 복귀 — 루프 = Shop ↔ Game)
- 클라이언트: 별도 신호 불필요 — 서버의 NGO 씬 동기화로 함께 Shop으로 이동. (현재 클라가 `OnConnectionLost`를 리셋 신호로 삼던 우회로 제거 — 세션이 안 끊기므로 발화하지 않는다)
- 로그아웃(`SessionTeardown`)은 **그대로 유지** — 세션을 내리는 유일한 경로. 루프와 무관.
- 테스트 씬 폴백(`EScene.None`일 때 자기 씬 재로드)은 유지 — 정식 씬 흐름 밖.

이 컴포넌트는 이제 "리셋"이 아니라 "라운드 종료 → 정산 → 로비 복귀"를 담당하므로 역할에 맞게 정리한다(이름/주석). 정산 UI **내용**은 #107/#183 — 여기서는 시간 대기 placeholder만.

## 5. 라운드 시작 트리거 이동

현재: `RoundManager.OnNetworkSpawn`에서 세션 중이면 대기하다가, `LobbyManager.StartGame()`(같은 씬)이 `StartRound()`를 부른다.

변경: 로비와 게임맵이 **다른 씬**이 되므로, 라운드 시작은 **게임 씬 진입(서버)** 시점에 건다.
- `GameManager`(구 `InGameManager`, 게임 씬 진입점)가 서버에서 `RoundManager.StartRound()`를 호출한다.
- 이슈의 "본부 대기 → 카운트다운 → 타이머 시작" 연출은 이 진입 지점에 훅으로 얹을 자리만 마련(상세는 범위 밖). 골격은 **게임 씬 진입 = 라운드 시작**.
- `Network/LobbyManager`의 `StartRound` 호출 역할은 제거되고, 이 클래스는 "출동 버튼 → Game 로드"만 담당(§3 T2). Game 도메인의 라운드 시작 책임과 분리.

## 6. 이월 데이터 구조 — 상주 NetworkObject 홀더

**문제**: `TeamFund`는 현재 게임 씬 NetworkObject라 `OnNetworkSpawn`마다 초기값으로 리셋된다. Lobby↔Game을 오갈 때마다 자금이 사라져 이월이 불가능하다.

**구조 (완료기준 3 = 이 구조 확정):**
- 세션 시작 시 서버가 **상주 홀더 프리팹을 1회 동적 스폰**하고, `DontDestroyOnLoad`로 승격한다. NGO Single 모드 씬 로드는 DDOL로 마킹된 NetworkObject를 파괴하지 않고 이월한다 — 씬을 넘어 값이 살아남는다.
- 이 홀더가 **팀 자금 `NetworkVariable<int>`(+ 이후 인벤토리)의 소유자**가 된다. 스폰(세션 시작) 시 1회만 초기화 — 씬 전환으로는 리셋되지 않는다.
- `TeamFund`를 이 상주 홀더로 이전한다. 상점(#182)·정산(#107)이 참조하고 검거 보상(ArrestJudge)이 가산하는 **다도메인 참조 → App 등록 자격(R3)**: `NetworkedManagerBase` 상속 + App 필드/프로퍼티 추가(예: `App.Game.TeamFund`).
- **씬-로컬 배선 재수립**: 자금 값은 상주하지만, 보상을 주는 `ArrestJudge`는 게임 씬 오브젝트다. 홀더는 게임 씬 진입 시 `ArrestJudge` 구독을 걸고 이탈 시 해제한다(`App.OnSceneLoaded`/despawn 훅). 값은 상주, 씬-로컬 연결은 씬 생명주기 따라 재설정.

**스폰 시점/주체**: `SessionManager.WithRelayNetwork()`가 NGO를 start한 직후 서버가 홀더를 스폰(NGO listening 확인 후). 클라는 스폰 복제로 자동 수신. 프리팹은 `DefaultNetworkPrefabs.asset` 등록.

**대안(반려)**: 상주 C# 매니저 + 씬마다 값 재주입 — 수동 재적용 코드가 늘고 NGO 동기화를 씬 오브젝트에 다시 의존해야 해 이월 이점이 흐려짐. NGO 정석인 DDOL 홀더 채택.

## 7. 구현 단계 (순서)

1. **`EScene` 확장 + `AppHelper` 매핑**: `Lobby` 추가, `InGame`→`Game` 개명. `App.SceneFlow`/`App.UI`의 `InGame` 접근자·`ToSceneName`/`FromSceneName` 갱신. (컴파일러가 파급 지점 전부 잡아줌)
2. **플레이스홀더 씬 생성**: `Lobby.unity` 신설(로비 스켈레톤 — 출동 버튼만), 기존 "Main Scene" → Game 역할. 최소 진입점 매니저 배치.
3. **씬 매니저 정리**: `InGameManager`→`GameManager`(게임 씬 진입 시 `StartRound`), `LobbyManager`를 로비 씬 매니저로(출동 버튼→`LoadScene(Game)`). `TitleManager.StartGame`→`Lobby`.
4. **상주 이월 홀더**: 홀더 프리팹 + `TeamFund` 이전 + App 등록 + 세션 시작 시 서버 스폰 + 씬-로컬 `ArrestJudge` 재배선.
5. **T3/T4 교체**: `RoundEndResetter`를 세션 유지 정산→Lobby 전환으로 재작성(정산 대기 placeholder).
6. **검증**: MPPM 2~3인으로 Title→Lobby→Game→정산→Lobby 왕복 + 전원 동반 이동 + 자금 이월 확인.

## 8. 완료 기준 매핑

- [ ] 메인메뉴→로비→게임맵→정산→로비 복귀 전환이 끊김 없이 동작 → §3 T1~T4 + §7-2/3/5
- [ ] 전환 중 세션 유지·전 클라 동반 이동 → §4 (teardown 제거) + §3(서버 개시·NGO 동기)
- [ ] 씬 간 이월 데이터 전달 구조 확정 → §6 (상주 DDOL 홀더)

## 8-1. 연결 승인(Connection Approval)은 세션 레벨 — 조인 가능한 모든 씬에 승인자 필요 ⚠️

**발견(테스트 중)**: `NetworkManager.prefab`에 `ConnectionApproval: 1`이 켜져 있어, 서버는 모든 원격 클라 접속을 `ConnectionApprovalCallback`으로 승인해야 한다. 그런데 이 콜백은 **씬-로컬 컴포넌트 `PlayerSpawnManager`(Main/Test 씬에만 존재)** 가 등록한다.

기존엔 호스트가 세션 생성 직후 Game(Main Scene)으로 직행 → 클라는 늘 Main Scene에서 승인돼 문제가 안 드러났다. 그러나 **로비는 클라가 들어와 대기하는 씬**이므로, 로비에 승인자가 없으면 클라 접속이 승인되지 못하고 서버가 끊는다(증상: 클라 `ProcessNetworkMetadataAsync` 예외 → "Failed to start the network manager", 호스트는 "붙었다 끊김").

**구조적 결론**: 승인은 **세션 레벨** 관심사다. 세션 유지 루프에서 **클라가 조인 가능한 모든 씬(최소 Lobby, Game)에 승인 콜백 제공자가 있어야** 한다.
- **단기(채택)**: 클라가 조인 가능한 모든 씬(**Lobby·Shop** — B안: 로비·상점 접속 허용)에 `PlayerSpawnManager` 추가 — 콜백 등록 + 플레이어 스폰. 스폰 위치 지정은 #154.
- **후속(권장 구조)**: 승인 콜백을 **상주(DDOL) 매니저에 등록**해 씬 무관하게 항상 유효화하고, 스폰 **위치**만 씬별 `RepositionConnectedPlayers`로 분리(승인=세션레벨 / 위치=씬레벨). PlayerSpawnManager(#51) 리팩터라 팀 조율 후.

> **플레이어는 상점(Shop)부터 스폰한다 — 로비·타이틀은 UI 대기(플레이어 없음).** (확정)
> `PlayerSpawnManager.m_spawnPlayers` 플래그로 씬별 제어:
> - **Title·Lobby** = `false` — 연결 승인만(`CreatePlayerObject=false`), 플레이어 안 만듦. (로비는 조작 없는 UI 대기)
> - **Shop·Game** = `true` — 씬 진입 시 그 피어에 플레이어가 없으면 스폰(`SpawnAsPlayerObject`), 있으면 재배치. 플레이어는 `destroyWithScene:false`라 Shop↔Game 루프 내내 유지(§8-2/§8-3).
> 승인 콜백은 조인 가능한 모든 씬(Title·Lobby·Shop)에 있어야 하므로 각 씬에 `PlayerSpawnManager`를 두되, 플레이어 생성 여부만 플래그로 가른다. 상점의 실제 룸·바닥·상점 UI는 #182/#154 콘텐츠.

## 8-2. 플레이어는 씬을 넘어 이월된다 — 씬 전환마다 재배치(원격 오너 포함) 필요 ⚠️

**발견(테스트 중)**: 플레이어 NetworkObject는 세션 유지 씬 전환에서 despawn되지 않고 이월된다. 그런데 `PlayerMovement.ApplyServerSpawnPose`는 `OnNetworkSpawn`에서만 불리므로, 재스폰되지 않는 이월 플레이어는 새 씬의 스폰 위치가 적용되지 않는다.

각 씬의 `PlayerSpawnManager`가 `RepositionConnectedPlayers` → `ServerReposition`으로 재배치하지만, 기존 `ServerReposition`은 **호스트(IsOwner)만** `SetPose`하고 **원격 클라는 NetworkVariable만 갱신**해 실제로 안 옮겨졌다(NetworkTransform 오너 권한). 옛 흐름은 원격 클라가 늘 Game에서 approval로 새로 스폰돼 이 경로를 안 탔기 때문에 안 드러났다.

**수정 2가지 (둘 다 필요)**:
1. `ServerReposition`이 원격 클라(비오너)에게 `ApplyPoseRpc`(`[Rpc(SendTo.Owner)]`)로 위치 적용을 넘긴다 — `ServerTeleport`와 동일 패턴. 원격 오너가 스스로 `SetPose`해야 전 피어에 전파된다.
2. **재배치 타이밍 = 피어별 로드 완료 시점**. 서버가 *자기* 씬 로드 시점에 원격 클라를 밀면, 클라는 아직 이전 씬(로비, 바닥 없음)에 있어 좌표가 게임 씬으로 안 살아남는다(낙하). `PlayerSpawnManager`가 `NetworkSceneManager.OnLoadComplete(clientId, sceneName, mode)`를 구독해 **그 클라가 이 씬 로드를 끝낸 순간** 재배치한다(서버 자신은 Start에서 즉시). 그래야 `active`가 대상 씬(바닥 존재)일 때 `SetPose`가 적용된다.

**구조적 함의**: 세션 유지 루프에서 **플레이어는 씬마다 재스폰되지 않으므로**, 각 피어가 새 씬 로드를 끝낸 시점에 (원격 오너 포함) 명시적 재배치가 필요하다. 로비/게임 각 씬의 스폰 포인트는 콘텐츠 이슈(#154 등)에서 지정.

## 8-3. 씬-바운드 스폰물은 `destroyWithScene: true` — 세션 유지 씬 전환에서 따라오지 않게 ⚠️

**발견(테스트 중)**: 동적 스폰된 NetworkObject는 기본 `destroyWithScene=false`라, Single 모드 씬 전환에서 파괴되지 않고 새 씬으로 **이월된다**. 플레이어에겐 이게 필요하지만(이월 대상), NPC 등 게임 씬 소속 엔티티가 로비로 따라오는 문제가 된다. (기존 `ResetSpawnState` 정리는 Shutdown+StartHost 재시작 전용이라 세션 유지 루프에선 안 돈다.)

**원칙**:
- **플레이어** — `destroyWithScene: false`(기본) → 씬 넘어 유지(§8-2 재배치와 짝).
- **NPC·라운드 중 스폰물(드랍 아이템 등)** — `NetworkObject.Spawn(destroyWithScene: true)` → 게임 씬 언로드 시 자동 despawn(전 클라 전파). `NpcSpawner`가 이 방식으로 스폰.

세션 유지 루프에 새 동적 스폰물을 추가할 때는 "이월 대상인가, 씬과 함께 사라져야 하는가"를 정해 `destroyWithScene`를 맞춘다.

## 9. 미결 / 리스크

- **NGO DDOL 이월 실검증**: 동적 스폰 NetworkObject를 DDOL로 올렸을 때 `LoadScene(Single)` 동기화에서 파괴되지 않고 값이 보존되는지 MPPM으로 1차 확인 필요(정석이나 SDK 버전 거동 확인).
- **씬 오브젝트 배선**: 씬 매니저 개명/이동은 씬 파일의 컴포넌트 배선(Missing Script) 리스크 — 스크립트 개명 전 씬 guid 참조 확인 (#166 교훈). Editor 수동 작업 병행.
- **로비 스폰/텔레포트**: 로비 씬 진입 시 플레이어 배치 — #154 범위와 경계. #214는 씬 전환까지만, 스폰 위치는 로비 콘텐츠에서.
- **정산 표시 시간**: placeholder 상수 → #107/#183가 실제 UI·시간 확정.

---
*작성: 2026-07-22 · 근거: #214 · 현행 코드(App/AppHelper/RoundEndResetter/TeamFund/LobbyManager) 조사*
