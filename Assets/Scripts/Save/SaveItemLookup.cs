using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 세이브에 담을 소지형 아이템의 id 체계 (#373) — <b>프리팹 이름</b>을 그대로 쓴다.
///
/// 별도의 id 필드나 카탈로그 자산을 두지 않는 이유: 스폰 가능한 아이템 프리팹은 어차피 전부
/// DefaultNetworkPrefabs에 등록돼 있어야 하고(NGO 요건), 그 목록이 이미 "이 게임에 존재하는 아이템 전부"의
/// 유일한 명부다. 여기에 이름 조회만 얹으면 새 아이템을 추가할 때 배선할 곳이 늘지 않는다.
/// (ShopPurchases가 "품목 id 체계도 카탈로그도 필요 없다"고 적어 둔 전제를 세이브가 깨지 않게 하는 선택이기도 하다.)
///
/// <b>대가</b>: 프리팹 <i>파일 이름을 바꾸면 그 아이템에 한해 기존 세이브가 복원되지 않는다</i>(경고 로그 후 무시).
/// 알파 단계에서 감수하는 비용이고, 문제가 되면 ItemBase에 안정적 키 필드를 추가하는 쪽으로 옮기면 된다
/// (EmoteDefinition이 같은 이유로 그 방식을 쓴다).
/// </summary>
public static class SaveItemLookup
{
    /// <summary>세이브에 적을 id — 프리팹 이름. null이면 빈 문자열.</summary>
    public static string GetId(ItemBase prefab) => prefab != null ? prefab.name : string.Empty;

    /// <summary>
    /// id로 아이템 프리팹을 되찾는다 — 등록 명부(NetworkConfig.Prefabs)를 훑는다. 없으면 null.
    /// 세션 시작 시 구매 목록을 복원할 때만 쓰므로(목록 길이 = 산 개수) 선형 탐색으로 충분하다.
    /// </summary>
    public static ItemBase Find(string id)
    {
        if (string.IsNullOrEmpty(id))
            return null;

        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || nm.NetworkConfig == null || nm.NetworkConfig.Prefabs == null)
            return null;

        foreach (NetworkPrefab entry in nm.NetworkConfig.Prefabs.Prefabs)
        {
            GameObject prefab = entry?.Prefab;
            if (prefab == null || prefab.name != id)
                continue;

            ItemBase item = prefab.GetComponent<ItemBase>();
            if (item != null)
                return item;
        }

        return null;
    }
}
