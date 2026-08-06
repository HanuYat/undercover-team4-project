using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// 한 지점에 여러 NPC가 모여 설 때의 자리 배정 — 원한 구역(#277)이 쓴다.
///
/// <b>유치장은 더 이상 쓰지 않는다 (#462)</b> — 계산된 자리가 인원이 늘수록 전방위로 퍼져 일부가 문 앞
/// 통로에 떨어졌고, 그 캡슐이 NPC의 진입과 플레이어의 탈출을 막았다. 좁고 출입구가 하나인 방에는 계산
/// 배치가 맞지 않아 손으로 배치한 지점 목록으로 바꿨다(JailZone.ReservePlacement). 원한 구역은 넓은 야외라
/// 같은 문제가 없어 그대로 둔다.
///
/// 원래 겹침은 NavMesh 로컬 회피가 흩어 줬다(NpcDetainedState의 옛 주석).
/// 판정 후 이송 중에는 그 회피를 끄기 때문에(플레이어가 몸으로 길을 막는 것을 없애려고 —
/// 근거는 <see cref="NpcJailedState"/>.Enter 주석) 흩어 줄 주체가 사라졌다. 대신 <b>보내는 쪽</b>이
/// 순번대로 자리를 나눠 준다. 회피에 맡기던 때보다 결정적이다 — 인원이 같으면 항상 같은 대형이 된다.
///
/// 배치는 해바라기(황금각) 꼴이다: 자리 수를 미리 몰라도 순번만으로 고르게 퍼지고,
/// 0번은 반지름이 0이라 <b>혼자일 때는 지정된 지점에 정확히</b> 선다(종전 동작 그대로).
/// </summary>
public static class GatherSlot
{
    // 황금각(rad) — 연속한 순번끼리 가장 덜 겹치는 각도. 해바라기 씨앗 배치와 같은 값이다.
    private const float k_goldenAngle = 2.39996323f;

    /// <summary>
    /// <paramref name="slot"/>번째가 설 자리의 기준점 대비 오프셋(수평).
    /// <paramref name="spacing"/>은 이웃한 자리 사이의 최소 간격(m) — NPC 캡슐 지름(0.8m) 이상으로 줄 것.
    /// </summary>
    public static Vector3 Offset(int slot, float spacing)
    {
        if (slot <= 0)
            return Vector3.zero;

        // 반지름을 √순번으로 키우면 자리 밀도가 일정해진다(면적이 순번에 비례) — 인원이 늘어도
        // 바깥으로 무한히 퍼지지 않고, 안쪽이 비지도 않는다.
        float radius = spacing * Mathf.Sqrt(slot);
        float angle = slot * k_goldenAngle;
        return new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
    }

    /// <summary>
    /// 배정된 자리를 실제로 걸어갈 수 있는 지점으로 확정한다 — 오프셋을 얹은 자리를 NavMesh에 스냅한다.
    /// <paramref name="areaMask"/>로 스냅 범위를 제한할 수 있다(유치장은 Jail 영역만 — 오프셋이 창살
    /// 밖 바닥에 걸리면 수감자가 감옥 밖에 서게 된다). 스냅에 실패하면 기준점을 그대로 돌려준다:
    /// 겹쳐 서는 것이 구역 밖으로 나가는 것보다 낫다.
    /// </summary>
    public static Vector3 Resolve(Vector3 anchor, Vector3 offset, int areaMask, float snapRadius)
    {
        // 0번 자리는 스냅도 건너뛴다 — 혼자 걸어가는 흔한 경로가 종전과 완전히 같은 목적지를 쓴다
        if (offset == Vector3.zero)
            return anchor;

        return NavMesh.SamplePosition(anchor + offset, out NavMeshHit hit, snapRadius, areaMask)
            ? hit.position
            : anchor;
    }
}
