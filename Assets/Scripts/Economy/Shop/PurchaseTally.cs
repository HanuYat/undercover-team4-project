using System;
using Unity.Netcode;

/// <summary>
/// 구매 집계 한 줄 (#840) — 품목 하나의 "세션 누적 구매 수"와 "지금 남은 수".
/// 서버가 <see cref="ShopPurchases"/>의 NetworkList에 담아 전 클라에 동기화한다.
///
/// 품목 id는 ShopCatalog 인덱스다 — 이미 네트워크 계약인 표현이라(ShopLineup.Slot이 같은 것을 싣는다)
/// 소지형·설치형을 한 표현으로 덮고, 아이콘·이름은 보는 쪽이 ShopCatalog.Entry에서 읽는다.
/// 수명이 세션이라 빌드 간 인덱스 변동은 문제되지 않는다.
/// </summary>
public struct PurchaseTally : INetworkSerializable, IEquatable<PurchaseTally>
{
    public ushort CatalogIndex;

    /// <summary>세션 누적 구매 개수 — 줄지 않는다. 주문창 구매 내역이 읽는다.</summary>
    public ushort Bought;

    /// <summary>지금 남은 개수 — 소모·분실로 줄어든다. 본부 재고 게시판이 읽는다.</summary>
    public ushort Remaining;

    public void NetworkSerialize<T>(BufferSerializer<T> serializer)
        where T : IReaderWriter
    {
        serializer.SerializeValue(ref CatalogIndex);
        serializer.SerializeValue(ref Bought);
        serializer.SerializeValue(ref Remaining);
    }

    public bool Equals(PurchaseTally other) =>
        CatalogIndex == other.CatalogIndex && Bought == other.Bought && Remaining == other.Remaining;

    public override bool Equals(object obj) => obj is PurchaseTally other && Equals(other);

    public override int GetHashCode() => (CatalogIndex << 16) ^ (Bought << 8) ^ Remaining;
}
