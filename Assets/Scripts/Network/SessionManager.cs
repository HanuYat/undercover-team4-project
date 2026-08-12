using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using Unity.Services.Multiplayer;
using UnityEngine;

[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class SessionManager : CommonManagerBase
{
    // 게임 버전을 담는 세션 프로퍼티 키 (#586). 값은 NetworkProtocol.VersionString.
    private const string k_versionProperty = "ver";

    // 프로퍼티가 아예 없는 세션 — #586 이전 빌드가 만든 방이다. 값이 다른 것과 똑같이 취급하되
    // 표시만 구분한다(빈 문자열이면 "방 버전 " 뒤가 비어 무슨 말인지 알 수 없다).
    private const string k_unknownVersion = "?";

    [SerializeField]
    private int m_maxPlayer = 6;

    [SerializeField]
    private AuthBootstrap m_auth; // 인스펙터로 연결
    public AuthBootstrap Auth => m_auth;

    private ISession m_session;
    public ISession CurrentSession => m_session;

    public event Action<string> OnSessionJoined; // 인자: session.Id
    public event Action OnSessionLeft;
    public event Action OnConnectionLost; // 비자발 끊김
    private bool m_isLeaving; // 자발적 LeaveAsync 진행 중 표시

    private void OnEnable()
    {
        if (m_auth != null)
            m_auth.CanSignOut = () => m_session == null && !m_isBusy; // 세션에 접속 중이 아니면 로그아웃 가능
    }

    private void OnDisable()
    {
        if (m_auth != null)
            m_auth.CanSignOut = null;
    }

    public async UniTask EnsureSignedInAsync()
    {
        if (m_auth == null)
        {
            Debug.LogError($"[SessionManager] AuthBootstrap 참조가 없습니다.");
            throw new InvalidOperationException("AuthBootstrap not assigned");
        }

        await m_auth.InitializeAndSignInAsync();
    }

    /// <summary>인스펙터의 최대 인원(m_maxPlayer)으로 세션을 생성한다 — 세션 관문 UI(#247)용.</summary>
    public UniTask<string> CreateSessionAsync() => CreateSessionAsync(m_maxPlayer);

    public async UniTask<string> CreateSessionAsync(int maxPlayer)
    {
        await EnsureSignedInAsync();
        var options = new SessionOptions
        {
            MaxPlayers = maxPlayer,
            Type = "Session",
            // Public이어야 참가자가 Properties로 읽을 수 있다 (#586)
            SessionProperties = new Dictionary<string, SessionProperty>
            {
                [k_versionProperty] = new SessionProperty(
                    NetworkProtocol.VersionString,
                    VisibilityPropertyOptions.Public
                ),
            },
        }.WithRelayNetwork();
        ISession session = await MultiplayerService.Instance.CreateSessionAsync(options);
        AdoptSession(session);
        Debug.Log(
            $"[SessionManager] 세션과 호스트 만들어짐 / Id: {session.Id}, Code = {session.Code}"
        );

        return session.Code;
    }

    public async UniTask JoinByCodeAsync(string code)
    {
        await EnsureSignedInAsync();
        ISession session = await MultiplayerService.Instance.JoinSessionByCodeAsync(code);

        // 버전 검사는 AdoptSession보다 먼저 — 채택하면 OnSessionJoined가 발화해 Vivox가 음성 채널까지
        // 붙는다. 여기까지 await 없이 이어지므로 참가와 검사 사이에 NGO가 한 프레임도 돌지 않는다. (#586)
        string sessionVersion = ReadVersion(session);
        if (sessionVersion != NetworkProtocol.VersionString)
        {
            Debug.LogWarning(
                $"[SessionManager] 버전 불일치로 참가 취소 / 내 버전: {NetworkProtocol.VersionString}, 방 버전: {sessionVersion}"
            );
            await AbandonAsync(session);
            throw new SessionVersionMismatchException(
                NetworkProtocol.VersionString,
                sessionVersion
            );
        }

        AdoptSession(session);
        Debug.Log($"[SessionManager] 세션 참가 완료 / Id: {session.Id}, Code: {session.Code}");
    }

    private static string ReadVersion(ISession session)
    {
        if (
            session.Properties != null
            && session.Properties.TryGetValue(k_versionProperty, out SessionProperty property)
            && !string.IsNullOrEmpty(property.Value)
        )
        {
            return property.Value;
        }

        return k_unknownVersion;
    }

    /// <summary>
    /// 버전이 다른 세션에서 즉시 물러난다 (#586). NGO를 먼저 끊는 이유는 LeaveAsync(HTTP 왕복)를
    /// 기다리는 사이 호스트의 씬 동기화가 도착해 인게임 씬 로드가 시작되기 때문이다 — 그러면
    /// 실패 문구를 띄울 SessionPanel이 이미 파괴된 뒤다.
    /// 아직 AdoptSession 전이라 m_session이 없어 HandleConnectionLost(드롭 복귀)는 걸리지 않는다.
    /// </summary>
    private static async UniTask AbandonAsync(ISession session)
    {
        if (NetworkManager.Singleton != null)
            NetworkManager.Singleton.Shutdown();

        try
        {
            await session.LeaveAsync();
        }
        catch (Exception ex)
        {
            // 사용자에게 전할 말은 버전 불일치지 나가기 실패가 아니다 — 삼키고 원래 예외를 올린다
            Debug.LogWarning($"[SessionManager] 버전 불일치 세션 나가기 실패(무시): {ex.Message}");
        }
    }

    public async UniTask LeaveAsync()
    {
        if (m_session == null)
        {
            Debug.Log($"[SessionManager] 나갈 세션 없음");
            return;
        }

        ISession leaving = m_session;
        m_isLeaving = true;

        try
        {
            await leaving.LeaveAsync();
        }
        finally
        {
            UnsubscribeSessionEvents(leaving);
            UnsubscribeNetworkEvents();
            if (ReferenceEquals(m_session, leaving))
                m_session = null;

            m_isLeaving = false;
            OnSessionLeft?.Invoke();
        }
    }

    private void AdoptSession(ISession session)
    {
        if (m_session != null && !ReferenceEquals(m_session, session))
        {
            UnsubscribeSessionEvents(m_session);
        }

        m_session = session;
        SubscribeSessionEvents(m_session);
        UnsubscribeNetworkEvents();
        SubscribeNetworkEvents();

        OnSessionJoined?.Invoke(session.Id);
    }

    private void SubscribeSessionEvents(ISession session)
    {
        if (session == null)
            return;

        session.PlayerJoined += OnPlayerJoined;
        session.Changed += OnSessionChanged;
        session.SessionPropertiesChanged += OnSessionPropertiesChanged;
        session.Deleted += OnSessionDeleted;
    }

    private void UnsubscribeSessionEvents(ISession session)
    {
        if (session == null)
            return;

        session.PlayerJoined -= OnPlayerJoined;
        session.Changed -= OnSessionChanged;
        session.SessionPropertiesChanged -= OnSessionPropertiesChanged;
        session.Deleted -= OnSessionDeleted;
    }

    private void SubscribeNetworkEvents()
    {
        var nm = NetworkManager.Singleton;
        if (nm != null)
            nm.OnClientDisconnectCallback += OnClientDisconnected;
    }

    private void UnsubscribeNetworkEvents()
    {
        var nm = NetworkManager.Singleton;
        if (nm != null)
            nm.OnClientDisconnectCallback -= OnClientDisconnected;
    }

    private void OnClientDisconnected(ulong clientId)
    {
        var nm = NetworkManager.Singleton;
        if (nm == null)
            return;
        if (nm.IsServer && clientId != nm.LocalClientId)
        {
            Debug.Log($"[SessionManager] 원격 클라 끊김: {clientId} (무시)");
            return;
        }

        HandleConnectionLost("NGO 본인 드롭");
    }

    private void OnSessionDeleted() => HandleConnectionLost("세션 삭제/호스트 종료");

    private void HandleConnectionLost(string reason)
    {
        if (m_isLeaving)
            return;
        if (m_session == null)
            return;

        Debug.Log($"[SessionManager] 연결 끊김 정규화: {reason}");
        ISession lost = m_session;
        UnsubscribeSessionEvents(lost);
        UnsubscribeNetworkEvents();
        m_session = null;

        OnConnectionLost?.Invoke();

        // 비자발 드롭은 SDK에 Deleted/RemovedFromSession 이벤트를 안 주므로, MultiplayerService
        // 레지스트리에 세션이 남아 다음 생성이 "already registered"로 실패한다. SDK LeaveAsync를 걸어
        // SDK 자체 핸들러가 레지스트리에서 세션을 빼게 한다(호스트=DeleteAsync). (#287)
        TeardownLostSessionAsync(lost).Forget();
    }

    // 끊긴 세션을 SDK 레지스트리에서 내린다. 죽은 relay와 무관하게 Lobby 백엔드(HTTP)로 정리되며,
    // 이미 삭제된 세션이면 SDK 내부에서 즉시 반환한다(안전한 no-op). 실패해도 게임 흐름은 막지 않는다.
    private static async UniTaskVoid TeardownLostSessionAsync(ISession lost)
    {
        try
        {
            await lost.LeaveAsync();
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[SessionManager] 끊긴 세션 SDK 정리 실패(무시): {ex.Message}");
        }
    }

    private void OnPlayerJoined(string playerId)
    {
        Debug.Log($"[SessionManager] 플레이어 참가: {playerId}");
    }

    private void OnSessionChanged()
    {
        Debug.Log($"[SessionManager] OnSessionChanged()");
    }

    private void OnSessionPropertiesChanged()
    {
        Debug.Log($"[SessionManager] OnSessionPropertiesChanged()");
    }

    protected override void OnDestroy()
    {
        base.OnDestroy(); // App 등록 해제

        if (m_session != null)
        {
            UnsubscribeSessionEvents(m_session);
            UnsubscribeNetworkEvents();
        }
    }

    [Tooltip(
        "OnGUI 디버그 패널 표시 — 테스트 씬 수동 세션 조작용. 정식 UI는 SessionPanel·SessionCodePanel (#247)"
    )]
    [SerializeField]
    private bool m_showDebugGui;

    [SerializeField]
    private float m_guiTopOffset = 10f;

    private string m_joinCodeInput = string.Empty;
    private bool m_isBusy;
    private string m_status = "대기 중 - 세션을 만들거나 코드로 참가하세요.";

    private void OnGUI()
    {
        if (!m_showDebugGui)
            return;

        GUILayout.BeginArea(new Rect(10, m_guiTopOffset, 380, 300));

        if (m_session == null)
        {
            DrawLobbyUI();
        }
        else
        {
            DrawInSessionUI();
        }

        GUILayout.Space(8);
        GUILayout.Label(m_status);

        GUILayout.EndArea();
    }

    private void DrawLobbyUI()
    {
        GUILayout.Label("세션 — 만들거나, 코드로 참가");

        GUI.enabled = !m_isBusy;

        if (GUILayout.Button("세션 만들기 (Create)"))
        {
            HandleCreateAsync().Forget();
        }

        GUILayout.Space(6);
        GUILayout.Label("Join 코드:");
        m_joinCodeInput = GUILayout.TextField(m_joinCodeInput ?? string.Empty);

        if (GUILayout.Button("코드로 참가 (Join)"))
        {
            HandleJoinAsync(m_joinCodeInput).Forget();
        }

        GUI.enabled = true;
    }

    // 게임 종료 버튼은 Title 메인 메뉴(TitleUIManager)로 이관됨 — 여기 OnGUI는 순수 세션 디버그 조작만 남긴다. (#224)

    private void DrawInSessionUI()
    {
        GUILayout.Label($"세션 Id: {m_session.Id}");

        GUILayout.Label("이 코드를 공유하세요:");
        GUILayout.TextField(m_session.Code ?? string.Empty);

        GUILayout.Space(8);
        GUI.enabled = !m_isBusy;
        if (GUILayout.Button("세션 나가기 (Leave)"))
        {
            HandleLeaveAsync().Forget();
        }
        GUI.enabled = true;
    }

    private async UniTaskVoid HandleCreateAsync()
    {
        if (m_isBusy)
            return;
        m_isBusy = true;
        m_status = "세션 생성 중...";
        try
        {
            string code = await CreateSessionAsync(m_maxPlayer);
            m_status = $"세션 생성됨. 공유 코드: {code}";
        }
        catch (Exception e)
        {
            m_status = $"세션 생성 실패: {e.Message}";
            Debug.LogError($"[SessionManager] 세션 생성 실패: {e}");
        }
        finally
        {
            m_isBusy = false;
        }
    }

    private async UniTaskVoid HandleJoinAsync(string code)
    {
        if (m_isBusy)
            return;
        m_isBusy = true;
        m_status = "세션 참가 중...";
        try
        {
            await JoinByCodeAsync(code);
            m_status = "세션에 참가했습니다.";
        }
        catch (Exception e)
        {
            m_status = $"세션 참가 실패: {e.Message}";
            Debug.LogError($"[SessionManager] 세션 참가 실패: {e}");
        }
        finally
        {
            m_isBusy = false;
        }
    }

    private async UniTaskVoid HandleLeaveAsync()
    {
        if (m_isBusy)
            return;
        m_isBusy = true;
        m_status = "세션 나가는 중...";
        try
        {
            await LeaveAsync();
            m_status = "세션에서 나갔습니다.";
        }
        catch (Exception e)
        {
            m_status = $"세션 나가기 실패: {e.Message}";
            Debug.LogError($"[SessionManager] 세션 나가기 실패: {e}");
        }
        finally
        {
            m_isBusy = false;
        }
    }

    /// <summary>세션 잠금/해제 — 호스트 전용. 잠그면 코드 참가가 거부된다. (#214 게임 중 신규 접속 차단)</summary>
    public async UniTask SetLockedAsync(bool locked)
    {
        if (m_session == null)
            return;
        try
        {
            IHostSession host = m_session.AsHost(); // 호스트(세션 생성자)만 유효
            if (host.IsLocked == locked)
                return;
            host.IsLocked = locked;
            await host.SavePropertiesAsync();
            Debug.Log($"[SessionManager] 세션 {(locked ? "잠금(참가 차단)" : "해제(참가 허용)")}");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[SessionManager] 세션 잠금 변경 실패(무시): {ex.Message}");
        }
    }
}
