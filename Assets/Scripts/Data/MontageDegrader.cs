using UnityEngine;

/// <summary>
/// 몽타주 화질 저하 계산 (#724). 런타임 표시와 에디터 굽기 툴의 측정이 같은 함수를 써야
/// "최저 화질에서도 구분 가능"의 로그 측정이 실제 표시와 어긋나지 않는다.
///
/// 알파는 그대로 두고 RGB만 블록 평균 + fade한다 — 미공개 축의 반투명 표시(m_unknownTint)와
/// 형태 미상 머리의 체크무늬 농도가 화질 저하로 사라지면 안 된다.
/// </summary>
public static class MontageDegrader
{
    public static Color[] Apply(Color[] src, int srcSize, in MontageClarityStep step)
    {
        // PixelSize는 "결과가 몇 픽셀처럼 보이는가"다 — 128이면 안 뭉개고, 16이면 128/16=8픽셀짜리
        // 블록으로 뭉갠다. 블록 크기 자체가 아니라 목표 겉보기 해상도라서 srcSize로 나눠 뒤집는다.
        int targetSize = Mathf.Clamp(step.PixelSize, 1, srcSize);
        int blockSize = Mathf.Max(1, Mathf.RoundToInt((float)srcSize / targetSize));
        var result = new Color[src.Length];

        for (int by = 0; by < srcSize; by += blockSize)
        {
            int blockH = Mathf.Min(blockSize, srcSize - by);
            for (int bx = 0; bx < srcSize; bx += blockSize)
            {
                int blockW = Mathf.Min(blockSize, srcSize - bx);
                Color avg = BlockAverage(src, srcSize, bx, by, blockW, blockH);
                Color faded = Fade(avg, step.Fade);

                for (int y = by; y < by + blockH; y++)
                    for (int x = bx; x < bx + blockW; x++)
                        result[y * srcSize + x] = faded;
            }
        }

        return result;
    }

    private static Color BlockAverage(Color[] src, int srcSize, int startX, int startY, int width, int height)
    {
        float r = 0f, g = 0f, b = 0f, a = 0f;
        int count = width * height;

        for (int y = startY; y < startY + height; y++)
        {
            for (int x = startX; x < startX + width; x++)
            {
                Color c = src[y * srcSize + x];
                r += c.r;
                g += c.g;
                b += c.b;
                a += c.a;
            }
        }

        return new Color(r / count, g / count, b / count, a / count);
    }

    private static Color Fade(Color c, float amount)
    {
        if (amount <= 0f)
            return c;

        const float k_gray = 0.5f;
        return new Color(
            Mathf.Lerp(c.r, k_gray, amount),
            Mathf.Lerp(c.g, k_gray, amount),
            Mathf.Lerp(c.b, k_gray, amount),
            c.a
        );
    }
}
