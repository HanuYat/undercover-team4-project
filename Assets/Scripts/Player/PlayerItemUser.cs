using UnityEngine;

[RequireComponent(typeof(PlayerInputHandler))]
public class PlayerItemUser : MonoBehaviour
{
    [Header("장착 아이템")]
    [SerializeField] private ItemBase m_equippedItem;

    private PlayerInputHandler m_inputHandler;

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
        m_equippedItem = item;
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
