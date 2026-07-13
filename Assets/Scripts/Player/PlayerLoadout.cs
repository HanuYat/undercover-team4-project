using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 게임 시작 시 플레이어에게 기본 장비를 지급한다. (#47)
/// 지정된 아이템 프리팹들을 인스턴스화해 플레이어 하위에 붙이고, 보유 목록으로 들고 있다가
/// 기본 아이템 하나를 PlayerItemUser에 장착한다.
/// 보유 목록(HeldItems)은 이후 마우스 휠 아이템 전환(#46)이 순환 대상으로 사용한다.
/// </summary>
// TODO: 네트워크 전환(#55) 시 서버 권위로 지급하도록 (서버가 아이템을 스폰/소유권 부여 후 동기화)
[RequireComponent(typeof(PlayerItemUser))]
public class PlayerLoadout : MonoBehaviour
{
    [Header("기본 지급 장비")]
    [Tooltip("게임 시작 시 순서대로 지급할 아이템 프리팹. 첫 항목이 기본 장착된다.")]
    [SerializeField]
    private List<ItemBase> m_startingGear = new List<ItemBase>();

    [Header("장착 위치 (비우면 플레이어 루트에 부착)")]
    [Tooltip("지급된 아이템 인스턴스를 붙일 부모. 비우면 이 GameObject 하위에 붙는다.")]
    [SerializeField]
    private Transform m_itemAnchor;

    private readonly List<ItemBase> m_heldItems = new List<ItemBase>();
    private PlayerItemUser m_itemUser;

    /// <summary>현재 보유 중인 아이템 목록. 마우스 휠 전환(#46)이 순환 대상으로 사용한다.</summary>
    public IReadOnlyList<ItemBase> HeldItems => m_heldItems;

    private void Awake()
    {
        m_itemUser = GetComponent<PlayerItemUser>();
    }

    private void Start()
    {
        GrantStartingGear();
    }

    // 기본 장비 프리팹을 인스턴스화해 보유 목록에 등록하고 첫 아이템을 장착한다.
    private void GrantStartingGear()
    {
        Transform parent = m_itemAnchor != null ? m_itemAnchor : transform;

        foreach (ItemBase gearPrefab in m_startingGear)
        {
            if (gearPrefab == null)
            {
                continue;
            }

            ItemBase item = Instantiate(gearPrefab, parent);
            m_heldItems.Add(item);
        }

        // 첫 지급 아이템을 기본 장착 — 없으면 빈손으로 둔다.
        if (m_heldItems.Count > 0)
        {
            m_itemUser.SetEquippedItem(m_heldItems[0]);
        }
    }
}
