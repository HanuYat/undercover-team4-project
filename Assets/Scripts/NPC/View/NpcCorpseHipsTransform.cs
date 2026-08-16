using Unity.Netcode.Components;
using UnityEngine;

/// <summary>
/// 시체 골반의 <see cref="NetworkTransform"/> — <b>순간이동을 원격에 사실로 알리는 통로다.</b>
/// (ragdoll-corpse-jail-teleport §4)
///
/// <b>왜 RPC가 아니라 NT 서브클래스인가.</b> 원격이 순간이동을 알아야 하는 이유는 하나다 — 골반이
/// 점프하는 <b>그 프레임에</b> 뼈를 함께 옮겨야 관절이 위반되지 않기 때문이다. RPC로 알리면 그
/// 도착 시점이 골반 상태 적용 시점과 <b>같은 프레임이라는 보장이 없고</b>, 한 프레임만 어긋나도
/// 위반이 전량 들어간다 — 실측으로 그 한 프레임의 대가가 <b>초속 6261m</b>였다(계획서 §3-3).
///
/// 여기서는 그 위험이 성립할 자리가 없다: <b>알림과 이동이 같은 이벤트다.</b>
/// <see cref="OnNetworkTransformStateUpdated"/>는 이 컴포넌트가 골반을 옮기기 직전에 불리므로,
/// 거기서 얼리면 순서가 어긋날 방법이 없다.
///
/// <b>얼림이 곧 순간이동의 수단이다</b> — 래그돌 뼈는 전부 골반의 자식이고
/// (<c>RagdollSetup</c>이 <c>Root/Hips</c> 이하만 뼈로 받는다), <b>키네마틱 뼈는 부모 트랜스폼을
/// 따라간다.</b> 그래서 얼린 채 골반이 움직이면 몸이 통째로 도착한다. 이것은 서버가 이미 하고 있는
/// 시퀀스(<see cref="NpcRagdoll.ServerPlaceCorpse"/>)를 원격에서 재생하는 것이다 — 실측에서 호스트가
/// 도착 시 <b>속도 0 · 여유 +0.086</b>으로 멀쩡했던 그 시퀀스다.
///
/// ⚠ <b>#572가 되돌린 "원격 얼림"과 다른 것이다.</b> 저쪽은 <b>정착 시점의 영구</b> 얼림이라 시체가
/// 루트 높이에 매달린 조각상이 되어 지면 −0.141까지 내려갔다. 여기는 순간이동 구간만 감싸는
/// <b>일시</b> 얼림이고, 끝나면 물리로 되돌려 각 피어의 로컬 물리가 바닥에 맞게 무너뜨린다 —
/// 원격이 스스로 정착하는 성질을 그대로 남긴다.
///
/// <b>붙이는 곳: 골반 뼈</b>(관절이 없는 뼈). 배선은 <c>RagdollSetup</c>이 한다 — 손으로 붙이지 말 것.
/// </summary>
public class NpcCorpseHipsTransform : NetworkTransform
{
    private NpcRagdoll m_ragdoll;

    // ⚠ <b><c>protected override</c> + <c>base</c> 호출이다.</b> <see cref="NetworkTransform"/>이
    // 자기 <c>Awake</c>를 <c>protected virtual</c>로 갖고 있어(보간기·상태 초기화가 거기 있다),
    // <c>private void Awake()</c>로 새로 선언하면 그것을 <b>가려서</b> NT가 초기화되지 않는다.
    protected override void Awake()
    {
        base.Awake();

        // 골반은 리그 깊숙한 자식이고 NpcRagdoll은 NPC 루트에 있다.
        m_ragdoll = GetComponentInParent<NpcRagdoll>();
    }

    /// <summary>
    /// 상태가 적용되기 직전에 불린다 — 순간이동이면 그 자리에서 몸을 얼려 골반에 매어 둔다.
    ///
    /// <b>권위 피어는 건드리지 않는다.</b> 서버는 <see cref="NpcRagdoll.ServerPlaceCorpse"/>가
    /// 이미 얼리고 옮기고 녹이는 시퀀스를 갖고 있고, 실측으로 정상이다 — 여기서 한 번 더 손대면
    /// 그 시퀀스와 싸운다. 이 클래스가 메우는 것은 <b>원격에만 없던 얼림</b>이다.
    /// </summary>
    protected override void OnNetworkTransformStateUpdated(
        ref NetworkTransformState oldState,
        ref NetworkTransformState newState
    )
    {
        base.OnNetworkTransformStateUpdated(ref oldState, ref newState);

        bool teleporting = newState.IsTeleportingNextFrame || newState.WasTeleported;

        if (!teleporting || m_ragdoll == null)
            return;

        if (CanCommitToTransform)
            return; // 권위 피어 — 자기 시퀀스가 있다 (위 주석)

        m_ragdoll.BeginTeleportBracket();
    }
}
