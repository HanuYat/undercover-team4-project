using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 소매치기가 훔쳐 든 물건 — 훔친 순간 NPC에 붙고, 결말에서 되돌려주거나 없앤다. (#303)
///
/// 아이템은 디스폰하지 않고 <b>부모만 NPC로 옮긴다</b>. WorldItemPickup이 부모 유무로 월드 표시·줍기를
/// 켜고 끄므로, 붙이면 저절로 숨고 떼면 저절로 돌아온다.
///
/// 결말은 둘이다:
///  · <see cref="ServerDropHere"/> — 제압당하면 그 자리에 떨어뜨린다. 주우면 회수 끝.
///    <b>놓쳐서 배회로 넘어간 뒤에 잡아도 마찬가지다</b> — 들린 채 도심에 남으므로 뒤늦게
///    잡아도 되찾는다(<see cref="MisdemeanorLoiterer"/>).
///  · <see cref="ServerLose"/> — 라운드가 끝날 때까지 못 잡으면 없앤다. 구매품이면 팀 구매
///    목록에서도 빼 영구 손실로 만든다(기본 지급품은 표식이 없고, 어차피 다음 라운드에 다시 지급된다).
///
/// 판정이 서버 권위라 복제하지 않는다 — 런타임 AddComponent (MisdemeanorOffender와 같은 관례).
/// </summary>
public class StolenGoods : MonoBehaviour
{
    private ItemBase m_item;

    /// <summary>지금 훔친 물건을 들고 있는가.</summary>
    public bool HasItem => m_item != null;

    /// <summary>훔친 물건을 넘겨받아 기록한다 — 부착은 <see cref="PlayerLoadout.ServerStealRandom"/>이 이미 했다.</summary>
    public void ServerTake(ItemBase item)
    {
        m_item = item;
    }

    /// <summary>
    /// 지금 자리에 떨어뜨린다 — 제압당했을 때. 떨어진 물건은 기존 줍기로 회수한다.
    /// 위치를 먼저 옮기고 떼는 순서가 중요하다: 아이템엔 NetworkTransform이 없어 분리 시 나가는
    /// ParentSyncMessage가 위치를 복제하는 유일한 수단이다 (#361).
    /// </summary>
    public void ServerDropHere()
    {
        if (m_item == null)
            return;

        NetworkObject itemObject = m_item.NetworkObject;
        m_item = null;

        if (itemObject == null || !itemObject.IsSpawned)
            return;

        itemObject.transform.SetPositionAndRotation(transform.position, Quaternion.identity);
        itemObject.TrySetParent((Transform)null, true);
    }

    /// <summary>놓쳤다 — 물건을 없앤다. 구매품이면 다음 라운드 배달 목록에서도 빼 영구 손실로 만든다.</summary>
    public void ServerLose()
    {
        if (m_item == null)
            return;

        ItemBase item = m_item;
        m_item = null;

        if (item.TryGetComponent(out ShopDeliveredItem delivered))
            App.Game.ShopPurchases?.RemoveCarried(delivered.SourcePrefab);

        NetworkObject itemObject = item.NetworkObject;
        if (itemObject != null && itemObject.IsSpawned)
            itemObject.Despawn(destroy: true);
    }
}