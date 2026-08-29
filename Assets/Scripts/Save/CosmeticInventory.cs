using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 계정이 가진 치장과 뽑기 토큰 (#818 D) — <b>정본은 Cloud Save, PlayerPrefs는 계정별 캐시</b>다.
/// 저장·복원은 <see cref="CosmeticsSaveService"/>가 색·치장과 한 레코드로 묶어 나른다.
///
/// <b>기본 지급 세트는 여기 담기지 않는다.</b> 카탈로그의 <c>DefaultOwned</c>가 정하고
/// <see cref="IsOwned"/>가 합쳐 본다 — 담아 두면 세트를 고쳐도 이미 만든 계정에는 반영되지 않는다.
/// 그래서 이 클래스가 들고 있는 것은 <b>뽑아서 얻은 것</b>뿐이다.
///
/// 보유 여부를 비트로 두는 이유는 항목이 슬롯당 30개를 넘고 앞으로도 늘기 때문이다. 32칸씩 묶어
/// 담으므로 한 슬롯이 32개를 넘어도 포맷이 깨지지 않는다 — 머리카락이 이미 31개다.
///
/// static인 이유는 <see cref="GameSettings"/>와 같다 — 씬에 실체도 인스펙터 설정도 없다.
/// 계정 자리 갈아타기도 그쪽과 같은 규칙이고, 부르는 곳은 <see cref="CosmeticsSaveService"/> 하나다.
/// </summary>
public static class CosmeticInventory
{
    private const string k_ownedKeyPrefix = "cosmetic.owned.";
    private const string k_tokenKeyPrefix = "cosmetic.tokens.";
    private const string k_localAccount = "local"; // 로그인 전에 얻은 것이 갈 자리 (GameSettings와 같은 규칙)

    private const int k_bitsPerChunk = 32;

    private static readonly int[][] s_owned = new int[
        Enum.GetValues(typeof(EAccessorySlot)).Length
    ][];
    private static string s_account = k_localAccount;
    private static int s_tokens;

    /// <summary>보유함이 바뀌었다 — 선택 칸이 잠금 표시를 다시 그린다.</summary>
    public static event Action OnOwnedChanged;

    /// <summary>토큰 수가 바뀌었다 — 자판기 안내가 다시 읽는다.</summary>
    public static event Action OnTokensChanged;

    /// <summary>남은 뽑기 토큰. 라운드를 클리어하면 1개씩 쌓인다.</summary>
    public static int Tokens => s_tokens;

    // static 이벤트·값을 함께 리셋한다 — 도메인 리로드를 끄면 이전 플레이의 죽은 구독자와
    // 값이 남는다 (GameSettings.Load와 같은 방침).
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Load()
    {
        OnOwnedChanged = null;
        OnTokensChanged = null;

        s_pendingReward = 0;
        s_account = k_localAccount;
        LoadAccount();
    }

    /// <summary>
    /// 그 항목을 쓸 수 있는가 — <b>기본 지급 세트와 뽑아서 얻은 것을 합쳐</b> 본다 (#818 D).
    /// 카탈로그가 없으면 뽑아 얻은 것만 본다("안 씀"은 늘 참).
    /// </summary>
    public static bool IsOwned(AccessoryCatalog catalog, EAccessorySlot slot, int index)
    {
        if (index <= 0)
            return true;

        if (catalog != null && catalog.IsDefaultOwned(slot, index))
            return true;

        return HasBit(slot, index);
    }

    /// <summary>
    /// 뽑아서 얻은 것으로 담는다 — <b>새로 얻었으면 true</b>, 이미 갖고 있었으면 false다.
    /// 자판기가 중복 환급을 판단하는 근거가 이 반환값이다.
    ///
    /// 기본 지급 세트인지는 보지 않는다 — 그 판단은 뽑기 풀을 고르는 쪽(<see cref="IsOwned"/>)의 몫이고,
    /// 여기서 또 걸러 내면 "담았는데 false"와 "안 담았는데 false"가 섞여 환급 판정이 흐려진다.
    /// </summary>
    public static bool Grant(EAccessorySlot slot, int index)
    {
        if (index <= 0 || HasBit(slot, index))
            return false;

        SetBit(slot, index);
        SaveOwned(slot);
        OnOwnedChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// 안 가진 것을 입고 있으면 벗긴다 (#818 D) — 해금이 생기기 전에 고른 값과, 기본 지급 세트에서
    /// 빠진 항목이 여기 걸린다. <b>부르는 쪽이 카탈로그를 넘겨야</b> 하므로 카탈로그를 쥔 두 곳
    /// (내 로봇이 스폰될 때·선택 칸이 열릴 때)에서 부른다. 이미 정상이면 아무것도 하지 않는다.
    /// </summary>
    public static void SanitizeEquipped(AccessoryCatalog catalog)
    {
        if (catalog == null)
            return;

        foreach (EAccessorySlot slot in Enum.GetValues(typeof(EAccessorySlot)))
        {
            int index = CosmeticLoadout.GetAccessory(slot);
            if (index <= 0 || IsOwned(catalog, slot, index))
                continue;

            Debug.Log($"[치장] 가지지 않은 {slot} {index}번을 입고 있어 벗긴다 (#818 D)");
            CosmeticLoadout.SetAccessory(slot, 0);
        }
    }

    /// <summary>
    /// 아직 축하하지 못한 지급분 (#850) — 정산에서 적어 두고 상점에서 소비한다.
    ///
    /// 저장하지 않는다: 이 값은 <b>보유량이 아니라 "이번에 알릴 것이 남았는가"</b>다. 토큰 자체는
    /// <see cref="AddTokens"/>가 이미 계정에 넣었으므로, 알림을 못 보고 껐다고 잃는 것은 없다.
    /// </summary>
    private static int s_pendingReward;

    /// <summary>축하할 지급분을 적어 둔다 — 상점에 들어갈 때까지 쌓인다.</summary>
    public static void QueueRewardNotice(int count)
    {
        if (count > 0)
            s_pendingReward += count;
    }

    /// <summary>적어 둔 지급분을 가져가며 비운다 — 두 번 축하하지 않는다.</summary>
    public static int ClaimRewardNotice()
    {
        int claimed = s_pendingReward;
        s_pendingReward = 0;
        return claimed;
    }

    /// <summary>토큰을 더한다 — 라운드 클리어 지급. 0 이하는 무시한다.</summary>
    public static void AddTokens(int count)
    {
        if (count <= 0)
            return;

        s_tokens += count;
        SaveTokens();
        OnTokensChanged?.Invoke();
    }

    /// <summary>토큰 1개를 쓴다 — 없으면 false고 아무것도 바뀌지 않는다.</summary>
    public static bool TrySpendToken()
    {
        if (s_tokens <= 0)
            return false;

        s_tokens--;
        SaveTokens();
        OnTokensChanged?.Invoke();
        return true;
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

    /// <summary>계정에 올릴 한 벌을 뜬다. (CosmeticsSaveService)</summary>
    public static List<CosmeticSlotOwnership> Capture()
    {
        var captured = new List<CosmeticSlotOwnership>();
        foreach (EAccessorySlot slot in Enum.GetValues(typeof(EAccessorySlot)))
        {
            int[] bits = s_owned[(int)slot];
            if (bits == null || bits.Length == 0)
                continue;

            captured.Add(
                new CosmeticSlotOwnership { Slot = (int)slot, Bits = (int[])bits.Clone() }
            );
        }

        return captured;
    }

    /// <summary>
    /// 클라우드에서 받은 한 벌을 적용한다 — 캐시에도 남긴다. (CosmeticsSaveService)
    ///
    /// <b>합치지 않고 덮어쓴다.</b> 계정이 정본이므로, 이 기기에서만 얻은 것이 있으면 그쪽이 틀린 값이다
    /// (기기를 옮겨 다니면 합치기는 지운 적 없는 항목이 되살아나는 쪽으로만 어긋난다).
    /// 모르는 슬롯 번호는 건너뛴다 — 슬롯이 줄어든 빌드로 옛 레코드를 읽는 경우다.
    /// </summary>
    public static void Apply(IList<CosmeticSlotOwnership> owned, int tokens)
    {
        int slotCount = s_owned.Length;
        for (int i = 0; i < slotCount; i++)
            s_owned[i] = null;

        if (owned != null)
            foreach (CosmeticSlotOwnership entry in owned)
            {
                if (entry == null || entry.Slot < 0 || entry.Slot >= slotCount)
                    continue;

                s_owned[entry.Slot] = entry.Bits == null ? null : (int[])entry.Bits.Clone();
            }

        s_tokens = Mathf.Max(0, tokens);

        foreach (EAccessorySlot slot in Enum.GetValues(typeof(EAccessorySlot)))
            SaveOwned(slot);

        SaveTokens();
        OnOwnedChanged?.Invoke();
        OnTokensChanged?.Invoke();
    }

    private static bool HasBit(EAccessorySlot slot, int index)
    {
        int[] bits = s_owned[(int)slot];
        int chunk = index / k_bitsPerChunk;
        if (bits == null || chunk >= bits.Length)
            return false;

        return (bits[chunk] & (1 << (index % k_bitsPerChunk))) != 0;
    }

    private static void SetBit(EAccessorySlot slot, int index)
    {
        int chunk = index / k_bitsPerChunk;
        int[] bits = s_owned[(int)slot];

        if (bits == null)
            bits = new int[chunk + 1];
        else if (chunk >= bits.Length)
            Array.Resize(ref bits, chunk + 1);

        bits[chunk] |= 1 << (index % k_bitsPerChunk);
        s_owned[(int)slot] = bits;
    }

    // 캐시에서 이 계정의 보유함·토큰을 다시 읽는다. 없으면 빈 보유함이다 —
    // 기본 지급 세트는 카탈로그가 들고 있으므로 여기서 채울 것이 없다.
    private static void LoadAccount()
    {
        foreach (EAccessorySlot slot in Enum.GetValues(typeof(EAccessorySlot)))
            s_owned[(int)slot] = ParseBits(PlayerPrefs.GetString(OwnedKey(slot), string.Empty));

        s_tokens = Mathf.Max(0, PlayerPrefs.GetInt(TokenKey(), 0));

        OnOwnedChanged?.Invoke();
        OnTokensChanged?.Invoke();
    }

    // PlayerPrefs에는 int 배열을 담을 수 없어 쉼표로 잇는다. 빈 칸은 빈 문자열이다.
    private static int[] ParseBits(string stored)
    {
        if (string.IsNullOrEmpty(stored))
            return null;

        string[] parts = stored.Split(',');
        var bits = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            int.TryParse(parts[i], out bits[i]);

        return bits;
    }

    private static void SaveOwned(EAccessorySlot slot)
    {
        int[] bits = s_owned[(int)slot];
        PlayerPrefs.SetString(
            OwnedKey(slot),
            bits == null || bits.Length == 0
                ? string.Empty
                : string.Join(",", Array.ConvertAll(bits, b => b.ToString()))
        );
    }

    private static void SaveTokens() => PlayerPrefs.SetInt(TokenKey(), s_tokens);

    // 색·치장과 같은 자리 규칙 — 계정별로 갈라 둔다 (GameSettings.AccessoryKey)
    private static string OwnedKey(EAccessorySlot slot) =>
        k_ownedKeyPrefix + s_account + "." + slot;

    private static string TokenKey() => k_tokenKeyPrefix + s_account;
}

/// <summary>
/// 한 슬롯의 보유 비트 (#818 D). 32칸씩 묶여 있고 비트 자리가 곧 카탈로그 인덱스다.
/// <c>JsonUtility</c>가 들쭉날쭉한 배열을 직렬화하지 못해 슬롯마다 클래스 하나로 나눠 담는다.
/// </summary>
[Serializable]
public class CosmeticSlotOwnership
{
    /// <summary><see cref="EAccessorySlot"/> 값.</summary>
    public int Slot;

    /// <summary>32칸 묶음. 인덱스 0("안 씀") 자리는 쓰지 않는다 — 늘 보유이므로 담을 필요가 없다.</summary>
    public int[] Bits;
}
