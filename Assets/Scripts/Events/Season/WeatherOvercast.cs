using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

/// <summary>
/// 먹구름 표현 — 씬 태양을 어둡게 하는 단일 창구. (#227)
///
/// <b>구름 파티클을 대신한다.</b> 하늘을 파티클로 덮으려면 시스템이 수십 개 필요해 프레임이 크게
/// 떨어졌다(9×9=81개). 먹구름의 값은 "하늘이 무겁다"는 인상이고, 그건 밝기 하나로 거의 다 나온다.
///
/// <b>참조 계수로 관리한다.</b> 눈과 비가 동시에 올 수 있는데 각 뷰가 제 밝기를 직접 밀면 나중에
/// 끝난 쪽이 남은 쪽의 어둠을 걷어 버린다. 요청을 세고 <b>가장 어두운 값</b>을 적용하며, 마지막 요청이
/// 빠질 때만 원래 밝기로 돌린다.
///
/// 대상은 <see cref="RenderSettings.sun"/>(Lighting 창의 Sun Source)이다 — 씬마다 다른 라이트를
/// 프리팹이 직렬화로 물 수 없어 런타임에 찾는다. 태양이 없는 씬에서는 조용히 아무 일도 하지 않는다.
///
/// 페이드는 UniTask가 PlayerLoop에서 돌린다 — 씬을 넘겨도 끊기지 않는다. 예전에는 코루틴을
/// 태우려고 <c>DontDestroyOnLoad</c> 러너 오브젝트를 만들었는데, 이제 그릇이 필요 없다.
///
/// 표현 계층이라 각 피어에서 따로 돈다 — 복제하지 않는다.
/// 낙뢰 섬광은 이 값을 건드리지 않고 <see cref="LightningView"/>가 직접 쥔다(짧은 순간의 덮어쓰기라
/// 계수에 넣으면 오히려 어긋난다) — 섬광이 끝나면 이 클래스의 현재 목표로 되돌린다.
/// </summary>
public static class WeatherOvercast
{
    // 살아 있는 요청 수와 그중 가장 어두운 배율. 정적이지만 매니저가 아니라 밝기 하나를 조정하는
    // 표현 상태다 — App 파사드(R1) 대상이 아니고 씬 오브젝트도 아니다.
    private static int s_requests;
    private static float s_scale = 1f;

    private static Light s_sun;
    private static float s_baseIntensity;
    private static bool s_hasBase;

    private static CancellationTokenSource s_fadeCts; // 새 요청이 돌던 페이드를 끊는다

    /// <summary>어둡게 하기를 요청한다 — 같은 날씨가 두 번 켜지지 않는 한 뷰당 한 번.</summary>
    public static void Push(float intensityScale, float fadeSeconds)
    {
        s_requests++;
        s_scale = s_requests == 1 ? intensityScale : Mathf.Min(s_scale, intensityScale);
        Apply(fadeSeconds);
    }

    /// <summary>요청을 뺀다 — 마지막이 빠지면 원래 밝기로 돌아간다.</summary>
    public static void Pop(float fadeSeconds)
    {
        s_requests = Mathf.Max(0, s_requests - 1);
        if (s_requests == 0)
            s_scale = 1f;

        Apply(fadeSeconds);
    }

    /// <summary>지금 적용해야 할 밝기 — 낙뢰 섬광이 끝난 뒤 되돌아갈 목표값이다.</summary>
    public static float CurrentTarget => s_hasBase ? s_baseIntensity * s_scale : 0f;

    /// <summary>기준 밝기를 잡아 뒀는가 — 태양이 없는 씬에서는 false.</summary>
    public static bool HasSun => s_hasBase;

    private static void Apply(float fadeSeconds)
    {
        if (!ResolveSun())
            return;

        s_fadeCts?.Cancel();
        s_fadeCts?.Dispose();
        s_fadeCts = new CancellationTokenSource();

        FadeToAsync(s_baseIntensity * s_scale, fadeSeconds, s_fadeCts.Token).Forget();
    }

    private static bool ResolveSun()
    {
        if (s_sun == null || !s_sun.isActiveAndEnabled)
        {
            s_sun = RenderSettings.sun;
            s_hasBase = false; // 태양이 바뀌었으면 기준값도 다시 잡는다
        }

        if (s_sun == null)
            return false;

        if (!s_hasBase)
        {
            s_baseIntensity = s_sun.intensity;
            s_hasBase = true;
        }

        return true;
    }

    // 끊긴 페이드는 목표값을 찍지 않고 그 자리에서 멈춘다 — 뒤이은 요청이 지금 밝기에서 이어 간다.
    private static async UniTaskVoid FadeToAsync(float target, float seconds, CancellationToken token)
    {
        float from = s_sun != null ? s_sun.intensity : target;

        for (float t = 0f; t < seconds && s_sun != null; t += Time.deltaTime)
        {
            s_sun.intensity = Mathf.Lerp(from, target, t / seconds);
            await UniTask.NextFrame(token);
        }

        if (s_sun != null)
            s_sun.intensity = target;
    }
}
