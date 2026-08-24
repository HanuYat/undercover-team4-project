using UnityEngine;

/// <summary>
/// 라운드별 세력 복수대 인원 표 (#721) — 라운드가 오를수록 몰려오는 조직원이 늘어나는 난이도 레버.
/// 조회 규약은 <see cref="RoundQuotaTable"/>(#377)·<see cref="RoundWeatherTable"/>(#700)과 같다 —
/// 첫 항목이 1라운드, 표 끝을 넘으면 마지막 행을 유지한다.
///
/// 수치는 밸런싱 보류 항목(GDD 12장)이라 코드에 박지 않고 데이터 에셋으로 뺀다(GDD 10-4).
/// <b>현장 인원은 보지 않는다</b> (2026-08-24 확정) — 표 하나로 끝나고 예측 가능하기 때문이고,
/// 그 대신 "인원이 적은 판에서 잠기지 않을 것"을 표 값 자체로 맞춰야 한다.
/// </summary>
[CreateAssetMenu(fileName = "FactionRevengeTable", menuName = "Undercover/Events/Faction Revenge Table")]
public class FactionRevengeTable : ScriptableObject
{
    [Tooltip("라운드 순서대로 나열한 복수대 인원. 첫 항목 = 1라운드, 마지막 항목 = 그 이후 모든 라운드")]
    [Min(1)]
    [SerializeField] private int[] m_memberCounts = { 2, 3, 4 };

    /// <summary>표에 실제로 굴릴 행이 있는가 — 비어 있으면 호출부는 자기 인스펙터 값을 쓴다.</summary>
    public bool HasRows => m_memberCounts != null && m_memberCounts.Length > 0;

    /// <summary>
    /// N라운드의 복수대 인원. 표가 비었거나 해당 값이 0 이하면 <paramref name="fallback"/>.
    /// 표 끝을 넘는 라운드는 마지막 행으로 clamp — 라운드가 이어져도 인원이 폭주하지 않는다.
    /// </summary>
    public int GetMemberCount(int round, int fallback)
    {
        if (!HasRows) return fallback;

        int index = Mathf.Clamp(round - RoundProgress.k_firstRound, 0, m_memberCounts.Length - 1);
        // 0 이하는 [Min(1)]로 막혀 있지만, 그 속성이 붙기 전에 저장된 에셋이 있을 수 있어 방어로 남긴다
        int count = m_memberCounts[index];
        return count > 0 ? count : fallback;
    }
}
