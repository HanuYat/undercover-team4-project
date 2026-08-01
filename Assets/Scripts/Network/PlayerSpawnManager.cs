using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 연결 승인 + 플레이어 스폰/재배치. (#214/#51)
/// m_spawnPlayers=false(로비·타이틀): 접속만 승인하고 플레이어는 만들지 않는다. 이미 있던 플레이어와
///   바닥에 남은 아이템은 여기서 내린다 — 둘 다 destroyWithScene:false라 씬을 바꿔도 안 사라진다.
/// m_spawnPlayers=true(상점·게임): 진입한 피어에 플레이어가 없으면 스폰, 있으면 스폰 포인트로 재배치.
/// 플레이어는 destroyWithScene:false라 Shop↔Game 루프 내내 유지된다.
/// 게임 씬 진입일 때는 기본 장비 지급까지 여기서 트리거한다 — 피어별 씬 진입을 아는 유일한 지점이라
/// 매 라운드 지급이 이 한 경로로 모인다. (#370)
/// </summary>
public class PlayerSpawnManager : MonoBehaviour
{
    [Tooltip("이 씬에서 플레이어를 스폰/유지하는가 (로비·타이틀은 false — UI 대기)")]
    [SerializeField] private bool m_spawnPlayers = true;

    [SerializeField] private Transform m_spawnPoint;
    [SerializeField] private float m_spreadRadius = 1.5f; // 겹침 방지용 분산 반경(m)

    private NetworkManager m_networkManager;
    private int m_placedCount;

    private void Start()
    {
        m_networkManager = NetworkManager.Singleton;
        if (m_networkManager == null)
        {
            Debug.LogWarning("[PlayerSpawnManager] NetworkManager 없음.");
            return;
        }

        m_networkManager.ConnectionApprovalCallback = OnConnectionApproval;

        if (!m_networkManager.IsServer)
            return;

        if (m_spawnPlayers)
        {
            m_networkManager.SceneManager.OnLoadComplete += HandleLoadComplete;
            EnsureAndPlace(m_networkManager.LocalClientId); // 서버(호스트) 자신
        }
        else
        {
            // 순서 중요 — 플레이어를 먼저 내려야 소지품이 PlayerLoadout을 통해 함께 정리되고,
            // 그 뒤 남는 것(바닥에 버려진 아이템)만 아래에서 쓸어 담는다.
            DespawnAllPlayers();
            DespawnLooseItems();
        }
    }

    private void OnDestroy()
    {
        if (m_networkManager != null)
        {
            m_networkManager.ConnectionApprovalCallback = null;
            if (m_networkManager.SceneManager != null)
                m_networkManager.SceneManager.OnLoadComplete -= HandleLoadComplete;
        }
    }

    /// <summary>
    /// 살아남은 플레이어 오브젝트를 전부 내린다 — 플레이어를 두지 않는 씬(로비·타이틀) 전용. (#395)
    /// 플레이어는 Shop↔Game 루프를 유지하려고 destroyWithScene:false로 스폰되므로, 씬을 바꿔도
    /// 저절로 사라지지 않는다. 라운드 실패로 로비에 돌아오면 대기 화면 뒤에 캐릭터가 남아
    /// 바닥 없는 공간을 떨어지는 것이 그대로 보인다.
    /// </summary>
    private void DespawnAllPlayers()
    {
        // 순회 중에 Despawn하면 ConnectedClientsList가 흔들릴 수 있어 먼저 모아 둔다
        var players = new List<NetworkObject>();
        foreach (NetworkClient client in m_networkManager.ConnectedClientsList)
        {
            if (client.PlayerObject != null)
                players.Add(client.PlayerObject);
        }

        foreach (NetworkObject player in players)
        {
            if (player != null && player.IsSpawned)
                player.Despawn(destroy: true);
        }

        if (players.Count > 0)
            Debug.Log($"[PlayerSpawnManager] 플레이어 {players.Count}개 정리 — 이 씬은 플레이어를 두지 않는다");
    }

    /// <summary>
    /// 씬에 남은 아이템을 전부 내린다 — 플레이어를 두지 않는 씬(로비·타이틀) 전용. (#395)
    /// 바닥에 버린 아이템은 부모가 해제된 서버 소유 월드 오브젝트이고, 아이템 프리팹은
    /// destroyWithScene:false로 스폰되므로(PlayerLoadout) 씬을 바꿔도 그대로 따라온다.
    /// 소지품은 플레이어를 내릴 때 PlayerLoadout이 함께 정리하므로 여기 걸리는 건 버려진 것들이다.
    /// </summary>
    private void DespawnLooseItems()
    {
        // 순회 중에 Despawn하면 SpawnedObjectsList가 흔들린다 — 먼저 모아 둔다
        var items = new List<NetworkObject>();
        foreach (NetworkObject spawned in m_networkManager.SpawnManager.SpawnedObjectsList)
        {
            if (spawned != null && spawned.GetComponent<ItemBase>() != null)
                items.Add(spawned);
        }

        foreach (NetworkObject item in items)
        {
            if (item != null && item.IsSpawned)
                item.Despawn(destroy: true);
        }

        if (items.Count > 0)
            Debug.Log($"[PlayerSpawnManager] 남은 아이템 {items.Count}개 정리 — 이 씬은 아이템을 두지 않는다");
    }

    private void HandleLoadComplete(ulong clientId, string sceneName, LoadSceneMode loadSceneMode)
    {
        if (sceneName != gameObject.scene.name) return;
        if (clientId == m_networkManager.LocalClientId) return; // 서버는 Start에서 처리
        EnsureAndPlace(clientId);
    }

    private void EnsureAndPlace(ulong clientId)
    {
        if (!m_networkManager.ConnectedClients.TryGetValue(clientId, out NetworkClient client))
            return;

        if (client.PlayerObject == null)
            SpawnPlayerFor(clientId);
        else
            RepositionPlayer(client.PlayerObject);

        // 기본 장비는 게임 씬에 들어온 시점에 지급한다 — 상점(허브)은 빈손 대기. (#370)
        // 씬별 인스펙터 플래그를 새로 두지 않고 App의 씬 상태로 가른다(다른 스크립트와 같은 관례).
        // 이 시점엔 sceneLoaded 훅이 이미 CurrentScene을 갱신한 뒤다(Start/OnLoadComplete 모두 그 이후).
        if (App.CurrentScene == EScene.Game)
            client.PlayerObject?.GetComponent<PlayerItemSupply>()?.ServerGrantStartingGear();
    }

    private void SpawnPlayerFor(ulong clientId)
    {
        GameObject prefab = m_networkManager.NetworkConfig.PlayerPrefab;
        if (prefab == null)
        {
            Debug.LogError("[PlayerSpawnManager] NetworkConfig.PlayerPrefab 미설정");
            return;
        }

        (Vector3 pos, Quaternion rot) = NextPose();
        NetworkObject no = Instantiate(prefab, pos, rot).GetComponent<NetworkObject>();
        no.SpawnAsPlayerObject(clientId, destroyWithScene: false); // 루프 내내 유지
        Debug.Log($"[PlayerSpawnManager] 클라이언트 {clientId} 플레이어 스폰: {pos}");
    }

    private void RepositionPlayer(NetworkObject playerObject)
    {
        PlayerMovement movement = playerObject.GetComponent<PlayerMovement>();
        if (movement == null) return;
        (Vector3 pos, Quaternion rot) = NextPose();
        movement.ServerReposition(pos, rot);
    }

    private (Vector3, Quaternion) NextPose()
    {
        Vector3 basePos = m_spawnPoint != null ? m_spawnPoint.position : Vector3.zero;
        Quaternion rot = m_spawnPoint != null ? m_spawnPoint.rotation : Quaternion.identity;
        return (basePos + GetSpreadOffset(m_placedCount++), rot);
    }

    private void OnConnectionApproval(
        NetworkManager.ConnectionApprovalRequest request,
        NetworkManager.ConnectionApprovalResponse response)
    {
        response.Approved = true;
        response.CreatePlayerObject = m_spawnPlayers; // 로비=false → 접속만, 플레이어 안 만듦
        if (m_spawnPlayers && m_spawnPoint != null)
        {
            (response.Position, response.Rotation) = NextPose();
        }
    }

    private Vector3 GetSpreadOffset(int index)
    {
        if (index == 0) return Vector3.zero;
        float angle = (index % 6) * 60f * Mathf.Deg2Rad;
        return new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * m_spreadRadius;
    }
}
