using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// 다음 이동 목적지 추첨 — 무작위 방향으로 후보를 뽑아 NavMesh 위로 끌어당기고,
/// <b>끊기지 않는 경로</b>가 있는 것만 고른다. 배회(<see cref="NpcWalkState"/>)와
/// 질주(<see cref="NpcSprintState"/>)가 거리·샘플 반경만 달리해 같은 규칙을 쓴다. (#805)
///
/// 규칙 세 가지가 여기 모여 있다:
///  · <b>에이전트의 통행 마스크로 샘플한다</b> — 못 가는 영역(Jail)을 뽑으면 경로가 문 앞에서 끊긴다 (#415).
///  · <b>도로는 목적지에서만 뺀다</b> — 도로 한복판을 목적지로 잡으면 거기 멈춰 서서 치인다.
///    지나가는 경로는 그대로 도로를 건넌다 (#634).
///  · <b>부분 경로는 도달 불가로 본다</b> — <see cref="NavMesh.SamplePosition"/>은 "거기에 NavMesh가
///    있는가"만 답하지 벽 너머인지는 모른다. 그대로 목적지로 주면 경로 끝에 붙어 제자리 걸음이 된다 (#661).
///
/// 서버(또는 오프라인) FSM에서만 불린다 — 경로 계산 인스턴스를 공유하는 근거다.
/// </summary>
public static class NpcMovePoint
{
    // 재사용 인스턴스 — NavMeshPath는 할당이 비싸다. 서버에서만 불리므로 공유해도 안전하다.
    private static NavMeshPath s_pathProbe;

    /// <summary>
    /// 다음 목적지를 뽑는다. 도달 가능한 지점을 찾으면 그것을, 하나도 없으면 <b>차선</b>(NavMesh 위이긴 한
    /// 지점)을 돌려준다 — 목적지를 아예 안 주면 NPC가 그 자리에 굳기 때문이다.
    /// </summary>
    /// <param name="agent">통행 마스크·위치의 기준이 되는 에이전트</param>
    /// <param name="minDistance">후보를 이 거리(m)보다 가까이 뽑지 않는다 — 한두 걸음 걷고 마는 이동 방지</param>
    /// <param name="maxDistance">후보를 뽑는 최대 거리(m)</param>
    /// <param name="sampleRadius">후보를 NavMesh 위로 끌어당길 때 허용하는 거리(m). 멀리 뽑을수록 넉넉히</param>
    /// <param name="maxAttempts">후보 추첨 시도 횟수 상한</param>
    /// <param name="maxProbes">경로 계산 횟수 상한 — 후보 전부에 돌리면 지점 하나 뽑는 데 배보다 배꼽이 커진다</param>
    /// <param name="point">고른 지점 (돌려준 값이 false면 의미 없음)</param>
    /// <param name="reachable">고른 지점까지 끊기지 않는 경로가 있는가 — false면 차선 지점이다</param>
    /// <returns>지점을 하나라도 뽑았는가. false면 주변에 NavMesh가 없다</returns>
    public static bool TryPick(
        NavMeshAgent agent,
        float minDistance,
        float maxDistance,
        float sampleRadius,
        int maxAttempts,
        int maxProbes,
        out Vector3 point,
        out bool reachable
    )
    {
        point = default;
        reachable = false;

        // NavMesh 밖의 에이전트에는 목적지를 줄 수 없다 — 뽑아 봐야 SetDestination이 에러만 남긴다
        if (agent == null || !agent.isOnNavMesh)
            return false;

        Vector3 origin = agent.transform.position;
        int mask = NpcNavAreas.ExcludeRoad(agent.areaMask);

        Vector3? fallback = null;
        int probes = 0;

        for (int i = 0; i < maxAttempts && probes < maxProbes; i++)
        {
            float angle = Random.Range(0f, Mathf.PI * 2f);
            float distance = Random.Range(minDistance, maxDistance);
            Vector3 direction = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
            Vector3 candidate = origin + direction * distance;

            if (!NavMesh.SamplePosition(candidate, out NavMeshHit hit, sampleRadius, mask))
                continue;

            fallback ??= hit.position;

            probes++;
            if (!IsReachable(agent, origin, hit.position))
                continue;

            point = hit.position;
            reachable = true;
            return true;
        }

        if (fallback == null)
            return false;

        point = fallback.Value;
        return true;
    }

    // origin에서 point까지 끊기지 않는 경로가 있는가 — 부분 경로는 도달 불가로 본다
    private static bool IsReachable(NavMeshAgent agent, Vector3 origin, Vector3 point)
    {
        s_pathProbe ??= new NavMeshPath();

        return NavMesh.CalculatePath(origin, point, agent.areaMask, s_pathProbe)
            && s_pathProbe.status == NavMeshPathStatus.PathComplete;
    }
}
