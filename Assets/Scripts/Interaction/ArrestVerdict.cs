/// <summary>
/// 검거 판정 결과 — 본부로 인계된 NPC가 올바른 체포 대상이었는지. (GDD 7-2/7-3/9-1, #41)
/// 인계 NPC의 실제 신원(CitizenIdentity.IsCriminal)을 대조해 결정된다.
/// </summary>
public enum ArrestVerdict
{
    /// <summary>현상수배범 — 실제 범인 검거. 보상 지급 대상 (GDD 9-1: 10,000원).</summary>
    WantedCriminal,

    /// <summary>
    /// 오검거 — 진범이 아닌 대상을 검거함. 보상 없음, 개인별 오검거 기록 대상 (GDD 7-3).
    /// 도주·저항한 무고 시민을 잡아도 이 판정이다 — 도주/저항은 진범을 헷갈리게 하는 미끼 행동일 뿐,
    /// 진범(WantedCriminal) 외의 검거는 순응·도주·저항을 가리지 않고 전부 오검거로 성립한다. (#78)
    /// </summary>
    WrongfulArrest,
}
