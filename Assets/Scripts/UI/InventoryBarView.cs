using System;
using Cysharp.Threading.Tasks;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 화면 하단 인벤토리 핫바 (#144, 오너 로컬 HUD). 고정 3칸 슬롯에 보유 아이템을 표시하고
/// 장착 슬롯을 하이라이트한다. 선택/줍기 시 아이템 이름 팝업을 띄우며,
/// Tab 편집 모드에서는 커서를 풀어 드래그 정렬·호버 툴팁을 지원한다 (WASD 이동 시 자동 닫힘).
/// 데이터는 PlayerLoadout.Slots가 진실 — 이 클래스는 표시만 한다.
/// </summary>
public class InventoryBarView : NetworkBehaviour
{
    [Header("플레이어 참조")]
    [SerializeField]
    private PlayerLoadout m_loadout;

    [SerializeField]
    private PlayerItemUser m_itemUser;

    [SerializeField]
    private PlayerInputHandler m_inputHandler;

    [SerializeField]
    private PlayerMovement m_movement;

    [Header("UI 참조")]
    [Tooltip("핫바 패널 루트 — 비오너에선 통째로 꺼진다.")]
    [SerializeField]
    private GameObject m_barRoot;

    [SerializeField]
    private Image m_barBackground;

    [SerializeField]
    private InventorySlotView[] m_slotViews;

    [Tooltip("선택/줍기 시 아이템 이름 팝업 라벨.")]
    [SerializeField]
    private TextMeshProUGUI m_itemNameLabel;

    [Header("툴팁 (편집 모드 호버)")]
    [SerializeField]
    private GameObject m_tooltipPanel;

    [SerializeField]
    private TextMeshProUGUI m_tooltipName;

    [SerializeField]
    private TextMeshProUGUI m_tooltipDescription;

    [Tooltip("호버한 슬롯 위로 띄울 세로 오프셋(px).")]
    [SerializeField]
    private float m_tooltipOffsetY = 110f;

    [Header("연출")]
    [SerializeField]
    private float m_itemNameDuration = 1.5f;

    [SerializeField]
    private Color m_barNormalColor = new Color(0f, 0f, 0f, 0.25f);

    [SerializeField]
    private Color m_barEditColor = new Color(0.2f, 0.5f, 1f, 0.35f);

    private bool m_isEditMode;
    private bool m_cursorUnlockedBeforeEdit; // 편집 진입 전 커서 상태 — 종료 시 복원(ESC 토글과 desync 방지)
    private int m_itemNameVersion; // 팝업 연속 발생 시 이전 숨김 예약 무효화용
    private readonly ItemBase[] m_lastSlots = new ItemBase[PlayerLoadout.k_maxHeldItems]; // 줍기 감지 스냅샷

    /// <summary>Tab 편집 모드 여부 — 슬롯 드래그·툴팁이 이때만 동작한다.</summary>
    public bool IsEditMode => m_isEditMode;

    public override void OnNetworkSpawn()
    {
        // 내 화면에만 표시 — 비오너 인스턴스는 바를 통째로 끈다. (PlayerHpUI 관례)
        if (!IsOwner)
        {
            m_barRoot.SetActive(false);
            enabled = false;
            return;
        }

        for (int i = 0; i < m_slotViews.Length; i++)
        {
            m_slotViews[i].Setup(i, this);
        }

        m_loadout.OnSlotsChanged += HandleSlotsChanged;
        m_loadout.OnEquippedSlotChanged += RefreshHighlight;
        m_itemUser.OnEquippedItemChanged += HandleEquippedItemChanged;
        m_inputHandler.OnToggleInventory += ToggleEditMode;

        m_itemNameLabel.gameObject.SetActive(false);
        m_tooltipPanel.SetActive(false);
        m_barBackground.color = m_barNormalColor;
        HandleSlotsChanged(); // 초기 표시 (스폰 시점에 이미 지급됐을 수 있음)
    }

    public override void OnNetworkDespawn()
    {
        if (!IsOwner)
        {
            return;
        }

        m_loadout.OnSlotsChanged -= HandleSlotsChanged;
        m_loadout.OnEquippedSlotChanged -= RefreshHighlight;
        m_itemUser.OnEquippedItemChanged -= HandleEquippedItemChanged;
        m_inputHandler.OnToggleInventory -= ToggleEditMode;
    }

    private void Update()
    {
        if (!m_isEditMode)
        {
            return;
        }

        // 편집 모드 자동 종료 조건:
        //  - WASD 이동 입력 → 움직이기 시작하면 조준 복귀 (UX).
        //  - 외부(ESC 임시 토글 등)에서 커서가 다시 잠기면 → 편집 UI만 살아 있는 desync를 막으려 함께 닫는다 (#144).
        if (m_inputHandler.MoveInput != Vector2.zero || Cursor.lockState == CursorLockMode.Locked)
        {
            SetEditMode(false);
        }
    }

    // ---- 슬롯 표시 ----

    private void HandleSlotsChanged()
    {
        // 스냅샷에 없던 아이템이 정확히 하나면 줍기 — 이름 팝업. (초기 지급 2개는 장착 팝업이 대신 알린다)
        ItemBase newItem = null;
        int newCount = 0;
        for (int i = 0; i < m_slotViews.Length; i++)
        {
            ItemBase current = m_loadout.Slots[i];
            if (current != null && Array.IndexOf(m_lastSlots, current) < 0)
            {
                newItem = current;
                newCount++;
            }
        }

        for (int i = 0; i < m_slotViews.Length; i++)
        {
            m_lastSlots[i] = m_loadout.Slots[i];
            m_slotViews[i].Bind(m_loadout.Slots[i]);
        }

        RefreshHighlight();

        if (newCount == 1)
        {
            ShowItemName(newItem);
        }
    }

    // 장착 아이템이 바뀌면 이름 팝업만 띄운다. 하이라이트는 이 이벤트를 부르는 두 경로(EquipSlot→
    // OnEquippedSlotChanged, RebuildHeldItems→OnSlotsChanged)가 직후에 각자 RefreshHighlight하므로 여기선 불필요.
    private void HandleEquippedItemChanged(ItemBase item)
    {
        if (item != null)
        {
            ShowItemName(item);
        }
    }

    // 선택 슬롯 인덱스로 하이라이트 — 빈 칸을 선택해도 그 칸이 강조돼 현재 위치가 보인다 (#144).
    private void RefreshHighlight()
    {
        int equipped = m_loadout.EquippedIndex;
        for (int i = 0; i < m_slotViews.Length; i++)
        {
            m_slotViews[i].SetSelected(i == equipped);
        }
    }

    // ---- 아이템 이름 팝업 ----

    private void ShowItemName(ItemBase item)
    {
        // 표시 시점마다 새로 가져오는 일회성 텍스트라 동기 호출로 충분 — 언어 전환 갱신 구독 불필요 (#251)
        m_itemNameLabel.text = item.ItemName.GetLocalizedString();
        m_itemNameLabel.gameObject.SetActive(true);
        HideItemNameAsync(++m_itemNameVersion).Forget();
    }

    private async UniTaskVoid HideItemNameAsync(int version)
    {
        await UniTask.Delay(TimeSpan.FromSeconds(m_itemNameDuration));

        // 파괴됐거나 그 사이 새 팝업이 떠서 예약이 낡았으면 무시.
        if (this == null || version != m_itemNameVersion)
        {
            return;
        }

        m_itemNameLabel.gameObject.SetActive(false);
    }

    // ---- 편집 모드 (Tab) ----

    private void ToggleEditMode()
    {
        // 다운(무력화) 중에는 편집 모드 진입 차단 — 커서 해제·슬롯 정렬이 다운 상태와 충돌 (#105).
        // 이미 편집 모드였다면 닫는 건 허용(정리).
        if (m_loadout.IsIncapacitated && !m_isEditMode)
        {
            return;
        }

        SetEditMode(!m_isEditMode);
    }

    private void SetEditMode(bool on)
    {
        if (m_isEditMode == on)
        {
            return;
        }

        m_isEditMode = on;

        if (on)
        {
            // 진입 전 커서 상태를 기억 — 종료 시 이 상태로 되돌린다. ESC 임시 토글이 풀어둔 커서를
            // 편집 모드 종료가 강제로 잠그지 않게 한다. 해제 중엔 시점 회전도 정지 (PlayerMovement).
            m_cursorUnlockedBeforeEdit = Cursor.lockState == CursorLockMode.None;
            m_movement.SetCursorUnlocked(true);
        }
        else
        {
            m_movement.SetCursorUnlocked(m_cursorUnlockedBeforeEdit);
            HideTooltip();
        }

        m_barBackground.color = on ? m_barEditColor : m_barNormalColor;
    }

    /// <summary>슬롯 드래그 정렬 완료 — PlayerLoadout에 스왑을 위임한다. 표시는 OnSlotsChanged로 돌아온다.</summary>
    public void RequestSwap(int from, int to) => m_loadout.SwapSlots(from, to);

    // ---- 툴팁 (편집 모드 호버) ----

    /// <summary>슬롯 호버 진입 — 편집 모드에서 아이템 이름·설명 툴팁을 슬롯 위에 띄운다.</summary>
    public void ShowTooltip(InventorySlotView slot)
    {
        if (!m_isEditMode || slot.Item == null)
        {
            return;
        }

        m_tooltipName.text = slot.Item.ItemName.GetLocalizedString();
        m_tooltipDescription.text = slot.Item.ItemDescription.GetLocalizedString();
        m_tooltipPanel.transform.position =
            slot.transform.position + new Vector3(0f, m_tooltipOffsetY, 0f);
        m_tooltipPanel.SetActive(true);
    }

    public void HideTooltip() => m_tooltipPanel.SetActive(false);
}
