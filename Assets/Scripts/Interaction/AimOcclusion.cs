using UnityEngine;

/// <summary>
/// 조준·가시선 판정에서 "무엇이 앞을 막고 있는가"를 정하는 단일 규칙.
/// 테이저 사격(<c>Taser.EvaluateAim</c>)·진압봉 스윙(<c>Baton.EvaluateSwing</c>)·상호작용 가시선
/// (<c>PlayerInteractor.HasLineOfSight</c>) 셋이 모두 여기를 읽는다 — 기준을 바꾸면 셋이 함께 움직인다.
/// 셋이 갈라져 있으면 "테이저는 맞는데 E는 안 되는" 식으로 어긋난다.
///
/// 기준은 교차점 거리(<c>hit.distance</c>)가 아니라 <b>맞은 오브젝트의 Transform(피봇)까지 거리</b>다.
/// 맵의 충돌 껍질은 예외 없이 28삼각형 박스 프록시(<c>Models/Collision/Convex</c>)라 NPC가 서 있는
/// 공간까지 덮고 있어서, 교차점 기준으로는 코앞의 NPC보다 껍질 앞면이 먼저 맞아 오차단·빗나감이 된다.
///
/// <b>한계 — 피봇은 가림의 기하와 무관한 값이다.</b> 형상 중심이 아니라 아티스트가 둔 원점이고,
/// 이 프로젝트 에셋은 피봇↔형상중심이 평균 4.3m·최대 42.8m 떨어져 있다(콜라이더가 하나뿐인 얇은 벽도
/// 마찬가지 — <c>SM_Bld_Advanced_01</c>은 두께 0.71m·폭 5m에 피봇이 한쪽 끝). 벽 피봇이 대상보다
/// 멀기만 하면 그 벽은 무시되므로 관통이 가능하다. 근본 원인은 NavMesh가 렌더 메시로 구워져
/// (<c>NavMeshSurface.useGeometry = RenderMeshes</c>) 물리와 다른 지오메트리를 보는 것이고,
/// 그쪽을 맞추는 것이 이 규칙을 걷어낼 수 있는 유일한 길이다.
///
/// <b>부수효과 없는 순수 함수로 유지할 것</b> — 오너 크로스헤어 피드백이 매 프레임 호출한다 (#328).
/// 로그·상태 변경을 넣으면 조준만 해도 그게 매 프레임 실행된다.
/// </summary>
public static class AimOcclusion
{
    /// <summary>
    /// 정렬·판정 키 — 맞은 오브젝트의 피봇까지 거리.
    /// 콜라이더가 사라진 히트는 맨 뒤로 보낸다(파괴된 오브젝트를 고르지 않도록).
    /// </summary>
    public static float PivotDistance(Vector3 origin, RaycastHit hit) =>
        hit.collider == null
            ? float.MaxValue
            : Vector3.Distance(origin, hit.collider.transform.position);

    /// <summary>
    /// 후보 히트 중 피봇이 가장 가까운 것의 인덱스 — 후보가 없으면 -1.
    /// <paramref name="excludedRoot"/> 계층에 속한 히트는 건너뛴다 — 휘두른 본인과 그가 들고 있는 것들
    /// (SphereCast는 원점에 겹친 콜라이더를 distance 0으로 되돌려주고, 소지자 피봇은 원점 바로 아래라
    /// 늘 최근접이 된다), 또는 가시선 판정에서 대상 자신의 콜라이더.
    /// </summary>
    public static int FindNearestByPivot(
        Vector3 origin,
        RaycastHit[] hits,
        int count,
        Transform excludedRoot
    )
    {
        if (hits == null)
        {
            return -1;
        }

        int limit = Mathf.Min(count, hits.Length); // 버퍼 길이를 넘는 count가 와도 안전하게
        int nearestIndex = -1;
        float nearestDistance = float.PositiveInfinity;

        for (int i = 0; i < limit; i++)
        {
            Collider collider = hits[i].collider;
            if (collider == null)
            {
                continue;
            }

            // IsChildOf는 자기 자신도 true — 루트를 넘기면 그 계층 전체가 제외된다
            if (excludedRoot != null && collider.transform.IsChildOf(excludedRoot))
            {
                continue;
            }

            float distance = PivotDistance(origin, hits[i]);
            if (distance >= nearestDistance)
            {
                continue;
            }

            nearestDistance = distance;
            nearestIndex = i;
        }

        return nearestIndex;
    }
}
