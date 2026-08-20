using System;
using Unity.Collections;
using Unity.Netcode;

/// <summary>
/// 본부 수배 리스트(#58)의 한 항목 — 검거 대상 1명의 표시·매칭 데이터.
/// 서버가 만들어 NetworkList로 전 클라이언트에 동기화한다 (서버 권위, #52/#56).
///
/// 몽타주는 <b>완성 문장이 아니라 원본 데이터</b>로 담는다 — 외형 프로필 인덱스 + 공개 축.
/// 문장은 표시하는 피어가 <see cref="AppearanceDatabase.BuildMontageText"/>로 조립하므로,
/// 호스트와 클라이언트의 언어 설정이 갈려도 각자 자기 언어로 본다 (#497).
/// 공개 축은 라운드마다 서버에서 랜덤 결정되므로 프로필만으로는 부족해 항목에 함께 싣는다 —
/// 항목 하나가 자족적이면 리스트 갱신과 공개 축 동기화의 도착 순서를 신경 쓸 일도 없다.
/// </summary>
public struct WantedEntry : INetworkSerializable, IEquatable<WantedEntry>
{
    public ulong NpcId; // 검거 대상 NPC의 NetworkObjectId — 검거 시 이 항목을 특정해 제거하는 고유 키

    public FixedString64Bytes Name; // 유일 판별자 — 현장이 스캔(#39)으로 확인해 대조하는 이름.

    // 몽타주 원본 — 축별 옵션 인덱스. 공개 축만 담고 비공개 축은 미배정(-1)이다
    // (AppearanceProfile.Masked). 정답 외형은 서버 전용 값이므로 화면에 안 띄우는 축이라도 실어 보내지 않는다.
    public AppearanceProfile Appearance;

    public RevealedAxisSet RevealedAxes; // 이 몽타주로 공개된 축들 — 조립·표시는 이 축들만 쓴다

    // 이 대상의 현상금 (#395) — 본부 수배 화면에 함께 띄운다. 원본은 서버 전용 값
    // (CitizenIdentity.Bounty)이라, 본부에 보여주려면 이렇게 항목에 실어 보내야 한다.
    public int Bounty;

    // 수배 조건 (생사 불문/생포 필수) — Bounty와 같은 이유로 실어 보낸다. (#766)
    public WantedCondition Condition;

    public void NetworkSerialize<T>(BufferSerializer<T> serializer)
        where T : IReaderWriter
    {
        serializer.SerializeValue(ref NpcId);
        serializer.SerializeValue(ref Name);
        serializer.SerializeValue(ref Appearance);
        serializer.SerializeValue(ref RevealedAxes);
        serializer.SerializeValue(ref Bounty);
        serializer.SerializeValue(ref Condition);
    }

    public bool Equals(WantedEntry other) => NpcId == other.NpcId; // 제거 매칭 용도

    public override bool Equals(object obj) => obj is WantedEntry other && Equals(other);

    public override int GetHashCode() => NpcId.GetHashCode();
}
