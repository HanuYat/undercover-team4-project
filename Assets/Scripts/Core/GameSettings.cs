using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;

/// <summary>
/// 로컬 게임 설정(마우스 감도 · 시점 스무딩 · 시야각 · 화면 흔들림 · 속도 비네트 · 음량 · 마이크 · 언어) 저장소.
/// (#225, #430, #374, #665)
/// 설계 정본: docs/design/settings-ui.md · 언어는 docs/design/localization.md
/// </summary>
public static class GameSettings
{
    // PlayerPrefs 키 — 점 계층 접두사로 묶는다 (AuthBootstrap의 "player.nickname." 관례).
    // 값은 저장소에 남는 식별자라 배포 후 변경 금지 — 바꾸면 기존 저장값을 못 읽는다.
    // ⚠ 뒤에 v2가 붙은 이유 — 감도 밑값이 바뀌어(1 → 0.08) 예전에 저장된 배율이 그대로 살면
    // 전혀 다른 속도가 된다. 키를 갈아 옛 값을 버리고 기본값에서 다시 시작하게 한다. (#665)
    //
    // 아래는 키가 아니라 <b>이름</b>이다 — 실제 키는 Key()가 계정 자리를 끼워 만든다. (#796 후속)
    private const string k_mouseSensitivityName = "mouseSensitivity.v2";
    private const string k_lookSmoothingName = "lookSmoothing";
    private const string k_fovName = "fov";
    private const string k_screenShakeName = "screenShake";
    private const string k_speedVignetteName = "speedVignette";
    private const string k_masterVolumeName = "masterVolume";
    private const string k_voiceVolumeName = "voiceVolume";
    private const string k_micMutedName = "micMuted";
    private const string k_vSyncName = "vSync";
    // 창모드·해상도 (#796) — 확인창에서 [유지]를 눌러야 여기 남는다 (KeepDisplay).
    // <b>계정으로 가르지 않는 유일한 항목</b>이다 — 모니터가 다른 PC에 같은 계정으로 로그인하면
    // 그 PC에 없는 해상도가 걸리고, 로그인 순간 확인창 없이 화면이 갈아치워진다. 기기의 성질이다.
    private const string k_windowModeKey = "settings.windowMode";
    private const string k_resolutionWidthKey = "settings.resolutionWidth";
    private const string k_resolutionHeightKey = "settings.resolutionHeight";
    // settings.playerColor.<계정>.<부위> — 색은 기기 설정이 아니라 그 사람의 것이라 계정으로 가른다.
    // 정본은 Cloud Save(CosmeticsSaveService)이고 여기 값은 캐시다. (#432 후속)
    private const string k_playerColorKeyPrefix = "settings.playerColor.";

    private const string k_settingPrefix = "settings.";
    private const string k_localAccount = "local"; // 로그인 전에 고른 값이 갈 자리

    // 감도는 '배율'이다 — 프리팹의 기준 감도에 곱한다 (PlayerLook.HandleLook).
    // 슬라이더 min/max도 이 상수로 맞춰 인스펙터 값과 어긋나지 않게 한다.
    //
    // 밑값을 프리팹에서 1 → 0.08로 내렸다 (#665) — 배율 최저(0.2)에서도 화면이 빨랐다.
    // 그래서 여기 배율은 x1.00이 기본으로 읽히게 되돌린다: 실제 감도는 밑값이 정하고,
    // 이 슬라이더는 '기본보다 몇 배'만 고른다.
    public const float k_minMouseSensitivity = 0.25f;
    public const float k_maxMouseSensitivity = 3f;

    // 스무딩은 강도(0~1)로 내보낸다 — 실제로 쓰는 계수는 클수록 덜 부드러워서 슬라이더로 두면
    // 방향이 거꾸로 읽힌다. 0이면 스무딩 없음. (#665)
    public const float k_minLookSmoothing = 0f;
    public const float k_maxLookSmoothing = 1f;

    // 강도를 옮겨 담을 계수 구간 — 10이 가장 부드럽고 늦게 붙는 쪽, 60이 거의 즉시 붙는 쪽이다.
    private const float k_slowestLookRate = 10f;
    private const float k_fastestLookRate = 60f;

    private const float k_defaultMouseSensitivity = 1f;

    // 계수로 40 — 예전 값 20은 마우스를 멈춘 뒤에도 화면이 더 도는 게 느껴졌다 (#665).
    private const float k_defaultLookSmoothing = 0.4f;

