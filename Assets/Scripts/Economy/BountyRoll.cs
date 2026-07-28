using UnityEngine;
using Random = UnityEngine.Random;

/// <summary>
/// 현상금 추첨 (#395) — 진범·위조범·난동꾼·침입자가 모두 이 한 경로로 금액을 뽑는다.
///
/// 100원 단위로만 나온다. 십·일의 자리까지 살아 있으면(13,590원 / 8,097원 같은 값) 수배 목록과
/// 정산 화면의 숫자가 읽기 어렵고, 만원 단위인 목표 금액과 자릿수가 어긋나 얼마나 남았는지 가늠이 안 된다.
///
/// 뽑는 시점은 각 호출자가 정한다 — 진범·위조범은 라운드 시작 배정 때(CriminalAssigner),
/// 난동꾼·침입자는 스폰 때. 판정 시점에 뽑으면 재검거로 금액을 리롤할 수 있게 된다.
/// </summary>
public static class BountyRoll
{
    /// <summary>추첨 단위 — 금액은 항상 이 값의 배수로 나온다.</summary>
    public const int k_unit = 100;

    /// <summary>
    /// [min, max] 안에서 <see cref="k_unit"/> 배수 금액을 뽑는다(양 끝 포함).
    /// 상한이 하한보다 작게 설정돼도 하한을 보장한다.
    /// </summary>
    public static int Roll(int min, int max)
    {
        if (max < min)
            max = min;

        // 단위 배수 중 범위 안에 들어오는 것만 후보로 삼는다 — 먼저 뽑고 반올림하면 범위 밖으로 샌다
        int lo = Mathf.CeilToInt(min / (float)k_unit);
        int hi = Mathf.FloorToInt(max / (float)k_unit);

        // 범위가 좁아 배수를 하나도 담지 못하는 설정(예: 550~560) — 가장 가까운 배수로 떨어뜨린다
        if (hi < lo)
            return Mathf.RoundToInt(min / (float)k_unit) * k_unit;

        return Random.Range(lo, hi + 1) * k_unit;
    }
}
