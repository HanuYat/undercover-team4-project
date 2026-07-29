using System;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 로비 접속자 1명 (#429) — 각 클라이언트가 자기 로컬 값을 ServerRpc로 보고해 채운다.
/// 서버는 남의 닉네임·UGS PlayerId를 모르기 때문(ConnectedClientsIds로 clientId만 안다).
/// 방장 여부는 필드로 두지 않는다 — ClientId == NetworkManager.ServerClientId로 판정한다.
/// 발화 상태도 넣지 않는다 — 각 클라의 Vivox가 로컬로 그린다 (PlayerNameTag와 동일 방침).
/// </summary>
public struct LobbyPlayerEntry : INetworkSerializable, IEquatable<LobbyPlayerEntry>
{
    public ulong ClientId;
    public FixedString64Bytes Nickname;
    public FixedString64Bytes PlayerId;

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref ClientId);
        serializer.SerializeValue(ref Nickname);
        serializer.SerializeValue(ref PlayerId);
    }

    // clientId만으로 판정한다 — 한 접속자당 한 행이므로 이게 고유 키다 (WantedEntry가 NpcId만 보는 것과 같은 취지)
    public bool Equals(LobbyPlayerEntry other) => ClientId == other.ClientId;

    public override bool Equals(object obj) => obj is LobbyPlayerEntry other && Equals(other);

    public override int GetHashCode() => ClientId.GetHashCode();
}
