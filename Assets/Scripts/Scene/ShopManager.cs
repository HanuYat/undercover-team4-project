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
        client.PlayerObject?.GetComponent<PlayerData>()?.ServerResetState();
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
