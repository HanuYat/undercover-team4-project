using UnityEngine;

/// <summary>
/// 납치 기습 판정 — 표적의 뒤로 돌아 접근하고, 뒤를 잡았을 때만 포획이 성립하게 한다. (#775)
///
/// 각도·지점 계산만 든다. <b>FSM도 상태도 모른다</b> — <see cref="ChaseSteering"/>과 같은 결이다.
/// </summary>
public class ChaseAmbush
{
    private readonly NpcChaseConfig m_config;

    public ChaseAmbush(NpcChaseConfig config)
    {
        m_config = config;
    }

    /// <summary>
    /// 표적의 후방 부채꼴 안인가 — <b>몸통 정면 기준</b>, XZ 평면. (#775)
    /// 1인칭이라 몸통 yaw가 곧 시선이다(<see cref="PlayerLook"/>이 좌우를 transform에 돌린다) —
    /// 카메라를 따로 볼 필요도, 그것을 동기화할 필요도 없다.
    /// </summary>
    public bool IsBehind(Transform target, Vector3 from)
    {
        Vector3 toNpc = Flat(from - target.position);
        if (toNpc.sqrMagnitude < 0.0001f)
            return true; // 겹쳐 섰다 — 각도를 못 재는 거리면 잡은 것으로 본다

        return Vector3.Angle(Back(target), toNpc) <= m_config.AmbushRearHalfAngle;
    }

    /// <summary>
    /// 표적이 지금 <b>나를 보고 있는가</b> — 정면 반각 안인지. (#775)
    /// 다가갈지 말지를 가르는 값이다. 포획 판정(<see cref="IsBehind"/>)보다 넓어서,
    /// 시선만 벗어나면 옆구리에서 붙을 수 있다.
    /// </summary>
    public bool IsWatched(Transform target, Vector3 from)
    {
        Vector3 toNpc = Flat(from - target.position);
        if (toNpc.sqrMagnitude < 0.0001f)
            return false; // 겹쳐 섰다 — 이 거리면 이미 늦었다

        Vector3 forward = Flat(target.forward);
        if (forward.sqrMagnitude < 0.0001f)
            return false;

        return Vector3.Angle(forward, toNpc) <= m_config.AmbushViewHalfAngle;
    }

    /// <summary>
    /// 접근 목적지 — <b>보이지 않을 때만 다가간다</b>. 보이는 동안에는 반대쪽으로 걸어간다. (#775)
    ///
    /// 시간이 아니라 시선으로 재시도를 가른다 — 타이머로 두면 표적이 빤히 보고 있는데도 때가 되면
    /// 다시 다가가서 눈앞에서 앞뒤로 왕복한다. 보이는 동안 <b>제 갈 길 가는 시민</b>으로 보이는 것이
    /// 이 이벤트의 전제(겉모습으로 구분되지 않는다)를 지키는 유일한 방법이다.
    /// </summary>
    public Vector3 ApproachPoint(Transform target, Vector3 from)
    {
        Vector3 back = Back(target);
        Vector3 toNpc = Flat(from - target.position);
        Vector3 dir = toNpc.sqrMagnitude > 0.0001f ? toNpc.normalized : back;

        // 2인조가 겹치지 않게 각자 지금 서 있는 쪽을 맡는다 — 상태를 들지 않으므로 흔들리지 않는다
        Vector3 side = Vector3.Cross(Vector3.up, back);
        float sign = Vector3.Dot(dir, side) >= 0f ? 1f : -1f;

        // 안 보고 있다 — 조용히 뒤로 파고든다
        if (!IsWatched(target, from))
        {
            return target.position
                   + back * m_config.AmbushApproachDistance
                   + side * (sign * m_config.AmbushSideSpread);
        }

        // 보고 있다 — 그냥 반대쪽으로 걸어간다. 시선이 벗어나면 그때 돌아선다
        return from + dir * m_config.AmbushWalkAwayDistance;
    }

    // 표적의 등 방향 — 몸통 정면의 반대, XZ 평면
    private static Vector3 Back(Transform target)
    {
        Vector3 back = Flat(-target.forward);
        return back.sqrMagnitude > 0.0001f ? back.normalized : Vector3.forward;
    }

    private static Vector3 Flat(Vector3 v)
    {
        v.y = 0f;
        return v;
    }
}
