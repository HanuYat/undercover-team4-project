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

    // ▼서버에 요청. 트리거 무관(지금은 Q/E 테스트, 나중엔 IInteractable.Interact).
    // RequireOwnership 기본 false → 모니터 앞 아무 클라이언트나 요청 가능.
    [Rpc(SendTo.Server)]
    public void RequestSwitchNextRpc()
    {
        m_currentIndex.Value = (m_currentIndex.Value + 1) % m_cameras.Length;
    }

    // 채널 변경에 단일 상호작용 키(E)를 사용하므로 주석 처리 하였음.
    //[Rpc(SendTo.Server)]
    //public void RequestSwitchPrevRpc()
    //{
    //    m_currentIndex.Value = (m_currentIndex.Value - 1 + m_cameras.Length) % m_cameras.Length;
    //}

    void Apply()
    {
        for (int i = 0; i < m_cameras.Length; i++)
        {
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