    // 시야각은 Unity 관례대로 세로 기준이다 (Camera.fieldOfView). 16:9에서 세로 70 ≈ 가로 104도.
    // 좁을수록 멀미가 심해진다 — 하한은 예전 프리팹 값(60)에 맞췄다. (#665)
    public const float k_minFov = 60f;
    public const float k_maxFov = 100f;

    private const float k_defaultFov = 70f;
    private const bool k_defaultScreenShake = true;
    private const bool k_defaultSpeedVignette = true;

    private const float k_defaultMasterVolume = 1f;
    private const float k_defaultVoiceVolume = 1f;
    private const bool k_defaultMicMuted = false;

    // 수직동기화는 켜고 시작한다 — 끄면 프레임 상한이 사라져 매 프레임 비용이 그만큼 더 돈다 (#796).
    private const bool k_defaultVSync = true;

    // 이보다 세로가 작은 해상도는 목록에 내지 않는다 (#796). 캔버스가 기준 1920x1080에
    // match=height라 배율이 곧 세로 비율이다 — 480이면 0.44배가 되어 본문 26pt가 11.6px,
    // 자동 축소가 걸린 라벨은 그보다 더 작아져 읽을 수 없다. 720이면 17.3px로 읽힌다.
    private const int k_minResolutionHeight = 720;

    // 팔레트 첫 색 — 여기서는 목록 길이를 모른다. 범위 밖 값은 읽는 쪽(PlayerColorPalette.Get)이 자른다. (#432)
    private const int k_defaultPlayerColor = 0;

    // 인덱스 = EBodyPart. 길이를 enum에서 얻는다 — 부위가 늘어도 여기서 터지지 않게
    private static readonly int[] s_playerColors = new int[Enum.GetValues(typeof(EBodyPart)).Length];

    private static string s_account = k_localAccount;

    private static float s_mouseSensitivity = k_defaultMouseSensitivity; // 백킹 필드
    private static float s_lookSmoothing = k_defaultLookSmoothing;
    private static float s_fov = k_defaultFov;
    private static bool s_screenShake = k_defaultScreenShake;
    private static bool s_speedVignette = k_defaultSpeedVignette;
    private static float s_masterVolume = k_defaultMasterVolume;
    private static float s_voiceVolume = k_defaultVoiceVolume;
    private static bool s_micMuted = k_defaultMicMuted;
    private static bool s_vSync = k_defaultVSync;

    // 창모드·해상도는 '지금 화면에 걸려 있는 것'이다 — 저장값과 다를 수 있다(적용 후 확인 전). (#796)
    private static EWindowMode s_windowMode;
    private static Vector2Int s_resolution;
    private static Vector2Int[] s_resolutions; // 폭x높이로 묶은 해상도 목록 캐시

    /// <summary>마우스 감도 배율 (0.25~3.0, 기본 1.0). 프리팹 기준 감도에 곱해진다.</summary>
    public static float MouseSensitivity
    {
        get => s_mouseSensitivity;
        set
        {
            s_mouseSensitivity = Mathf.Clamp(value, k_minMouseSensitivity, k_maxMouseSensitivity);
            PlayerPrefs.SetFloat(Key(k_mouseSensitivityName), s_mouseSensitivity);
        }
    }

    /// <summary>
    /// 시점 스무딩 강도 (0~1). 0이면 마우스 입력을 그대로 쓰고, 올릴수록 부드러운 대신 늦게 따라온다.
    /// 감도와 달리 배율이 아니라 값 자체다 — 프리팹마다 다를 이유가 없는 취향값이다. (#665)
    /// </summary>
    public static float LookSmoothing
    {
        get => s_lookSmoothing;
        set
        {
            s_lookSmoothing = Mathf.Clamp(value, k_minLookSmoothing, k_maxLookSmoothing);
            PlayerPrefs.SetFloat(Key(k_lookSmoothingName), s_lookSmoothing);
        }
    }

    /// <summary>
    /// <see cref="LookSmoothing"/>을 <see cref="PlayerLook"/>이 쓰는 계수로 바꾼 값. 0이면 스무딩 없음.
    /// 강도와 방향이 반대다 — 강도를 올리면 계수는 내려간다. (#665)
    /// </summary>
    public static float LookSmoothingRate =>
        s_lookSmoothing <= 0.001f
            ? 0f
            : Mathf.Lerp(k_fastestLookRate, k_slowestLookRate, s_lookSmoothing);

