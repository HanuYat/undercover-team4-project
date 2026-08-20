# #622 — 빌드에 커밋 sha를 자동으로 굽는다 (버전 섞임 진단용)

- **날짜:** 2026-08-20
- **브랜치:** `feature/622-build-stamp-sha`
- **관련:** #586 (세션 프로퍼티 버전 검사), #628 (연결 승인 버전 검사)

---

## 1. 한 줄 요약

#586/#628의 버전 관문은 **손으로 올려야 하는 값**(`Application.version` + `NetworkProtocol.k_protocolVersion`)만 비교한다. 프로토콜 번호를 올리는 걸 깜빡하면 관문은 에러가 아니라 **조용한 통과**로 실패한다 — 소스는 여러 커밋 차이인데 양쪽 `VersionString`이 똑같아 아무것도 안 막힌다. 그래서 **차단에는 쓰지 않는** 커밋 sha를 자동으로 굽고, 세션 참가 시 로그에 남겨 그 상황을 몇 초 안에 알아채게 한다.

---

## 2. 역할 분리 (이 문서의 핵심)

| | 차단 | 진단 |
|---|---|---|
| 값 | `NetworkProtocol.VersionString` (수동 프로토콜 번호) | `BuildStamp.Sha` (자동 커밋 sha) |
| 올리는 방법 | 사람이 호환성 깨질 때 `k_protocolVersion` +1 | 빌드 훅이 자동 |
| 하는 일 | 참가를 막는다 | 로그에만 남는다 |

### sha로 차단하지 않는 이유

커밋 sha는 **네트워크 호환성과 무관한 변경**(오타·문서·UI 색상)에도 매번 바뀐다. sha가 다르면 막는 방식은 팀원이 같은 시각에 pull하지 않으면 거의 항상 막힌다는 뜻이다. 게이트가 노이즈가 되면 사람들이 우회하기 시작하고, 그러면 없는 것보다 나쁘다.

반대로 "직렬화가 깨지는 변경인가"는 커밋 diff로 기계 판단이 불가능하다(한 줄만 바꿔도 깨질 수 있고, 수백 줄이 늘어도 안 깨질 수 있다). 그래서 차단 기준은 **사람의 판단**(`k_protocolVersion`)으로 남기고, 사람이 놓쳤을 때 **알아채는 속도**만 자동화한다.

### 불변 조건 (깨지면 이 이슈가 무의미해짐)

1. 버전 비교는 `PeerStamp.Version`만 본다 — sha는 로그 전용
2. sha를 못 읽어도 빌드가 실패하지 않는다 (`?` fallback)
3. sha가 달라도 참가가 막히지 않는다

3번을 타입으로 강제하려고 [`NetworkProtocol.DecodePayload`](../Assets/Scripts/Network/Config/NetworkProtocol.cs)의 반환 타입을 `string` → `PeerStamp` 구조체로 바꿨다. 통짜 문자열을 반환하면 언젠가 누가 `decoded != VersionString`으로 비교하다 sha가 차단에 끼어든다.

---

## 3. sha를 얻는 두 경로

| 실행 환경 | 경로 |
|---|---|
| 빌드(플레이어) | [`BuildStampBaker`](../Assets/Scripts/Editor/BuildStampBaker.cs)가 빌드 직전에 구운 `Resources/BuildStamp.txt`를 `Resources.Load`로 읽는다 |
| 에디터 | 빌드 훅이 안 도니 구운 값이 없다 → `git rev-parse --short HEAD`를 직접 읽는다 |

- git 판독은 [`BuildStamp.ReadFromGit`](../Assets/Scripts/Network/Config/BuildStamp.cs) 한 곳에 있고 빌드 훅도 이걸 재사용한다. 그래서 이 함수가 `Assets/Scripts/Editor/`가 아니라 **런타임 어셈블리의 `#if UNITY_EDITOR` 안**에 있다 — 반대로 두면 런타임 코드가 참조할 수 없다.
- `git status --porcelain`이 비어 있지 않으면 `-dirty` 접미.
- 프로젝트에 `Process.Start` 선례가 없었다. 그래서 에디터 전용으로 못 박고 정적 캐시로 도메인 리로드당 1회만 돌린다. `Assets/Imported`는 별도 리포지만 `.gitignore`되어 있어 `status`가 그쪽으로 새지 않는다.

