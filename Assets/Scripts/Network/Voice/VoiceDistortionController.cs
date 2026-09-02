using System;
using System.Collections.Generic;
using Unity.Services.Vivox;
using UnityEngine;

/// <summary>
/// 먹통 음성 왜곡 (#372) — <see cref="VivoxManager"/>의 부품 (#466).
/// Vivox 오디오 탭으로 참가자 음성을 AudioSource로 끌어와 필터를 건다. 전부 로컬 처리(동기화 없음).
/// 음색은 <see cref="VoiceDistortionProfile"/>이 정하고, 이 부품은 '언제 왜곡할지'만 안다.
///
/// 배선: VivoxManager와 같은 오브젝트에 붙여 SerializeField로 연결 (architecture.md R3).
/// 매니저가 아니므로 CommonManagerBase 비상속. 참가자 훅과 정리 순서는 VivoxManager가 소유한다.
/// </summary>
public class VoiceDistortionController : MonoBehaviour
{
    [Tooltip(
        "왜곡 음색 프로파일(SO) — 필터 조합·수치는 전부 이 에셋이 정한다. 비면 왜곡을 걸지 않는다"
    )]
    [SerializeField]
    private VoiceDistortionProfile m_profile;

    [Tooltip(
        "근접 채널 음성도 왜곡할지. 끄면 무전 채널만 왜곡한다 — 탭으로 빼낸 뒤에도 Vivox의 3D 감쇠가 "
            + "유지되는지 확인이 필요하다(멀리 있는 사람이 크게 들리면 끌 것)"
    )]
    [SerializeField]
    private bool m_distortProximityToo = true;

    private bool m_distorted;
    private float m_nextGlitchTime;

    private bool m_channelsJoined; // ActiveChannels 접근 가드 (예전 m_loggedIn 자리)
    private string m_proximityChannelName;

    // 탭을 건 참가자 → 재생 AudioSource. 해제 시 전부 되돌린다.
    private readonly Dictionary<VivoxParticipant, AudioSource> m_taps = new();

    /// <summary>왜곡 중인지 — 디버그 패널 표시용.</summary>
    public bool IsDistorted => m_distorted;

    // ---- VivoxManager가 부르는 진입점 ----

    /// <summary>왜곡을 켜고 끈다 — <see cref="DeviceBlackoutView"/> → VivoxManager 위임 경로. (#372)</summary>
    public void SetDistorted(bool distorted)
    {
        if (m_distorted == distorted)
            return;

        // 프로파일이 없으면 시작조차 하지 않는다 — 탭만 걸고 필터를 못 얹으면 그 사람이 통째로 무음이 된다
        if (distorted && m_profile == null)
        {
            Debug.LogWarning(
                "[VoiceDistortionController] VoiceDistortionProfile 미할당 — 왜곡을 건너뛴다",
                this
            );
            return;
        }

        m_distorted = distorted;

        if (distorted)
            ApplyToAll();
        else
            ClearAll();
    }

    /// <summary>
    /// 왜곡 중 재생 볼륨에 설정값 반영 (#225) — 왜곡 중에는 출력 장치 볼륨이 통하지 않는다.
    /// 값은 복사하지 않고 매번 GameSettings에서 읽는다.
    /// </summary>
    public void ApplyVolume()
    {
        float volume = GameSettings.VoiceVolume;

        foreach (AudioSource source in m_taps.Values)
        {
            if (source != null)
                source.volume = volume;
        }
    }

    /// <summary>참가자 입장 통보 — 먹통 중에 들어온 사람도 왜곡을 받아야 한다. (#372)</summary>
    public void HandleParticipantAdded(VivoxParticipant participant)
    {
        if (m_distorted && ShouldDistortChannel(participant.ChannelName))
            Apply(participant);
    }

    /// <summary>참가자 퇴장 통보 — 탭 오브젝트는 Vivox가 함께 정리하므로 기록만 지운다.</summary>
    public void HandleParticipantRemoved(VivoxParticipant participant) =>
        m_taps.Remove(participant);

    /// <summary>채널 참가 완료 — 먹통 중 재참가면 이미 들어와 있는 참가자에게도 다시 건다.</summary>
    public void NotifyChannelsJoined(string proximityChannelName)
    {
        m_proximityChannelName = proximityChannelName;
        m_channelsJoined = true;

        if (m_distorted)
            ApplyToAll();
    }

