using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Shop 씬 진입점 — 라운드 사이 준비 허브(인게임 로비 역할). 상점 이용(#182) + 호스트 '출동'으로 게임 전환. (#214)
/// 라운드 종료 시 이 씬으로 복귀한다. 루프 = Shop ↔ Game.
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class ShopManager : SceneManagerBase
{
    // 내 몸이 제자리로 가기를 기다리는 상한 — 안 오면 경고하고 진행한다(InGameManager와 같은 방침).
    private const float k_localReadyTimeoutSeconds = 10f;

    private SessionManager Session => App.Net.Session;
    private bool IsServer => NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;
    private bool m_dispatched;

    /// <summary>
    /// 상점 준비 완료 대기 (#656) — <b>내 플레이어가 스폰 지점으로 재배치될 때까지</b> 로딩 화면을 유지한다.
    ///
    /// 상점은 로비와 달리 플레이어를 유지하고 옮긴다(m_spawnPlayers=true). 재배치는 서버가 내
    /// 로드 완료를 보고 RPC로 지시하므로 왕복이 붙는다.
    ///
    /// 플레이어는 destroyWithScene:false라 씬을 넘어와 <b>직전 맵 좌표에 서 있다.</b> 호스트는
    /// Start에서 동기적으로 정리하지만 클라는 그 지시가 네트워크로 와야 해서, 먼저 화면을 내리면
    /// 그 사이가 맵 밖으로 튕겨 나가는 것으로 보인다.
    ///
    /// 프레임 수만 세면(LoadingScreen.k_settleFrames) fps가 높고 RTT가 붙는 빌드에서 진다 —
    /// 에디터에서 재현되지 않던 이유다. 그래서 시간이 아니라 조건을 기다린다.
    /// </summary>
    public override async UniTask WaitUntilReadyAsync(CancellationToken token)
    {
        float deadline = Time.realtimeSinceStartup + k_localReadyTimeoutSeconds;
        await UniTask.WaitUntil(
            () => IsLocallyReady() || Time.realtimeSinceStartup >= deadline,
            cancellationToken: token
        );

        if (!IsLocallyReady())
            Debug.LogWarning(
                $"[ShopManager] 플레이어 재배치를 {k_localReadyTimeoutSeconds}초 내에 확인하지 못했다 — 그대로 진행한다",
                this
            );
    }

    // 내 몸이 도착했고, 서버가 지시한 재배치를 적용했어야 준비 완료다.
    // 로비에서 처음 들어올 때는 재배치가 아니라 새로 스폰되는데(PlayerSpawnManager.SpawnPlayerFor),
    // 그 경우 회차가 0으로 맞아떨어져 몸이 도착하는 즉시 통과한다.
    private static bool IsLocallyReady()
    {
        NetworkManager net = NetworkManager.Singleton;
        if (net == null || !net.IsListening)
            return true; // 세션 밖(오프라인·씬 직접 Play) — 옮겨 줄 서버가 없다

        NetworkClient local = net.LocalClient;
        if (local == null)
            return true;

        NetworkObject player = local.PlayerObject;
        if (player == null)
            return false; // 아직 내 몸이 도착하지 않았다

        PlayerMovement movement = player.GetComponent<PlayerMovement>();
        return movement == null || movement.IsRepositionApplied;
    }

    /// <summary>이미 출동했는가 — 맵 선택 잠금 기준. 판단 근거는 <see cref="MapSelection.IsSelectable"/>. (#578)</summary>
    public bool IsDispatched => m_dispatched;

    // 상점(라운드 사이)도 조인 가능 — 진입 시 잠금 해제. 게임 종료 후 복귀 시에도 다시 열린다.
    private void Start()
    {
        if (!IsServer)
            return;

        Session?.SetLockedAsync(false).Forget();

        DespawnDroppedItems(); // 지난 라운드에 바닥에 버려진 아이템 회수 (#370)
        App.Game.ArrestJudge?.ServerResetRound(); // 개인 진범 체포 집계 초기화 (#739)
        App.Game.WrongfulArrestPenalty?.ServerResetRound(); // 개인 오검거 집계 초기화 (#739 후속)

        NetworkManager.Singleton.SceneManager.OnLoadComplete += HandleLoadComplete;
        ResetPlayer(NetworkManager.Singleton.LocalClientId); // 호스트 자신
    }

    protected override void OnDestroy()
    {
        base.OnDestroy(); // ★ 매니저 등록 해제 유지 (R5)
        if (NetworkManager.Singleton?.SceneManager != null)
            NetworkManager.Singleton.SceneManager.OnLoadComplete -= HandleLoadComplete;
    }

    private void HandleLoadComplete(ulong clientId, string sceneName, LoadSceneMode mode)
    {
        if (sceneName != gameObject.scene.name)
            return;
        if (clientId == NetworkManager.Singleton.LocalClientId)
            return; // 서버는 Start에서
        ResetPlayer(clientId);
    }

    private void ResetPlayer(ulong clientId)
    {
        if (!NetworkManager.Singleton.ConnectedClients.TryGetValue(clientId, out var client))
            return;
        client.PlayerObject?.GetComponent<PlayerHealth>()?.ServerResetState();
        client.PlayerObject?.GetComponent<PlayerItemSupply>()?.ServerClearHeldItems(); // 지급 장비 회수 (#370)
        client.PlayerObject?.GetComponent<PlayerWallet>()?.ServerResetRound(); // 이번 라운드 몫만 초기화 (#484)
        client.PlayerObject?.GetComponent<PlayerKillCredit>()?.ServerResetRound(); // 처치 수 초기화 (#869)
        client.PlayerObject?.GetComponent<PlayerAssistCredit>()?.ServerResetRound(); // 구조 수 초기화 (#739)
    }

    // 주인 없이 바닥에 떨어져 있는 아이템을 정리한다 — 아이템은 destroyWithScene:false로 스폰돼 안 치우면
    // 지난 라운드에 버린 것이 옛 좌표 그대로 따라온다. 손에 든 것은 플레이어 자식이라 여기 안 걸린다
    // (PlayerLoadout 몫). 로비·타이틀의 비슷한 정리는 플레이어를 먼저 내리므로 조건이 달라
    // PlayerSpawnManager가 따로 한다(#395). 조회는 씬 Find가 아니라 NGO 스폰 목록. (#370)
    private static void DespawnDroppedItems()
    {
        NetworkSpawnManager spawnManager = NetworkManager.Singleton.SpawnManager;
        if (spawnManager == null)
            return;

        // 디스폰이 스폰 목록을 건드리므로 스냅샷을 떠서 순회한다.
        int despawned = 0;
        foreach (NetworkObject spawned in new List<NetworkObject>(spawnManager.SpawnedObjectsList))
        {
            if (spawned == null || spawned.transform.parent != null || !spawned.TryGetComponent(out ItemBase _))
                continue;

            spawned.Despawn(true);
            despawned++;
        }

        Debug.Log($"[ShopManager] 바닥 아이템 회수 — {despawned}개");
    }

    /// <summary>호스트 전용 — 출동. 게임 씬으로 전환하며 세션을 잠근다(게임 진행 중 신규 접속 차단).</summary>
    public void Dispatch()
    {
        if (!IsServer || m_dispatched)
            return;
        m_dispatched = true;

        // 상점에서 쓴 돈과 산 물건이 확정되는 지점이라 여기서 한 번 저장한다 (#373).
        // 라운드 종료 저장만 있으면 장비를 다 사고 라운드 중에 끊겼을 때 그 구매가 통째로 사라진다.
        // 상태는 호출 즉시 스냅샷되므로 출동을 붙잡지 않고 던진다.
        SaveService.SaveAsync().Forget();

        Session?.SetLockedAsync(true).Forget();
        MoveToNextScene(EScene.Game);
    }
}
