using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 소매치기 NPC의 <b>탈취 행동</b> — 물건을 채는 동작과 훔친 물건의 결말을 함께 맡는다. (#303)
///
/// 이 부품이 행위 주체다: 피해자에게서는 소지품 <b>목록만</b> 받아 오고
/// (<see cref="PlayerLoadout.CollectDetachableItems"/>), 어느 것을 채고 어디에 두고 언제 잃는지는 여기서 정한다.
/// 플레이어 쪽에 탈취 로직을 두면 "털린다"가 소지품 관리의 일부가 되어 버린다.
///
/// 아이템은 디스폰하지 않고 <b>부모만 NPC로 옮긴다</b>. WorldItemPickup이 부모 유무로 월드 표시·줍기를
/// 켜고 끄므로, 붙이면 저절로 숨고 떼면 저절로 돌아온다.
///
/// 결말은 둘이다:
///  · <see cref="ServerDropStolenItem"/> — 제압당하면 그 자리에 떨어뜨린다. 주우면 회수 끝.
///    <b>놓쳐서 배회로 넘어간 뒤에 잡아도 마찬가지다</b> — 들린 채 도심에 남으므로 뒤늦게
///    잡아도 되찾는다(<see cref="MisdemeanorLoiterer"/>).
///  · <see cref="ServerLoseStolenItem"/> — 라운드가 끝날 때까지 못 잡으면 없앤다. 구매품이면 팀 구매
///    목록에서도 빼 영구 손실로 만든다(기본 지급품은 표식이 없고, 어차피 다음 라운드에 다시 지급된다).
///
/// 판정이 서버 권위라 복제하지 않는다 — 런타임 AddComponent (MisdemeanorOffender와 같은 관례).
/// </summary>
public class Pickpocket : MonoBehaviour
{
    // 탈취 후보 버퍼 — 탈취마다 새 List를 만들지 않게 재사용한다. 소매치기 하나당 하나라 경합이 없다.
    private readonly List<ItemBase> m_candidates = new List<ItemBase>();

    private ItemBase m_stolen;

    /// <summary>
    /// 피해자의 소지품 하나를 무작위로 채 이 NPC 밑으로 옮긴다 — 밀착한 순간 서버가 부른다.
    /// 뺏을 것이 없으면(빈손이거나 묶어 둔 밧줄뿐) null — 부르는 쪽은 그래도 도주로 넘긴다.
    ///
    /// 어느 것을 뺏을지는 고르지 않는다(무작위) — 무엇을 잃었는지가 매번 달라지는 것이 이 이벤트의 값이고,
    /// 특정 아이템을 노리게 하면 그 아이템을 든 사람만 표적이 되는 규칙이 하나 더 생긴다.
    /// </summary>
    /// <returns>훔친 물건 — 부르는 쪽의 로그·알림용이다. 물건의 뒷일은 이 부품이 계속 쥔다.</returns>
    public ItemBase ServerStealFrom(PlayerLoadout victim)
    {
        if (victim == null)
            return null;

        victim.CollectDetachableItems(m_candidates);
        if (m_candidates.Count == 0)
            return null;

        ItemBase stolen = m_candidates[Random.Range(0, m_candidates.Count)];
        m_candidates.Clear(); // 고른 뒤에는 후보를 들고 있지 않는다 — 파괴된 아이템 참조가 남지 않게

        // 채널링 중이면 끊는다 — 소유권을 잃은 뒤엔 오너의 취소 RPC가 막혀 배터리가 새고 오완료된다 (드롭과 같은 처리)
        stolen.ServerCancelActiveUse();

        NetworkObject stolenObject = stolen.NetworkObject;
        stolenObject.RemoveOwnership();
        stolenObject.TrySetParent(transform, false);
        stolenObject.transform.localPosition = Vector3.zero;
        stolenObject.transform.localRotation = Quaternion.identity;

        m_stolen = stolen;
        victim.ServerNotifyHeldItemsChanged(); // 오너 인벤토리 재구성은 소지품 주인이 알린다
        return stolen;
    }

    // 몸에서 옆으로 밀어내는 거리(m)와 높이(m).
    //
    // 몸 안쪽(transform.position)에 두면 쓰러진 소매치기와 겹쳐 <b>조준·줍기가 막힌다</b> —
    // 줍기는 조준 레이와 가시선을 보므로(WorldItemPickup·PlayerInteractor) 시체 같은 큰 콜라이더에
    // 파묻힌 아이템은 눈에도 안 보이고 집히지도 않는다. 옆에 내려놓으면 바로 보이고 바로 집힌다.
    private const float k_dropSideDistance = 0.8f;
    private const float k_dropHeight = 0.2f;

    // 착지면으로 인정할 레이어 — 기본 Default. 런타임 AddComponent(#303)라 인스펙터로 못 바꾸므로
    // 다른 곳(ShopDelivery 등)의 SerializeField 기본값과 같은 값을 상수로 둔다.
    private const int k_groundMask = 1;

    /// <summary>
    /// <b>몸 옆에</b> 떨어뜨린다 — 무력화된 순간. 떨어진 물건은 기존 줍기로 회수한다.
    /// 위치를 먼저 옮기고 떼는 순서가 중요하다: 아이템엔 NetworkTransform이 없어 분리 시 나가는
    /// ParentSyncMessage가 위치를 복제하는 유일한 수단이다 (#361).
    /// </summary>
    public void ServerDropStolenItem()
    {
        if (m_stolen == null)
            return;

        NetworkObject stolenObject = m_stolen.NetworkObject;
        m_stolen = null;

        if (stolenObject == null || !stolenObject.IsSpawned)
            return;

        Vector3 dropPosition = ResolveDropPosition();
        stolenObject.transform.SetPositionAndRotation(dropPosition, Quaternion.identity);
        WorldItemPickup.SettleOnGround(stolenObject.gameObject, dropPosition.y);
        stolenObject.TrySetParent((Transform)null, true);
    }

    // 몸 옆 바닥 지점 — 좌우 중 벽에 막히지 않은 쪽을 고른다.
    // 골목에서 옆이 벽이면 아이템이 벽 너머로 넘어가 영영 회수 불가가 된다(버리기 #360과 같은 사정).
    private Vector3 ResolveDropPosition()
    {
        Vector3 origin = transform.position + Vector3.up * k_dropHeight;

        // 쓰러진 몸은 forward가 눕어 있을 수 있어 right를 수평으로 다시 세운다
        Vector3 side = transform.right;
        side.y = 0f;
        if (side.sqrMagnitude < 0.01f)
            side = Vector3.right;
        side.Normalize();

        Vector3 candidate = origin; // 양쪽이 다 막혔다 — 발밑이 벽 너머보다 낫다

        foreach (Vector3 dir in new[] { side, -side })
        {
            if (!Physics.Raycast(origin, dir, k_dropSideDistance, ~0, QueryTriggerInteraction.Ignore))
            {
                candidate = origin + dir * k_dropSideDistance;
                break;
            }
        }

        // 옆이 인도 턱·계단처럼 몸보다 높으면 발밑 높이가 실제 지면보다 아래라 파묻힌다(#937,
        // 버리기와 같은 결함). 같은 하향 레이캐스트로 실제 지면 y를 구한다.
        return DeliveryScatter.SnapToGround(candidate, k_groundMask);
    }

    /// <summary>놓쳤다 — 물건을 없앤다. 구매품이면 다음 라운드 배달 목록에서도 빼 영구 손실로 만든다.</summary>
    public void ServerLoseStolenItem()
    {
        if (m_stolen == null)
            return;

        ItemBase stolen = m_stolen;
        m_stolen = null;

        if (stolen.TryGetComponent(out ShopDeliveredItem delivered))
            App.Game.ShopPurchases?.RemoveCarried(delivered.SourcePrefab);

        NetworkObject stolenObject = stolen.NetworkObject;
        if (stolenObject != null && stolenObject.IsSpawned)
            stolenObject.Despawn(destroy: true);
    }
}
