using UnityEngine;

/// <summary>
/// UI 공용 색 팔레트 (#951) — 의미 단위(성공/실패/중립/주의)로 색을 한 곳에 정의한다.
/// 같은 톤이 스크립트마다 따로 박혀 있어 하나를 바꾸면 나머지가 안 따라오던 문제를 없앤다
/// (#943에서 <see cref="VerdictBanner"/>의 초록을 바꾸고 <see cref="ArrestNoticeBroadcaster"/>를
/// 손으로 맞춰야 했던 사례). 설계 정본: GDD 10-4 — 데이터는 ScriptableObject.
///
/// <b>알파는 담지 않는다</b> — 같은 색을 배너는 채움으로, 토스트는 배경으로 쓰는 등 투명도는
/// 자리마다 다르다. 쓰는 쪽이 <see cref="WithAlpha"/>로 자기 값을 얹는다.
/// </summary>
[CreateAssetMenu(fileName = "UiColors", menuName = "Scriptable Objects/UiColorPalette")]
public class UiColorPalette : ScriptableObject
{
    [Tooltip("성공·달성 — 진범 검거, 목표 금액 달성 등")]
    [SerializeField] private Color m_positive = new Color(0.290f, 0.871f, 0.502f);

    [Tooltip("실패·오류 — 오검거 등")]
    [SerializeField] private Color m_negative = new Color(0.863f, 0.149f, 0.149f);

    [Tooltip("중립 — 성패로 가르지 않는 알림. 경범죄 등")]
    [SerializeField] private Color m_neutral = new Color(0.612f, 0.639f, 0.686f);

    [Tooltip("주의 — 조건 불충족처럼 실패는 아니지만 짚어야 하는 경우")]
    [SerializeField] private Color m_caution = new Color(0.984f, 0.749f, 0.141f);

    public Color Positive => m_positive;
    public Color Negative => m_negative;
    public Color Neutral => m_neutral;
    public Color Caution => m_caution;

    /// <summary>알파만 갈아 끼운 색을 돌려준다 — 팔레트는 RGB만 정하고 투명도는 쓰는 쪽 몫이다.</summary>
    public static Color WithAlpha(Color color, float alpha)
    {
        return new Color(color.r, color.g, color.b, alpha);
    }
}
