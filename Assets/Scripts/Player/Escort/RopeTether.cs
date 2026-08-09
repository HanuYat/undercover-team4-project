using System;
using Unity.Netcode;

/// <summary>
/// 밧줄 연결 하나 — 어느 NPC에 걸렸고 지금 <b>끌고 있는지</b>를 담는다. (#390)
/// 서버가 만들어 <see cref="PlayerEscorter"/>의 NetworkList로 전 클라이언트에 동기화한다 (WantedEntry와 같은 관례, #52/#56).
/// 서버 자신은 읽지 않는다 — 진실은 NPC의 앵커 목록(<see cref="NpcRopeDrag.IsDraggedBy"/>)이다.
///
/// 끌기 여부를 NPC가 아니라 <b>연결마다</b> 두는 이유: 여러 명이 같은 대상을 함께 묶을 수 있어(줄다리기)
/// <see cref="NpcRopeDrag.IsRoped"/>는 "누구든 끌고 있다"까지만 말해 준다. 내가 E로 놓았는데 남이
/// 계속 끌고 있는 상태를 클라가 구분하려면 참가자별 플래그가 필요하다.
/// </summary>
public struct RopeTether : INetworkSerializable, IEquatable<RopeTether>
{
    public ulong NpcId; // 묶인 NPC의 NetworkObjectId — 이 항목을 특정해 제거·갱신하는 고유 키

    // 지금 실제로 끌고 있는가. false면 묶여만 있다(E로 놓아둔 상태) — 장력에 기여하지 않고,
    // 줄은 늘어나기만 하다가 거리 초과로 끊긴다. (#369의 '놓기'를 참가자별로 확장한 것)
    public bool Dragging;

    public void NetworkSerialize<T>(BufferSerializer<T> serializer)
        where T : IReaderWriter
    {
        serializer.SerializeValue(ref NpcId);
        serializer.SerializeValue(ref Dragging);
    }

    /// <summary>
    /// <b>모든 필드를 비교할 것 — NGO의 변경 감지가 이걸 쓴다.</b> <c>NetworkList.Set</c>은
    /// <c>NetworkVariableSerialization&lt;T&gt;.AreEqual</c>(→ 이 메서드)로 갱신 여부를 판단해서, 같다고
    /// 나오면 내부 리스트에도 쓰지 않고 복제도 하지 않는다. <see cref="NpcId"/>만 보던 동안에는
    /// <see cref="Dragging"/> 반전이 삼켜져 원격 클라의 플래그가 처음 걸린 값에 굳었다 (#390 → #398).
    ///
    /// 항목 검색·제거는 이 비교에 의존하지 않는다 — <c>IndexOfSynced</c>가 <see cref="NpcId"/>를 직접 훑고
    /// 제거는 <c>RemoveAt</c>이다.
    /// </summary>
    public bool Equals(RopeTether other) => NpcId == other.NpcId && Dragging == other.Dragging;

    public override bool Equals(object obj) => obj is RopeTether other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(NpcId, Dragging);
}
