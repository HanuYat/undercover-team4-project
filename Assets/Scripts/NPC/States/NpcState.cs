/// <summary>
/// NPC 상태 enum — GDD 10-2 기준. 이번 이슈(#10)에서는 Idle/Walk만 구현한다.
/// </summary>
public enum NpcState
{
    Idle,
    Walk,
    Run,
    Attack,
    Captured,
    Stunned,
    Escorted, // 연행 중 — 체포한 플레이어를 따라 이동 (#59). enum 값 = Animator 번호이므로 반드시 끝에만 추가할 것

    /// <summary>
    /// 수감 — 인계존 판정 후 유치장으로 이송·수용 (#228/#462).
    /// enum 값 = Animator 번호 규약의 예외: 이 값에 대응하는 Animator 상태는 없다.
    /// 좌석까지 걷는 동안엔 Escorted 모션을 빌려 쓰고, 도착해 앉으면 전용 착석(Begin/Loop)
    /// 모션으로 갈린다 — 둘 다 NpcAnimationDriver가 IsSeated를 보고 지정한다.
    /// </summary>
    Jailed,

    /// <summary>
    /// 침입 — 돌발 이벤트가 스폰한 침입자가 목표 지점(유치장 자물쇠)까지 걸어간다 (#231).
    /// Jailed와 같은 규약 예외: 대응 Animator 상태가 없어 NpcAnimationDriver가 Walk 모션을 지정한다.
    /// </summary>
    Intruding,

    /// <summary>
    /// 원한 구역 수용 — 오검거당한 시민이 석방 대신 전용 구역으로 걸어가 대기한다 (#277).
    /// 팀 오검거 카운트가 임계치를 넘으면 여기 모인 전원이 추격(Chasing)으로 출동한다.
    /// Jailed와 같은 규약 예외: 대응 Animator 상태가 없어 NpcAnimationDriver가 걷기/대기 모션을 지정한다.
    /// (수갑은 판정 시 이미 반환됐으므로(#229) Escorted가 아니라 Walk/Idle 모션이다)
    /// </summary>
    Detained,

    /// <summary>
    /// 오검거 추격 — 원한 구역에서 출동한 시민이 플레이어를 쫓는다 (#278).
    /// 가속 추격·범위 이탈 시 재타겟·격퇴(도주)·포획 후 수렴까지 이 한 상태 안의 페이즈로 처리한다.
    /// 규약 예외: NpcAnimationDriver가 속도 기준으로 Idle/Walk/Run 모션을 지정한다.
    /// </summary>
    Chasing,

    /// <summary>
    /// 오검거 호송 — 포획된 플레이어를 광장까지 끌고 간다 (#279).
    /// 선두 2명이 양옆에서 끌고 나머지는 뒤따른다. 규약 예외: 속도 기준 Walk/Idle 모션.
    /// </summary>
    PenaltyEscorting,

    /// <summary>
    /// 임시 거처 이송 — 경범죄 이벤트 NPC(난동자·난동꾼)가 판정 후 임시 거처 지점까지 걸어가 도착 시 소멸한다. (#291)
    /// Jailed와 같은 규약 예외: 대응 Animator 상태가 없어 NpcAnimationDriver가 Walk 모션을 대여한다.
    /// 원한 구역(Detained #277)과는 다른 지점·다른 목적이다(추격 출동 없이 정리 대상).
    /// </summary>
    Holding,
}
