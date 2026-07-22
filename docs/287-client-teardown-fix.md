# #287 — 멀티플레이 클라이언트 로그아웃(타이틀 복귀) 문제 해결 정리

- **날짜:** 2026-07-22
- **브랜치:** `bug/287-session-recreate`
- **증상 재현:** 멀티플레이에서 게임이 시작된 뒤, **클라이언트**가 "메인으로 나가기(로그아웃)" 버튼을 눌러 타이틀 씬으로 가려고 하면 에러가 발생하고 씬 전환이 막힘. (호스트는 정상)

---

## 1. 한 줄 요약

로그아웃 처리기 [`SessionTeardown`](../Assets/Scripts/Network/SessionTeardown.cs)가 매니저를 `[SerializeField]`로 참조하고 있었는데, 그 매니저들이 **DontDestroyOnLoad 상주 매니저**라 씬 오브젝트에서는 런타임에 **참조가 null**이 됐다. 그 결과 **로그아웃 정리 코드(Vivox 로그아웃·세션 이탈·SignOut)가 통째로 스킵**되고, NGO가 살아있는 채로 씬 전환을 시도해 막혔다. → 매니저 접근을 **`App` 파사드**로 바꿔 해결(아키텍처 규칙 R1 준수). 부수적으로 발견된 3건도 함께 정리.

---

## 2. 최초 증상 (콘솔 에러)

```
[AppHelper] 세션 중 씬 전환은 서버만 할 수 있습니다: Title
  AppHelper:LoadScene (at AppHelper.cs:46)
  App:LoadScene (at App.cs:52)
  SessionTeardown/<LeaveToMainAsync>d__4 (at SessionTeardown.cs:57)
  ... WaitForNetworkShutdownAsync ...
```

- 이 에러는 [`AppHelper.LoadScene`](../Assets/Scripts/Core/AppHelper.cs)의 가드에서 난다: `NetworkManager가 IsListening && !IsServer`(= 세션에 붙어있는 클라이언트)면 씬 전환을 거부하고 `LogError` 후 `return`.
- 즉 **로그아웃 시점에 클라의 NGO가 아직 `IsListening == true`**라서 이 가드에 걸린 것.
- 호스트는 `IsServer`라 가드를 통과 → 에러가 안 보여서 **클라 전용 증상**처럼 보였음.

---

## 3. 진단 과정 (로그 기반으로 좁혀감)

> 핵심 교훈: **"문제 예상 지점에 로그를 심고 데이터로 좁혀가는" 방식**이 결정타였다. 추측이 길어질 땐 이 방식으로 전환할 것.

중간에 세운 가설들과 결과:

1. ❌ *"세션 이탈 시 SDK가 클라 NGO를 안 내려서 그렇다"* → SDK 소스(`NetworkManagerSession`)를 읽어보니, `session.LeaveAsync()`는 원래 NGO를 내려주는 게 맞았다.
2. ❌ *"`NetworkManager.Shutdown()`을 직접 부르면 SDK가 NRE를 낸다"* → 이건 **사실로 확인**됨(아래 4-B). 하지만 근본 원인은 아니었다.
3. ❌ *"세션 모듈 teardown이 예외를 던지고 SDK가 삼킨다"* → verbose 로그로 반증됨. 체인은 정상 진입.
4. ❌ *"RoundEndResetter가 앞질러 raw Shutdown을 해서 충돌"* → raw Shutdown을 다 제거했는데도 재발 → 이것도 원인 아님.
5. ✅ **`SessionTeardown`에 진단 로그를 심어 클라 상태를 직접 찍음** → 결정적 단서 발견:

```
[Teardown-DIAG] 이탈 전 — IsListening=True, IsServer=False, IsClient=True,
                IsConnectedClient=True, ShutdownInProgress=False, LocalId=1 | session-mgr=null
[Teardown-DIAG] session.LeaveAsync 직후 — IsListening=True, ... | session-mgr=null
[Teardown-DIAG] LoadScene 직전 — IsListening=True, ... | session-mgr=null
```

