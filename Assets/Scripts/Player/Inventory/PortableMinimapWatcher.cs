using Unity.Netcode;
using UnityEngine;

// 장착 아이템이 PortableMinimap이면 HUD 위젯을 켠다 (#835). Player 프리팹에 붙어 오너 클라에서만
// 돈다 — AreaScanPresenter와 같은 구조. HUD는 로컬 인스턴스(App.UI.PortableMinimap)라 ?.로
// 널가드한다 — 오너 스폰 시점엔 HUD가 아직 안 만들어졌을 수 있다.
public class PortableMinimapWatcher : NetworkBehaviour
{
    private PlayerItemUser m_itemUser;

    public override void OnNetworkSpawn()
    {
        if (!IsOwner)
        {
            enabled = false;
            return;
        }

        m_itemUser = GetComponentInParent<PlayerItemUser>();
        if (m_itemUser == null)
        {
            Debug.LogWarning("PortableMinimapWatcher: PlayerItemUser를 찾지 못함", this);
            return;
        }

        m_itemUser.OnEquippedItemChanged += HandleEquippedItemChanged;
        HandleEquippedItemChanged(m_itemUser.EquippedItem);
    }

    public override void OnNetworkDespawn()
    {
        if (m_itemUser != null)
            m_itemUser.OnEquippedItemChanged -= HandleEquippedItemChanged;
    }

    private void HandleEquippedItemChanged(ItemBase item) =>
        App.UI.PortableMinimap?.SetVisible(item is PortableMinimap);
}
