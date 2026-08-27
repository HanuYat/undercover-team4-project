using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 주문창 구매 내역의 한 줄 (#840) — 아이콘 / 이름 / "구매 n · 남음 m". 표시 전용.
/// 문구는 <see cref="Bind"/> 때 지금 언어로 한 번 읽는다 — 언어가 바뀌면 목록이 통째로 다시 그려진다 (#497).
/// </summary>
public class ShopPurchaseRowView : MonoBehaviour
{
    private const string k_shopTable = "ShopTable";
    private const string k_rowKey = "Shop.History.Row";

    [SerializeField]
    private Image m_icon;

    [SerializeField]
    private TMP_Text m_nameText;

    [Tooltip("구매·남음 개수 줄 — Shop.History.Row 서식으로 채운다")]
    [SerializeField]
    private TMP_Text m_countText;

    /// <summary>줄 내용 갱신. entry는 카탈로그 항목, bought는 세션 누적, remaining은 지금 남은 개수.</summary>
    public void Bind(ShopCatalog.Entry entry, int bought, int remaining)
    {
        if (m_icon != null)
        {
            Sprite icon = entry != null ? entry.Icon : null;
            m_icon.sprite = icon;
            m_icon.enabled = icon != null; // 아이콘 미배선이면 이름 글자가 폴백
        }

        if (m_nameText != null)
            m_nameText.text = entry != null ? entry.DisplayName : string.Empty;

        if (m_countText != null)
            m_countText.text = LocalizedStrings.Get(k_shopTable, k_rowKey, bought, remaining);
    }
}
