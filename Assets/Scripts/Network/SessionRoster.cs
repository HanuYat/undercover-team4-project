using System;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 세션 접속자 명부 (#429, #598) — 세션 시작 시 서버가 1회 스폰하는 상주 NetworkObject.
/// 서버가 NetworkList를 소유하고, 각 클라이언트는 스폰 시 자기 닉네임·PlayerId·음소거를 보고한다
/// (서버가 알 수 없는 로컬 값이라).
///
/// 로비 씬 오브젝트였다가 상주로 승격했다 (#598) — 입퇴장 알림이 상점·게임맵에서도 떠야 하는데
/// 닉네임을 아는 곳이 여기뿐이라 씬과 함께 죽으면 안 된다. TeamFund와 같은 구조다(#214 §6).
/// 승격의 부수 효과로 <see cref="OnNetworkSpawn"/>이 세션당 1회만 돌고, 그래서 "명부에 처음 추가되는
/// 보고"가 곧 세션 입장이 된다 — 씬 전환은 재입장으로 잡히지 않는다.
///
/// 늦게 들어온 클라는 스폰 시 리스트 전체를 복제받지만 OnListChanged는 불리지 않으므로
/// <see cref="OnListReady"/>로 "지금 상태를 한 번 그려라"를 알린다 (WantedListManager와 동일).
/// 상주가 되면서 구독자(로비 패널)보다 먼저 준비되는 경우가 생겼다 — 늦게 붙는 쪽은 구독 직후
/// 스스로 한 번 그려야 한다.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class SessionRoster : NetworkedManagerBase
{
    // 서버만 쓰기, 전 클라 읽기. 패널이 OnListChanged로 갱신받는다.
    private readonly NetworkList<LobbyPlayerEntry> m_players = new NetworkList<LobbyPlayerEntry>();

    /// <summary>동기화된 접속자 목록 — 패널이 구독·열람한다. 서버 외에는 읽기 전용.</summary>
    public NetworkList<LobbyPlayerEntry> Players => m_players;

    /// <summary>이 엔트리가 방장인가 — 별도 필드를 두지 않고 서버 clientId와 비교한다 (#429).</summary>
    public bool IsHostEntry(LobbyPlayerEntry entry) => NetworkManager != null && entry.ClientId == NetworkManager.ServerClientId;

    /// <summary>
    /// 이 클라이언트의 명부 항목을 찾는다 — 스폰 시점에 색·닉네임을 심는 쪽이 쓴다. (#790)
    /// 아직 보고가 안 닿았으면 false다(입장 직후 한두 틱).
    /// </summary>
    public bool TryGetEntry(ulong clientId, out LobbyPlayerEntry entry)
    {
        for (int i = 0; i < m_players.Count; i++)
        {
            if (m_players[i].ClientId != clientId)
                continue;

            entry = m_players[i];
            return true;
        }

        entry = default;
        return false;
    }

    /// <summary>이 피어에서 리스트가 스폰·초기 동기화된 시점 — late-join 빈 화면 방지.</summary>
    public event Action OnListReady;

    /// <summary>누군가 세션에 들어왔다 — 인자는 들어온 사람 닉네임. 받는 사람 본인은 제외된다. (#598)
    /// 표시는 <see cref="PlayerPresenceToastView"/>가 맡는다 — 명부는 무엇이 일어났는지만 알린다.</summary>
    public event Action<string> OnPlayerJoined;

    /// <summary>누군가 세션을 떠났다 — 남은 사람들에게 알리려고 각 피어에서 발행한다. 인자는 떠난 사람 닉네임. (#598)</summary>
    public event Action<string> OnPlayerLeft;

    public override void OnNetworkSpawn()
    {
        if (IsServer)
        {
            // 같은 실행에서 세션을 다시 만들면 이전 항목이 남을 수 있다 (#209 패턴)
            m_players.Clear();
            NetworkManager.OnClientDisconnectCallback += HandleClientDisconnected;
        }

        // 전 피어 공통 — 이 시점엔 리스트가 초기 동기화된 상태다. late-join도 여기서 처음 그린다.
        OnListReady?.Invoke();

        // 음소거를 바꾸면 다시 보고한다 — 자기 것만 올리므로 서버·클라 구분이 없다 (#430)
        GameSettings.OnMicMutedChanged += HandleMicMutedChanged;

        // 로봇 색도 같은 경로로 올린다 (#432)
        GameSettings.OnPlayerColorChanged += HandlePlayerColorChanged;

        // 자기 정보 보고. 호스트도 자기 행이 필요하므로 서버·클라 구분 없이 부른다.
        ReportSelfRpc(BuildSelf());
    }

    public override void OnNetworkDespawn()
    {
        GameSettings.OnMicMutedChanged -= HandleMicMutedChanged;
        GameSettings.OnPlayerColorChanged -= HandlePlayerColorChanged;

        // 세션이 끝나 명부가 사라질 때 반드시 뗀다 — 죽은 객체를 가리키는 구독이 남는다.
        if (IsServer && NetworkManager != null)
            NetworkManager.OnClientDisconnectCallback -= HandleClientDisconnected;
    }

    // InvokePermission = Everyone을 명시한다 — 이 명부는 서버 소유 오브젝트라 어떤 플레이어도 오너가
    // 아니다. 기본값(오너 전용)이면 클라가 자기 정보를 보고할 수 없다.
    // (ShopStand.RequestPurchaseRpc·SignalDecoder.RequestBroadcastRpc·Scanner.RequestChargeRpc가 같은 이유로 이렇게 돼 있다)
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void ReportSelfRpc(LobbyPlayerEntry entry, RpcParams rpcParams = default)
    {
        // 보낸 값을 믿지 않고 서버가 본 발신자로 덮는다 — 남의 행을 갈아치우지 못하게.
        entry.ClientId = rpcParams.Receive.SenderClientId;

        for (int i = 0; i < m_players.Count; i++)
        {
            if (m_players[i].ClientId == entry.ClientId)
            {
                bool hadNickname = !m_players[i].Nickname.IsEmpty;
                m_players[i] = entry; // 중복 보고는 갱신으로 흡수 (행이 두 개로 늘지 않게)

                // 이름이 이제야 도착했으면 지금이 알릴 때다 — 추가 시점엔 이름이 없어 미뤄 뒀다.
                if (!hadNickname && !entry.Nickname.IsEmpty)
                    AnnounceJoinedRpc(entry.Nickname, entry.ClientId);
                return;
            }
        }

        m_players.Add(entry);

        // 명부에 없던 사람이 처음 보고했다 = 세션 입장이다 (#598). OnClientConnectedCallback이 아닌 이유는
        // 그 시점엔 서버가 닉네임을 모르기 때문 — 음소거 변경 등의 재보고는 위 갱신 분기로 빠진다.
        // 이름이 아직 비어 있으면(인증이 늦은 클라) 알리지 않고 이름이 채워지는 보고까지 미룬다 —
        // 여기서 알려 버리면 빈 이름이라 표시 쪽에서 조용히 버려져 입장 알림이 통째로 사라진다.
        if (!entry.Nickname.IsEmpty)
            AnnounceJoinedRpc(entry.Nickname, entry.ClientId);
    }

    // 끊긴 클라의 행을 서버가 지운다. 로비엔 플레이어 오브젝트가 없어(#214) 엔트리를 회수해 줄 주인이 없다.
    private void HandleClientDisconnected(ulong clientId)
    {
        for (int i = m_players.Count - 1; i >= 0; i--)
        {
            if (m_players[i].ClientId != clientId)
                continue;

            // 닉네임은 지우기 전에 챙긴다 — 지우고 나면 누가 나갔는지 알릴 방법이 없다 (#598)
            FixedString64Bytes nickname = m_players[i].Nickname;
            m_players.RemoveAt(i);
            AnnounceLeftRpc(nickname, clientId);
        }
    }

    // 지금 붙어 있는 사람들에게만 간다 — 나중에 들어온 사람은 받지 않는다(RPC 특성 그대로가 요구사항이다). (#598)
    [Rpc(SendTo.ClientsAndHost)]
    private void AnnounceJoinedRpc(FixedString64Bytes nickname, ulong clientId) =>
        Announce(OnPlayerJoined, nickname, clientId);

    [Rpc(SendTo.ClientsAndHost)]
    private void AnnounceLeftRpc(FixedString64Bytes nickname, ulong clientId) =>
        Announce(OnPlayerLeft, nickname, clientId);

    // 자기 얘기는 자기에게 띄우지 않는다 — 입장에서 특히 어색하다. 퇴장은 당사자가 이미 떠나 도달하지
    // 않지만, 거르는 자리를 하나로 모아 둔다.
    private void Announce(Action<string> handler, FixedString64Bytes nickname, ulong clientId)
    {
        if (NetworkManager != null && clientId == NetworkManager.LocalClientId)
            return;

        handler?.Invoke(nickname.ToString());
    }

    // 음소거가 바뀌면 자기 보고를 다시 보낸다 — 전용 RPC를 만들지 않는다. ReportSelfRpc가 중복 보고를
    // 갱신으로 흡수하므로(행이 두 개로 늘지 않는다) 상태를 나르는 경로가 하나로 유지된다. (#430)
    private void HandleMicMutedChanged(bool _) => ReportSelf();

    private void HandlePlayerColorChanged(EBodyPart _) => ReportSelf();

    private void ReportSelf()
    {
        if (IsSpawned)
            ReportSelfRpc(BuildSelf());
    }

    // 닉네임·PlayerId는 각 클라의 로컬 값이다 (AuthBootstrap — PlayerPrefs + UGS, #249)
    private static LobbyPlayerEntry BuildSelf()
    {
        AuthBootstrap auth = App.Net.Auth;

        var nickname = (auth != null ? auth.Nickname : null).ToFixed64();
        var playerId = (auth != null ? auth.PlayerId : null).ToFixed64();

        // ClientId는 서버가 발신자로 채운다 — 여기서 넣지 않는다.
        return new LobbyPlayerEntry
        {
            Nickname = nickname,
            PlayerId = playerId,
            MicMuted = GameSettings.MicMuted,
            Colors = PlayerColorSet.FromSettings(),
        };
    }
}