    /// <summary>
    /// 시야각(세로, 도). <see cref="PlayerLook"/>이 카메라에 넣는다. (#665)
    /// 1인칭 팔은 뷰모델 전용 카메라가 따로 그리므로(#265) 여기를 올려도 손 크기는 그대로다.
    /// </summary>
    public static float Fov
    {
        get => s_fov;
        set
        {
            s_fov = Mathf.Clamp(value, k_minFov, k_maxFov);
            PlayerPrefs.SetFloat(Key(k_fovName), s_fov);
        }
    }

    /// <summary>
    /// 카메라를 흔들 것인가. 꺼도 손 떨림(<see cref="PlayerHandView"/>)은 남는다 —
    /// "감전됐다"는 정보는 두고 멀미가 나는 화면 흔들림만 뺀다. (#665)
    /// </summary>
    public static bool ScreenShake
    {
        get => s_screenShake;
        set
        {
            s_screenShake = value;
            PlayerPrefs.SetInt(Key(k_screenShakeName), s_screenShake ? 1 : 0);
        }
    }

    /// <summary>
    /// 빠르게 움직일 때 화면 가장자리를 좁힐 것인가 — 멀미를 줄이지만 시야도 함께 줄어든다.
    /// 켜고 끄는 것이 전제인 연출이라 설정에 낸다. (<see cref="SpeedVignetteUI"/>, #665)
    /// </summary>
    public static bool SpeedVignette
    {
        get => s_speedVignette;
        set
        {
            s_speedVignette = value;
            PlayerPrefs.SetInt(Key(k_speedVignetteName), s_speedVignette ? 1 : 0);
        }
    }

    /// <summary>Unity 사운드 전체 음량 (0~1). Vivox 음성에는 걸리지 않는다 — 그쪽은 VoiceVolume.</summary>
    public static float MasterVolume
    {
        get => s_masterVolume;
        set
        {
            s_masterVolume = Mathf.Clamp01(value);
            PlayerPrefs.SetFloat(Key(k_masterVolumeName), s_masterVolume);
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
            PlayerPrefs.SetFloat(Key(k_voiceVolumeName), s_voiceVolume);
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
            PlayerPrefs.SetInt(Key(k_micMutedName), s_micMuted ? 1 : 0);
            App.Net.Vivox?.ApplyMicMute();
            OnMicMutedChanged?.Invoke(s_micMuted);
        }
    }

    /// <summary>
    /// 수직동기화 (#796). 잘못 켜고 꺼도 화면이 깨지지 않으므로 창모드·해상도와 달리
    /// 다른 토글처럼 즉시 적용한다.
    /// </summary>
    public static bool VSync
    {
        get => s_vSync;
        set
        {
            s_vSync = value;
            PlayerPrefs.SetInt(Key(k_vSyncName), s_vSync ? 1 : 0);
            QualitySettings.vSyncCount = s_vSync ? 1 : 0;
        }
    }

    /// <summary>
    /// 지금 화면에 걸려 있는 창 모드 (#796). 대입하지 않는다 — 해상도와 한 번에
    /// <see cref="ApplyDisplay"/>로 바꾼다(<c>Screen.SetResolution</c>이 둘을 함께 받는다).
    /// </summary>
    public static EWindowMode WindowMode => s_windowMode;

    /// <summary>지금 화면에 걸려 있는 해상도 (#796).</summary>
    public static Vector2Int Resolution => s_resolution;

