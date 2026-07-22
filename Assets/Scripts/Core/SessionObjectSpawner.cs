using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 세션(서버) 시작 시 세션 내내 유지할 네트워크 오브젝트를 스폰한다 — 씬을 넘어 사는 상주 상태(팀 자금 등). (#214 §6)
/// 서버만 스폰하고 destroyWithScene:false라 씬 전환에서 안 사라진다. 클라는 복제로 받는다.
/// **세션 생성 씬(Title)에 배치**한다 — 세션 생성이 Title에서 일어나 OnServerStarted가 거기서 발화하기 때문.
/// (다른 씬에도 두면 그 인스턴스는 이미 서버가 켜진 뒤라 OnServerStarted를 못 받아 무동작 — Title에만 둘 것)
/// </summary>
public class SessionObjectSpawner : MonoBehaviour
{
    [Tooltip("세션 동안 유지할 프리팹들 (DefaultNetworkPrefabs에 등록 필수)")]
    [SerializeField] private NetworkObject[] m_persistentPrefabs;

    private NetworkManager m_nm;
    private bool m_spawned; // 이 인스턴스가 이미 스폰했는지 — 중복 스폰 가드

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
        if (m_spawned)
            return;
        m_spawned = true;

        foreach (NetworkObject prefab in m_persistentPrefabs)
        {
            if (prefab == null) continue;
            Instantiate(prefab).Spawn(destroyWithScene: false);
        }
    }
}
