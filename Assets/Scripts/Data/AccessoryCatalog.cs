using System;
using UnityEngine;

/// <summary>
/// 슬롯별 치장 프리팹 목록 (#818) — <b>배열 인덱스가 곧 네트워크로 오가는 값</b>이다.
/// 인덱스 0은 "안 씀"이라 0번 원소는 비워 둔다. 수치·목록을 코드에 박지 않는다 (GDD 10-4).
/// </summary>
[CreateAssetMenu(fileName = "AccessoryCatalog", menuName = "Undercover/Player/Accessory Catalog")]
public class AccessoryCatalog : ScriptableObject
{
    [Serializable]
    private class SlotEntry
    {
        public EAccessorySlot Slot;

        [Tooltip("0번은 '안 씀'이라 비워 둘 것 — 실제 아이템은 1번부터. 콜라이더가 있는 프리팹을 넣지 말 것")]
        public GameObject[] Prefabs;
    }

    [SerializeField] private SlotEntry[] m_slots = new SlotEntry[0];

    /// <summary>그 슬롯의 항목 수 — 0번("안 씀")을 포함한 길이다. 목록이 없으면 1(안 씀만).</summary>
    public int CountOf(EAccessorySlot slot)
    {
        SlotEntry entry = Find(slot);
        return entry == null || entry.Prefabs == null ? 1 : Mathf.Max(1, entry.Prefabs.Length);
    }

    /// <summary>붙일 프리팹 — 0이거나 범위 밖이면 null("안 씀")이다.</summary>
    public GameObject Get(EAccessorySlot slot, int index)
    {
        if (index <= 0)
            return null;

        SlotEntry entry = Find(slot);
        if (entry == null || entry.Prefabs == null || index >= entry.Prefabs.Length)
            return null;

        return entry.Prefabs[index];
    }

    private SlotEntry Find(EAccessorySlot slot)
    {
        for (int i = 0; i < m_slots.Length; i++)
            if (m_slots[i] != null && m_slots[i].Slot == slot)
                return m_slots[i];

        return null;
    }
}
