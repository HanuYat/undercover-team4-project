using System;
using System.Collections.Generic;

/// <summary>
/// 플레이어 소지 슬롯의 순수 배치 모델 — 고정 칸 배열 위의 인덱스 연산(대조·순환·선택·스왑)만 담당한다. (#144)
/// Unity·Netcode에 전혀 의존하지 않아 단독 인스턴스화·단위 테스트가 가능하다(그래서 제네릭 T).
/// 아이템 스폰·부착·소유권·네트워크 동기화 같은 부수효과는 소유자(PlayerLoadout)가 이 모델의 결과를 받아 수행한다.
///
/// 책임 경계: "어느 칸에 무엇이 있고 지금 어느 칸이 선택됐나"만 안다. 그 항목이 실제로 어떻게 표시·전송되는지는 모른다.
/// </summary>
/// <typeparam name="T">슬롯에 담기는 항목 타입(참조 타입). 런타임에선 ItemBase, 테스트에선 임의 더미.</typeparam>
public sealed class LoadoutSlots<T>
    where T : class
{
    private static readonly EqualityComparer<T> s_comparer = EqualityComparer<T>.Default;

    // 빈 칸 = null. 배치(어느 칸에 있는지)만 유지한다.
    private readonly T[] m_slots;

    // 현재 선택(장착) 칸 인덱스. 빈손이면 -1. 빈 칸을 선택하면 그 칸 인덱스가 된다.
    private int m_equippedIndex = -1;

    public LoadoutSlots(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(capacity),
                "슬롯 용량은 1 이상이어야 한다."
            );
        }

        m_slots = new T[capacity];
    }

    /// <summary>고정 칸(빈 칸 = null). 읽기 전용 뷰.</summary>
    public IReadOnlyList<T> Slots => m_slots;

    /// <summary>칸 수(용량).</summary>
    public int Capacity => m_slots.Length;

    /// <summary>현재 선택 인덱스. 빈손이면 -1.</summary>
    public int EquippedIndex => m_equippedIndex;

    /// <summary>현재 선택 칸의 항목. 빈손이거나 빈 칸을 선택 중이면 null.</summary>
    public T Equipped => m_equippedIndex >= 0 ? m_slots[m_equippedIndex] : null;

    /// <summary>index가 칸 범위 안인지 (빈손 -1은 유효 인덱스가 아님).</summary>
    public bool IsValidIndex(int index) => index >= 0 && index < m_slots.Length;

    /// <summary>첫 빈 칸 인덱스. 꽉 찼으면 -1.</summary>
    public int FirstEmptySlot()
    {
        for (int i = 0; i < m_slots.Length; i++)
        {
            if (m_slots[i] == null)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>첫 항목이 든 칸 인덱스. 전부 비었으면 -1.</summary>
    public int FirstOccupiedSlot()
    {
        for (int i = 0; i < m_slots.Length; i++)
        {
            if (m_slots[i] != null)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>선택 인덱스를 설정한다. -1(빈손) 또는 유효 칸을 넘긴다 — 유효성은 호출부 책임.</summary>
    public void SetEquippedIndex(int index) => m_equippedIndex = index;

    /// <summary>
    /// 현재 선택에서 direction만큼 순환 이동한 칸 인덱스를 계산해 반환한다(상태는 바꾸지 않음).
    /// 빈손(-1)이면 첫 칸 기준. 빈 칸도 대상이라 결과가 빈손이 될 수 있다.
    /// </summary>
    public int NextIndex(int direction)
    {
        int baseIndex = m_equippedIndex >= 0 ? m_equippedIndex : 0;
        int length = m_slots.Length;

        // % 결과가 음수일 수 있으므로 length를 더해 양수 범위로 보정.
        return ((baseIndex + direction) % length + length) % length;
    }

    /// <summary>
    /// 두 칸의 내용을 맞바꾸고, 선택 인덱스가 그 칸을 가리켰으면 함께 따라 옮긴다.
    /// 같은 칸이거나 범위를 벗어나면 아무것도 하지 않고 false. 성공하면 true.
    /// </summary>
    public bool TrySwap(int a, int b)
    {
        if (a == b || a < 0 || b < 0 || a >= m_slots.Length || b >= m_slots.Length)
        {
            return false;
        }

        (m_slots[a], m_slots[b]) = (m_slots[b], m_slots[a]);

        // 장착 슬롯이 이동했으면 인덱스만 따라간다 — 장착 항목 자체는 그대로.
        if (m_equippedIndex == a)
        {
            m_equippedIndex = b;
        }
        else if (m_equippedIndex == b)
        {
            m_equippedIndex = a;
        }

        return true;
    }

    /// <summary>
    /// 진실 목록(incoming)으로 칸 배치를 대조(reconcile)한다 — positional. (#144)
    /// 1) 목록에서 사라진 항목은 그 칸만 비운다(버린 자리 유지).
    /// 2) 목록에 새로 생긴 항목은 첫 빈 칸에 넣는다(줍기/초기 지급). 칸 초과분은 무시(방어적).
    /// 선택 인덱스는 바꾸지 않고, 유지해야 할 인덱스만 계산해 반환한다:
    /// 현재 선택이 있으면 그대로, 아직 없으면(-1) 첫 항목 칸.
    /// </summary>
    public int Reconcile(IReadOnlyList<T> incoming)
    {
        // 1) 사라진 항목의 칸을 비운다.
        for (int i = 0; i < m_slots.Length; i++)
        {
            if (m_slots[i] != null && !Contains(incoming, m_slots[i]))
            {
                m_slots[i] = null;
            }
        }

        // 2) 새 항목을 첫 빈 칸에 넣는다.
        for (int i = 0; i < incoming.Count; i++)
        {
            T item = incoming[i];
            if (Array.IndexOf(m_slots, item) >= 0)
            {
                continue;
            }

            int emptySlot = FirstEmptySlot();
            if (emptySlot < 0)
            {
                break;
            }

            m_slots[emptySlot] = item;
        }

        // 3) 유지할 선택 인덱스 (positional): 현재 선택 유지, 없으면 첫 항목 칸.
        return m_equippedIndex >= 0 ? m_equippedIndex : FirstOccupiedSlot();
    }

    private static bool Contains(IReadOnlyList<T> list, T item)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (s_comparer.Equals(list[i], item))
            {
                return true;
            }
        }

        return false;
    }
}
