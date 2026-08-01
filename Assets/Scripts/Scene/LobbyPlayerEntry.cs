using System;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 로비 접속자 1명 (#429) — 각 클라이언트가 자기 로컬 값을 ServerRpc로 보고해 채운다.
/// 서버는 남의 닉네임·UGS PlayerId를 모르기 때문(ConnectedClientsIds로 clientId만 안다).
/// 방장 여부는 필드로 두지 않는다 — ClientId == NetworkManager.ServerClientId로 판정한다.
/// 발화 상태도 넣지 않는다 — 각 클라의 Vivox가 로컬로 그린다 (PlayerNameTag와 동일 방침).
/// 반면 음소거는 여기 올린다 — 로컬 입력 장치 설정이라 Vivox 참가자 API로 남의 음소거 여부를
/// 조회할 방법이 없다. 발화와 달리 명시적으로 보고해야 하는 상태다. (#430)
/// </summary>
public struct LobbyPlayerEntry : INetworkSerializable, IEquatable<LobbyPlayerEntry>
{
    public ulong ClientId;
    public FixedString64Bytes Nickname;
    public FixedString64Bytes PlayerId;
    public bool MicMuted;

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref ClientId);
        serializer.SerializeValue(ref Nickname);
        serializer.SerializeValue(ref PlayerId);
        serializer.SerializeValue(ref MicMuted);
    }

    // NetworkList는 이 Equals로 "값이 바뀌었는가"를 판정한다 (NetworkList.Set → NetworkVariableSerialization.AreEqual).
    // ClientId만 비교하면 같은 사람의 음소거·닉네임이 바뀌어도 "같다"로 보고 갱신을 통째로 버린다 — 복제도
    // OnListChanged도 일어나지 않는다. 행 매칭은 LobbyRoster가 ClientId를 직접 비교하므로(중복 보고 흡수)
    // 여기서는 값 전체를 본다. GetHashCode는 그대로 둔다 — 값이 같으면 ClientId도 같아 규약을 지킨다. (#430)
    public bool Equals(LobbyPlayerEntry other) =>
        ClientId == other.ClientId
        && Nickname == other.Nickname
        && PlayerId == other.PlayerId
        && MicMuted == other.MicMuted;

    public override bool Equals(object obj) => obj is LobbyPlayerEntry other && Equals(other);

    public override int GetHashCode() => ClientId.GetHashCode();
}
