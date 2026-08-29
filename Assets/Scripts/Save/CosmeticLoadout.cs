using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 계정이 <b>착용한</b> 치장 — 로봇 색(#432)과 액세서리(#818). (#931에서 GameSettings에서 분리)
///
/// <see cref="CosmeticInventory"/>(계정이 <b>가진</b> 것)와 짝이고 관례도 같다:
/// static · 계정별 PlayerPrefs 캐시 · <b>정본은 Cloud Save</b>(<see cref="CosmeticsSaveService"/>) ·
/// 계정 자리 갈아타기를 부르는 곳은 그 서비스 하나.
///
/// <b>GameSettings에서 떼어낸 이유</b> — 소비자가 갈린다. 색·치장을 읽는 11곳(로비 초상·명부 보고·
/// 게임 씬 표현·피커) 중 설정 창은 한 곳도 없다. 계정 단위 외형 상태를 로컬 설정 저장소에 두면
/// 계정 API까지 그쪽으로 딸려 들어간다.
///
/// 값을 <b>자르지 않고</b> 그대로 담는다 — 팔레트·카탈로그 길이를 여기서 모르므로 아는 쪽이 자른다.
/// </summary>
public static class CosmeticLoadout
{
    // settings.playerColor.<계정>.<부위> · settings.accessory.<계정>.<슬롯>
    // ⚠ 키는 저장소에 남는 식별자라 바꾸면 이미 저장된 색·치장을 못 읽는다 — 분리하면서도 그대로 뒀다.
    private const string k_playerColorKeyPrefix = "settings.playerColor.";
    private const string k_accessoryKeyPrefix = "settings.accessory.";

    private const string k_localAccount = "local"; // 로그인 전에 고른 값이 갈 자리

    // 팔레트 첫 색 — 여기서는 목록 길이를 모른다. 범위 밖 값은 읽는 쪽(PlayerColorPalette.Get)이 자른다. (#432)
    private const int k_defaultPlayerColor = 0;

    // 인덱스 = EBodyPart. 길이를 enum에서 얻는다 — 부위가 늘어도 여기서 터지지 않게
    private static readonly int[] s_playerColors = new int[
        Enum.GetValues(typeof(EBodyPart)).Length
    ];

    // 인덱스 = EAccessorySlot. 0은 "안 씀"이라 기본값 자체가 안전한 상태다 (#818)
    private static readonly int[] s_accessories = new int[
        Enum.GetValues(typeof(EAccessorySlot)).Length
    ];

    private static string s_account = k_localAccount;

    /// <summary>내 로봇 색이 바뀌었다 — 로비 로스터 보고·초상·팔레트 표시가 되읽는다. 인자는 바뀐 부위. (#432)</summary>
    public static event Action<EBodyPart> OnPlayerColorChanged;

    /// <summary>내 치장이 바뀌었다 — 로비 명부 보고·선택 칸이 되읽는다. 인자는 바뀐 슬롯. (#818)</summary>
    public static event Action<EAccessorySlot> OnAccessoryChanged;

    /// <summary>
    /// 그 부위의 색 인덱스 (#432) — <see cref="PlayerColorPalette"/>의 몇 번째 색인지.
    /// 순수 코스메틱이고, 값의 출처는 여기 하나다: 로비 명부와 게임 씬의 <c>PlayerCosmetics</c>가
    /// 각자 자기 씬의 운반 수단으로 나르되 <b>읽는 값은 이것</b>이다.
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

    /// <summary>그 슬롯에 쓴 카탈로그 인덱스 — <b>0은 안 씀</b>. 카탈로그 길이는 여기서 모른다.</summary>
    public static int GetAccessory(EAccessorySlot slot) => s_accessories[(int)slot];

    public static void SetAccessory(EAccessorySlot slot, int index)
    {
        int clamped = Mathf.Max(0, index);
        if (s_accessories[(int)slot] == clamped)
            return;

        s_accessories[(int)slot] = clamped;
        PlayerPrefs.SetInt(AccessoryKey(slot), clamped);
        OnAccessoryChanged?.Invoke(slot);
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

    /// <summary>클라우드에서 받은 한 벌을 적용한다 — 캐시에도 남긴다. (CosmeticsSaveService)</summary>
    public static void ApplyAccessories(IReadOnlyList<int> accessories)
    {
        if (accessories == null)
            return;

        foreach (EAccessorySlot slot in Enum.GetValues(typeof(EAccessorySlot)))
        {
            int index = (int)slot;
            if (index >= accessories.Count)
                continue;

            int clamped = Mathf.Max(0, accessories[index]);
            s_accessories[index] = clamped;
            PlayerPrefs.SetInt(AccessoryKey(slot), clamped);
            OnAccessoryChanged?.Invoke(slot);
        }
    }

    /// <summary>계정 자리를 갈아탄다 — 캐시에서 그 계정 것을 다시 읽는다. (CosmeticsSaveService)</summary>
    public static void UseAccount(string accountId)
    {
        string next = string.IsNullOrWhiteSpace(accountId) ? k_localAccount : accountId;
        if (s_account == next)
            return;

        s_account = next;
        LoadAccount();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Load()
    {
        // static 이벤트도 함께 리셋한다 — 도메인 리로드를 끄면 이전 플레이의 죽은 구독자가 남아
        // 파괴된 UI를 깨운다. 씬 로드 전이라 이번 플레이의 구독자는 아직 붙지 않았다. (#430)
        OnPlayerColorChanged = null;
        OnAccessoryChanged = null;

        // 로그인 전이라 아직 'local' 자리를 읽는다. 로그인하면 계정 것으로 갈아탄다 (#796 후속)
        s_account = k_localAccount;
        LoadAccount();
    }

    private static void LoadAccount()
    {
        LoadPlayerColors();
        LoadAccessories();
    }

    // 색만 로그인 전에도 계정 자리를 쓴다 — 이미 그 형식으로 저장돼 있어 굳이 바꾸지 않는다 (#432)
    private static string ColorKey(EBodyPart part) =>
        k_playerColorKeyPrefix + s_account + "." + part;

    // 색과 같은 자리 규칙 — 계정별로 갈라 둔다 (#818)
    private static string AccessoryKey(EAccessorySlot slot) =>
        k_accessoryKeyPrefix + s_account + "." + slot;

    // 캐시에서 전 부위를 다시 읽어 적용한다. 저장된 값이 없으면 팔레트 첫 색이다.
    private static void LoadPlayerColors()
    {
        foreach (EBodyPart part in Enum.GetValues(typeof(EBodyPart)))
        {
            s_playerColors[(int)part] = PlayerPrefs.GetInt(ColorKey(part), k_defaultPlayerColor);
            OnPlayerColorChanged?.Invoke(part);
        }
    }

    // 캐시에서 전 슬롯을 다시 읽어 적용한다. 저장된 값이 없으면 0(안 씀)이다. (#818)
    private static void LoadAccessories()
    {
        foreach (EAccessorySlot slot in Enum.GetValues(typeof(EAccessorySlot)))
        {
            s_accessories[(int)slot] = PlayerPrefs.GetInt(AccessoryKey(slot), 0);
            OnAccessoryChanged?.Invoke(slot);
        }
    }
}
