using Unity.Netcode;
using UnityEngine;

public class CCTVSwitcher : NetworkBehaviour, IInteractable
{
    [SerializeField] Camera[] m_cameras;
    [SerializeField] RenderTexture m_monitorRt;

    NetworkVariable<int> m_currentIndex = new(
        0,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    public override void OnNetworkSpawn()
    {
        m_currentIndex.OnValueChanged += HandleIndexChanged;
        Apply();
    }

    public override void OnNetworkDespawn()
    {
        m_currentIndex.OnValueChanged -= HandleIndexChanged;
    }

    void HandleIndexChanged(int previous, int current) => Apply();

    [Rpc(SendTo.Server)]
    public void RequestSwitchNextRpc()
    {
        if (m_cameras == null || m_cameras.Length == 0) return;
        m_currentIndex.Value = (m_currentIndex.Value + 1) % m_cameras.Length;
    }

    void Apply()
    {
        if (m_cameras == null || m_cameras.Length == 0) return;

        for (int i = 0; i < m_cameras.Length; i++)
        {
            if (m_cameras[i] == null) continue;  // 슬롯 미할당 방어

            bool active = (i == m_currentIndex.Value);
            m_cameras[i].targetTexture = active ? m_monitorRt : null;
            m_cameras[i].enabled = active;   // 안 보이는 카메라는 렌더 안 함
        }
    }

    public void Interact(GameObject interactor)
    {
        RequestSwitchNextRpc();
    }
}
