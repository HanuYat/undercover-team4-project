using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 플레이어의 아이템 보유·장착·줍기·버리기를 관리한다. (#47, #46, #88)
/// 아이템은 독립 NetworkObject 프리팹이므로(#88) 서버가 스폰·소유권 부여·부착을 담당하고,
/// 오너 클라는 서버가 보낸 보유 목록으로 자기 인벤토리(HeldItems)를 재구성해 휠 전환(#46)·
/// 1인칭 손 표시(#45)에 쓴다.
///
/// 권위 구분:
/// - 서버: 시작 지급 스폰, 줍기/버리기 요청 검증, 소유권 이전, 부모(부착) 변경.
/// - 오너: 줍기/버리기 입력 발신, 휠 순환 장착, 손 표시.
/// 3인칭(타 플레이어) 장착 표시는 이 이슈 범위 밖 — 후속.
/// </summary>
// TODO: #55 서버권위 전환 시 장착/사용 실행도 서버 기준으로 (지금은 장착 상태가 오너 로컬)
[RequireComponent(typeof(PlayerItemUser))]
public class PlayerLoadout : NetworkBehaviour
{
    [Header("기본 지급 장비")]
    [Tooltip("게임 시작 시 순서대로 지급할 아이템 프리팹(NetworkObject). 첫 항목이 기본 장착된다.")]
    [SerializeField]
    private List<ItemBase> m_startingGear = new List<ItemBase>();

    [Header("장착 위치 (비우면 플레이어 루트에 부착)")]
    [Tooltip("지급·주운 아이템 인스턴스를 붙일 부모. 비우면 이 GameObject 하위에 붙는다.")]
    [SerializeField]
    private Transform m_itemAnchor;

    [Header("버리기")]
    [Tooltip("버릴 때 플레이어 정면으로 내려놓는 거리(m)")]
    [SerializeField]
    private float m_dropDistance = 1.2f;

    private readonly List<ItemBase> m_heldItems = new List<ItemBase>();
    private PlayerItemUser m_itemUser;
    private PlayerInputHandler m_inputHandler;
    private PlayerIncapacitation m_incapacitation; // 다운(무력화) 중 아이템 전환·버리기 차단용 (#105)

    // 서버 줍기 거리 검증용 사거리(m) — PlayerInteractor의 조준 사거리를 캐싱해 재사용한다.
    // 값을 따로 두지 않고 여기서 읽어야 조준-줍기 사거리가 항상 정합된다 (#147).
    private float m_pickupRange;

    // 다운(무력화) 중 여부 — 무력화 컴포넌트가 없으면(테스트 구성 등) 항상 false. (PlayerMovement 관례)
    private bool IsIncapacitated => m_incapacitation != null && m_incapacitation.IsIncapacitated;

    // 현재 장착 중인 아이템의 m_heldItems 인덱스. 보유 아이템이 없으면 -1. (#46)
    private int m_equippedIndex = -1;

    /// <summary>현재 보유 중인 아이템 목록. 마우스 휠 전환(#46)이 순환 대상으로 사용한다. (오너 로컬)</summary>
    public IReadOnlyList<ItemBase> HeldItems => m_heldItems;

    private Transform ItemParent => m_itemAnchor != null ? m_itemAnchor : transform;

    private void Awake()
    {
        m_itemUser = GetComponent<PlayerItemUser>();
        m_inputHandler = GetComponent<PlayerInputHandler>();
        m_incapacitation = GetComponent<PlayerIncapacitation>();
        m_pickupRange = GetComponent<PlayerInteractor>().Range;
    }

    public override void OnNetworkSpawn()
    {
        // 서버가 기본 장비를 스폰해 소유권을 부여하고 플레이어에 부착한다.
        // 단, OnNetworkSpawn 내부에서 NetworkObject를 스폰하면 이후 접속하는 클라의 씬 동기화가
        // 중복 스폰(같은 NetworkObjectId 재생성)으로 깨진다. 스폰 처리 캐스케이드 밖(다음 프레임)에서
        // 지급하도록 한 프레임 미룬다.
        if (IsServer)
        {
            GrantStartingGearAsync().Forget();
        }

        // 오너만 입력을 받는다 (휠 순환·버리기). 입력 핸들러는 오너 외엔 비활성.
        if (IsOwner)
        {
            m_inputHandler.OnPreviousItem += EquipPrevious;
            m_inputHandler.OnNextItem += EquipNext;
            m_inputHandler.OnDropItem += RequestDropEquipped;
        }
    }

    public override void OnNetworkDespawn()
    {
        if (IsOwner)
        {
            m_inputHandler.OnPreviousItem -= EquipPrevious;
            m_inputHandler.OnNextItem -= EquipNext;
            m_inputHandler.OnDropItem -= RequestDropEquipped;
        }
    }

    // ---- 서버: 시작 지급 ----

    // OnNetworkSpawn 밖으로 한 프레임 미뤄 지급한다 — 스폰 메시지 처리 중 스폰을 피해 후속 접속
    // 클라의 동기화 중복 스폰을 막는다.
    private async UniTaskVoid GrantStartingGearAsync()
    {
        await UniTask.NextFrame();

        // 대기 중 디스폰됐거나 더 이상 서버가 아니면 중단.
        if (this == null || !IsSpawned || !IsServer)
        {
            return;
        }

        GrantStartingGear();
    }

    // 기본 장비 프리팹을 NetworkObject로 스폰해 오너 소유로 만들고 플레이어에 부착한 뒤,
    // 오너에게 보유 목록을 동기화한다.
    private void GrantStartingGear()
    {
        Transform parent = ItemParent;

        foreach (ItemBase gearPrefab in m_startingGear)
        {
            if (gearPrefab == null)
            {
                continue;
            }

            ItemBase item = Instantiate(gearPrefab);
            NetworkObject itemNetworkObject = item.GetComponent<NetworkObject>();
            itemNetworkObject.SpawnWithOwnership(OwnerClientId);
            AttachToParent(itemNetworkObject, parent);
        }

        SyncHeldItemsRpc(BuildHeldItemRefs());
    }

    // ---- 줍기 (오너 요청 → 서버 실행) ----

    /// <summary>월드 아이템 줍기 요청. WorldItemPickup이 상호작용한 플레이어에게 호출한다. (오너에서만 유효)</summary>
    public void RequestPickup(NetworkObject itemNetworkObject)
    {
        if (!IsOwner || itemNetworkObject == null)
        {
            return;
        }

        PickupRpc(new NetworkObjectReference(itemNetworkObject));
    }

    [Rpc(SendTo.Server)]
    private void PickupRpc(NetworkObjectReference itemRef, RpcParams rpcParams = default)
    {
        if (!itemRef.TryGet(out NetworkObject itemNetworkObject))
        {
            return;
        }

        // 이미 누군가 들고 있으면(부모가 있으면) 무시 — 중복 줍기·가로채기 방지.
        if (itemNetworkObject.transform.parent != null)
        {
            return;
        }

        // 서버 거리 검증 — 위조 RPC로 원격 줍기 방지 (#147). root↔아이템 sqrMagnitude 비교.
        Vector3 toItem = itemNetworkObject.transform.position - transform.position;
        if (toItem.sqrMagnitude > m_pickupRange * m_pickupRange)
        {
            return;
        }

        ulong requester = rpcParams.Receive.SenderClientId;
        itemNetworkObject.ChangeOwnership(requester);
        AttachToParent(itemNetworkObject, ItemParent);

        SyncHeldItemsRpc(BuildHeldItemRefs());
    }

    // ---- 버리기 (오너 요청 → 서버 실행) ----

    // 현재 장착 아이템을 버린다 (버리기 입력 핸들러).
    private void RequestDropEquipped()
    {
        if (!IsOwner)
        {
            return;
        }

        // 다운(무력화) 중에는 아이템을 버릴 수 없다 (#105)
        if (IsIncapacitated)
        {
            return;
        }

        ItemBase equipped = m_itemUser.EquippedItem;
        if (equipped == null)
        {
            return;
        }

        NetworkObject itemNetworkObject = equipped.GetComponent<NetworkObject>();
        if (itemNetworkObject == null)
        {
            return;
        }

        DropRpc(new NetworkObjectReference(itemNetworkObject));
    }

    [Rpc(SendTo.Server)]
    private void DropRpc(NetworkObjectReference itemRef)
    {
        if (!itemRef.TryGet(out NetworkObject itemNetworkObject))
        {
            return;
        }

        // 이 플레이어가 실제로 들고 있는(부착된) 아이템만 버릴 수 있다.
        if (itemNetworkObject.transform.parent != ItemParent)
        {
            return;
        }

        // 플레이어에서 분리해 정면 바닥에 내려놓고, 소유권은 서버로 되돌린다(월드 상태).
        itemNetworkObject.TrySetParent((Transform)null, true);
        Vector3 dropPosition = transform.position + transform.forward * m_dropDistance;
        itemNetworkObject.transform.SetPositionAndRotation(dropPosition, Quaternion.identity);
        itemNetworkObject.RemoveOwnership();

        SyncHeldItemsRpc(BuildHeldItemRefs());
    }

    // ---- 서버 → 오너: 보유 목록 동기화 ----

    // 플레이어에 현재 부착된 아이템들의 참조 목록을 만든다 (서버 진실).
    private NetworkObjectReference[] BuildHeldItemRefs()
    {
        Transform parent = ItemParent;
        List<NetworkObjectReference> refs = new List<NetworkObjectReference>();

        for (int i = 0; i < parent.childCount; i++)
        {
            ItemBase item = parent.GetChild(i).GetComponent<ItemBase>();
            if (item != null && item.NetworkObject != null)
            {
                refs.Add(new NetworkObjectReference(item.NetworkObject));
            }
        }

        return refs.ToArray();
    }

    // 오너가 서버 진실 목록으로 자기 인벤토리를 재구성한다 — 줍기/버리기로 목록이 바뀌어도
    // 휠 순환이 최신 목록을 대상으로 하고, 장착 중이던 아이템은 신원으로 유지된다(클로버링 방지).
    [Rpc(SendTo.Owner)]
    private void SyncHeldItemsRpc(NetworkObjectReference[] itemRefs)
    {
        // 갓 스폰된 아이템 NetworkObject가 이 클라에 아직 도착하지 않았을 수 있다(스폰 메시지 vs RPC
        // 도착 순서 경쟁). 특히 원격 클라의 초기 지급에서 참조가 즉시 해석되지 않아 지급이 누락된다.
        // 전부 해석될 때까지 몇 프레임 기다렸다가 재구성한다.
        ResolveAndRebuildAsync(itemRefs).Forget();
    }

    private async UniTaskVoid ResolveAndRebuildAsync(NetworkObjectReference[] itemRefs)
    {
        const int k_maxWaitFrames = 120;
        for (int frame = 0; frame < k_maxWaitFrames && !AllResolved(itemRefs); frame++)
        {
            await UniTask.Yield(PlayerLoopTiming.Update);

            // 대기 중 디스폰(퇴장 등)되면 중단 — 파괴된 객체 접근 방지.
            if (this == null || !IsSpawned)
            {
                return;
            }
        }

        RebuildHeldItems(itemRefs);
    }

    private static bool AllResolved(NetworkObjectReference[] itemRefs)
    {
        foreach (NetworkObjectReference itemRef in itemRefs)
        {
            if (!itemRef.TryGet(out _))
            {
                return false;
            }
        }

        return true;
    }

    private void RebuildHeldItems(NetworkObjectReference[] itemRefs)
    {
        ItemBase previouslyEquipped = m_itemUser.EquippedItem;

        m_heldItems.Clear();
        foreach (NetworkObjectReference itemRef in itemRefs)
        {
            if (itemRef.TryGet(out NetworkObject itemNetworkObject)
                && itemNetworkObject.TryGetComponent(out ItemBase item))
            {
                m_heldItems.Add(item);
            }
        }

        if (m_heldItems.Count == 0)
        {
            m_equippedIndex = -1;
            m_itemUser.SetEquippedItem(null);
            return;
        }

        // 장착 중이던 아이템이 아직 목록에 있으면 그대로 유지, 아니면 첫 아이템을 장착.
        int keptIndex = previouslyEquipped != null ? m_heldItems.IndexOf(previouslyEquipped) : -1;
        m_equippedIndex = keptIndex >= 0 ? keptIndex : 0;
        m_itemUser.SetEquippedItem(m_heldItems[m_equippedIndex]);
    }

    // ---- 휠 순환 장착 (#46, 오너 로컬) ----

    // 마우스 휠 위 — 보유 목록의 이전 아이템으로 순환 전환.
    private void EquipPrevious() => Cycle(-1);

    // 마우스 휠 아래 — 보유 목록의 다음 아이템으로 순환 전환.
    private void EquipNext() => Cycle(1);

    // 현재 인덱스에서 direction만큼 이동해 순환 장착한다. 보유 아이템이 없으면 무시.
    private void Cycle(int direction)
    {
        // 다운(무력화) 중에는 마우스 휠 아이템 전환 차단 (#105)
        if (IsIncapacitated)
        {
            return;
        }

        int count = m_heldItems.Count;
        if (count == 0)
        {
            return;
        }

        // 아직 장착 인덱스가 없으면(빈손 상태) 첫 아이템부터 시작한다.
        int baseIndex = m_equippedIndex >= 0 ? m_equippedIndex : 0;

        // % 결과가 음수일 수 있으므로 count를 더해 양수 범위로 보정.
        m_equippedIndex = (baseIndex + direction % count + count) % count;
        m_itemUser.SetEquippedItem(m_heldItems[m_equippedIndex]);
    }

    // ---- 공통 ----

    // 스폰된 아이템을 플레이어 부모에 부착하고 로컬 원점에 맞춘다 (서버에서 호출).
    private static void AttachToParent(NetworkObject itemNetworkObject, Transform parent)
    {
        itemNetworkObject.TrySetParent(parent, false);
        itemNetworkObject.transform.localPosition = Vector3.zero;
        itemNetworkObject.transform.localRotation = Quaternion.identity;
    }
}
