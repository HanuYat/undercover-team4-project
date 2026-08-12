using UnityEngine;

/// <summary>
/// 날씨 실내 판정 (#227) — <b>그 자리의 하늘이 막혀 있는가</b>를 묻는 한 줄짜리 공용 규칙.
///
/// 쓰는 곳이 둘이고 계층이 다르다: 강수 표현(<see cref="WeatherSkyRig"/>)은 지붕 아래에서 방출을 닫고,
/// 낙뢰 판정(<see cref="LightningEvent"/>)은 지붕 아래 플레이어를 대상에서 뺀다. 같은 질문에 두 곳이
/// 다르게 답하면 "천장 아래인데 벼락을 맞는다"거나 "비는 그쳤는데 벼락은 떨어진다"가 된다.
///
/// 파티클 충돌이나 실내 트리거 볼륨 대신 <b>레이 하나</b>로 푸는 이유: 맵마다 실내를 표시해 두는 작업이
/// 없고(건물은 그냥 Default 레이어 콜라이더다), 부르는 쪽이 프레임당 한 번씩만 물어보므로 비용이 없다.
/// </summary>
public static class WeatherShelter
{
    /// <summary>
    /// 이 지점 위가 막혀 있는가 — 막혔으면 실내(또는 처마 밑)로 본다.
    /// </summary>
    /// <param name="position">발밑이 아니라 <b>몸이 있는 높이</b>를 넘길 것 — 바닥에서 쏘면 자기가 선
    /// 바닥에 걸리는 맵이 있다.</param>
    /// <param name="blockMask">하늘을 막는 것으로 칠 레이어 — 건물은 <c>Default</c>다.</param>
    /// <param name="probeHeight">이 거리(m) 안에 뭔가 있으면 막힌 것. 0 이하면 판정하지 않는다(늘 노출).</param>
    public static bool IsSheltered(Vector3 position, LayerMask blockMask, float probeHeight)
    {
        if (probeHeight <= 0f)
            return false;

        return Physics.Raycast(
            position,
            Vector3.up,
            probeHeight,
            blockMask,
            QueryTriggerInteraction.Ignore
        );
    }
}
