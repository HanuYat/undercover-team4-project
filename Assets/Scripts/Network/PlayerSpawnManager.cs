using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 접속하는 모든 플레이어를 지정한 스폰 포인트 한 곳에서 생성한다.
/// NetworkManager 인스펙터에서 Connection Approval이 켜져 있어야 동작한다.
/// 오너 클라이언트 쪽 위치 보정은 PlayerMovement.ApplyServerSpawnPose가 담당한다
/// (NetworkTransform이 Owner 권한이라 서버 지정 위치만으로는 부족함).
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

        if (!m_networkManager.NetworkConfig.ConnectionApproval)
        {
            Debug.LogWarning(
                "[PlayerSpawnManager] NetworkManager의 Connection Approval이 꺼져 있어 "
                    + "Approval 콜백이 호출되지 않습니다 → 항상 기본 위치에 스폰됩니다."
            );
        }

        if (m_networkManager.IsListening)
        {
            Debug.LogWarning(
                "[PlayerSpawnManager] 콜백 등록 전에 네트워크가 이미 시작됨 — "
                    + "먼저 접속한 플레이어(호스트 포함)는 기본 위치에 스폰됐을 수 있습니다."
            );
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
            Debug.Log(
                $"[PlayerSpawnManager] 클라이언트 {request.ClientNetworkId} 스폰 위치 지정: "
                    + $"{response.Position}"
            );
        }
        else
        {
            Debug.LogWarning(
                $"[PlayerSpawnManager] 스폰 포인트가 지정되지 않아 클라이언트 "
                    + $"{request.ClientNetworkId}가 기본 위치(프리팹 원점)에 생성됩니다."
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
