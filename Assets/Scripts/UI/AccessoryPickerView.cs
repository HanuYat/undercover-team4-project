using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 한 슬롯의 치장을 고르는 칸들 (#818) — <see cref="PlayerColorPickerView"/>와 같은 구조다.
/// 고르면 <see cref="GameSettings"/>에 쓰고, 남에게 나르는 일은 명부와 <c>PlayerAccessories</c>가 맡는다.
///
/// <b>안 가진 항목은 잠긴 칸으로 남긴다</b> (#818 D) — 숨기지 않는 것은 무엇이 더 있는지 보여야
/// 자판기를 돌릴 이유가 생기기 때문이다. 보유 판단은 <see cref="CosmeticInventory"/>가 한다.
/// </summary>
public class AccessoryPickerView : MonoBehaviour
{
    [Tooltip("이 줄이 고르는 슬롯")]
    [SerializeField] private EAccessorySlot m_slot;

    [Tooltip("치장 카탈로그 — Player 프리팹과 같은 에셋을 물릴 것")]
    [SerializeField] private AccessoryCatalog m_catalog;

    [Tooltip("칸을 넣을 부모 (Layout Group)")]
    [SerializeField] private RectTransform m_container;

    [SerializeField] private AccessoryCellView m_cellPrefab;

    private readonly List<AccessoryCellView> m_cells = new List<AccessoryCellView>();

    private void Awake()
    {
        if (m_catalog == null || m_container == null || m_cellPrefab == null)
        {
            Debug.LogWarning($"[{nameof(AccessoryPickerView)}] 배선이 빠졌습니다 (#818)", this);
            enabled = false;
            return;
        }

        Build();
    }

    private void OnEnable()
    {
        GameSettings.OnAccessoryChanged += HandleAccessoryChanged;
        CosmeticNames.OnLanguageChanged += RefreshLabels;
        CosmeticInventory.OnOwnedChanged += RefreshLocks;

        // 창을 열 때 한 번 걸러 낸다 — 해금이 생기기 전에 고른 값이 남아 있을 수 있다 (#818 D)
        CosmeticInventory.SanitizeEquipped(m_catalog);

        RefreshLocks();
        RefreshSelection();
    }

    private void OnDisable()
    {
        GameSettings.OnAccessoryChanged -= HandleAccessoryChanged;
        CosmeticNames.OnLanguageChanged -= RefreshLabels;
        CosmeticInventory.OnOwnedChanged -= RefreshLocks;
    }

    private void Build()
    {
        int count = m_catalog.CountOf(m_slot);
        for (int i = 0; i < count; i++)
        {
            int index = i; // 클로저가 루프 변수를 잡지 않게 사본을 넘긴다
            AccessoryCellView cell = Instantiate(m_cellPrefab, m_container);
            cell.name = $"Accessory {index}";
            cell.Bind(
                m_catalog.IconOf(m_slot, index),
                CosmeticNames.Of(m_catalog.Get(m_slot, index)),
                () => GameSettings.SetAccessory(m_slot, index)
            );
            m_cells.Add(cell);
        }

        RefreshLocks();
        RefreshSelection();
    }

    private void HandleAccessoryChanged(EAccessorySlot slot)
    {
        if (slot == m_slot)
            RefreshSelection();
    }

    private void RefreshLabels()
    {
        for (int i = 0; i < m_cells.Count; i++)
            m_cells[i].SetLabel(CosmeticNames.Of(m_catalog.Get(m_slot, i)));
    }

    // 자판기로 뽑으면 잠금이 풀린다 — 칸을 다시 만들지 않고 표시만 갈아 준다
    private void RefreshLocks()
    {
        for (int i = 0; i < m_cells.Count; i++)
            m_cells[i].SetLocked(!CosmeticInventory.IsOwned(m_catalog, m_slot, i));
    }

    private void RefreshSelection()
    {
        int selected = GameSettings.GetAccessory(m_slot);

        for (int i = 0; i < m_cells.Count; i++)
            m_cells[i].SetSelected(i == selected);
    }
}
