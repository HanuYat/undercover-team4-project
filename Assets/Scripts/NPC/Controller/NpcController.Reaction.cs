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

    // E 제압 경로는 전부 제거됐다 — E는 신병 조작(재연행·줄다리기 복귀) 전용 키가 됐다.
    //  · 도주 NPC 근접 제압(CaptureBySubdue, #436) — 홀드 완주로 Run에서 Captured로 바로 점프했다.
    //  · 제압 타격(RequestSubdueHit/SubdueHitRpc/ServerSubdueHit, #438) — 맨몸 타격으로 체력을 깎았다.
    // 이제 체력을 깎는 플레이어 경로는 진압봉(Baton.ServerSwing)뿐이고, 즉시 무력화는 테이저(#292),
    // 신병 확보는 밧줄(#369)이 맡는다. 피격 반응(ReactionTrigger.Damage)도 진압봉이 직접 부른다.

    // 기절 진입(EnterStunned)은 NpcController.Stun.cs로 이사했다 — 상태 전이가 아니라
    // 오버레이 플래그가 됐기 때문이다 (#292).
}
