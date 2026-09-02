using UnityEngine;

/// <summary>
/// 라운드별 세력 복수대 인원 표 (#721). 조회 규약은 <see cref="RoundQuotaTable"/>과 같다 —
/// 첫 항목이 1라운드, 표 끝을 넘으면 마지막 행을 유지한다. 수치는 보류 항목이라 에셋으로 뺀다 (GDD 12장).
/// </summary>
[CreateAssetMenu(fileName = "FactionRevengeTable", menuName = "Undercover/Events/Faction Revenge Table")]
public class FactionRevengeTable : ScriptableObject
{
    [Tooltip("라운드 순서대로 나열한 복수대 인원. 첫 항목 = 1라운드, 마지막 항목 = 그 이후 모든 라운드")]
    [Min(1)]
    [SerializeField] private int[] m_memberCounts = { 2, 3, 4 };

    /// <summary>표에 굴릴 행이 있는가 — 비어 있으면 호출부는 자기 인스펙터 값을 쓴다.</summary>
    public bool HasRows => m_memberCounts != null && m_memberCounts.Length > 0;

    /// <summary>N라운드의 복수대 인원. 표가 비었거나 값이 0 이하면 <paramref name="fallback"/>.</summary>
    public int GetMemberCount(int round, int fallback)
    {
        if (!HasRows) return fallback;

        int index = Mathf.Clamp(round - RoundProgress.k_firstRound, 0, m_memberCounts.Length - 1);
        int count = m_memberCounts[index];
        return count > 0 ? count : fallback;
    }
}
