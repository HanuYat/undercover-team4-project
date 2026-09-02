using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// 스폰 후보가 <b>거기서 빠져나올 수 있는 곳인지</b> 판정할 기준점을 스폰 포인트마다 하나씩 잡는다 (#660).
///
/// <see cref="NavMesh.SamplePosition"/>은 "NavMesh 위인가"만 답한다. 끊긴 조각 위여도 참이라
/// 건물 안 주머니나 2층 문턱에 갇힌 채로 스폰된다. 그래서 후보에서 기준점까지
/// <b>경로가 완주하는지</b>를 따로 묻는다.
///
/// 기준점은 스폰 포인트 <b>근처</b>에 둔다. 맵 전체에 하나만 두면 경로가 맵을 가로질러 길어지고,
/// 그 값을 스폰 한 마리마다 치르게 된다.
///
/// 기준점은 탐침 여러 개를 서로 이어보고 <b>가장 많이 닿는 것</b>으로 고른다. 본토가 다수라
/// 다수결이 곧 본토다. 한 번 뽑고 마는 대신 이렇게 하는 이유는, 기준점이 섬에 앉으면 그 포인트의
/// 정상 후보까지 전부 기각돼 스폰 수가 조용히 미달하기 때문이다.
/// </summary>
public static class NpcSpawnAnchors
{
    /// <summary>기준점을 고를 때 쓰는 탐침 수 기본값. 적을수록 기준점이 섬에 앉을 확률이 오른다.</summary>
    public const int k_defaultProbeCount = 8;

    // 탐침을 뽑다가 NavMesh가 없어 계속 실패해도 멈추도록 두는 상한 (탐침 1개당)
    private const int k_probeAttemptsPerSample = 25;

    /// <summary>스폰 포인트 하나에 대해 잡힌 기준점.</summary>
    public readonly struct Anchor
    {
        public readonly Vector3 Position;

        /// <summary>기준점을 잡지 못했는지 — 스폰 포인트 주변에 통행 가능 NavMesh가 아예 없는 경우다.</summary>
        public readonly bool IsValid;

        public Anchor(Vector3 position, bool isValid)
        {
            Position = position;
            IsValid = isValid;
        }
    }

    /// <summary>
    /// 스폰 포인트마다 기준점을 하나씩 잡는다. 스폰 시작 전 1회만 부르면 된다
    /// (실측 비용: 아포칼립스 8포인트 × 탐침 8개 = 224회 호출, 1.97ms).
    /// </summary>
    /// <param name="spawnAreaMask">탐침을 <b>뽑을 때</b> 쓰는 마스크 — 스폰과 같아야 하므로 도로를 뺀 것을 넘긴다.</param>
    /// <param name="pathAreaMask">
    /// 탐침끼리 <b>이어보는</b> 마스크 — 반드시 에이전트의 전체 마스크를 넘긴다.
    /// 도로를 뺀 마스크로 이으면 블록 간이 전부 <c>PathPartial</c>이라(실측: 1000건 중 완주 77건)
    /// 멀쩡한 지점이 죄다 섬으로 오판된다. <see cref="NpcNavAreas"/> 클래스 주석의 분단이 그대로 재현되는 지점.
    /// </param>
    public static Anchor[] Resolve(IReadOnlyList<Transform> spawnPoints, float spawnRadius, float sampleMaxDistance, int spawnAreaMask, int pathAreaMask, int probeCount)
    {
        Anchor[] anchors = new Anchor[spawnPoints.Count];
        List<Vector3> probes = new List<Vector3>();
        NavMeshPath path = new NavMeshPath();
        int clampedProbeCount = Mathf.Max(1, probeCount);

        for (int i = 0; i < spawnPoints.Count; i++)
        {
            Transform point = spawnPoints[i];
            if (point == null)
            {
                anchors[i] = new Anchor(Vector3.zero, false);
                continue;
            }

            CollectProbes(probes, point.position, spawnRadius, sampleMaxDistance, spawnAreaMask, clampedProbeCount);
            if (probes.Count == 0)
            {
                anchors[i] = new Anchor(Vector3.zero, false);
                continue;
            }

            anchors[i] = new Anchor(PickMostConnected(probes, pathAreaMask, path), true);
        }

        return anchors;
    }

    /// <summary>
    /// 후보가 기준점까지 <b>완주하는 경로</b>를 갖는지. 끊긴 조각 위면 거짓이다.
    /// <paramref name="pathAreaMask"/>는 <see cref="Resolve"/>와 같은 이유로 에이전트의 전체 마스크여야 한다.
    /// </summary>
    public static bool IsConnected(Vector3 candidate, Vector3 anchor, int pathAreaMask, NavMeshPath path)
    {
        return NavMesh.CalculatePath(candidate, anchor, pathAreaMask, path) && path.status == NavMeshPathStatus.PathComplete;
    }

    /// <summary>
    /// 기준점끼리 서로 닿지 않는 쌍이 몇 개인지 — 0이 아니면 기준점 하나가 고립 구역에 앉았다는 뜻이다.
    /// 그 경우 그 포인트의 정상 후보가 전부 기각되므로, 부르는 쪽에서 경고를 남겨 조용한 미달 스폰을 막는다.
    /// </summary>
    public static int CountDisconnectedPairs(Anchor[] anchors, int pathAreaMask)
    {
        NavMeshPath path = new NavMeshPath();
        int disconnected = 0;

        for (int i = 0; i < anchors.Length; i++)
        {
            if (!anchors[i].IsValid)
                continue;

            for (int j = i + 1; j < anchors.Length; j++)
            {
                if (!anchors[j].IsValid)
                    continue;

                if (!IsConnected(anchors[i].Position, anchors[j].Position, pathAreaMask, path))
                    disconnected++;
            }
        }

        return disconnected;
    }

    // 스폰과 똑같은 방식으로 후보를 뽑아 탐침을 모은다 — 검사 대상과 같은 분포여야 의미가 있다
    private static void CollectProbes(List<Vector3> probes, Vector3 center, float spawnRadius, float sampleMaxDistance, int spawnAreaMask, int probeCount)
    {
        probes.Clear();
        int maxAttempts = probeCount * k_probeAttemptsPerSample;

        for (int attempt = 0; attempt < maxAttempts && probes.Count < probeCount; attempt++)
        {
            Vector2 offset = Random.insideUnitCircle * spawnRadius;
            Vector3 candidate = center + new Vector3(offset.x, 0f, offset.y);

            if (NavMesh.SamplePosition(candidate, out NavMeshHit hit, sampleMaxDistance, spawnAreaMask))
                probes.Add(hit.position);
        }
    }

    // 탐침끼리 전부 이어보고 가장 많이 닿는 것을 고른다 — 본토가 다수라 다수결이 곧 본토다
    private static Vector3 PickMostConnected(List<Vector3> probes, int pathAreaMask, NavMeshPath path)
    {
        int[] reachCount = new int[probes.Count];

        for (int i = 0; i < probes.Count; i++)
        {
            for (int j = i + 1; j < probes.Count; j++)
            {
                if (!IsConnected(probes[i], probes[j], pathAreaMask, path))
                    continue;

                reachCount[i]++;
                reachCount[j]++;
            }
        }

        int best = 0;
        for (int i = 1; i < probes.Count; i++)
        {
            if (reachCount[i] > reachCount[best])
                best = i;
        }

        return probes[best];
    }
}
