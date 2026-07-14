using Unity.Services.Core.Environments;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;
using System;

public class AuthBootstrap : MonoBehaviour
{
    [SerializeField] private bool m_signInOnStart = true;
    [SerializeField] private string m_environmentName = "production";
    [SerializeField] private string m_profile = string.Empty;

    private bool m_isBusy;
    private string m_status = "대기 중...";
    private bool m_eventsRegistered;

    public event Action OnSignedIn;

    public bool IsSignedIn => 
        UnityServices.State == ServicesInitializationState.Initialized 
        && AuthenticationService.Instance.IsSignedIn;

    public string PlayerId => IsSignedIn ? AuthenticationService.Instance.PlayerId : string.Empty;

    public bool SessionTokenExists => UnityServices.State == ServicesInitializationState.Initialized
        && AuthenticationService.Instance.SessionTokenExists;

    public bool IsNetworkConnected
    {
        get
        {
            var nm = NetworkManager.Singleton;
            return nm != null && (nm.IsClient || nm.IsServer);
        }
    }

    private void RegisterEvents()
    {
        if (m_eventsRegistered) return;
        
        m_eventsRegistered = true;
    }

    private void OnDestroy()
    {
        if (!m_eventsRegistered) return;

        m_eventsRegistered = false;
    }

    private void Start()
    {
        if (m_signInOnStart)
        {
            HandleSignInAsync(m_profile).Forget();
        }
    }

    private async UniTaskVoid HandleSignInAsync(string profile)
    {
        if (m_isBusy) return;
        
        m_isBusy = true;
        m_status = "초기화 + 익명 로그인 중...";

        try
        {
            await InitializeAndSignInAsync(profile);
            m_status = $"로그인 성공 - PlayerId: {PlayerId}";
        }
        catch (Exception ex)
        {
            m_status = $"로그인 실패 - {ex.Message}";
        }
        finally
        {
            m_isBusy = false;
        }
    }

    public async UniTask InitializeAndSignInAsync(string profile = null)
    {
        bool wasSignedIn = IsSignedIn;

        if (UnityServices.State != ServicesInitializationState.Initialized)
        {
            var options = new InitializationOptions();
            if (!string.IsNullOrWhiteSpace(m_environmentName))
            {
                options.SetEnvironmentName(m_environmentName);
            }
            if (!string.IsNullOrWhiteSpace(profile))
            {
                options.SetProfile(profile);
            }

            await UnityServices.InitializeAsync(options);
            Debug.Log($"[AuthBootstrap] UnityServices 초기화 완료 / env: {m_environmentName}, profile: {m_profile}");
        }

        RegisterEvents();

        if (!AuthenticationService.Instance.IsSignedIn)
        {
            try
            {
                await AuthenticationService.Instance.SignInAnonymouslyAsync();
                Debug.Log($"[AuthBootstrap] 익명 로그인 완료 / playerId: {PlayerId}");
            }
            catch (AuthenticationException ex)
            {
                Debug.LogError($"[AuthBootstrap] 인증 실패: {ex.Message}");
                throw;
            }
            catch (RequestFailedException ex)
            {
                Debug.LogError($"[AuthBootstrap] 오류: {ex.Message}");
                throw;
            }
        }

        if (!wasSignedIn && IsSignedIn)
            OnSignedIn?.Invoke();
    }

    public void SignOut(bool clearCredentials = false)
    {
        if (IsNetworkConnected)
        {
            m_status = "세션 참가 중에는 로그아웃 불가";
            Debug.LogWarning("[AuthBootstrap] 연결 중 SignOut 거부 - 세션 이탈 후 재시도.");
            return;
        }

        if (!IsSignedIn) return;

        AuthenticationService.Instance.SignOut(clearCredentials);
        Debug.Log($"[AuthBootstrap] SignOut 완료");
    }

    public void ClearSessionToken()
    {
        if (IsNetworkConnected)
        {
            m_status = "세션 참가 중 토큰 삭제 불가.";
            Debug.LogWarning("[AuthBootstrap] 연결 중 ClearSessionToken 거부 - 세션 이탈 후 재시도.");
            return;
        }

        if (UnityServices.State != ServicesInitializationState.Initialized) return;

        if (IsSignedIn)
        {
            AuthenticationService.Instance.SignOut();
        }

        AuthenticationService.Instance.ClearSessionToken();
        Debug.Log($"[AuthBootstrap] ClearSessionToken 완료");
    }

    private void OnGUI()
    {
        GUILayout.BeginArea(new Rect(10, 10, 380, 280));

        GUILayout.Label("Authentication (익명) — 상태");

        bool initialized = UnityServices.State == ServicesInitializationState.Initialized;
        GUILayout.Label($"초기화됨: {initialized}");
        GUILayout.Label($"IsSignedIn: {IsSignedIn}");
        GUILayout.Label($"PlayerId: {(string.IsNullOrEmpty(PlayerId) ? "(없음)" : PlayerId)}");
        GUILayout.Label($"SessionTokenExists: {(initialized ? SessionTokenExists.ToString() : "(미초기화)")}");

        GUILayout.Label($"연결됨(세션/NGO): {IsNetworkConnected}");
        GUILayout.Space(8);

        bool canSignOut = !m_isBusy && !IsNetworkConnected;

        GUI.enabled = !m_isBusy;
        if (GUILayout.Button("Sign In (init + 익명 로그인)"))
        {
            HandleSignInAsync(string.IsNullOrWhiteSpace(m_profile) ? null : m_profile).Forget();
        }

        GUI.enabled = canSignOut;
        if (GUILayout.Button("Sign Out"))
        {
            SignOut();
        }

        if (GUILayout.Button("New Player (로그아웃 + 토큰 삭제 → 새 PlayerId)"))
        {
            ClearSessionToken();
        }

        GUI.enabled = true;

        if (IsNetworkConnected)
        {
            GUILayout.Label("*세션 참가 중*");
        }

        GUILayout.Space(8);
        GUILayout.Label(m_status);

        GUILayout.EndArea();
    }
}
