using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 본부 재고 게시판의 한 줄 (#840) — 아이콘 / 품목 이름 / 오른쪽에 남은 개수. 표시 전용.
/// 개수를 오른쪽 끝에 붙여 줄이 여럿일 때 숫자가 한 열로 읽히게 한다.
/// 문구는 <see cref="Bind"/> 때 지금 언어로 한 번 읽는다 — 언어가 바뀌면 게시판이 통째로 다시 그린다 (#497).
/// </summary>
public class HqStockRowView : MonoBehaviour
{
    private const string k_hqTable = "HqTable";
    private const string k_countKey = "Hq.Stock.Count";

    [SerializeField]
    private Image m_icon;

    [SerializeField]
    private TMP_Text m_nameText;

    [Tooltip("남은 개수 — Hq.Stock.Count 서식으로 채운다. 줄 오른쪽 끝에 붙인다")]
    [SerializeField]
    private TMP_Text m_countText;

    [Tooltip("남은 것이 있을 때 글자색. 비우지 말 것 — 0인 줄과 구분되는 기준색이다")]
    [SerializeField]
    private Color m_stockedColor = Color.white;

    [Tooltip("다 쓴 줄(0개) 글자색 — 숨기지 않고 회색으로 남긴다. '없다'도 본부가 알아야 한다")]
    [SerializeField]
    private Color m_emptyColor = new Color(0.55f, 0.58f, 0.62f);

    /// <summary>줄 내용 갱신. remaining이 0이면 회색으로 남는다.</summary>
    public void Bind(ShopCatalog.Entry entry, int remaining)
    {
        if (entry == null)
            return;

        bool stocked = remaining > 0;

        if (m_icon != null)
        {
            m_icon.sprite = entry.Icon;
            m_icon.enabled = entry.Icon != null;
            m_icon.color = stocked ? m_stockedColor : m_emptyColor;
        }

        if (m_nameText != null)
        {
            m_nameText.text = entry.DisplayName;
            m_nameText.color = stocked ? m_stockedColor : m_emptyColor;
        }

        if (m_countText != null)
        {
            m_countText.text = LocalizedStrings.Get(k_hqTable, k_countKey, remaining);
            m_countText.color = stocked ? m_stockedColor : m_emptyColor;
        }
    }
}
