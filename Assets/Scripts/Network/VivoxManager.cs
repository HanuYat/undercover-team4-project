using System;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.InputSystem;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using Unity.Services.Vivox;
using Unity.Services.Authentication;

public class VivoxManager : MonoBehaviour
{
    [SerializeField] private string m_channelPrefix = "Radio";
    [SerializeField] private InputActionReference m_pushToTalkAction;
    [SerializeField] private SessionManager m_session;   // 인스펙터에서 연결
    private bool m_loggedIn;
    //private bool m_joined; // -> m_radioJoined, m_proximityJoined로 대체
    private bool m_transmitting;
    private bool m_starting;
    private string m_status = "대기 중...";

    [Header("근접 음성 (positional)")]
    [SerializeField] private string m_proximityChannelPrefix = "Proximity";
    [SerializeField] private int m_conversationalDistance = 3;
    [SerializeField] private int m_audibleDistance = 15;
    [SerializeField] private float m_audioFadeIntensity = 1.0f; // 감쇠 강도 (테스트 중 멀어져도 크게 들리면 강도 ↑)
    [SerializeField] private float m_positionUpdateInterval = 0.1f; // 위치 보고 주기
    private bool m_radioJoined;
    private bool m_proximityJoined;
    private string m_proximityChannelName;
    private CancellationTokenSource m_posLoopCts;

    private void OnEnable()
    {
        if (m_session != null)
        {
            m_session.OnSessionJoined += HandleSessionJoined;
            m_session.OnSessionLeft += HandleSessionLeft;

            if (m_session.Auth != null)
                m_session.Auth.OnSignedOut += HandleAuthSignedOut;
        }

        if (m_pushToTalkAction != null)
        {
            m_pushToTalkAction.action.started += OnPushToTalkStarted;
            m_pushToTalkAction.action.canceled += OnPushToTalkCanceled;
            m_pushToTalkAction.action.Enable();
        }
    }

    private void OnDisable()
    {
        if (m_session != null)
        {
            m_session.OnSessionJoined -= HandleSessionJoined;
            m_session.OnSessionLeft -= HandleSessionLeft;

            if (m_session.Auth != null)
                m_session.Auth.OnSignedOut -= HandleAuthSignedOut;
        }

        if (m_pushToTalkAction != null)
        {
            m_pushToTalkAction.action.started -= OnPushToTalkStarted;
            m_pushToTalkAction.action.canceled -= OnPushToTalkCanceled;
            m_pushToTalkAction.action.Disable();
        }

        m_posLoopCts?.Cancel();
    }

    private async UniTask EnsureLoggedInAsync()
    {
        if (m_loggedIn || m_starting) return;
        m_starting = true;

        try
        {
            if (m_session == null)
            {
                m_status = "SessionManager 미할당";
                Debug.LogError("[VivoxManager] SessionManager 참조가 없습니다.");
                return;
            }

            await m_session.EnsureSignedInAsync();

            if (!AuthenticationService.Instance.IsSignedIn)
            {
                m_status = "로그인 안 됨 - 세션 인증 필요";
                return;
            }

            m_status = "Vivox 초기화 중...";
            await VivoxService.Instance.InitializeAsync();

            m_status = "Vivox 로그인 중...";
            await VivoxService.Instance.LoginAsync(
                new LoginOptions { DisplayName = AuthenticationService.Instance.PlayerId });

            m_loggedIn = true;
            m_status = "Vivox 로그인 완료";
        }
        catch (Exception ex)
        {
            m_status = $"로그인 실패: {ex.Message}";
            Debug.LogError($"[VivoxManager] {ex}");
        }
        finally
        {
            m_starting = false;
        }
    }

    private async UniTask JoinChannelAsync(string sessionId)
    {
        await EnsureLoggedInAsync();
        if (!m_loggedIn) return;

        await LeaveChannelAsync();  // 재참가 대비

        string radio = BuildChannelName(m_channelPrefix, sessionId);
        m_proximityChannelName = BuildChannelName(m_proximityChannelPrefix, sessionId);

        try
        {
            // 거리 무관 무전 채널
            await VivoxService.Instance.JoinGroupChannelAsync(radio, ChatCapability.AudioOnly);
            m_radioJoined = true;

            // 3D positional 채널
            var props = new Channel3DProperties(m_audibleDistance, m_conversationalDistance, m_audioFadeIntensity, AudioFadeModel.InverseByDistance);
            await VivoxService.Instance.JoinPositionalChannelAsync(m_proximityChannelName, ChatCapability.AudioOnly, props);
            m_proximityJoined = true;
            StartPositionLoop();

            // 오픈마이크 장치 언뮤트 + 기본 송신은 근접 채널로만
            VivoxService.Instance.UnmuteInputDevice();
            await VivoxService.Instance.SetChannelTransmissionModeAsync(TransmissionMode.Single, m_proximityChannelName);

            m_status = "무전 + 근접 채널 참가 완료";
        }
        catch (Exception ex)
        {
            m_status = $"채널 참가 실패: {ex.Message}";
            Debug.LogError($"[VivoxManager] {ex}");
        }
    }

