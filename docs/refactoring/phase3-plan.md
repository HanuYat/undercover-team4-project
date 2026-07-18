# 3단계 실행 계획 — AppBootstrap 프리팹 + Title 씬 (다음 세션용)

> 작성: 2026-07-19. 이 문서 하나로 새 세션(다른 PC 포함)에서 바로 시작할 수 있게 쓴다.
> 선행 문서: [phase1-2-summary.md](phase1-2-summary.md)(경위), [architecture.md](../architecture.md)(규칙 정본).

## 0. 시작 조건

- **PR #245가 main에 머지된 후**, main에서 새 브랜치(예: `refactoring/phase3-bootstrap`)로 시작한다.
  - 머지 전에 시작해야 하면 `refactoring/architecture`에서 분기하되, #245가 마지막에 머지되는 전략이므로 이 브랜치도 그 뒤로 밀린다.
- **씬 작업이 본체이므로 시작 전에 팀 공지**: "이번 주 X요일까지 공용 씬(Main Scene) 수정 금지" 같은 머지 윈도우를 잡을 것 (PR #86 씬 덮어쓰기 사고 재발 방지).
- Unity 6000.3.15f1, Unity MCP 연결되면 컴파일/씬 조작 자동화 가능 (안 되면 에디터 수동 + Editor.log 확인으로 진행 — 1~2단계가 그렇게 했음).

## 1. 목표

1. 씬마다 복사 배치된 상주 매니저(Session·Auth·Vivox)를 **AppBootstrap 프리팹 하나**(DontDestroyOnLoad)로 통합
2. **Title(로비) 씬 신설** — 세션 생성/참가 UI, TitleManager/TitleUIManager
3. 빌드 세팅 정리 — Title(0), Main Scene(1), 테스트 씬은 빌드에서 제외
4. 세션 성립 → 서버가 `App.LoadScene(EScene.InGame)` → NGO 씬 동기화로 전원 진입

## 2. 현재 배치 현황 (2026-07-19 조사 — 제거 대상 지도)

| 매니저 | 배치된 씬 |
|---|---|
| SessionManager·AuthBootstrap | HQTest, Item_Test, Main Scene |
| VivoxManager | HQTest, Main Scene |
| RoundManager·ArrestJudge·CriminalAssigner·NpcSpawner | 6개 씬 전부 (HQTest, Item_Test, Main, NPCMove, ScanUI_Test, TotalTest) |
| AppearanceAssigner | HQTest, Item_Test, Main, TotalTest |
| WantedListManager | HQTest, Main Scene |
| SuddenEventManager | NPCMove |

Main Scene의 매니저들은 `=== GameManagers ===` 루트 오브젝트 아래에 있다.

## 3. 작업 순서 (체크리스트)

### 3-1. 코드 (씬 작업 전에 먼저 — 컴파일 가능 상태 유지)
- [ ] `Core/AppBootstrap.cs` 신규: `[DefaultExecutionOrder((int)EExecutionOrder.Bootstrap)]`, Awake에서 ①중복 가드(이미 존재하면 `Destroy(gameObject)` 후 return — static bool 또는 App 측 플래그로 판정) ②`DontDestroyOnLoad(gameObject)` ③`Application.targetFrameRate` 설정(템플릿 AppHelper 참고)
- [ ] `TitleManager : SceneManagerBase`, `InGameManager : SceneManagerBase` 신규 (얇게 — RoundManager는 그대로 두고 InGameManager는 씬 진입점 역할만)
- [ ] `TitleUIManager : UIManagerBase`, `InGameUIManager : UIManagerBase` 신규
- [ ] App.cs: `App.UI.Title`/`App.UI.InGame` 캐스트 프로퍼티 추가 (1단계 때 예고한 자리), SceneFlow에 Title/InGame 편의 프로퍼티
- [ ] 로비 UI: 세션 생성/참가(코드 입력) 패널 — PanelBase 기반이면 4단계 선행 겸함. SessionManager의 CreateSessionAsync/JoinSessionByCodeAsync 사용
- [ ] RoundEndResetter의 `SceneManager.LoadScene(active.buildIndex)` → `App.LoadScene(App.CurrentScene)` 전환 + architecture.md §4 예외 표에서 해당 줄 제거
- [ ] (겸사) RoundEndResetter UniTask 취소 토큰 버그 수정 — `this.GetCancellationTokenOnDestroy()`를 Delay에 전달

### 3-2. 프리팹/씬 (Unity 에디터)
- [ ] `Prefabs/AppBootstrap.prefab` 생성: AppBootstrap + SessionManager + AuthBootstrap + VivoxManager (SessionManager의 m_auth 인스펙터 연결 유지)
- [ ] **주의**: 기존 씬별 SessionManager 인스펙터 값(m_maxPlayer 등)이 씬마다 다를 수 있음 — Main Scene 값을 기준으로 프리팹에 옮기고, 다른 씬 값과 차이나면 팀 확인
- [ ] Title 씬 신설 (`Assets/Scenes/Title.unity` — **이름이 EScene 매핑("Title")과 일치해야 함**, AppHelper.ToSceneName 참고): AppBootstrap 프리팹 + TitleManager + TitleUIManager + 로비 UI + NetworkManager(NGO) 배치
- [ ] **NetworkManager의 "Enable Scene Management" 체크 확인** — 꺼져 있으면 NGO 씬 동기화 불가 (AppHelper의 NGO 분기 전제)
- [ ] Main Scene: 상주 매니저 3종 오브젝트 삭제 → AppBootstrap 프리팹 배치(단독 Play 지원), InGameManager/InGameUIManager 추가
- [ ] 각 테스트 씬: 상주 매니저 사본 삭제 → AppBootstrap 프리팹으로 교체 (씬을 아예 안 건드리고 두는 선택지도 있음 — 중복 가드가 있어서 동작은 함. 팀과 상의)
- [ ] Build Settings: Title(0), Main Scene(1), 테스트 씬 제거

### 3-3. 검증 (필수 순서)
- [ ] Title 씬 Play → 세션 생성 → InGame 진입 (호스트 단독)
- [ ] **Multiplayer Play Mode 2인**: 호스트 세션 생성 + 클라 코드 참가 → 둘 다 InGame 진입 → 라운드 정상 → 라운드 종료 → 로비 복귀(RoundEndResetter 경로) → 재입장
- [ ] Main Scene 단독 Play (테스트 워크플로 확인 — AppBootstrap 중복 가드 동작 포함: Title에서 넘어온 경우와 단독 실행 경우 모두)
- [ ] 각 테스트 씬 단독 Play 1회씩
- [ ] Editor.log에서 `[ManagerHandler]` 에러 0건 확인

## 4. 알려진 함정 (1~2단계에서 실제로 겪은 것)

1. **자체 Awake/OnDestroy가 베이스를 가림** — 새 클래스 작성 시 반드시 `protected override` + `base` 호출. 누락하면 csc.rsp가 컴파일 에러로 잡아줌 (NpcSpawner 사고의 교훈)
2. **매니저 간 구독은 Start에서** — 같은 실행 순서끼리 Awake 순서 비보장 (R6)
3. **DontDestroyOnLoad 객체의 App 등록**: 상주 매니저는 씬 전환에도 살아있으므로 OnDestroy 해제가 안 일어남 → 정상. 단, Title로 "복귀"했을 때 씬에 있는 AppBootstrap 프리팹과 상주 인스턴스가 중복 → 중복 가드가 새 것을 파괴해야 함 (파괴되는 쪽의 OnDestroy가 상주 인스턴스의 App 등록을 지우지 않는지 확인 — ManagerHandler.Unregister는 "자기가 등록된 경우만" 지우므로 안전하지만, 파괴 순서상 새 오브젝트가 등록을 덮은 뒤 파괴되면 필드가 비는 경계 사례 주의. **가드는 매니저 Awake(-300)보다 먼저 돌도록 Bootstrap(-400) 순서 + 자식 매니저 포함 즉시 파괴 + `gameObject.SetActive(false)` 선행**으로 처리)
4. **씬 파일 검증은 로그로**: 플레이 후 Editor.log(`C:\Users\<user>\AppData\Local\Unity\Editor\Editor.log`)에서 등록/경고 확인하는 방법이 1~2단계에서 유효했음
5. **Enter Play Mode Options**가 켜져 있는 프로젝트 — static 상태는 `[RuntimeInitializeOnLoadMethod]` 리셋이 이미 App에 있음(1단계). AppBootstrap의 중복 가드 static도 같은 방식으로 리셋할 것

## 5. PR 분리 권장

- PR A: 3-1 코드만 (리뷰 가능) → PR B: 프리팹+씬+빌드 세팅 (본문에 씬 변경 명세 상세히 — 템플릿 씬/프리팹 칸이 핵심). 한 PR로 합치면 씬 YAML에 코드 리뷰가 묻힌다.
