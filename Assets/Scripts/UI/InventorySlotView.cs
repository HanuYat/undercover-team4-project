using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// 인벤토리 핫바의 슬롯 한 칸 (#144). 아이콘·이름 표시와 선택 하이라이트,
/// 편집 모드(Tab)에서의 호버 툴팁·드래그 정렬 이벤트를 담당한다. 로직은 InventoryBarView가 소유.
/// </summary>
public class InventorySlotView : MonoBehaviour,
    IPointerEnterHandler, IPointerExitHandler,
    IBeginDragHandler, IDragHandler, IEndDragHandler, IDropHandler
{
    [Header("표시")]
    [SerializeField] private Image m_background;
    [SerializeField] private Image m_icon;
    [SerializeField] private TextMeshProUGUI m_nameText;

    [Header("하이라이트 색")]
    [SerializeField] private Color m_normalColor = new Color(0f, 0f, 0f, 0.5f);
    [SerializeField] private Color m_selectedColor = new Color(1f, 0.85f, 0.2f, 0.7f);

    private InventoryBarView m_owner;
    private int m_index;
    private ItemBase m_item;

    // 드래그 고스트 복원용 — 드래그 중 아이콘을 캔버스 최상위로 옮겼다가 되돌린다.
    private Transform m_iconOriginalParent;
    private Vector3 m_iconOriginalLocalPosition;

    /// <summary>이 슬롯에 표시 중인 아이템. 빈 칸이면 null.</summary>
    public ItemBase Item => m_item;

    /// <summary>슬롯 인덱스 (0~2).</summary>
    public int Index => m_index;

    /// <summary>바가 스폰 시 1회 호출 — 슬롯 인덱스와 소유 바를 연결한다.</summary>
    public void Setup(int index, InventoryBarView owner)
    {
        m_index = index;
        m_owner = owner;
    }

    /// <summary>슬롯 내용 갱신. null = 빈 칸 (프레임만 표시).</summary>
    public void Bind(ItemBase item)
    {
        m_item = item;

        if (item == null)
        {
            m_icon.enabled = false;
            m_nameText.text = string.Empty;
            return;
        }

        m_icon.sprite = item.ItemIcon;
        m_icon.enabled = item.ItemIcon != null; // 아이콘 미설정이면 이름 텍스트가 폴백
        m_nameText.text = item.ItemName;
    }

    /// <summary>선택(장착) 하이라이트 — 배경색 스왑.</summary>
    public void SetSelected(bool selected)
    {
        m_background.color = selected ? m_selectedColor : m_normalColor;
    }

    // ---- 편집 모드 상호작용 (바가 편집 모드일 때만 유효) ----

    public void OnPointerEnter(PointerEventData eventData) => m_owner.ShowTooltip(this);

    public void OnPointerExit(PointerEventData eventData) => m_owner.HideTooltip();

    public void OnBeginDrag(PointerEventData eventData)
    {
        if (!m_owner.IsEditMode || m_item == null)
        {
            eventData.pointerDrag = null; // 드래그 취소 — 이후 Drag/Drop 이벤트가 오지 않는다.
            return;
        }

        // 아이콘을 캔버스 최상위로 옮겨 다른 슬롯 위에 그려지게 하고, 드롭 레이캐스트를 막지 않게 한다.
        m_iconOriginalParent = m_icon.transform.parent;
        m_iconOriginalLocalPosition = m_icon.transform.localPosition;
        m_icon.transform.SetParent(m_icon.canvas.rootCanvas.transform, true);
        m_icon.raycastTarget = false;
    }

    public void OnDrag(PointerEventData eventData)
    {
        m_icon.transform.position = eventData.position;
    }

    public void OnEndDrag(PointerEventData eventData)
    {
        m_icon.transform.SetParent(m_iconOriginalParent, false);
        m_icon.transform.localPosition = m_iconOriginalLocalPosition;
        m_icon.raycastTarget = false; // 아이콘은 항상 레이캐스트 비대상 — 배경이 포인터를 받는다.
    }

    public void OnDrop(PointerEventData eventData)
    {
        if (!m_owner.IsEditMode || eventData.pointerDrag == null)
        {
            return;
        }

        InventorySlotView source = eventData.pointerDrag.GetComponent<InventorySlotView>();
        if (source != null && source != this)
        {
            m_owner.RequestSwap(source.Index, m_index);
        }
    }
}
