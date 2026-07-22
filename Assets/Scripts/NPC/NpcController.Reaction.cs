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

    /// <summary>저항 진입 시 게이지를 최대로 리셋한다 — NpcResistState.Enter 전용.</summary>
    public void ResetSubdueGauge()
    {
        SetSubdueGauge(m_resistConfig.SubdueGaugeMax);
    }

    /// <summary>
    /// 제압 타격 — 저항 게이지를 깎는다. 진압봉 등 타격 수단(후속 아이템 이슈)이 호출.
    /// 여러 명이 함께 때리면 그만큼 빨리 깎인다 (GDD 7-4 협동 인센티브).
    /// </summary>
    public void ApplySubdueHit(float amount)
    {
        if (IsSpawned && !IsServer)
            return;
        if (CurrentState != NpcState.Attack)
            return; // 저항 중이 아닐 때의 타격은 무시 — 배회 NPC 폭행 방지

        SetSubdueGauge(Mathf.Max(0f, SubdueGauge - amount));
    }

    /// <summary>
    /// 제압 홀드 타격 요청 — 상호작용 경로(NpcSubdueInteractable)가 호출. (#79)
    /// 클라이언트에서 불리면 서버로 전달되므로 비호스트 플레이어의 타격도 게이지에 반영된다.
    /// 타격량은 서버가 자기 인스펙터 값(m_resistConfig.SubdueHitPower)을 쓴다 — 클라이언트가 수치를 보낼 수 없다.
    /// </summary>
    // TODO: 상호작용 네트워크 전환(#55 계열)에서 거리·조준 서버 검증 추가 (지금은 요청 자체는 신뢰)
    public void RequestSubdueHit()
    {
        if (IsSpawned && !IsServer)
        {
            SubdueHitRpc();
            return;
        }

        ApplySubdueHit(m_resistConfig.SubdueHitPower);
    }

    [Rpc(SendTo.Server)]
    private void SubdueHitRpc()
    {
        ApplySubdueHit(m_resistConfig.SubdueHitPower);
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

    /// <summary>
    /// 기절 진입 — 테이저의 연결고리. 지속 시간이 끝나면 스스로 일어나 <b>도주</b>한다(#269 확정).
    /// </summary>
    /// <param name="threat">
    /// 기절시킨 상대(테이저 사수). 깨어났을 때 이 대상에게서 도망친다 — 없으면(null) 도주 상태가
    /// 그 시점의 가장 가까운 추격자를 폴백으로 잡고, 주변에 아무도 없으면 배회로 돌아간다(NpcFleeState).
    /// </param>
    public void EnterStunned(Transform threat = null)
    {
        if (IsSpawned && !IsServer)
            return;

        ThreatTarget = threat;
        m_stateMachine.ChangeState(NpcState.Stunned);
    }

    // 게이지는 서버 진실값과 동기화 변수에 함께 기록한다 — 오프라인에서는 NetworkVariable에 쓰지 않는다 (#56 상태 패턴과 동일)
    private void SetSubdueGauge(float value)
    {
        m_subdueGauge = value;
        if (IsSpawned && IsServer)
            m_syncedSubdueGauge.Value = value;
    }
}
