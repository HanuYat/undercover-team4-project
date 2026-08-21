using UnityEngine;

/// <summary>
/// 날씨 실내 판정 (#227) — <b>그 자리의 하늘이 막혀 있는가</b>를 묻는 한 줄짜리 공용 규칙.
///
/// 쓰는 곳이 셋이다: 낙뢰 판정(<see cref="LightningEvent"/>)과 빙판 누적(<see cref="SnowEvent"/>)이
/// <b>지점 단위</b>로 묻고, 강수 표현은 <see cref="PrecipitationMask"/>가 <b>화면 칸마다</b> 묻는다 (#782).
/// 같은 질문에 다르게 답하면 "천장 아래인데 벼락을 맞는다"가 되므로 규칙을 복제하지 않고 이 함수를 공유한다.
///
/// 표현과 판정이 갈릴 때의 방향은 정해 뒀다 — 마스크가 "열림", 레이가 "막힘"이면 <b>눈은 보이지만
/// 벼락은 안 맞는다</b>. 그 반대(안 보이는데 맞는다)보다 낫다.
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
