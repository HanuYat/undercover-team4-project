# #403 — 씬 전환 로딩 화면 + 라운드 시작 준비 단계 정리

- **날짜:** 2026-07-28
- **브랜치:** `feature/403-loadpanel` (기준: `main` = `e03079e`, 리베이스 완료)
- **커밋 4개:**
  - `909c1a1` 기능 — 씬 전환 로딩 화면 (#403)
  - `2b3d12e` 기능 — 라운드 시작을 준비 단계 뒤로 (NPC 스폰 → 전원 입장 → 지연) (#403)
  - `00ee567` 기록용 문서 (이 파일)
  - `6b091af` Loading화면 프리팹으로 분리
- **상태:** 원격 미푸시 / PR 미생성. 컴파일·플레이 검증 상태는 6장 참고.
- **리베이스 이력:** `e03079e`로 리베이스하며 `RoundManager.cs`에서 **#395(라운드 목표를 검거 수 → 금액으로 전환)와 의미 충돌**이 있었다. 해소 내용은 3-1·5장 참고.

---

## 1. 한 줄 요약

씬 전환이 **동기 로드**(`SceneManager.LoadScene`)라 로딩 화면을 띄울 틈 자체가 없었다. 전환 경로를 **비동기 파이프라인**(`App.LoadSceneAsync`)으로 바꾸고 상주 `LoadingScreen`을 그 앞뒤에 끼워 넣었다. 이어서 "씬을 덮은 동안 준비가 끝나 있어야" 로딩 화면이 의미가 있으므로, **NPC 스폰을 라운드 시작 전(준비 단계)으로 옮기고** 스폰 완료 → 전원 입장 확인 → 시작 지연을 거친 뒤 `StartRound`로 넘어가게 했다.

---

## 2. 커밋 ①  씬 전환 로딩 화면 (`909c1a1`)

### 2-1. 문제

- `AppHelper.LoadScene`이 `SceneManager.LoadScene`(동기)를 호출 → **호출 프레임에 씬이 갈아끼워진다.** 로딩 화면을 켜도 그 프레임에 렌더될 기회가 없어 유저에겐 옛 씬이 멈춘 화면 → 새 씬으로 툭 튀는 것으로 보인다.
- 캔버스를 켜는 것 = 그려진 것이 아니다. "덮었다"를 보장하려면 **최소 몇 프레임을 실제로 흘려보내야** 한다.
- 세션 중에는 **서버만** `App.LoadScene`을 호출한다. 클라이언트는 NGO 씬 동기화로 끌려올 뿐이라 서버의 파이프라인에 들어오지 않는다 → 클라 전용 경로가 따로 필요하다.

### 2-2. `App` — 전환 파이프라인 ([App.cs](../Assets/Scripts/Core/App.cs))

```
정리 훅(OnSceneLoad) → 화면 덮기(렌더 보장까지 대기) → 실제 로드 → 씬 준비 대기 → 페이드 아웃
```

- `App.LoadScene(scene)`은 유지(기존 호출부 전부 그대로) — 내부에서 `LoadSceneAsync(scene).Forget()`.
- `App.LoadSceneAsync(scene)` 신설 — 전환 완료까지 기다려야 하는 호출부만 `await`.
- **재진입 가드** `s_isLoading` — 전환 중 겹친 `LoadScene`은 경고 후 무시(로딩 화면 표시 상태가 꼬이는 것 방지). `ResetStatics`에서 함께 초기화.
- **취소 토큰:** `App`은 MonoBehaviour가 아니라 파괴 토큰이 없다 → `Application.exitCancellationToken` 사용.
- **예외 규칙:** `Title → Lobby`는 덮지 않는다 — 세션 생성 UI가 이미 진행 상태를 보여주고 있다. (`ShouldCoverWithLoadingScreen`)
- `App.WaitUntilSceneReadyAsync(token)` (internal) — 현재 씬 매니저의 준비 대기를 위임. 클라이언트 경로에서 `LoadingScreen`이 같은 대기를 재사용한다.
- `App.UI.Loading` 프로퍼티 추가. **씬을 직접 Play하면 AppBootstrap이 없어 null일 수 있다** → 사용처는 null 가드 필수.

### 2-3. `AppHelper` — 비동기 로드, 경로별 완료 판정 ([AppHelper.cs](../Assets/Scripts/Core/AppHelper.cs))

`LoadScene` → `LoadSceneAsync(scene, token)`. 완료 판정이 경로마다 다르다.

| 경로 | 방식 |
|---|---|
| **오프라인(로컬)** | `LoadSceneAsync` + `allowSceneActivation = false` → `progress >= 0.9`까지 에셋 로드 → 우리가 활성화를 연다 → `op` 완료 대기 |
| **NGO(서버)** | 활성화 시점을 열어주지 않는다 → `SceneManager.OnLoadComplete`를 **LoadScene 호출 전에** 구독해 로컬 완료를 확인 (완료가 먼저 울려 신호를 놓치는 경우 제거) |

- NGO 경로는 **데드라인 폴링**(30초) + `IsListening` 종료 조건 → 세션이 도중에 끊겨도 로딩 화면에 갇히지 않는다. 확인 실패 시 경고 후 그대로 진행. (`SessionFlow.WaitForNetworkShutdownAsync`와 동일 방침)
- 두 경로 모두 마지막에 `k_firstRenderFrames`(2프레임) 흘림 — 활성화 직후 첫 프레임의 셰이더 컴파일·텍스처 업로드 스파이크를 덮은 채로 지나간다.
- 이벤트 해제는 `finally`에서, `net.SceneManager != null` 가드와 함께(세션이 내려가면 SceneManager 자체가 사라진다).

> 활성화 프레임 자체의 스파이크는 없앨 수 없다 — 새 씬 전체의 `Awake/OnEnable/Start`가 한 프레임에 다 돌기 때문. **가리는 것이 최선**이라는 전제로 설계했다.

### 2-4. `LoadingScreen` — 상주 UI ([LoadingScreen.cs](../Assets/Scripts/UI/LoadingScreen.cs), 신규)

`CommonManagerBase` 상속 → AppBootstrap 프리팹 하위(DontDestroyOnLoad)에 배치, Awake에서 자동 등록(R4).

**구동 경로 2개:** (→ #748에서 클라이언트 쪽에 **전환 예고** 경로가 하나 늘어 셋이 됐다. 10장 참고)

| 경로 | 트리거 | 흐름 |
|---|---|---|
| 서버·오프라인 | `App.LoadSceneAsync` | `ShowAsync` → 로드 → `HideAsync` |
| 클라이언트 | NGO `SceneManager.OnLoad` 구독 | `ShowInstant` → `OnLoadComplete` 대기(30초 상한) → 첫 렌더 프레임 흘림 → `App.WaitUntilSceneReadyAsync` → `HideAsync` |

- 서버는 두 경로 모두에 걸리므로 `IsBusy` + `IsServer` 체크로 클라이언트용 자동 경로를 막는다. (→ #748에서 클라 자동 경로의 재진입 가드가 `IsBusy`와 갈라졌다 — 예고로 먼저 덮으면 `IsBusy`가 이미 true라 그것으로 막으면 화면이 안 내려간다. 10장 참고)
- **NGO SceneManager 재훅:** 세션마다 새로 만들어지고 이 객체는 상주 → 생성 시 한 번 걸어둘 수 없다. `Update`에서 참조 비교로 **대상이 바뀐 프레임에만** 다시 건다(`RefreshNetworkHook`).
- **페이드 인은 없다.** 대신 `k_settleFrames`(60프레임) 흘려 "덮은 화면이 최소 한 번 렌더됐다"를 보장한다. (→ #748에서 **3프레임**으로 줄었다. 10장 참고)
- 페이드 아웃 `m_fadeOutSeconds`(기본 0.35초). 스피너 회전·페이드 모두 **`Time.unscaledDeltaTime`** 기준 — 돌발 이벤트 freeze로 `timeScale`이 0이어도 돈다. (→ #582에서 스피너가 사라지고 그 자리를 달리는 캐릭터와 게이지바가 대신한다. 실시간 기준은 그대로 유지. 9장 참고)
- `SetVisible`은 `canvas.enabled` + `CanvasGroup`(alpha/blocksRaycasts/interactable) 동시 제어 → 아래 씬 UI로 클릭이 새지 않는다.
- `SetStatus(string)` — 상태 문구 교체 API(전원 대기 표시 등 후속 확장용). 비워두면 프리팹 기본 문구 유지. (→ #497에서 인수가 `LocalizedString`으로 바뀌었고, #582가 이 API의 첫 사용처를 만들었다 — 9장 참고)

### 2-5. `SceneManagerBase.WaitUntilReadyAsync` 훅 ([SceneManagerBase.cs](../Assets/Scripts/Core/SceneManagerBase.cs))

씬 파일에 배치된 오브젝트는 활성화 프레임에 이미 준비되므로 **기본 구현은 즉시 완료**. 활성화 후에도 프레임에 걸쳐 채워지는 것이 있는 씬만 override. (override 시 자체 상한 필수 — 로딩 화면 고착 방지)

### 2-6. `InGameManager` override ([InGameManager.cs](../Assets/Scripts/Scene/InGameManager.cs))

NPC는 활성화 프레임 이후에 채워지므로 override 했고, **서버와 클라가 기다리는 대상이 다르다.**

- **서버·오프라인** — `NpcSpawner.IsSpawnCompleted` (스포너가 프레임당 한 마리씩 스폰). 스포너가 없는 씬 구성이면 대기 없음.
- **클라이언트** — `NpcSpawner.StartSpawn`이 클라에서 즉시 return하므로 `IsSpawnCompleted`가 영영 false → 그걸 기다리면 타임아웃까지 갇힌다. 그래서 **자기 플레이어 오브젝트 도착까지만** 기다리는 근사로 뒀다.
- 상한 20초, 초과 시 경고 후 로딩 화면 내림.

### 2-7. 프리팹

Canvas(ScreenSpaceOverlay, **sortingOrder 1000**) + CanvasScaler + CanvasGroup + `LoadingScreen` 컴포넌트, 하위에 `Background`(Image) / `Spinner`(Dot0~Dot2 Image) / `StatusText`(TMP). 직렬화 참조 4개(`m_canvas`/`m_canvasGroup`/`m_spinner`/`m_statusText`)와 `m_fadeOutSeconds = 0.35`가 채워져 있다.

> **#582에서 계층이 바뀌었다** — `Spinner`·`Dot0~Dot2`는 삭제되고 `Runner`(RawImage) / `ProgressTrack` + `ProgressFill` / `PercentText`가 추가됐다. 직렬화 참조는 6개(`m_canvas`/`m_canvasGroup`/`m_statusText`/`m_progressFill`/`m_percentText`/`m_runnerStage`) + 문구 2개(`m_defaultStatus`/`m_readyWaitStatus`). 9장 참고.

처음엔 `AppBootstrap.prefab`에 계층을 직접 넣었지만, 커밋 `6b091af`에서 **`Assets/Prefabs/UI/LoadingScreen.prefab`으로 분리**하고 AppBootstrap은 그것을 중첩 참조하도록 바꿨다 — UI를 AppBootstrap 열지 않고 따로 편집할 수 있다.

---

## 3. 커밋 ②  라운드 시작을 준비 단계 뒤로 (`2b3d12e`)

### 3-1. 왜

**로딩 화면이 스폰 완료까지 덮을 수 있게** — 스폰이 `StartRound` 안에 있으면 라운드가 이미 시작된 뒤에 NPC가 프레임마다 튀어나온다.

> 최초 작성 시에는 "범인 배정 후 `CriminalAssigner`의 할당량 보정(#149)이 시작값에 반영된다"는 근거도 함께 들었으나, 리베이스 과정에서 **#395가 할당량(`ArrestQuota`)을 목표 금액(`m_targetFund`)으로 대체하며 그 보정 자체를 없앴다**(값을 깎지 않고 경고만 남기는 방식). 따라서 지금 남은 근거는 로딩 화면 하나다. (5장 참고)

### 3-2. 새 흐름 ([RoundManager.cs](../Assets/Scripts/Round/RoundManager.cs))

```
씬 진입(서버/오프라인)
  → BeginRoundPreparation()          Phase = Preparing 유지
      · Spawner.StartSpawn()
  → ① NPC 스폰 완료 대기 (이 사이에 범인 배정도 끝난다)
  → ② 전원 입장 확인 대기 (기다릴 상대가 있을 때만, 상한 30초)
  → ③ 시작 지연 (기본 3초, ignoreTimeScale)
  → StartRound()                     Phase = InProgress, 제한시간 시작, OnRoundStarted 발행
```

- `BeginRoundPreparation()` — 신규 public 진입점. `m_preparing` 래치로 1회만.
- **`StartRound`는 상태 전환만 한다** — `Spawner.StartSpawn()`이 준비 단계로 빠졌고, 진행도 초기화(`CriminalArrestCount = 0`)는 #395 코드 그대로 `StartRound`에 남겼다. 준비 구간에는 누적될 일이 없다 — `HandleArrestJudged`가 `Phase != InProgress`면 곧바로 return한다.
- **전원 입장 판정** — 서버가 `NetworkSceneManager.OnLoadEventCompleted`를 구독(`m_allPeersLoaded`). 씬 이름이 자기 씬일 때만 처리. 시간 초과 클라가 있어도 진행한다(NGO가 이미 자체 상한을 적용한 뒤라 더 기다려도 안 온다) — 경고만 남긴다.
- **호스트 혼자면 건너뛴다** — `ConnectedClientsIds.Count > 1`이 아니면 NGO 씬 동기화 자체가 없어 완료 신호가 영영 오지 않는다. 솔로 플레이·`DevAutoHost` 개발 흐름이 여기 해당.
- `OnDestroy` override 추가 — `base.OnDestroy()`(R5) + `OnLoadEventCompleted` 해제.
- `ResetRoundState`에 `m_preparing`/`m_allPeersLoaded` 초기화 추가.
- `OnRoundStarted`의 의미가 바뀌었다 — **"스폰 트리거 직후"가 아니라 "Phase가 InProgress로 넘어가는 순간"**. 구독처(UI·연출) 입장에서는 이 시점에 NPC·범인 배정이 이미 끝나 있다.

### 3-3. 새 인스펙터 값

| 필드 | 기본값 | 의미 |
|---|---|---|
| `m_startDelaySeconds` | 3초 | 준비 완료 후 실제 시작까지 대기. 0 이하면 즉시 |
| `m_peerWaitTimeoutSeconds` | 30초 | 전원 입장 확인 상한. 초과 시 경고 후 남은 인원으로 시작 |

> 이 브랜치에 **씬 파일 변경은 없다** — 새 필드는 코드 기본값으로 들어간다. 밸런싱하려면 씬의 RoundManager 인스펙터에서 조정.

### 3-4. `DevAutoHost` ([DevAutoHost.cs](../Assets/Scripts/Test/DevAutoHost.cs))

`StartRound()` → `BeginRoundPreparation()`. 스폰이 준비 단계로 옮겨졌으므로 준비 진입점을 불러야 한다. 호스트 혼자라 전원 입장 대기는 자동으로 건너뛴다.

---

## 4. 타임아웃·상수 한눈에

| 위치 | 상수 | 값 | 용도 |
|---|---|---|---|
| `AppHelper` | `k_activationReadyProgress` | 0.9 | 활성화 대기 시 progress 상한(1.0이 안 된다) |
| `AppHelper` | `k_firstRenderFrames` | 2 | 활성화 직후 스파이크 프레임 흘림 |
| `AppHelper` | `k_networkLoadTimeoutSeconds` | 30초 | NGO 로컬 로드 완료 확인 상한 |
| `LoadingScreen` | `k_settleFrames` | ~~60~~ → **3** | 덮은 화면의 렌더 보장 프레임 — **#748에서 축소**(10장) |
| `LoadingScreen` | `k_loadTimeoutSeconds` | 30초 | 클라 자동 경로 상한 |
| `LoadingScreen` | `k_announceTimeoutSeconds` | 10초 | 전환 예고 뒤 로드가 시작되지 않을 때의 상한 (#748) |
| `LoadingScreen` | ~~`k_spinnerDegreesPerSecond`~~ | ~~180~~ | 스피너 회전 속도 — **#582에서 스피너가 삭제되며 함께 제거** |
| `LoadingScreen` | `k_progressPerSecond` | 2.5 | 게이지 표시값이 목표를 따라가는 속도 (#582) |
| `InGameManager` | `k_readyTimeoutSeconds` | 20초 | 씬 준비 완료 대기 상한 |
| `RoundManager` | `m_peerWaitTimeoutSeconds` | 30초 | 전원 입장 확인 상한 |
| `RoundManager` | `m_startDelaySeconds` | 3초 | 시작 지연 |

**모든 대기는 상한 + 경고 후 진행**이다 — 어떤 신호가 안 와도 로딩 화면에 영구히 갇히지 않는다는 것이 공통 방침.

---

## 5. 의도적으로 뺀 것 / 알려진 한계

**뺀 것 (후속 판단):**
- **페이드 인** — `k_settleFrames`로 렌더 보장만 한다. 페이드 인을 넣으면 그 시간만큼 전환이 늘어난다.
- ~~**진행바**~~ / **셰이더 프리워밍**(`ShaderVariantCollection.WarmUp`) — `SetStatus`만 뚫어두고 실제 진행률 표시는 넣지 않았다. → **진행바는 #582에서 해소**(9장). 셰이더 프리워밍은 여전히 미착수.
- **로딩 화면은 NPC 스폰 완료까지만 덮는다** — 전원 입장 대기 + 3초 지연은 덮지 않는다(플레이어가 맵을 보며 기다리는 그림).

**한계:**
- ⚠️ **스폰 이동의 근거가 하나로 줄었다** — #395가 할당량 보정(#149)을 없앴으므로, 이제 "스폰을 시작 앞으로"를 지지하는 것은 로딩 화면뿐이다. 로딩 화면 요구가 바뀌면 이 배치를 재검토할 근거가 사라진다는 뜻이기도 하다.
- ⚠️ **클라이언트 준비 판정이 근사다** — 자기 플레이어 오브젝트 도착까지만 기다린다. NPC 전원 도착까지 정확히 맞추려면 **서버 권위 "준비 완료" 플래그를 복제**해야 한다.
- ⚠️ **전원 준비 판정이 서버 관점이다** — `OnLoadEventCompleted`는 "클라가 씬을 로드했다"까지만 알려줘, 클라 화면이 아직 로딩 중인데 라운드가 시작될 수 있다. → **#410으로 분리 등록**(권장 방향: `SceneReadyGate : NetworkedManagerBase` + 클라 → 서버 ServerRpc 보고). 로딩 화면의 `대기 중 (2/4)` 표시도 여기서 함께 해결된다.
- ⚠️ **준비 구간(3초) 동안 NPC는 이미 활동한다.** 스폰 직후 freeze는 넣지 않았다 — "3초 뒤 시작"은 타이머와 `OnRoundStarted`만 미루는 것.
- **카운트다운 UI 없음** — `RoundManager`가 NetworkBehaviour가 아니라 `Phase`가 클라에 가지 않는다(코드의 `TODO(#43)`). 다만 `RoundTimerSync`의 NetworkVariable 덕에 클라도 "타이머가 돌기 시작"으로 시작 시점은 인지한다.
- **입력 차단은 `CanvasGroup.blocksRaycasts`까지만** — Input System 액션 차단은 하지 않았다.
- [`RoundEndResetter`](../Assets/Scripts/Round/RoundEndResetter.cs)의 NGO Shutdown 대기 구간은 덮이지 않는다 — `App.LoadScene`이 shutdown 완료 *후*에 불리기 때문. 덮으려면 호출부 수정이 필요해 이슈 완료기준("호출부 수정 0")과 충돌.
- ~~`k_settleFrames = 60`은 60fps 기준 약 1초 — 렌더 보장에 필요한 최소치보다 넉넉하다. 전환 체감 속도가 문제되면 줄일 여지가 있다.~~ → **#748에서 3프레임으로 줄이며 해소**(10장). 이 값이 서버의 전환 시작을 늦추고 있었다는 것이 그때 드러났다.
- `App.UI.Loading`이 null인 경우(씬 직접 Play)는 **덮지 않고 그냥 로드**한다 — 개발 흐름을 막지 않기 위한 선택.

---

## 6. 검증 상태

**확인된 것**
- ✅ 컴파일 클린.
- ✅ 오프라인 씬 전환 1건을 Play로 실측(덮기 → 페이드아웃 정상).

**미확인 (체크리스트)** — 작업 시점에 에디터가 Title Scene에 열려 있어 Main Scene 테스트를 못 돌렸다.

- [ ] **오프라인 단독 Play** — Title → Lobby(덮지 않음) / Lobby → InGame(덮음) 동작.
- [ ] **멀티(MPPM) 경로** — 호스트·클라 양쪽에서 로딩 화면이 뜨고 내려가는지. **특히 클라이언트 자동 경로(NGO 씬 이벤트)는 미검증.**
- [ ] **라운드 준비 순서** — NPC가 로딩 화면 아래에서 다 스폰되는지, 화면이 내려간 뒤 3초 후 라운드 시작 로그가 찍히는지.
- [ ] **전원 입장 대기** — 클라가 늦게 로드될 때 호스트가 기다리는지, 30초 초과 시 경고 후 진행하는지.
- [ ] **재진입 가드** — 전환 중 다른 `LoadScene`이 겹칠 때 경고만 남고 화면이 꼬이지 않는지.
- [ ] **#395 회귀** — 리베이스로 목표 금액 방식과 합쳐졌으므로: 목표 진행도(`CurrentFund`) 표시, 본부 종료 버튼 활성 조건, 제한시간 종료 시 성공/실패 판정이 그대로 동작하는지.

---

## 7. 변경 파일 목록

| 파일 | 내용 |
|---|---|
| `Assets/Scripts/UI/LoadingScreen.cs` | **신규** — 상주 로딩 화면. 서버 파이프라인 + 클라 NGO 이벤트 2경로 |
| `Assets/Scripts/Core/App.cs` | `LoadSceneAsync` 파이프라인, 재진입 가드, `App.UI.Loading`, `WaitUntilSceneReadyAsync` |
| `Assets/Scripts/Core/AppHelper.cs` | 동기 → 비동기 로드. 로컬은 활성화 제어, NGO는 `OnLoadComplete` 폴링(30초) |
| `Assets/Scripts/Core/SceneManagerBase.cs` | `WaitUntilReadyAsync` 훅 추가(기본 즉시 완료) |
| `Assets/Scripts/Scene/InGameManager.cs` | 준비 대기 override — 서버는 스폰 완료, 클라는 플레이어 오브젝트 도착 |
| `Assets/Scripts/Round/RoundManager.cs` | `BeginRoundPreparation` 신설, NPC 스폰을 준비 단계로 이동, 전원 입장 대기, 시작 지연 |
| `Assets/Scripts/Test/DevAutoHost.cs` | `StartRound` → `BeginRoundPreparation` |
| `Assets/Prefabs/UI/LoadingScreen.prefab` | **신규** — 로딩 화면 캔버스 계층(sortingOrder 1000) + 참조 연결 |
| `Assets/Prefabs/AppBootstrap.prefab` | 위 프리팹을 중첩 참조 |
| `docs/403-loading-screen.md` | **신규** — 이 문서 |

---

## 8. 관련 문서 / 이슈

- 구조 규칙: [docs/architecture.md](architecture.md) — R1(App 파사드), R4(매니저 등록), R5(`base` 호출), R7(`App.LoadScene` 단일 경로)
- 관련 이슈: **#403**(본건), #395(라운드 목표를 금액으로 — 리베이스 충돌 상대), #410(전원 준비 판정 후속), #56(스폰 서버 권위), #214(로비 별도 씬), #43(라운드 UI 동기화)
---

## 9. 후속 — #582 진행률 게이지바 + 달리는 캐릭터

- **날짜:** 2026-08-10 · **브랜치:** `feature/582-loading-screen-revamp` · **이슈:** #582 · **PR:** #583
- 5장의 보류 항목 **"진행바"** 를 해소하고, 검은 배경 + 회전 스피너를 야간 경찰서 배경 + 달리는 로봇으로 교체했다.

### 9-1. 진행률은 새로 만든 값이 아니다

세 로드 경로가 이미 `AsyncOperation`을 손에 들고 있었고, 그중 클라이언트 경로는 `LoadingScreen.HandleNetworkLoad`가 **인자로 받아 버리고 있었다.** 새 폴링이나 RPC 없이 그 값을 화면까지 전달한다.

| 경로 | 진행률 출처 | 주의 |
|---|---|---|
| 오프라인 | `LoadLocalAsync`의 `op.progress` | 활성화 대기 탓에 **0.9가 상한**이라 `k_activationReadyProgress`로 나눠 0~1로 편다 |
| 서버 NGO | `SceneManager.OnLoad`를 새로 구독해 로컬 핸들만 집는다 | NGO는 활성화 시점을 열어주지 않아 **읽기 전용**이다. 확인된 완료(`localLoaded`)에만 100%를 보고한다 — 타임아웃·세션 끊김에는 보고하지 않는다 |
| 클라이언트 NGO | `HandleNetworkLoad`가 받던 `AsyncOperation` | 이 경로가 클라의 **유일한** 진행률 출처다 (클라는 `App.LoadScene`을 타지 않는다) |

전달 수단은 `AppHelper.LoadSceneAsync(scene, token, Action<float> onProgress)`다. BCL 델리게이트라 `AppHelper`가 UI를 알지 않는다.

### 9-2. 게이지에 태우는 구간과 태우지 않는 구간

**씬 로드가 게이지 전체(0~100%)를 쓴다.** 그 뒤의 런타임 스폰 대기는 게이지에 태우지 않고 문구로 알린다(`BeginSceneReadyWait` → `Common.Loading.Preparing`).

처음에는 씬 로드에 0.9를 주고 남은 0.1을 스폰 대기에 배정했으나 **얻는 것이 없어 되돌렸다.** 대기가 끝나는 즉시 `HideAsync`가 이어져 0.9→1.0이 한 프레임도 돌지 못하고, 클라이언트는 `k_settleFrames`(약 1초)만큼 90%에 멈춰 있다가 사라졌다. "90%에서 멈추는 로딩바"라는 인상만 남는 구조였다. (PR #583 리뷰 🟠-1)

- 진척을 실제로 알 수 있게 되면(#410 `SceneReadyGate`가 `대기 중 (2/4)`를 내주는 방향 — 5장 참고) 그때 게이지로 되돌릴 여지가 있다.
- 표시값은 목표를 `MoveTowards`로 추종한다(`k_progressPerSecond = 2.5`) — 로드 진행률은 계단식으로 튀어 그대로 대면 바가 순간이동한다.
- **되감기 금지**(`SetTargetProgress`의 `Mathf.Max`). 구동 경로가 둘이라 늦게 도착한 낮은 값이 섞일 수 있고, 줄어드는 퍼센트는 그 자체로 고장으로 읽힌다. **이 가드를 "불필요한 방어"로 보고 지우지 말 것.**

### 9-3. 달리는 캐릭터 — 별도 무대 + RenderTexture

2D 런사이클 스프라이트가 프로젝트에 없어 3D를 쓴다. `Assets/Prefabs/UI/LoadingRunnerStage.prefab`(신규)이 `AppBootstrap` 하위 **y=-5000**에 붙고, `LoadingScreen.SetVisible`이 무대를 함께 토글한다(켠 채 두면 로딩이 아닐 때도 매 프레임 `RenderTexture`를 그린다).

| 설정 | 값 | 이유 |
|---|---|---|
| 레이어 | `LoadingStage` (슬롯 11) | 카메라 `cullingMask`를 이 레이어로 제한. y=-5000 거리와 함께 이중으로 게임 카메라에서 떼어 놓는다 |
| 카메라 | 직교, 배경 알파 0 | UI 그라데이션/배경 위에 합성된다 |
| 머티리얼 | **URP/Unlit** | Synty 셰이더는 씬 조명을 받는데, 로딩 중은 하필 씬이 언로드·로드되며 조명이 오락가락하는 구간이라 캐릭터 밝기가 튄다. 조명 컬링 마스크로 막으려면 **씬마다 손을 대야 하고 새 씬은 조용히 빠진다** — 아예 조명에서 뗐다. 대가는 음영 없는 평평한 룩 |
| `Animator.updateMode` | `UnscaledTime` | 이 화면 전체가 실시간 기준이다(2-4 참고) |
| `Animator.cullingMode` | `AlwaysAnimate` | 메인 카메라에 안 보이는 위치라 기본 컬링이면 T포즈로 굳는다 |
| `SkinnedMeshRenderer.updateWhenOffscreen` | true | 같은 이유 |

Synty 원본(`SM_Gen_Chr_Robot_01.prefab`)은 **수정하지 않았다** — 중첩 프리팹 인스턴스로 두고 레이어·머티리얼만 오버라이드했다.

캐릭터 위치는 눈대중이 아니라 역산했다. `RenderTexture`(512²) 안에서 달리기 사이클이 쓰는 세로 구간이 27~466이므로, 크기 `S`일 때 **사이클 최저 발 = 중심 − 0.4473·S**다. 게이지바 윗변에서 12만큼 위에 두려면 `y = -214 + 0.4473·S` (현재 `S = 180`). 크기를 바꾸면 이 식으로 y를 다시 계산할 것.

### 9-4. 배경

`Assets/Art/Loading/LoadingBackdrop_Precinct.png` — 야간 경찰서 로비 일러스트. 그림이 4:3, 화면이 16:9라 `AspectRatioFitter`(EnvelopeParent)로 채우고 넘치는 위아래를 버린다(천장 램프·앞쪽 바닥 일부가 잘린다).

밝은 낮 버전을 먼저 넣어 봤으나 폐기했다 — 흰 게이지바가 밝은 타일 바닥에 묻히고 Unlit 회색 로봇도 배경에 녹는다. 야간 버전으로 확정하며 UI 색을 반전했다(문구·퍼센트를 밝은 흰색, 트랙을 흰색 22%).

> ⚠️ **#584(상점 씬을 `PolygonPoliceStation`으로 제작)와 조율 대상.** 이 배경은 경찰서 실내 일러스트고 #584는 같은 공간을 3D로 짓는다. 방치하면 로딩 화면의 경찰서와 실제 상점이 서로 다른 장소로 보인다. 어차피 4:3 크롭 때문에 한 번 손댈 자리이므로, #584의 3D 실내가 완성된 뒤 그 스크린샷으로 교체하는 방향이 크롭 문제까지 함께 해소한다.

### 9-5. 남은 것

- **퍼센트 문구가 로컬라이제이션 테이블을 우회한다** — `Mathf.RoundToInt(...) + "%"`. 숫자+기호뿐이라 예외로 뒀으나 방침은 **#525(#497 Phase 3)** 에서 함께 정한다. 표시 정수가 바뀔 때만 문자열을 만든다.
- **`"출동 중"` 문구가 모든 전환에 쓰인다** — Shop 복귀에도 "출동 중"이 뜬다. **#509**(정산 → 상점 복귀)가 그 경로를 건드리므로 함께 정리하면 좋다. `SetStatus`가 `LocalizedString`을 받으므로 호출부만 늘리면 된다.
- **러너가 플레이어 색을 반영하지 않는다** — 고정 Unlit 머티리얼이다. **#432**(로봇 색 커스터마이징)가 색을 인덱스로 `PlayerPrefs`에 저장하므로 읽을 수는 있지만, 게임 쪽 색칠 경로와 별개로 손대야 한다.
- **셰이더 프리워밍** — 5장의 보류 항목 중 이쪽은 그대로 남았다.
- `LoadingScreen` 실코드 약 240줄 — 기준선(250) 바로 아래다. `[Header]` 4그룹으로 관심사가 나뉘어 있고(참조/진행률/캐릭터/연출), 다음에 무엇을 더 얹으면 분리 검토 대상이 된다.

---

## 10. 후속 — #748 호스트·클라 로딩창 동시 전환

**증상:** 세션 중 씬 전환에서 호스트 화면만 먼저 로딩창으로 바뀌고 클라이언트는 1초쯤 뒤에 바뀐다.

### 10-1. 원인은 상수가 아니라 순서였다

2장의 구동 경로 두 개가 **서로 다른 시점에 걸린다**는 것이 그대로 시차가 된다.

| | 덮는 시점 |
|---|---|
| 호스트 | `App.LoadSceneAsync`가 `ShowAsync()`로 덮고 → `k_settleFrames` 대기 → **그 다음에야** `net.SceneManager.LoadScene()` |
| 클라 | 그 `LoadScene()`이 만든 **NGO 씬 이벤트를 받고 나서야** `ShowInstant()` |

클라가 덮으려면 호스트가 대기를 끝내야 한다. 즉 **`k_settleFrames`가 통째로 시차**이고, 이 구조에서는 클라가 늦는 것이 정상 동작이다 — 상수만 줄여서는 없앨 수 없다.

### 10-2. 전환 예고 — 덮기 전에 먼저 알린다

[`SceneTransitionAnnouncer`](../Assets/Scripts/Network/SceneTransitionAnnouncer.cs)(신규)가 서버에서 `[Rpc(SendTo.NotServer)]`를 쏘고, 받은 클라는 즉시 `LoadingScreen.CoverForIncomingSceneChange()`로 덮는다. `App.LoadSceneAsync`가 **`ShowAsync` 직전에** 부르므로 시차가 `k_settleFrames` → RTT로 줄어든다.

- **자리는 `SessionState.prefab`** — 씬 전환을 알리는 쪽이 씬과 함께 죽으면 알릴 수 없다. `TeamFund`·`RoundProgress`와 같은 세션 상주 홀더고, App 등재도 같은 사정이다(런타임 스폰이라 인스펙터 배선 불가 — architecture §4 "세션 상주 홀더" 예외).
- **덮지 않는 전환은 예고도 하지 않는다** — `ShouldCoverWithLoadingScreen` 안쪽에서 부른다(Title → Lobby 제외).
- 세션 밖(오프라인·씬 직접 Play)에서는 `App.Net.SceneTransition`이 null이라 아무 일도 하지 않는다.

### 10-3. "덮여 있다"와 "완료를 기다린다"를 갈랐다

이 작업의 실질적인 함정. 클라 자동 경로의 재진입 가드가 `IsBusy`였는데, 예고로 미리 덮으면 `IsBusy`가 이미 true라 **자동 경로가 통째로 건너뛰어지고 로딩창이 영영 안 내려간다**(완료 대기·`HideAsync`가 전부 그 경로 안에 있다).

가드를 `m_isTrackingNetworkLoad`(자동 경로가 도는 중인가)로 갈랐다. `IsBusy`는 "화면이 떠 있는가"로만 쓴다.

- 예고와 NGO 이벤트의 **도착 순서는 뒤바뀌어도 된다** — 이벤트가 먼저면 자동 경로가 플래그를 세우고, 뒤늦은 예고는 `IsBusy`를 보고 빠진다.
- **예고만 오고 로드가 시작되지 않는 경우**(로드 실패·세션 끊김)를 위해 `k_announceTimeoutSeconds`(10초) 감시를 붙였다. 자동 경로의 30초 상한은 그 경로에 들어간 뒤에만 돌아서 여기를 대신해 주지 못한다.

### 10-4. `k_settleFrames` 60 → 3

5장에 "줄일 여지"로 적어 뒀던 값이다. 렌더 보장에 필요한 것은 한두 프레임인데(`AppHelper.k_firstRenderFrames`가 2다) 60(약 1초)으로 잡혀 있었고, 그만큼 **서버의 NGO 로드 시작 자체가 늦어** 전환이 굼뜨게 느껴졌다. 예고를 넣어도 이 대기는 남으므로 함께 줄였다.

### 10-5. 남은 것

- 예고는 **덮기만** 알린다 — 어느 씬으로 가는지는 싣지 않는다. 클라의 문구를 목적지별로 가르려면(9-5의 "출동 중" 항목) 그때 인자를 늘리면 된다.
- `RoundEndResetter`의 NGO Shutdown 대기 구간이 덮이지 않는 것(5장)은 그대로다 — 예고는 `App.LoadScene` 파이프라인 안쪽이라 그 구간보다 뒤에 있다.
