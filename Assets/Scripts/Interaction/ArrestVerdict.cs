/// <summary>
/// 검거 판정 결과 — 본부로 인계된 NPC가 올바른 체포 대상이었는지. (GDD 7-2/7-3/9-1, #41)
/// 인계 NPC의 실제 신원(CitizenIdentity.IsCriminal)을 대조해 결정된다.
/// </summary>
public enum ArrestVerdict
{
    /// <summary>현상수배범 — 실제 범인 검거. 보상 지급 대상 (GDD 9-1: 10,000원).</summary>
    WantedCriminal,

    /// <summary>오검거 — 올바른 체포 대상이 아니었음. 보상 없음, 개인별 오검거 기록 대상 (GDD 7-3).</summary>
    WrongfulArrest,

    // TODO: Misdemeanor(경범죄) — 위조 정보 소지 대상(GDD 6-2/9-1: 1,000원).
    //       위조 정보 생성·거수자(#78)가 아직 없어 지금은 배정 불가 — 해당 시스템 도입 후 추가.
}
