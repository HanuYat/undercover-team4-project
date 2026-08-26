using UnityEngine;

/// <summary>
/// 장비 카탈로그 (#843) — 사용하면 주문창(<see cref="ShopBrowserPanel"/>)이 열린다.
/// 상점 씬에서만 지급되고 나갈 때 회수된다 (<see cref="PlayerItemSupply"/> #370).
///
/// 창을 여는 것 말고는 아무 효과가 없어 네트워크 경로가 없다 — 실제 주문은 창의 칸이
/// 진열대 슬롯에 직접 요청하고, 가격 판정은 서버가 자기 NetworkVariable로 한다.
/// 각자 하나씩 들고 있으므로 6명이 동시에 볼 수 있다.
/// </summary>
public class ShopCatalogItem : ItemBase
{
    public override void Use(GameObject target)
    {
        if (!IsOwner)
            return; // 남의 카탈로그에서 온 호출 방지

        if (App.UI.Current == null || !App.UI.Current.TryGetPanel(out ShopBrowserPanel panel))
        {
            Debug.LogWarning(
                "카탈로그: 주문창을 찾지 못했다 — ShopBrowserPanel은 상점 씬에만 있다",
                this
            );
            return;
        }

        panel.Open(Holder);
    }
}
