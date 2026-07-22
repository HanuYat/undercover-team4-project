using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 세션(서버) 시작 시 세션 내내 유지할 네트워크 오브젝트를 스폰한다 — 씬을 넘어 사는 상주 상태(팀 자금 등). (#214 §6)
/// 서버만 스폰하고 destroyWithScene:false라 씬 전환에서 안 사라진다. 클라는 복제로 받는다.
/// AppBootstrap(상주)에 두어 항상 OnServerStarted를 받는다.
/// </summary>
public class SessionObjectSpawner : MonoBehaviour
{
    [Tooltip("세션 동안 유지할 프리팹들 (DefaultNetworkPrefabs에 등록 필수)")]
    [SerializeField] private NetworkObject[] m_persistentPrefabs;

    private NetworkManager m_nm;

    private void Start()
    {
        m_nm = NetworkManager.Singleton;
        if (m_nm != null)
            m_nm.OnServerStarted += HandleServerStarted;
    }

    private void OnDestroy()
    {
        if (m_nm != null)
            m_nm.OnServerStarted -= HandleServerStarted;
    }

    private void HandleServerStarted()
    {
        foreach (NetworkObject prefab in m_persistentPrefabs)
        {
            if (prefab == null) continue;
            Instantiate(prefab).Spawn(destroyWithScene: false);
        }
    }
}
