using System;
using UnityEngine;
using Cysharp.Threading.Tasks;
using Unity.Services.Multiplayer;
using Unity.Netcode;

public class SessionManager : MonoBehaviour
{
    [SerializeField] private int m_maxPlayer = 6;
    [SerializeField] private AuthBootstrap m_auth; // 인스펙터로 연결
    public AuthBootstrap Auth => m_auth;

    private ISession m_session;
    public ISession CurrentSession => m_session;

    public event Action<string> OnSessionJoined; // 인자: session.Id
    public event Action OnSessionLeft;
    public event Action OnConnectionLost;   // 비자발 끊김
    private bool m_isLeaving;   // 자발적 LeaveAsync 진행 중 표시

    private void OnEnable()
    {
        if (m_auth != null) m_auth.CanSignOut = () => m_session == null && !m_isBusy;   // 세션에 접속 중이 아니면 로그아웃 가능
    }

    private void OnDisable()
    {
        if (m_auth != null) m_auth.CanSignOut = null;
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

    public async UniTask<string> CreateSessionAsync(int maxPlayer)
    {
        await EnsureSignedInAsync();
        var options = new SessionOptions { MaxPlayers = maxPlayer, Type = "Session" }.WithRelayNetwork();
        ISession session = await MultiplayerService.Instance.CreateSessionAsync(options);
        AdoptSession(session);
        Debug.Log($"[SessionManager] 세션과 호스트 만들어짐 / Id: {session.Id}, Code = {session.Code}");

        return session.Code;
    }

    public async UniTask JoinByCodeAsync(string code)
    {
        await EnsureSignedInAsync();
        ISession session = await MultiplayerService.Instance.JoinSessionByCodeAsync(code);
        AdoptSession(session);
        Debug.Log($"[SessionManager] 세션 참가 완료 / Id: {session.Id}, Code: {session.Code}");
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
        if (session == null) return;

        session.PlayerJoined    += OnPlayerJoined;
        session.Changed         += OnSessionChanged;
        session.SessionPropertiesChanged += OnSessionPropertiesChanged;
        session.Deleted         += OnSessionDeleted;
    }

    private void UnsubscribeSessionEvents(ISession session)
    {
        if (session == null) return;

        session.PlayerJoined    -= OnPlayerJoined;
        session.Changed         -= OnSessionChanged;
        session.SessionPropertiesChanged -= OnSessionPropertiesChanged;
        session.Deleted         -= OnSessionDeleted;
    }

    private void SubscribeNetworkEvents()
    {
        var nm = NetworkManager.Singleton;
        if (nm != null) nm.OnClientDisconnectCallback += OnClientDisconnected;
    }

    private void UnsubscribeNetworkEvents()
    {
        var nm = NetworkManager.Singleton;
        if (nm != null) nm.OnClientDisconnectCallback -= OnClientDisconnected;
    }

    private void OnClientDisconnected(ulong clientId)
    {
        var nm = NetworkManager.Singleton;
        if (nm == null) return;
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
        if (m_isLeaving)        return;
        if (m_session == null)  return;

        Debug.Log($"[SessionManager] 연결 끊김 정규화: {reason}");
        ISession lost = m_session;
        UnsubscribeSessionEvents(lost);
        UnsubscribeNetworkEvents();
        m_session = null;

        OnConnectionLost?.Invoke();
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

    private void OnDestroy()
    {
        if (m_session != null)
        {
            UnsubscribeSessionEvents(m_session);
            UnsubscribeNetworkEvents();
        }
    }

    [SerializeField] private float m_guiTopOffset = 10f;

    private string m_joinCodeInput = string.Empty;
    private bool m_isBusy;
    private string m_status = "대기 중 - 세션을 만들거나 코드로 참가하세요.";

    private void OnGUI()
    {
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

        // 게임 종료 (#210) — 세션 접속 중에는 OnGUI가 DrawInSessionUI로 분기해 이 버튼이 그려지지 않고,
        // 세션에서 나오면(m_session == null) 로비와 함께 다시 나타난다. (Auth/Vivox GUI와 동일한 수명)
        GUILayout.Space(6);
        if (GUILayout.Button("게임 종료 (Quit)"))
        {
            QuitGame();
        }
    }

    // 빌드에서는 앱 종료, 에디터에서는 플레이 모드 종료. (임시 메인 메뉴 버튼, #210)
    private static void QuitGame()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

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
        if (m_isBusy) return;
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
        if (m_isBusy) return;
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
        if (m_isBusy) return;
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
}
