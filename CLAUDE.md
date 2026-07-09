# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 프로젝트 개요

**Project Undercover** (가제: 별점 1점 경찰서 / One-Star Precinct) — 3~6인 **비대칭 협동 추리 게임**. 플레이어는 사이버펑크 도시의 로봇 경찰이 되어 **본부(관제)** 와 **현장(수사)** 으로 나뉘어 무전으로 소통하며 용의자를 검거한다.

- **게임 디자인 정본:** [docs/GDD.md](docs/GDD.md). 게임 시스템/규칙 관련 작업 전 반드시 참고.
  - 초안 단계 항목은 헤더에 `(초안 — 팀 검토 필요)` 표기되어 있으니 확정 사항과 구분할 것.
  - 여러 기획 자료 간 모순의 최종 확정은 GDD **부록 B**에 근거표로 정리되어 있음.
- 개발은 이제 막 시작 단계. `Assets/`에는 아직 게임플레이 코드가 없고 Synty 아트 에셋과 `SampleScene`만 있음. 새 게임 코드는 여기서부터 작성됨.

## 엔진 & 실행 환경

- **Unity `6000.3.15f1`** (정확히 이 버전으로 열 것 — Unity Hub).
- 개발·빌드·테스트는 **Unity Editor**에서 수행한다. 전통적인 CLI 빌드 스크립트는 없다.
- 저장소에 **MCP for Unity**(`com.coplaydev.unity-mcp`) 패키지가 포함되어 있어, 원하는 팀원은 Claude Code를 Editor에 연결해 스크립트·씬·테스트·빌드를 도구로 자동화할 수 있다. **필수는 아니다** — MCP를 쓰지 않으면 Editor에서 동일 작업을 수동으로 하면 된다. (아래 괄호 안 `MCP: ...`는 연결한 경우의 선택 경로)

### 코드 수정 후 (공통, MCP 여부 무관)
- 스크립트를 수정하면 Unity가 컴파일한다. **새 타입/컴포넌트를 사용하기 전에 Unity Console에서 컴파일 에러가 없는지 확인**할 것. (MCP: `read_console`, `editor_state.isCompiling`로 폴링)

### 테스트 / 빌드 / 실행
- **테스트:** Unity Test Framework — Editor의 Test Runner. (MCP: `run_tests`) 아직 테스트 코드 없음.
- **빌드:** Editor의 Build Profiles / Build Settings. (MCP: `manage_build`)
- **플레이/디버그:** Editor Play 모드. **멀티플레이 동시 테스트는 Multiplayer Play Mode**(`com.unity.multiplayer.playmode`)로 여러 가상 플레이어를 띄운다. (MCP: `manage_editor`)

## 아키텍처 (패키지가 정의하는 큰 그림)

이 프로젝트의 구조는 대부분 설치된 패키지 스택으로 결정된다:

- **네트워킹 (핵심):** **Netcode for GameObjects**(`netcode.gameobjects`) + **Unity Gaming Services 멀티플레이**(`services.multiplayer`, Relay/Lobby). 3~6인 온라인 협동. 네트워크로 동기화되는 프리팹은 `Assets/DefaultNetworkPrefabs.asset`에 등록된다. 게임플레이는 이 세션/네트워크 위에서 동작하므로 **소유권(ownership)·서버 권한 여부**를 먼저 고려하고 설계할 것.
- **렌더링:** URP. 파이프라인 에셋은 `Assets/Settings/`에 PC(`PC_RPAsset`/`PC_Renderer`)와 Mobile 두 세트가 있음. 타깃은 PC(GDD 기준).
- **입력:** Input System. 액션 정의는 `Assets/InputSystem_Actions.inputactions` (WASD 이동 / 마우스 회전 등).
- **NPC AI:** AI Navigation(NavMesh). 시민/용의자는 **FSM + NavMesh** 기반 (GDD 6장, 상태 enum은 GDD 10-2).
- **카메라:** Cinemachine. 플레이어 카메라 및 본부 **CCTV** 기능에 사용.
- **비동기:** UniTask (`async`/`await` 대신 `UniTask` 우선 사용 가능).
- **데이터:** 프로필·대조용 데이터·스탯 등은 **ScriptableObject**로 구현 (GDD 5-3, 10-4).
- **아트:** Synty Polygon 에셋 — `Assets/Imported/Synty/`. 이 아래 코드/셰이더는 서드파티이므로 임의 수정 금지.

## 코드 컨벤션 (팀 확정)

C# 네이밍 규칙 (GDD 10-5 · 2026-07-09 회의 확정). 신규 코드는 반드시 이 규칙을 따른다:

| 규칙 | 대상 | 예시 |
|------|------|------|
| `m_` | private/protected 인스턴스 멤버 | `m_health` |
| `s_` | static 멤버 | `s_instance` |
| `k_` | const 상수 | `k_maxHp` |
| `I` 접두사 | 인터페이스 | `IDamageable` |
| `T` 접두사 | 제네릭 타입 파라미터 | `TComponent` |
| camelCase (접두사 없음) | 매개변수/지역변수 | `damageAmount` |
| PascalCase (접두사 없음) | public 프로퍼티/메서드, enum 값 | `Health`, `TakeDamage()` |
| `On` 접두사 | 이벤트 | `OnPlayerDied` |

## 협업 / 저장소

- 기본 브랜치: `main`. 이슈 템플릿은 `.github/ISSUE_TEMPLATE/`(버그/기능).
- **마일스톤:** M1 핵심 메카닉(3주) → M2 콘텐츠 확장(2주) → M3 데모 완성(2주). GDD 11장 참고. 주간 빌드는 별도 이터레이션으로 관리.
