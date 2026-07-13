using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 플레이어의 검거·연행 서버 권위 허브. (#59, #56/#118 네트워크 전환)
/// 오너 클라의 아이템/상호작용(Handcuffs·NpcSubdueInteractable)이 이 컴포넌트의 요청 API를 호출하면,
/// 요청을 서버로 넘겨(ServerRpc) 서버가 채널링·사거리·반응 판정을 실행한다.
/// 그 결과 NpcController 상태 변경은 서버에서 일어나고 NetworkVariable로 전 피어에 동기화된다.
/// 한 번에 1명만 연행 가능 (동시 1명 제약).
/// </summary>
public class PlayerEscorter : NetworkBehaviour
{
    /// <summary>지금 연행 중인 NPC. 없으면 null.</summary>
    public NpcController EscortingNpc { get; private set; }

    public bool IsEscorting => EscortingNpc != null;

    /// <summary>연행 시작. 이미 다른 NPC를 연행 중이면 무시된다 (동시 1명 제약).</summary>
    public void StartEscort(NpcController npc)
    {
        if (IsEscorting || npc == null)
            return;

        EscortingNpc = npc;
        npc.StartEscort(transform);
        Debug.Log($"연행 시작: {npc.name}");
    }

    /// <summary>연행 놓기 — NPC는 그 자리에서 체포 상태로 멈춘다. 다시 다가가 E로 재연행 가능.</summary>
    public void Release()
    {
        if (!IsEscorting)
            return;

        Debug.Log($"연행 놓기: {EscortingNpc.name}");
        EscortingNpc.StopEscort();
        EscortingNpc = null;
    }

    private void Update()
    {
        // 참조 정리는 서버(또는 오프라인)에서만 — 연행 상태 자체가 서버 권위다 (#56/#118).
        // 클라이언트에서는 EscortingNpc가 서버 로직으로만 세팅되므로 여기서 건드리지 않는다.
        if (IsSpawned && !IsServer)
            return;

        // 거리 이탈 등으로 NPC 쪽에서 연행이 스스로 풀린 경우 참조를 정리한다
        if (EscortingNpc != null && EscortingNpc.CurrentState != NpcState.Escorted)
            EscortingNpc = null;
    }
}
