using UnityEngine;

/// <summary>
/// 플레이어의 연행 상태 관리 — 지금 누구를 연행 중인지 추적한다. (이슈 #59)
/// 연행 시작(체포 성공)과 놓기는 같은 E 입력을 받는 Handcuffs가 분기해서 호출한다.
/// 한 번에 1명만 연행 가능 (이슈 완료 기준).
/// </summary>
// TODO: 네트워크 전환 시 연행 소유권을 서버 권위로 (플레이어별 NetworkVariable)
public class PlayerEscorter : MonoBehaviour
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
        // 거리 이탈 등으로 NPC 쪽에서 연행이 스스로 풀린 경우 참조를 정리한다
        if (EscortingNpc != null && EscortingNpc.StateMachine.CurrentState != NpcState.Escorted)
            EscortingNpc = null;
    }
}