    /// <summary>
    /// 고를 수 있는 해상도 — <see cref="Screen.resolutions"/>를 폭x높이로 묶은 것 (#796).
    /// 원본은 주사율마다 같은 해상도를 되풀이해 돌려주므로 그대로 쓰면 드롭다운에 중복이 뜬다.
    ///
    /// <b>세로 <see cref="k_minResolutionHeight"/> 미만은 뺀다</b> — 고를 수 있게 두면 UI가
    /// 읽히지 않는 화면이 되고, 그 상태에서 되돌리려면 그 읽히지 않는 설정 창을 봐야 한다.
    /// <b>큰 것부터</b> 낸다 — 사람이 찾는 것은 대개 자기 모니터의 최대치다.
    /// </summary>
    public static IReadOnlyList<Vector2Int> AvailableResolutions
    {
        get
        {
            if (s_resolutions != null)
                return s_resolutions;

            var seen = new HashSet<Vector2Int>();
            var list = new List<Vector2Int>();

            foreach (var resolution in Screen.resolutions)
            {
                var size = new Vector2Int(resolution.width, resolution.height);
                if (size.y < k_minResolutionHeight)
                    continue;

                if (seen.Add(size))
                    list.Add(size);
            }

            // 지금 해상도가 목록에 없으면(걸러졌거나 창 크기를 직접 끌었거나) 넣어 준다 —
            // 드롭다운이 맞출 커서 자리가 없으면 고르지도 않은 항목이 선택된 것처럼 보인다.
            // 걸러진 값을 다시 넣는 것은 '이미 그 화면인 사람'을 위한 것이라 하한과 어긋나지 않는다.
            // 0은 넣지 않는다 — Load()가 아직 안 돈 상태(에디터 도메인 리로드 직후)의 값이다.
            if (s_resolution.x > 0 && s_resolution.y > 0 && seen.Add(s_resolution))
                list.Add(s_resolution);

            list.Sort((a, b) => a.x != b.x ? b.x.CompareTo(a.x) : b.y.CompareTo(a.y));
            s_resolutions = list.ToArray();
            return s_resolutions;
        }
    }

    /// <summary>
    /// 창 모드·해상도를 <b>화면에만</b> 적용한다 (#796) — 저장은 <see cref="KeepDisplay"/>가 한다.
    /// 다른 설정과 달리 setter에서 바로 저장하지 않는 이유: 잘못 고르면 화면이 깨져 되돌릴 수 없으므로,
    /// 확인창에서 [유지]를 누르기 전에는 남기지 않는다. 확인하지 못한 채 껐다 켜도 이전 값으로 돌아온다.
    /// (설계 결정 (e) — docs/design/settings-ui.md)
    /// </summary>
    public static void ApplyDisplay(EWindowMode mode, Vector2Int resolution)
    {
        s_windowMode = mode;
        s_resolution = new Vector2Int(Mathf.Max(1, resolution.x), Mathf.Max(1, resolution.y));
        Screen.SetResolution(s_resolution.x, s_resolution.y, ToFullScreenMode(mode));

        // 창 모드가 바뀌면 OS가 커서 잠금을 푼다 — 요청 수는 그대로 두고 판정만 다시 건다.
        // 설정 창이 떠 있는 동안은 계속 풀림이고, 창을 닫을 때 PopUnlock이 한 번 더 잡는다.
        CursorLock.Reassert();
    }

    /// <summary>
    /// 지금 적용돼 있는 창 모드·해상도를 저장한다 — 확인창 [유지] (#796).
    /// 다른 항목과 달리 디스크까지 바로 쓴다: 창을 닫기 전에 화면이 깨져 강제 종료하는 경우가
    /// 이 설정에서는 실제로 있고, 그때 "유지를 눌렀는데 안 남았다"가 되면 안 된다.
    /// </summary>
    public static void KeepDisplay()
    {
        PlayerPrefs.SetInt(k_windowModeKey, (int)s_windowMode);
        PlayerPrefs.SetInt(k_resolutionWidthKey, s_resolution.x);
        PlayerPrefs.SetInt(k_resolutionHeightKey, s_resolution.y);
        PlayerPrefs.Save();
    }

    private static FullScreenMode ToFullScreenMode(EWindowMode mode) =>
        mode switch
        {
            EWindowMode.Windowed => FullScreenMode.Windowed,
            EWindowMode.Fullscreen => FullScreenMode.ExclusiveFullScreen,
            _ => FullScreenMode.FullScreenWindow,
        };

    // 드롭다운에 없는 값(macOS의 MaximizedWindow 등)은 테두리 없는 전체 창으로 읽는다.
    private static EWindowMode ToWindowMode(FullScreenMode mode) =>
        mode switch
        {
            FullScreenMode.Windowed => EWindowMode.Windowed,
            FullScreenMode.ExclusiveFullScreen => EWindowMode.Fullscreen,
            _ => EWindowMode.Borderless,
        };

    /// <summary>내 로봇 색이 바뀌었다 — 로비 로스터 보고·초상·팔레트 표시가 되읽는다. 인자는 바뀐 부위. (#432)</summary>
    public static event Action<EBodyPart> OnPlayerColorChanged;

