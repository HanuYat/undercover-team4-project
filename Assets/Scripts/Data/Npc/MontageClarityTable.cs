using UnityEngine;

/// <summary>
/// 라운드별 몽타주 화질 표 (#724). 조회 규약은 <see cref="RoundQuotaTable"/>(#377)과 같다 —
/// 첫 항목이 1라운드, 표 끝은 마지막 행 유지, 표가 비면 원본 화질을 그대로 쓴다.
/// </summary>
[CreateAssetMenu(
    fileName = "MontageClarityTable",
    menuName = "Undercover/NPC/Montage Clarity Table"
)]
public class MontageClarityTable : ScriptableObject
{
    [SerializeField]
    private MontageClarityStep[] m_steps =
    {
        new MontageClarityStep { PixelSize = 128, Fade = 0f },
        new MontageClarityStep { PixelSize = 64, Fade = 0f },
        new MontageClarityStep { PixelSize = 32, Fade = 0f },
        new MontageClarityStep { PixelSize = 16, Fade = 0f },
    };

    public bool HasRows => m_steps != null && m_steps.Length > 0;

    /// <summary>전체 단계 — 굽기 툴이 단계별 갈래 수를 재려고 순회하는 용도. 게임플레이 조회는 GetStep을 쓸 것.</summary>
    public MontageClarityStep[] Steps => m_steps;

    /// <summary>표가 비면 <paramref name="fallback"/>. 표 끝을 넘는 라운드는 마지막 행으로 clamp.</summary>
    public MontageClarityStep GetStep(int round, MontageClarityStep fallback)
    {
        if (!HasRows)
            return fallback;

        int index = Mathf.Clamp(round - RoundProgress.k_firstRound, 0, m_steps.Length - 1);
        return m_steps[index];
    }
}
