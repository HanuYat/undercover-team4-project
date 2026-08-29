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
    /// 수감 — 유치장 좌석에 수용 (#228/#462/#492). 플레이어가 끌고 들어와 놓으면 진입하고,
    /// 배정된 좌석까지 걸어가 앉는다(도시에서 스스로 걸어오던 자동 이송은 #492에서 폐기).
    /// enum 값 = Animator 번호 규약의 예외: 이 값에 대응하는 Animator 상태는 없다.
    /// 좌석까지 걷는 동안엔 Escorted 모션을 빌려 쓰고, 도착해 앉으면 전용 착석(Begin/Loop)
    /// 모션으로 갈린다 — 둘 다 NpcAnimationDriver가 IsSeated를 보고 지정한다 (#462).
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

    // Holding(임시 거처 이송, #291)이 여기 있었다 — #310이 "경범죄자도 유치장 수감"으로 바꾸며
    // 호출부를 전부 지웠고, 도달 불가 상태로 남아 있던 것을 #503에서 제거했다.

    /// <summary>
    /// 사망 — 체력 0. <b>빠져나가지 않는 유일한 상태다.</b> (#571)
    ///
    /// 기절(<see cref="Stunned"/>)과 달리 오버레이가 아니라 <b>전이</b>인 이유는 목적이 정반대이기
    /// 때문이다: 오버레이는 호송·수감·페널티 링크를 <b>지키려고</b> 상태를 안 바꾸는 것인데(#292),
    /// 사망은 그 링크를 전부 <b>끊어야</b> 한다. 상태로 두면 <see cref="NpcStateRules"/>의 제외 목록
    /// 한 곳만 고쳐도 체포·타격·밧줄·E가 함께 닫히고, 커스터디 표식 정리도
    /// <c>NpcController.HandleFsmStateChanged</c>가 이미 하던 경로를 그대로 탄다.
    ///
    /// 규약 예외: 대응 Animator 상태가 없다. 래그돌이 Animator를 통째로 끄기 때문이고
    /// (<c>NpcRagdoll</c>), 래그돌이 붙기 전까지는 NpcAnimationDriver가 기절 모션을 빌려 쓴다.
    /// </summary>
    Dead,

    /// <summary>
    /// 질주 — 목적지를 계속 갈아 끼우며 <b>멈추지 않고</b> 도심을 뛰어다닌다 (#106, 공연음란범).
    /// 도주(<see cref="Run"/>)와 달리 위협도 종료 조건도 없다 — 잡히거나 라운드가 끝날 때까지 뛴다.
    /// Jailed와 같은 규약 예외: 대응 Animator 상태가 없어 NpcAnimationDriver가 달리기 모션을 지정한다.
    /// </summary>
    Sprinting,
}
