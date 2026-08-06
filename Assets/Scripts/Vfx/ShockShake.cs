using UnityEngine;

/// <summary>
/// 감전 경련의 흔들림 수식 — 카메라(<see cref="PlayerLook"/>)와 1인칭 팔(<see cref="PlayerHandView"/>)이
/// <b>같은 파형을 공유한다</b>. (#477)
///
/// 합쳐 둔 이유는 <see cref="k_frequency"/> 때문이다. 손과 시야가 다른 주파수로 떨면 두 개의 진동이
/// 서로 미끄러져 경련이 아니라 고장난 화면처럼 보인다. 상수를 두 파일에 따로 두면 한쪽만 고쳤을 때
/// 그렇게 조용히 어긋난다 — 그래서 진폭만 호출부가 정하고 <b>주파수와 파형은 여기 하나로</b> 둔다.
///
/// Perlin이 아니라 사인인 이유: 경련은 '떨림'이라 불규칙한 표류보다 빠른 진동이 맞고, 값이 0을
/// 중심으로 정확히 오가야 카메라나 손이 한쪽으로 밀려나지 않는다.
/// </summary>
public static class ShockShake
{
    /// <summary>진동수(Hz). 카메라와 손이 반드시 같은 값을 써야 한다.</summary>
    public const float k_frequency = 33f;

    /// <summary>
    /// 지금 시각의 흔들림 포즈를 낸다 — 상태가 없는 순수 함수다.
    /// </summary>
    /// <param name="intensity">0~1. 0이면 결과도 0이다.</param>
    /// <param name="degrees">최대 회전 진폭(도).</param>
    /// <param name="offset">최대 위치 진폭(m).</param>
    /// <param name="euler">축별 회전(도).</param>
    /// <param name="position">위치 오프셋(m).</param>
    public static void Evaluate(
        float intensity,
        float degrees,
        float offset,
        out Vector3 euler,
        out Vector3 position
    )
    {
        if (intensity <= 0.001f)
        {
            euler = Vector3.zero;
            position = Vector3.zero;
            return;
        }

        // 축마다 위상과 주기를 어긋나게 줘 규칙적인 진자 운동으로 보이지 않게 한다.
        float t = Time.time * k_frequency;

        euler = new Vector3(
            Mathf.Sin(t) * degrees * intensity,
            Mathf.Sin(t * 1.37f + 1.1f) * degrees * intensity,
            Mathf.Sin(t * 0.83f + 2.3f) * degrees * intensity
        );
        position = new Vector3(
            Mathf.Sin(t * 1.11f + 0.7f) * offset * intensity,
            Mathf.Sin(t * 1.53f) * offset * intensity,
            0f
        );
    }
}
