using Unity.Netcode;
using UnityEngine;

public partial class NpcController
{
    // ---- 검거 반응 (#76) ----
    // FSM 전이는 전부 서버 권위 — 클라이언트 호출은 StartEscort와 같은 방식으로 무시한다.
    // TODO: 아이템/상호작용 네트워크 전환(#55 계열) 시 클라 입력 → ServerRpc 경로로 연결

    /// <summary>도주 시작 — 수갑 채널링 성공 순간 도주형의 반응. threat(플레이어) 반대 방향으로 달아난다.</summary>
    public void StartFlee(Transform threat)
    {
        if (IsSpawned && !IsServer)
            return;

        ThreatTarget = threat;
        m_stateMachine.ChangeState(NpcState.Run);
    }

    /// <summary>위협 참조 정리 — 반응(도주·저항)이 끝나는 지점에서 호출한다.</summary>
    public void ClearThreat() => ThreatTarget = null;

    /// <summary>저항 시작 — 수갑 채널링 성공 순간 저항형의 반응. 그 자리에서 버틴다.</summary>
    public void StartResist(Transform subduer = null)
    {
        if (IsSpawned && !IsServer)
            return;

        // 저항을 유발한(수갑 채우려던) 플레이어를 위협으로 기억한다 — 제압 실패 시 이 대상에게서 도주한다.
        // (도주형이 StartFlee(subduer)로 위협을 받는 것과 대칭 — #205)
        ThreatTarget = subduer;
        m_stateMachine.ChangeState(NpcState.Attack);
    }

    /// <summary>
    /// 제압 타격 요청 — 상호작용 경로(NpcSubdueInteractable)가 호출한다. (#79/#366)
    /// 클라이언트에서 불리면 서버로 전달되므로 비호스트 플레이어의 타격도 반영된다.
    /// 타격량은 서버가 자기 config 값을 쓴다 — 클라이언트가 수치를 보낼 수 없다.
    ///
    /// 상태 게이트는 TakeDamage가 CanBeDamaged로 건다 (#366/#292). 예전의 "저항 중이 아니면 무시"
    /// (배회 NPC 폭행 방지)는 체력이 지속형이 되면서 없어졌다 — 배회 중인 NPC도 때릴 수 있다.
    /// </summary>
    // TODO: 상호작용 네트워크 전환(#55 계열)에서 거리·조준 서버 검증 추가 (지금은 요청 자체는 신뢰)
    public void RequestSubdueHit()
    {
        if (IsSpawned && !IsServer)
        {
            SubdueHitRpc();
            return;
        }

        TakeDamage(m_commonConfig.SubdueHitPower, null);
    }

    // 가해자를 넘기지 않는 이유: RPC가 요청자를 싣지 않기 때문이다(기존 경로와 동일).
    // 깨어나면 도주하지만(#269) 위협이 null이어도 NpcFleeState가 EscapeDistance 안 추격자를
    // 스캔해 폴백하므로, 때린 플레이어가 옆에 있는 한 그쪽에서 도망친다. 정확히 '때린 사람'을
    // 물려야 할 이유가 생기면 그때 RPC에 요청자를 싣는다(#55 계열 상호작용 네트워크 전환).
    [Rpc(SendTo.Server)]
    private void SubdueHitRpc()
    {
        TakeDamage(m_commonConfig.SubdueHitPower, null);
    }

    /// <summary>도주 중인 NPC 근접 제압 — 상호작용 홀드 성공 시 그 자리에서 체포. (NpcSubdueInteractable 경유)</summary>
    public void CaptureBySubdue()
    {
        if (IsSpawned && !IsServer)
            return;
        if (CurrentState != NpcState.Run)
            return;

        m_stateMachine.ChangeState(NpcState.Captured);
    }

    // 기절 진입(EnterStunned)은 NpcController.Stun.cs로 이사했다 — 상태 전이가 아니라
    // 오버레이 플래그가 됐기 때문이다 (#292).
}
