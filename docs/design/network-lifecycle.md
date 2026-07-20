# 네트워크 라이프사이클 설계 (초안)

> 상태: **초안 — 팀 검토 필요**. 작성 2026-07-14 (김진아).
> 목적: 인증 · 세션 · NGO · 음성(Vivox) 네 계층의 **소유권과 이벤트 흐름 규칙**을 확정해,
> 관련 이슈 #166 · #167 · #168 · #169(및 #170 · #171)가 서로 모순 없이 구현되도록 한다.

## 배경 (문제의 뿌리)

auth · session · NGO · Vivox 네 계층이 느슨하게만 연결돼 있어, **한 계층의 라이프사이클 전환이 다른 계층으로 전파되지 않는다.**
이것이 하나의 병이고, 네 계층 경계마다 다른 증상으로 나타난다:

- #164 — 로그아웃이 세션/연결을 끊지 않음 (auth → session/NGO 미전파)
- #166 — `NetworkBootstrap`(수동)과 세션(`WithRelayNetwork`)이 같은 `NetworkManager`를 각자 조종 (소유권 이중화)
- #167 — 연결이 밑에서 죽어도(드롭·킥·호스트 이탈) 위 계층이 모름 (NGO → session 미관찰)
- #171 — auth 로그아웃을 Vivox가 관찰하지 않음 (auth → voice 미전파)

아래 규칙은 이 병을 구조적으로 제거한다.

## 원칙

### 1. 단일 소유권
`NetworkManager`(NGO)와 세션 상태의 **소유자는 `SessionManager` 하나**다.

- `SessionManager`가 세션 생성/참가 시 NGO를 띄우고, 이탈 시 내린다.
- `NetworkBootstrap`의 수동 Host/Client/Shutdown 버튼은 **relay 없는 순수 로컬 디버그 전용**으로 격리하거나 제거한다. 세션과 동시에 쓰지 않는다.
- → **#166**

### 2. 단일 진입점
계층 전환은 두 개의 문으로만 들어온다.

- **올라갈 때:** `SessionManager.CreateSessionAsync` / `JoinByCodeAsync`
- **내려갈 때:** `SessionManager.LeaveAsync` ← **유일한 teardown 레버**

"연결을 끊는" 모든 경로(로비 나가기, 메인으로, 로그아웃)는 반드시 `LeaveAsync`를 통과한다.
- → **#169**

### 3. 이벤트는 단방향 (아래 → 위)
`SessionManager`가 **상태의 단일 출처(single source of truth)**이고 이벤트를 방출만 한다.
구독자는 위(Vivox · UI · 게임 흐름)에만 있다. **하위가 상위를 참조하지 않는다** → 순환 없음.

```
SessionManager  ──emit──►  OnSessionJoined(id)
                ──emit──►  OnSessionLeft()          // 자발적 이탈
                ──emit──►  OnConnectionLost()        // 비자발 (드롭·킥·호스트 이탈)
   구독: VivoxManager, UI, (추후) 게임 흐름
```

### 4. 로그아웃 게이트는 델리게이트 주입
`AuthBootstrap`은 세션을 **모르는 채로(leaf)** 있어야 한다. 대신 게이트 훅을 노출하고,
`SessionManager`가 자기 상태를 꽂아준다(타입 의존 없음 → 순환 회피).

```csharp
// AuthBootstrap (세션 타입 참조 없음)
public Func<bool> CanSignOut = () => true;   // 기본 허용
public void SignOut(...)
{
    if (!CanSignOut()) { /* 거부 + 상태 메시지 */ return; }
    ...
}

// SessionManager.Awake/Start 에서 주입 (SessionManager는 이미 m_auth 참조함)
m_auth.CanSignOut = () => m_session == null && !m_isTransitioning;
```

게이트 신호가 **세션 멤버십(`m_session`) + 전환 중(`m_isTransitioning`)** 이라,
#164의 NetworkManager 판정과 create/join 도중 레이스를 **하나로** 덮는다.
- → **#168** (#164를 흡수)

### 5. 끊김은 한 곳에서 정규화
연결이 밑에서 죽는 신호를 `SessionManager`가 전부 받아, `m_session`을 정리하고 위 이벤트로 바꿔 방출한다.

| 입력 신호 | 처리 |
|-----------|------|
| NGO `OnClientDisconnectCallback` (본인 드롭) | `m_session` 정리 → `OnConnectionLost` |
| 세션 삭제 / 호스트 이탈 이벤트 | `m_session` 정리 → `OnConnectionLost` |
| 사용자가 `LeaveAsync` 호출 | 정상 정리 → `OnSessionLeft` |

- → **#167**

## 종료 순서 (원칙 2·3의 결과)

로그아웃(가장 깊은 종료)의 정해진 순서. 이 순서를 **호출부(로비/메뉴 컨트롤러)가 오케스트레이션**한다:

```
await Vivox.LogoutAsync();    // ① 음성 채널 이탈 + 로그아웃을 먼저 완결  (#171)
await Session.LeaveAsync();   // ② 세션 이탈 → NGO 내려감 → OnSessionLeft 방출
                              //    (채널은 ①에서 이미 정리됨 → 이벤트발 채널 이탈은 no-op)
Auth.SignOut();               // ③ 세션이 비었으므로 게이트 열림 → 로그아웃 성공
```

