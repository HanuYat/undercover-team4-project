using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 씬에 깔린 도로 타일을 격자로 읽어 <b>직선 구간</b>을 찾아 준다 — 폭주 차량(#304)의 경로원.
///
/// 마커를 따로 찍지 않는다. 맵 제작자가 이미 놓아 둔 도로 타일(이름 접두사로 식별)이 곧 경로 데이터다 —
/// 맵이 교체돼도 같은 방식으로 깔려 있으면 그대로 동작한다.
///
/// 타일은 일정 간격 격자에 놓인다는 전제다(현재 맵은 5m). 간격은 최근접 이웃 거리로 스스로 잰다.
/// 서버(또는 오프라인)에서만 쓴다.
/// </summary>
public static class RoadGrid
{
    private static readonly HashSet<Vector2Int> s_cells = new HashSet<Vector2Int>();
    private static float s_cellSize = 5f;
    private static float s_roadY;
    private static bool s_built;

    private static readonly Vector2Int[] k_axes = { new Vector2Int(1, 0), new Vector2Int(0, 1) };

    /// <summary>격자를 버린다 — 씬이 바뀌면 다시 읽어야 한다. 라운드 정리에서 부른다.</summary>
    public static void Invalidate()
    {
        s_built = false;
        s_cells.Clear();
    }

    /// <summary>도로가 하나라도 잡혔는가 — 없으면 이 맵에는 경로가 없다.</summary>
    public static bool Build(string namePrefix)
    {
        if (s_built)
            return s_cells.Count > 0;

        s_built = true;
        s_cells.Clear();

        List<Vector3> points = new List<Vector3>();
        GameObject[] roots = UnityEngine
            .SceneManagement.SceneManager.GetActiveScene()
            .GetRootGameObjects();
        foreach (GameObject root in roots)
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t.name.StartsWith(namePrefix))
                    points.Add(t.position);
            }
        }

        if (points.Count < 2)
            return false;

        s_cellSize = EstimateCellSize(points);
        s_roadY = points[0].y;

        foreach (Vector3 p in points)
            s_cells.Add(ToCell(p));

        return s_cells.Count > 0;
    }

    // 격자 간격 = 최근접 이웃 거리의 중앙값. 타일 크기를 코드에 박지 않으려는 것이다.
    private static float EstimateCellSize(List<Vector3> points)
    {
        List<float> nearest = new List<float>();
        int sample = Mathf.Min(points.Count, 24);
        for (int i = 0; i < sample; i++)
        {
            float best = float.MaxValue;
            for (int j = 0; j < points.Count; j++)
            {
                if (i == j)
                    continue;

                float d = Vector3.Distance(points[i], points[j]);
                if (d > 0.01f && d < best)
                    best = d;
            }

            if (best < 1000f)
                nearest.Add(best);
        }

        if (nearest.Count == 0)
            return 5f;

        nearest.Sort();
        return Mathf.Max(0.5f, nearest[nearest.Count / 2]);
    }

    private static Vector2Int ToCell(Vector3 world) =>
        new Vector2Int(
            Mathf.RoundToInt(world.x / s_cellSize),
            Mathf.RoundToInt(world.z / s_cellSize)
        );

    private static Vector3 ToWorld(Vector2Int cell) =>
        new Vector3(cell.x * s_cellSize, s_roadY, cell.y * s_cellSize);

    /// <summary>
    /// <paramref name="near"/>에서 가장 가까운 도로를 찾아, 그 지점을 지나는 <b>가장 긴 직선 구간</b>의
    /// 양 끝을 돌려준다. 구간이 <paramref name="minLength"/>보다 짧으면 실패.
    /// </summary>
    public static bool TryFindStraightRun(
        Vector3 near,
        float searchRadius,
        float minLength,
        out Vector3 fromPoint,
        out Vector3 toPoint
    )
    {
        fromPoint = Vector3.zero;
        toPoint = Vector3.zero;

        if (s_cells.Count == 0)
            return false;

        // 표적 근처의 도로 칸을 고른다 — 멀리 있는 도로를 달려봐야 아무도 못 본다
        Vector2Int seed = ToCell(near);
        if (!s_cells.Contains(seed) && !TryFindNearestCell(near, searchRadius, out seed))
            return false;

        float bestLength = -1f;
        for (int a = 0; a < k_axes.Length; a++)
        {
            Vector2Int axis = k_axes[a];
            Vector2Int head = Walk(seed, axis);
            Vector2Int tail = Walk(seed, -axis);

            Vector3 headWorld = ToWorld(head);
            Vector3 tailWorld = ToWorld(tail);
            float length = Vector3.Distance(headWorld, tailWorld);
            if (length <= bestLength)
                continue;

            bestLength = length;
            fromPoint = tailWorld;
            toPoint = headWorld;
        }

        return bestLength >= minLength;
    }

    // 도로 칸이 이어지는 데까지 한 방향으로 나아가 마지막 칸을 돌려준다
    private static Vector2Int Walk(Vector2Int from, Vector2Int step)
    {
        Vector2Int current = from;
        while (s_cells.Contains(current + step))
            current += step;

        return current;
    }

    private static bool TryFindNearestCell(Vector3 near, float radius, out Vector2Int found)
    {
        found = default;
        float best = radius * radius;
        bool hit = false;

        foreach (Vector2Int cell in s_cells)
        {
            float d = (ToWorld(cell) - near).sqrMagnitude;
            if (d >= best)
                continue;

            best = d;
            found = cell;
            hit = true;
        }

        return hit;
    }
}