### `.cs`를 생성하지 않는 이유

빌드 전처리에서 스크립트를 쓰면 빌드 중 재컴파일이 끼어들어 불안정하다. 그래서 `TextAsset`으로 굽는다.

### 생성 파일을 추적하지 않는 결정

`.gitignore`에 넣었다. 빌드마다 내용이 바뀌어 diff 노이즈가 되고, 다른 작업의 diff·머지에 섞인다. 빌드 후처리에서 곧바로 지우므로 평소엔 존재하지도 않는다 — `.gitignore` 항목은 **빌드가 중간에 죽어 파일이 남는 경우의 안전망**이다.

지우는 것 자체도 의미가 있다: 남겨 두면 에디터가 살아 있는 git 대신 지난 빌드의 낡은 sha를 보게 된다.

폴더는 최상위 `Assets/Resources/`를 새로 점유하지 않고 읽는 코드 옆(`Assets/Scripts/Network/Config/Resources/`)에 둔다. `Resources.Load`는 `Assets` 아래 어디든 `Resources` 폴더를 찾고, 프로젝트는 지금까지 `Resources`를 전혀 쓰지 않았으므로 관례를 최소로 건드린다.

---

## 4. 어떻게 실려 오나

`SessionProperty`는 **호스트만 쓸 수 있어** 참가자 각자의 sha를 그 통로로는 올릴 수 없다. 그래서 방향을 나눴다.

| 방향 | 통로 | 결과 |
|---|---|---|
| 호스트 → 참가자 | 세션 프로퍼티 `sha` (`ver` 옆, Public) | 클라 로그에 `내 sha / 방 sha` |
| 참가자 → 호스트 | #628 승인 페이로드에 필드 추가 | **호스트 로그에 참가자 전원의 sha** |

②는 `PlayerProperty`도 신규 RPC도 필요 없었다 — #628이 깐 `NetworkConfig.ConnectionData`가 이미 클라→호스트로 흐르고 있어서 필드 하나만 덧붙였다. 페이로드 포맷이 `"{VersionString}\n{sha}"`가 됐다.

덕분에 **버그 재현한 사람 로그 하나**로 판단이 끝난다. 호스트 sha만 실었다면 "3번 플레이어가 뒤처졌나"를 알려고 그 사람 로그를 받아와야 했다.

### `k_protocolVersion` 1 → 2

페이로드 포맷이 바뀌는 것은 실제 호환성 파괴다 — 구버전 호스트는 새 페이로드(`버전\nsha`)를 통짜로 비교해 거부한다. 이건 헛차단이 아니라 정당한 차단이므로 번호를 올려 정직하게 만들었다.

---

## 5. 한계

- **`-dirty`는 서로 다른 dirty 트리를 구분하지 못한다.** 둘 다 `-dirty`라 같은 값으로 보인다. "이 빌드는 커밋 상태가 아님" 정도의 의미만 갖는다.
- 본부 화면에 전원 sha를 띄우려면 `SessionRoster`의 `NetworkList`에 실어야 한다 — 이 이슈 범위 밖(완료 기준은 로그만 요구).
- 테스트 코드는 없다. `NetworkProtocol`이 `Assembly-CSharp`에 있어 asmdef가 없고, 현재 테스트가 참조할 수 있는 어셈블리는 `Undercover.Emote` 뿐이다.

---

## 6. 변경 파일

| 파일 | 내용 |
|---|---|
| `Assets/Scripts/Network/Config/BuildStamp.cs` | **신규** — sha 판독(구운 값 / 에디터 git fallback) |
| `Assets/Scripts/Editor/BuildStampBaker.cs` | **신규** — 빌드 전처리 굽기 + 후처리 삭제 |
| `Assets/Scripts/Network/Config/NetworkProtocol.cs` | 페이로드 2필드화, `PeerStamp` 추가, 프로토콜 2 |
| `Assets/Scripts/Network/ConnectionApprovalGate.cs` | `StampLocalPayload` 개명, 승인 로그에 클라 sha |
| `Assets/Scripts/Network/SessionManager.cs` | `sha` 세션 프로퍼티, `ReadSha`, 참가 시 sha 로그 |
| `Assets/Scripts/Test/DevAutoHost.cs` | 개명 반영 (2곳) |
| `.gitignore` | 생성 자산 제외 |
