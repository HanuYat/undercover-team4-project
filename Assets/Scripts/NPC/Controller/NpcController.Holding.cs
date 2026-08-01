using System;
using UnityEngine;

public partial class NpcController
{
    // ---- 임시 거처 이송 (#291) ----

    /// <summary>임시 거처 이송 중 걸어갈 지점. 이송 중이 아니면 null. 서버에서만 유효. (#291)</summary>
    public Transform HoldingSpot { get; private set; }

    /// <summary>임시 거처 도착 — NpcHoldingState 전용. SpawnedNpcEvent가 구독해 NPC를 정리(despawn)한다. 서버에서만 발생. (#291)</summary>
    public event Action<NpcController> OnReachedHolding;

    /// <summary>
    /// 임시 거처로 이송 — 경범죄 이벤트 NPC를 판정 후 spot으로 걸어가게 한다. 도착 시 <see cref="OnReachedHolding"/> 발행.
    /// spot이 null이면 진입 즉시 도착 처리(그 자리 통보). 유치장(#228)·원한구역(#277)과는 별개 경로다. (#291)
    /// </summary>
    public void SendToHolding(Transform spot)
    {
        // FSM 전이는 서버 권위 — StartEscort와 동일하게 클라이언트 호출은 무시한다
        if (IsSpawned && !IsServer)
            return;

        EscortTarget = null; // 판정 시점에 연행은 이미 풀렸지만, 참조가 남아 있으면 여기서 끊는다
        HoldingSpot = spot;
        m_stateMachine.ChangeState(NpcState.Holding);
    }

    /// <summary>임시 거처 도착 통보 — NpcHoldingState 전용.</summary>
    public void NotifyReachedHolding() => OnReachedHolding?.Invoke(this);
}
