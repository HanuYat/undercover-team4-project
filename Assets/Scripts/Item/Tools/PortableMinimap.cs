using UnityEngine;

// 휴대용 미니맵 (#835) — 좌클릭 동작이 없다. 장착 여부만 필요해서 존재하는 타입 식별자이고,
// 실제 표시는 PortableMinimapPresenter가 장착 이벤트를 듣고 켠다.
public class PortableMinimap : ItemBase
{
    public override void Use(GameObject target) { }
}
