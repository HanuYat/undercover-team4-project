using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 연결 승인 콜백의 단일 소유자 (#628 B층). 씬과 무관하게 항상 걸려야 해 상주 매니저
/// (<see cref="SessionManager"/>)가 들고 있다가 NGO 콜백에 꽂는다. 씬별 스폰 정책은
/// <see cref="SpawnPolicy"/>로 위임받는다(<see cref="PlayerSpawnManager"/>가 채운다) —
/// NGO 콜백이 단일 델리게이트라 두 관심사가 따로 response를 만지지 않게 하나로 합쳤다.
/// </summary>
public class ConnectionApprovalGate
{
    public System.Action<NetworkManager.ConnectionApprovalResponse> SpawnPolicy { get; set; }

    private NetworkManager m_networkManager;

    /// <summary>SDK가 StartClient/StartHost를 부르기 전에 대입해야 실제 요청에 실린다 (#628).</summary>
    public static void StampLocalPayload(NetworkManager networkManager)
    {
        if (networkManager != null)
            networkManager.NetworkConfig.ConnectionData = NetworkProtocol.EncodePayload();
    }

    public void Install(NetworkManager networkManager)
    {
        m_networkManager = networkManager;
        if (m_networkManager != null)
            m_networkManager.ConnectionApprovalCallback = Approve;
    }

    private void Approve(
        NetworkManager.ConnectionApprovalRequest request,
        NetworkManager.ConnectionApprovalResponse response
    )
    {
        // 호스트 자기 자신도 이 콜백을 타지만 NGO는 호스트 거부를 무시하고 강제 승인한다 —
        // 검사를 건너뛰고 곧장 스폰 정책까지 실행해야 호스트 자신의 플레이어가 생긴다.
        bool isSelf =
            m_networkManager != null && request.ClientNetworkId == m_networkManager.LocalClientId;

        if (!isSelf)
        {
            PeerStamp client = NetworkProtocol.DecodePayload(request.Payload);

            // 차단 판정은 Version만 본다 — Sha는 로그에만 실린다 (#622).
            if (client.Version != NetworkProtocol.VersionString)
            {
                Debug.LogWarning(
                    $"[ConnectionApprovalGate] 버전 불일치로 연결 거부 / 내 버전(호스트): {NetworkProtocol.VersionString}, 클라 버전: {client.Version}, 내 sha: {BuildStamp.Sha}, 클라 sha: {client.Sha}"
                );
                response.Approved = false;
                response.Reason = NetworkProtocol.BuildMismatchReason(
                    NetworkProtocol.VersionString
                );
                return;
            }

            // 버전이 같아도 sha가 다르면 프로토콜 번호를 안 올린 것이다 — 막지 않고 남긴다.
            if (client.Sha != BuildStamp.Sha)
            {
                Debug.LogWarning(
                    $"[ConnectionApprovalGate] 버전은 같은데 커밋이 다름(참가 허용) / 버전: {NetworkProtocol.VersionString}, 내 sha: {BuildStamp.Sha}, 클라 sha: {client.Sha}"
                );
            }
            else
            {
                Debug.Log(
                    $"[ConnectionApprovalGate] 클라 접속 승인 / 버전: {client.Version}, sha: {client.Sha}"
                );
            }
        }

        response.Approved = true;
        response.CreatePlayerObject = false; // 정책이 없으면 접속만 승인
        SpawnPolicy?.Invoke(response);
    }
}
