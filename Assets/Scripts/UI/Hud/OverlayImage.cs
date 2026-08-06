using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 화면 전체를 덮는 HUD 오버레이 이미지의 공통 처리. (#478)
/// <see cref="DamageVignetteUI"/>(피격 비네트·방향 아크·저체력 글리치)와
/// <see cref="TaserShockUI"/>(감전 지직)가 함께 쓴다.
///
/// <b>플리커 리듬까지 합치지는 않았다.</b> 두 연출의 성격이 정확히 그 리듬으로 갈리기 때문이다 —
/// 저체력 글리치는 0.5~2초 간격으로 성기게 깜빡여 "몸이 상했다"를 알리고, 감전은 0.02~0.055초로
/// 떨려 "지금 전류가 흐른다"를 알린다. 하나로 묶으면 그 차이가 파라미터 뒤로 숨는다.
/// </summary>
public static class OverlayImage
{
    /// <summary>
    /// 알파를 적용한다. <b>0이면 오브젝트째 비활성화</b>한다 — 전체 화면을 덮는 이미지라
    /// 투명해도 매 프레임 오버드로가 남기 때문이다.
    /// </summary>
    public static void SetAlpha(Image image, float alpha)
    {
        if (image == null)
            return;

        bool visible = alpha > 0.001f;
        if (image.gameObject.activeSelf != visible)
            image.gameObject.SetActive(visible);

        if (!visible)
            return;

        Color color = image.color;
        color.a = alpha;
        image.color = color;
    }
}
