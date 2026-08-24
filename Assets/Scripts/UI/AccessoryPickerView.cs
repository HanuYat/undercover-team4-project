using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 한 슬롯의 치장을 고르는 칸들 (#818) — <see cref="PlayerColorPickerView"/>와 같은 구조다.
/// 고르면 <see cref="GameSettings"/>에 쓰고, 남에게 나르는 일은 명부와 <c>PlayerAccessories</c>가 맡는다.
///
/// <b>보유 개념이 아직 없다</b> — 카탈로그의 전 항목을 고를 수 있다. 보유함 필터는 후속(#818 D)에서 얹는다.
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
        RefreshSelection();
    }

    private void OnDisable()
    {
        GameSettings.OnAccessoryChanged -= HandleAccessoryChanged;
        CosmeticNames.OnLanguageChanged -= RefreshLabels;
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

    private void RefreshSelection()
    {
        int selected = GameSettings.GetAccessory(m_slot);

        for (int i = 0; i < m_cells.Count; i++)
            m_cells[i].SetSelected(i == selected);
    }
}
