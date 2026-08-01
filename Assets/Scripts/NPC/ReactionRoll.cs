using Random = UnityEngine.Random;

/// <summary>
/// 검거 반응 추첨 (#76 · #78) — 순응/도주/저항을 가중치 비율로 뽑는다.
///
/// 뽑는 시점은 호출자가 정한다: 라운드 시작 배정(<see cref="CriminalAssigner"/>)과
/// 제보 전화 승격(<see cref="SuspectRevealer"/>)이 같은 규칙을 써야 해서 여기 하나로 모았다.
/// 예비 용의자는 시민 가중치로 뽑혀 있다가 승격되는 순간 범인 가중치로 다시 뽑히는데,
/// 두 경로가 각자 계산하면 그 규칙이 조용히 어긋난다. (<see cref="BountyRoll"/>과 같은 방침)
/// </summary>
public static class ReactionRoll
{
    /// <summary>
    /// 가중치 비율로 반응을 뽑는다. 합이 1일 필요는 없다 — 비율로만 쓴다.
    /// 셋 다 0이면 안전하게 <see cref="ReactionType.Compliant"/>.
    /// </summary>
    public static ReactionType Roll(float compliantWeight, float fleeWeight, float resistWeight)
    {
        float total = compliantWeight + fleeWeight + resistWeight;
        if (total <= 0f)
            return ReactionType.Compliant; // 가중치가 전부 0이면 안전하게 순응

        float roll = Random.Range(0f, total);
        if (roll < compliantWeight)
            return ReactionType.Compliant;
        if (roll < compliantWeight + fleeWeight)
            return ReactionType.Flee;
        return ReactionType.Resist;
    }
}
