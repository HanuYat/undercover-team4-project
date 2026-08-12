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
/// <b>서버 전용 컬렉션 2개 — 네트워크 동기화하지 않는다.</b> 클라가 목록을 알 필요가 없기 때문이다:
/// 중복 구매는 서버가 거부하고, 진열대의 "구매함" 표시는 진열대 자신의 bool NetworkVariable이 낸다.
/// 그래서 품목 id 체계도, 카탈로그 자산도 필요 없다.
///
/// 세이브(#373)만은 목록을 문자열로 적어야 하지만, 그 id도 <b>여기서 들지 않는다</b> — 프리팹 이름을
/// 그대로 쓰고 되찾는 일은 SaveItemLookup이 NGO 등록 명부로 한다. 위 전제는 그대로다.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class ShopPurchases : NetworkedManagerBase
{
    // 소지형 — 같은 아이템 중복 구매 허용이라 List (산 개수만큼 매 라운드 배달된다).
    private readonly List<ItemBase> m_carried = new List<ItemBase>();

    // 설치형 — 본부 씬 인스턴스를 켜는 방식이라 두 번 사도 의미가 없다. HashSet + 서버가 재구매 거부.
    private readonly HashSet<EInstallable> m_installables = new HashSet<EInstallable>();

    /// <summary>구매한 소지형 아이템 프리팹 목록(중복 포함). 배달(ShopDelivery)이 순회한다. 서버 전용.</summary>
    public IReadOnlyList<ItemBase> Carried => m_carried;

    /// <summary>
    /// 이어하기로 시작했으면 저장된 구매 목록을 되살린다 (#373). 세션 시작 시 1회.
    /// 프리팹은 이름으로 되찾는다(SaveItemLookup) — 이 홀더가 id 체계를 들지 않는다는 전제는 그대로 유지된다.
    /// </summary>
    public override void OnNetworkSpawn()
    {
        if (!IsServer) return;

        SessionSaveData save = SaveService.Pending;
        if (save == null) return;

        foreach (string id in save.CarriedItems)
        {
            ItemBase prefab = SaveItemLookup.Find(id);
            if (prefab != null)
                m_carried.Add(prefab);
            else
                Debug.LogWarning($"[상점] 세이브의 소지형 '{id}'을(를) 찾지 못해 건너뛴다 — 프리팹 이름이 바뀌었는가?", this);
        }

        foreach (string installableName in save.Installables)
        {
            if (Enum.TryParse(installableName, out EInstallable installable) && installable != EInstallable.None)
                m_installables.Add(installable);
            else
                Debug.LogWarning($"[상점] 세이브의 설치형 '{installableName}'을(를) 알 수 없어 건너뛴다", this);
        }

        Debug.Log($"[상점] 세이브 복원 — 소지형 {m_carried.Count}개, 설치형 {m_installables.Count}종");
    }

    /// <summary>이 소지형을 한 번이라도 샀는가 — 진열대 "구매함" 표시 복원용. 서버 전용.</summary>
    public bool HasCarried(ItemBase itemPrefab) => itemPrefab != null && m_carried.Contains(itemPrefab);

    /// <summary>구매한 설치형 목록. 배달(ShopDelivery)이 순회한다. 서버 전용.</summary>
    public IReadOnlyCollection<EInstallable> Installables => m_installables;

    /// <summary>이 설치형을 이미 샀는가 — 중복 구매 거부·표시 복원용. 서버 전용.</summary>
    public bool HasInstallable(EInstallable installable) => m_installables.Contains(installable);

    /// <summary>소지형 구매를 기록한다 — 진열대의 구매 RPC(서버)가 자금 차감 성공 후 호출한다.</summary>
    public void AddCarried(ItemBase itemPrefab)
    {
        if (!IsServer)
        {
            Debug.LogWarning("ShopPurchases.AddCarried는 서버에서만", this);
            return;
        }
        if (itemPrefab == null) return;

        m_carried.Add(itemPrefab);
        Debug.Log($"[상점] 소지형 구매 기록 — {itemPrefab.name} (총 {m_carried.Count}개)");
    }

    /// <summary>설치형 구매를 기록한다 — 진열대의 구매 RPC(서버)가 자금 차감 성공 후 호출한다.</summary>
    public void AddInstallable(EInstallable installable)
    {
        if (!IsServer)
        {
            Debug.LogWarning("ShopPurchases.AddInstallable은 서버에서만", this);
            return;
        }
        if (installable == EInstallable.None) return;

        m_installables.Add(installable);
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
            Debug.Log($"[상점] 구매품 소실 — {itemPrefab.name} (남은 {m_carried.Count}개)");
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
    }
}
