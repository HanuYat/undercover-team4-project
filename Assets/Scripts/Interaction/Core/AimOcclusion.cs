using UnityEngine;

/// <summary>
/// 조준·가시선 판정에서 무엇이 앞을 막고 있는지 정하는 공용 로직이다.
/// 테이저 사격, 진압봉 스윙, 상호작용 가시선이 모두 이 로직을 쓴다.
/// 설계 배경은 docs/853-aim-occlusion.md 참고.
/// </summary>
public static class AimOcclusion
{
    // 환경 가림 검사용 공유 버퍼 — 메인 스레드 물리 쿼리에서만 쓴다. 넘치면 남은 히트를 못 보고
    // "안 막혔다"로 기운다 — 폭발이 조용히 사라지는 것보다 피해가 들어가는 쪽이 낫다.
    private static readonly RaycastHit[] s_blockHits = new RaycastHit[32];

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
    /// 원점에서 targetPoint 사이를 <b>환경이</b> 가로막는지 확인한다 — 사람은 가림물로 치지 않는다.
    /// 폭발처럼 "벽 뒤면 안 맞는다"를 판정하는 쪽이 쓴다. 근거는 docs/853-aim-occlusion.md §7 (#947).
    ///
    /// 얇은 선 대신 <paramref name="probeRadius"/>의 구를 쓸어 난간·소품 틈으로 새는 오판을 줄이고,
    /// <paramref name="ignoredRoot"/>(대개 폭심 자신)와 사람 계층은 후보에서 뺀다.
    /// </summary>
    public static bool IsEnvironmentBlocked(
        Vector3 origin,
        Vector3 targetPoint,
        LayerMask blockMask,
        float probeRadius,
        Transform ignoredRoot
    )
    {
        Vector3 delta = targetPoint - origin;
        // 대상 바로 뒤의 벽까지 집어내지 않도록 구 반지름만큼 앞에서 끊는다
        float distance = delta.magnitude - probeRadius;
        if (distance <= 0f)
        {
            return false; // 코앞 — 사이에 무언가 낄 공간이 없다
        }

        int count = Physics.SphereCastNonAlloc(
            origin,
            probeRadius,
            delta.normalized,
            s_blockHits,
            distance,
            blockMask,
            QueryTriggerInteraction.Ignore
        );

        for (int i = 0; i < count; i++)
        {
            Collider collider = s_blockHits[i].collider;
            if (collider == null)
            {
                continue;
            }

            // distance 0은 "시작 구가 이미 겹쳐 있다"는 뜻이지 사이를 막았다는 뜻이 아니다 (§5.5)
            if (s_blockHits[i].distance <= 0f)
            {
                continue;
            }

            if (ignoredRoot != null && collider.transform.IsChildOf(ignoredRoot))
            {
                continue;
            }

            if (IsCharacter(collider))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    // 사람인가 — 플레이어는 CharacterController, NPC는 NpcController가 루트에 있다.
    // 둘 다 Default 레이어에 걸릴 수 있어 레이어 마스크로는 벽과 갈라낼 수 없다.
    private static bool IsCharacter(Collider collider) =>
        collider.GetComponentInParent<CharacterController>() != null
        || collider.GetComponentInParent<NpcController>() != null;

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
