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
        Apply(() => GameSettings.UseAccount(App.Net.Auth.PlayerId));

        // ② 클라우드 — 있으면 이것이 정본이다.
        CosmeticsSaveData data = await ReadAsync();
        if (data == null)
        {
            await WriteAsync(Capture()); // 첫 로그인 — 지금 색을 계정에 올린다
            return;
        }

        Apply(() => GameSettings.ApplyPlayerColors(data.Colors));
    }

    // GameSettings가 부위마다 변경 이벤트를 내므로, 그대로 두면 방금 받은 값을 되올린다.
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
    public static void OnSignedOut() => Apply(() => GameSettings.UseAccount(null));

    // 색이 바뀔 때마다 저장을 예약한다. 구독은 한 번만.
    private static void Hook()
    {
        if (s_hooked)
            return;

        s_hooked = true;
        GameSettings.OnPlayerColorChanged += HandleColorChanged;
    }

    private static void HandleColorChanged(EBodyPart part)
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
            colors[(int)parts[i]] = GameSettings.GetPlayerColor(parts[i]);

        return new CosmeticsSaveData { Colors = colors };
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

            // 포맷이 어긋나면 읽지 않는다 — 색이 조용히 뒤섞이는 쪽이 기본색보다 나쁘다.
            if (data == null || data.Version != CosmeticsSaveData.k_version || data.Colors == null)
            {
                Debug.LogWarning(
                    $"[커스터마이징] 포맷이 달라 무시한다 — 저장 {data?.Version}, 현재 {CosmeticsSaveData.k_version}"
                );
                return null;
            }

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
    public const int k_version = 1;

    public int Version = k_version;

    /// <summary>인덱스 = <see cref="EBodyPart"/>, 값 = 팔레트 색 인덱스.</summary>
    public int[] Colors;
}
