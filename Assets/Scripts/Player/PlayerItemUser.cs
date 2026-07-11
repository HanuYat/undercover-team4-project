using System;
using UnityEngine;

[RequireComponent(typeof(PlayerInputHandler))]
public class PlayerItemUser : MonoBehaviour
{
    [Header("장착 아이템")]
    [SerializeField] private ItemBase m_equippedItem;

    private PlayerInputHandler m_inputHandler;

    /// <summary>현재 장착 중인 아이템. 없으면 null. (#45 — PlayerHandView가 초기 표시에 사용)</summary>
    public ItemBase EquippedItem => m_equippedItem;

    /// <summary>장착 아이템 변경 이벤트 — 실제로 값이 바뀔 때만 발행. 1인칭 손 표시(#45)·UI 등이 구독한다.</summary>
    public event Action<ItemBase> OnEquippedItemChanged;

    private void Awake()
    {
        m_inputHandler = GetComponent<PlayerInputHandler>();
    }

    private void OnEnable()
    {
        m_inputHandler.OnAttackPerformed += HandleUseItem;
    }

    private void OnDisable()
    {
        m_inputHandler.OnAttackPerformed -= HandleUseItem;
    }

    public void SetEquippedItem(ItemBase item)
    {
        if (m_equippedItem == item)
        {
            return;
        }

        m_equippedItem = item;
        OnEquippedItemChanged?.Invoke(item);
    }

    private void HandleUseItem()
    {
        if (m_equippedItem == null || !m_equippedItem.CanUse())
        {
            return;
        }

        m_equippedItem.Use();
    }
}
