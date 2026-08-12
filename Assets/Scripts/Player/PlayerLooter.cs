using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 아군 약탈 — 기능 정지(Die)된 동료의 소지품과 개인 자금을 빼앗는다. 서버 권위 허브. (#487, GDD 7-5 · 9-2)
///
/// <b>터는 쪽</b>만 맡는다. 당하는 쪽(털릴 수 있는 상태인가·무엇을 들고 있나·피해 알림)은
/// <see cref="PlayerLootable"/>이고, 이 컴포넌트는 소매치기(<see cref="Pickpocket"/>, #303)처럼
/// <b>행위 주체</b>다 — 대상에게서는 목록만 받아 오고, 무엇을 언제 가져갈지는 여기서 정한다.
///
/// <b>세션 상태를 두지 않는다</b> — "누가 누구를 털고 있는가"를 서버가 기억하지 않고, 열기·가져가기
/// 요청마다 <see cref="CanLoot"/>로 처음부터 다시 검증한다(#118 요청/실행 분리). 약탈 창(UI)은 순전히
/// 약탈자 클라의 표시이고, 그 창이 열려 있다는 사실은 서버에서 아무 권한도 되지 않는다 —
/// 창을 띄워 둔 채 대상이 부활하거나 멀어져도 다음 요청이 그냥 거부된다.
///
/// 이전 절차 자체는 소매치기 NPC(<see cref="Pickpocket.ServerStealFrom"/>)가 검증한 순서를 그대로
/// 따른다 — 채널링 끊기 → 소유권 이전 → 부착 → <b>양쪽</b> 오너에게 목록 동기화.
/// 배터리 잔량 같은 아이템 상태는 아이템 NetworkObject에 실려 있어 저절로 따라온다(<see cref="ItemBattery"/>).
/// </summary>
public class PlayerLooter : ChanneledInteractionBehaviour
{
    // 사거리는 조준·윤곽선과 같은 기준 — PlayerInteractor.Range 재사용 (#147 패턴, #184)
    private const float k_fallbackRange = 3f; // 테스트 구성 등 PlayerInteractor가 없을 때

    private PlayerInteractor m_interactor;
    private PlayerIncapacitation m_incapacitation; // 쓰러진 내가 남을 털지 못하게
    private PlayerLoadout m_loadout; // 가져온 것을 받을 내 소지품
    private PlayerWallet m_wallet; // 뺏은 자금이 들어올 내 지갑

    // 약탈 가능 소지품 후보 버퍼 — 요청마다 새 List를 만들지 않게 재사용한다 (Pickpocket과 같은 관례).
    // 서버 검증 전용이다. 오너 표시 쪽(ShowLoot)은 자기 목록을 따로 만든다 — 호스트에서는 둘이 같은
    // 피어에서 도는데, 한 버퍼를 나눠 쓰면 표시가 검증 중간 상태를 들여다보게 된다.
    private readonly List<ItemBase> m_candidates = new List<ItemBase>();

    private void Awake()
    {
        m_interactor = GetComponent<PlayerInteractor>();
        m_incapacitation = GetComponent<PlayerIncapacitation>();
        m_loadout = GetComponent<PlayerLoadout>();
        m_wallet = GetComponent<PlayerWallet>();
    }

    // ---- 오너 클라 진입점 (LootableBodyInteractable·약탈 창이 호출) ----

    /// <summary>
    /// 약탈 열기 요청 — 쓰러진 동료를 겨냥한 E가 부른다 (<see cref="LootableBodyInteractable"/>).
    /// 서버가 대상을 확인하면 <b>개인 자금은 그 자리에서 전액 넘어오고</b>, 소지품은 창에서 골라 가져간다.
    ///
    /// 자금을 고르게 하지 않는 이유는 표시할 방법이 없기 때문이다 — 개인 잔액은 읽기 권한이
    /// Owner라(#484) 약탈자 클라가 남의 잔액을 읽을 경로 자체가 없다. 결과 금액만 서버가
    /// 양쪽 오너에게 알린다.
    /// </summary>
    public void RequestOpenLoot(PlayerLootable victim)
    {
        if (victim == null)
            return;

        // 서버(호스트 포함)·오프라인은 로컬 참조로 바로 실행 (PlayerCarrier.RequestCarry 관례)
        if (HasServerAuthority)
        {
            ServerOpenLoot(victim);
            return;
        }
        if (!IsOwner)
            return; // 남의 플레이어 오브젝트에서 온 호출 방지
        if (!IsNetworkReady(victim))
            return;

        OpenLootRpc(new NetworkObjectReference(victim.NetworkObject));
    }

    /// <summary>소지품 하나를 가져가는 요청 — 약탈 창에서 항목을 고르면 부른다. (#487)</summary>
    public void RequestTakeItem(PlayerLootable victim, ItemBase item)
    {
        if (victim == null || item == null)
            return;

        NetworkObject itemObject = item.NetworkObject;
        if (itemObject == null)
            return;

        if (HasServerAuthority)
        {
            ServerTakeItem(victim, itemObject);
            return;
        }
        if (!IsOwner)
            return;
        if (!IsNetworkReady(victim) || !itemObject.IsSpawned)
            return;

        TakeItemRpc(
            new NetworkObjectReference(victim.NetworkObject),
            new NetworkObjectReference(itemObject)
        );
    }

    // 원격 클라 → 서버로 대상을 넘기려면 스폰돼 있어야 한다(NetworkObjectReference 제약).
    private bool IsNetworkReady(PlayerLootable victim)
    {
        if (victim.NetworkObject != null && victim.NetworkObject.IsSpawned)
            return true;

        Debug.LogWarning($"약탈 요청 무시 — 대상이 네트워크 스폰되지 않음: {victim.name}", this);
        return false;
    }

    // ---- 서버 RPC (오너 → 서버) ----

    [Rpc(SendTo.Server)]
    private void OpenLootRpc(NetworkObjectReference victimRef)
    {
        if (TryResolveVictim(victimRef, out PlayerLootable victim))
            ServerOpenLoot(victim);
    }

    [Rpc(SendTo.Server)]
    private void TakeItemRpc(NetworkObjectReference victimRef, NetworkObjectReference itemRef)
    {
        if (TryResolveVictim(victimRef, out PlayerLootable victim)
            && itemRef.TryGet(out NetworkObject itemObject))
        {
            ServerTakeItem(victim, itemObject);
        }
    }

    private static bool TryResolveVictim(NetworkObjectReference victimRef, out PlayerLootable victim)
    {
        victim = null;
        return victimRef.TryGet(out NetworkObject victimObject)
            && victimObject.TryGetComponent(out victim);
    }

    // ---- 서버 실행 (권위) ----

    /// <summary>
    /// 공통 관문 — 열기·가져가기가 <b>매번</b> 통과해야 한다. 자금과 아이템이 같은 검증을 쓰는 것이
    /// 중요하다: 둘로 갈라 두면 한쪽만 조건이 밀려도 티가 나지 않는다.
    /// 위조 RPC로 멀쩡한 동료를 털거나 벽 너머로 손을 뻗는 것을 여기서 막는다 (#487 완료 기준 4번).
    /// 클라 조기검증(<see cref="LootableBodyInteractable.CanInteract"/>)과 같은 기준이라
    /// "윤곽선은 뜨는데 눌러도 반응이 없는" 어긋남이 생기지 않는다 (#184).
    /// </summary>
    private bool CanLoot(PlayerLootable victim)
    {
        if (victim == null || victim.gameObject == gameObject)
            return false; // 자기 자신은 못 턴다
        if (m_incapacitation != null && m_incapacitation.IsIncapacitated)
            return false; // 쓰러진 사람이 남을 털 수는 없다
        if (!victim.CanBeLooted)
            return false; // 기능 정지(Die)만 — 멀쩡한 동료·기절한 동료는 대상이 아니다

        return IsInRange(victim);
    }

    private void ServerOpenLoot(PlayerLootable victim)
    {
        if (!HasServerAuthority)
            return;
        if (!CanLoot(victim))
            return;

        // 개인 자금은 여는 순간 전액 넘어온다 — 이전이라 총량은 늘지 않는다 (PlayerWallet.ServerTransferAllTo)
        int stolenFunds = 0;
        if (victim.Wallet != null && m_wallet != null)
            stolenFunds = victim.Wallet.ServerTransferAllTo(m_wallet);

        if (stolenFunds > 0)
            victim.ServerNotifyRobbedFunds(stolenFunds);

        NotifyLootOpened(victim, stolenFunds);
    }

    private void ServerTakeItem(PlayerLootable victim, NetworkObject itemObject)
    {
        if (!HasServerAuthority)
            return;
        if (!CanLoot(victim))
            return;
        if (itemObject == null || !itemObject.IsSpawned)
            return;

        PlayerLoadout victimLoadout = victim.Loadout;
        if (victimLoadout == null || m_loadout == null)
            return;

        // 그 몸에 실제로 붙어 있는 것만 — 부착 여부가 소지의 진실이다 (PlayerLoadout.DropRpc와 같은 판정)
        if (!victimLoadout.Held.Holds(itemObject))
            return;

        // 묶어 둔 밧줄은 뺄 수 없다 — 줄이 손을 떠나면 묶인 NPC가 주인 없이 남는다 (#369).
        // 버리기·소매치기와 같은 기준을 쓰려고 목록으로 받아 대조한다.
        if (!IsDetachable(victimLoadout, itemObject))
            return;

        // 소지 3칸 제한 (#144) — 꽉 차면 거부한다. 자동 드롭·스왑은 하지 않는다:
        // 약탈자가 의도하지 않은 아이템이 바닥에 떨어지는 편이 못 가져가는 것보다 나쁘다.
        if (m_loadout.Held.Count >= PlayerLoadout.k_maxHeldItems)
        {
            NotifyOwner("약탈 실패 — 소지 슬롯이 꽉 찼다 (먼저 버릴 것)");
            return;
        }

        itemObject.TryGetComponent(out ItemBase item);

        // 채널링 중이면 끊는다 — 소유권을 잃은 뒤엔 옛 오너의 취소 RPC가 막혀 배터리가 새고
        // 오완료된다 (버리기·소매치기와 같은 처리)
        if (item != null)
            item.ServerCancelActiveUse();

        itemObject.ChangeOwnership(OwnerClientId);
        m_loadout.Held.Attach(itemObject);

        // 양쪽 오너가 각자 인벤토리를 재구성해야 한다 — 한쪽만 알리면 털린 쪽 화면에 유령 아이템이 남는다
        victimLoadout.ServerNotifyHeldItemsChanged();
        m_loadout.ServerNotifyHeldItemsChanged();

        victim.ServerNotifyRobbedItem();
        NotifyOwner($"[약탈] 소지품 확보 — {(item != null ? item.name : itemObject.name)} ({victim.name})");
    }

    // 손에서 떼어 낼 수 있는 소지품인가 — 기준은 버리기·소매치기와 같다 (PlayerLoadout.CollectDetachableItems).
    private bool IsDetachable(PlayerLoadout victimLoadout, NetworkObject itemObject)
    {
        victimLoadout.CollectDetachableItems(m_candidates);

        bool found = false;
        foreach (ItemBase candidate in m_candidates)
        {
            if (candidate != null && candidate.NetworkObject == itemObject)
            {
                found = true;
                break;
            }
        }

        m_candidates.Clear(); // 파괴된 아이템 참조를 들고 있지 않는다
        return found;
    }

    // ---- 약탈자 쪽 결과 (서버 → 오너) ----

    // 서버가 대상을 확인해 준 뒤에야 창이 열린다 — 클라가 혼자 판단해 열면 서버가 거부할 대상 앞에서도
    // 창이 뜬다. (PlayerCarrier의 오너 피드백과 같은 분기)
    private void NotifyLootOpened(PlayerLootable victim, int stolenFunds)
    {
        if (IsSpawned && IsServer && !IsOwner)
        {
            LootOpenedRpc(new NetworkObjectReference(victim.NetworkObject), stolenFunds);
            return;
        }

        ShowLoot(victim, stolenFunds);
    }

    [Rpc(SendTo.Owner)]
    private void LootOpenedRpc(NetworkObjectReference victimRef, int stolenFunds)
    {
        if (TryResolveVictim(victimRef, out PlayerLootable victim))
            ShowLoot(victim, stolenFunds);
    }

    // 약탈자 오너 로컬 — 창을 열고, 자금 결과를 로그로 남긴다.
    private void ShowLoot(PlayerLootable victim, int stolenFunds)
    {
        // 개인 자금은 표시할 화면이 없다(잔액 읽기 권한이 Owner라 애초에 남의 것을 못 읽는다) —
        // 로그가 유일한 확인 경로라 창이 열리든 말든 남긴다.
        if (stolenFunds > 0)
            Debug.Log($"[약탈] 개인 자금 강탈 — {victim.name}에게서 {stolenFunds}");
        else
            Debug.Log($"[약탈] {victim.name}의 개인 자금은 비어 있다");

        // 소지품 목록·가져가기는 창이 맡는다. 창이 없는 구성(HUD 없는 씬)에서도 자금은 이미 넘어왔다 —
        // 여는 것과 자금 이전은 서버에서 한 동작이고, 창은 그 결과의 표시일 뿐이다.
        if (App.UI.Current != null && App.UI.Current.TryGetPanel(out LootPanel panel))
            panel.Open(this, victim);
        else
            Debug.LogWarning("[약탈] 약탈 창(LootPanel)을 찾지 못했다 — 소지품을 가져갈 수 없다");
    }

    private bool IsInRange(PlayerLootable victim) =>
        PlayerInteractor.IsWithinReach(
            m_interactor,
            victim.transform,
            PlayerInteractor.RangeOf(m_interactor, k_fallbackRange),
            transform.position
        );
}
