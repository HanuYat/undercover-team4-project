using UnityEngine;

/// <summary>
/// 조준·가시선 판정에서 무엇이 앞을 막고 있는지 정하는 공용 로직이다.
/// 테이저 사격, 진압봉 스윙, 상호작용 가시선이 모두 이 로직을 쓴다.
/// 설계 배경은 docs/853-aim-occlusion.md 참고.
/// </summary>
public static class AimOcclusion
{
    /// <summary>
    /// 후보 히트 중 교차점이 가장 가까운 것의 인덱스를 반환한다. 없으면 -1.
    /// excludedRoot 아래에 속한 히트(휘두른 본인, 대상 자신)는 제외한다.
    /// </summary>
    public static int FindNearest(
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
        int firstZeroIndex = -1; // distance<=0인 첫 히트 — 유효 히트가 없을 때의 대체용

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

            float distance = hits[i].distance;
            if (distance <= 0f)
            {
                if (firstZeroIndex < 0)
                {
                    firstZeroIndex = i;
                }
                continue;
            }

            if (distance >= nearestDistance)
            {
                continue;
            }

            nearestDistance = distance;
            nearestIndex = i;
        }

        return nearestIndex >= 0 ? nearestIndex : firstZeroIndex;
    }

    /// <summary>
    /// 원점에서 targetPoint 사이를 무언가 가로막는지 확인한다.
    /// targetRoot 아래에 속한 콜라이더(대상 자신)는 가림으로 치지 않는다.
    /// </summary>
    public static bool IsBlocked(
        Vector3 origin,
        Vector3 targetPoint,
        Transform targetRoot,
        LayerMask blockMask
    )
    {
        if (
            !Physics.Linecast(
                origin, targetPoint, out RaycastHit hit, blockMask, QueryTriggerInteraction.Ignore)
        )
        {
            return false;
        }

        return targetRoot == null || !hit.collider.transform.IsChildOf(targetRoot);
    }
}
