/// <summary>
/// 정산 화면 개인 칭호 — 팀원마다 최대 1개 (#739). 값 이름이 곧 <c>Settlement.Title.</c> + 이름 키다.
/// 한 사람이 여러 칭호 1위면 선언 순서가 우선순위(SettlementController.BuildPlayerTitles) — 아래로 갈수록 후순위.
/// (2026-09-01, 임시 판단 — 팀 검토 가능)
/// </summary>
[LocalizedEnum("SettlementTable", "Settlement.Title.", nameof(SettlementTitleKind.None))]
public enum SettlementTitleKind
{
    None, // 키 없음 — 표시하지 않는다
    TopArrester, // 최다 진범 체포
    TopOffender, // 최다 오검거 (기존 "미친 로봇" 코믹 스탯)
    TopInnocentKiller, // 최다 무고 시민(순수 민간인) 사살
    TopDowns, // 최다 다운·즉사(무력화 진입) 횟수
    TopRescuer, // 최다 팀원 구조(리바이브 + 부활 키트) 성공
}