    private void StartPositionLoop()
    {
        m_posLoopCts?.Cancel();
        m_posLoopCts?.Dispose();
        m_posLoopCts = new CancellationTokenSource();
        PositionLoopAsync(m_posLoopCts.Token).Forget();
    }

    private async UniTaskVoid PositionLoopAsync(CancellationToken token)
    {
        Vector3 lastPos = Vector3.positiveInfinity;
        Quaternion lastRot = Quaternion.identity;
        NetworkObject local = null;

        while (!token.IsCancellationRequested)
        {
            if (local == null)
            {
                var nm = NetworkManager.Singleton;
                local = (nm != null && nm.IsClient) ? nm.LocalClient?.PlayerObject : null;
            }

            if (m_proximityJoined && local != null)
            {
                var t = local.transform;
                bool moved = (t.position - lastPos).sqrMagnitude > 0.0001f || Quaternion.Angle(t.rotation, lastRot) > 0.5f;

                if (moved)
                {
                    VivoxService.Instance.Set3DPosition(local.gameObject, m_proximityChannelName);
                    lastPos = t.position;
                    lastRot = t.rotation;
                }
            }

            await UniTask.Delay(TimeSpan.FromSeconds(m_positionUpdateInterval), cancellationToken: token);
        }
    }

    private async UniTask LeaveChannelAsync()
    {
        if (!m_radioJoined && !m_proximityJoined) return;
        try
        {
            await VivoxService.Instance.LeaveAllChannelsAsync();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[VivoxManager] 채널 나가기 실패: {ex}");
        }
        finally
        {
            m_radioJoined = false;
            m_proximityJoined = false;
            m_transmitting = false;
            m_posLoopCts?.Cancel();
        }
    }

    private string BuildChannelName(string prefix, string sessionId)
    {
        var sb = new StringBuilder(prefix);
        foreach (char c in sessionId)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        }
        return sb.ToString();
    }

    private void OnPushToTalkStarted(InputAction.CallbackContext ctx) => SetRadioTransmit(true);
    private void OnPushToTalkCanceled(InputAction.CallbackContext ctx) => SetRadioTransmit(false);

    private void SetRadioTransmit(bool on)
    {
        if (!m_radioJoined || !m_proximityJoined) return;
        m_transmitting = on;

        var mode = on ? TransmissionMode.All : TransmissionMode.Single;
        string ch = on ? null : m_proximityChannelName;
        VivoxService.Instance.SetChannelTransmissionModeAsync(mode, ch).AsUniTask().Forget();
    }

    private async UniTask LeaveAsync()
    {
        if (m_radioJoined || m_proximityJoined)
        {
            await VivoxService.Instance.LeaveAllChannelsAsync();
            m_radioJoined = false;
            m_proximityJoined = false;
        }
        if (m_loggedIn)
        {
            await VivoxService.Instance.LogoutAsync();
            m_loggedIn = false;
        }
    }

    private void HandleSessionJoined(string sessionId)
    {
        JoinChannelAsync(sessionId).Forget();
    }

    private void HandleSessionLeft()
    {
        LeaveChannelAsync().Forget();
    }

    private void HandleAuthSignedOut()
    {
        CleanupAsync().Forget();
    }

    private void OnDestroy()
    {
        CleanupAsync().Forget();
        m_posLoopCts?.Cancel();
    }

    private async UniTaskVoid CleanupAsync()
    {
        try { await LeaveAsync(); }
        catch (Exception ex) { Debug.LogError($"[VivoxManager] 정리 실패: {ex}"); }
    }

    [SerializeField] private float m_guiTopOffset = 10f;

    private void OnGUI()
    {
        if (m_session != null && m_session.Auth != null && m_session.Auth.IsNetworkConnected) return;

        GUILayout.BeginArea(new Rect(450, m_guiTopOffset, 320, 160));
        GUILayout.Label("Vivox 무전 — 상태");
        GUILayout.Label($"LoggedIn: {m_loggedIn}");
        GUILayout.Label($"Proximity Joined: {m_proximityJoined}");
        GUILayout.Label($"Radio Joined: {m_radioJoined}");
        GUILayout.Label($"Transmitting(PTT): {m_transmitting}");
        GUILayout.Label($"Push To Talk: {(m_pushToTalkAction != null ? m_pushToTalkAction.action.GetBindingDisplayString() : "(미할당)")}");
        GUILayout.Space(6);
        GUILayout.Label(m_status);
        GUILayout.EndArea();
    }
}
