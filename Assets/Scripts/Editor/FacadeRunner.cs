using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 두 점을 잇는 선을 따라 <b>건물 파사드 한 줄</b>을 랜덤 적층으로 깔아주는 에디터 도구. (#605)
///
/// 사이버펑크 골목의 밀도는 건물 <i>가짓수</i>가 아니라 실루엣 겹침에서 나온다 — 폭·층수·조각이
/// 제각각인 벽을 도로변에 붙여 세우면 하늘이 가려지고 거리가 캐니언이 된다. 그 반복 작업만 자동화한다.
///
/// <b>조각 팔레트는 이름 규칙으로 자동 수집한다</b> — 폴더 경로와 접두사만 주면 되고, 프리팹 목록을
/// 손으로 관리하지 않는다. Synty SciFiCity의 <c>SM_Bld_Section_*</c>이 기본값이다.
///
/// <b>좌표 규약.</b> 조각은 로컬 +Z가 벽면(바깥쪽), 피봇은 가로 중앙이다. 시작→끝 방향의 오른쪽이
/// 바깥(도로 쪽)이 되도록 세운다 — 블록을 시계 방향으로 돌면 벽면이 전부 도로를 향한다.
///
/// <b>사용법.</b> 씬에서 시작·끝을 나타내는 오브젝트 2개를 고르고 메뉴 Tools/파사드 생성기.
/// 만들어진 것은 평범한 씬 오브젝트라 이후 자유롭게 옮기고 지우면 된다.
/// </summary>
public static class FacadeRunner
{
    /// <summary>파사드 한 줄의 생성 규칙. 전부 인스펙터(창)에서 조절한다 — 코드에 수치를 박지 않는다.</summary>
    public class Settings
    {
        public string PieceFolder = "Assets/Imported/Synty/PolygonSciFiCity/Prefabs/Buildings";

        public float FloorHeight = 3f;      // 조각 한 층 높이
        public int MinFloors = 3;
        public int MaxFloors = 7;
        public float MinWidthScale = 1f;    // 조각 기본 폭(5m)에 곱하는 값 — 0.5 단위로 스냅된다
        public float MaxWidthScale = 2.5f;
        public float DepthScale = 1.25f;
        public float Gap = 0.1f;            // 이웃 건물 사이 여유 — 돌출부끼리 파고드는 것을 막는다
        public int MaxSameFloors = 2;       // 같은 창문 조각을 연속으로 쓸 최대 층수 — 한 건물이 통짜로 보이는 것 방지
        public float RoofVolumeHeight = 2f; // 옥상 NavMesh 차단 볼륨 두께 (0이면 안 만든다)
        public int Seed = 0;
    }

    // 층 역할별 팔레트 — 조각마다 두께·돌출이 달라서 아무거나 섞으면 벽면이 어긋나고 서로 파고든다.
    // 1층은 출입구, 중간층은 창문, 옥탑은 설비/막힌 벽으로 마감한다.
    private static readonly string[] k_groundPrefixes = { "SM_Bld_Section_Door_" };
    private static readonly string[] k_middlePrefixes = { "SM_Bld_Section_Window_" };
    private static readonly string[] k_topPrefixes = { "SM_Bld_Section_Industrial_", "SM_Bld_Section_Wall_", "SM_Bld_Section_Grid_" };

    // 간판·에어컨 같은 벽면 부착물은 일부러 만들지 않는다 — 자동 배치는 공중에 뜨거나 층선과 어긋나
    // 오히려 손배치보다 나쁘다. 이 도구는 건물 덩어리(실루엣)까지만 책임진다.

    [MenuItem("Tools/파사드 생성기")]
    private static void OpenWindow() => FacadeRunnerWindow.Open();

    /// <summary>
    /// start→end 선을 따라 건물을 이어 붙인다. 반환값은 생성된 묶음의 루트.
    /// 선의 진행 방향 기준 <b>오른쪽</b>이 바깥(벽면이 향하는 쪽)이다.
    /// </summary>
    public static GameObject Build(Vector3 start, Vector3 end, Settings settings, Transform parent)
    {
        List<GameObject> ground = Collect(settings.PieceFolder, k_groundPrefixes);
        List<GameObject> middle = Collect(settings.PieceFolder, k_middlePrefixes);
        List<GameObject> top = Collect(settings.PieceFolder, k_topPrefixes);
        if (ground.Count == 0 || middle.Count == 0 || top.Count == 0)
        {
            Debug.LogError($"FacadeRunner: '{settings.PieceFolder}'에서 Section 조각을 찾지 못했다");
            return null;
        }

        Vector3 flatStart = new Vector3(start.x, 0f, start.z);
        Vector3 flatEnd = new Vector3(end.x, 0f, end.z);
        Vector3 dir = flatEnd - flatStart;
        float length = dir.magnitude;
        if (length < 0.01f)
        {
            Debug.LogError("FacadeRunner: 시작점과 끝점이 같은 자리다");
            return null;
        }

        dir /= length;
        Vector3 outward = new Vector3(dir.z, 0f, -dir.x);   // 진행 방향의 오른쪽 = 벽면이 향하는 쪽
        float yaw = Mathf.Atan2(outward.x, outward.z) * Mathf.Rad2Deg;

        Random.InitState(settings.Seed);
        Undo.IncrementCurrentGroup();
        Undo.SetCurrentGroupName("파사드 생성");
        int undoGroup = Undo.GetCurrentGroup();

        GameObject root = new GameObject("Facade");
        Undo.RegisterCreatedObjectUndo(root, "파사드 생성");
        if (parent != null) root.transform.SetParent(parent, false);
        root.transform.position = flatStart;

        // 조각 폭이 제각각이면 줄이 어긋난다 — 가장 흔한 폭(SciFiCity 기준 5m)만 남기고 거른다
        float baseWidth = ModalWidth(middle);
        KeepWidth(ground, baseWidth);
        KeepWidth(middle, baseWidth);
        KeepWidth(top, baseWidth);
        // 중간층은 층마다 바뀌므로 두께까지 같아야 벽면이 안 어긋난다
        KeepDepth(middle, ModalDepth(middle));
        if (ground.Count == 0 || middle.Count == 0 || top.Count == 0)
        {
            Debug.LogError($"FacadeRunner: 폭 {baseWidth:0.00}m 조각이 없다");
            return null;
        }

        float cursor = 0f;
        int built = 0;

        while (length - cursor > baseWidth * settings.MinWidthScale * 0.5f)
        {
            // 폭은 0.5칸(2.5m) 단위로 스냅 — 5m 격자 위에 얹었을 때 어긋나지 않는다
            float widthScale = Mathf.Round(Random.Range(settings.MinWidthScale, settings.MaxWidthScale) * 2f) / 2f;
            float width = baseWidth * widthScale;
            if (cursor + width > length) // 마지막 칸은 남은 만큼으로 줄인다 — 선 밖으로 넘기지 않도록 내림
            {
                widthScale = Mathf.Floor((length - cursor) / baseWidth * 2f) / 2f;
                if (widthScale < 0.5f) break;
                width = baseWidth * widthScale;
            }

            int floors = Random.Range(settings.MinFloors, settings.MaxFloors + 1);
            Vector3 center = flatStart + dir * (cursor + width * 0.5f);
            GameObject building = new GameObject($"Bld_{built:00}");
            Undo.RegisterCreatedObjectUndo(building, "파사드 생성");
            building.transform.SetParent(root.transform, true);
            building.transform.SetPositionAndRotation(center, Quaternion.Euler(0f, yaw, 0f));

            // 1층·옥탑은 건물마다 하나로 고정. 중간층은 1~MaxSameFloors 층씩 같은 창문을 쓰고 바꾼다 —
            // 통짜로 한 종류면 밋밋하고, 층마다 바꾸면 산만하다.
            GameObject groundPiece = ground[Random.Range(0, ground.Count)];
            GameObject topPiece = top[Random.Range(0, top.Count)];
            GameObject middlePiece = middle[Random.Range(0, middle.Count)];
            int sameLeft = Random.Range(1, settings.MaxSameFloors + 1);

            for (int floor = 0; floor < floors; floor++)
            {
                if (floor > 0 && floor < floors - 1 && --sameLeft <= 0)
                {
                    middlePiece = PickOther(middle, middlePiece);   // 바꾸는데 같은 걸 또 뽑으면 의미가 없다
                    sameLeft = Random.Range(1, settings.MaxSameFloors + 1);
                }

                GameObject source = floor == 0 ? groundPiece : (floor == floors - 1 ? topPiece : middlePiece);
                GameObject piece = (GameObject)PrefabUtility.InstantiatePrefab(source, building.transform);
                piece.transform.localPosition = new Vector3(0f, floor * settings.FloorHeight, 0f);
                piece.transform.localRotation = Quaternion.identity;
                piece.transform.localScale = new Vector3(widthScale, 1f, settings.DepthScale);
            }

            // 조각마다 피봇·두께가 달라 계산만 믿으면 줄이 어긋난다 — 실측해서 두 방향 다 스냅한다
            SnapEdge(building.transform, -dir, flatStart + dir * cursor);   // 진행 반대쪽 끝을 커서에 붙인다
            SnapEdge(building.transform, outward, flatStart);               // 바깥 면을 선 위에 올린다
            float actualWidth = ExtentAlong(building.transform, dir) * 2f;
            if (cursor + actualWidth > length + 0.1f)
            {
                Undo.DestroyObjectImmediate(building);
                break;
            }

            MarkStatic(building);
            AddRoofVolume(building.transform, settings.RoofVolumeHeight);

            cursor += actualWidth + settings.Gap;
            built++;
        }

        Debug.Log($"[파사드] {built}채 · 길이 {length:0.0}m · 벽면 방향 {yaw:0}°", root);
        Undo.CollapseUndoOperations(undoGroup);
        Selection.activeGameObject = root;
        return root;
    }

    /// <summary>
    /// 옥상에 Not Walkable 볼륨을 얹는다 — 이게 없으면 NavMesh가 지붕 위에까지 깔려 NPC가 옥상을 걷는다.
    /// 크기는 그 건물 실측 풋프린트에 맞춘다 (건물마다 폭·깊이가 달라서 공용 값으로는 못 덮는다).
    /// </summary>
    private static void AddRoofVolume(Transform building, float height)
    {
        if (height <= 0f) return;

        Renderer[] renderers = building.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0) return;

        // 건물이 yaw로 돌아가 있어서 월드 AABB로는 못 쓴다 — 코너를 로컬로 옮겨 로컬 경계를 구한다
        Bounds local = new Bounds();
        bool init = false;
        foreach (Renderer renderer in renderers)
        {
            Bounds b = renderer.bounds;
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 point = new Vector3(
                    (corner & 1) == 0 ? b.min.x : b.max.x,
                    (corner & 2) == 0 ? b.min.y : b.max.y,
                    (corner & 4) == 0 ? b.min.z : b.max.z);
                Vector3 localPoint = building.InverseTransformPoint(point);
                if (!init) { local = new Bounds(localPoint, Vector3.zero); init = true; }
                else local.Encapsulate(localPoint);
            }
        }

        GameObject volume = new GameObject("NavMesh Modifier Volume");
        Undo.RegisterCreatedObjectUndo(volume, "파사드 생성");
        volume.transform.SetParent(building, false);
        volume.transform.localPosition = new Vector3(local.center.x, local.max.y, local.center.z);
        volume.transform.localRotation = Quaternion.identity;

        Unity.AI.Navigation.NavMeshModifierVolume modifier = volume.AddComponent<Unity.AI.Navigation.NavMeshModifierVolume>();
        modifier.area = 1;   // Not Walkable
        modifier.center = Vector3.zero;
        modifier.size = new Vector3(local.size.x + 0.4f, height, local.size.z + 0.4f);
    }

    /// <summary>axis 방향으로 가장 튀어나온 면이 planePoint를 지나는 평면에 닿도록 통째로 민다.</summary>
    private static void SnapEdge(Transform building, Vector3 axis, Vector3 planePoint)
    {
        if (!TryMeasure(building, out Bounds b)) return;
        float face = Vector3.Dot(b.center, axis) + ExtentAlong(building, axis);
        building.position += axis * (Vector3.Dot(planePoint, axis) - face);
    }

    // 축 정렬 방향이라 AABB 성분만 보면 된다
    private static float ExtentAlong(Transform building, Vector3 axis)
    {
        if (!TryMeasure(building, out Bounds b)) return 0f;
        return Mathf.Abs(axis.x) * b.extents.x + Mathf.Abs(axis.z) * b.extents.z;
    }

    private static bool TryMeasure(Transform building, out Bounds bounds)
    {
        bounds = new Bounds();
        Renderer[] renderers = building.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0) return false;

        bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
        return true;
    }

    // 팔레트에서 가장 흔한 폭을 고른다 — 규격 밖 조각(코너·특수)을 걸러내는 기준
    private static float ModalWidth(List<GameObject> pool)
    {
        Dictionary<float, int> counts = new Dictionary<float, int>();
        foreach (GameObject go in pool)
        {
            float w = Mathf.Round(MeasureBounds(go).size.x * 100f) / 100f;
            counts[w] = counts.ContainsKey(w) ? counts[w] + 1 : 1;
        }

        float best = 5f;
        int bestCount = 0;
        foreach (KeyValuePair<float, int> pair in counts)
        {
            if (pair.Value <= bestCount) continue;
            best = pair.Key;
            bestCount = pair.Value;
        }
        return best;
    }

    private static void KeepWidth(List<GameObject> pool, float width)
    {
        pool.RemoveAll(go => Mathf.Abs(MeasureBounds(go).size.x - width) > 0.15f);
    }

    // 직전에 쓴 것 말고 다른 조각을 고른다 (후보가 하나뿐이면 그대로)
    private static GameObject PickOther(List<GameObject> pool, GameObject current)
    {
        if (pool.Count <= 1) return pool[0];

        GameObject picked = current;
        while (picked == current) picked = pool[Random.Range(0, pool.Count)];
        return picked;
    }

    private static float ModalDepth(List<GameObject> pool)
    {
        Dictionary<float, int> counts = new Dictionary<float, int>();
        foreach (GameObject go in pool)
        {
            float d = Mathf.Round(MeasureBounds(go).size.z * 100f) / 100f;
            counts[d] = counts.ContainsKey(d) ? counts[d] + 1 : 1;
        }

        float best = 0f;
        int bestCount = 0;
        foreach (KeyValuePair<float, int> pair in counts)
        {
            if (pair.Value <= bestCount) continue;
            best = pair.Key;
            bestCount = pair.Value;
        }
        return best;
    }

    private static void KeepDepth(List<GameObject> pool, float depth)
    {
        if (depth <= 0f) return;
        pool.RemoveAll(go => Mathf.Abs(MeasureBounds(go).size.z - depth) > 0.15f);
    }

    private static Bounds MeasureBounds(GameObject prefab)
    {
        MeshFilter[] filters = prefab.GetComponentsInChildren<MeshFilter>(true);
        Bounds b = new Bounds();
        for (int i = 0; i < filters.Length; i++)
        {
            if (filters[i].sharedMesh == null) continue;
            if (b.size == Vector3.zero) b = filters[i].sharedMesh.bounds;
            else b.Encapsulate(filters[i].sharedMesh.bounds);
        }
        return b;
    }

    // LODGroup이 없는 팩이라 정적 배칭·Occlusion Culling에 기대야 한다 — 격자 생성기와 같은 플래그를 건다
    private static void MarkStatic(GameObject go)
    {
        GameObjectUtility.SetStaticEditorFlags(go,
            StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic);
        foreach (Transform child in go.GetComponentsInChildren<Transform>(true))
        {
            GameObjectUtility.SetStaticEditorFlags(child.gameObject,
                StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic);
        }
    }

    private static List<GameObject> Collect(string folder, string[] prefixes)
    {
        List<GameObject> found = new List<GameObject>();
        if (!AssetDatabase.IsValidFolder(folder)) return found;

        foreach (string guid in AssetDatabase.FindAssets("t:prefab", new[] { folder }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            string name = System.IO.Path.GetFileNameWithoutExtension(path);
            foreach (string prefix in prefixes)
            {
                if (!name.StartsWith(prefix)) continue;
                found.Add(AssetDatabase.LoadAssetAtPath<GameObject>(path));
                break;
            }
        }
        return found;
    }
}

