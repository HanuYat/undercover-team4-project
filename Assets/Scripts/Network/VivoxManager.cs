using System;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.InputSystem;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using Unity.Services.Vivox;
using Unity.Services.Authentication;
using System.Collections.Generic;

[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class VivoxManager : CommonManagerBase
{
    [SerializeField] private string m_channelPrefix = "Radio";
    [SerializeField] private InputActionReference m_pushToTalkAction;
    [SerializeField] private SessionManager m_session;   // 인스펙터에서 연결
    private bool m_loggedIn;
    private bool m_transmitting;   // PTT를 누르고 있는지 — 디버그 표시용
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

    private readonly Dictionary<string, bool> m_speakingByPlayer = new();
    private bool m_participantEventsHooked;
    public event Action<string, bool> OnSpeakingChanged;

    public bool IsSpeaking(string playerId) =>
        !string.IsNullOrEmpty(playerId)
        && m_speakingByPlayer.TryGetValue(playerId, out var speaking)
        && speaking;

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

        if (m_pushToTalkAction != null)
        {
            m_pushToTalkAction.action.started += OnPushToTalkStarted;
            m_pushToTalkAction.action.canceled += OnPushToTalkCanceled;
            m_pushToTalkAction.action.Enable();
        }

        if (m_proximityJoined) StartPositionLoop();
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
            HookParticipantEvents();

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

        // EnsureLoggedInAsync(Vivox 초기화+로그인)를 기다리는 사이에 로그아웃/세션 이탈이 끝났을 수 있다
        // (#287 teardown 레이스). 그 상태로 채널에 참가하면 인증이 풀려 Vivox 토큰을 못 만들고
        // accessToken null 예외가 난다 — 아직 세션·인증이 살아있을 때만 참가한다.
        if (m_session == null || m_session.CurrentSession == null
            || !AuthenticationService.Instance.IsSignedIn)
            return;

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

            // 로그인 전에는 출력 장치 볼륨을 걸 수 없으므로, 참가 시점에 설정값을 당겨 온다 (#225)
            ApplyVoiceVolume();

            m_status = "무전 + 근접 채널 참가 완료";
        }
        catch (Exception ex)
        {
            m_status = $"채널 참가 실패: {ex.Message}";
            Debug.LogError($"[VivoxManager] {ex}");
            await LeaveChannelAsync();
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

        // 먹통 진행 중에 들어온 참가자도 왜곡을 받아야 한다 — 안 하면 그 사람 목소리만 멀쩡하다 (#372)
        if (m_voiceDistorted && ShouldDistortChannel(participant.ChannelName))
            ApplyDistortion(participant);
    }

    private void OnParticipantRemoved(VivoxParticipant participant)
    {
        RefreshSpeaking(participant.PlayerId);

        // 나간 참가자의 탭 기록을 지운다 — 탭 오브젝트는 Vivox가 참가자와 함께 정리한다 (#372)
        m_distortTaps.Remove(participant);
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
            m_radioJoined = false;
            m_proximityJoined = false;
            m_transmitting = false;
            m_posLoopCts?.Cancel();

            // 채널을 떠나면 참가자와 탭이 함께 사라진다 — 기록만 비운다 (#372).
            // m_voiceDistorted는 유지: 먹통 중 재접속하면 OnParticipantAdded가 다시 왜곡을 건다.
            m_distortTaps.Clear();
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

        ApplyRadioTransmission(on);
    }

    // 무전 채널 송신을 켜고 끈다 — 끄면 근접 채널로만 송신한다(참가 시 기본값과 동일).
    private void ApplyRadioTransmission(bool on)
    {
        var mode = on ? TransmissionMode.All : TransmissionMode.Single;
        string ch = on ? null : m_proximityChannelName;
        VivoxService.Instance.SetChannelTransmissionModeAsync(mode, ch).AsUniTask().Forget();
    }

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

        foreach (AudioSource source in m_distortTaps.Values)
        {
            if (source != null)
                source.volume = volume;
        }
    }

    // 0~1 → Vivox 정수 스케일. 0은 확실한 무음으로 떨어뜨리고, 그 위는 실사용 구간으로 보간한다.
    private static int ToVivoxVolume(float volume01)
    {
        if (volume01 <= 0f)
            return k_vivoxVolumeMute;

        return Mathf.RoundToInt(Mathf.Lerp(k_vivoxVolumeFloor, k_vivoxVolumeCeil, volume01));
    }

    // ---- 먹통 음성 왜곡 (#372) ----
    //
    // 먹통 중 음성을 '끊는' 대신 '망가뜨린다'. 완전 침묵은 협동 게임에서 답답하고 버그로 오인되는데,
    // 왜곡은 이벤트가 터졌다는 게 즉시 전달되면서 알아듣기 어려워 통신 제한 목적도 달성한다.
    // 신호 해석기(#108)의 텍스트 경로는 여전히 또렷하므로 "먹통 시 정확한 통신 수단"이라는 역할도 남는다.
    //
    // 구현: Vivox 오디오 탭으로 참가자 음성을 Unity AudioSource로 끌어와 필터를 건다.
    // silenceInChannelAudioMix=true로 Vivox 자체 믹스에서는 죽여야 소리가 두 번 나지 않는다.
    // 전부 로컬 재생 처리라 네트워크 동기화가 없다 — 각 피어가 자기가 듣는 소리만 망가뜨린다.
    //
    // 어떤 필터를 어떤 값으로 얹을지는 VoiceDistortionProfile(SO)이 소유한다 — 이 매니저는
    // '언제 왜곡할지'만 안다. 튜닝 값이 여기 늘어나면 프리셋 교체가 불가능해진다 (#372 리뷰).

    [Header("먹통 음성 왜곡 (#372)")]
    [Tooltip("왜곡 음색 프로파일(SO) — 필터 조합·수치는 전부 이 에셋이 정한다. 비면 왜곡을 걸지 않는다")]
    [SerializeField] private VoiceDistortionProfile m_distortProfile;

    [Tooltip(
        "근접 채널 음성도 왜곡할지. 끄면 무전 채널만 왜곡한다 — 근접은 Vivox가 자체 3D 감쇠를 처리하는데, "
        + "탭으로 빼내면 그 감쇠가 유지되는지 확인이 필요하다(멀리 있는 사람이 크게 들리면 이 옵션을 끌 것). "
        + "음색이 아니라 '어느 채널에 거는가'라는 Vivox 배선이라 프로파일이 아니라 여기 남는다")]
    [SerializeField] private bool m_distortProximityToo = true;

    private bool m_voiceDistorted;
    private float m_nextGlitchTime;

    // 탭을 건 참가자 → 그 참가자의 재생 AudioSource. 해제 시 전부 되돌린다.
    private readonly Dictionary<VivoxParticipant, AudioSource> m_distortTaps = new();

    /// <summary>
    /// 먹통 음성 왜곡을 켜고 끈다 — <see cref="DeviceBlackoutView"/>가 먹통 플래그에 맞춰 호출한다. (#372)
    /// 자기 목소리(IsSelf)는 어차피 자기에게 재생되지 않으므로 건너뛴다.
    /// </summary>
    public void SetVoiceDistorted(bool distorted)
    {
        if (m_voiceDistorted == distorted) return;

        // 프로파일이 없으면 왜곡 자체를 시작하지 않는다 — 탭만 걸고 필터를 못 얹으면
        // Vivox 믹스에서 죽인 목소리를 대신 재생해 줄 설정이 없어 그 사람이 통째로 무음이 된다.
        if (distorted && m_distortProfile == null)
        {
            Debug.LogWarning("[VivoxManager] VoiceDistortionProfile 미할당 — 먹통 음성 왜곡을 건너뛴다", this);
            return;
        }

        m_voiceDistorted = distorted;

        if (distorted)
            ApplyDistortionToAll();
        else
            ClearAllDistortion();

        m_status = distorted ? "음성 왜곡(먹통)" : "음성 정상";
    }

    private void ApplyDistortionToAll()
    {
        if (!m_loggedIn) return;

        foreach (var channel in VivoxService.Instance.ActiveChannels)
        {
            if (!ShouldDistortChannel(channel.Key)) continue;
            foreach (VivoxParticipant participant in channel.Value)
                ApplyDistortion(participant);
        }
    }

    // 근접 채널 왜곡 여부는 인스펙터 토글 — 3D 감쇠 확인 전까지 끌 수 있어야 한다
    private bool ShouldDistortChannel(string channelName)
        => m_distortProximityToo || channelName != m_proximityChannelName;

    private void ApplyDistortion(VivoxParticipant participant)
    {
        if (participant == null || participant.IsSelf) return;
        if (m_distortProfile == null) return; // SetVoiceDistorted가 이미 막지만 진입점이 둘이라 여기서도 확인
        if (m_distortTaps.ContainsKey(participant)) return; // 중복 탭 방지

        // 탭이 만들어졌는지 추적한다 — 아래 어느 경로로 빠져나가든 되돌리기 위함 (실패 시 영구 무음 방지)
        bool tapCreated = false;

        try
        {
            // silenceInChannelAudioMix=true — Vivox 믹스에서는 죽이고 우리 AudioSource로만 재생한다.
            // 이 호출이 성공한 순간부터 그 참가자는 Vivox 믹스에서 들리지 않는다. 따라서 이후
            // 어느 경로로 실패하든 탭을 반드시 되돌려야 한다 — 탭만 걸리고 우리도 재생하지 않으면
            // 그 사람 목소리가 세션 내내 완전히 사라지고, m_distortTaps에 없으니 해제 때도 못 살린다.
            GameObject tapObject = participant.CreateVivoxParticipantTap(
                $"BlackoutVoiceTap_{participant.PlayerId}", true);
            tapCreated = true;

            AudioSource source = participant.ParticipantTapAudioSource;
            if (tapObject == null || source == null)
            {
                Debug.LogWarning($"[VivoxManager] 오디오 탭 생성 실패 — {participant.PlayerId}");
                SafeDestroyTap(participant); // 음소거만 남기고 나가지 않는다
                return;
            }

            // 어떤 필터를 어떤 순서·값으로 얹을지는 프로파일이 안다 (#372 리뷰)
            m_distortProfile.Apply(tapObject, source);

            // 새 AudioSource의 기본 volume은 1 — 설정값을 걸지 않으면 음소거가 풀린다 (#225)
            source.volume = GameSettings.VoiceVolume;

            m_distortTaps[participant] = source;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[VivoxManager] 음성 왜곡 적용 실패 ({participant.PlayerId}): {ex}");

            // 필터를 얹다 실패했어도 탭은 이미 걸려 있을 수 있다 — 등록에 성공하지 못했다면 되돌린다.
            // (등록됐다면 정상 경로이므로 해제는 ClearAllDistortion이 맡는다)
            if (tapCreated && !m_distortTaps.ContainsKey(participant))
                SafeDestroyTap(participant);
        }
    }

    // 탭 해제 — 어느 경로에서 부르든 여기서 실패가 나머지 정리를 막지 않게 한다.
    // 탭 GameObject가 통째로 파괴되므로 얹은 필터도 함께 사라진다.
    private void SafeDestroyTap(VivoxParticipant participant)
    {
        if (participant == null) return;

        try
        {
            participant.DestroyVivoxParticipantTap();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[VivoxManager] 탭 해제 실패 ({participant.PlayerId}): {ex}");
        }
    }

    private void ClearAllDistortion()
    {
        // 키 복사본을 순회한다 — SafeDestroyTap이 Vivox 참가자 콜백을 동기로 깨우면
        // OnParticipantRemoved가 m_distortTaps를 건드려 순회 중 수정 예외가 난다 (#372)
        foreach (VivoxParticipant participant in new List<VivoxParticipant>(m_distortTaps.Keys))
            SafeDestroyTap(participant);

        m_distortTaps.Clear();
    }

    // 피치를 주기적으로 튀게 해 "신호가 튄다"는 인상을 준다 — 왜곡 중에만 돈다.
    // 튀는 간격·폭은 프로파일이 정한다 (m_voiceDistorted가 켜졌다면 프로파일은 반드시 있다).
    private void Update()
    {
        if (!m_voiceDistorted || m_distortTaps.Count == 0) return;
        if (Time.time < m_nextGlitchTime) return;

        m_nextGlitchTime = Time.time + m_distortProfile.NextGlitchInterval();
        float pitch = m_distortProfile.NextGlitchPitch();

        foreach (AudioSource source in m_distortTaps.Values)
        {
            if (source != null)
                source.pitch = pitch;
        }
    }

    // 먹통 중 무전을 '차단'하던 SetCommsJammed는 제거했다 (#372). 먹통 연출이 차단에서 왜곡으로
    // 바뀌면서 호출부가 사라졌고, 통신을 끊는 경로가 둘로 남으면 다음 사람이 어느 쪽이 살아있는지
    // 알 수 없다. 차단형으로 되돌릴 일이 생기면 이 커밋의 diff에서 복원하면 된다.

    public async UniTask LogoutAsync()
    {
        UnhookParticipantEvents();
        m_speakingByPlayer.Clear();
        m_distortTaps.Clear(); // 탭은 채널 이탈과 함께 정리된다 — 기록만 비운다 (#372)
        m_voiceDistorted = false;

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
        // 세션 이탈은 채널 이탈과 다른 사건이다 — 채널은 세션 도중에도 다시 붙지만(그래서
        // LeaveChannelAsync는 왜곡 플래그를 유지한다), 세션이 끝나면 먹통이라는 맥락 자체가 사라진다.
        // 여기서 리셋하지 않으면 다음 세션이 이유 없이 왜곡된 채 시작된다 (#372).
        // 지금은 새 씬의 DeviceBlackoutView가 초기 상태를 내려줘 우연히 풀리지만, 그 초기화에
        // 기대는 구조라 View 쪽이 바뀌면 조용히 깨진다.
        m_voiceDistorted = false;
        LeaveChannelAsync().Forget();
    }

    // 비자발 드롭(#287): 호스트가 세션을 내리면 클라의 NGO는 끊기지만 Vivox는 NGO/호스트와 별개 서비스라
    // (자체 서버 연결) 클라의 음성 연결은 그대로 살아있다. 채널에서 실제로 나가지 않으면 세션이 죽어도
    // 클라들끼리 계속 목소리가 들린다. 그래서 자발적 경로와 동일하게 완전 정리한다.
    // (클라 본인 인터넷이 끊긴 진짜 드롭이면 LeaveAllChannelsAsync가 타임아웃날 수 있으나 fire-and-forget이라 무해.)
    private void HandleConnectionLost()
    {
        m_posLoopCts?.Cancel();
        m_transmitting = false;
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
        m_posLoopCts?.Cancel();
    }

    private async UniTaskVoid CleanupAsync()
    {
        try { await LogoutAsync(); }
        catch (Exception ex) { Debug.LogError($"[VivoxManager] 정리 실패: {ex}"); }
    }

    [Tooltip("OnGUI 디버그 패널 표시 — 테스트 씬 수동 조작용 (#247)")]
    [SerializeField] private bool m_showDebugGui;

    [SerializeField] private float m_guiTopOffset = 10f;

    private void OnGUI()
    {
        if (!m_showDebugGui) return;
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
