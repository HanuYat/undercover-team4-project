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
    /// 반응이 시작된 뒤에는 수갑 채널링이 걸리지 않아야 한다.
    /// <b>호출부는 대개 이쪽이 아니라 컨트롤러 오버로드를 써야 한다</b> — 스턴 오버레이가 상태값에
    /// 나타나지 않기 때문이다 (#292).</summary>
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

    /// <summary>수갑 체포 채널링의 대상이 될 수 있는가 — 위 상태 판정에 스턴 오버레이를 얹은 것. (#292)
    /// 기절한 대상은 도주(Run)·저항(Attack) 중이어도 수갑이 채워진다: 무방비로 누워 있는데
    /// "반응 중"이라는 이유로 막으면 테이저→수갑 콤보가 성립하지 않는다(오버레이 전에는 상태가
    /// Stunned로 바뀌어 저절로 통과됐다).
    /// 다만 <b>확보·페널티군까지 열리지는 않는다</b> — 기절했다고 남이 호송 중인 대상을 가로챌 수 없다.</summary>
    public static bool IsCapturable(NpcController npc) =>
        npc != null
        && (IsCapturable(npc.CurrentState) || (IsIncapacitated(npc) && IsReactive(npc.CurrentState)));

    /// <summary>타격 피해가 들어가는 상태인가 — <b>스턴 게이트가 아니다.</b> (#292)
    /// 스턴은 오버레이가 되면서 전 상태에 걸리게 됐지만(#292), 타격까지 함께 열면 연행 중인
    /// NPC를 때려 기절시켜 신병에서 빼내는 우회가 생긴다. 그래서 게이트를 둘로 쪼개고
    /// 이쪽은 구 CanBeStunned(#289)의 제외 목록을 그대로 물려받았다 — 팀 결정은
    /// "스턴은 허용, 타격은 차단"이다.
    ///
    /// 제외하는 건 이미 신병을 확보(Escorted/Captured/Jailed)했거나 오검거 페널티가 진행(Detained/
    /// Chasing/PenaltyEscorting) 중인 상태 — 도주(Run)·저항(Attack)은 주 타격 대상이라 제외하지 않는다.</summary>
    public static bool CanBeDamaged(NpcState state) =>
        state != NpcState.Escorted
        && state != NpcState.Captured
        && state != NpcState.Jailed
        && state != NpcState.Detained
        && state != NpcState.Chasing
        && state != NpcState.PenaltyEscorting;

    /// <summary>반응·배회군인가 — 스턴이 풀릴 때 도주로 전환되는 쪽. (#292)
    /// 여집합(확보·페널티군 + Holding)은 스턴이 풀려도 아무 전이 없이 하던 일을 재개한다 —
    /// 상태 enum이 애초에 안 바뀌므로 호송·수감·페널티·정리 링크가 그대로 살아 있다.
    ///
    /// 포함 목록 방식이라 <b>새 상태는 기본이 '재개'</b>다. 도주로 깨어나야 하면 여기 추가할 것.
    /// 의도적으로 뺀 둘: <see cref="NpcState.Holding"/>(#291 — 임시 거처로 걸어가 소멸하는 정리
    /// 대상이라 도주시키면 경로가 끊긴다)과 <see cref="NpcState.Stunned"/>(넉백 KO — 자기 상태
    /// 클래스가 스스로 빠져나간다).</summary>
    public static bool IsReactive(NpcState state) =>
        state is NpcState.Idle
            or NpcState.Walk
            or NpcState.Run
            or NpcState.Attack
            or NpcState.Intruding;

    /// <summary>무력화(기절) 상태인가 — 기절 경로가 둘이라 호출부가 매번 OR를 쓰지 않게 모은다. (#292)
    /// 오버레이(테이저·체력 0 #366)와 넉백 KO(<see cref="NpcState.Stunned"/>)를 함께 잡는다.
    /// <b>새로 "기절인가?"를 묻는 코드는 반드시 이걸 쓸 것</b> — CurrentState만 보면 오버레이
    /// 경로가 조용히 누락되고, IsStunned만 보면 넉백 KO가 누락된다.</summary>
    public static bool IsIncapacitated(NpcController npc) =>
        npc != null && (npc.IsStunned || npc.CurrentState == NpcState.Stunned);

    /// <summary>밧줄로 묶어 끌 수 있는 대상인가 — 기절한 대상만. (#269)
    /// 기절 경로가 둘이라 테이저 전용이 아니다: 테이저 직격, 제압 타격으로 HP 0 도달(#366),
    /// 넉백 착지가 모두 대상이다. 상태값이 아니라 컨트롤러를 받는 이유는 오버레이(#292)가
    /// NpcState에 나타나지 않기 때문이다.
    /// 수갑 연행과 역할이 갈린다: 수갑은 순응형 즉시 연행, 밧줄은 기절시킨 대상 전용.</summary>
    public static bool IsRopeable(NpcController npc) => IsIncapacitated(npc);

    /// <summary>빈손 좌클릭 채널링으로 수갑을 풀어 회수할 수 있는 '상태'인가 — 체포되어 멈춘 대상. (#290)
    /// 상태 게이트는 Captured만(ReleaseFromCustody의 게이트와 일치). 순수 함수라 여기서 수갑 유무는
    /// 보지 않는다 — 호출부(PlayerItemUser·PlayerEscorter)가 npc.HasHandcuffs를 함께 걸어 '수갑 찬
    /// Captured'로 좁힌다. 수갑 없이 제압만 된 Captured(도주·저항 제압)는 대상이 아니며, 그런 NPC는
    /// 인계 방치 타이머(NpcCapturedState)로 스스로 풀려난다.</summary>
    public static bool IsUncuffable(NpcState state) => state == NpcState.Captured;

    /// <summary>E 상호작용(제압·타격·재연행)이 반응하는 상태인가.
    /// 포함 목록 방식 — 새 상태는 기본 'E 불가'이므로 열어야 하면 여기 추가할 것.
    /// NpcSubdueInteractable.Interact의 분기 집합과 반드시 일치해야 한다.
    /// 배회(Idle/Walk)가 열린 것은 체력이 지속형이 되면서다 (#366) — 예전에는 '배회 NPC 폭행 방지'로
    /// 막혀 있었지만, 이제 아무 때나 때려 체력을 깎을 수 있다.</summary>
    public static bool HasSubdueInteraction(NpcState state) =>
        state is NpcState.Idle or NpcState.Walk or NpcState.Run or NpcState.Attack or NpcState.Captured;
}
