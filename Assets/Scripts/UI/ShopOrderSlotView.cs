using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 주문창 그리드의 한 칸 (#843). 진열 칸 하나의 품목을 그리고, 주문 버튼을 창에 전달한다.
/// 로직은 <see cref="ShopBrowserPanel"/>이 소유한다 — 이 칸은 표시와 클릭 전달만 한다
/// (<see cref="LootSlotView"/>와 같은 분담).
///
/// 문구는 <see cref="Bind"/> 때 한 번 읽는다 — 언어가 바뀌면 창이 전체를 다시 바인드한다.
/// 칸마다 StringChanged를 걸지 않는 이유: 항목이 여럿이라 로케일 변경을 한 곳에 걸고 통째로
/// 다시 채우는 편이 해제 누락이 없다.
/// </summary>
public class ShopOrderSlotView : MonoBehaviour
{
    private const string k_itemTable = "ItemTable";
    private const string k_shopTable = "ShopTable";
    private const string k_commonTable = "CommonTable";
    private const string k_descriptionKeyPrefix = "Item.Description.";
    private const string k_moneyKey = "Common.Unit.Money";

    [Header("표시")]
    [SerializeField]
    private Image m_background;

    [SerializeField]
    private Image m_icon;

    [SerializeField]
    private TMP_Text m_nameText;

    [SerializeField]
    private TMP_Text m_priceText;

    [Tooltip("설명 줄. 비워 두면 설명을 표시하지 않는다")]
    [SerializeField]
    private TMP_Text m_descriptionText;

    [Header("주문")]
    [SerializeField]
    private Button m_orderButton;

    [SerializeField]
    private TMP_Text m_orderButtonText;

    [Header("색")]
    [SerializeField]
    private Color m_filledColor = new Color(0.031f, 0.078f, 0.122f, 0.93f);

    [SerializeField]
    private Color m_emptyColor = new Color(0.031f, 0.078f, 0.122f, 0.45f);

    [Tooltip("품절일 때 주문 버튼 글자색")]
    [SerializeField]
    private Color m_soldOutColor = new Color(1f, 0.361f, 0.416f, 1f);

    [Tooltip("이미 산 설치형일 때 주문 버튼 글자색")]
    [SerializeField]
    private Color m_ownedColor = new Color(0.271f, 0.890f, 0.604f, 1f);

    private ShopBrowserPanel m_owner;
    private int m_slot = -1; // 빈 칸이면 -1

    // 주문 가능할 때의 버튼 글자색 — 상태색으로 덮어썼다가 되돌려야 해서 처음 값을 들고 있는다
    private Color m_orderLabelColor = Color.white;
    private bool m_orderLabelColorCached;

    /// <summary>창이 1회 호출 — 소유 창을 연결하고 버튼을 건다.</summary>
    public void Setup(ShopBrowserPanel owner)
    {
        m_owner = owner;

        if (m_orderButtonText != null && !m_orderLabelColorCached)
        {
            m_orderLabelColor = m_orderButtonText.color;
            m_orderLabelColorCached = true;
        }

        if (m_orderButton != null)
        {
            m_orderButton.onClick.RemoveListener(HandleOrderClicked);
            m_orderButton.onClick.AddListener(HandleOrderClicked);
        }
    }

    /// <summary>칸 내용 갱신. entry가 null이면 빈 칸.</summary>
    public void Bind(int slot, ShopCatalog.Entry entry, EShopSlotStatus status)
    {
        m_slot = entry != null ? slot : -1;

        if (m_background != null)
            m_background.color = entry != null ? m_filledColor : m_emptyColor;

        if (entry == null)
        {
            SetEmpty();
            return;
        }

        if (m_icon != null)
        {
            m_icon.sprite = entry.Icon;
            m_icon.enabled = entry.Icon != null; // 아이콘 미배선이면 이름 글자가 폴백
        }

        SetText(m_nameText, ResolveName(entry));
        SetText(m_priceText, LocalizedStrings.Get(k_commonTable, k_moneyKey, entry.Price));
        SetText(m_descriptionText, ResolveDescription(entry));

        bool orderable = status == EShopSlotStatus.Available;

        // 상태는 별도 배지가 아니라 주문 버튼 자리에 띄운다 — 못 사는 칸에 "주문"이 그대로 있으면
        // 누를 수 있는 것처럼 읽히고, 구석 배지는 눈이 가지 않는다.
        SetText(m_orderButtonText, LocalizedStrings.Get(k_shopTable, StatusKey(status)));

        if (m_orderButtonText != null)
        {
            m_orderButtonText.color =
                status == EShopSlotStatus.SoldOut ? m_soldOutColor
                : status == EShopSlotStatus.Owned ? m_ownedColor
                : m_orderLabelColor;
        }

        if (m_orderButton != null)
        {
            m_orderButton.gameObject.SetActive(true);
            m_orderButton.interactable = orderable;
        }
    }

    private static string StatusKey(EShopSlotStatus status) =>
        status == EShopSlotStatus.SoldOut ? "Shop.Order.SoldOut"
        : status == EShopSlotStatus.Owned ? "Shop.Order.Owned"
        : "Shop.Order.Button";

    private void SetEmpty()
    {
        if (m_icon != null)
            m_icon.enabled = false;

        SetText(m_nameText, LocalizedStrings.Get(k_shopTable, "Shop.Order.Empty"));
        SetText(m_priceText, string.Empty);
        SetText(m_descriptionText, string.Empty);

        if (m_orderButton != null)
            m_orderButton.gameObject.SetActive(false);
    }

    // 이름 조회는 카탈로그 항목이 한다 (#840) — 구매 내역·본부 재고 게시판도 같은 것을 읽는다.
    private static string ResolveName(ShopCatalog.Entry entry)
    {
        string name = entry.DisplayName;
        return string.IsNullOrEmpty(name) ? LocalizedStrings.Get(k_shopTable, "Shop.Order.Empty") : name;
    }

    private static string ResolveDescription(ShopCatalog.Entry entry)
    {
        if (entry.IsInstallable)
            return LocalizedStrings.Get(k_itemTable, k_descriptionKeyPrefix + entry.Installable);

        return entry.ItemPrefab != null && !entry.ItemPrefab.ItemDescription.IsEmpty
            ? entry.ItemPrefab.ItemDescription.GetLocalizedString()
            : string.Empty;
    }

    private static void SetText(TMP_Text label, string text)
    {
        if (label != null)
            label.text = text;
    }

    private void HandleOrderClicked()
    {
        if (m_slot < 0 || m_owner == null)
            return;

        m_owner.RequestOrder(m_slot);
    }

    private void OnDestroy()
    {
        if (m_orderButton != null)
            m_orderButton.onClick.RemoveListener(HandleOrderClicked);
    }
}
