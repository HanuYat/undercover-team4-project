using System;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using Cysharp.Threading.Tasks;
using Unity.Services.Vivox;
using Unity.Services.Authentication;

// GDD 4-91: 전역 오픈 음성 채널 1개(거리무관, non-positional) + PTT.
public class VivoxManager : MonoBehaviour
{
    [SerializeField] private string m_channelPrefix = "Radio";
    [SerializeField] private InputActionReference m_pushToTalkAction;
    [SerializeField] private SessionManager m_session;   // 인스펙터에서 연결

    private bool m_loggedIn;
    private bool m_joined;
    private bool m_transmitting;
    private bool m_starting;
    private string m_status = "대기 중...";

    private void OnEnable()
    {
        if (m_session != null)
        {
            m_session.OnSessionJoined += HandleSessionJoined;
            m_session.OnSessionLeft += HandleSessionLeft;
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
        }

        if (m_pushToTalkAction != null)
        {
            m_pushToTalkAction.action.started -= OnPushToTalkStarted;
            m_pushToTalkAction.action.canceled -= OnPushToTalkCanceled;
            m_pushToTalkAction.action.Disable();
        }
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

    private async UniTask JoinRadioAsync(string sessionId)
    {
        await EnsureLoggedInAsync();
        if (!m_loggedIn) return;

        await LeaveChannelAsync();
        await JoinChannelAsync(BuildChannelName(sessionId));
    }

    private async UniTask JoinChannelAsync(string channelName)
    {
        if (m_joined) return;
        try
        {
            m_status = "채널 참가 중...";
            await VivoxService.Instance.JoinGroupChannelAsync(channelName, ChatCapability.AudioOnly);
            m_joined = true;

            // PTT: 기본은 송신 차단(mute). 키 누를 때만 unmute.
            VivoxService.Instance.MuteInputDevice();
            m_transmitting = false;

            m_status = $"채널 참가 완료: {channelName}";
            Debug.Log($"[VivoxManager] 무전 채널 참가 완료 / channel: {channelName}");
        }
        catch (Exception e)
        {
            m_status = $"실패: {e.Message}";
            Debug.LogError($"[VivoxManager] {e}");
        }
    }

    private async UniTask LeaveChannelAsync()
    {
        if (!m_joined) return;
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
            m_joined = false;
            m_transmitting = false;
        }
    }

    private string BuildChannelName(string sessionId)
    {
        var sb = new StringBuilder(m_channelPrefix);
        foreach (char c in sessionId)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        }
        return sb.ToString();
    }

    private void OnPushToTalkStarted(InputAction.CallbackContext ctx) => SetTransmitting(true);
    private void OnPushToTalkCanceled(InputAction.CallbackContext ctx) => SetTransmitting(false);

    private void SetTransmitting(bool on)
    {
        if (!m_joined) return;  // 채널 참가 전에는 무시
        if (m_transmitting == on) return;
        m_transmitting = on;

        if (on) VivoxService.Instance.UnmuteInputDevice();
        else VivoxService.Instance.MuteInputDevice();
    }

    private async UniTask LeaveAsync()
    {
        if (m_joined)
        {
            await VivoxService.Instance.LeaveAllChannelsAsync();
            m_joined = false;
        }
        if (m_loggedIn)
        {
            await VivoxService.Instance.LogoutAsync();
            m_loggedIn = false;
        }
    }

    private void HandleSessionJoined(string sessionId)
    {
        JoinRadioAsync(sessionId).Forget();
    }

    private void HandleSessionLeft()
    {
        LeaveChannelAsync().Forget();
    }

    private void OnDestroy()
    {
        CleanupAsync().Forget();
    }

    private async UniTaskVoid CleanupAsync()
    {
        try { await LeaveAsync(); }
        catch (Exception ex) { Debug.LogError($"[VivoxManager] 정리 실패: {ex}"); }
    }

    private void OnGUI()
    {
        GUILayout.BeginArea(new Rect(400, 10, 320, 160));
        GUILayout.Label("Vivox 무전 — 상태");
        GUILayout.Label($"LoggedIn: {m_loggedIn}");
        GUILayout.Label($"Joined: {m_joined}");
        GUILayout.Label($"Transmitting(PTT): {m_transmitting}");
        GUILayout.Label($"Push To Talk: {(m_pushToTalkAction != null ? m_pushToTalkAction.action.GetBindingDisplayString() : "(미할당)")}");
        GUILayout.Space(6);
        GUILayout.Label(m_status);
        GUILayout.EndArea();
    }
}