`Auth.SignOut`은 반드시 세션 이탈 뒤에 온다 — `Session.LeaveAsync` 없이 `SignOut`부터 부르면 원칙 4의 게이트가 막는다(**순서 위반이 구조적으로 불가능**).
Vivox 로그아웃은 세션·NGO와 독립이라 세션 이탈보다 **먼저** 완결시킨다. 이렇게 하면 세션 이탈이 방출하는 `OnSessionLeft → 채널 이탈`과의 동시 실행(#169 리뷰에서 지적된 레이스 컨디션)이 구조적으로 사라진다 — 세션 이탈 시점엔 채널이 이미 정리돼 있어 이벤트 핸들러가 즉시 반환한다.

## 의존 방향 요약

```
VivoxManager ──▶ SessionManager ──▶ AuthBootstrap
   (구독자)         (단일 소유자)        (leaf, 세션 모름)
```

- **참조(컴파일타임):** 위 → 아래 한 방향
- **이벤트(런타임):** 아래 → 위 한 방향
- **게이트:** `SessionManager`가 `AuthBootstrap.CanSignOut`으로 역주입 (타입 의존 없음)

## 이슈 매핑 / 구현 순서

| 순서 | 이슈 | 구현하는 규칙 |
|------|------|--------------|
| 1 | #166 ✅ | 원칙 1 — SessionManager를 NGO 단일 소유자로, NetworkBootstrap **제거**(수동 경로 삭제) |
| 2 | #167 | 원칙 5 — 끊김 신호 정규화 → `OnConnectionLost` 방출 |
| 3 | #168 | 원칙 4 — `CanSignOut` 델리게이트 게이트 (#164 흡수) |
| 4 | #169 ✅ | 원칙 2 + 종료 순서 — `LeaveAsync` 단일 레버 + 호출부 오케스트레이션. 임시 오케스트레이터 `SessionTeardown`(PR #243)로 구현, 로비(#154) 도입 시 흡수·제거 예정 |

각각 별도 PR이지만 전부 이 규칙 위에서 움직이므로 서로 충돌하지 않는다.

## #167 구현 계획 (착수 노트)

> #166(단일 소유권) 위에 얹는 자기완결 작업. 로비 불필요. `SessionManager` 한 파일에서 완성된다.

**목표:** 연결이 밑에서 죽는 신호(본인 드롭·킥·호스트 이탈·세션 삭제)를 `SessionManager`가 전부 받아 `m_session`을 정리하고 위로 이벤트화(`OnConnectionLost`). 자발적 이탈(`LeaveAsync`)의 `OnSessionLeft`와 구분한다.

**단계:**
1. `public event Action OnConnectionLost;` 추가 (기존 `OnSessionJoined`/`OnSessionLeft` 옆).
2. NGO `NetworkManager.Singleton.OnClientDisconnectCallback` 구독 → **본인 드롭** 감지 → `m_session` 정리 → `OnConnectionLost` 방출.
3. `ISession`의 세션 삭제/호스트 이탈 이벤트 구독 → 동일 정규화 (기존 `SubscribeSessionEvents`/`UnsubscribeSessionEvents`에 대칭 추가).
4. **자발 vs 비자발 가드(필수):** `LeaveAsync`는 이미 `OnSessionLeft`를 쏜다 → Leave 진행 중엔 `OnConnectionLost`가 새지 않도록 플래그(예: `m_isLeaving`)로 구분. 미구현 시 나가기 때 두 이벤트가 겹쳐 발화한다.
5. 삭제된 `NetworkBootstrap`의 접속/끊김 `Debug.Log` 로깅을 여기로 재구현.

**착수 전 확인할 불확실 요소 2개 (견적이 여기서 갈림):**
1. **어떤 `ISession` 이벤트가 "호스트 이탈/세션 삭제"를 알리는지** — Multiplayer SDK 2.2.4 문서 확인 필요. 현재 구독 중인 건 `PlayerJoined`/`Changed`/`SessionPropertiesChanged`뿐.
2. **NGO `OnClientDisconnectCallback` 구독 타이밍** — `NetworkManager`가 세션 생성 중에 뜨므로 `AdoptSession` 시점 또는 `IsListening` 확인 후 구독, `OnDisable`에서 대칭 해제.

**검증:** MPPM에서 호스트 종료 / 클라 드롭 / 네트워크 킬 각각 `OnConnectionLost` **1회만** 발화 + 자발 `LeaveAsync` 시 새지 않음.

**참고:** `Assets/Scripts/Network/SessionManager.cs` (`LeaveAsync`, `AdoptSession`, `Subscribe/UnsubscribeSessionEvents`, `OnDisable`). 삭제된 로깅 로직은 커밋 `dbf0c06^`에서 참고.

## 미결 사항 (검토 필요)

- ~~**teardown 오케스트레이션 주체:** 별도 클래스 vs 로비/메뉴 컨트롤러 겸임.~~ **결정(#169, 2026-07-19):** 로비(#154)가 아직 없어 **임시 별도 클래스 `SessionTeardown`** 로 구현(PR #243). 로비 도입 시 그 컨트롤러가 `LeaveToMainAsync()`를 호출하도록 흡수하고 임시 클래스는 제거한다.
- ~~`SessionManager.cs`의 `TODO(#51/#55/#56)` 주석은 **stale**.~~ **확인 완료(#169, 2026-07-19):** 해당 주석은 선행 PR에서 이미 정리돼 현재 `SessionManager.cs`에 없음 — 조치 불필요.
- ~~세션 SDK가 `LeaveAsync` 시 NGO를 자동 Shutdown 하는지 확인 필요.~~ **확인 완료(#166, 2026-07-15):** Multiplayer SDK 2.2.4가 `LeaveAsync` 시 NGO를 자동으로 내려준다(MPPM에서 Leave 후 `NetworkManager.Singleton.IsListening → false` 검증). 따라서 `SessionManager`가 명시적으로 `Shutdown()`을 호출할 필요 없음.
