using UnityEngine;

/// <summary>
/// 라운드 날씨 확률 표 (#700) — 라운드가 오를수록 맑음이 줄어드는 난이도 레버이자 맵별 날씨 성향.
/// 조회 규약은 <see cref="RoundQuotaTable"/>(#377)과 같다 — 첫 항목이 1라운드, 표 끝을 넘으면 마지막 행 유지.
/// 맵별 분리는 각 맵 씬의 매니저에 그 맵의 표를 꽂아서 한다 — 맵을 늘려도 코드는 그대로다.
/// </summary>
[CreateAssetMenu(
    fileName = "RoundWeatherTable",
    menuName = "Undercover/Events/Round Weather Table"
)]
public class RoundWeatherTable : ScriptableObject
{
    [Tooltip(
        "라운드 순서대로 나열한 맑음 확률(%). 첫 항목 = 1라운드, 마지막 항목 = 그 이후 모든 라운드.\n\n"
            + "나머지 확률을 아래 가중치대로 날씨 3종이 나눠 갖는다"
    )]
    [Range(0, 100)]
    [SerializeField]
    private int[] m_clearPercents = { 70, 50, 30, 20 };

    [System.Serializable]
    private class WeatherWeight
    {
        public WeatherKind kind;

        [Tooltip("맑음이 아닐 때 이 날씨가 뽑힐 상대 가중치 — 0이면 이 맵에서는 나오지 않는다")]
        [Min(0f)]
        public float weight = 1f;
    }

    [Tooltip(
        "날씨별 상대 가중치. 목록에 없는 날씨는 1(균등)로 친다 — 차등을 줄 것만 적으면 된다.\n\n"
            + "예: 실내 위주 맵에서 눈을 0으로 두면 그 맵에는 눈이 오지 않는다"
    )]
    [SerializeField]
    private WeatherWeight[] m_weights = new WeatherWeight[0];

    /// <summary>표에 굴릴 행이 있는가 — 비어 있으면 호출부는 자기 인스펙터 폴백을 쓴다.</summary>
    public bool HasRows => m_clearPercents != null && m_clearPercents.Length > 0;

    /// <summary>
    /// N라운드의 맑음 확률(%). 표가 비었으면 <paramref name="fallback"/>.
    /// 표 끝을 넘는 라운드는 마지막 행으로 clamp — 라운드가 이어져도 확률이 폭주하지 않는다.
    /// </summary>
    public int GetClearPercent(int round, int fallback)
    {
        if (!HasRows)
            return fallback;

        int index = Mathf.Clamp(
            round - RoundProgress.k_firstRound,
            0,
            m_clearPercents.Length - 1
        );
        return m_clearPercents[index];
    }

    /// <summary>
    /// 이 날씨의 상대 가중치 — 목록에 없으면 <b>1(균등)</b>이다.
    /// 전부 적지 않아도 되게 한 것이고, 막고 싶은 날씨만 0으로 적으면 된다.
    /// </summary>
    public float WeightOf(WeatherKind kind)
    {
        if (m_weights == null)
            return 1f;

        for (int i = 0; i < m_weights.Length; i++)
            if (m_weights[i] != null && m_weights[i].kind == kind)
                return Mathf.Max(0f, m_weights[i].weight);

        return 1f;
    }
}