    /// <summary>채널 이탈 — 탭은 참가자와 함께 사라지므로 기록만 비운다. 플래그는 유지(재참가 대비).</summary>
    public void NotifyChannelsLeft()
    {
        m_channelsJoined = false;
        m_taps.Clear();
    }

    /// <summary>음성 종료(로그아웃·세션 이탈) — 먹통 맥락이 사라지므로 플래그까지 내린다. (#372)</summary>
    public void NotifyVoiceEnded()
    {
        m_distorted = false;
        m_channelsJoined = false;
        m_taps.Clear();
    }

    // ---- 내부 ----

    private void ApplyToAll()
    {
        if (!m_channelsJoined)
            return;

        foreach (var channel in VivoxService.Instance.ActiveChannels)
        {
            if (!ShouldDistortChannel(channel.Key))
                continue;

            foreach (VivoxParticipant participant in channel.Value)
                Apply(participant);
        }
    }

    private bool ShouldDistortChannel(string channelName) =>
        m_distortProximityToo || channelName != m_proximityChannelName;

    private void Apply(VivoxParticipant participant)
    {
        if (participant == null || participant.IsSelf)
            return; // 자기 목소리는 어차피 자기에게 재생되지 않는다
        if (m_profile == null)
            return; // 진입점이 둘이라 여기서도 확인
        if (m_taps.ContainsKey(participant))
            return; // 중복 탭 방지

        bool tapCreated = false;

        try
        {
            // silenceInChannelAudioMix=true — 이 호출이 성공한 순간부터 Vivox 믹스에서는 안 들린다.
            // 따라서 이후 어느 경로로 실패하든 탭을 되돌려야 한다 (안 하면 그 사람이 세션 내내 무음).
            GameObject tapObject = participant.CreateVivoxParticipantTap(
                $"BlackoutVoiceTap_{participant.PlayerId}",
                true
            );
            tapCreated = true;

            AudioSource source = participant.ParticipantTapAudioSource;
            if (tapObject == null || source == null)
            {
                Debug.LogWarning(
                    $"[VoiceDistortionController] 탭 생성 실패 — {participant.PlayerId}"
                );
                SafeDestroyTap(participant);
                return;
            }

            m_profile.Apply(tapObject, source);
            source.volume = GameSettings.VoiceVolume; // 새 AudioSource 기본값은 1 (#225)

            m_taps[participant] = source;
        }
        catch (Exception ex)
        {
            Debug.LogError(
                $"[VoiceDistortionController] 왜곡 적용 실패 ({participant.PlayerId}): {ex}"
            );

            // 등록에 실패했다면 이미 걸린 탭을 되돌린다 (등록됐다면 해제는 ClearAll이 맡는다)
            if (tapCreated && !m_taps.ContainsKey(participant))
                SafeDestroyTap(participant);
        }
    }

    // 여기서 난 실패가 나머지 정리를 막지 않게 한다. 탭 오브젝트가 파괴되며 필터도 함께 사라진다.
    private void SafeDestroyTap(VivoxParticipant participant)
    {
        if (participant == null)
            return;

        try
        {
            participant.DestroyVivoxParticipantTap();
        }
        catch (Exception ex)
        {
            Debug.LogError(
                $"[VoiceDistortionController] 탭 해제 실패 ({participant.PlayerId}): {ex}"
            );
        }
    }

    private void ClearAll()
    {
        // 키 복사본 순회 — SafeDestroyTap이 Vivox 콜백을 동기로 깨우면 HandleParticipantRemoved가
        // m_taps를 건드려 순회 중 수정 예외가 난다 (#372)
        foreach (VivoxParticipant participant in new List<VivoxParticipant>(m_taps.Keys))
            SafeDestroyTap(participant);

        m_taps.Clear();
    }

    // 피치를 주기적으로 튀게 해 "신호가 튄다"는 인상을 준다. 간격·폭은 프로파일이 정한다.
    private void Update()
    {
        if (!m_distorted || m_taps.Count == 0)
            return;
        if (Time.time < m_nextGlitchTime)
            return;

        m_nextGlitchTime = Time.time + m_profile.NextGlitchInterval();
        float pitch = m_profile.NextGlitchPitch();

        foreach (AudioSource source in m_taps.Values)
            if (source != null)
                source.pitch = pitch;
    }
}
