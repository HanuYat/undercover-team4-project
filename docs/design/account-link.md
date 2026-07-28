# 계정 연동 (#384)

> 설계 정본. 닉네임/계정을 **기기 간에 유지**하기 위한 계정 연동 방식과 익명→정식 **승격 경로**를 정의한다.
> 코드 구조 규칙은 [architecture.md](../architecture.md), 게임 규칙은 [GDD.md](../GDD.md)가 정본 — 이 문서는 계정 인증만 다룬다.
> 관련: #249 (닉네임 영속 저장 — A+D 하이브리드, 이 이슈의 선행), #218/#234 (이름표 표시), [settings-ui.md](settings-ui.md) 결정 (c) (닉네임 UI를 설정 창에 두지 않는 이유).

## 1. 목표 & 범위

#249로 닉네임은 **같은 기기 안에서만** 복원된다. 익명 계정의 `PlayerId`가 로컬 세션 토큰에 묶여 있어, 토큰이 없는 새 기기에서는 다른 `PlayerId`가 발급되고 그 계정에는 이전 닉네임이 없다. UGS PlayerName도 Cloud Save도 **동일하게 `PlayerId`를 키로** 쓰므로 저장소를 바꿔서는 해결되지 않는다 — **계정 식별자 자체를 영속화**해야 한다.

### 범위 안
- 인증 방식 결정 및 근거 기록 (§2, GDD 부록 B)
- 익명 계정 → 정식 계정 **승격** (`PlayerId`·닉네임 유지)
- 다른 기기에서 같은 계정으로 로그인 → 같은 닉네임
- 아이디 충돌·상태 위반 시의 동작 정의
- #249 PlayerPrefs 캐시와 서버 값의 **우선순위 재정의** (§4 — 방치하면 데이터 유실)
- `AuthPanel`에 계정 UI

