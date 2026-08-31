using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Unity.Services.CloudSave;
using Unity.Services.CloudSave.Models;
using UnityEngine;

/// <summary>
/// 로봇 색을 계정에 저장한다 (#432 후속) — <b>정본은 Cloud Save, PlayerPrefs는 계정별 캐시</b>다.
/// 계정 단위 설정(감도·볼륨 등)의 자리 갈아타기도 여기서 함께 일어난다 — 로그인·로그아웃 훅이
/// 여기 하나뿐이고, 색 저장 예약을 막는 가드 안에서 불러야 안전하기 때문이다. (#796 후속)
/// 캐시를 두는 이유는 로드가 비동기라서다: 없으면 켤 때마다 클라우드가 도착할 때까지 기본색이 보이고,
/// 오프라인에서는 색이 아예 없다. 값 자체는 <see cref="GameSettings"/>가 들고 동기로 읽히므로
/// 읽는 쪽(로비 명부·초상·PlayerCosmetics)은 이 클래스를 몰라도 된다.
///
/// static인 이유는 <see cref="SaveService"/>와 같다 — 씬에 실체도 인스펙터 설정도 없다.
/// </summary>
public static class CosmeticsSaveService
{
    // 배포 후 변경 금지. Cloud Save 키는 영숫자·대시·언더스코어만 허용한다.
    private const string k_key = "player_cosmetics";

    // 부위를 연달아 고르면 저장 요청이 부위 수만큼 나간다 — 한 번으로 묶는다.
    private const float k_debounceSeconds = 1f;

    private static bool s_flushQueued;
    private static bool s_hooked;
    private static bool s_applying; // 불러온 값을 넣는 중 — 그대로 되저장하지 않게

    private static bool IsReady => App.Net.Auth != null && App.Net.Auth.IsSignedIn;

    /// <summary>
    /// 로그인 직후 계정 색을 복원한다 — 캐시를 먼저 적용하고, 클라우드에 값이 있으면 그것으로 덮는다.
    /// 클라우드에 아무것도 없으면 지금 값을 한 번 올린다(계정 없이 고른 색을 잃지 않게).
    /// </summary>
    public static async UniTask RestoreAsync()
    {
        if (!IsReady)
            return;

        Hook();

        // ① 캐시 — 즉시. 클라우드 왕복 동안 기본색이 보이지 않게 한다.
        // 색뿐 아니라 계정 단위 설정 전부가 이 자리에서 갈아탄다 (#796 후속) — Apply로 감싸는 이유는
        // 그대로다: 방금 읽은 캐시를 되올리지 않게 저장 예약을 막는다.
        Apply(() =>
        {
            GameSettings.UseAccount(App.Net.Auth.PlayerId);
            CosmeticLoadout.UseAccount(App.Net.Auth.PlayerId);
            CosmeticInventory.UseAccount(App.Net.Auth.PlayerId);
        });

        // ② 클라우드 — 있으면 이것이 정본이다.
        CosmeticsSaveData data = await ReadAsync();
        if (data == null)
        {
            await WriteAsync(Capture()); // 첫 로그인 — 지금 색을 계정에 올린다
            return;
        }

        Apply(() =>
        {
            CosmeticLoadout.ApplyPlayerColors(data.Colors);
            CosmeticLoadout.ApplyAccessories(data.Accessories); // v1 레코드면 null — 그쪽에서 무시한다

            // v2 이하 레코드는 보유함이 없다 — 빈 보유함으로 두면 기본 지급 세트만 남고,
            // 자판기로 얻은 것이 있었다면 애초에 v3로 저장됐을 것이므로 잃는 것이 없다. (#818 D)
            CosmeticInventory.Apply(data.Owned, data.Tokens);
            CosmeticLoadout.ApplyCrosshairSettings(data.Crosshair); // v3 이하 레코드면 null — 기본값 유지
        });
    }

    // CosmeticLoadout이 부위마다 변경 이벤트를 내므로, 그대로 두면 방금 받은 값을 되올린다.
    private static void Apply(Action apply)
    {
        s_applying = true;
        try
        {
            apply();
        }
        finally
        {
            s_applying = false;
        }
    }

    /// <summary>계정이 바뀌면 캐시 기준을 되돌린다 — 다음 로그인이 자기 값을 다시 불러온다.</summary>
    public static void OnSignedOut() =>
        Apply(() =>
        {
            GameSettings.UseAccount(null);
            CosmeticLoadout.UseAccount(null);
            CosmeticInventory.UseAccount(null);
        });

    // 색이 바뀔 때마다 저장을 예약한다. 구독은 한 번만.
    private static void Hook()
    {
        if (s_hooked)
            return;

        s_hooked = true;
        CosmeticLoadout.OnPlayerColorChanged += HandleColorChanged;
        CosmeticLoadout.OnAccessoryChanged += HandleAccessoryChanged;
        CosmeticLoadout.OnCrosshairSettingsChanged += HandleCrosshairChanged;
        CosmeticInventory.OnOwnedChanged += HandleInventoryChanged;
        CosmeticInventory.OnTokensChanged += HandleInventoryChanged;
    }

    private static void HandleColorChanged(EBodyPart part)
    {
        if (!s_applying)
            QueueSave();
    }