/// <summary>파사드 생성기의 설정 창. 값은 창을 닫아도 도메인 리로드 전까지 남는다.</summary>
public class FacadeRunnerWindow : EditorWindow
{
    private static readonly FacadeRunner.Settings s_settings = new FacadeRunner.Settings();

    public static void Open() => GetWindow<FacadeRunnerWindow>("파사드 생성기").minSize = new Vector2(360f, 300f);

    private void OnGUI()
    {
        EditorGUILayout.HelpBox(
            "씬에서 시작·끝 오브젝트 2개를 고른 뒤 생성. 시작→끝 진행 방향의 오른쪽이 벽면(도로 쪽)이다.",
            MessageType.Info);

        s_settings.FloorHeight = EditorGUILayout.FloatField("층고", s_settings.FloorHeight);
        s_settings.MinFloors = EditorGUILayout.IntField("최소 층수", s_settings.MinFloors);
        s_settings.MaxFloors = EditorGUILayout.IntField("최대 층수", s_settings.MaxFloors);
        s_settings.MinWidthScale = EditorGUILayout.FloatField("최소 폭 배율", s_settings.MinWidthScale);
        s_settings.MaxWidthScale = EditorGUILayout.FloatField("최대 폭 배율", s_settings.MaxWidthScale);
        s_settings.DepthScale = EditorGUILayout.FloatField("깊이 배율", s_settings.DepthScale);
        s_settings.Gap = EditorGUILayout.FloatField("건물 간격", s_settings.Gap);
        s_settings.MaxSameFloors = EditorGUILayout.IntField("같은 창문 최대 연속 층", s_settings.MaxSameFloors);
        s_settings.RoofVolumeHeight = EditorGUILayout.FloatField("옥상 차단 볼륨 두께", s_settings.RoofVolumeHeight);
        s_settings.Seed = EditorGUILayout.IntField("시드", s_settings.Seed);

        EditorGUILayout.Space();
        s_settings.PieceFolder = EditorGUILayout.TextField("조각 폴더", s_settings.PieceFolder);

        EditorGUILayout.Space();
        GameObject[] picked = Selection.gameObjects;
        using (new EditorGUI.DisabledScope(picked.Length != 2))
        {
            if (GUILayout.Button("선택한 두 점 사이에 파사드 생성", GUILayout.Height(30f)))
            {
                FacadeRunner.Build(picked[0].transform.position, picked[1].transform.position, s_settings,
                    picked[0].transform.parent);
            }
        }

        if (picked.Length != 2)
        {
            EditorGUILayout.LabelField($"오브젝트 2개를 골라야 한다 (현재 {picked.Length}개)");
        }
    }
}