    /// <summary>
    /// 그 부위의 색 인덱스 (#432) — <see cref="PlayerColorPalette"/>의 몇 번째 색인지.
    /// 순수 코스메틱이고, 값의 출처는 여기 하나다: 로비 명부와 게임 씬의 <c>PlayerCosmetics</c>가
    /// 각자 자기 씬의 운반 수단으로 나르되 <b>읽는 값은 이것</b>이다 (음소거와 같은 구조, #430).
    ///
    /// 팔레트 길이를 여기서 모르므로 <b>자르지 않고</b> 그대로 담는다 — 팔레트를 아는 쪽이 자른다.
    /// </summary>
    public static int GetPlayerColor(EBodyPart part) => s_playerColors[(int)part];

    public static void SetPlayerColor(EBodyPart part, int index)
    {
        int clamped = Mathf.Max(0, index);
        if (s_playerColors[(int)part] == clamped)
            return;

        s_playerColors[(int)part] = clamped;
        PlayerPrefs.SetInt(ColorKey(part), clamped);
        OnPlayerColorChanged?.Invoke(part);
    }

    /// <summary>
    /// 설정을 이 계정 것으로 갈아탄다 (#796 후속) — 로그인·로그아웃이 부른다.
    /// 비우면 로그인 전 자리로 돌아간다. <b>창모드·해상도는 따라오지 않는다</b> — 기기 단위다.
    /// </summary>
    public static void UseAccount(string accountId)
    {
        string next = string.IsNullOrWhiteSpace(accountId) ? k_localAccount : accountId;
        if (s_account == next)
            return;

        s_account = next;
        LoadAccountSettings();
    }

    /// <summary>클라우드에서 받은 한 벌을 적용한다 — 캐시에도 남긴다. (CosmeticsSaveService)</summary>
    public static void ApplyPlayerColors(IReadOnlyList<int> colors)
    {
        if (colors == null)
            return;

        foreach (EBodyPart part in Enum.GetValues(typeof(EBodyPart)))
        {
            int index = (int)part;
            if (index >= colors.Count)
                continue;

            int clamped = Mathf.Max(0, colors[index]);
            s_playerColors[index] = clamped;
            PlayerPrefs.SetInt(ColorKey(part), clamped);
            OnPlayerColorChanged?.Invoke(part);
        }
    }

    /// <summary>
    /// 그 항목의 실제 PlayerPrefs 키 (#796 후속). <b>로그인 전('local')에는 계정 자리를 넣지 않는다</b> —
    /// 계정으로 가르기 전에 쓰던 키가 그대로 로그인 전 자리가 되어, 갱신해도 저장값을 잃지 않는다.
    /// </summary>
    private static string Key(string name) =>
        s_account == k_localAccount
            ? k_settingPrefix + name
            : k_settingPrefix + s_account + "." + name;

    // 색만 로그인 전에도 계정 자리를 쓴다 — 이미 그 형식으로 저장돼 있어 굳이 바꾸지 않는다 (#432)
    private static string ColorKey(EBodyPart part) =>
        k_playerColorKeyPrefix + s_account + "." + part;

    // 캐시에서 전 부위를 다시 읽어 적용한다. 저장된 값이 없으면 팔레트 첫 색이다.
    private static void LoadPlayerColors()
    {
        foreach (EBodyPart part in Enum.GetValues(typeof(EBodyPart)))
        {
            s_playerColors[(int)part] = PlayerPrefs.GetInt(ColorKey(part), k_defaultPlayerColor);
            OnPlayerColorChanged?.Invoke(part);
        }
    }

    /// <summary>
    /// 고를 수 있는 언어 목록 — 설정 창 드롭다운이 이 순서 그대로 항목을 만든다. (#374)
    /// 로케일 추가는 Localization Settings에서 하며 여기 코드는 건드리지 않는다.
    /// </summary>
    public static IList<Locale> AvailableLocales => LocalizationSettings.AvailableLocales.Locales;

