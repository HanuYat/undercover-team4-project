using System;
using UnityEngine;

/// <summary>
/// 로컬 게임 설정(마우스 감도 · 음량 · 마이크) 저장소. (#225, #430)
/// 설계 정본: docs/design/settings-ui.md
/// </summary>
public static class GameSettings
{
    // PlayerPrefs 키 — 점 계층 접두사로 묶는다 (AuthBootstrap의 "player.nickname." 관례).
    // 값은 저장소에 남는 식별자라 배포 후 변경 금지 — 바꾸면 기존 저장값을 못 읽는다.
    private const string k_mouseSensitivityKey = "settings.mouseSensitivity";
    private const string k_masterVolumeKey = "settings.masterVolume";
    private const string k_voiceVolumeKey = "settings.voiceVolume";
    private const string k_micMutedKey = "settings.micMuted";

    // 감도는 '배율'이다 — 프리팹의 기준 감도에 곱한다 (PlayerMovement.HandleLook).
    // 슬라이더 min/max도 이 상수로 맞춰 인스펙터 값과 어긋나지 않게 한다.
    public const float k_minMouseSensitivity = 0.2f;
    public const float k_maxMouseSensitivity = 3f;

    private const float k_defaultMouseSensitivity = 1f;
    private const float k_defaultMasterVolume = 1f;
    private const float k_defaultVoiceVolume = 1f;
    private const bool k_defaultMicMuted = false;

    private static float s_mouseSensitivity = k_defaultMouseSensitivity;    // 백킹 필드
    private static float s_masterVolume = k_defaultMasterVolume;
    private static float s_voiceVolume = k_defaultVoiceVolume;
    private static bool s_micMuted = k_defaultMicMuted;

    /// <summary>마우스 감도 배율 (0.2~3.0). 프리팹 기준 감도에 곱해진다.</summary>
    public static float MouseSensitivity
    {
        get => s_mouseSensitivity;
        set
        {
            s_mouseSensitivity = Mathf.Clamp(value, k_minMouseSensitivity, k_maxMouseSensitivity);
            PlayerPrefs.SetFloat(k_mouseSensitivityKey, s_mouseSensitivity);
        }
    }

    /// <summary>Unity 사운드 전체 음량 (0~1). Vivox 음성에는 걸리지 않는다 — 그쪽은 VoiceVolume.</summary>
    public static float MasterVolume
    {
        get => s_masterVolume;
        set
        {
            s_masterVolume = Mathf.Clamp01(value);
            PlayerPrefs.SetFloat(k_masterVolumeKey, s_masterVolume);
            AudioListener.volume = s_masterVolume;
        }
    }

    /// <summary>
    /// 음성 채팅 음량 (0~1). 무전·근접 공통 — 채널별 분리는 Phase 2.
    /// 로그인·채널 참가 전에는 매니저가 없거나 적용해도 의미가 없으므로, VivoxManager가
    /// 채널 참가 시점에 이 값을 다시 당겨 간다. 여기서는 살아 있을 때만 밀어 넣는다.
    /// </summary>
    public static float VoiceVolume
    {
        get => s_voiceVolume;
        set
        {
            s_voiceVolume = Mathf.Clamp01(value);
            PlayerPrefs.SetFloat(k_voiceVolumeKey, s_voiceVolume);
            App.Net.Vivox?.ApplyVoiceVolume();
        }
    }

    /// <summary>
    /// 음소거 표시를 되읽는 쪽(설정 창 토글 · HUD 아이콘 · 로비 로스터 보고)에 변경을 알린다. (#430)
    /// 감도·볼륨과 달리 싱크가 전역 API가 아니라 '표시'라서, 설정값 중 처음으로 이벤트가 필요하다
    /// (설정 창 토글과 토글 키가 같은 값을 가리키므로 한쪽 변경이 다른 쪽 표시에 닿아야 한다).
    /// </summary>
    public static event Action<bool> OnMicMutedChanged;

    /// <summary>
    /// 내 마이크 음소거. 상태의 출처는 여기 하나 — VivoxManager는 값을 복사해 두지 않고
    /// 적용할 때마다 이 값을 읽는다 (VivoxManager.ApplyMicMute).
    /// </summary>
    public static bool MicMuted
    {
        get => s_micMuted;
        set
        {
            s_micMuted = value;
            PlayerPrefs.SetInt(k_micMutedKey, s_micMuted ? 1 : 0);
            App.Net.Vivox?.ApplyMicMute();
            OnMicMutedChanged?.Invoke(s_micMuted);
        }
    }

    /// <summary>
    /// 저장된 값을 읽어 적용한다. 플레이 시작마다 자동 실행 — 네 필드를 무조건 덮어쓰므로
    /// 도메인 리로드를 꺼도 이전 플레이 값이 남지 않는다 (App·AppBootstrap과 같은 방침).
    /// </summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Load()
    {
        // static 이벤트도 함께 리셋한다 — 도메인 리로드를 끄면 이전 플레이의 죽은 구독자가 남아
        // 파괴된 UI를 깨운다. 씬 로드 전이라 이번 플레이의 구독자는 아직 붙지 않았다. (#430)
        OnMicMutedChanged = null;

        MouseSensitivity = PlayerPrefs.GetFloat(k_mouseSensitivityKey, k_defaultMouseSensitivity);
        MasterVolume = PlayerPrefs.GetFloat(k_masterVolumeKey, k_defaultMasterVolume);
        VoiceVolume = PlayerPrefs.GetFloat(k_voiceVolumeKey, k_defaultVoiceVolume);
        MicMuted = PlayerPrefs.GetInt(k_micMutedKey, k_defaultMicMuted ? 1 : 0) != 0;
    }

    /// <summary>네 값을 기본값으로 되돌린다 — 설정 창의 [기본값 복원].</summary>
    public static void ResetToDefaults()
    {
        MouseSensitivity = k_defaultMouseSensitivity;
        MasterVolume = k_defaultMasterVolume;
        VoiceVolume = k_defaultVoiceVolume;
        MicMuted = k_defaultMicMuted;
    }

    /// <summary>
    /// 디스크에 기록한다 — 설정 창을 닫을 때 한 번만 부른다.
    /// (setter의 PlayerPrefs 대입은 메모리까지다. 슬라이더를 끌 때마다 여기까지 오면 드래그 내내 디스크를 쓴다)
    /// </summary>
    public static void Save() => PlayerPrefs.Save();
}
