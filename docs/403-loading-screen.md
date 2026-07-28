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

**구동 경로 2개:**

| 경로 | 트리거 | 흐름 |
|---|---|---|
| 서버·오프라인 | `App.LoadSceneAsync` | `ShowAsync` → 로드 → `HideAsync` |
| 클라이언트 | NGO `SceneManager.OnLoad` 구독 | `ShowInstant` → `OnLoadComplete` 대기(30초 상한) → 첫 렌더 프레임 흘림 → `App.WaitUntilSceneReadyAsync` → `HideAsync` |

- 서버는 두 경로 모두에 걸리므로 `IsBusy` + `IsServer` 체크로 클라이언트용 자동 경로를 막는다.
- **NGO SceneManager 재훅:** 세션마다 새로 만들어지고 이 객체는 상주 → 생성 시 한 번 걸어둘 수 없다. `Update`에서 참조 비교로 **대상이 바뀐 프레임에만** 다시 건다(`RefreshNetworkHook`).
- **페이드 인은 없다.** 대신 `k_settleFrames`(60프레임) 흘려 "덮은 화면이 최소 한 번 렌더됐다"를 보장한다.
- 페이드 아웃 `m_fadeOutSeconds`(기본 0.35초). 스피너 회전·페이드 모두 **`Time.unscaledDeltaTime`** 기준 — 돌발 이벤트 freeze로 `timeScale`이 0이어도 돈다.
- `SetVisible`은 `canvas.enabled` + `CanvasGroup`(alpha/blocksRaycasts/interactable) 동시 제어 → 아래 씬 UI로 클릭이 새지 않는다.
- `SetStatus(string)` — 상태 문구 교체 API(전원 대기 표시 등 후속 확장용). 비워두면 프리팹 기본 문구 유지.

### 2-5. `SceneManagerBase.WaitUntilReadyAsync` 훅 ([SceneManagerBase.cs](../Assets/Scripts/Core/SceneManagerBase.cs))

씬 파일에 배치된 오브젝트는 활성화 프레임에 이미 준비되므로 **기본 구현은 즉시 완료**. 활성화 후에도 프레임에 걸쳐 채워지는 것이 있는 씬만 override. (override 시 자체 상한 필수 — 로딩 화면 고착 방지)

### 2-6. `InGameManager` override ([InGameManager.cs](../Assets/Scripts/Scene/InGameManager.cs))

NPC는 활성화 프레임 이후에 채워지므로 override 했고, **서버와 클라가 기다리는 대상이 다르다.**

- **서버·오프라인** — `NpcSpawner.IsSpawnCompleted` (스포너가 프레임당 한 마리씩 스폰). 스포너가 없는 씬 구성이면 대기 없음.
- **클라이언트** — `NpcSpawner.StartSpawn`이 클라에서 즉시 return하므로 `IsSpawnCompleted`가 영영 false → 그걸 기다리면 타임아웃까지 갇힌다. 그래서 **자기 플레이어 오브젝트 도착까지만** 기다리는 근사로 뒀다.
- 상한 20초, 초과 시 경고 후 로딩 화면 내림.

### 2-7. 프리팹

Canvas(ScreenSpaceOverlay, **sortingOrder 1000**) + CanvasScaler + CanvasGroup + `LoadingScreen` 컴포넌트, 하위에 `Background`(Image) / `Spinner`(Dot0~Dot2 Image) / `StatusText`(TMP). 직렬화 참조 4개(`m_canvas`/`m_canvasGroup`/`m_spinner`/`m_statusText`)와 `m_fadeOutSeconds = 0.35`가 채워져 있다.

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
| `LoadingScreen` | `k_settleFrames` | 60 | 덮은 화면의 렌더 보장 프레임 |
| `LoadingScreen` | `k_loadTimeoutSeconds` | 30초 | 클라 자동 경로 상한 |
| `LoadingScreen` | `k_spinnerDegreesPerSecond` | 180 | 스피너 회전 속도 |
| `InGameManager` | `k_readyTimeoutSeconds` | 20초 | 씬 준비 완료 대기 상한 |
| `RoundManager` | `m_peerWaitTimeoutSeconds` | 30초 | 전원 입장 확인 상한 |
| `RoundManager` | `m_startDelaySeconds` | 3초 | 시작 지연 |

**모든 대기는 상한 + 경고 후 진행**이다 — 어떤 신호가 안 와도 로딩 화면에 영구히 갇히지 않는다는 것이 공통 방침.

---

## 5. 의도적으로 뺀 것 / 알려진 한계

**뺀 것 (후속 판단):**
- **페이드 인** — `k_settleFrames`로 렌더 보장만 한다. 페이드 인을 넣으면 그 시간만큼 전환이 늘어난다.
- **진행바 / 셰이더 프리워밍**(`ShaderVariantCollection.WarmUp`) — `SetStatus`만 뚫어두고 실제 진행률 표시는 넣지 않았다.
- **로딩 화면은 NPC 스폰 완료까지만 덮는다** — 전원 입장 대기 + 3초 지연은 덮지 않는다(플레이어가 맵을 보며 기다리는 그림).

**한계:**
- ⚠️ **스폰 이동의 근거가 하나로 줄었다** — #395가 할당량 보정(#149)을 없앴으므로, 이제 "스폰을 시작 앞으로"를 지지하는 것은 로딩 화면뿐이다. 로딩 화면 요구가 바뀌면 이 배치를 재검토할 근거가 사라진다는 뜻이기도 하다.
- ⚠️ **클라이언트 준비 판정이 근사다** — 자기 플레이어 오브젝트 도착까지만 기다린다. NPC 전원 도착까지 정확히 맞추려면 **서버 권위 "준비 완료" 플래그를 복제**해야 한다.
- ⚠️ **전원 준비 판정이 서버 관점이다** — `OnLoadEventCompleted`는 "클라가 씬을 로드했다"까지만 알려줘, 클라 화면이 아직 로딩 중인데 라운드가 시작될 수 있다. → **#410으로 분리 등록**(권장 방향: `SceneReadyGate : NetworkedManagerBase` + 클라 → 서버 ServerRpc 보고). 로딩 화면의 `대기 중 (2/4)` 표시도 여기서 함께 해결된다.
- ⚠️ **준비 구간(3초) 동안 NPC는 이미 활동한다.** 스폰 직후 freeze는 넣지 않았다 — "3초 뒤 시작"은 타이머와 `OnRoundStarted`만 미루는 것.
- **카운트다운 UI 없음** — `RoundManager`가 NetworkBehaviour가 아니라 `Phase`가 클라에 가지 않는다(코드의 `TODO(#43)`). 다만 `RoundTimerSync`의 NetworkVariable 덕에 클라도 "타이머가 돌기 시작"으로 시작 시점은 인지한다.
- **입력 차단은 `CanvasGroup.blocksRaycasts`까지만** — Input System 액션 차단은 하지 않았다.
- [`RoundEndResetter`](../Assets/Scripts/Round/RoundEndResetter.cs)의 NGO Shutdown 대기 구간은 덮이지 않는다 — `App.LoadScene`이 shutdown 완료 *후*에 불리기 때문. 덮으려면 호출부 수정이 필요해 이슈 완료기준("호출부 수정 0")과 충돌.
- `k_settleFrames = 60`은 60fps 기준 약 1초 — 렌더 보장에 필요한 최소치보다 넉넉하다. 전환 체감 속도가 문제되면 줄일 여지가 있다.
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
