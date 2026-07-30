using System.Collections.Generic;
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
    private SessionManager Session => App.Net.Session;
    private bool IsServer => NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;
    private bool m_dispatched;

    // 상점(라운드 사이)도 조인 가능 — 진입 시 잠금 해제. 게임 종료 후 복귀 시에도 다시 열린다.
    private void Start()
    {
        if (!IsServer)
            return;

        Session?.SetLockedAsync(false).Forget();

        DespawnDroppedItems(); // 지난 라운드에 바닥에 버려진 아이템 회수 (#370)

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
        client.PlayerObject?.GetComponent<PlayerLoadout>()?.ServerClearHeldItems(); // 지급 장비 회수 (#370)
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
        Session?.SetLockedAsync(true).Forget();
        MoveToNextScene(EScene.Game);
    }
}
