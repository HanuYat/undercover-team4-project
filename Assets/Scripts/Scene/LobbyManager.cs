using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Lobby 씬 진입점 — 세션 생성 후 최초 대기 공간. 호스트 '시작'으로 상점(허브)으로 넘어간다. (#214)
/// 라운드 루프는 Shop↔Game이고 로비는 최초 1회만 거친다.
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class LobbyManager : SceneManagerBase
{
    private SessionManager Session => App.Net.Session;
    private bool IsServer => NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;
    private bool m_started;

    // 로비는 조인 가능 — 진입 시 세션 잠금 해제. (잠금은 Shop의 '출동'에서)
    private void Start()
    {
        if (IsServer)
            Session?.SetLockedAsync(false).Forget();
    }

    /// <summary>호스트 전용 — 게임 시작. 상점(허브)으로 전환. 전 클라가 NGO 동기화로 따라온다.</summary>
    public void StartGame()
    {
        if (!IsServer || m_started) return;
        m_started = true;
        MoveToNextScene(EScene.Shop);
    }

    private void OnGUI()
    {
        if (!IsServer || m_started) return;
        GUILayout.BeginArea(new Rect(10, 90, 220, 60));
        if (GUILayout.Button("게임 시작 (→ 상점)"))
            StartGame();
        GUILayout.EndArea();
    }
}
