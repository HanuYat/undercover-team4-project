using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 크로스헤어 시각 요소 참조 묶음 — 실제 게임 크로스헤어(<see cref="CrosshairUI"/>)와 설정 패널
/// 미리보기(<see cref="CrosshairPreviewView"/>)가 같은 계층 구조를 각자 인스턴스로 들고 이 구조체에 담아 넘긴다.
/// </summary>
public struct CrosshairVisualRefs
{
    public RectTransform Up;
    public RectTransform Down;
    public RectTransform Left;
    public RectTransform Right;
    public RectTransform Dot;
    public RectTransform CircleRing;
    public Image CircleImage;
}

/// <summary>
/// 크로스헤어 모양·크기·굵기·색 계산을 한 곳에 모은다 (#945). 정적 순수 함수라 게임 크로스헤어와
/// 설정 패널 미리보기가 완전히 같은 그림을 그린다 — 둘이 따로 계산하면 미리보기와 실제가 어긋난다.
/// </summary>
public static class CrosshairRenderer
{
    public const float k_minSize = 4f;
    public const float k_maxSize = 24f;
    public const float k_minThickness = 1f;
    public const float k_maxThickness = 6f;

    // 중심에서 선분이 시작되는 간격(px) — 간격 슬라이더는 이번 범위 밖(설계 문서 §6)이라 고정값.
    private const float k_lineGap = 4f;

    /// <summary>모양·크기·굵기를 반영해 각 조각의 표시 여부·RectTransform 크기를 다시 잡는다.</summary>
    public static void ApplyShape(CrosshairVisualRefs refs, CrosshairSettings settings)
    {
        float size = Mathf.Clamp(settings.Size, k_minSize, k_maxSize);
        float thickness = Mathf.Clamp(settings.Thickness, k_minThickness, k_maxThickness);

        bool showLines =
            settings.Shape == ECrosshairShape.Cross || settings.Shape == ECrosshairShape.CrossDot;
        bool showDot =
            settings.Shape == ECrosshairShape.Dot || settings.Shape == ECrosshairShape.CrossDot;
        bool showCircle = settings.Shape == ECrosshairShape.Circle;

        // vertical=세로선(길이가 y축)인가, direction=중심에서 어느 쪽으로 밀어낼지(+1/-1).
        // 회전값으로 방향을 추측하지 않는다 — Task 7에서 만드는 네 오브젝트는 전부 회전 0인
        // 평범한 RectTransform이라, 어느 쪽 선인지는 여기서 명시적으로 정해 준다.
        SetLine(refs.Up, showLines, vertical: true, direction: 1f, size, thickness, k_lineGap);
        SetLine(refs.Down, showLines, vertical: true, direction: -1f, size, thickness, k_lineGap);
        SetLine(refs.Left, showLines, vertical: false, direction: -1f, size, thickness, k_lineGap);
        SetLine(refs.Right, showLines, vertical: false, direction: 1f, size, thickness, k_lineGap);

        if (refs.Dot != null)
        {
            refs.Dot.gameObject.SetActive(showDot);
            if (showDot)
                refs.Dot.sizeDelta = new Vector2(thickness * 2f, thickness * 2f);
        }

        if (refs.CircleRing != null)
        {
            refs.CircleRing.gameObject.SetActive(showCircle);
            if (showCircle)
                refs.CircleRing.sizeDelta = new Vector2(size * 2f, size * 2f);
        }
    }

    // 선 하나 — vertical이면 sizeDelta.y가 길이(세로선), 아니면 sizeDelta.x가 길이(가로선).
    // direction(+1/-1)이 중심에서 어느 쪽으로 밀어낼지를 정한다. 둘 다 호출부(ApplyShape)가 명시한다.
    private static void SetLine(
        RectTransform line,
        bool visible,
        bool vertical,
        float direction,
        float size,
        float thickness,
        float gap
    )
    {
        if (line == null)
            return;

        line.gameObject.SetActive(visible);
        if (!visible)
            return;

        line.sizeDelta = vertical ? new Vector2(thickness, size) : new Vector2(size, thickness);

        float offset = gap + size * 0.5f;
        line.anchoredPosition = vertical
            ? new Vector2(0f, direction * offset)
            : new Vector2(direction * offset, 0f);
    }

    /// <summary>
    /// 지금 보이는 조각(선/점 또는 원) 전부에 색을 칠한다. <paramref name="colorOverride"/>가 있으면
    /// 그 색(상호작용/무기 조준 등 기능 색)을 쓰고, 없으면 설정의 <see cref="CrosshairSettings.ColorIndex"/>를
    /// 팔레트에서 찾아 쓴다 — 기본(중립) 상태에서만 커스텀 색이 보이는 이유가 이 분기다 (#945, GDD 568행).
    /// </summary>
    public static void ApplyColor(
        CrosshairVisualRefs refs,
        CrosshairSettings settings,
        Color? colorOverride,
        PlayerColorPalette palette
    )
    {
        Color color =
            colorOverride ?? (palette != null ? palette.Get(settings.ColorIndex) : Color.white);

        SetGraphicColor(refs.Up, color);
        SetGraphicColor(refs.Down, color);
        SetGraphicColor(refs.Left, color);
        SetGraphicColor(refs.Right, color);
        SetGraphicColor(refs.Dot, color);

        if (refs.CircleImage != null)
            refs.CircleImage.color = color;
    }

    private static void SetGraphicColor(RectTransform target, Color color)
    {
        if (target == null)
            return;

        Graphic graphic = target.GetComponent<Graphic>();
        if (graphic != null)
            graphic.color = color;
    }
}