    /// <summary>
    /// 표시 언어. 다른 설정과 달리 <b>백킹 필드를 두지 않는다</b> — 현재 언어의 출처는
    /// <see cref="LocalizationSettings.SelectedLocale"/> 하나이고, 여기 사본을 두면 F10·디버그 등
    /// 다른 경로로 언어가 바뀌었을 때 설정 창 표시가 어긋난다.
    ///
    /// 영속화도 여기서 하지 않고 <see cref="PlayerPrefLocaleSelector"/>에 맡긴다. 그쪽이
    /// <c>IStartupLocaleSelector</c>로 <b>시작 시 복원</b>까지 담당하므로, 저장 키를 양쪽이 각자
    /// 가지면 "설정 창이 저장한 언어"와 "다음 실행에 복원되는 언어"가 갈린다. 쓰는 곳은 여기,
    /// 키를 아는 곳은 거기 하나다.
    /// </summary>
    public static Locale Locale
    {
        get => LocalizationSettings.SelectedLocale;
        set
        {
            if (value == null || value == LocalizationSettings.SelectedLocale)
                return;

            LocalizationSettings.SelectedLocale = value;
            PlayerPrefLocaleSelector.Save(value);
        }
    }

    /// <summary>
    /// 저장된 값을 읽어 적용한다. 플레이 시작마다 자동 실행 — 언어 외 전부를 무조건 덮어쓰므로
    /// 도메인 리로드를 꺼도 이전 플레이 값이 남지 않는다 (App·AppBootstrap과 같은 방침).
    ///
    /// 여기서 읽는 것은 <b>로그인 전('local') 자리</b>다. 로그인하면 <see cref="UseAccount"/>가
    /// 계정 자리로 갈아탄다 (#796 후속).
    ///
    /// 언어는 여기서 건드리지 않는다 — <see cref="PlayerPrefLocaleSelector"/>가 Localization 초기화
    /// 시점에 이미 복원한다. 여기서 또 대입하면 초기화 순서에 따라 복원값을 덮어쓸 수 있다.
    /// </summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Load()
    {
        // static 이벤트도 함께 리셋한다 — 도메인 리로드를 끄면 이전 플레이의 죽은 구독자가 남아
        // 파괴된 UI를 깨운다. 씬 로드 전이라 이번 플레이의 구독자는 아직 붙지 않았다. (#430)
        OnMicMutedChanged = null;
        OnPlayerColorChanged = null;

        // 밑값부터 되돌린 뒤 읽는다 — LoadAccountSettings는 '저장값이 없으면 지금 값을 둔다'라서,
        // 도메인 리로드를 껐을 때 이전 플레이 값이 그대로 살아남는 것을 여기서 끊는다.
        // ResetToDefaults가 아니라 필드만 되돌리는 것에 주의 — 저장값을 읽기 전에 덮어쓰면 안 된다.
        ResetFields();

        // 로그인 전이라 아직 'local' 자리를 읽는다. 로그인하면 계정 것으로 갈아탄다 (#796 후속)
        s_account = k_localAccount;
        LoadAccountSettings();

        LoadDisplay(); // 창모드·해상도는 계정과 무관한 기기 단위다
    }

    /// <summary>
    /// 지금 계정 자리의 값을 읽어 적용한다 — 시작할 때와 계정이 바뀔 때 (#796 후속).
    ///
    /// <b>저장값이 없으면 지금 값을 그대로 둔다.</b> 처음 로그인하는 계정에는 아직 아무것도 없는데
    /// 밑값으로 떨어뜨리면, 로그인 전에 맞춰 둔 감도·볼륨이 로그인하는 순간 기본값으로 튄다.
    /// 대입은 setter를 타므로 그 값이 곧 계정 자리에 쓰이고, 다음 로그인부터는 자기 값을 읽는다.
    /// </summary>
    private static void LoadAccountSettings()
    {
        MouseSensitivity = PlayerPrefs.GetFloat(Key(k_mouseSensitivityName), s_mouseSensitivity);
        LookSmoothing = PlayerPrefs.GetFloat(Key(k_lookSmoothingName), s_lookSmoothing);
        Fov = PlayerPrefs.GetFloat(Key(k_fovName), s_fov);
        ScreenShake = PlayerPrefs.GetInt(Key(k_screenShakeName), s_screenShake ? 1 : 0) != 0;
        SpeedVignette = PlayerPrefs.GetInt(Key(k_speedVignetteName), s_speedVignette ? 1 : 0) != 0;
        MasterVolume = PlayerPrefs.GetFloat(Key(k_masterVolumeName), s_masterVolume);
        VoiceVolume = PlayerPrefs.GetFloat(Key(k_voiceVolumeName), s_voiceVolume);
        MicMuted = PlayerPrefs.GetInt(Key(k_micMutedName), s_micMuted ? 1 : 0) != 0;
        VSync = PlayerPrefs.GetInt(Key(k_vSyncName), s_vSync ? 1 : 0) != 0;
        LoadPlayerColors();
    }

