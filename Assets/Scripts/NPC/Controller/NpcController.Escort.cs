using UnityEngine;

public partial class NpcController
{
    /// <summary>연행 시작 — 체포 성공 직후 호출. NPC가 target(플레이어)을 따라 이동한다. (#59)</summary>
    // TODO: 아이템(수갑) 네트워크화 시 클라 입력 → ServerRpc 경로로 호출되도록 연결 (#56에서는 서버 가드만)
    public void StartEscort(Transform target)
    {
        // FSM 전이는 서버 권위 — 클라이언트에서 직접 부르면 무시한다
        if (IsSpawned && !IsServer)
            return;

        EscortTarget = target;
        m_stateMachine.ChangeState(NpcState.Escorted);
    }

    /// <summary>연행 중단 — 그 자리에서 체포(Captured) 상태로 멈춘다. (#59)</summary>
    public void StopEscort()
    {
        if (IsSpawned && !IsServer)
            return;

        EscortTarget = null;
        m_stateMachine.ChangeState(NpcState.Captured);
    }

}
