using System;
using System.Text;
using System.Threading;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
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
    [SerializeField] private InputActionReference m_micMuteToggleAction;   // 마이크 음소거 토글 (#430)
    [SerializeField] private SessionManager m_session;   // 인스펙터에서 연결
    private bool m_loggedIn;
    private bool m_transmitting;   // PTT를 누르고 있는지 — 디버그 표시용
    private bool m_starting;
    private string m_statusDetail = string.Empty;   // 상태에 담기지 않는 부가 설명(실패 사유 등) — 디버그 패널 전용

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

    // ---- 음성 연결 상태 (#430) ----
    // 예전에는 상태가 m_status 문자열 하나뿐이어서 디버그 패널 밖에서 "연결됨/실패"를 알 수 없었다.
    // 값으로 올려 로비가 '음성 연결 중 / 실패'를 표시할 수 있게 한다 (실패 후 재시도는 범위 밖).

    /// <summary>로컬 음성 연결 상태 — UI는 <see cref="ToLabel"/>로 문자열로 바꿔 표시한다.</summary>
    public EVoiceState VoiceState { get; private set; } = EVoiceState.Idle;

    public event Action<EVoiceState> OnVoiceStateChanged;

    /// <summary>표시 문자열은 상태에서 파생한다 — 같은 문구를 UI마다 따로 쓰지 않게 한 곳에 둔다.</summary>
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

        // 누를 때 한 번만 뒤집는다 — PTT와 달리 뗄 때는 아무 일도 없어야 하므로 performed만 본다 (#430)
        if (m_micMuteToggleAction != null)
        {
            m_micMuteToggleAction.action.performed += OnMicMuteToggled;
            m_micMuteToggleAction.action.Enable();
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

        if (m_micMuteToggleAction != null)
        {
            m_micMuteToggleAction.action.performed -= OnMicMuteToggled;
            m_micMuteToggleAction.action.Disable();
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
            StartPositionLoop();

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
            // 여기서 연결 상태를 Idle로 내리지 않는다 — 이 메서드는 재참가 직전과 참가 실패 직후에도
            // 불려서, 내리면 방금 세운 Joining·Failed를 지운다. 음성이 끝났다는 판정은 부르는 쪽
            // (HandleSessionLeft · LogoutAsync)이 한다. (#430)
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

    /// <summary>무전 키 표시 문자열 — 로비 안내와 디버그 패널이 함께 쓴다.</summary>
    public string PushToTalkBinding =>
        m_pushToTalkAction != null ? m_pushToTalkAction.action.GetBindingDisplayString() : "(미할당)";

    // 텍스트 입력 중에는 음성 단축키를 무시한다 — 닉네임·세션 코드를 치다가 v·m이 섞이면 무전이
    // 나가거나 마이크가 꺼진다. Input System 액션은 UI 포커스와 무관하게 항상 살아 있어서
    // 여기서 직접 확인해야 한다. 프로젝트의 입력 필드는 전부 TMP_InputField다. (#430)
    //
    // 액션을 Disable/Enable로 껐다 켜지 않는 이유: 키를 누른 채 포커스가 바뀌면 canceled를 놓쳐
    // 송신이 켜진 채로 남는다. 콜백에서 걸러내는 편이 상태가 어긋날 여지가 없다.
    private static bool IsTypingInUI()
    {
        EventSystem events = EventSystem.current;
        GameObject selected = events != null ? events.currentSelectedGameObject : null;

        return selected != null
            && selected.TryGetComponent(out TMP_InputField input)
            && input.isFocused;
    }

    private void OnPushToTalkStarted(InputAction.CallbackContext ctx)
    {
        if (IsTypingInUI()) return;

        // 음소거가 이긴다 — 송신을 막는 가드는 넣지 않는다(입력 장치가 뮤트면 송신 모드와 무관하게
        // 소리가 나가지 않아 두 경로가 자연히 독립이다). 대신 눌렀다는 사실만 알린다 — 이 안내가
        // 없으면 음소거를 잊고 말하는 상황이 그대로 남는다. (#430)
        if (GameSettings.MicMuted) OnMutedTalkAttempt?.Invoke();

        SetRadioTransmit(true);
    }

    // 뗄 때는 타이핑 여부를 보지 않는다 — 누른 뒤 입력창을 클릭하고 떼는 순서면 송신이 켜진 채
    // 남는다. 켜져 있을 때만 끄면 되므로 m_transmitting으로 판단한다. (#430)
    private void OnPushToTalkCanceled(InputAction.CallbackContext ctx)
    {
        if (m_transmitting) SetRadioTransmit(false);
    }

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

    // ---- 마이크 음소거 (#430) ----
    // 상태는 GameSettings.MicMuted 하나가 소유한다 — 여기에 복사해 두지 않는다(설정 창·토글 키
    // 두 경로로 바뀌므로 복사본은 반드시 어긋난다). 적용은 입력 장치 뮤트 — 송신 모드는 PTT의 것이다.

    /// <summary>음소거 중에 무전 키를 눌렀다 — HUD가 "마이크가 꺼져 있습니다"를 띄운다.</summary>
    public event Action OnMutedTalkAttempt;

    /// <summary>음소거 토글 키 표시 문자열 — 안내·디버그 패널용.</summary>
    public string MicMuteBinding =>
        m_micMuteToggleAction != null ? m_micMuteToggleAction.action.GetBindingDisplayString() : "(미할당)";

    private void OnMicMuteToggled(InputAction.CallbackContext ctx)
    {
        // 로그인 전에는 끌 마이크가 없다 — 타이틀에서는 음소거 표시가 어디에도 없어서(HUD는 게임,
        // 로스터는 로비, 설정 창은 열려 있을 때만) 눌러도 반응이 없는 것처럼 보인다. 씬 이름이 아니라
        // '음성이 살아 있는가'로 판정해 세션 전 어떤 상황에서도 같게 동작한다. 설정 창 토글은 이
        // 제한을 받지 않으므로 접속 전에 미리 꺼두는 경로는 그대로 남는다. (#430)
        if (!m_loggedIn) return;
        if (IsTypingInUI()) return;

        GameSettings.MicMuted = !GameSettings.MicMuted;
    }

    /// <summary>
    /// 설정의 음소거 값을 입력 장치에 적용한다. 값을 필드로 복사하지 않고 매번 GameSettings를 읽는다
    /// (ApplyVoiceVolume과 같은 방침 — 부르는 지점이 둘이라 복사본을 두면 어긋난다).
    ///
    /// 송신 모드(SetChannelTransmissionModeAsync)로 구현하지 않는다 — PTT가 그 API를 쓰므로
    /// 무전 키를 누르는 순간 음소거가 풀린다. 입력 장치 뮤트는 PTT 경로와 겹치지 않는다.
    ///
    /// 로그인 전에는 걸 수 없으므로 채널 참가 시점에 다시 부른다(JoinChannelAsync) — 그러지 않으면
    /// 마이크를 꺼둔 사람이 채널에 붙는 순간 음소거가 저절로 풀린다.
    /// </summary>
    public void ApplyMicMute()
    {
        if (!m_loggedIn) return;

        if (GameSettings.MicMuted) VivoxService.Instance.MuteInputDevice();
        else VivoxService.Instance.UnmuteInputDevice();
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
        m_voiceDistorted = false;
        SetVoiceState(EVoiceState.Idle);
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
        GUILayout.Label($"Transmitting(PTT): {m_transmitting}");
        GUILayout.Label($"Mic Muted: {GameSettings.MicMuted}");
        GUILayout.Label($"Push To Talk: {PushToTalkBinding}");
        GUILayout.Label($"Mic Mute Toggle: {MicMuteBinding}");
        GUILayout.Space(6);
        GUILayout.Label($"{ToLabel(VoiceState)} ({VoiceState})");
        if (!string.IsNullOrEmpty(m_statusDetail)) GUILayout.Label(m_statusDetail);
        if (m_voiceDistorted) GUILayout.Label("음성 왜곡(먹통) 중");
        GUILayout.EndArea();
    }
}