`session-mgr=null` = **`SessionTeardown.m_session`(매니저 참조)이 클라에서 null**. 그래서 `session.LeaveAsync()` 자체가 호출된 적이 없었다(`ShutdownInProgress=False`가 끝까지 유지). NGO에 "나가라" 신호가 아예 안 간 것.

> MCP는 메인 에디터(호스트) 콘솔만 읽을 수 있어서, MPPM 가상 플레이어(클라) 상태를 못 봤던 게 진단을 오래 끌게 한 원인. 클라 상태를 직접 로그로 찍고 나서야 즉시 확정됨.

---

## 4. 근본 원인 + 부수 문제

### 4-A. (진짜 원인) 매니저를 serialized 참조로 잡아 런타임 null

[`App.cs`](../Assets/Scripts/Core/App.cs)에 명시된 대로 `SessionManager`·`VivoxManager`·`AuthBootstrap`은 **DontDestroyOnLoad 상주 매니저(AppBootstrap 프리팹)**.
`SessionTeardown`은 **게임 씬 오브젝트**라, 이들을 `[SerializeField]`로 직접 참조하면 **상주 프리팹 인스턴스를 씬에서 못 잡아 런타임에 null**이 된다. (CLAUDE.md **규칙 R1**: 매니저 접근은 `App` 파사드 단일 경로, serialized 참조 금지)

그 결과 로그아웃 시 실제 실행은 이랬다:

```csharp
if (Vivox   != null) await Vivox.LogoutAsync();   // Vivox 참조 null → 스킵 → Vivox 소리가 계속 남
if (Session != null) await Session.LeaveAsync();  // Session 참조 null → 스킵 → NGO 안 내려감
if (Auth    != null) Auth.SignOut();              // Auth 참조 null → 스킵
App.LoadScene(Title);                             // NGO 살아있음 → 클라 씬 전환 차단 에러
```

→ **teardown 전체가 no-op**이었고, 지금까지 본 모든 증상(씬 전환 차단, Vivox 잔존, Relay 비활동 타임아웃)이 여기서 파생됨.

> 참고: `VivoxManager`/`SessionManager`는 서로 **같은 AppBootstrap 프리팹**에 있어 그들끼리의 serialized 참조는 정상 동작한다. 문제는 프리팹 **밖**의 씬 오브젝트(`SessionTeardown`)가 참조할 때만 발생.

### 4-B. (부수) `NetworkManager.Shutdown()` 직접 호출 → SDK NRE

원인 추적 중 임시로 넣었던 fallback `nm.Shutdown()`이 아래 NRE를 유발했다.

```
[ServicesCore]: System.NullReferenceException
  at NetworkManagerSession.OnStopCompleted () ... NetworkManagerSession.cs:417
```

SDK(`NetworkManagerSession`)는 세션이 NGO를 소유할 때 **`ISession.LeaveAsync`로만** 내려야 하며, `NetworkManager.Shutdown()`을 직접 부르면 자기 종료 핸드셰이크를 건너뛰어(`ShutdownInProgress` 가드) 완료 콜백에서 null 참조가 난다. SDK 소스에 경고문이 박혀 있음:
> *"Do not call NetworkManager.Shutdown when using a session. Use ISession.LeaveAsync instead."*

→ **raw Shutdown은 어디서도 부르지 않는다**로 정리.

### 4-C. (부수) 이중 teardown 충돌

[`RoundEndResetter`](../Assets/Scripts/Round/RoundEndResetter.cs)가 `NetworkManager.OnClientStopped`를 구독해, **어떤 이유로든 NGO가 멈추면** 독립적으로 또 하나의 완전한 teardown(세션 이탈 + Shutdown + `LoadScene(Title)`)을 돌렸다. 자발적 로그아웃(`SessionTeardown`)과 겹쳐 충돌 소지.

### 4-D. (부수) Vivox 채널 참가 레이스 → `accessToken null`

teardown이 **실제로 동작하게 되자** 드러난 레이스.

