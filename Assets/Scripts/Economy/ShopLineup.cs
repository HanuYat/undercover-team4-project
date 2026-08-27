using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 상점 진열 추첨과 주문 (#814, #843). 카탈로그에서 소모형 고정 칸 + 랜덤 칸을 뽑아 이번 라운드
/// 칸에 배정하고, 주문창(<see cref="ShopBrowserPanel"/>)이 누른 주문을 서버 권위로 판정한다.
/// Shop 씬은 라운드마다 재로드되므로 씬 로드 1회 = 라운드 1회 추첨이 자동으로 성립한다.
///
/// <b>진열대가 없어지며 칸 상태가 여기로 모였다.</b> 예전에는 칸마다 씬 오브젝트(ShopStand)가
/// 자기 NetworkVariable과 구매 RPC를 들고 있었지만, 전시가 사라진 지금 칸은 주문창의 칸일 뿐이라
/// 씬 오브젝트로 둘 이유가 없다. 클라가 보내는 것이 "몇 번 칸"뿐이라 가격·품절을 위조할 수 없는
/// 성질은 그대로다 — 품목과 상태의 주인은 여전히 서버다.
///
/// App 파사드에 올리지 않는 이유: Shop 씬에서만 사는 씬 스코프 오브젝트다(주문창이 같은 씬이라
/// 인스펙터로 잡는다). 라운드를 넘겨야 하는 값은 <see cref="ShopPurchases"/>가 이미 들고 있다.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class ShopLineup : NetworkBehaviour
{
    /// <summary>칸 하나 — 카탈로그 인덱스와 판매 상태. NetworkList로 전 클라에 동기화된다.</summary>
    public struct Slot : INetworkSerializable, IEquatable<Slot>
    {
        public int EntryIndex; // -1 = 빈 칸
        public EShopSlotStatus Status;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer)
            where T : IReaderWriter
        {
            serializer.SerializeValue(ref EntryIndex);
            serializer.SerializeValue(ref Status);
        }

        public bool Equals(Slot other) => EntryIndex == other.EntryIndex && Status == other.Status;
    }

    [SerializeField]
    private ShopCatalog m_catalog;

    [Tooltip("주문창에 뿌릴 칸 수. 소모형·랜덤 칸의 합이 모자라면 남는 칸은 빈 칸이 된다")]
    [Min(0)]
    [SerializeField]
    private int m_slotCount = 7;

    // 서버만 쓰기, 전 클라 읽기 — DirectoryManager와 같은 패턴 (#223)
    private readonly NetworkList<Slot> m_slots = new NetworkList<Slot>();

    /// <summary>이번 라운드 칸 수 — 스폰 전이면 0.</summary>
    public int SlotCount => IsSpawned ? m_slots.Count : 0;

    /// <summary>칸의 품목·상태가 바뀌었다 — 주문창이 열려 있으면 목록을 다시 그린다.</summary>
    public event Action OnChanged;

    /// <summary>구매 응답(성공·자금 부족 등). 요청자 클라에서만 울린다.</summary>
    public event Action<string> OnPurchaseReply;

    /// <summary>칸이 파는 품목 — 빈 칸이거나 범위 밖이면 null.</summary>
    public ShopCatalog.Entry GetEntry(int slot) =>
        m_catalog != null && TryGetSlot(slot, out Slot value)
            ? m_catalog.Get(value.EntryIndex)
            : null;

    /// <summary>칸의 판매 상태 — 범위 밖이면 Available.</summary>
    public EShopSlotStatus GetStatus(int slot) =>
        TryGetSlot(slot, out Slot value) ? value.Status : EShopSlotStatus.Available;

    public override void OnNetworkSpawn()
    {
        m_slots.OnListChanged += HandleSlotsChanged;

        if (IsServer)
        {
            // 재시작 시 씬 NetworkObject의 NetworkList에 이전 세션 항목이 남는다 (#209 패턴)
            m_slots.Clear();
            AssignLineupAsync().Forget();
        }

        OnChanged?.Invoke(); // late-join도 여기서 처음 그린다
    }

    public override void OnNetworkDespawn()
    {
        m_slots.OnListChanged -= HandleSlotsChanged;
    }

    private void HandleSlotsChanged(NetworkListEvent<Slot> _) => OnChanged?.Invoke();

    private bool TryGetSlot(int slot, out Slot value)
    {
        if (IsSpawned && slot >= 0 && slot < m_slots.Count)
        {
            value = m_slots[slot];
            return true;
        }

        value = default;
        return false;
    }

    // ---- 추첨 (서버 전용) ----

    // 씬 로드 콜백 안에서 바로 네트워크 상태를 건드리지 않는다 — ShopDelivery.DeliverAsync와 같은 이유.
    private async UniTaskVoid AssignLineupAsync()
    {
        await UniTask.NextFrame(this.GetCancellationTokenOnDestroy());

        AssignLineup();
    }

    private void AssignLineup()
    {
        if (m_catalog == null)
        {
            Debug.LogWarning("ShopLineup: 카탈로그가 배선되지 않았다", this);
            return;
        }

        int slotCount = m_slotCount;
        if (slotCount <= 0)
            return;

        List<int> staples = new List<int>();
        List<int> others = new List<int>();
        for (int i = 0; i < m_catalog.Count; i++)
        {
            ShopCatalog.Entry entry = m_catalog.Get(i);
            if (entry == null || !entry.IsValid)
                continue;

            (entry.IsStaple ? staples : others).Add(i);
        }

        int stapleSlots = Mathf.Clamp(m_catalog.StapleSlots, 0, slotCount);
        int randomSlots = Mathf.Clamp(m_catalog.RandomSlots, 0, slotCount - stapleSlots);

        // 설정값 합이 칸 수와 다르면(모자라거나 후보가 한쪽뿐이면) 남는 칸을 다른 쪽으로 넘긴다.
        if (staples.Count == 0)
        {
            randomSlots = Mathf.Min(slotCount, randomSlots + stapleSlots);
            stapleSlots = 0;
        }
        else if (others.Count == 0)
        {
            stapleSlots = Mathf.Min(slotCount, stapleSlots + randomSlots);
            randomSlots = 0;
        }
        else if (stapleSlots + randomSlots < slotCount)
        {
            randomSlots = slotCount - stapleSlots;
        }

        List<int> assignment = new List<int>(slotCount);
        assignment.AddRange(PickStapleSlots(staples, stapleSlots));
        assignment.AddRange(PickRandomSlots(others, randomSlots));
        while (assignment.Count < slotCount)
            assignment.Add(-1);

        Shuffle(assignment);

        ShopPurchases purchases = App.Game.ShopPurchases;
        m_slots.Clear();
        for (int slot = 0; slot < slotCount; slot++)
            m_slots.Add(MakeSlot(assignment[slot], purchases));

        Debug.Log(
            $"[상점] 진열 추첨 — 소모형 {stapleSlots}칸, 랜덤 {randomSlots}칸, 빈 칸 {slotCount - stapleSlots - randomSlots}"
        );
    }

    // 이미 산 설치형은 처음부터 Owned로 연다 — 옛 ShopStand.ServerAssign이 하던 판정이다.
    private Slot MakeSlot(int entryIndex, ShopPurchases purchases)
    {
        ShopCatalog.Entry entry = m_catalog.Get(entryIndex);
        bool owned =
            entry != null
            && entry.IsInstallable
            && purchases != null
            && purchases.HasInstallable(entry.Installable);

        return new Slot
        {
            EntryIndex = entryIndex,
            Status = owned ? EShopSlotStatus.Owned : EShopSlotStatus.Available,
        };
    }

    // 종류별 최소 1칸을 먼저 배정하고, 남는 칸은 복원 추첨으로 채운다.
    private static List<int> PickStapleSlots(List<int> staples, int count)
    {
        List<int> result = new List<int>(count);
        if (staples.Count == 0 || count <= 0)
            return result;

        List<int> pool = new List<int>(staples);
        Shuffle(pool);
        for (int i = 0; i < pool.Count && result.Count < count; i++)
            result.Add(pool[i]);

        while (result.Count < count)
            result.Add(staples[UnityEngine.Random.Range(0, staples.Count)]);

        return result;
    }

    // 설치형은 뽑히면 사본에서 제거해 라운드 내 중복을 막는다. 소지형은 남겨 중복을 허용한다.
    private List<int> PickRandomSlots(List<int> others, int count)
    {
        List<int> result = new List<int>(count);
        if (others.Count == 0 || count <= 0)
            return result;

        List<int> pool = new List<int>(others);
        for (int i = 0; i < count && pool.Count > 0; i++)
        {
            int pickAt = UnityEngine.Random.Range(0, pool.Count);
            int index = pool[pickAt];
            result.Add(index);

            if (m_catalog.Get(index).IsInstallable)
                pool.RemoveAt(pickAt);
        }

        return result;
    }

    private static void Shuffle(List<int> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = UnityEngine.Random.Range(0, i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    // ---- 주문 (서버 권위) ----

    /// <summary>주문창이 칸의 주문 버튼을 눌렀을 때 부른다.</summary>
    public void RequestPurchase(int slot)
    {
        if (!IsSpawned)
        {
            Debug.LogWarning(
                "ShopLineup: 세션이 없어 구매할 수 없다 (정식 경로 Title→Lobby→Shop으로 진입할 것)",
                this
            );
            return;
        }

        RequestPurchaseRpc(slot);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)] // 오너 없는 씬 오브젝트
    private void RequestPurchaseRpc(int slot, RpcParams rpcParams = default)
    {
        ulong requester = rpcParams.Receive.SenderClientId;

        ShopPurchases purchases = App.Game.ShopPurchases;
        TeamFund fund = App.Game.TeamFund;
        if (purchases == null || fund == null)
        {
            Debug.LogWarning(
                "ShopLineup: 상주 홀더(TeamFund/ShopPurchases)를 찾지 못해 구매를 처리할 수 없다",
                this
            );
            return;
        }

        if (!TryGetSlot(slot, out Slot value))
        {
            Debug.LogWarning($"ShopLineup: 범위 밖 칸({slot})에 구매 요청이 들어왔다", this);
            return;
        }

        ShopCatalog.Entry entry = m_catalog != null ? m_catalog.Get(value.EntryIndex) : null;
        if (entry == null)
        {
            Debug.LogWarning("ShopLineup: 빈 칸에 구매 요청이 들어왔다", this);
            return;
        }

        if (entry.IsInstallable && purchases.HasInstallable(entry.Installable))
        {
            ReplyRpc(
                "이미 구매한 장비",
                EAudioClip.None,
                RpcTarget.Single(requester, RpcTargetUse.Temp)
            );
            return;
        }

        if (value.Status == EShopSlotStatus.SoldOut)
        {
            ReplyRpc(
                "품절된 품목",
                EAudioClip.None,
                RpcTarget.Single(requester, RpcTargetUse.Temp)
            );
            return;
        }

        if (!fund.TrySpend(entry.Price))
        {
            ReplyRpc(
                "팀 자금 부족",
                EAudioClip.None,
                RpcTarget.Single(requester, RpcTargetUse.Temp)
            );
            return;
        }

        if (entry.IsInstallable)
            purchases.AddInstallable(entry.Installable);
        else
            purchases.AddCarried(entry.ItemPrefab);

        value.Status = EShopSlotStatus.SoldOut; // 다음 라운드 재추첨 때 다시 판정된다
        m_slots[slot] = value;

        ReplyRpc(
            "주문 완료 — 다음 라운드에 본부로 배달된다",
            EAudioClip.ShopPurchase,
            RpcTarget.Single(requester, RpcTargetUse.Temp)
        );
    }

    [Rpc(SendTo.SpecifiedInParams)] // 거절은 무음 — 값만 보려고 눌러도 실패음이 나지 않게
    private void ReplyRpc(string message, EAudioClip sound, RpcParams rpcParams)
    {
        OnPurchaseReply?.Invoke(message);
        App.Sound?.PlaySfx2D(sound);
    }
}
