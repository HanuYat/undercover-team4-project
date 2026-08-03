# 아키텍처 규칙 (초안 — 팀 검토 필요)

> 이 문서가 코드 구조 규칙의 **정본**이다. PR 자동 리뷰(`.claude/skills/pr-review`)와 CLAUDE.md가 이 문서를 기준으로 검사한다.
> 적용 시점: `refactoring/architecture` 브랜치 머지 이후의 신규·수정 코드부터 (유예 조항 참고).
> 게임 규칙의 정본은 [GDD.md](GDD.md) — 이 문서는 코드 구조만 다룬다.

## 1. 구조 개요

```
사용하는 쪽 컴포넌트  ──(App.그룹.매니저 로만 접근)──▶  App (전역 파사드)
                                                        ▲
매니저  ──(베이스 상속만 하면 Awake에서 자동 등록)──────┘
```

- **App** ([Assets/Scripts/Core/App.cs](../Assets/Scripts/Core/App.cs)) — 전역 매니저 접근의 단일 경로.
  - `App.Net` — SessionManager · AuthBootstrap · VivoxManager
  - `App.Game` — RoundManager · SuddenEventManager · WantedListManager · DirectoryManager · ArrestJudge · CriminalAssigner · NpcSpawner · AppearanceAssigner · WrongfulArrestPenalty · TeamFund
  - `App.UI` — UIManagerBase(3단계 예정) · CrosshairUI · ChannelingGaugeUI
  - `App.SceneFlow` — 현재 씬의 SceneManagerBase (3단계 예정)
- **등록 메커니즘** — `CommonManagerBase`(일반) / `NetworkedManagerBase`(NetworkBehaviour)를 상속하면 Awake에서 `ManagerHandler`가 리플렉션으로 App의 같은 타입 필드에 주입하고, 파괴 시 해제한다. **App 필드에 직접 대입하는 코드를 만들지 말 것.**
- **도메인** = `Assets/Scripts/` 하위의 **게임플레이 폴더**. 현재: Round, NPC, Player, HQ, Item, Interaction, Events, Network, Economy (게임플레이 폴더가 새로 생기면 자동 포함). 판별이 애매하면 "이 파일이 바뀌는 이유가 뭐냐"로 판단한다.
  - **도메인으로 세지 않는 폴더**: `Core`(App 인프라 자체), `UI`(각 도메인의 화면 표현), `Data`(전 도메인 공유 어휘), `Scene`(씬 진입점), `Localization`(공유 자원), `Editor`·`Test`(런타임 아님).
  - **참조를 셀 때**: (a) **실제 코드 의존성만** 센다 — 주석·독스트링의 언급(`<see cref=...>` 포함)은 제외. (b) 같은 도메인 내부의 다른 파일 참조는 세지 않는다. (c) 같은 오브젝트에 강제된 컴포넌트(`[RequireComponent]`) 배선은 같은 도메인으로 본다 (별도 도메인이 아님).

## 2. 규칙 (R1–R8)

리뷰에서 기계적으로 판정할 수 있게 쓴다. 각 규칙의 위반 판정 기준을 함께 명시한다.

| # | 규칙 | 위반 판정 |
|---|---|---|
| **R1** | 매니저 접근은 App 경유만 | App 등록 타입을 `FindFirstObjectByType`으로 찾는 코드 (§4 예외 제외) |
| **R2** | 신규 싱글톤 금지 | `static <자기타입> Instance` 프로퍼티/필드 신설 |
| **R3** | App 등록 기준: ① 씬에 개념적으로 하나뿐인 서비스 AND ② 두 개 이상의 도메인이 참조 | 기준 미달인데 App에 필드 추가 (참조자 1곳·같은 도메인이면 SerializeField 연결이 맞다) |
| **R4** | 새 매니저 = 베이스 상속 + 실행 순서 명시 + App 필드/프로퍼티 추가 | 셋 중 하나라도 누락 — `[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]`는 상속돼도 다시 명시 |
| **R5** | 매니저의 `Awake`/`OnDestroy`는 `protected override` + `base` 호출 | base 호출 누락 (선언 자체를 잘못하면 CS0114가 컴파일 에러 — `Assets/csc.rsp`) |
| **R6** | 매니저 간 이벤트 구독은 `Start`에서 (모든 Awake=등록 완료 보장) | 다른 매니저 구독을 Awake/OnEnable에서 수행. 매니저 오브젝트의 씬 도중 비활성화도 금지 |
| **R7** | 씬 전환은 `App.LoadScene`만 (NGO 세션 동기화 자동 분기) | `UnityEngine.SceneManagement.SceneManager.LoadScene` 직접 호출 (AppHelper 내부·§4 예외 제외) |
| **R8** | 매니저 참조는 읽기 프로퍼티: `private X Xxx => App.그룹.X;` | 매니저를 필드에 캐싱 (파괴된 참조를 쥐는 원인). null 가드는 기존 관례대로 사용처에서 |