```
[VivoxManager] System.ArgumentNullException: Parameter name: accessToken
  at ChannelSession.BeginConnect ...
  at VivoxManager.JoinChannelAsync (at VivoxManager.cs:146)
```

세션 참가 시 시작된 `JoinChannelAsync`가 내부 `EnsureLoggedInAsync`(Vivox 초기화+로그인, 느림)를 await하는 동안 로그아웃이 `Auth.SignOut()`을 실행 → 뒤늦게 로그인이 끝나 채널 참가로 진행 → 인증이 풀려 토큰을 못 만들어 예외. (try/catch로 잡히긴 하지만 빨간 로그가 남음)

---

## 5. 적용한 코드 (수정 4건)

### ① SessionTeardown — 매니저 참조를 `App` 파사드로 (진짜 원인 수정)

```diff
-    [SerializeField]
-    private SessionManager m_session;
-    [SerializeField]
-    private VivoxManager m_vivox;
-    [SerializeField]
-    private AuthBootstrap m_auth;
+    // 매니저는 App 파사드로만 접근한다 (R1). SessionManager·VivoxManager·AuthBootstrap은 DontDestroyOnLoad
+    // 상주 매니저(AppBootstrap 프리팹)라, 씬 오브젝트인 이 클래스에서 [SerializeField]로 잡으면 런타임에
+    // null이 된다 — 그러면 아래 teardown이 통째로 스킵돼 NGO/Vivox가 안 내려간다(#287의 실제 원인).
+    private static SessionManager Session => App.Net.Session;
+    private static VivoxManager   Vivox   => App.Net.Vivox;
+    private static AuthBootstrap  Auth    => App.Net.Auth;
```
사용처도 `m_session`→`Session`, `m_vivox`→`Vivox`, `m_auth`→`Auth`로 교체.

### ② SessionTeardown — NGO는 SDK로만 내림 (raw Shutdown 금지, 폴링만)

```csharp
// NGO는 반드시 SDK(ISession.LeaveAsync)로만 내려야 한다 — NetworkManager.Shutdown()을 직접 부르면
// OnStopCompleted에서 NRE가 난다. 그래서 SDK가 내릴 때까지 폴링만 한다. 무한 대기 방지 타임아웃.
private static async UniTask WaitForNetworkShutdownAsync()
{
    float deadline = Time.realtimeSinceStartup + k_shutdownTimeoutSeconds;
    while (NetworkManager.Singleton != null
        && NetworkManager.Singleton.IsListening
        && Time.realtimeSinceStartup < deadline)
    {
        await UniTask.Yield();
    }
    if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
        Debug.LogWarning("[SessionTeardown] NGO가 제한시간 내에 완전히 내려가지 않음 — 그대로 진행");
}
```

### ③ RoundEndResetter — 클라 트리거를 비자발 드롭에만 반응하도록 변경 + raw Shutdown 가드

```diff
-        // NetworkManager는 자체 Awake에서 Singleton을 세팅하므로, 준비가 보장되는 Start에서 구독한다.
-        NetworkManager nm = NetworkManager.Singleton;
-        if (nm != null)
-            nm.OnClientStopped += HandleClientStopped;
+        // 클라이언트 리셋 신호는 "비자발 드롭"(호스트가 세션을 내림)만 삼는다 — SessionManager.OnConnectionLost.
+        // OnClientStopped(모든 NGO 정지에 반응)를 쓰면 자발적 로그아웃(SessionTeardown)이 촉발한 정지에도
+        // 깨어나 중복 teardown으로 충돌한다(#287). OnConnectionLost는 m_isLeaving 가드로 자발적 이탈 땐 발화 안 함.
+        if (Session != null)
+            Session.OnConnectionLost += HandleConnectionLost;
```
```diff
-        // 2) NGO 종료 (떠 있을 때만).
+        // 2) NGO 종료. 세션이 있으면 위 LeaveAsync가 SDK 경로로 이미 내렸다 — 직접 Shutdown하면 NRE(#287).
+        //    그래서 SDK 세션이 아예 없는 로컬/오프라인 NGO만 여기서 직접 내린다.
         NetworkManager nm = NetworkManager.Singleton;
-        if (nm != null && (nm.IsListening || nm.IsClient || nm.IsServer))
+        if ((Session == null || Session.CurrentSession == null)
+            && nm != null && (nm.IsListening || nm.IsClient || nm.IsServer))
             nm.Shutdown();
```
핸들러 `HandleClientStopped(bool)` → `HandleConnectionLost()`로 변경.

