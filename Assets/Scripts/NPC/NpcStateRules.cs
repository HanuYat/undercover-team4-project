/// <summary>
/// NPC 상태 → 상호작용 가능성 해석 규칙. (#184)
/// 클라 조기검증(Handcuffs)·서버 가드(PlayerEscorter)·조준 피드백(InteractionFeedback)이
/// 모두 여기를 읽는다 — 새 상태 추가 시 이 파일만 고치면 셋이 함께 움직인다.
/// 상태별 '행동'은 NpcXxxState 클래스(FSM, 서버 전용), 상태별 '가능 여부'는 여기 — 역할 분리.
/// 클라이언트는 동기화된 enum(NpcController.CurrentState)만 알기 때문에 순수 함수로 둔다.
/// </summary>
public static class NpcStateRules
{
    /// <summary>수갑 체포 채널링의 대상이 될 수 있는 상태인가.
    /// 제외 목록 방식 — 새 상태는 기본 '체포 가능'이므로 막아야 하면 여기 추가할 것.
    /// 도주(Run)·저항(Attack)은 수갑이 아니라 E 제압 홀드·테이저로만 잡는다 (GDD 6-1/7-4, #254) —
    /// 반응이 시작된 뒤에는 수갑 채널링이 걸리지 않아야 한다.</summary>
    public static bool IsCapturable(NpcState state) =>
        state != NpcState.Escorted
        && state != NpcState.Captured
        && state != NpcState.Jailed
        && state != NpcState.Run
        && state != NpcState.Attack
        // 오검거 페널티에 얽힌 시민(수용·추격·호송)은 다시 수갑을 채울 수 없다 (#277~#279) —
        // 추격대를 체포해 페널티 집행을 무산시키는 우회를 막는다 (회피 수단은 격퇴(호루라기 #250)뿐)
        && state != NpcState.Detained
        && state != NpcState.Chasing
        && state != NpcState.PenaltyEscorting;

    /// <summary>테이저 스턴이 걸리는 상태인가 — 수갑용 <see cref="IsCapturable"/>과 분리한다 (#289).
    /// 도주(Run)·저항(Attack)이 주 대상이라 <see cref="IsCapturable"/>과 달리 이 둘을 제외하지 않는다.
    /// 제외하는 건 이미 신병을 확보(Escorted/Captured/Jailed)했거나 오검거 페널티가 진행(Detained/
    /// Chasing/PenaltyEscorting) 중인 상태뿐 — 이들은 스턴이 호송·수감·페널티 집행을 끊어버리고(스턴은
    /// Idle로 복귀할 뿐 원래 링크로 못 돌아온다) 전술 가치도 없어 no-op으로 둔다.
    /// 전 상태 스턴 + 원상 복귀(pause-resume)는 별도 이슈(#292)에서 오버레이 방식으로 다룬다.
    /// Stunned 재진입은 상태 머신이 같은 상태로 막으므로 자연히 무해한 no-op이다.</summary>
    public static bool CanBeStunned(NpcState state) =>
        state != NpcState.Escorted
        && state != NpcState.Captured
        && state != NpcState.Jailed
        && state != NpcState.Detained
        && state != NpcState.Chasing
        && state != NpcState.PenaltyEscorting;

    /// <summary>밧줄로 묶어 끌 수 있는 상태인가 — 테이저로 기절한 대상만. (#269)
    /// 수갑 연행과 역할이 갈린다: 수갑은 순응형 즉시 연행, 밧줄은 기절시킨 대상 전용.</summary>
    public static bool IsRopeable(NpcState state) => state == NpcState.Stunned;

    /// <summary>E 상호작용(제압·타격·재연행)이 반응하는 상태인가.
    /// 포함 목록 방식 — 새 상태는 기본 'E 불가'이므로 열어야 하면 여기 추가할 것.
    /// NpcSubdueInteractable.Interact의 분기 집합과 반드시 일치해야 한다.</summary>
    public static bool HasSubdueInteraction(NpcState state) =>
        state is NpcState.Run or NpcState.Attack or NpcState.Captured;
}