    // 백킹 필드만 밑값으로 되돌린다 — PlayerPrefs는 건드리지 않는다.
    // (되돌린 값을 저장까지 하면 바로 뒤에서 읽을 저장값을 스스로 지운다)
    private static void ResetFields()
    {
        s_mouseSensitivity = k_defaultMouseSensitivity;
        s_lookSmoothing = k_defaultLookSmoothing;
        s_fov = k_defaultFov;
        s_screenShake = k_defaultScreenShake;
        s_speedVignette = k_defaultSpeedVignette;
        s_masterVolume = k_defaultMasterVolume;
        s_voiceVolume = k_defaultVoiceVolume;
        s_micMuted = k_defaultMicMuted;
        s_vSync = k_defaultVSync;
    }

    /// <summary>
    /// 저장된 창 모드·해상도를 적용한다 (#796). <b>저장값이 없으면 화면을 건드리지 않는다</b> —
    /// 첫 실행에는 빌드가 띄운 창이 그대로 기본값이다.
    /// </summary>
    private static void LoadDisplay()
    {
        s_resolutions = null; // 도메인 리로드 OFF 대비 — 이전 플레이의 캐시가 남지 않게
        s_windowMode = ToWindowMode(Screen.fullScreenMode);
        s_resolution = new Vector2Int(Screen.width, Screen.height);

        if (!PlayerPrefs.HasKey(k_windowModeKey))
            return;

        int storedMode = PlayerPrefs.GetInt(k_windowModeKey, (int)s_windowMode);
        ApplyDisplay(
            Enum.IsDefined(typeof(EWindowMode), storedMode)
                ? (EWindowMode)storedMode
                : s_windowMode,
            new Vector2Int(
                PlayerPrefs.GetInt(k_resolutionWidthKey, s_resolution.x),
                PlayerPrefs.GetInt(k_resolutionHeightKey, s_resolution.y)
            )
        );
    }

    /// <summary>
    /// 언어와 로봇 색을 뺀 전부를 기본값으로 되돌린다 — 설정 창의 [기본값 복원].
    /// <b>로봇 색도 빼는 이유는 언어와 같다</b> — 감도·볼륨을 되돌리려다 자기 색이 지워지면
    /// 되돌린 줄도 모르고 남의 색과 겹친다. 색은 로비 팔레트에서 언제든 다시 고른다. (#432)
    /// <b>언어는 포함하지 않는다</b> — 되돌릴 '기본 언어'가 시스템 로케일이라, 한국어로 쓰던 사람이
    /// 이 버튼을 누르면 메뉴 언어가 통째로 바뀐다. 감도·볼륨을 되돌리려다 화면을 못 읽게 되는 쪽이
    /// 잘못 조절한 값보다 나쁘고, 언어는 바로 위 드롭다운에서 되돌릴 수 있다. (#374)
    /// <b>창 모드·해상도도 빼고 수직동기화만 넣는다</b> — 버튼 한 번에 창이 통째로 바뀌면
    /// 감도를 되돌리려던 사람이 확인창부터 마주한다. 되돌릴 수단이 바로 위 드롭다운에 있는 것도
    /// 언어와 같다. 수직동기화는 잘못 돌아가도 화면이 깨지지 않아 함께 되돌린다. (#796)
    /// </summary>
    public static void ResetToDefaults()
    {
        MouseSensitivity = k_defaultMouseSensitivity;
        LookSmoothing = k_defaultLookSmoothing;
        Fov = k_defaultFov;
        ScreenShake = k_defaultScreenShake;
        SpeedVignette = k_defaultSpeedVignette;
        MasterVolume = k_defaultMasterVolume;
        VoiceVolume = k_defaultVoiceVolume;
        MicMuted = k_defaultMicMuted;
        VSync = k_defaultVSync;
    }

    /// <summary>
    /// 디스크에 기록한다 — 설정 창을 닫을 때 한 번만 부른다.
    /// (setter의 PlayerPrefs 대입은 메모리까지다. 슬라이더를 끌 때마다 여기까지 오면 드래그 내내 디스크를 쓴다)
    /// </summary>
    public static void Save() => PlayerPrefs.Save();
}
