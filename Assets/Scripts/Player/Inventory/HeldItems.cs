using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 플레이어가 부착해 들고 있는 아이템 집합 — "지금 무엇을 들고 있나"의 단일 접근 경로. (#88, #144, #370, #395)
/// 진실은 부착 지점(anchor)의 자식 목록이다: 아이템은 독립 NetworkObject라 소지가 곧 부모 부착이고,
/// 부착은 NGO가 복제하므로 서버·오너 양쪽에서 같은 답이 나온다. 별도 목록을 들면 그 복제와 어긋난다.
///
/// 책임 경계: 집합의 개수·소속 판정·부착·디스폰만 안다. 들 자격이 있는지(권위·거리·가시선·용량)와
/// 어느 칸에 놓이는지(<see cref="LoadoutSlots{T}"/>)는 모른다 — 그건 소유자인 PlayerLoadout 몫이다.
/// <see cref="LoadoutSlots{T}"/>와 달리 Transform 계층 자체가 상태라 Unity 없이 테스트할 수는 없다.
/// </summary>
public sealed class HeldItems
{
    // 부착 지점. 이 아래 자식 중 ItemBase를 가진 것이 곧 소지품이다.
    private readonly Transform m_anchor;

    /// <param name="anchor">아이템을 붙일 부모. 소유자가 Awake에서 확정해 넘긴다(런타임에 바뀌지 않는다).</param>
    public HeldItems(Transform anchor)
    {
        m_anchor = anchor;
    }

    // 부착 지점이 아직 살아 있는가 — 플레이어 정리 중에는 파괴돼 있을 수 있다(#395 경로).
    // 원본에서 디스폰 경로에만 있던 가드를 모든 열거로 넓혔다 — 이미 예외로 죽던 자리만 0/빈 값이 된다.
    private bool IsUsable => m_anchor != null;

    /// <summary>소지 중인 아이템 수. 무할당 — 줍기 요청마다 용량 검사로 불린다.</summary>
    public int Count => CountOf<ItemBase>();

    /// <summary>
    /// <typeparamref name="TComponent"/>를 가진 소지품 수(예: 밧줄 개수, #390). 무할당.
    /// 개체를 구별하지 않고 개수만 센다 — 같은 종류끼리는 어느 것이든 동등하다는 전제.
    /// </summary>
    public int CountOf<TComponent>()
        where TComponent : Component
    {
        if (!IsUsable)
        {
            return 0;
        }

        int count = 0;
        for (int i = 0; i < m_anchor.childCount; i++)
        {
            if (m_anchor.GetChild(i).GetComponent<TComponent>() != null)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>이 아이템을 지금 들고 있는가 — 부착 여부로 판정한다(버리기 권한 검증용).</summary>
    public bool Holds(NetworkObject item) =>
        item != null && IsUsable && item.transform.parent == m_anchor;

    /// <summary>
    /// <typeparamref name="TComponent"/>를 가진 첫 소지품(없으면 null) — 자가 부활 게이트 등 "하나라도
    /// 있으면" 판정용. 무할당. CollectInto처럼 목록이 필요하지 않을 때 이걸 쓴다. (#820)
    /// </summary>
    public TComponent FirstOf<TComponent>()
        where TComponent : Component
    {
        if (!IsUsable)
        {
            return null;
        }

        for (int i = 0; i < m_anchor.childCount; i++)
        {
            TComponent found = m_anchor.GetChild(i).GetComponent<TComponent>();
            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>소지품을 into에 담는다(기존 내용은 지운다) — 목록이 필요한 쪽만 쓴다. (#303)</summary>
    public void CollectInto(List<ItemBase> into)
    {
        into.Clear();
        if (!IsUsable)
        {
            return;
        }

        for (int i = 0; i < m_anchor.childCount; i++)
        {
            ItemBase item = m_anchor.GetChild(i).GetComponent<ItemBase>();
            if (item != null)
            {
                into.Add(item);
            }
        }
    }

    /// <summary>
    /// 아이템을 부착하고 로컬 원점에 맞춘다. 서버에서만 호출 — 부착 자체는 NGO가 복제한다.
    /// </summary>
    public void Attach(NetworkObject item)
    {
        item.TrySetParent(m_anchor, false);
        item.transform.localPosition = Vector3.zero;
        item.transform.localRotation = Quaternion.identity;
    }

    /// <summary>
    /// 오너에게 보낼 보유 목록(서버 진실)을 만든다.
    /// 디스폰된 아이템은 건너뛴다 — Despawn은 즉시지만 GameObject 파괴는 프레임 끝이라 회수
    /// (PlayerItemSupply.ServerClearHeldItems) 직후에도 자식으로 남는다. 그대로 참조를 만들면 생성자가 던져
    /// 동기화 RPC가 발송되지 않고, 오너는 파괴된 아이템을 계속 장착·표시한다. (#370)
    /// </summary>
    public NetworkObjectReference[] BuildRefs()
    {
        if (!IsUsable)
        {
            return Array.Empty<NetworkObjectReference>();
        }

        List<NetworkObjectReference> refs = new List<NetworkObjectReference>();
        for (int i = 0; i < m_anchor.childCount; i++)
        {
            ItemBase item = m_anchor.GetChild(i).GetComponent<ItemBase>();
            if (item != null && item.NetworkObject != null && item.NetworkObject.IsSpawned)
            {
                refs.Add(new NetworkObjectReference(item.NetworkObject));
            }
        }

        return refs.ToArray();
    }

    /// <summary>
    /// 들고 있는 아이템을 전부 디스폰한다 — 아이템의 수명을 플레이어와 묶는다. 서버 전용. (#395)
    /// 월드에 버린 아이템은 대상이 아니다 — 이미 부모가 해제돼 이 밑에 없다.
    /// 상점 복귀 회수(#370)도 이 경로를 쓴다.
    /// </summary>
    /// <returns>디스폰한 아이템 수.</returns>
    public int DespawnAll()
    {
        // 세션이 통째로 내려가는 중이면 NGO가 알아서 정리한다 — 그 와중에 Despawn을 부르면 경고만 남는다
        NetworkManager manager = NetworkManager.Singleton;
        if (manager == null || !manager.IsListening || !IsUsable)
        {
            return 0;
        }

        // 디스폰하면 자식 목록이 바뀌므로 먼저 모아 둔다 (BuildRefs와 같은 열거 방식)
        List<NetworkObject> held = new List<NetworkObject>();
        for (int i = 0; i < m_anchor.childCount; i++)
        {
            ItemBase item = m_anchor.GetChild(i).GetComponent<ItemBase>();
            if (item != null && item.NetworkObject != null)
            {
                // 채널링 중이면 먼저 끊는다 — 드롭과 같은 이유(배터리 낭비·오완료 방지). (#370)
                item.ServerCancelActiveUse();
                held.Add(item.NetworkObject);
            }
        }

        foreach (NetworkObject item in held)
        {
            if (item != null && item.IsSpawned)
            {
                item.Despawn(destroy: true);
            }
        }

        return held.Count;
    }
}
