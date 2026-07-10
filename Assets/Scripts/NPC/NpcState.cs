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
    Escorted // 연행 중 — 체포한 플레이어를 따라 이동 (#59). enum 값 = Animator 번호이므로 반드시 끝에만 추가할 것
}
