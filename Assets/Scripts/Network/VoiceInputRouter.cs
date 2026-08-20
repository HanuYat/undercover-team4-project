using System;
using Cysharp.Threading.Tasks;
using TMPro;
using Unity.Services.Vivox;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

/// <summary>
/// 음성 입력 — PTT 무전 송신과 마이크 음소거 토글 (#430). <see cref="VivoxManager"/>의 부품 (#466).
/// 두 경로는 서로 독립이다: PTT는 송신 모드를, 음소거는 입력 장치 뮤트를 건드린다.
///
/// 배선: VivoxManager와 같은 오브젝트에 붙여 SerializeField로 연결 (architecture.md R3).
/// 로그인·참가 상태는 VivoxManager가 통보한다 — 이 부품은 Vivox 수명을 직접 알지 않는다.
/// </summary>
public class VoiceInputRouter : MonoBehaviour
{
    [SerializeField]
    private InputActionReference m_pushToTalkAction;

    [Tooltip("마이크 음소거 토글 (#430)")]
    [SerializeField]
    private InputActionReference m_micMuteToggleAction;

    private bool m_loggedIn;
    private bool m_channelsJoined;
    private string m_proximityChannelName;
    private bool m_transmitting; // PTT를 누르고 있는지 — 디버그 표시용

    // 완전 사망 중 PTT 차단 (#725) — GameSettings.MicMuted(음소거)와는 독립된 축이라 따로 둔다.
    private bool m_transmitBlocked;

    /// <summary>음소거 중에 무전 키를 눌렀다 — HUD가 "마이크가 꺼져 있습니다"를 띄운다.</summary>
    public event Action OnMutedTalkAttempt;

    /// <summary>PTT 송신 중인지 — 디버그 패널 표시용.</summary>
    public bool IsTransmitting => m_transmitting;

    /// <summary>무전 키 표시 문자열 — 로비 안내와 디버그 패널이 함께 쓴다.</summary>
    public string PushToTalkBinding =>
        m_pushToTalkAction != null
            ? m_pushToTalkAction.action.GetBindingDisplayString()
            : "(미할당)";

    /// <summary>음소거 토글 키 표시 문자열 — 안내·디버그 패널용.</summary>
    public string MicMuteBinding =>
        m_micMuteToggleAction != null
            ? m_micMuteToggleAction.action.GetBindingDisplayString()
            : "(미할당)";

    // ---- VivoxManager가 통보하는 수명 ----

    /// <summary>Vivox 로그인 완료 — 이 시점부터 입력 장치 뮤트를 걸 수 있다.</summary>
    public void NotifyLoggedIn() => m_loggedIn = true;

    /// <summary>채널 참가 완료 — 이 시점부터 송신 모드를 바꿀 수 있다.</summary>
    public void NotifyChannelsJoined(string proximityChannelName)
    {
        m_proximityChannelName = proximityChannelName;
        m_channelsJoined = true;
    }

    /// <summary>채널 이탈·비자발 드롭 — 송신 상태를 내린다(로그인은 유지).</summary>
    public void NotifyChannelsLeft()
    {
        m_channelsJoined = false;
        m_transmitting = false;
    }

    /// <summary>Vivox 로그아웃 — 입력이 닿을 대상이 사라졌다.</summary>
    public void NotifyVoiceEnded()
    {
        m_loggedIn = false;
        m_channelsJoined = false;
        m_transmitting = false;
    }

    /// <summary>
    /// 설정의 음소거 값을 입력 장치에 적용한다. 값을 필드로 복사하지 않고 매번 GameSettings를 읽는다
    /// (ApplyVoiceVolume과 같은 방침 — 부르는 지점이 둘이라 복사본을 두면 어긋난다).
    ///
    /// 송신 모드로 구현하지 않는다 — PTT가 그 API를 쓰므로 무전 키를 누르는 순간 음소거가 풀린다.
    /// 로그인 전에는 걸 수 없으므로 채널 참가 시점에 VivoxManager가 다시 부른다.
    /// </summary>
    public void ApplyMicMute()
    {
        if (!m_loggedIn)
            return;

        if (GameSettings.MicMuted)
            VivoxService.Instance.MuteInputDevice();
        else
            VivoxService.Instance.UnmuteInputDevice();
    }

    /// <summary>
    /// PTT 송신을 강제로 막거나 푼다 — 완전 사망(Die) 동안 말을 막는 규칙(#725)용.
    /// 음소거(<see cref="ApplyMicMute"/>)와는 독립된 축이다.
    /// </summary>
    public void SetTransmitBlocked(bool blocked)
    {
        if (m_transmitBlocked == blocked)
            return;

        m_transmitBlocked = blocked;

        if (blocked && m_transmitting)
            SetRadioTransmit(false);
    }

    // ---- 입력 구독 ----

    private void OnEnable()
    {
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
    }

    private void OnDisable()
    {
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
    }

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
        if (IsTypingInUI())
            return;

        // 완전 사망 중엔 조용히 무시한다 — 화면 암전·무음으로 이미 신호가 뚜렷하다 (#725)
        if (m_transmitBlocked)
            return;

        // 음소거가 이긴다 — 송신을 막는 가드는 넣지 않는다(입력 장치가 뮤트면 송신 모드와 무관하게
        // 소리가 나가지 않아 두 경로가 자연히 독립이다). 대신 눌렀다는 사실만 알린다 — 이 안내가
        // 없으면 음소거를 잊고 말하는 상황이 그대로 남는다. (#430)
        if (GameSettings.MicMuted)
            OnMutedTalkAttempt?.Invoke();

        SetRadioTransmit(true);
    }

    // 뗄 때는 타이핑 여부를 보지 않는다 — 누른 뒤 입력창을 클릭하고 떼는 순서면 송신이 켜진 채
    // 남는다. 켜져 있을 때만 끄면 되므로 m_transmitting으로 판단한다. (#430)
    private void OnPushToTalkCanceled(InputAction.CallbackContext ctx)
    {
        if (m_transmitting)
            SetRadioTransmit(false);
    }

    private void OnMicMuteToggled(InputAction.CallbackContext ctx)
    {
        // 로그인 전에는 끌 마이크가 없다 — 타이틀에서는 음소거 표시가 어디에도 없어서(HUD는 게임,
        // 로스터는 로비, 설정 창은 열려 있을 때만) 눌러도 반응이 없는 것처럼 보인다. 씬 이름이 아니라
        // '음성이 살아 있는가'로 판정해 세션 전 어떤 상황에서도 같게 동작한다. 설정 창 토글은 이
        // 제한을 받지 않으므로 접속 전에 미리 꺼두는 경로는 그대로 남는다. (#430)
        if (!m_loggedIn)
            return;
        if (IsTypingInUI())
            return;

        GameSettings.MicMuted = !GameSettings.MicMuted;
    }

    private void SetRadioTransmit(bool on)
    {
        if (!m_channelsJoined)
            return;

        m_transmitting = on;

        // 무전 채널 송신을 켜고 끈다 — 끄면 근접 채널로만 송신한다(참가 시 기본값과 동일).
        var mode = on ? TransmissionMode.All : TransmissionMode.Single;
        string channel = on ? null : m_proximityChannelName;
        VivoxService.Instance.SetChannelTransmissionModeAsync(mode, channel).AsUniTask().Forget();
    }
}
