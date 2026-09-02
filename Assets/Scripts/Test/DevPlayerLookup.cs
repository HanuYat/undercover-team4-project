#if UNITY_EDITOR
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 개발자 단축키가 쓰는 플레이어 조회 — 폭탄·차량 단축키가 같은 것을 복사해 쓰던 것을 모았다.
/// <b>에디터 전용</b>이라 빌드에는 들어가지 않는다.
/// </summary>
public static class DevPlayerLookup
{
    /// <summary>이 피어의 플레이어 — 세션이 없으면(오프라인 Play) 씬에 하나뿐이다.</summary>
    public static Transform LocalPlayer()
    {
        NetworkManager manager = NetworkManager.Singleton;
        if (manager != null && manager.IsListening && manager.LocalClient?.PlayerObject != null)
            return manager.LocalClient.PlayerObject.transform;

        // 매니저가 아니라 스폰물이라 R1(FindFirstObjectByType 금지)의 대상이 아니다
        PlayerMovement player = Object.FindFirstObjectByType<PlayerMovement>();
        return player != null ? player.transform : null;
    }

    /// <summary>지정한 접속자의 플레이어 — 없으면 null.</summary>
    public static Transform ResolvePlayer(ulong clientId)
    {
        NetworkManager manager = NetworkManager.Singleton;
        if (manager != null
            && manager.ConnectedClients.TryGetValue(clientId, out NetworkClient client)
            && client.PlayerObject != null)
            return client.PlayerObject.transform;

        return null;
    }
}
#endif
