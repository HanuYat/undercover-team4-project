using System;
using System.Collections.Generic;

/// <summary>
/// 폭탄 선 색 — 매뉴얼 규칙과 선 시각화가 공유하는 색 팔레트. (#232)
/// 값 순서는 시드 결정론에 영향을 주므로 함부로 재배치하지 말 것(뒤에 추가만).
/// </summary>
public enum BombWireColor
{
    Red,
    Blue,
    Yellow,
    Black,
    White,
}

/// <summary>규칙의 조건 — "이 특징이면" 부분. 매뉴얼 렌더와 서버 판정이 같은 enum을 해석한다.</summary>
public enum BombRuleCondition
{
    NoRed,        // 빨강 선이 하나도 없으면
    LastIsBlack,  // 마지막 선이 검정이면
    TwoOrMoreRed, // 빨강 선이 2개 이상이면
    SerialOdd,    // 일련번호 끝자리가 홀수면
    SerialEven,   // 일련번호 끝자리가 짝수면
    Always,       // 그 외에는 (기본 규칙 — 항상 성립)
}

/// <summary>규칙의 행동 — "이 선을 자른다" 부분. 색 조건부 행동은 해당 색이 없으면 성립하지 않는다.</summary>
public enum BombRuleAction
{
    CutFirst,        // 첫 번째 선
    CutLast,         // 마지막 선
    CutFirstOfColor, // 첫 번째 {색} 선
    CutLastOfColor,  // 마지막 {색} 선
    CutSecond,       // 두 번째 선
}

/// <summary>규칙 1줄 — 조건 + 행동(+ 색 인자). 매뉴얼에 사람이 읽는 문장으로 렌더된다.</summary>
public readonly struct BombRule
{
    public readonly BombRuleCondition Condition;
    public readonly BombRuleAction Action;
    public readonly BombWireColor ActionColor; // 색 조건부 행동에서만 의미

    public BombRule(BombRuleCondition condition, BombRuleAction action, BombWireColor actionColor)
    {
        Condition = condition;
        Action = action;
        ActionColor = actionColor;
    }
}

/// <summary>퍼즐 생성 튜너블 — 인스펙터(BombDevice) 노출용. 규칙 의미 자체는 코드가 소유한다(단일 진실 소스).</summary>
[Serializable]
public class BombPuzzleConfig
{
    // 매뉴얼에 노출되는 규칙 수. 마지막 1줄은 항상 기본(Always) 규칙이라 최소 2 이상이어야 의미가 있다.
    public int ruleCount = 5;

    // 일련번호 범위(양끝 포함). 끝자리 홀짝만 규칙에 쓰지만 표시는 전체 번호로 한다.
    public int serialMin = 100;
    public int serialMax = 999;
}

/// <summary>
/// 폭탄 해체 퍼즐 — 정수 시드 하나에서 <b>결정론적으로</b> 재구성되는 퍼즐. (#232)
///
/// <b>단일 진실 소스.</b> 매뉴얼 UI(본부가 읽는 규칙표)와 서버 검증(정답 선 판정)은 반드시 같은
/// 퍼즐을 봐야 한다 — 어긋나면 "규칙대로 잘랐는데 폭발"이 난다. 그래서 시드 하나만 전 클라에
/// 동기화하고(<see cref="BombDevice"/>의 NetworkVariable), 모든 피어가 이 빌더로 동일 퍼즐을
/// 재구성한다. 규칙 의미가 전부 이 코드에 있으므로 매뉴얼과 코드가 어긋날 여지가 없다.
///
/// 규칙표는 시드로 매번 재배치된다(규칙 종류·색·순서가 흔들린다) — 본부는 "항상 빨강"을 외울 수
/// 없고 매 라운드 현재 규칙표를 읽어 폭탄 특징에 적용해야 한다. (KTANE 모델)
///
/// 결정론 전제: 같은 빌드의 모든 피어가 같은 시드로 같은 결과를 내야 한다. <see cref="System.Random"/>은
/// 시드가 같으면 같은 수열을 주므로(런타임 동일) 안전하다. 규칙 순서/정답이 절대 어긋나면 안 되는 값이라
/// 다른 랜덤 소스(UnityEngine.Random 전역 상태 등)를 섞지 말 것.
/// </summary>
public sealed class BombPuzzle
{
    private static readonly BombWireColor[] s_palette =
    {
        BombWireColor.Red,
        BombWireColor.Blue,
        BombWireColor.Yellow,
        BombWireColor.Black,
        BombWireColor.White,
    };

    /// <summary>각 선의 색 — 인덱스 = 프리팹 선 순서. 전 클라가 같은 배열을 재구성한다.</summary>
    public BombWireColor[] WireColors { get; }

