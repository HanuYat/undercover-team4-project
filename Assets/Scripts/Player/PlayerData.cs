using Unity.Netcode;
using UnityEngine;

public class PlayerData : NetworkBehaviour
{
    [Header("스테이터스")]
    [SerializeField] private int m_maxHp = 100;

    private readonly NetworkVariable<int> m_currentHp = new NetworkVariable<int>();

    public ulong PlayerId => OwnerClientId;
    public int MaxHp => m_maxHp;
    public int CurrentHp => m_currentHp.Value;

    public override void OnNetworkSpawn()
    {
        if (IsServer)
        {
            m_currentHp.Value = m_maxHp;
        }
    }

    // 서버 권위로만 실제 값 변경. (데미지 소스가 클라라면 별도 ServerRpc로 요청)
    public void ModifyHp(int delta)
    {
        if (!IsServer) return;

        m_currentHp.Value = Mathf.Clamp(m_currentHp.Value + delta, 0, m_maxHp);
    }
}
