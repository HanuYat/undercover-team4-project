using System;
using UnityEngine;

/// <summary>
/// 부착 지점의 자식이 바뀌는 순간을 알린다 — <b>전 피어 로컬 이벤트</b>. (#487)
///
/// 소지는 곧 부모 부착이고 부착은 NGO가 복제하므로(<see cref="HeldItems"/>), 누가 무엇을 들고
/// 내려놓든 <b>모든 피어에서</b> 자식 목록이 바뀐다. 그 순간을 잡으려면 Unity 메시지를 받는
/// 컴포넌트가 부착 지점에 있어야 한다 — 순수 클래스인 <see cref="HeldItems"/>는 받지 못한다.
///
/// <see cref="PlayerLoadout"/>이 부착 지점에 런타임으로 붙이고 자기 이벤트로 중계한다.
/// 부모 쪽에서 같은 취지를 쓰는 선례가 <see cref="WorldItemPickup"/>(OnTransformParentChanged)이다.
///
/// <b>왜 이게 필요한가</b>: <see cref="PlayerLoadout.OnSlotsChanged"/>는 서버 동기화 RPC
/// (<c>SendTo.Owner</c>)에서 나오는 오너 로컬 이벤트라 <b>남의 소지품을 보는 쪽</b>에는 오지 않는다.
/// 약탈 창(<see cref="LootPanel"/>)이 그 경우다.
/// </summary>
public class HeldItemsWatcher : MonoBehaviour
{
    /// <summary>부착 자식이 추가·제거·재정렬됐다. 무엇이 어떻게 바뀌었는지는 알리지 않는다 — 다시 읽을 것.</summary>
    public event Action OnChanged;

    private void OnTransformChildrenChanged() => OnChanged?.Invoke();
}
