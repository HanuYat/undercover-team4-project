using System;
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
        NpcController npc = other.GetComponentInParent<NpcController>();

        // 연행 중인 NPC만 인계 대상 — 배회하다 지나가는 시민은 무시
        // (동기화된 CurrentState로 판정해야 클라이언트에서도 올바르다, #56)
        if (npc == null || npc.CurrentState != NpcState.Escorted)
            return;

        Debug.Log($"본부 도달 — 인계 가능: {npc.name}");
        OnNpcDelivered?.Invoke(npc);
    }
}
