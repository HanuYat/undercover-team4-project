using System;
using UnityEngine;

/// <summary>
/// 슬롯별 치장 아이템 목록 (#818) — <b>배열 인덱스가 곧 네트워크로 오가는 값</b>이다.
/// 인덱스 0은 "안 씀"이라 0번 항목은 프리팹을 비워 둔다. 수치·목록을 코드에 박지 않는다 (GDD 10-4).
/// </summary>
[CreateAssetMenu(fileName = "AccessoryCatalog", menuName = "Undercover/Player/Accessory Catalog")]
public class AccessoryCatalog : ScriptableObject
{
    [Serializable]
    private class Item
    {
        [Tooltip("붙일 프리팹 — 0번은 '안 씀'이라 비워 둘 것. 콜라이더가 있는 프리팹을 넣지 말 것")]
        public GameObject Prefab;

        [Tooltip("선택 칸에 띄울 아이콘 — 비어 있으면 이름만 보인다")]
        public Sprite Icon;

        [Tooltip("이걸 쓰면 가려지는 슬롯 — 전면 헬멧이 머리카락을, 마스크가 수염을 덮는 식")]
        public EAccessorySlotMask Hides;

        [Tooltip("계정을 새로 만들어도 처음부터 쓸 수 있는가 — 나머지는 자판기로 해금한다")]
        public bool DefaultOwned;
    }

    [Serializable]
    private class SlotEntry
    {
        public EAccessorySlot Slot;
        public Item[] Items;
    }

    [SerializeField] private SlotEntry[] m_slots = new SlotEntry[0];

    /// <summary>그 슬롯의 항목 수 — 0번("안 씀")을 포함한 길이다. 목록이 없으면 1(안 씀만).</summary>
    public int CountOf(EAccessorySlot slot)
    {
        SlotEntry entry = Find(slot);
        return entry == null || entry.Items == null ? 1 : Mathf.Max(1, entry.Items.Length);
    }

    /// <summary>붙일 프리팹 — 0이거나 범위 밖이면 null("안 씀")이다.</summary>
    public GameObject Get(EAccessorySlot slot, int index)
    {
        Item item = ItemAt(slot, index);
        return item == null ? null : item.Prefab;
    }

    /// <summary>선택 칸 아이콘 — 없으면 null이고, 그 자리는 이름만 보인다.</summary>
    public Sprite IconOf(EAccessorySlot slot, int index)
    {
        Item item = ItemAt(slot, index);
        return item == null ? null : item.Icon;
    }

    /// <summary>
    /// 처음부터 쓸 수 있는 항목인가 (#818 D). <b>보유함에 담지 않고 카탈로그가 정한다</b> —
    /// 담아 두면 기본 지급 세트를 고칠 때 이미 만든 계정에는 반영되지 않는다.
    /// "안 씀"(0)은 늘 쓸 수 있다.
    /// </summary>
    public bool IsDefaultOwned(EAccessorySlot slot, int index)
    {
        if (index <= 0)
            return true;

        Item item = ItemAt(slot, index);
        return item != null && item.DefaultOwned;
    }

    /// <summary>
    /// 이 조합에서 <b>가려지는</b> 슬롯 (#818) — 전면 헬멧을 쓰면 머리카락이, 마스크를 쓰면 수염이
    /// 덮인다. 가린다고 선택을 지우지는 않는다: 벗으면 골라 둔 것이 그대로 다시 나온다.
    ///
    /// 아이템이 <b>자기 슬롯을 가리는 것은 무시한다</b> — 그러면 자기 자신이 안 붙는다.
    /// </summary>
    public EAccessorySlotMask HiddenSlots(AccessorySet set)
    {
        EAccessorySlotMask hidden = EAccessorySlotMask.None;

        // enum 선언 순서가 곧 우선순위다 — <b>이미 가려진 아이템은 남을 가리지 못한다</b>.
        // 서로 가리게 적어 두면(헬멧이 머리카락을, 그 머리카락이 헬멧을) 둘 다 사라지기 때문이다.
        foreach (EAccessorySlot slot in Enum.GetValues(typeof(EAccessorySlot)))
        {
            if (IsHidden(hidden, slot))
                continue;

            Item item = ItemAt(slot, set[slot]);
            if (item == null)
                continue;

            hidden |= item.Hides & ~MaskOf(slot);
        }

        return hidden;
    }

    /// <summary>그 슬롯이 <see cref="HiddenSlots"/>에 걸렸는가.</summary>
    public static bool IsHidden(EAccessorySlotMask hidden, EAccessorySlot slot) =>
        (hidden & MaskOf(slot)) != 0;

    public static EAccessorySlotMask MaskOf(EAccessorySlot slot) => (EAccessorySlotMask)(1 << (int)slot);

    // 0번("안 씀")과 범위 밖은 항목이 없는 것으로 본다 — 부르는 쪽이 null 하나만 보면 된다
    private Item ItemAt(EAccessorySlot slot, int index)
    {
        if (index <= 0)
            return null;

        SlotEntry entry = Find(slot);
        if (entry == null || entry.Items == null || index >= entry.Items.Length)
            return null;

        Item item = entry.Items[index];
        return item == null || item.Prefab == null ? null : item;
    }

    private SlotEntry Find(EAccessorySlot slot)
    {
        for (int i = 0; i < m_slots.Length; i++)
            if (m_slots[i] != null && m_slots[i].Slot == slot)
                return m_slots[i];

        return null;
    }
}
