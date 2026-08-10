using UnityEngine;

/// <summary>
/// 라운드별 할당량(목표 금액) 표 (#377) — 라운드가 지날수록 벌어야 하는 금액이 오르는 난이도 레버.
/// 수치는 밸런싱 보류 항목(GDD 12장)이라 코드에 박지 않고 데이터 에셋으로 뺀다(GDD 10-4).
///
/// 첫 항목이 1라운드다(RoundProgress.k_firstRound 기준). 표 끝을 넘어가면 마지막 값을 그대로 유지한다 —
/// 라운드가 무한히 이어져도 할당량이 폭주하지 않는다.
/// 표를 지정하지 않거나 항목이 비어 있으면 RoundManager의 인스펙터 목표 금액을 그대로 쓴다 —
/// 표를 안 붙인 씬은 지금과 똑같이 돌아야 한다.
/// </summary>
[CreateAssetMenu(fileName = "RoundQuotaTable", menuName = "Undercover/Round/Quota Table")]
public class RoundQuotaTable : ScriptableObject
{
    [Tooltip("라운드 순서대로 나열한 할당량(목표 금액). 첫 항목 = 1라운드, 마지막 항목 = 그 이후 모든 라운드")]
    [SerializeField] private int[] m_quotas = { 30000, 45000, 60000 };

    /// <summary>표에 실제로 굴릴 행이 있는가 — 비어 있으면 호출부는 자기 인스펙터 값을 쓴다.</summary>
    public bool HasRows => m_quotas != null && m_quotas.Length > 0;

    /// <summary>
    /// N라운드의 할당량. 표가 비었거나 해당 값이 0 이하면 <paramref name="fallback"/>을 돌려준다.
    /// 표 끝을 넘는 라운드는 마지막 행으로 clamp 한다.
    /// </summary>
    public int GetQuota(int round, int fallback)
    {
        if (!HasRows) return fallback;

        int index = Mathf.Clamp(round - RoundProgress.k_firstRound, 0, m_quotas.Length - 1);
        int quota = m_quotas[index];
        return quota > 0 ? quota : fallback;
    }
}
