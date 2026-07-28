using System;
using Unity.Collections;
using Unity.Netcode;

/// <summary>
/// 본부 수배 리스트(#58)의 한 항목 — 검거 대상 1명의 표시·매칭 데이터.
/// 서버가 만들어 NetworkList로 전 클라이언트에 동기화한다 (서버 권위, #52/#56).
///
/// 표시 텍스트를 서버에서 완성해 담는다 — 몽타주 공개 축(RevealedAxes)이 라운드마다
/// 서버에서 랜덤 결정되므로, 인덱스만 보내면 클라이언트가 같은 텍스트를 만들 수 없다.
/// 완성된 글을 실으면 클라이언트는 AppearanceDatabase·공개 축 동기화 없이 그대로 표시하면 된다.
/// (localization·재렌더가 필요해지면 그때 인덱스+공개 축 동기화로 확장)
/// </summary>
public struct WantedEntry : INetworkSerializable, IEquatable<WantedEntry>
{
    public ulong NpcId; // 검거 대상 NPC의 NetworkObjectId — 검거 시 이 항목을 특정해 제거하는 고유 키

    public FixedString64Bytes Name; // 유일 판별자 — 현장이 스캔(#39)으로 확인해 대조하는 이름.

    public FixedString128Bytes Montage; // 글 방식 몽타주 텍스트 - 본부 화면에 그대로 띄울 것

    // 현상금은 일부러 싣지 않는다 (#395) — 본부에 금액이 보이면 몽타주를 대조해 찾는 수사가
    // "비싼 대상부터 고르기"로 바뀌어 재미가 죽는다. 서버 전용 값(CitizenIdentity.Bounty)으로 남긴다.

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref NpcId);
        serializer.SerializeValue(ref Name);
        serializer.SerializeValue(ref Montage);
    }

    public bool Equals(WantedEntry other) => NpcId == other.NpcId;  // 제거 매칭 용도

    public override bool Equals(object obj) => obj is WantedEntry other && Equals(other);

    public override int GetHashCode() => NpcId.GetHashCode();
}
