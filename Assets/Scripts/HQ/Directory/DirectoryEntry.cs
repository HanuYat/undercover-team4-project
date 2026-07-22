using System;
using Unity.Collections;
using Unity.Netcode;

/// <summary>
/// 본부 인명부 항목 (#223) — 스폰된 시민 1명의 정본 신원. 서버가 배정 후 채워 전 클라에 동기화한다.
/// 현장 스캔(표시값)과 대조해 위조 여부를 판단하는 기준. 문양(심볼)은 #222 연동 시 추가한다.
/// </summary>
public struct DirectoryEntry : INetworkSerializable, IEquatable<DirectoryEntry>
{
    public FixedString64Bytes Name; // 정본 이름 — 대조 기준
    public OfficialRecords.CitizenType Type;
    public OfficialRecords.Faction Faction;

    public static DirectoryEntry FromProfile(CitizenProfile profile)
    {
        var name = new FixedString64Bytes();
        name.CopyFromTruncated(
            profile != null ? (profile.CitizenName ?? string.Empty) : string.Empty
        );
        return new DirectoryEntry
        {
            Name = name,
            Type = profile != null ? profile.CitizenType : default,
            Faction = profile != null ? profile.Faction : default,
        };
    }

    public void NetworkSerialize<T>(BufferSerializer<T> serializer)
        where T : IReaderWriter
    {
        serializer.SerializeValue(ref Name);
        serializer.SerializeValue(ref Type);
        serializer.SerializeValue(ref Faction);
    }

    public bool Equals(DirectoryEntry other) =>
        Name.Equals(other.Name) && Type == other.Type && Faction == other.Faction;

    public override bool Equals(object obj) => obj is DirectoryEntry other && Equals(other);

    public override int GetHashCode() => Name.GetHashCode();
}
