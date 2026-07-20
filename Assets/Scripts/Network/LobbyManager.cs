using Unity.Netcode;
using UnityEngine;

// 로비(본부) 대기 → 호스트가 게임을 시작하면 RoundManager.StartRound()를 호출한다.
// 로비는 플레이 맵 안 본부를 그대로 쓴다. (임시?)
public class LobbyManager : MonoBehaviour
{
    [Header("라운드 (비우면 씬에서 자동 탐색)")]
    [SerializeField] private RoundManager m_roundManager;

    private NetworkManager m_networkManager;
    private bool m_gameStarted;

    private bool IsServer => m_networkManager != null && m_networkManager.IsServer;

    private void Start()
    {
        m_networkManager = NetworkManager.Singleton;
        if (m_roundManager == null)
            m_roundManager = FindFirstObjectByType<RoundManager>();
    }

    public void StartGame()
    {
        if (!IsServer || m_gameStarted) return;
        if (m_roundManager == null)
        {
            Debug.LogWarning("[LobbyManager] RoundManager 미설정", this);
            return;
        }

        m_gameStarted = true;
        m_roundManager.StartRound();
        Debug.Log("[LobbyManager] 게임 시작 - 라운드 시작");
    }

    private void OnGUI()
    {
        if (!IsServer || m_gameStarted)
            return;

        GUILayout.BeginArea(new Rect(10, 320, 220, 60));
        if (GUILayout.Button("게임 시작 (StartGame)")) StartGame();
        GUILayout.EndArea();
    }
}
