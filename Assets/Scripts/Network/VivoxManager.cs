using System;
using System.Text;
using UnityEngine;
using Cysharp.Threading.Tasks;
using Unity.Services.Vivox;
using Unity.Services.Authentication;
using System.Collections.Generic;

[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class VivoxManager : CommonManagerBase
{
    [SerializeField] private string m_channelPrefix = "Radio";
    [SerializeField] private SessionManager m_session;   // 인스펙터에서 연결
    [SerializeField] private VoiceDistortionController m_distortion;   // 먹통 음성 왜곡 부품 (#466)
    [SerializeField] private ProximityPositionReporter m_positionReporter;   // 근접 위치 보고 부품 (#466)
    [SerializeField] private VoiceInputRouter m_input;   // PTT·마이크 음소거 부품 (#466)
    private bool m_loggedIn;
    private bool m_starting;
    private string m_statusDetail = string.Empty;   // 상태에 담기지 않는 부가 설명(실패 사유 등) — 디버그 패널 전용

    [Header("근접 음성 (positional)")]
    [SerializeField] private string m_proximityChannelPrefix = "Proximity";
    [SerializeField] private int m_conversationalDistance = 3;
    [SerializeField] private int m_audibleDistance = 15;
    [SerializeField] private float m_audioFadeIntensity = 1.0f; // 감쇠 강도 (테스트 중 멀어져도 크게 들리면 강도 ↑)
    private bool m_radioJoined;
    private bool m_proximityJoined;
    private string m_proximityChannelName;

    private readonly Dictionary<string, bool> m_speakingByPlayer = new();
    private bool m_participantEventsHooked;
    public event Action<string, bool> OnSpeakingChanged;

    public bool IsSpeaking(string playerId) =>
        !string.IsNullOrEmpty(playerId)
        && m_speakingByPlayer.TryGetValue(playerId, out var speaking)
        && speaking;

    // ---- 음성 연결 상태 (#430) ----
    // 예전에는 상태가 m_status 문자열 하나뿐이어서 디버그 패널 밖에서 "연결됨/실패"를 알 수 없었다.
    // 값으로 올려 로비가 '음성 연결 중 / 실패'를 표시할 수 있게 한다 (실패 후 재시도는 범위 밖).

    /// <summary>
    /// 로컬 음성 연결 상태. 플레이어에게 보이는 문구는 `LobbyTable`의 <c>Lobby.Voice.&lt;상태&gt;</c>가 주인이고
    /// 표시 측(<see cref="LobbyRosterPanel"/>)이 이 값의 이름으로 키를 만들어 조회한다. (#497)
    /// <see cref="ToLabel"/>은 디버그 GUI 전용이다.
    /// </summary>
    public EVoiceState VoiceState { get; private set; } = EVoiceState.Idle;

    public event Action<EVoiceState> OnVoiceStateChanged;

    /// <summary>
    /// <b>디버그 GUI 전용</b> — 플레이어에게 보이는 음성 상태 문구는 `LobbyTable`의 <c>Lobby.Voice.&lt;상태&gt;</c>가
    /// 주인이다(문서 §2 결정 (h)의 규약 기반 매핑). 여기 한국어는 개발자 화면에만 나오므로 번역 대상이 아니다.
    /// 상태를 추가하면 이 switch가 아니라 <b>테이블에 키를 추가</b>해야 한다. (#497)
    /// </summary>
    public static string ToLabel(EVoiceState state) =>
        state switch
        {
            EVoiceState.LoggingIn => "음성 연결 중...",
            EVoiceState.Joining => "음성 채널 참가 중...",
            EVoiceState.Connected => "음성 연결됨",
            EVoiceState.Failed => "음성 연결 실패",
            _ => "음성 대기 중",
        };

    // detail은 상태가 그대로여도 갱신한다 — 같은 LoggingIn 안에서 초기화→로그인으로 진행이 바뀐다.
    private void SetVoiceState(EVoiceState state, string detail = "")
    {
        m_statusDetail = detail;
        if (VoiceState == state) return;

        VoiceState = state;
        OnVoiceStateChanged?.Invoke(state);
    }

    protected override void Awake()
    {
        base.Awake();   // App 등록 (R5)
        if (m_distortion == null)
            Debug.LogWarning("[VivoxManager] VoiceDistortionController 미할당 — 먹통 음성 왜곡이 걸리지 않는다", this);
        if (m_positionReporter == null)
            Debug.LogWarning("[VivoxManager] ProximityPositionReporter 미할당 — 근접 음성 거리 감쇠가 갱신되지 않는다", this);
        if (m_input == null)
            Debug.LogWarning("[VivoxManager] VoiceInputRouter 미할당 — 무전·마이크 음소거 키가 동작하지 않는다", this);
    }

    private void OnEnable()
    {
        if (m_session != null)
        {
            m_session.OnSessionJoined += HandleSessionJoined;
            m_session.OnSessionLeft += HandleSessionLeft;
            m_session.OnConnectionLost += HandleConnectionLost; // 비자발 드롭은 경량 정리 (#287)

            if (m_session.Auth != null)
                m_session.Auth.OnSignedOut += HandleAuthSignedOut;
        }
    }

    private void OnDisable()
    {
        if (m_session != null)
        {
            m_session.OnSessionJoined -= HandleSessionJoined;
            m_session.OnSessionLeft -= HandleSessionLeft;
            m_session.OnConnectionLost -= HandleConnectionLost; // #287

            if (m_session.Auth != null)
                m_session.Auth.OnSignedOut -= HandleAuthSignedOut;
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
                SetVoiceState(EVoiceState.Failed, "SessionManager 미할당");
                Debug.LogError("[VivoxManager] SessionManager 참조가 없습니다.");
                return;
            }

            await m_session.EnsureSignedInAsync();

            if (!AuthenticationService.Instance.IsSignedIn)
            {
                SetVoiceState(EVoiceState.Failed, "로그인 안 됨 - 세션 인증 필요");
                return;
            }

            SetVoiceState(EVoiceState.LoggingIn, "Vivox 초기화 중");
            await VivoxService.Instance.InitializeAsync();

            SetVoiceState(EVoiceState.LoggingIn, "Vivox 로그인 중");
            await VivoxService.Instance.LoginAsync(
                new LoginOptions { DisplayName = AuthenticationService.Instance.PlayerId });

            m_loggedIn = true;
            m_input?.NotifyLoggedIn();
            HookParticipantEvents();

            // 아직 채널에는 붙지 않았다 — Connected는 참가까지 끝난 뒤에만 세운다
            SetVoiceState(EVoiceState.LoggingIn, "Vivox 로그인 완료 — 채널 참가 전");
        }
        catch (Exception ex)
        {
            SetVoiceState(EVoiceState.Failed, $"로그인 실패: {ex.Message}");
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

        // EnsureLoggedInAsync(Vivox 초기화+로그인)를 기다리는 사이에 로그아웃/세션 이탈이 끝났을 수 있다
        // (#287 teardown 레이스). 그 상태로 채널에 참가하면 인증이 풀려 Vivox 토큰을 못 만들고
        // accessToken null 예외가 난다 — 아직 세션·인증이 살아있을 때만 참가한다.
        if (m_session == null || m_session.CurrentSession == null
            || !AuthenticationService.Instance.IsSignedIn)
        {
            SetVoiceState(EVoiceState.Idle, "세션·인증이 먼저 정리됨");
            return;
        }

        await LeaveChannelAsync();  // 재참가 대비

        string radio = BuildChannelName(m_channelPrefix, sessionId);
        m_proximityChannelName = BuildChannelName(m_proximityChannelPrefix, sessionId);

        try
        {
            SetVoiceState(EVoiceState.Joining);

            // 거리 무관 무전 채널
            await VivoxService.Instance.JoinGroupChannelAsync(radio, ChatCapability.AudioOnly);
            m_radioJoined = true;

            // 3D positional 채널
            var props = new Channel3DProperties(m_audibleDistance, m_conversationalDistance, m_audioFadeIntensity, AudioFadeModel.InverseByDistance);
            await VivoxService.Instance.JoinPositionalChannelAsync(m_proximityChannelName, ChatCapability.AudioOnly, props);
            m_proximityJoined = true;
            m_distortion?.NotifyChannelsJoined(m_proximityChannelName);
            m_positionReporter?.StartReporting(m_proximityChannelName);
            m_input?.NotifyChannelsJoined(m_proximityChannelName);

            // 오픈마이크 장치는 설정값대로 — 무조건 언뮤트하면 마이크를 꺼둔 사람이 채널에 붙는 순간 풀린다 (#430)
            ApplyMicMute();
            await VivoxService.Instance.SetChannelTransmissionModeAsync(TransmissionMode.Single, m_proximityChannelName);

            // 로그인 전에는 출력 장치 볼륨을 걸 수 없으므로, 참가 시점에 설정값을 당겨 온다 (#225)
            ApplyVoiceVolume();

            SetVoiceState(EVoiceState.Connected);
        }
        catch (Exception ex)
        {
            SetVoiceState(EVoiceState.Failed, $"채널 참가 실패: {ex.Message}");
            Debug.LogError($"[VivoxManager] {ex}");
            await LeaveChannelAsync();
        }
    }

    private void HookParticipantEvents()
    {
        if (m_participantEventsHooked) return;
        VivoxService.Instance.ParticipantAddedToChannel += OnParticipantAdded;
        VivoxService.Instance.ParticipantRemovedFromChannel += OnParticipantRemoved;
        m_participantEventsHooked = true;
    }

    private void UnhookParticipantEvents()
    {
        if (!m_participantEventsHooked) return;
        VivoxService.Instance.ParticipantAddedToChannel -= OnParticipantAdded;
        VivoxService.Instance.ParticipantRemovedFromChannel -= OnParticipantRemoved;
        m_participantEventsHooked = false;
    }

    private void OnParticipantAdded(VivoxParticipant participant)
    {
        // 참가자 인스턴스가 발화 상태 변화를 알림.
        participant.ParticipantSpeechDetected += () => RefreshSpeaking(participant.PlayerId);
        RefreshSpeaking(participant.PlayerId);

        m_distortion?.HandleParticipantAdded(participant);
    }

    private void OnParticipantRemoved(VivoxParticipant participant)
    {
        RefreshSpeaking(participant.PlayerId);

        m_distortion?.HandleParticipantRemoved(participant);
    }

    private void RefreshSpeaking(string playerId)
    {
        if (string.IsNullOrEmpty(playerId)) return;

        bool speaking = false;
        foreach (var channel in VivoxService.Instance.ActiveChannels.Values)
        {
            foreach (var p in channel)
            {
                if (p.PlayerId == playerId && p.SpeechDetected) { speaking = true; break; }
            }
            if (speaking) break;
        }

        bool prev = m_speakingByPlayer.TryGetValue(playerId, out var v) && v;
        if (prev == speaking) return;

        m_speakingByPlayer[playerId] = speaking;
        OnSpeakingChanged?.Invoke(playerId, speaking);
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
            // 여기서 연결 상태를 Idle로 내리지 않는다 — 이 메서드는 재참가 직전과 참가 실패 직후에도
            // 불려서, 내리면 방금 세운 Joining·Failed를 지운다. 음성이 끝났다는 판정은 부르는 쪽
            // (HandleSessionLeft · LogoutAsync)이 한다. (#430)
            m_radioJoined = false;
            m_proximityJoined = false;
            m_positionReporter?.StopReporting();
            m_input?.NotifyChannelsLeft();
            m_distortion?.NotifyChannelsLeft();
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

    // ---- 음성 입력 (#430) ----
    // PTT·마이크 음소거는 VoiceInputRouter 부품이 한다 (#466) — 여기서는 외부 진입점만 유지한다.

    /// <summary>무전 키 표시 문자열 — 로비 안내와 디버그 패널이 함께 쓴다.</summary>
    public string PushToTalkBinding => m_input != null ? m_input.PushToTalkBinding : "(미할당)";

    /// <summary>음소거 토글 키 표시 문자열 — 안내·디버그 패널용.</summary>
    public string MicMuteBinding => m_input != null ? m_input.MicMuteBinding : "(미할당)";

    /// <summary>음소거 중에 무전 키를 눌렀다 — HUD가 "마이크가 꺼져 있습니다"를 띄운다.</summary>
    public event Action OnMutedTalkAttempt
    {
        add { if (m_input != null) m_input.OnMutedTalkAttempt += value; }
        remove { if (m_input != null) m_input.OnMutedTalkAttempt -= value; }
    }

    /// <summary>설정의 음소거 값을 입력 장치에 적용한다 (#430) — GameSettings·채널 참가 두 곳이 부른다.</summary>
    public void ApplyMicMute() => m_input?.ApplyMicMute();

    // ---- 음성 음량 (#225) ----
    // Vivox 출력 볼륨은 -50~50 정수 로그 스케일이고 0이 '변화 없음'이다.
    // 문서 기준 실사용 구간이 -10~25라 하한을 -50까지 열면 슬라이더 아래 80%가 무음 구간이 된다.
    // '완전 무음'은 곡선의 끝이 아니라 별개 상태로 취급해 슬라이더 0에서만 -50으로 떨어뜨린다.
    private const int k_vivoxVolumeMute = -50;
    private const int k_vivoxVolumeFloor = -20;
    private const int k_vivoxVolumeCeil = 0;

    /// <summary>
    /// 설정의 음성 음량을 실제 재생 경로에 적용한다. (#225)
    /// ① 평소 — Vivox 자체 믹스로 재생되므로 출력 장치 볼륨으로 조절한다.
    /// ② 먹통 왜곡 중(#372) — Vivox 믹스를 죽이고 우리 AudioSource로 재생하므로 ①이 통하지 않는다.
    ///    새 AudioSource의 기본 volume은 1이라, 여기서 걸지 않으면 음성을 0으로 내려둔 사람도
    ///    먹통이 터지는 순간 목소리가 원래 크기로 되살아난다 (음소거가 저절로 풀리는 셈).
    ///
    /// 값을 필드로 복사하지 않고 매번 GameSettings에서 읽는다 — 부르는 지점이 셋(설정 변경·채널
    /// 참가·탭 생성)이라 복사본을 두면 어긋날 여지가 생긴다.
    /// </summary>
    public void ApplyVoiceVolume()
    {
        float volume = GameSettings.VoiceVolume;
        if (m_loggedIn)
            VivoxService.Instance.SetOutputDeviceVolume(ToVivoxVolume(volume));

        m_distortion?.ApplyVolume();
    }

    // 0~1 → Vivox 정수 스케일. 0은 확실한 무음으로 떨어뜨리고, 그 위는 실사용 구간으로 보간한다.
    private static int ToVivoxVolume(float volume01)
    {
        if (volume01 <= 0f)
            return k_vivoxVolumeMute;

        return Mathf.RoundToInt(Mathf.Lerp(k_vivoxVolumeFloor, k_vivoxVolumeCeil, volume01));
    }

    // ---- 먹통 음성 왜곡 (#372) ----
    // 실제 처리는 VoiceDistortionController 부품이 한다 (#466) — 여기서는 외부 진입점만 유지한다.

    /// <summary>먹통 음성 왜곡을 켜고 끈다 — <see cref="DeviceBlackoutView"/>가 먹통 플래그에 맞춰 호출한다. (#372)</summary>
    public void SetVoiceDistorted(bool distorted) => m_distortion?.SetDistorted(distorted);

    // 먹통 중 무전을 '차단'하던 SetCommsJammed는 제거했다 (#372). 먹통 연출이 차단에서 왜곡으로
    // 바뀌면서 호출부가 사라졌고, 통신을 끊는 경로가 둘로 남으면 다음 사람이 어느 쪽이 살아있는지
    // 알 수 없다. 차단형으로 되돌릴 일이 생기면 이 커밋의 diff에서 복원하면 된다.

    public async UniTask LogoutAsync()
    {
        UnhookParticipantEvents();
        m_speakingByPlayer.Clear();
        m_distortion?.NotifyVoiceEnded();
        m_input?.NotifyVoiceEnded();

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

        // 음성이 끝났다는 판정은 여기서 한다 — LeaveChannelAsync는 재참가 경로에도 끼어서 못 내린다 (#430)
        SetVoiceState(EVoiceState.Idle);
    }

    private void HandleSessionJoined(string sessionId)
    {
        JoinChannelAsync(sessionId).Forget();
    }

    private void HandleSessionLeft()
    {
        // 세션 이탈은 채널 이탈과 다른 사건이다 — 채널은 세션 도중에도 다시 붙지만(그래서
        // LeaveChannelAsync는 왜곡 플래그를 유지한다), 세션이 끝나면 먹통이라는 맥락 자체가 사라진다.
        // 여기서 리셋하지 않으면 다음 세션이 이유 없이 왜곡된 채 시작된다 (#372).
        // 지금은 새 씬의 DeviceBlackoutView가 초기 상태를 내려줘 우연히 풀리지만, 그 초기화에
        // 기대는 구조라 View 쪽이 바뀌면 조용히 깨진다.
        m_distortion?.NotifyVoiceEnded();
        SetVoiceState(EVoiceState.Idle);
        LeaveChannelAsync().Forget();
    }

    // 비자발 드롭(#287): 호스트가 세션을 내리면 클라의 NGO는 끊기지만 Vivox는 NGO/호스트와 별개 서비스라
    // (자체 서버 연결) 클라의 음성 연결은 그대로 살아있다. 채널에서 실제로 나가지 않으면 세션이 죽어도
    // 클라들끼리 계속 목소리가 들린다. 그래서 자발적 경로와 동일하게 완전 정리한다.
    // (클라 본인 인터넷이 끊긴 진짜 드롭이면 LeaveAllChannelsAsync가 타임아웃날 수 있으나 fire-and-forget이라 무해.)
    private void HandleConnectionLost()
    {
        m_positionReporter?.StopReporting();
        m_input?.NotifyChannelsLeft();
        CleanupAsync().Forget(); // 채널 이탈 + Vivox 로그아웃 (LogoutAsync는 멱등)
    }

    // 인증 로그아웃 → Vivox도 정리 (#171 auth→voice 전파). 세션만 나가고 로그인은 유지된 상태에서
    // 직접 SignOut한 경로의 안전망이다. SessionFlow 경로에선 이미 LogoutAsync가 끝난 뒤라
    // no-op(LogoutAsync는 멱등).
    private void HandleAuthSignedOut()
    {
        CleanupAsync().Forget();
    }

    protected override void OnDestroy()
    {
        base.OnDestroy(); // App 등록 해제

        CleanupAsync().Forget();
    }

    private async UniTaskVoid CleanupAsync()
    {
        try { await LogoutAsync(); }
        catch (TimeoutException)
        {
            // 종료 시점엔 Vivox 메시지 펌프가 먼저 내려가 이탈 응답을 못 받는다 —
            // 요청은 서버에 갔고 세션은 서버가 정리하므로 실패가 아니다.
            Debug.LogWarning("[VivoxManager] 종료 중 채널 이탈 응답 없음 (무해)");
        }
        catch (Exception ex) { Debug.LogError($"[VivoxManager] 정리 실패: {ex}"); }
    }

    [Tooltip("OnGUI 디버그 패널 표시 — 테스트 씬 수동 조작용 (#247)")]
    [SerializeField] private bool m_showDebugGui;

    [SerializeField] private float m_guiTopOffset = 10f;

    private void OnGUI()
    {
        if (!m_showDebugGui) return;
        if (m_session != null && m_session.Auth != null && m_session.Auth.IsNetworkConnected) return;

        GUILayout.BeginArea(new Rect(450, m_guiTopOffset, 320, 240));
        GUILayout.Label("Vivox 무전 — 상태");
        GUILayout.Label($"LoggedIn: {m_loggedIn}");
        GUILayout.Label($"Proximity Joined: {m_proximityJoined}");
        GUILayout.Label($"Radio Joined: {m_radioJoined}");
        GUILayout.Label($"Transmitting(PTT): {m_input != null && m_input.IsTransmitting}");
        GUILayout.Label($"Mic Muted: {GameSettings.MicMuted}");
        GUILayout.Label($"Push To Talk: {PushToTalkBinding}");
        GUILayout.Label($"Mic Mute Toggle: {MicMuteBinding}");
        GUILayout.Space(6);
        GUILayout.Label($"{ToLabel(VoiceState)} ({VoiceState})");
        if (!string.IsNullOrEmpty(m_statusDetail)) GUILayout.Label(m_statusDetail);
        if (m_distortion != null && m_distortion.IsDistorted) GUILayout.Label("음성 왜곡(먹통) 중");
        GUILayout.EndArea();
    }
}
