using Cysharp.Threading.Tasks;
using System;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Vivox;
using UnityEngine;
using UnityEngine.InputSystem;

// GDD 4-91: 전역 오픈 음성 채널 1개(거리무관, non-positional) + PTT. 최소 핵심.
public class VivoxManager : MonoBehaviour
{
    [SerializeField] private string m_channelName = "GlobalRadio";
    [SerializeField] private Key m_pushToTalkKey = Key.V;
    [SerializeField] private AuthBootstrap m_auth;   // 인스펙터에서 연결

    private bool m_loggedIn;
    private bool m_joined;
    private bool m_transmitting;
    private bool m_starting;
    private string m_status = "대기 중...";

    private void OnEnable()
    {
        if (m_auth != null) m_auth.OnSignedIn += HandleSignedIn;
    }

    private void OnDisable()
    {
        if (m_auth != null) m_auth.OnSignedIn -= HandleSignedIn;
    }


    private void Start()
    {
        if (m_auth != null && m_auth.IsSignedIn) HandleSignedIn();
    }

    // init → login → 전역 채널 join. 인증(AuthBootstrap)이 먼저 끝나 있어야 함.
    public async UniTask StartRadioAsync()
    {
        if (m_loggedIn || m_starting) return;
        m_starting = true;

        try
        {
            if (UnityServices.State != ServicesInitializationState.Initialized)
            {
                await UnityServices.InitializeAsync();
            }
            if (!AuthenticationService.Instance.IsSignedIn)
            {
                m_status = "로그인 안 됨 — AuthBootstrap 먼저 로그인 필요";
                return;
            }

            m_status = "Vivox 초기화 중...";
            await VivoxService.Instance.InitializeAsync();

            m_status = "Vivox 로그인 중...";
            await VivoxService.Instance.LoginAsync(
                new LoginOptions { DisplayName = AuthenticationService.Instance.PlayerId });
            m_loggedIn = true;

            m_status = "전역 채널 참가 중...";
            await VivoxService.Instance.JoinGroupChannelAsync(m_channelName, ChatCapability.AudioOnly);
            m_joined = true;

            // PTT: 기본은 송신 차단(mute). 키 누를 때만 unmute.
            VivoxService.Instance.MuteInputDevice();
            m_transmitting = false;

            m_status = $"채널 참가 완료: {m_channelName}";
            Debug.Log($"[VivoxManager] 무전 채널 참가 완료 / channel: {m_channelName}");
        }
        catch (Exception e)
        {
            m_status = $"실패: {e.Message}";
            Debug.LogError($"[VivoxManager] {e}");
        }
        finally
        {
            m_starting = false;
        }
    }

    private void Update()
    {
        if (!m_joined) return;

        var control = Keyboard.current?[m_pushToTalkKey];
        if (control == null) return;

        if (control.wasPressedThisFrame) SetTransmitting(true);
        if (control.wasReleasedThisFrame) SetTransmitting(false);
    }

    private void SetTransmitting(bool on)
    {
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

    private void HandleSignedIn()
    {
        StartRadioAsync().Forget();
    }

    private void OnDestroy()
    {
        // 세션 나갈 때 정리. 완료 보장은 안 되지만 draft 단계에선 충분.
        LeaveAsync().Forget();
    }

    private void OnGUI()
    {
        GUILayout.BeginArea(new Rect(400, 10, 320, 160));
        GUILayout.Label("Vivox 무전 — 상태");
        GUILayout.Label($"LoggedIn: {m_loggedIn}");
        GUILayout.Label($"Joined: {m_joined}");
        GUILayout.Label($"Transmitting(PTT): {m_transmitting}");
        GUILayout.Label($"PTT 키: {m_pushToTalkKey}");
        GUILayout.Space(6);
        GUILayout.Label(m_status);
        GUILayout.EndArea();
    }
}
