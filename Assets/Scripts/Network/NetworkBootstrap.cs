using UnityEngine;
using Unity.Netcode;

public class NetworkBootstrap : MonoBehaviour
{
    private NetworkManager m_networkManager;

    private void Awake()
    {
        m_networkManager = GetComponent<NetworkManager>();
        if (m_networkManager == null)
        {
            m_networkManager = NetworkManager.Singleton;
        } 
    }

    private void OnEnable()
    {
        if (m_networkManager == null) return;
        m_networkManager.OnClientConnectedCallback += HandleClientConnected;
        m_networkManager.OnClientDisconnectCallback += HandleClientDisconnected;
    }

    private void OnDisable()
    {
        if (m_networkManager == null) return;
        m_networkManager.OnClientConnectedCallback -= HandleClientConnected;
        m_networkManager.OnClientDisconnectCallback -= HandleClientDisconnected;
    }

    private void HandleClientConnected(ulong clientId)
    {
        Debug.Log($"[NetworkBootstrap] 클라이언트 접속 {clientId}");
    }

    private void HandleClientDisconnected(ulong clientId)
    {
        Debug.Log($"[NetworkBootstrap] 클라이언트 끊김 {clientId}");
    }

    private void OnGUI()
    {
        if (m_networkManager == null)
        {
            GUILayout.BeginArea(new Rect(10, 10, 320, 60));
            GUILayout.Label("NetworkManager 를 찾을 수 없습니다.");
            GUILayout.EndArea();
            return;
        }

        GUILayout.BeginArea(new Rect(10, 10, 320, 220));

        if (!m_networkManager.IsClient && !m_networkManager.IsServer)   // 접속 전
        {
            if (GUILayout.Button("Host (서버 + 클라이언트)"))
            {
                if (!m_networkManager.StartHost())
                {
                    Debug.LogWarning("[NetworkBootstrap] StartHost 실패");
                }
            }
            if (GUILayout.Button("Client (서버에 접속)"))
            {
                if (!m_networkManager.StartClient())
                {
                    Debug.LogWarning("[NetworkBootstrap] StartClient 실패");
                }
            }
        }
        else // 접속 후
        {
            string mode = m_networkManager.IsHost ? "Host" : "Client";
            GUILayout.Label($"모드: {mode}");

            if (m_networkManager.IsHost)
                GUILayout.Label($"연결된 클라이언트 수: {m_networkManager.ConnectedClientsIds.Count}");
            
            if (GUILayout.Button("Shutdown (연결 종료)"))
                m_networkManager.Shutdown();
        }

        GUILayout.EndArea();
    }
}
