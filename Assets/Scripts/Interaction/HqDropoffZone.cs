using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 본부 인계 구역 — 연행 중인 NPC가 들어오면 도달 이벤트를 발행한다. (이슈 #59)
/// 실제 인계 처리(오검거 판정·라운드 연계)는 본부 연행 상호작용(#40)에서 이 이벤트를 구독해 구현한다.
/// 콜라이더는 Is Trigger여야 한다.
/// </summary>
[RequireComponent(typeof(Collider))]
public class HqDropoffZone : MonoBehaviour
{
    /// <summary>연행된 NPC가 구역에 도달했을 때 — 인계 처리(#40)가 구독한다.</summary>
    public event Action<NpcController> OnNpcDelivered;

    private void OnTriggerEnter(Collider other)
    {
        // 서버 권위 게이트 (클라이언트에서는 실행 무시 - 로그 스팸 및 중복 발화 방지)
        if (NetworkManager.Singleton != null && !NetworkManager.Singleton.IsServer)
            return;

        NpcController npc = other.GetComponentInParent<NpcController>();

        if (npc == null)
            return;

        // [가장 중요한 수정] 이미 판정이 끝난 NPC라면, 존에 닿아도 완전히 무시합니다.
        if (npc.IsDelivered)
        {
            Debug.Log($"[중복 방지] 이미 판정 완료된 NPC가 존에 재진입하여 무시됩니다: {npc.name}");
            return;
        }

        // 연행 중이거나 밧줄로 끌려온 기절 NPC만 인계 대상 — 배회 시민은 무시 (#59/#269)
        if (npc.CurrentState != NpcState.Escorted && !npc.IsRoped)
            return;

        Debug.Log($"본부 도달 — 인계 가능: {npc.name}");
        OnNpcDelivered?.Invoke(npc);
    }
}