    private static void HandleAccessoryChanged(EAccessorySlot slot)
    {
        if (!s_applying)
            QueueSave();
    }

    private static void HandleCrosshairChanged()
    {
        if (!s_applying)
            QueueSave();
    }

    // 뽑기로 보유함이 늘거나 토큰이 오갈 때 — 색과 같은 예약을 탄다. (#818 D)
    private static void HandleInventoryChanged()
    {
        if (!s_applying)
            QueueSave();
    }

    private static void QueueSave()
    {
        if (s_flushQueued || !IsReady)
            return;

        s_flushQueued = true;
        FlushAsync().Forget();
    }

    private static async UniTaskVoid FlushAsync()
    {
        await UniTask.Delay(TimeSpan.FromSeconds(k_debounceSeconds));
        s_flushQueued = false;

        if (IsReady)
            await WriteAsync(Capture());
    }

    private static CosmeticsSaveData Capture()
    {
        var parts = (EBodyPart[])Enum.GetValues(typeof(EBodyPart));
        var colors = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            colors[(int)parts[i]] = CosmeticLoadout.GetPlayerColor(parts[i]);

        var slots = (EAccessorySlot[])Enum.GetValues(typeof(EAccessorySlot));
        var accessories = new int[slots.Length];
        for (int i = 0; i < slots.Length; i++)
            accessories[(int)slots[i]] = CosmeticLoadout.GetAccessory(slots[i]);

        return new CosmeticsSaveData
        {
            Colors = colors,
            Accessories = accessories,
            Owned = CosmeticInventory.Capture(),
            Tokens = CosmeticInventory.Tokens,
            Crosshair = CosmeticLoadout.GetCrosshairSettings(),
        };
    }

    private static async UniTask<CosmeticsSaveData> ReadAsync()
    {
        try
        {
            Dictionary<string, Item> loaded = await CloudSaveService.Instance.Data.Player.LoadAsync(
                new HashSet<string> { k_key }
            );

            if (!loaded.TryGetValue(k_key, out Item item))
                return null; // 이 계정으로 저장한 적이 없다

            var data = JsonUtility.FromJson<CosmeticsSaveData>(item.Value.GetAs<string>());

            // 모르는(미래) 포맷은 읽지 않는다 — 색이 조용히 뒤섞이는 쪽이 기본색보다 나쁘다.
            // 반대로 옛 포맷은 버리지 않는다 (#818): v1은 색만 있으므로 색을 살리고 치장은 기본값으로 둔다.
            if (
                data == null
                || data.Colors == null
                || data.Version < 1
                || data.Version > CosmeticsSaveData.k_version
            )
            {
                Debug.LogWarning(
                    $"[커스터마이징] 읽을 수 없는 포맷이라 무시한다 — 저장 {data?.Version}, 현재 {CosmeticsSaveData.k_version}"
                );
                return null;
            }

            if (data.Version < CosmeticsSaveData.k_version)
                Debug.Log(
                    $"[커스터마이징] v{data.Version} 레코드를 읽었다 — 그 판에 있던 것만 복원하고 나머지는 기본값으로 둔다"
                );

            Debug.Log($"[커스터마이징] 계정 색을 불러왔다 — {string.Join(",", data.Colors)}");
            return data;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[커스터마이징] 조회 실패(캐시 값으로 진행): {ex.Message}");
            return null;
        }
    }

    // 실패는 경고만 남긴다 — 저장 실패로 게임 흐름을 막지 않는다 (SaveService와 같은 방침).
    private static async UniTask WriteAsync(CosmeticsSaveData data)
    {
        try
        {
            await CloudSaveService.Instance.Data.Player.SaveAsync(
                new Dictionary<string, object> { { k_key, JsonUtility.ToJson(data) } }
            );
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[커스터마이징] 저장 실패(무시하고 진행): {ex.Message}");
        }
    }
}

/// <summary>계정에 올리는 커스터마이징 한 벌 (#432 후속). 필드를 늘리면 <see cref="k_version"/>을 올릴 것.</summary>
[Serializable]
public class CosmeticsSaveData
{
    // v2에서 치장(Accessories), v3에서 보유함·토큰(Owned·Tokens), v4에서 크로스헤어(Crosshair)가 추가됐다.
    // 옛 레코드는 버리지 않고 있는 것만 살린다 (CosmeticsSaveService.ReadAsync)
    public const int k_version = 4;

    public int Version = k_version;

    /// <summary>인덱스 = <see cref="EBodyPart"/>, 값 = 팔레트 색 인덱스.</summary>
    public int[] Colors;

    /// <summary>인덱스 = <see cref="EAccessorySlot"/>, 값 = 카탈로그 인덱스(0 = 안 씀). v1에는 없다.</summary>
    public int[] Accessories;

    /// <summary>자판기로 얻은 치장 (#818 D) — 기본 지급 세트는 카탈로그가 정하므로 여기 없다. v2 이하에는 없다.</summary>
    public List<CosmeticSlotOwnership> Owned;

    /// <summary>남은 뽑기 토큰 (#818 D). v2 이하에는 없어 0으로 읽힌다.</summary>
    public int Tokens;

    /// <summary>크로스헤어 설정 (#945). v3 이하에는 없어 null로 읽힌다 — 그 경우 기본값을 유지한다.</summary>
    public CrosshairSettings Crosshair;
}
