using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 접속하는 모든 플레이어를 지정한 스폰 포인트 한 곳에서 생성한다.
/// NetworkManager 인스펙터에서 Connection Approval이 켜져 있어야 동작한다.
/// </summary>
public class PlayerSpawnManager : MonoBehaviour
{
    [SerializeField]
    private Transform m_spawnPoint;

    [SerializeField]
    private float m_spreadRadius = 1.5f; // 겹침 방지용 분산 반경(m)

    private NetworkManager m_networkManager;
    private int m_approvedCount;

    private void Start()
    {
        m_networkManager = NetworkManager.Singleton;
        if (m_networkManager == null)
        {
            Debug.LogWarning("[PlayerSpawnManager] NetworkManager를 찾을 수 없습니다.");
            return;
        }

        m_networkManager.ConnectionApprovalCallback = OnConnectionApproval;
    }

    private void OnDestroy()
    {
        if (m_networkManager != null)
        {
            m_networkManager.ConnectionApprovalCallback = null;
        }
    }

    private void OnConnectionApproval(
        NetworkManager.ConnectionApprovalRequest request,
        NetworkManager.ConnectionApprovalResponse response
    )
    {
        response.Approved = true;
        response.CreatePlayerObject = true;

        if (m_spawnPoint != null)
        {
            response.Position = m_spawnPoint.position + GetSpreadOffset(m_approvedCount++);
            response.Rotation = m_spawnPoint.rotation;
        }
        else
        {
            Debug.LogWarning(
                "[PlayerSpawnManager] 스폰 포인트가 지정되지 않아 기본 위치(프리팹 원점)에 생성됩니다."
            );
        }
    }

    // 접속 순서대로 스폰 포인트 주변 원 둘레에 배치한다 — 같은 좌표에 겹쳐 스폰되면
    // CharacterController 겹침 해소가 플레이어를 임의 방향으로 밀어내기 때문 (최대 6인, GDD 기준)
    private Vector3 GetSpreadOffset(int index)
    {
        if (index == 0)
        {
            return Vector3.zero;
        }

        float angle = (index % 6) * 60f * Mathf.Deg2Rad;
        return new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * m_spreadRadius;
    }
}