    /// <summary>폭탄 일련번호 — 표시용 전체 번호. 규칙은 끝자리 홀짝만 본다.</summary>
    public int Serial { get; }

    /// <summary>매뉴얼에 노출되는 규칙표(순서 = 우선순위, 첫 성립 규칙이 정답을 정한다).</summary>
    public IReadOnlyList<BombRule> Rules { get; }

    /// <summary>정답 선 인덱스 — 규칙표를 특징에 적용해 계산된 유일 해. 서버만 검증에 쓴다.</summary>
    public int CorrectWireIndex { get; }

    private BombPuzzle(BombWireColor[] wireColors, int serial, IReadOnlyList<BombRule> rules, int correctWireIndex)
    {
        WireColors = wireColors;
        Serial = serial;
        Rules = rules;
        CorrectWireIndex = correctWireIndex;
    }

    /// <summary>
    /// 시드로 퍼즐을 결정론적으로 만든다. <paramref name="wireCount"/>는 프리팹의 실제 선 수 —
    /// 색 배열 길이와 행동 인덱스 해석의 기준이 된다.
    /// </summary>
    public static BombPuzzle Build(BombPuzzleConfig config, int wireCount, int seed)
    {
        if (config == null)
            config = new BombPuzzleConfig();
        if (wireCount < 1)
            wireCount = 1;

        Random rng = new Random(seed);

        // 1) 특징 생성 — 선 색과 일련번호. 전 피어가 같은 시드로 같은 특징을 얻는다.
        BombWireColor[] colors = new BombWireColor[wireCount];
        for (int i = 0; i < wireCount; i++)
            colors[i] = s_palette[rng.Next(s_palette.Length)];

        int serial = rng.Next(config.serialMin, config.serialMax + 1);

        // 2) 규칙표 생성 — 조건은 중복 없이(섞어서 앞에서부터), 마지막은 항상 성립하는 기본 규칙으로 닫는다.
        //    (기본 규칙이 없으면 어떤 특징에도 안 걸리는 폭탄이 나와 해체 불능이 된다)
        //    같은 조건이 여러 줄이면 대부분 죽은 규칙이 되어 매뉴얼이 얕아지므로 중복을 피한다.
        int ruleCount = Math.Max(2, config.ruleCount);

        List<BombRuleCondition> pool = new List<BombRuleCondition>();
        for (int c = 0; c < (int)BombRuleCondition.Always; c++) // Always 앞의 조건들 = 기본이 아닌 조건 전체
            pool.Add((BombRuleCondition)c);
        for (int i = pool.Count - 1; i > 0; i--) // 시드로 섞는다 (Fisher–Yates)
        {
            int j = rng.Next(i + 1);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }

        int need = ruleCount - 1;
        List<BombRule> rules = new List<BombRule>(ruleCount);
        for (int i = 0; i < need; i++)
            rules.Add(MakeRule(rng, pool[i % pool.Count], colors)); // 규칙 수가 조건 수보다 많으면 순환(중복 허용)
        rules.Add(MakeRule(rng, BombRuleCondition.Always, colors)); // 기본 규칙 — 항상 성립

        // 3) 정답 계산 — 위에서부터 처음으로 "조건 성립 + 행동이 유효 선을 가리키는" 규칙이 정답을 정한다.
        int correct = ResolveCorrectWire(rules, colors, serial);

        return new BombPuzzle(colors, serial, rules, correct);
    }

    private static BombRule MakeRule(Random rng, BombRuleCondition condition, BombWireColor[] colors)
    {
        BombRuleAction action = (BombRuleAction)rng.Next(Enum.GetValues(typeof(BombRuleAction)).Length);
        // 색 조건부 행동은 폭탄에 실제로 있는 색에서 고른다 — 없는 색을 가리켜 규칙이 죽는 일을 막는다.
        BombWireColor color = colors.Length > 0 ? colors[rng.Next(colors.Length)] : s_palette[rng.Next(s_palette.Length)];
        return new BombRule(condition, action, color);
    }

    // 규칙표를 특징에 적용해 정답 선을 찾는다. 매뉴얼 렌더와 서버 판정이 반드시 이 로직을 공유한다.
    private static int ResolveCorrectWire(IReadOnlyList<BombRule> rules, BombWireColor[] colors, int serial)
    {
        for (int i = 0; i < rules.Count; i++)
        {
            if (!ConditionHolds(rules[i], colors, serial))
                continue;

            if (TryResolveAction(rules[i], colors, out int wire))
                return wire;
            // 조건은 성립했지만 행동이 유효 선을 못 가리키면(예: 없는 색) 다음 규칙으로 넘어간다
        }

        // 기본 규칙(Always)이 항상 성립하고 CutFirst류는 언제나 유효하므로 여기 도달하지 않지만,
        // 방어적으로 첫 선을 정답으로 둔다.
        return 0;
    }

