using System;
using Unity.Collections;
using Unity.Netcode;

/// <summary>
/// CitizenIdentity의 신원 동기화 페이로드 (#38/#52) — 서버가 배정한 신원 중
/// 스캔(#39)으로 공개되어도 되는 부분(이름·타입·세력)만 담아 전 클라이언트에 동기화한다.
/// IsCriminal(정답)·Reaction(검거 반응)은 의도적으로 제외 — 소비처가 전부 서버측이고,
/// 클라이언트로 보내면 메모리 조작으로 범인이 노출된다. (WantedEntry와 동일 직렬화 패턴)
/// </summary>
public struct CitizenData : INetworkSerializable, IEquatable<CitizenData>
{
    public FixedString64Bytes Name; // 정본 이름 — 수배 리스트(#58)·본부 인명부(#223) 대조의 유일 판별자
    public FixedString64Bytes NameView; // 스캔 표시 이름 — 정본과 다르면 이름 위조 (#223)
    public OfficialRecords.CitizenType Type;
    public OfficialRecords.Faction Faction;

    /// <summary>배정 완료 여부 — 서버 배정 전 기본값(빈 이름)과 구분한다.</summary>
    public bool IsAssigned => !Name.IsEmpty;

    /// <summary>서버 배정 프로필에서 공개 가능한 부분만 추려 담는다. (배정 측 전용)</summary>
    public static CitizenData FromProfile(CitizenProfile profile)
    {
        if (profile == null)
            return default;

        // FixedString은 용량 초과 시 던지므로 잘라 담는다 (WantedListManager와 동일 관례)
        var name = new FixedString64Bytes();
        name.CopyFromTruncated(profile.CitizenName ?? string.Empty);

        // 표시 이름이 비어 있으면 정본으로 대체 — 정상 시민은 정본==표시 (#223)
        var nameView = new FixedString64Bytes();
        nameView.CopyFromTruncated(profile.m_nameView ?? profile.CitizenName ?? string.Empty);

        return new CitizenData
        {
            Name = name,
            NameView = nameView,
            Type = profile.CitizenType,
            Faction = profile.Faction,
        };
    }

    public void NetworkSerialize<T>(BufferSerializer<T> serializer)
        where T : IReaderWriter
    {
        serializer.SerializeValue(ref Name);
        serializer.SerializeValue(ref NameView);
        serializer.SerializeValue(ref Type);
        serializer.SerializeValue(ref Faction);
    }

    public bool Equals(CitizenData other) =>
        Name.Equals(other.Name)
        && NameView.Equals(other.NameView)
        && Type == other.Type
        && Faction == other.Faction;

    public override bool Equals(object obj) => obj is CitizenData other && Equals(other);

    public override int GetHashCode() => Name.GetHashCode();
}