### ④ VivoxManager — 채널 참가 전 세션·인증 유효성 가드 (accessToken null 레이스)

```diff
         await EnsureLoggedInAsync();
         if (!m_loggedIn) return;
 
+        // EnsureLoggedInAsync(Vivox 초기화+로그인)를 기다리는 사이에 로그아웃/세션 이탈이 끝났을 수 있다
+        // (#287 teardown 레이스). 그 상태로 채널에 참가하면 인증이 풀려 accessToken null 예외가 난다 —
+        // 아직 세션·인증이 살아있을 때만 참가한다.
+        if (m_session == null || m_session.CurrentSession == null
+            || !AuthenticationService.Instance.IsSignedIn)
+            return;
 
         await LeaveChannelAsync();  // 재참가 대비
```

> ※ `VivoxManager.m_session`은 같은 AppBootstrap 프리팹 내 참조라 정상 동작(4-A와 무관).

---

## 6. 결과

- ✅ 클라이언트가 로그아웃 시 **Title로 정상 복귀** (teardown이 실제로 실행됨).
- ✅ raw Shutdown NRE 없음.
- ✅ Vivox 로그아웃이 실제로 실행됨(소리 잔존 해소 기대).
- ⚠️ 마지막 재현에서 4-D(Vivox accessToken null)가 관측됨 → ④ 가드로 수정 적용. **재검증 필요**.

---

## 7. 남은 작업 (내일 아침 확인용 체크리스트)

- [ ] **컴파일 검증** — 4개 파일 수정 후 Unity 콘솔에 컴파일 에러 0 확인. (마지막에 MCP 연결이 끊겨 자동 검증을 못 했음 — Play 정지 후 재컴파일되면 확인)
- [ ] **임시 진단 설정 제거** — Player Settings → Scripting Define Symbols에서 **`ENABLE_UNITY_MULTIPLAYER_VERBOSE_LOGGING` 제거**. (진단용으로 켰던 것 — `ProjectSettings.asset`이 이 브랜치에 변경으로 남아 있음)
- [ ] **재현 테스트 1 (자발적 로그아웃):** 호스트+클라 시작 → 클라에서 "메인으로 나가기" → 빨간 에러 없이 Title 복귀 확인.
      - `세션 중 씬 전환은 서버만` 없음 / `OnStopCompleted` NRE 없음 / `accessToken null` 없음 / Relay 타임아웃 없음.
- [ ] **재현 테스트 2 (비자발 드롭 회귀):** 호스트가 나가거나 세션 종료 → 클라가 여전히 자동으로 Title 복귀하는지 확인 (RoundEndResetter 본래 기능이 안 깨졌는지).

---

## 8. 변경 파일 목록

| 파일 | 내용 |
|---|---|
| `Assets/Scripts/Network/SessionTeardown.cs` | 매니저 참조 → App 파사드(①), raw Shutdown 제거·폴링만(②) |
| `Assets/Scripts/Round/RoundEndResetter.cs` | 클라 트리거 `OnClientStopped`→`OnConnectionLost`, raw Shutdown 가드(③) |
| `Assets/Scripts/Network/VivoxManager.cs` | 채널 참가 전 세션·인증 가드(④) |
| `ProjectSettings/ProjectSettings.asset` | ⚠️ 진단용 verbose 디파인 — **제거 예정** |

> 참고: 이 브랜치엔 이전 작업분(`SessionManager`의 `OnConnectionLost`/`m_isLeaving` 등 #287 관련)이 함께 올라와 있고, 이번 수정은 그 위에서 동작한다.
