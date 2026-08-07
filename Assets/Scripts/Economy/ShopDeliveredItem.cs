using UnityEngine;

/// <summary>
/// 상점에서 산 물건임을 나타내는 표식 — 배달 시 <see cref="ShopDelivery"/>가 붙인다. (#303)
/// 잃어버렸을 때 팀 구매 목록(<see cref="ShopPurchases"/>)에서도 빼야 하는지를 이것으로 가른다.
/// 기본 지급품에는 붙지 않으므로, 표식이 없으면 다음 라운드에 어차피 다시 지급된다.
///
/// 프리팹 자산을 들고 있는 이유: 목록이 프리팹 참조 List라 인스턴스만으로는 어느 항목을 뺄지 알 수 없다.
/// 판정이 서버 권위라 복제하지 않는다 (MisdemeanorOffender와 같은 관례).
/// </summary>
public class ShopDeliveredItem : MonoBehaviour
{
    /// <summary>이 인스턴스를 만든 원본 프리팹 — 구매 목록에서 뺄 항목을 찾는 열쇠.</summary>
    public ItemBase SourcePrefab { get; set; }
}
