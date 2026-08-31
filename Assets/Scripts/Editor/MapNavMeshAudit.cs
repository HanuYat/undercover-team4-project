using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// 구워진 NavMesh를 격자로 훑어 기준점에서 못 가는 구역과 스폰 앵커 도달성을 로그로 뽑는다. (#719, #660)
/// 맵 씬을 연 채 Tools/맵 NavMesh 점검. 굽지는 않는다 — Bake 뒤에 돌릴 것.
/// </summary>
public class MapNavMeshAudit : EditorWindow
{
    private const float k_originSampleRadius = 5f;
    private const int k_maxClustersLogged = 20;

    [SerializeField]
    private Transform m_origin;

    [SerializeField]
    private float m_cellSize = 2.5f;

    [SerializeField]
    private float m_sampleRadius = 1f;

    [SerializeField]
    private string m_levels = "0, 2.6, 5.2";

    [SerializeField]
    private string m_anchorPrefixes = "NpcSpawn_, BombSpawnPoint_, BombCrate_, PlayerSpawnPoint, InmatePoint";

    [SerializeField]
    private string m_probes = "";

    [SerializeField]
    private float m_minClusterArea = 4f;

    private Vector2 m_scroll;

    [MenuItem("Tools/맵 NavMesh 점검")]
    private static void Open()
    {
        GetWindow<MapNavMeshAudit>("NavMesh 점검").minSize = new Vector2(420f, 400f);
    }

    private void OnGUI()
    {
        m_scroll = EditorGUILayout.BeginScrollView(m_scroll);

        m_origin = (Transform)
            EditorGUILayout.ObjectField(
                new GUIContent("기준점", "비우면 PlayerSpawnPoint를 찾아 쓴다"),
                m_origin,
                typeof(Transform),
                true
            );
        m_cellSize = EditorGUILayout.FloatField("격자 간격(m)", m_cellSize);
        m_sampleRadius = EditorGUILayout.FloatField("표본 반경(m)", m_sampleRadius);
        m_levels = EditorGUILayout.TextField(
            new GUIContent("검사 높이(m)", "다층은 층마다 따로 훑는다"),
            m_levels
        );
        m_minClusterArea = EditorGUILayout.FloatField("보고 하한(m²)", m_minClusterArea);

        EditorGUILayout.Space();
        m_anchorPrefixes = EditorGUILayout.TextField("앵커 접두사", m_anchorPrefixes);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField(
            new GUIContent("지점 검사", "한 줄에 'x,y,z' 또는 'x,y,z,불가'")
        );
        m_probes = EditorGUILayout.TextArea(m_probes, GUILayout.MinHeight(60f));

        EditorGUILayout.Space();
        if (GUILayout.Button("전체 점검", GUILayout.Height(30f)))
        {
            RunAll();
        }

        if (GUILayout.Button("서피스 설정만 보기"))
        {
            LogSurfaces();
        }

        EditorGUILayout.EndScrollView();
    }

    private void RunAll()
    {
        LogSurfaces();

        if (!TryResolveOrigin(out Vector3 origin))
        {
            return;
        }

        NavMeshTriangulation tri = NavMesh.CalculateTriangulation();
        if (tri.vertices == null || tri.vertices.Length == 0)
        {
            Debug.LogError("맵 NavMesh 점검: 구워진 NavMesh가 없다");
            return;
        }

        Bounds bounds = new Bounds(tri.vertices[0], Vector3.zero);
        for (int i = 1; i < tri.vertices.Length; i++)
        {
            bounds.Encapsulate(tri.vertices[i]);
        }

        Debug.Log(
            $"맵 NavMesh 점검: 기준점 {Fmt(origin)} · 범위 x[{bounds.min.x:F1}~{bounds.max.x:F1}] "
                + $"z[{bounds.min.z:F1}~{bounds.max.z:F1}] y[{bounds.min.y:F1}~{bounds.max.y:F1}]"
        );

        foreach (float level in ParseFloats(m_levels))
        {
            AuditLevel(origin, bounds, level);
        }

        AuditAnchors(origin);
        AuditProbes(origin);
    }

