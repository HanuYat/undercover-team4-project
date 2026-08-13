using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Localization;
using UnityEngine.UI;

/// <summary>
/// 약탈 창의 슬롯 한 칸 (#487). 털 대상의 소지품 하나를 그리고, 클릭하면 가져가기를 요청한다.
/// 로직은 <see cref="LootPanel"/>이 소유한다 — 이 칸은 표시와 클릭 전달만 한다.
///
/// <see cref="InventorySlotView"/>(핫바)와 나눠 둔 이유는 <b>조작이 정반대</b>이기 때문이다:
/// 핫바 칸은 드래그로 자리를 바꾸고(#144) 여기는 클릭으로 물건이 손을 옮긴다. 한 칸에 두 조작을
/// 얹으면 "드래그하려다 뺏어 오는" 사고가 난다. 표시 코드는 닮았지만 그 닮음은 우연이다.
/// </summary>
public class LootSlotView : MonoBehaviour, IPointerClickHandler
{
    [Header("표시")]
    [SerializeField]
    private Image m_background;

    [SerializeField]
    private Image m_icon;

    [SerializeField]
    private TextMeshProUGUI m_nameText;

    [Header("색")]
    [SerializeField]
    private Color m_filledColor = new Color(0f, 0f, 0f, 0.5f);

    [SerializeField]
    private Color m_emptyColor = new Color(0f, 0f, 0f, 0.2f);

    private LootPanel m_owner;
    private ItemBase m_item;

    // 이름 갱신을 구독 중인 LocalizedString — 해제 기준을 아이템이 아니라 이 참조로 잡는다.
    // 아이템이 파괴되면 m_item이 Unity 가짜 null이라 아이템 기준 해제가 스킵되고, 남은 구독이
    // 나중에 발화해 빈 칸에 옛 이름을 쓴다. (InventorySlotView와 같은 사정, #251)
    private LocalizedString m_boundName;

    /// <summary>이 칸에 표시 중인 아이템. 빈 칸이면 null.</summary>
    public ItemBase Item => m_item;

    /// <summary>창이 1회 호출 — 소유 창을 연결한다.</summary>
    public void Setup(LootPanel owner) => m_owner = owner;

    /// <summary>칸 내용 갱신. null = 빈 칸.</summary>
    public void Bind(ItemBase item)
    {
        if (m_boundName != null)
        {
            m_boundName.StringChanged -= HandleItemNameChanged;
            m_boundName = null;
        }

        m_item = item;

        if (m_background != null)
            m_background.color = item != null ? m_filledColor : m_emptyColor;

        if (item == null)
        {
            if (m_icon != null)
                m_icon.enabled = false;
            if (m_nameText != null)
                m_nameText.text = string.Empty;
            return;
        }

        if (m_icon != null)
        {
            m_icon.sprite = item.ItemIcon;
            m_icon.enabled = item.ItemIcon != null; // 아이콘 미설정이면 이름 텍스트가 폴백
        }

        // 구독 즉시 현재 언어 값으로 1회 호출되고, 이후 언어 전환 시마다 다시 호출된다 (#251)
        m_boundName = item.ItemName;
        m_boundName.StringChanged += HandleItemNameChanged;
    }

    private void HandleItemNameChanged(string localizedName)
    {
        if (m_nameText != null)
            m_nameText.text = localizedName;
    }

    private void OnDestroy()
    {
        if (m_boundName != null)
            m_boundName.StringChanged -= HandleItemNameChanged;
    }

    public void OnPointerClick(PointerEventData eventData)
    {
        if (m_item == null || m_owner == null)
            return;

        m_owner.RequestTake(m_item);
    }
}
