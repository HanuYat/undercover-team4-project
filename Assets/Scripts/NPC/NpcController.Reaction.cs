using Unity.Netcode;
using UnityEngine;
using Random = UnityEngine.Random;

public partial class NpcController
{
    // ---- 검거 반응 (#76 → 트리거 변경 #400) ----
    // FSM 전이는 전부 서버 권위 — 클라이언트 호출은 StartEscort와 같은 방식으로 무시한다.
    // TODO: 아이템/상호작용 네트워크 전환(#55 계열) 시 클라 입력 → ServerRpc 경로로 연결

    /// <summary>
    /// 반응 판정 진입점 — 스캔·플레이어 타격이 공유한다. 서버(또는 오프라인) 전용. (#400)
    ///
    /// 도주형·저항형은 트리거와 무관하게 각자의 반응을 하고, 순응형만 갈린다 — 스캔에는 무반응,
    /// 피격에는 도주·저항 중 랜덤이며 뽑은 결과는 <see cref="CitizenIdentity.AssignReaction"/>으로
    /// 1회 확정이다(매번 재추첨하면 연타 도중 유형이 오가 전투가 성립하지 않는다).
    /// 밧줄 묶기는 더 이상 반응을 굴리지 않는다 — 순수 검거 수단이다.
    /// </summary>
    /// <param name="trigger">무엇이 반응을 촉발했는가 — 순응형 처리가 갈린다.</param>
    /// <param name="threat">위협 대상(가해자·스캔한 플레이어). 도주 방향과 저항 대상이 된다. null 허용.</param>
    public void ServerReactTo(ReactionTrigger trigger, Transform threat)
    {
        if (IsSpawned && !IsServer)
            return;

        // 이미 반응 중이거나 확보·페널티 상태면 재판정하지 않는다 — 규칙은 NpcStateRules가 갖는다
        if (!NpcStateRules.CanStartReaction(CurrentState))
            return;

        // 기절 중엔 반응하지 않는다 — 쓰러진 대상은 그대로 잡힌다 (테이저 콤보)
        if (IsStunned)
            return;

        CitizenIdentity identity = GetComponent<CitizenIdentity>();
        if (identity == null)
            return;

        switch (ResolveReaction(identity, trigger))
        {
            case ReactionType.Flee:
                StartFlee(threat);
                return;

            case ReactionType.Resist:
                StartResist(threat);
                return;
        }
    }

    // 배정된 유형을 그대로 쓰되, 순응형만 트리거에 따라 갈린다. 서버 전용.
    private static ReactionType ResolveReaction(CitizenIdentity identity, ReactionTrigger trigger)
    {
        ReactionType assigned = identity.Reaction;
        if (assigned != ReactionType.Compliant)
            return assigned;

        // 순응형은 스캔에 반응하지 않는다 — "의심받아도 태연한 시민"이 그 유형의 정의다
        if (trigger != ReactionTrigger.Damage)
            return ReactionType.Compliant;

        // 맞으면 도주·저항 중 하나로 돌변하고, 그 유형으로 굳는다 (1회 확정)
        ReactionType rolled = Random.value < 0.5f ? ReactionType.Flee : ReactionType.Resist;
        identity.AssignReaction(rolled);
        return rolled;
    }

    /// <summary>도주 시작 — threat(플레이어) 반대 방향으로 달아난다. 반응 판정은 ServerReactTo가 한다.</summary>
    public void StartFlee(Transform threat)
    {
        if (IsSpawned && !IsServer)
            return;

        ThreatTarget = threat;
        m_stateMachine.ChangeState(NpcState.Run);
    }

    /// <summary>위협 참조 정리 — 반응(도주·저항)이 끝나는 지점에서 호출한다.</summary>
    public void ClearThreat() => ThreatTarget = null;

    /// <summary>저항 시작 — 그 자리에서 버틴다. 반응 판정은 ServerReactTo가 한다.</summary>
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
    /// <param name="attacker">때린 플레이어. 피격 반응(#400)의 위협 대상이 된다.</param>
    public void RequestSubdueHit(GameObject attacker)
    {
        if (IsSpawned && !IsServer)
        {
            // 요청자를 함께 싣는다 (#400) — 예전엔 안 실어 서버에서 attacker가 항상 null이었다.
            // 피격이 트리거가 되면서 때린 사람을 정확히 물어야 그쪽으로 반격·도주한다.
            NetworkObject attackerObj =
                attacker != null ? attacker.GetComponentInParent<NetworkObject>() : null;
            SubdueHitRpc(attackerObj != null ? new NetworkObjectReference(attackerObj) : default);
            return;
        }

        ServerSubdueHit(attacker);
    }

    [Rpc(SendTo.Server)]
    private void SubdueHitRpc(NetworkObjectReference attackerRef)
    {
        // 해석 실패(디스폰 등)면 null — 위협이 null이어도 NpcFleeState가 추격자를 스캔해 폴백한다 (#269).
        GameObject attacker = attackerRef.TryGet(out NetworkObject obj) ? obj.gameObject : null;
        ServerSubdueHit(attacker);
    }

    // 타격량은 서버가 자기 config 값을 쓴다 — 클라이언트가 수치를 보낼 수 없다.
    private void ServerSubdueHit(GameObject attacker)
    {
        TakeDamage(m_commonConfig.SubdueHitPower, attacker);

        // 피해 뒤에 반응 (#400) — HP 0으로 기절했으면 ServerReactTo가 걸러 내고(깨어날 때의 도주 전환은
        // NpcStunnedState 담당), 피해가 상태 게이트로 무시된 경우엔 CanStartReaction이 함께 막는다.
        ServerReactTo(ReactionTrigger.Damage, attacker != null ? attacker.transform : null);
    }

    // 도주 NPC 근접 제압(CaptureBySubdue)은 제거됐다 (#436) — 홀드 완주로 Run에서 Captured로
    // 바로 점프하던 도주형 전용 경로다. 이제 도주형도 타격으로 체력을 깎아 기절시킨 뒤
    // 밧줄로 끌어 신병을 확보한다(저항형과 동일, #366/#369).

    // 기절 진입(EnterStunned)은 NpcController.Stun.cs로 이사했다 — 상태 전이가 아니라
    // 오버레이 플래그가 됐기 때문이다 (#292).
}
