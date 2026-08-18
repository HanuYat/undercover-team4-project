using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using Unity.Services.CloudSave;
using Unity.Services.CloudSave.Models;
using UnityEngine;

/// <summary>
/// 판 진행 상황 세이브/로드 (#373) — UGS Cloud Save에 키 하나로 올린다.
/// 매니저가 아니라 static이다(SessionFlow·GameSettings와 같은 부류) — 씬에 실체가 없고 인스펙터 설정도 없어서
/// App에 올릴 이유가 없다. 그래서 배선할 프리팹도 늘지 않는다.
///
/// <b>세이브는 호스트 계정의 것이다.</b> Cloud Save는 로그인한 플레이어 단위 저장소라, 저장도 로드도 서버(호스트)에서만
/// 일어난다. 다른 사람이 방을 만들면 그 사람의 세이브로 이어진다 — 개인 지갑을 clientId가 아니라 PlayerId로
/// 기록해 두는 이유이기도 하다(누가 방장이든 각자 자기 잔액을 되찾는다).
///
/// 흐름:
///  · 타이틀 — <see cref="RefreshAsync"/>로 세이브 유무를 미리 조회해 둔다. 없으면 '이어하기'가 사유를 띄운다 (#704).
///  · 이어하기 <see cref="UseSave"/> / 새로 시작 <see cref="StartFresh"/> — <b>세션 생성 전에</b> 부른다.
///    상주 홀더는 세션이 켜지는 순간(OnServerStarted) 스폰되면서 <see cref="Pending"/>을 읽기 때문이다.
///  · 저장 <see cref="SaveAsync"/> — 씬을 넘는 두 길목에서 각각 1회(서버·호스트).
///     ① 라운드 성공 종료 → 상점 (RoundEndResetter) — 정산까지 반영된 상태
///     ② 상점 출동 → 게임 (ShopManager.Dispatch) — 상점에서 쓴 돈과 산 물건이 반영된 상태
///    ②가 없으면 상점에서 장비를 다 사고 라운드 중에 끊겼을 때 그 구매가 통째로 사라진다.
///  · 라운드 실패 종료 — <see cref="DeleteAsync"/>. 판이 끝났으니 이어할 것이 없다.
///
/// 저장·로드 실패는 게임 흐름을 막지 않는다 — 경고만 남기고 진행한다(세이브가 없으면 새 판일 뿐이다).
/// </summary>
public static class SaveService
{
    // 배포 후 변경 금지 — 바꾸면 기존 세이브를 못 읽는다.
    // Cloud Save 키는 영숫자·대시·언더스코어만 허용한다(마침표를 쓰면 validation error).
    private const string k_key = "session_progress";

    // 마지막으로 클라우드에서 읽은(또는 방금 쓴) 세이브. 두 가지 용도다:
    //  · 타이틀의 '이어하기' 노출 판정
    //  · 저장 시 "이번에 접속하지 않은 사람의 지갑" 출처 — 없으면 빠진 사람 몫이 저장할 때마다 사라진다
    private static SessionSaveData s_known;

    // 이번 세션 시작에 적용할 세이브 — '이어하기'로 시작했을 때만 채워진다. null이면 새 판이다.
    public static SessionSaveData Pending { get; private set; }

    /// <summary>이어할 세이브가 있는가 — <see cref="RefreshAsync"/>로 조회한 결과.</summary>
    public static bool HasSave => s_known != null;

    /// <summary>이어하면 시작할 라운드 번호 — 타이틀 표시용. 세이브가 없으면 0.</summary>
    public static int SavedRound => s_known?.Round ?? 0;

    // 이번 세션에서 이미 복원한 지갑 — 플레이어가 다시 스폰돼도 두 번 지급하지 않게 한다.
    private static readonly HashSet<string> s_restoredWallets = new HashSet<string>();

    /// <summary>
    /// 클라우드에서 세이브를 읽어 둔다 — 타이틀에서 '이어하기' 노출을 정하기 위해. 읽기만 하고 적용하지는 않는다.
    /// </summary>
    /// <returns>이어할 세이브가 있으면 true.</returns>
    public static async UniTask<bool> RefreshAsync()
    {
        s_known = null;

        if (!IsReady)
            return false;

        try
        {
            Dictionary<string, Item> loaded = await CloudSaveService.Instance.Data.Player.LoadAsync(
                new HashSet<string> { k_key }
            );

            if (!loaded.TryGetValue(k_key, out Item item))
            {
                Debug.Log("[세이브] 저장된 판이 없다 — 새 판만 가능");
                return false;
            }

            var data = JsonUtility.FromJson<SessionSaveData>(item.Value.GetAs<string>());

            // 포맷이 다르면 읽지 않는다 — 필드가 어긋난 채 복원하면 값이 조용히 뒤섞인다.
            if (data == null || data.Version != SessionSaveData.k_version)
            {
                Debug.LogWarning(
                    $"[세이브] 포맷 버전이 달라 무시한다 — 저장 {data?.Version}, 현재 {SessionSaveData.k_version}"
                );
                return false;
            }

            s_known = data;
            Debug.Log($"[세이브] 불러옴 — {data.Round}라운드, 팀 자금 {data.TeamFund}, 지갑 {data.Players.Length}명");
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[세이브] 조회 실패(새 판으로 진행): {ex.Message}");
            return false;
        }
    }

