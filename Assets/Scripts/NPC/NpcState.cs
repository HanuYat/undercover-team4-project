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
    Panic,
    Escorted, // 연행 중 — 체포한 플레이어를 따라 이동 (#59). enum 값 = Animator 번호이므로 반드시 끝에만 추가할 것

    /// <summary>
    /// 수감 — 인계존 판정 후 유치장으로 이송·수용 (#228).
    /// enum 값 = Animator 번호 규약의 예외: 이 값에 대응하는 Animator 상태는 없다.
    /// 이송 중엔 Escorted(수갑 찬 채 걷기), 수용 후엔 Captured(수갑 찬 대기) 모션을
    /// NpcAnimationDriver가 대신 지정한다.
    /// </summary>
    Jailed,

    /// <summary>
    /// 침입 — 돌발 이벤트가 스폰한 침입자가 목표 지점(유치장 자물쇠)까지 걸어간다 (#231).
    /// Jailed와 같은 규약 예외: 대응 Animator 상태가 없어 NpcAnimationDriver가 Walk 모션을 지정한다.
    /// </summary>
    Intruding
}
