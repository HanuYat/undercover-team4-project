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

    /// <summary>
    /// 경범죄 — 무고하지만 검거 시 도주·저항으로 공무집행을 방해한 거수자. 소액 보상 (GDD 6-2/9-1: 1,000원, #78).
    /// 행위범: 스폰 시 정해지는 죄가 아니라 도주·저항 '행위'로 성립한다 — 순순히 따라온 무고자(WrongfulArrest)와 구분된다.
    /// </summary>
    Misdemeanor,
}
