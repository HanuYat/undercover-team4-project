using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

/// <summary>
/// CanvasGroup 알파 페이드. 타이틀의 커튼·배경 연출이 모두 이걸 쓴다.
///
/// <b>프레임 튐을 흡수하는 것이 이 클래스의 존재 이유다.</b> 씬을 막 로드한 프레임은 delta가
/// 크게 잡혀(로드 히치), 그대로 누적하면 0.5초 페이드가 한두 프레임에 끝나 페이드가 아예 안
/// 걸린 것처럼 보인다(실측: 게임 첫 실행 시 커튼이 툭 나타남). 그래서 두 가지를 한다 —
/// 시작 전 <see cref="k_settleFrames"/>만큼 흘려보내고, 한 프레임이 먹는 시간에 상한을 둔다.
/// <see cref="LoadingScreen"/>이 같은 이유로 프레임을 흘리는 것과 같은 방침이다.
///
/// timeScale이 0으로 잠겨도 돌아야 하므로 실시간(unscaled) 기준이다.
/// </summary>
public static class UIFade
{
    // 로드 직후의 큰 delta를 버리는 구간. 두 프레임이면 충분하다(실측).
    private const int k_settleFrames = 2;

    // 한 프레임이 먹을 수 있는 최대 시간. 중간에 히치가 나도 페이드가 건너뛰지 않게 한다 —
    // 느린 프레임에서는 실제 시간이 조금 늘지만, 연출이 보이지 않는 것보다 낫다.
    private const float k_maxStep = 1f / 30f;

    /// <summary><paramref name="group"/>의 알파를 옮긴다. null이거나 시간이 0이면 즉시 끝낸다.</summary>
    public static async UniTask ToAsync(
        CanvasGroup group,
        float from,
        float to,
        float seconds,
        CancellationToken token
    )
    {
        if (group == null)
            return;

        group.alpha = from;

        if (seconds <= 0f)
        {
            group.alpha = to;
            return;
        }

        await UniTask.DelayFrame(k_settleFrames, PlayerLoopTiming.Update, token);

        float elapsed = 0f;
        while (elapsed < seconds)
        {
            elapsed += Mathf.Min(Time.unscaledDeltaTime, k_maxStep);
            group.alpha = Mathf.Lerp(from, to, elapsed / seconds);
            await UniTask.Yield(PlayerLoopTiming.Update, token);
        }

        group.alpha = to;
    }
}