R9(예약): UI 패널은 `PanelBase` 상속 + `OpenPanel<T>()` 경유 — 4단계(UI 패널 시스템) 시행 시 활성화.

## 3. 승격/강등 절차

- **승격**: R3의 ①을 이미 만족하는 클래스에 두 번째 도메인의 참조가 생기는 순간 App에 올린다. 비용은 App 필드+프로퍼티 2줄 + 베이스 상속.
- **강등**: App에서 필드/프로퍼티를 지우면 사용처 전부가 컴파일 에러로 드러난다 — 컴파일러가 수정 목록을 준다.
- **올리지 않는 대안 (부품 조회)**: 승격 대상이 *이미 App에 있는 매니저가 관리하는 부품*이라면, 부품을 따로 등록하지 말고 그 매니저에게 제네릭으로 물어본다 — `App.Game.SuddenEvent?.GetEvent<DeviceBlackoutEvent>()`. 타입은 부르는 쪽에만 남으므로 매니저는 여전히 부품 종류를 모르고, App 필드도 늘지 않는다. 런타임 스폰 프리팹처럼 인스펙터 배선이 불가능한 사용처(스캐너)도 이 경로로 해결된다 (#372 리뷰).
- 판단이 애매하면 PR에서 R3 기준으로 논의한다.

## 4. 의도적 예외 (위반 아님)

| 코드 | 사유 |
|---|---|
| JailIntake → JailScanner `Find` | 장소(게이트) 오브젝트 — 매니저 아님. 유치장 밖(문 앞)에 서 있어 부모 탐색으로는 닿지 않는다. 참조 도메인은 Interaction 하나뿐이라 R3 승격 기준에 미달하고, Awake 1회 탐색이라 런타임 비용도 없다 (#492) |
| PlayerEscortCommands → JailIntake `Find` | 장소(출입구) 오브젝트 — 매니저 아님. 참조 도메인이 Player 하나라 R3 승격 기준에 미달이고, E 입력 때만 도는 경로라 매 프레임 탐색 비용도 없다. 인계 단말이 폐기되면서 삭제된 `ArrestJudge → HqDropoffZone Find` 항목을 대체한다 (#492) |
| RoundTimerUI → RoundTimerSync `Find` | Round 도메인 내부 부품 (HQ 타이머 표시가 생기면 승격 후보) |
| SessionManager → AuthBootstrap `SerializeField` | 같은 오브젝트/프리팹 내 직접 연결 |
| RoundEndResetter의 테스트 씬 폴백 `SceneManager.LoadScene` | EScene 매핑이 없는 테스트 씬 한정 — 정식 흐름은 App.LoadScene(Title)로 전환 완료 (#247) |
| JailbreakEvent · CustodyRouter · RoundManager · SettlementController · RoundFundHud → JailZone `Find`/`SerializeField` | 장소(구역) 오브젝트 — 매니저 아님. JailIntake·JailLock과 같은 분류다. 참조 도메인이 늘어(#395로 Round·UI 추가) R3의 승격 기준은 넘지만, 본부 맵에 배치되는 설치물이라 App 상주 매니저로 올리면 씬 없는 구성(로비·타이틀)에서 빈 슬롯이 된다. 인스펙터 연결을 우선하고 비었을 때만 씬 탐색으로 폴백한다 |
| SceneReadyGate → `App.Game.ReadyGate` 등재 | 세어지는 참조 도메인은 Round(RoundManager) 하나라 R3 ②에 미달하지만, 실사용은 로딩 흐름(InGameManager)·표시(ReadyWaitHud)·라운드 시작(RoundManager)을 가로지르는 서버 권위 코디네이션 지점이다. 특히 ReadyWaitHud는 HUD.prefab이 오너 스폰 시 **런타임 생성**돼 씬 오브젝트를 인스펙터로 배선할 수 없어 App 경로 외 대안이 없다 (#410) |

예외를 추가하려면 이 표에 사유와 함께 기재한다 (기재 없는 예외는 위반).

## 5. 유예 조항

`refactoring/architecture` 머지 **이전에** 열린 브랜치의 코드는 규칙 위반을 지적하되 🟡(후속 조치)로 분류한다. 머지 이후 새로 작성·수정되는 코드는 정식 적용(🟠 이상).

---
*최종 수정: 2026-08-03 (HqDropoffZone 예외 삭제 · JailIntake·JailScanner 예외 기재 — #492) · 2026-08-01 (SceneReadyGate 예외 기재 — #410) · 2026-07-28 (JailZone 예외 기재 — #395) · 작성 근거: refactoring/architecture 브랜치 1–2단계 (커밋 3039cd2…0b7aaab)*
