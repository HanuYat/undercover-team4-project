using System;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 로비 접속자 로스터 (#429) — 로비 씬에 배치된 NetworkObject. 서버가 NetworkList를 소유하고,
/// 각 클라이언트는 스폰 시 자기 닉네임·PlayerId를 보고한다(서버가 알 수 없는 로컬 값이라).
/// 늦게 들어온 클라는 NGO 씬 동기화로 이 오브젝트를 받으며 리스트 전체가 복제되지만,
/// OnListChanged는 불리지 않으므로 OnListReady로 "지금 상태를 한 번 그려라"를 알린다 (WantedListManager와 동일).
/// 매니저가 아니다 — 참조자가 LobbyRosterPanel 한 곳이라 App에 올리지 않고 SerializeField로 연결한다 (R3).
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class LobbyRoster : NetworkBehaviour
{
    // 서버만 쓰기, 전 클라 읽기. 패널이 OnListChanged로 갱신받는다.
    private readonly NetworkList<LobbyPlayerEntry> m_players = new NetworkList<LobbyPlayerEntry>();

    /// <summary>동기화된 접속자 목록 — 패널이 구독·열람한다. 서버 외에는 읽기 전용.</summary>
    public NetworkList<LobbyPlayerEntry> Players => m_players;

    /// <summary>이 엔트리가 방장인가 — 별도 필드를 두지 않고 서버 clientId와 비교한다 (#429).</summary>
    public bool IsHostEntry(LobbyPlayerEntry entry) => NetworkManager != null && entry.ClientId == NetworkManager.ServerClientId;

    /// <summary>이 피어에서 리스트가 스폰·초기 동기화된 시점 — late-join 빈 화면 방지.</summary>
    public event Action OnListReady;

    public override void OnNetworkSpawn()
    {
        if (IsServer)
        {
            // 같은 실행에서 세션을 다시 만들면 씬 오브젝트에 이전 항목이 남을 수 있다 (#209 패턴)
            m_players.Clear();
            NetworkManager.OnClientDisconnectCallback += HandleClientDisconnected;
        }

        // 전 피어 공통 — 이 시점엔 리스트가 초기 동기화된 상태다. late-join도 여기서 처음 그린다.
        OnListReady?.Invoke();

        // 자기 정보 보고. 호스트도 자기 행이 필요하므로 서버·클라 구분 없이 부른다.
        ReportSelfRpc(BuildSelf());
    }

    public override void OnNetworkDespawn()
    {
        // 상점으로 넘어가며 로비가 언로드될 때 반드시 뗀다 — 죽은 객체를 가리키는 구독이 남는다.
        if (IsServer && NetworkManager != null)
            NetworkManager.OnClientDisconnectCallback -= HandleClientDisconnected;
    }

    // 오너십을 요구하지 않는다 — 씬 NetworkObject의 오너는 서버이고 보고하는 쪽은 클라다.
    // (구 [ServerRpc]는 기본이 RequireOwnership=true라 클라 호출이 거부된다. 신 Rpc API는 그 제약이 없다)
    [Rpc(SendTo.Server)]
    private void ReportSelfRpc(LobbyPlayerEntry entry, RpcParams rpcParams = default)
    {
        // 보낸 값을 믿지 않고 서버가 본 발신자로 덮는다 — 남의 행을 갈아치우지 못하게.
        entry.ClientId = rpcParams.Receive.SenderClientId;

        for (int i = 0; i < m_players.Count; i++)
        {
            if (m_players[i].ClientId == entry.ClientId)
            {
                m_players[i] = entry; // 중복 보고는 갱신으로 흡수 (행이 두 개로 늘지 않게)
                return;
            }
        }

        m_players.Add(entry);
    }

    // 끊긴 클라의 행을 서버가 지운다. 로비엔 플레이어 오브젝트가 없어(#214) 엔트리를 회수해 줄 주인이 없다.
    private void HandleClientDisconnected(ulong clientId)
    {
        for (int i = m_players.Count - 1; i >= 0; i--)
        {
            if (m_players[i].ClientId == clientId)
                m_players.RemoveAt(i);
        }
    }

    // 닉네임·PlayerId는 각 클라의 로컬 값이다 (AuthBootstrap — PlayerPrefs + UGS, #249)
    private static LobbyPlayerEntry BuildSelf()
    {
        AuthBootstrap auth = App.Net.Auth;

        var nickname = new FixedString64Bytes();
        nickname.CopyFromTruncated(auth != null ? auth.Nickname ?? string.Empty : string.Empty);

        var playerId = new FixedString64Bytes();
        playerId.CopyFromTruncated(auth != null ? auth.PlayerId ?? string.Empty : string.Empty);

        // ClientId는 서버가 발신자로 채운다 — 여기서 넣지 않는다.
        return new LobbyPlayerEntry { Nickname = nickname, PlayerId = playerId };
    }
}