    /// <summary>불러온 세이브로 다음 세션을 시작한다 — <b>세션 생성 전에</b> 부를 것.</summary>
    public static void UseSave()
    {
        s_restoredWallets.Clear();
        Pending = s_known;

        if (Pending == null)
            Debug.LogWarning("[세이브] 이어할 세이브가 없어 새 판으로 시작한다");
    }

    /// <summary>세이브를 적용하지 않고 새 판으로 시작한다 — <b>세션 생성 전에</b> 부를 것.</summary>
    /// <remarks>
    /// 클라우드의 세이브를 여기서 지우지는 않는다 — 새 판이 첫 출동을 하면 어차피 덮어쓰므로,
    /// 실수로 눌렀을 때 아직 아무 것도 대체하지 못한 채 지난 판이 날아가는 상황만 막는다.
    /// </remarks>
    public static void StartFresh()
    {
        s_restoredWallets.Clear();
        Pending = null;
    }

    /// <summary>
    /// 지금 판 상태를 저장한다 — 씬을 넘는 길목에서 서버(호스트)가 부른다.
    /// 상주 홀더와 접속 중인 지갑을 훑어 한 벌로 만든 뒤 덮어쓴다.
    /// <b>상태는 호출 즉시 스냅샷</b>되므로(await 이전) 씬 전환 직전에 Forget()으로 던져도 값이 흔들리지 않는다.
    /// </summary>
    public static async UniTask SaveAsync()
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm != null && nm.IsListening && !nm.IsServer)
        {
            Debug.LogWarning("[세이브] 저장은 서버(호스트)에서만");
            return;
        }

        if (!IsReady)
            return;

        await WriteAsync(Capture());
    }

    // 세이브 한 벌을 키에 덮어쓴다. 실패는 경고만 남긴다 — 저장 실패로 게임 흐름을 막지 않는다.
    private static async UniTask WriteAsync(SessionSaveData data)
    {
        try
        {
            await CloudSaveService.Instance.Data.Player.SaveAsync(
                new Dictionary<string, object> { { k_key, JsonUtility.ToJson(data) } }
            );

            // 방금 쓴 값이 곧 "알고 있는 세이브"다 — 다음 저장에서 빠진 사람 몫의 출처가 된다.
            s_known = data;
            Debug.Log($"[세이브] 저장 완료 — {data.Round}라운드, 팀 자금 {data.TeamFund}, 지갑 {data.Players.Length}명");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[세이브] 저장 실패(무시하고 진행): {ex.Message}");
        }
    }

    /// <summary>세이브를 지운다 — 라운드 실패로 판이 끝났을 때. 이어할 판이 없어졌다는 뜻이다.</summary>
    public static async UniTask DeleteAsync()
    {
        s_known = null;
        Pending = null;
        s_restoredWallets.Clear();

        if (!IsReady)
            return;

        try
        {
            await CloudSaveService.Instance.Data.Player.DeleteAsync(k_key);
            Debug.Log("[세이브] 라운드 실패 — 저장된 판을 지웠다");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[세이브] 삭제 실패(무시): {ex.Message}");
        }
    }

    /// <summary>
    /// 이 PlayerId의 저장된 잔액을 <b>한 번만</b> 꺼낸다 — 플레이어 지갑이 스폰하며 서버에서 부른다.
    /// 한 번만인 이유: 플레이어 오브젝트는 로비를 거치면 다시 스폰되는데, 그때마다 복원하면
    /// 그 사이에 번 돈이 저장 시점 값으로 되돌아간다.
    /// </summary>
    public static bool TryTakeWalletBalance(string playerId, out int balance)
    {
        balance = 0;

        if (Pending?.Players == null || string.IsNullOrEmpty(playerId))
            return false;

        if (!s_restoredWallets.Add(playerId))
            return false;

        foreach (PlayerSaveEntry entry in Pending.Players)
        {
            if (entry.PlayerId != playerId)
                continue;

            balance = entry.Balance;
            return true;
        }

        // 세이브에 없는 사람 — 이 판에 새로 합류했다는 뜻이라 0에서 시작한다.
        return false;
    }

    // Cloud Save는 로그인한 플레이어 단위 저장소다 — 로그인 전에는 호출 자체가 성립하지 않는다.
    private static bool IsReady => App.Net.Auth != null && App.Net.Auth.IsSignedIn;

    // 지금 살아 있는 상주 홀더와 지갑을 한 벌로 모은다. 홀더가 없으면(세션 밖 단독 Play) 기본값이 실린다.
    private static SessionSaveData Capture()
    {
        RoundProgress progress = App.Game.RoundProgress;
        TeamFund fund = App.Game.TeamFund;
        MapSelection maps = App.Game.MapSelection;

        var data = new SessionSaveData
        {
            Round = progress != null ? progress.Current : RoundProgress.k_firstRound,
            TeamFund = fund != null ? fund.Balance : 0,
            MapIndex = maps != null ? maps.SelectedIndex : 0,
            Players = CapturePlayers(),
        };

        ShopPurchases purchases = App.Game.ShopPurchases;
        if (purchases == null)
            return data;

        var carried = new List<string>(purchases.Carried.Count);
        foreach (ItemBase item in purchases.Carried)
        {
            string id = SaveItemLookup.GetId(item);
            if (!string.IsNullOrEmpty(id))
                carried.Add(id);
        }
        data.CarriedItems = carried.ToArray();

        var installables = new List<string>(purchases.Installables.Count);
        foreach (EInstallable installable in purchases.Installables)
            installables.Add(installable.ToString());
        data.Installables = installables.ToArray();

        return data;
    }

    // 접속 중인 사람은 지금 잔액으로 덮고, 이번 판에 없던 사람은 마지막으로 알던 값을 그대로 남긴다.
    // 잠깐 빠진 사람의 돈이 사라지지 않게 하는 것이 이 병합의 목적이다.
    private static PlayerSaveEntry[] CapturePlayers()
    {
        var byPlayerId = new Dictionary<string, int>();

        if (s_known?.Players != null)
        {
            foreach (PlayerSaveEntry entry in s_known.Players)
            {
                if (!string.IsNullOrEmpty(entry.PlayerId))
                    byPlayerId[entry.PlayerId] = entry.Balance;
            }
        }

        NetworkManager nm = NetworkManager.Singleton;
        if (nm != null && nm.IsServer)
        {
            foreach (NetworkClient client in nm.ConnectedClientsList)
            {
                PlayerWallet wallet = client.PlayerObject != null
                    ? client.PlayerObject.GetComponent<PlayerWallet>()
                    : null;

                // PlayerId는 오너가 보고해야 서버가 안다 — 아직 안 왔으면 이번 저장에서는 건너뛴다
                // (마지막으로 알던 값이 위에서 이미 들어가 있어 잔액이 사라지지는 않는다).
                if (wallet == null || string.IsNullOrEmpty(wallet.OwnerPlayerId))
                    continue;

                byPlayerId[wallet.OwnerPlayerId] = wallet.Balance;
            }
        }

        var result = new PlayerSaveEntry[byPlayerId.Count];
        int index = 0;
        foreach (KeyValuePair<string, int> pair in byPlayerId)
            result[index++] = new PlayerSaveEntry { PlayerId = pair.Key, Balance = pair.Value };

        return result;
    }

