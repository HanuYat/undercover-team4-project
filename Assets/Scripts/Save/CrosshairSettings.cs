using System;

/// <summary>
/// 크로스헤어 커스터마이징 한 벌 (#945) — <see cref="CosmeticLoadout"/>·<see cref="CosmeticsSaveData"/>·
/// <see cref="CrosshairRenderer"/>가 이 값 하나로 통신한다. 로봇 색과 같은 계정 클라우드 경로를 탄다.
/// </summary>
[Serializable]
public class CrosshairSettings
{
    public ECrosshairShape Shape;

    /// <summary>색 팔레트(<see cref="PlayerColorPalette"/> 재사용) 인덱스 — 로봇 색과 같은 인덱스 방식.</summary>
    public int ColorIndex;

    /// <summary>선 길이 / 원 지름 기준(px). 슬라이더 범위는 <see cref="CrosshairRenderer"/>가 정한다.</summary>
    public float Size;

    /// <summary>선 굵기(px). 원 모양에는 적용되지 않는다(§4 참고 — 고정 두께 링 스프라이트 사용).</summary>
    public float Thickness;

    public static CrosshairSettings Default() =>
        new CrosshairSettings
        {
            Shape = ECrosshairShape.Cross,
            ColorIndex = 0,
            Size = 8f,
            Thickness = 2f,
        };
}