    public static bool ConditionHolds(BombRule rule, BombWireColor[] colors, int serial)
    {
        switch (rule.Condition)
        {
            case BombRuleCondition.NoRed:
                return CountColor(colors, BombWireColor.Red) == 0;
            case BombRuleCondition.LastIsBlack:
                return colors.Length > 0 && colors[colors.Length - 1] == BombWireColor.Black;
            case BombRuleCondition.TwoOrMoreRed:
                return CountColor(colors, BombWireColor.Red) >= 2;
            case BombRuleCondition.SerialOdd:
                return (serial % 10) % 2 == 1;
            case BombRuleCondition.SerialEven:
                return (serial % 10) % 2 == 0;
            case BombRuleCondition.Always:
                return true;
            default:
                return false;
        }
    }

    // 행동이 가리키는 선 인덱스를 낸다. 색 조건부 행동은 그 색 선이 없으면 false.
    public static bool TryResolveAction(BombRule rule, BombWireColor[] colors, out int wireIndex)
    {
        wireIndex = -1;
        if (colors.Length == 0)
            return false;

        switch (rule.Action)
        {
            case BombRuleAction.CutFirst:
                wireIndex = 0;
                return true;
            case BombRuleAction.CutLast:
                wireIndex = colors.Length - 1;
                return true;
            case BombRuleAction.CutSecond:
                if (colors.Length < 2)
                    return false;
                wireIndex = 1;
                return true;
            case BombRuleAction.CutFirstOfColor:
                return TryFindColor(colors, rule.ActionColor, false, out wireIndex);
            case BombRuleAction.CutLastOfColor:
                return TryFindColor(colors, rule.ActionColor, true, out wireIndex);
            default:
                return false;
        }
    }

    private static bool TryFindColor(BombWireColor[] colors, BombWireColor color, bool fromEnd, out int index)
    {
        if (fromEnd)
        {
            for (int i = colors.Length - 1; i >= 0; i--)
            {
                if (colors[i] == color)
                {
                    index = i;
                    return true;
                }
            }
        }
        else
        {
            for (int i = 0; i < colors.Length; i++)
            {
                if (colors[i] == color)
                {
                    index = i;
                    return true;
                }
            }
        }

        index = -1;
        return false;
    }

    private static int CountColor(BombWireColor[] colors, BombWireColor color)
    {
        int count = 0;
        for (int i = 0; i < colors.Length; i++)
        {
            if (colors[i] == color)
                count++;
        }
        return count;
    }

    // ---- 매뉴얼 표시 (본부가 읽는 문장) ----
    // 규칙 데이터에서 직접 문장을 만든다 — 매뉴얼 UI가 이 문자열을 그대로 띄우므로
    // 규칙 의미와 표시 문구가 어긋날 수 없다(둘 다 여기서 나온다).

    /// <summary>규칙 1줄을 "조건이면 이 선을 잘라라" 문장으로 만든다.</summary>
    public static string DescribeRule(BombRule rule)
    {
        return $"{DescribeCondition(rule.Condition)} {DescribeAction(rule)} 자른다.";
    }

    public static string DescribeCondition(BombRuleCondition condition)
    {
        switch (condition)
        {
            case BombRuleCondition.NoRed:
                return "빨강 선이 하나도 없으면";
            case BombRuleCondition.LastIsBlack:
                return "마지막 선이 검정이면";
            case BombRuleCondition.TwoOrMoreRed:
                return "빨강 선이 2개 이상이면";
            case BombRuleCondition.SerialOdd:
                return "일련번호 끝자리가 홀수면";
            case BombRuleCondition.SerialEven:
                return "일련번호 끝자리가 짝수면";
            case BombRuleCondition.Always:
                return "그 외에는";
            default:
                return "?";
        }
    }

    public static string DescribeAction(BombRule rule)
    {
        switch (rule.Action)
        {
            case BombRuleAction.CutFirst:
                return "첫 번째 선을";
            case BombRuleAction.CutLast:
                return "마지막 선을";
            case BombRuleAction.CutSecond:
                return "두 번째 선을";
            case BombRuleAction.CutFirstOfColor:
                return $"첫 번째 {DescribeColor(rule.ActionColor)} 선을";
            case BombRuleAction.CutLastOfColor:
                return $"마지막 {DescribeColor(rule.ActionColor)} 선을";
            default:
                return "?";
        }
    }

    public static string DescribeColor(BombWireColor color)
    {
        switch (color)
        {
            case BombWireColor.Red:
                return "빨강";
            case BombWireColor.Blue:
                return "파랑";
            case BombWireColor.Yellow:
                return "노랑";
            case BombWireColor.Black:
                return "검정";
            case BombWireColor.White:
                return "하양";
            default:
                return "?";
        }
    }
}
