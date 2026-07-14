/// <summary>
/// 플레이어 상태 enum — GDD 10-2 기준. (#105)
/// GDD 원문은 Die를 예약했으나, HP 0은 '사망'이 아니라 '다운(구조 가능)'이다(GDD 7-5).
/// 따라서 Die 대신 Down을 도입한다 — 진짜 파괴(복구 불가)가 생기면 그때 Die를 추가한다.
/// (애니메이터 번호 매핑이 생기면 값 순서 = Animator 번호이므로 그때 순서 확정)
/// </summary>
public enum PlayerState
{
    Idle,
    Walk,
    Run,
    Attack,
    Down,
    Dance
}