### 범위 밖
- 플랫폼 링크(Steam/Google/Apple) — 배포 플랫폼 확정 후 **같은 계정에 identity 추가**로 덧붙일 수 있다 (§7)
- 비밀번호 재설정/복구 — UGS가 **Admin API 전용**으로 두어 클라이언트에서 만들 수 없다 (§5)
- 닉네임 중복 방지·검열 (#249에서도 범위 밖)
- 계정별 진행 상황 저장 — 라운드 단독 구조라 저장할 진행도가 없다 (GDD 부록 B #4)

## 2. 확정된 설계 결정

| 항목 | 결정 | 근거 |
|------|------|------|
| (a) 인증 방식 | **UGS Username & Password** (2026-07-28 결정) | 브라우저 왕복이 없어 **게임 내 UI로 완결**된다. 후보 비교는 아래 표 |
| (b) 익명 유지 허용 | **허용.** 계정 없이도 전부 플레이 가능, 연동은 완전 선택 | 연동을 강제하면 데모 진입에 계정 생성 단계가 붙는다. 얻는 건 닉네임 이식뿐 |
| (c) 승격 API | `AddUsernamePasswordAsync` — **익명 로그인 상태를 유지한 채** 호출 | 신규 `SignUp`으로 처리하면 새 `PlayerId`가 발급돼 기존 닉네임이 유실된다. `Add...`만이 현재 계정에 자격증명을 붙인다 |
| (d) 새 기기 로그인 | `SignOut()` **선행 후** `SignInWithUsernamePasswordAsync` | `AuthBootstrap`이 씬 시작 시 이미 익명 로그인을 해둔다([AuthBootstrap.cs:99](../../Assets/Scripts/Network/AuthBootstrap.cs#L99)). 로그인 상태에서 호출하면 `ClientInvalidUserState` |
| (e) 연동 해제 | **UI 없음.** 대신 연동 버튼에 되돌릴 수 없음을 경고 | UGS가 username/password의 **제거를 지원하지 않는다.** 만들 수 없는 기능이므로 사용자에게 미리 알리는 것으로 대체 |
| (f) 입력 검증 위치 | UGS 규칙(§3)을 **클라이언트에서 먼저** 검사 | #249에서 `ValidateNickname`을 우리가 만든 것과 같은 이유 — UGS 응답이 불친절해 그대로 노출하면 사용자가 무엇을 고쳐야 할지 모른다 |
| (g) 닉네임 우선순위 | **계정 종류에 따라 방향이 뒤집힌다** (§4) | 연동 후에도 캐시를 서버로 밀면 새 기기의 낡은 익명 캐시가 진짜 계정 닉네임을 덮어쓴다 |
| (h) UI 위치 | **`AuthPanel`(Title 씬)만.** 설정 창(#225)에 넣지 않는다 | 설정 창은 4씬 전체에 배치돼 있어 넣으면 **게임 중 계정 변경 진입점**이 생긴다. Vivox 로그인이 `PlayerId`에 묶여 세션 중 계정 변경은 곧 버그. [settings-ui.md](settings-ui.md) 결정 (c)와 같은 판단 |
| (i) 경계 유지 | `AuthBootstrap` 내부만 교체. `PlayerNameTag`·`SessionFlow`는 수정 없음 | #249가 세운 경계를 그대로 잇는다. `PlayerNameTag`는 `App.Net.Auth.Nickname` 1줄만 읽고([PlayerNameTag.cs:63](../../Assets/Scripts/Player/PlayerNameTag.cs#L63)), `SessionFlow`의 `SignOut()`은 토큰을 유지하므로 연동 계정도 같은 기기에서 그대로 복원된다 |

### 후보 비교 (근거)

| | 브라우저 | 플레이어 부담 | 계정 복구 | 선행 조건 | 판정 |
|---|---|---|---|---|---|
| (C-1) Unity Player Accounts | **필요** | Unity 계정 생성 | 있음 | 대시보드 + 커스텀 URI 스킴, **Editor Play 콜백 복귀 불확실** | 탈락 — 데모 진입 장벽 + 검증 리스크 |
| (C-2) 플랫폼 링크 (Steam 등) | 불필요 | 없음 (플랫폼 로그인 재사용) | 있음 | **배포 플랫폼 확정 + Steamworks SDK + appID** | 보류 — 프로토타입 마일스톤 범위 초과 |
| (C-3) **Username & Password** | 불필요 | 아이디·비번 기억 | **없음** | 대시보드 provider 토글 | **채택** |

세 안 모두 `com.unity.services.authentication` **3.6.1**에 포함되어 있어 **추가 패키지가 필요 없다** (`services.multiplayer` 2.2.4를 통해 이미 설치됨 — packages-lock.json:229).

C-3는 막다른 길이 아니다 — 한 계정에 여러 identity를 링크할 수 있어, 배포 플랫폼이 확정되면 C-2를 **덧붙이는** 것으로 확장된다.

## 3. UGS 규칙 (문서 확인값)

| 항목 | 규칙 |
|------|------|
| 아이디 | 3~20자, 영숫자 + `.` `-` `@` `_`, **대소문자 무시**, **프로젝트 내 유일** |
| 비밀번호 | 8~30자, **소문자·대문자·숫자·기호 각 1개 이상** |
| 제거 | 한번 링크하면 **제거 불가** |
| 비밀번호 복구 | **Admin API 전용** — 클라이언트 재설정 플로우 없음 |
| 비밀번호 변경 | `UpdatePasswordAsync(현재, 신규)`. 성공 시 **모든 기기에서 로그아웃** |
| 상태 위반 | 이미 로그인된 상태에서 SignUp/SignIn → `AuthenticationErrorCodes.ClientInvalidUserState` |

## 4. 닉네임 우선순위 (가장 위험한 지점)

현재 `RestoreCachedNicknameAsync`([AuthBootstrap.cs:284](../../Assets/Scripts/Network/AuthBootstrap.cs#L284))는 **캐시를 정본으로 서버에 밀어넣는다** — #249에서 "토큰이 지워져 `PlayerId`가 새로 발급된 경우의 복원"을 위해 의도적으로 그렇게 만든 것이다.

그대로 두고 연동을 붙이면: 기기 B에서 계정 로그인 → 기기 B에 남아 있던 **낡은 익명 캐시**가 진짜 계정 닉네임을 덮어쓴다. 계정 종류에 따라 방향을 뒤집는다.

| 계정 상태 | 방향 | 동작 |
|---|---|---|
| 익명 | 캐시 → 서버 | **현행 유지** (토큰 삭제 후 같은 기기에서의 복원 경로) |
| 연동됨 | **서버 → 캐시** | 서버 값으로 캐시를 덮어쓰기만. push 금지 |
| 승격하는 순간 | 캐시 → 서버 1회 | 승격 직전까지 쓰던 이름이 계정에 남도록 |

## 5. 구조

```
AuthBootstrap (Network · CommonManagerBase)         ← 이 이슈에서 바뀌는 유일한 스크립트
   ├─ IsLinked                                     ← 연동 여부 (로그인 직후 1회 조회 후 캐시)
   ├─ LinkAccountAsync(id, pw)                     ← 익명 유지 → AddUsernamePasswordAsync
   ├─ SignInWithAccountAsync(id, pw)               ← SignOut() → SignInWithUsernamePasswordAsync
   ├─ ValidateCredentials(id, pw)                  ← §3 규칙, 위반이면 사유 문자열 (ValidateNickname과 같은 형태)
   └─ RestoreCachedNicknameAsync                    ← §4로 분기 추가
          가드: IsNetworkConnected / CanSignOut()  ← 기존 것 재사용, 신규 없음

AuthPanel (Title 씬)                                ← 입력 2 + 버튼 2 + 상태/경고 텍스트
PlayerNameTag · SessionFlow · SessionManager        ← 수정 없음
```

## 6. 작업 순서

1. **스파이크 — 대시보드 + 실제 API 확인.** Unity Cloud Dashboard에서 Username/Password provider 활성화 후, `m_showDebugGui` 패널([AuthBootstrap.cs:335](../../Assets/Scripts/Network/AuthBootstrap.cs#L335))에 입력 2개 + 버튼 3개를 임시로 붙여 확인한다. 정식 UI는 4단계.
   - `AddUsernamePasswordAsync` 후 **`PlayerId`가 그대로인지** — 승격 검증의 핵심. 바뀌면 이 설계가 성립하지 않는다
   - **연동 여부 판별 수단** — `PlayerInfo.Username` / `PlayerInfo.Identities` 중 무엇이 실제로 채워지는지
   - **아이디 중복 시 실제 에러 코드 값** — 문서에 없어 `AuthenticationErrorCodes` enum을 직접 봐야 한다

   **API 표면은 확인 완료** (2026-07-28, 컴파일 통과 — 공식 문서에 일부 누락되어 여기 기록한다):
   `AddUsernamePasswordAsync` · `SignInWithUsernamePasswordAsync` · `SignUpWithUsernamePasswordAsync` · `UpdatePasswordAsync` · `GetPlayerInfoAsync()` 모두 3.6.1에 존재하고, `PlayerInfo`에는 **`Username`과 `Identities`(`TypeId`/`UserId`)가 둘 다** 있다 → 연동 판별이 `Username` 하나로 끝날 가능성이 있다. 에러 코드는 `RequestFailedException.ErrorCode`로 읽으며 `AuthenticationException`은 파생 클래스라 함께 잡힌다.

   **남은 것은 전부 런타임 확인이다** — 프로퍼티가 존재한다는 것과 값이 채워진다는 것은 다르다. `Username`은 **승격 전·후 두 번** 조회해 익명 상태의 값(빈 문자열인지)까지 확인해야 판별 조건이 확정된다.
2. **`AuthBootstrap` 확장** — §5의 4개. 검증은 `ValidateNickname`(185줄)과 같은 "위반이면 사유 문자열, 통과면 null" 형태로 맞춘다. 세션 참가·전환 중 차단은 기존 `IsNetworkConnected`/`CanSignOut()` 가드를 그대로 재사용한다.
3. **닉네임 우선순위 분기** — `RestoreCachedNicknameAsync`에 §4 표를 반영. 승격 성공 직후의 1회 push는 `LinkAccountAsync` 안에 둔다.
4. **`AuthPanel` UI** — 아이디/비번 입력(비번은 `contentType = Password`) + [계정 만들기·연동] [로그인] + 상태 텍스트. 기존 닉네임 UI와 같은 패턴(`m_isApplyingNickname` 래치 → 실패 시 입력 유지 → `Refresh()`)을 따른다. `Refresh()`에서 `IsLinked`면 연동 버튼 비활성.
5. **문서** — 이 문서 갱신 + GDD 부록 B에 항목 추가(#8이 "기기 간 유지는 #384"로 넘긴 것을 받는 형태) + 이슈 #384에 결정 코멘트(완료 기준 첫 항목).

## 7. 함정 (구현 전에 알고 갈 것)

- **승격과 새 기기 로그인은 전제가 정반대다.** 승격은 익명 로그인 **유지**, 로그인은 `SignOut()` **선행**. `m_signInOnStart`가 이미 익명 로그인을 해두기 때문에 순서를 틀리면 `ClientInvalidUserState`가 난다. 이 예외는 **삼키지 말고** `Debug.LogError` — 사용자 입력 실수가 아니라 코드의 상태 전제 위반이다.
- **아이디 충돌은 익명 계정을 죽이지 않는다.** 중복 실패 후에도 현재 익명 계정은 그대로여야 하고, 사용자는 다른 아이디로 재시도할 수 있어야 한다. 실패 경로에서 `SignOut()`을 부르지 않는다.
- **이슈 본문의 `AccountAlreadyLinked` 시나리오는 형태가 바뀐다.** username/password는 계정당 1개뿐이라 "이미 다른 기기에서 연동된 계정과 충돌"이 아니라 **아이디 유일성 충돌**로 나타난다. 이슈 코멘트에 명시할 것.
- **`RestoreCachedNicknameAsync`의 예외 삼키기를 유지한다.** 여기서 예외가 새면 `OnSignedIn`이 실행되지 않아 로그인은 됐는데 UI가 갱신되지 않는다(319~322줄 주석). 분기를 추가하면서 이 구조를 깨지 않도록 주의.
- **비밀번호 분실 = 계정 상실.** 복구 경로가 없다는 걸 UI에서 미리 알린다. 팀 테스트 계정은 비밀번호를 공유 문서에 적어둘 것.
- **`UpdatePasswordAsync`는 전 기기 로그아웃을 유발한다.** 비밀번호 변경 UI를 넣을 경우 세션 중 호출을 반드시 막아야 한다. (이번 범위에 넣지 않는 이유)
- **MPPM으로 기기 간 유지를 검증할 수 없다.** 가상 플레이어는 같은 기기다. `ClearSessionToken()`이 새 `PlayerId`를 발급하므로 **"토큰 삭제 → 아이디로 로그인"이 새 기기와 동일 조건**이며, 이것이 유일한 검증 수단이다.
- **PlayerPrefs 프로필 스코프를 유지한다.** `NicknamePrefKey`(65줄)가 `m_profile`로 스코프되어 있어 MPPM 가상 플레이어 간 닉네임이 섞이지 않는다(#249 완료 기준). 계정 관련 값을 캐시할 일이 생기면 같은 스코프를 쓸 것.

## 8. 완료 확인 (테스트)

- [ ] 승격 후 **`PlayerId` 불변** + 닉네임 유지
- [ ] `ClearSessionToken()` → 아이디로 로그인 → **닉네임 복원** (= 기기 간 유지 검증)
- [ ] 아이디 중복 → 사유 표시 + **익명 계정 살아 있음** + 재시도 가능
- [ ] 규칙 위반 입력(짧은 아이디/기호 없는 비번 등)이 **클라이언트에서** 막히고 사유가 보임
- [ ] 미연동 익명 플레이가 기존과 동일하게 동작 (연동 UI를 한 번도 누르지 않는 경로)
- [ ] 연동된 계정에서 연동 버튼이 비활성
- [ ] 세션 참가 중에는 연동·로그인 버튼이 막힘 (기존 가드)
- [ ] MPPM 2인에서 가상 플레이어 간 닉네임·계정이 섞이지 않음
- [ ] 이름표(#218) 코드 변경 없이 정상 표시 (경계 유지 확인)

## 9. 미결 항목

- **1단계 스파이크 — 런타임 검증 미완.** API 표면은 확인됐고(§6 1단계), 대시보드 provider 활성화 후 ① 승격 시 `PlayerId` 유지 ② `Username`의 익명/연동 상태별 값 ③ 아이디 중복·이미연동 에러 코드 값을 확인하면 §5·§7을 갱신한다.
- **스파이크 디버그 GUI는 임시 코드다** — 커밋 `00ed905`(`m_showDebugGui` 패널 안쪽)로 분리돼 있다. 검증 결과를 이 문서에 반영한 뒤 PR 전에 삭제하거나 revert한다.
- **플랫폼 링크(C-2)** — 배포 플랫폼 확정 후 별도 이슈. 같은 계정에 identity 추가로 붙으므로 이 설계를 되돌릴 필요는 없다.
- **비밀번호 변경 UI** — Admin API 없이 가능한 유일한 경로가 `UpdatePasswordAsync`이고 전 기기 로그아웃을 동반한다. 필요해지면 별도 이슈.
- **UI 문자열 로컬라이즈** — 현행 관례대로 평문 TMP로 두고 일괄 작업 때 처리 ([settings-ui.md](settings-ui.md) §7과 동일).

---
*작성: 2026-07-28 · 근거: #384 · #249 결정 · 현행 코드(AuthBootstrap/AuthPanel/PlayerNameTag/SessionFlow) 조사 + [UGS Username & Password 문서](https://docs.unity.com/ugs/en-us/manual/authentication/manual/platform-signin-username-password) · [IAuthenticationService API](https://docs.unity3d.com/Packages/com.unity.services.authentication@3.3/api/Unity.Services.Authentication.IAuthenticationService.html)*