#if UNITY_EDITOR
    /// <summary>
    /// 개발 도구 전용 — 마지막으로 조회·저장한 세이브의 <b>사본</b>. SaveDevWindow가 편집 폼을 채울 때 읽는다.
    /// 원본을 내보내면 창에서 만진 값이 다음 저장의 지갑 병합 출처(s_known)를 조용히 오염시킨다.
    /// </summary>
    public static SessionSaveData DevKnown =>
        s_known == null ? null : JsonUtility.FromJson<SessionSaveData>(JsonUtility.ToJson(s_known));

    /// <summary>
    /// 개발 도구 전용 — 손으로 만든 세이브를 클라우드에 덮어쓴다 (SaveDevWindow).
    /// 실제 저장과 같은 키·같은 직렬화를 쓰므로, 올라간 뒤의 흐름은 진짜 세이브와 구분되지 않는다.
    /// </summary>
    public static async UniTask DevOverwriteAsync(SessionSaveData data)
    {
        if (data == null || !IsReady)
        {
            Debug.LogWarning("[세이브] 개발용 덮어쓰기 불가 — 로그인된 플레이 모드에서만 된다");
            return;
        }

        await WriteAsync(data);
    }
#endif

    // 도메인 리로드를 꺼도 이전 플레이의 세이브 상태가 남지 않도록 리셋 (App.ResetStatics와 같은 이유)
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        s_known = null;
        Pending = null;
        s_restoredWallets.Clear();
    }
}
