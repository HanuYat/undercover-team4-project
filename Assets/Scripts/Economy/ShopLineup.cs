using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 상점 진열 추첨 (#814). 카탈로그에서 소모형 고정 칸 + 랜덤 칸을 뽑아 진열대(슬롯)에 배정한다.
/// Shop 씬은 라운드마다 재로드되므로 씬 로드 1회 = 라운드 1회 추첨이 자동으로 성립한다.
/// </summary>
public class ShopLineup : MonoBehaviour
{
    [SerializeField]
    private ShopCatalog m_catalog;

    [Tooltip("슬롯 순서 = 배열 순서")]
    [SerializeField]
    private ShopStand[] m_stands;

    private void Awake()
    {
        if (m_catalog == null)
        {
            Debug.LogWarning("ShopLineup: 카탈로그가 배선되지 않았다", this);
            return;
        }

        foreach (ShopStand stand in m_stands)
        {
            if (stand != null)
                stand.BindCatalog(m_catalog);
        }
    }

    private void Start()
    {
        NetworkManager manager = NetworkManager.Singleton;
        if (manager == null || !manager.IsServer)
            return;

        AssignLineupAsync().Forget();
    }

    // 씬 로드 콜백 안에서 바로 네트워크 상태를 건드리지 않는다 — ShopDelivery.DeliverAsync와 같은 이유.
    private async UniTaskVoid AssignLineupAsync()
    {
        await UniTask.NextFrame(this.GetCancellationTokenOnDestroy());

        AssignLineup();
    }

    private void AssignLineup()
    {
        if (m_catalog == null || m_stands == null)
            return;

        int slotCount = 0;
        foreach (ShopStand stand in m_stands)
        {
            if (stand != null)
                slotCount++;
        }

        if (slotCount == 0)
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

        // 설정값 합이 슬롯 수와 다르면(모자라거나 후보가 한쪽뿐이면) 남는 칸을 다른 쪽으로 넘긴다.
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

        int slot = 0;
        foreach (ShopStand stand in m_stands)
        {
            if (stand == null)
                continue;

            stand.ServerAssign(assignment[slot]);
            slot++;
        }

        Debug.Log(
            $"[상점] 진열 추첨 — 소모형 {stapleSlots}칸, 랜덤 {randomSlots}칸, 빈 칸 {slotCount - stapleSlots - randomSlots}"
        );
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
            result.Add(staples[Random.Range(0, staples.Count)]);

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
            int pickAt = Random.Range(0, pool.Count);
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
            int j = Random.Range(0, i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