    private void AuditLevel(Vector3 origin, Bounds bounds, float level)
    {
        int cols = Mathf.Max(1, Mathf.CeilToInt(bounds.size.x / m_cellSize));
        int rows = Mathf.Max(1, Mathf.CeilToInt(bounds.size.z / m_cellSize));

        var state = new int[cols * rows]; // -1 대상 아님 / 0 못 감 / 1 도달
        var hitPos = new Vector3[cols * rows];
        var path = new NavMeshPath();

        int sampled = 0;
        int reached = 0;

        try
        {
            for (int r = 0; r < rows; r++)
            {
                if (
                    EditorUtility.DisplayCancelableProgressBar(
                        "맵 NavMesh 점검",
                        $"y={level:F2} ({r + 1}/{rows})",
                        (float)r / rows
                    )
                )
                {
                    Debug.LogWarning($"맵 NavMesh 점검: y={level:F2} 검사 취소");
                    return;
                }

                for (int c = 0; c < cols; c++)
                {
                    int i = r * cols + c;
                    state[i] = -1;

                    var probe = new Vector3(
                        bounds.min.x + (c + 0.5f) * m_cellSize,
                        level,
                        bounds.min.z + (r + 0.5f) * m_cellSize
                    );

                    if (
                        !NavMesh.SamplePosition(probe, out NavMeshHit hit, m_sampleRadius, NavMesh.AllAreas)
                        || Mathf.Abs(hit.position.y - level) > m_sampleRadius
                    )
                    {
                        continue;
                    }

                    sampled++;
                    hitPos[i] = hit.position;

                    bool ok =
                        NavMesh.CalculatePath(origin, hit.position, NavMesh.AllAreas, path)
                        && path.status == NavMeshPathStatus.PathComplete;

                    state[i] = ok ? 1 : 0;
                    if (ok)
                    {
                        reached++;
                    }
                }
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        if (sampled == 0)
        {
            Debug.Log($"맵 NavMesh 점검: y={level:F2} — NavMesh 없음");
            return;
        }

        string head =
            $"맵 NavMesh 점검: y={level:F2} — 도달률 {100f * reached / sampled:F1}% ({reached}/{sampled}칸)";

        List<string> clusters = CollectClusters(state, hitPos, cols, rows);
        if (clusters.Count == 0)
        {
            Debug.Log($"{head} · 고립 구역 없음");
            return;
        }

        var sb = new StringBuilder($"{head} · 고립 덩어리 {clusters.Count}개\n");
        for (int i = 0; i < clusters.Count && i < k_maxClustersLogged; i++)
        {
            sb.AppendLine($"  {clusters[i]}");
        }

        if (clusters.Count > k_maxClustersLogged)
        {
            sb.AppendLine($"  … 외 {clusters.Count - k_maxClustersLogged}개");
        }

        Debug.LogWarning(sb.ToString().TrimEnd());
    }

    private List<string> CollectClusters(int[] state, Vector3[] hitPos, int cols, int rows)
    {
        float cellArea = m_cellSize * m_cellSize;
        var found = new List<(float area, Vector3 center, Bounds box)>();
        var seen = new bool[state.Length];
        var stack = new Stack<int>();

        for (int start = 0; start < state.Length; start++)
        {
            if (state[start] != 0 || seen[start])
            {
                continue;
            }

            seen[start] = true;
            stack.Push(start);

            int count = 0;
            Vector3 sum = Vector3.zero;
            var box = new Bounds(hitPos[start], Vector3.zero);

            while (stack.Count > 0)
            {
                int i = stack.Pop();
                count++;
                sum += hitPos[i];
                box.Encapsulate(hitPos[i]);

                int c = i % cols;
                int r = i / cols;
                TryPush(state, seen, stack, cols, rows, c - 1, r);
                TryPush(state, seen, stack, cols, rows, c + 1, r);
                TryPush(state, seen, stack, cols, rows, c, r - 1);
                TryPush(state, seen, stack, cols, rows, c, r + 1);
            }

            float area = count * cellArea;
            if (area >= m_minClusterArea)
            {
                found.Add((area, sum / count, box));
            }
        }

        found.Sort((a, b) => b.area.CompareTo(a.area));

        var lines = new List<string>(found.Count);
        foreach ((float area, Vector3 center, Bounds box) in found)
        {
            lines.Add(
                $"{area:F0}m² · 중심 {Fmt(center)} · x[{box.min.x:F1}~{box.max.x:F1}] z[{box.min.z:F1}~{box.max.z:F1}]"
            );
        }

        return lines;
    }

    private static void TryPush(int[] state, bool[] seen, Stack<int> stack, int cols, int rows, int c, int r)
    {
        if (c < 0 || r < 0 || c >= cols || r >= rows)
        {
            return;
        }

        int i = r * cols + c;
        if (state[i] != 0 || seen[i])
        {
            return;
        }

        seen[i] = true;
        stack.Push(i);
    }

    private void AuditAnchors(Vector3 origin)
    {
        string[] prefixes = SplitCsv(m_anchorPrefixes);
        if (prefixes.Length == 0)
        {
            return;
        }

        var path = new NavMeshPath();
        var bad = new List<string>();
        int total = 0;

        foreach (Transform t in FindObjectsByType<Transform>(FindObjectsSortMode.None))
        {
            if (!StartsWithAny(t.name, prefixes))
            {
                continue;
            }

            total++;

            if (!NavMesh.SamplePosition(t.position, out NavMeshHit hit, 2f, NavMesh.AllAreas))
            {
                bad.Add($"{t.name} {Fmt(t.position)} — 2m 안에 NavMesh 없음");
            }
            else if (
                !NavMesh.CalculatePath(origin, hit.position, NavMesh.AllAreas, path)
                || path.status != NavMeshPathStatus.PathComplete
            )
            {
                bad.Add($"{t.name} {Fmt(t.position)} — 기준점에서 못 감 ({path.status})");
            }
            else if (hit.distance > 1f)
            {
                bad.Add($"{t.name} {Fmt(t.position)} — NavMesh에서 {hit.distance:F2}m 떠 있음");
            }
        }

        if (total == 0)
        {
            Debug.LogWarning($"맵 NavMesh 점검: 앵커를 못 찾았다 (접두사 '{m_anchorPrefixes}')");
            return;
        }

        if (bad.Count == 0)
        {
            Debug.Log($"맵 NavMesh 점검: 앵커 {total}개 전부 정상");
            return;
        }

        var sb = new StringBuilder($"맵 NavMesh 점검: 앵커 {total}개 중 {bad.Count}개 문제\n");
        foreach (string line in bad)
        {
            sb.AppendLine($"  {line}");
        }

        Debug.LogError(sb.ToString().TrimEnd());
    }

    private void AuditProbes(Vector3 origin)
    {
        if (string.IsNullOrWhiteSpace(m_probes))
        {
            return;
        }

        var path = new NavMeshPath();
        var sb = new StringBuilder("맵 NavMesh 점검: 지점 검사\n");
        bool anyFail = false;

        foreach (string raw in m_probes.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//", System.StringComparison.Ordinal))
            {
                continue;
            }

            string[] parts = SplitCsv(line);
            if (
                parts.Length < 3
                || !TryFloat(parts[0], out float x)
                || !TryFloat(parts[1], out float y)
                || !TryFloat(parts[2], out float z)
            )
            {
                sb.AppendLine($"  ?    {line} — 'x,y,z[,불가]' 형식이 아니다");
                anyFail = true;
                continue;
            }

            bool expectBlocked = parts.Length >= 4 && parts[3] == "불가";
            var point = new Vector3(x, y, z);

            bool onMesh = NavMesh.SamplePosition(point, out NavMeshHit hit, m_sampleRadius, NavMesh.AllAreas);
            bool walkable =
                onMesh
                && NavMesh.CalculatePath(origin, hit.position, NavMesh.AllAreas, path)
                && path.status == NavMeshPathStatus.PathComplete;

            bool pass = expectBlocked ? !walkable : walkable;
            anyFail |= !pass;

            string state = !onMesh
                ? $"{m_sampleRadius}m 안에 NavMesh 없음"
                : walkable
                    ? $"도달 가능 {Fmt(hit.position)}"
                    : "NavMesh는 있으나 못 감";

            sb.AppendLine(
                $"  {(pass ? "OK  " : "실패")} {Fmt(point)} 기대={(expectBlocked ? "불가" : "도달")} — {state}"
            );
        }

        if (anyFail)
        {
            Debug.LogError(sb.ToString().TrimEnd());
        }
        else
        {
            Debug.Log(sb.ToString().TrimEnd());
        }
    }

    private void LogSurfaces()
    {
        NavMeshSurface[] surfaces = FindObjectsByType<NavMeshSurface>(FindObjectsSortMode.None);
        if (surfaces.Length == 0)
        {
            Debug.LogError("맵 NavMesh 점검: 씬에 NavMeshSurface가 없다");
            return;
        }

        var sb = new StringBuilder($"맵 NavMesh 점검: NavMeshSurface {surfaces.Length}개\n");
        foreach (NavMeshSurface srf in surfaces)
        {
            NavMeshBuildSettings settings = NavMesh.GetSettingsByID(srf.agentTypeID);
            float voxel = srf.overrideVoxelSize ? srf.voxelSize : settings.agentRadius / 3f;

            sb.AppendLine(
                $"  {srf.name}: 복셀 {voxel:F3} · 최소영역 {srf.minRegionArea} · 수집 {srf.collectObjects}"
            );
            sb.AppendLine(
                $"    반지름 {settings.agentRadius} · 높이 {settings.agentHeight} · climb {settings.agentClimb} · slope {settings.agentSlope}"
            );
            sb.AppendLine($"    NavMeshData: {DescribeData(srf)}");
        }

        sb.Append($"  NavMeshLink {FindObjectsByType<NavMeshLink>(FindObjectsSortMode.None).Length}개");
        Debug.Log(sb.ToString());
    }

    private static string DescribeData(NavMeshSurface srf)
    {
        if (srf.navMeshData == null)
        {
            return "없음 — 굽지 않았다";
        }

        return AssetDatabase.Contains(srf.navMeshData)
            ? AssetDatabase.GetAssetPath(srf.navMeshData)
            : "⚠ 에셋이 아니다 — 씬을 저장하면 .unity가 바이너리가 된다";
    }

    private bool TryResolveOrigin(out Vector3 origin)
    {
        origin = Vector3.zero;
        Transform t = m_origin;

        if (t == null)
        {
            foreach (Transform candidate in FindObjectsByType<Transform>(FindObjectsSortMode.None))
            {
                if (candidate.name.StartsWith("PlayerSpawnPoint", System.StringComparison.Ordinal))
                {
                    t = candidate;
                    break;
                }
            }
        }

        if (t == null)
        {
            Debug.LogError("맵 NavMesh 점검: 기준점을 못 찾았다. 창에서 지정할 것");
            return false;
        }

        if (!NavMesh.SamplePosition(t.position, out NavMeshHit hit, k_originSampleRadius, NavMesh.AllAreas))
        {
            Debug.LogError($"맵 NavMesh 점검: 기준점 '{t.name}'{Fmt(t.position)} 근처에 NavMesh가 없다");
            return false;
        }

        m_origin = t;
        origin = hit.position;
        return true;
    }

    private static bool StartsWithAny(string name, string[] prefixes)
    {
        foreach (string prefix in prefixes)
        {
            if (name.StartsWith(prefix, System.StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string Fmt(Vector3 v) => $"({v.x:F1}, {v.y:F1}, {v.z:F1})";

    private static string[] SplitCsv(string s)
    {
        var parts = new List<string>();
        foreach (string raw in s.Split(','))
        {
            string part = raw.Trim();
            if (part.Length > 0)
            {
                parts.Add(part);
            }
        }

        return parts.ToArray();
    }

    private static bool TryFloat(string s, out float value) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static IEnumerable<float> ParseFloats(string s)
    {
        foreach (string part in SplitCsv(s))
        {
            if (TryFloat(part, out float v))
            {
                yield return v;
            }
        }
    }
}
