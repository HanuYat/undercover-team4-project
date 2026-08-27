using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 상점 구매 목록(#182) — 세션 내내 유지되는 상주 홀더. TeamFund와 같은 프리팹에 동거해
/// SessionObjectSpawner가 세션 시작 시 1회 스폰(destroyWithScene:false)한다. (#214 §6 이월 구조)
///
/// 구매품은 구매자 인벤토리로 직행하지 않는다 — <b>팀 소유</b>이고, 게임 씬 진입 시 ShopDelivery가
/// 이 목록을 훑어 본부 택배 지점에 다시 지급한다. 라운드 사이 이월을 오브젝트 생존이 아니라
/// 데이터로 처리하는 #370 방침의 지급쪽 근거 데이터다(회수는 ShopManager·PlayerLoadout이 한다).
///
/// <b>배달용 컬렉션 2개는 서버 전용이다</b> — 프리팹 참조·enum이라 그대로 실어 보낼 수 없고, 배달과
/// 중복 구매 판정은 서버가 한다. 그 옆에 <b>보여주기용 집계(#840)</b>를 NetworkList로 함께 둔다:
/// 주문창 구매 내역과 본부 재고 게시판이 "몇 개 샀고 몇 개 남았나"를 물어 오면서 "클라가 목록을 알
/// 필요가 없다"는 예전 전제가 뒤집혔다. 집계의 품목 id는 ShopCatalog 인덱스다(<see cref="PurchaseTally"/>).
///
/// 세이브(#373)는 여전히 프리팹 이름으로 적고 되찾는 일은 SaveItemLookup이 NGO 등록 명부로 한다 —
/// 카탈로그 인덱스는 수명이 세션인 집계에만 쓰고 저장에는 쓰지 않는다.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class ShopPurchases : NetworkedManagerBase
{
    [Tooltip("집계에 실을 품목 id 체계 — 카탈로그 인덱스로 싣는다 (#840). ShopLineup과 같은 에셋을 잡을 것")]
    [SerializeField]
    private ShopCatalog m_catalog;

    // 소지형 — 같은 아이템 중복 구매 허용이라 List (산 개수만큼 매 라운드 배달된다).
    private readonly List<ItemBase> m_carried = new List<ItemBase>();

    // 설치형 — 본부 씬 인스턴스를 켜는 방식이라 두 번 사도 의미가 없다. HashSet + 서버가 재구매 거부.
    private readonly HashSet<EInstallable> m_installables = new HashSet<EInstallable>();

    // 서버만 쓰기, 전 클라 읽기 — 표시용 집계다 (#840). 배달·판정은 위 두 컬렉션이 계속 담당한다.
    private readonly NetworkList<PurchaseTally> m_tallies = new NetworkList<PurchaseTally>();

    /// <summary>구매한 소지형 아이템 프리팹 목록(중복 포함). 배달(ShopDelivery)이 순회한다. 서버 전용.</summary>
    public IReadOnlyList<ItemBase> Carried => m_carried;

    /// <summary>품목별 구매 집계 — 주문창·본부 게시판이 구독해 읽는다 (#840). 서버 외에는 읽기 전용.</summary>
    public NetworkList<PurchaseTally> Tallies => m_tallies;

    /// <summary>
    /// 이 피어에서 집계가 스폰·초기 동기화된 시점 — 뷰가 최초 표시를 위해 구독한다.
    /// NetworkList는 late-join 클라에 초기 내용을 OnListChanged로 알리지 않는다 (WantedListManager와 같은 사정).
    /// </summary>
    public event Action OnTalliesReady;

    /// <summary>
    /// 세션 시작 시 1회 — 이어하기면 저장된 구매 목록을 되살리고(#373), 집계 준비를 알린다(#840).
    /// </summary>
    public override void OnNetworkSpawn()
    {
        if (IsServer)
        {
            m_tallies.Clear(); // 재시작 시 이전 세션 항목이 남는다 (#209 패턴)
            RestoreFromSave();
        }

        // 모든 피어 공통 — 이 시점엔 집계가 초기 동기화된 상태다 (late-join 빈 화면 방지)
        OnTalliesReady?.Invoke();
    }

    // 세이브에는 누적 구매가 남지 않는다 — 복원분은 "남은 것 = 산 것"으로 세운다.
    private void RestoreFromSave()
    {
        SessionSaveData save = SaveService.Pending;
        if (save == null) return;

        foreach (string id in save.CarriedItems)
        {
            ItemBase prefab = SaveItemLookup.Find(id);
            if (prefab != null)
            {
                m_carried.Add(prefab);
                BumpBought(IndexOf(prefab));
            }
            else
                Debug.LogWarning($"[상점] 세이브의 소지형 '{id}'을(를) 찾지 못해 건너뛴다 — 프리팹 이름이 바뀌었는가?", this);
        }

        foreach (string installableName in save.Installables)
        {
            if (Enum.TryParse(installableName, out EInstallable installable) && installable != EInstallable.None)
            {
                if (m_installables.Add(installable))
                    BumpBought(IndexOf(installable));
            }
            else
                Debug.LogWarning($"[상점] 세이브의 설치형 '{installableName}'을(를) 알 수 없어 건너뛴다", this);
        }

        Debug.Log($"[상점] 세이브 복원 — 소지형 {m_carried.Count}개, 설치형 {m_installables.Count}종");
    }

    /// <summary>구매한 설치형 목록. 배달(ShopDelivery)이 순회한다. 서버 전용.</summary>
    public IReadOnlyCollection<EInstallable> Installables => m_installables;

    /// <summary>이 설치형을 이미 샀는가 — 중복 구매 거부·표시 복원용. 서버 전용.</summary>
    public bool HasInstallable(EInstallable installable) => m_installables.Contains(installable);

    /// <summary>소지형 구매를 기록한다 — ShopLineup의 구매 RPC(서버)가 자금 차감 성공 후 호출한다.</summary>
    public void AddCarried(ItemBase itemPrefab)
    {
        if (!IsServer)
        {
            Debug.LogWarning("ShopPurchases.AddCarried는 서버에서만", this);
            return;
        }
        if (itemPrefab == null) return;

        m_carried.Add(itemPrefab);
        BumpBought(IndexOf(itemPrefab));
        Debug.Log($"[상점] 소지형 구매 기록 — {itemPrefab.name} (총 {m_carried.Count}개)");
    }

    /// <summary>설치형 구매를 기록한다 — ShopLineup의 구매 RPC(서버)가 자금 차감 성공 후 호출한다.</summary>
    public void AddInstallable(EInstallable installable)
    {
        if (!IsServer)
        {
            Debug.LogWarning("ShopPurchases.AddInstallable은 서버에서만", this);
            return;
        }
        if (installable == EInstallable.None) return;

        // 재구매는 서버가 이미 거부하지만, 집계를 두 번 올리지 않도록 새로 들어온 것만 센다
        if (m_installables.Add(installable))
            BumpBought(IndexOf(installable));
        Debug.Log($"[상점] 설치형 구매 기록 — {installable}");
    }

    /// <summary>
    /// 소지형 구매 기록 1개를 지운다 — 소매치기에게 뺏긴 채 놓치면 영구 손실이다 (#303).
    /// 같은 프리팹을 여러 개 샀으면 하나만 빠진다(중복 구매 허용이라 List).
    /// </summary>
    public void RemoveCarried(ItemBase itemPrefab)
    {
        if (!IsServer)
        {
            Debug.LogWarning("ShopPurchases.RemoveCarried는 서버에서만", this);
            return;
        }
        if (itemPrefab == null) return;

        if (m_carried.Remove(itemPrefab))
        {
            DropRemaining(IndexOf(itemPrefab));
            Debug.Log($"[상점] 구매품 소실 — {itemPrefab.name} (남은 {m_carried.Count}개)");
        }
    }

    /// <summary>
    /// 구매 목록을 비운다 — 라운드 실패로 판이 끝났을 때 호출한다 (#395, TeamFund.ResetToStarting과 같은 자리).
    /// 이 홀더는 씬을 넘어 유지되므로(#214) 새 판을 시작해도 스스로는 초기화되지 않는다.
    /// </summary>
    public void Clear()
    {
        if (!IsServer)
        {
            Debug.LogWarning("ShopPurchases.Clear는 서버에서만", this);
            return;
        }

        Debug.Log($"[상점] 라운드 실패로 구매 목록 초기화 — 소지형 {m_carried.Count}개, 설치형 {m_installables.Count}종");
        m_carried.Clear();
        m_installables.Clear();
        m_tallies.Clear();
    }

    // ---- 집계 (서버 전용) ----
    //
    // 배달용 컬렉션과 집계는 같은 사실의 두 표현이라 어긋날 수 있다 — 위 네 메서드 안에서만 함께
    // 갱신하고, 밖에서 컬렉션을 직접 만지는 경로는 두지 않는다. (#840)

    private int IndexOf(ItemBase itemPrefab) => m_catalog != null ? m_catalog.IndexOf(itemPrefab) : -1;

    private int IndexOf(EInstallable installable) => m_catalog != null ? m_catalog.IndexOf(installable) : -1;

    // 산 개수를 올린다 — 없던 품목이면 줄을 새로 만든다.
    private void BumpBought(int catalogIndex)
    {
        if (!TryTallyIndex(catalogIndex, out ushort index))
            return;

        int row = FindTally(index);
        if (row < 0)
        {
            m_tallies.Add(new PurchaseTally { CatalogIndex = index, Bought = 1, Remaining = 1 });
            return;
        }

        PurchaseTally tally = m_tallies[row];
        tally.Bought = Step(tally.Bought, 1);
        tally.Remaining = Step(tally.Remaining, 1);
        m_tallies[row] = tally;
    }

    // 남은 개수만 줄인다 — 산 기록(Bought)은 그대로 둔다.
    private void DropRemaining(int catalogIndex)
    {
        if (!TryTallyIndex(catalogIndex, out ushort index))
            return;

        int row = FindTally(index);
        if (row < 0)
            return;

        PurchaseTally tally = m_tallies[row];
        tally.Remaining = Step(tally.Remaining, -1);
        m_tallies[row] = tally;
    }

    // 카탈로그에 없는 품목은 집계에서 건너뛴다 — 아이콘·이름을 카탈로그 항목에서 읽기 때문이다.
    private bool TryTallyIndex(int catalogIndex, out ushort index)
    {
        index = 0;
        if (catalogIndex < 0 || catalogIndex > ushort.MaxValue)
        {
            Debug.LogWarning("[상점] 카탈로그에 없는 품목이라 구매 집계에 올리지 못한다 — 카탈로그 배선 확인 (#840)", this);
            return false;
        }

        index = (ushort)catalogIndex;
        return true;
    }

    private int FindTally(ushort catalogIndex)
    {
        for (int i = 0; i < m_tallies.Count; i++)
        {
            if (m_tallies[i].CatalogIndex == catalogIndex)
                return i;
        }

        return -1;
    }

    private static ushort Step(ushort value, int delta) =>
        (ushort)Mathf.Clamp(value + delta, 0, ushort.MaxValue);
}
