using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 미니맵 항공뷰 굽기 — 메뉴: Tools/미니맵 항공뷰 굽기 (#610, #611)
///
/// 열려 있는 맵 씬의 <see cref="MinimapViewer"/>가 들고 있는 월드 사각형(worldCenter/worldSize)을
/// 그대로 읽어, 그 영역만 직교 투영으로 내려찍어 PNG로 저장한다.
///
/// <b>카메라를 손으로 놓지 않는 이유</b>: 배경 그림이 좌표계와 어긋나면 그 위에 찍히는 아이콘이
/// 통째로 틀어진다. 기준값을 뷰어에서 직접 읽으면 어긋날 수가 없고, 맵 크기를 고친 뒤에도
/// 다시 굽기만 하면 된다.
///
/// 결과는 두 곳에 쓴다 — HQ 미니맵 mapView의 스프라이트(#610)와 맵 선택 콘솔의 Preview(#611).
/// 렌더 방식은 감정표현 아이콘 굽기(<see cref="EmoteIconBaker"/>)와 같은 계열이다.
/// </summary>
public class MinimapAerialBaker : EditorWindow
{
    // EmoteIconBaker(Assets/Imported/Art/EmoteIcons)와 같은 자리. 이 폴더는 .gitignore에 잡혀
    // 있지만 에셋 전용 저장소로 따로 push하므로 팀원에게도 전달된다 — 옮기지 말 것.
    private const string k_defaultOutput = "Assets/Imported/Art/Minimap";

    // UI 레이어 — 월드 캔버스(미니맵 자신·이름표)가 항공뷰에 함께 찍히면 안 된다
    private const int k_uiLayer = 5;

    [SerializeField]
    private MinimapViewer m_viewer;

    [Tooltip("월드 1m를 몇 픽셀로 찍을지 — 해상도가 이 값으로 정해진다")]
    [SerializeField]
    private float m_pixelsPerMeter = 10f;

    [Tooltip("카메라를 띄울 높이(m) — 맵에서 제일 높은 건물보다 위여야 한다")]
    [SerializeField]
    private float m_cameraHeight = 300f;

    [SerializeField]
    private Color m_background = new Color(0.08f, 0.09f, 0.12f, 1f);

    [SerializeField]
    private string m_outputFolder = k_defaultOutput;

    // 게임에는 있어야 하지만 지도에는 찍히면 안 되는 표식 — 굽는 동안만 끄고 끝나면 되돌린다.
    [Tooltip("굽는 동안 끌 오브젝트 이름 — 쉼표로 구분. 자식까지 함께 꺼진다")]
    [SerializeField]
    private List<string> m_hidden = new List<string>();

    // 미리 구운 결과 — 저장 전에 눈으로 확인한다. 저장은 이 결과를 그대로 쓴다.
    private Texture2D m_preview;

    [MenuItem("Tools/미니맵 항공뷰 굽기")]
    private static void Open() => GetWindow<MinimapAerialBaker>("미니맵 항공뷰");

    private void OnEnable() => TryFindViewer();

    private void OnDisable() => ClearPreview();

    private void OnGUI()
    {
        m_viewer = (MinimapViewer)
            EditorGUILayout.ObjectField("MinimapViewer", m_viewer, typeof(MinimapViewer), true);

        if (m_viewer == null)
        {
            EditorGUILayout.HelpBox(
                "맵 씬을 열고 HQ의 Minimap 오브젝트를 지정하라",
                MessageType.Info
            );
            if (GUILayout.Button("열린 씬에서 찾기"))
                TryFindViewer();
            return;
        }

        if (!TryReadArea(m_viewer, out Vector2 center, out Vector2 size))
        {
            EditorGUILayout.HelpBox(
                "worldSize가 0이다 — 씬에서 맵 크기를 배선하라",
                MessageType.Error
            );
            return;
        }

        m_pixelsPerMeter = EditorGUILayout.Slider("픽셀/미터", m_pixelsPerMeter, 1f, 40f);
        m_cameraHeight = EditorGUILayout.FloatField("카메라 높이(m)", m_cameraHeight);
        m_background = EditorGUILayout.ColorField("배경색", m_background);
        m_outputFolder = EditorGUILayout.TextField("저장 폴더", m_outputFolder);

        string hidden = EditorGUILayout.TextField("굽는 동안 끌 것", string.Join(", ", m_hidden));
        m_hidden = new List<string>(
            hidden.Split(new[] { ',' }, System.StringSplitOptions.RemoveEmptyEntries)
        );
        for (int i = 0; i < m_hidden.Count; i++)
            m_hidden[i] = m_hidden[i].Trim();

        Vector2Int resolution = ResolutionFor(size);
        EditorGUILayout.LabelField(
            $"월드 X {center.x - size.x * 0.5f:0.#} ~ {center.x + size.x * 0.5f:0.#}    "
                + $"Z {center.y - size.y * 0.5f:0.#} ~ {center.y + size.y * 0.5f:0.#}",
            EditorStyles.miniLabel
        );
        EditorGUILayout.LabelField(
            $"해상도 {resolution.x} x {resolution.y}",
            EditorStyles.miniLabel
        );

        EditorGUILayout.Space();

        if (GUILayout.Button("굽기"))
            BakePreview(center, size);

        using (new EditorGUI.DisabledScope(m_preview == null))
        {
            if (GUILayout.Button($"PNG 저장 ({SceneNameOf(m_viewer)}_Aerial.png)"))
                Save();
        }

        if (m_preview != null)
        {
            Rect rect = GUILayoutUtility.GetRect(position.width, 320f);
            GUI.DrawTexture(rect, m_preview, ScaleMode.ScaleToFit);
        }
    }

    private void TryFindViewer() =>
        m_viewer = FindFirstObjectByType<MinimapViewer>(FindObjectsInactive.Include);

    // worldCenter/worldSize는 private 직렬화 필드다 — 굽기 도구 하나 때문에 런타임 클래스에
    // 프로퍼티를 새로 뚫는 대신 SerializedObject로 읽는다.
    private static bool TryReadArea(MinimapViewer viewer, out Vector2 center, out Vector2 size)
    {
        center = Vector2.zero;
        size = Vector2.zero;

        var serialized = new SerializedObject(viewer);
        SerializedProperty centerX = serialized.FindProperty("m_worldCenterX");
        SerializedProperty centerZ = serialized.FindProperty("m_worldCenterZ");
        SerializedProperty sizeX = serialized.FindProperty("m_worldSizeX");
        SerializedProperty sizeZ = serialized.FindProperty("m_worldSizeZ");

        if (centerX == null || centerZ == null || sizeX == null || sizeZ == null)
        {
            Debug.LogError("MinimapAerialBaker: MinimapViewer의 월드 영역 필드명이 바뀌었다");
            return false;
        }

        center = new Vector2(centerX.floatValue, centerZ.floatValue);
        size = new Vector2(sizeX.floatValue, sizeZ.floatValue);
        return size.x > 0.01f && size.y > 0.01f;
    }

    private Vector2Int ResolutionFor(Vector2 size) =>
        new Vector2Int(
            Mathf.Max(8, Mathf.RoundToInt(size.x * m_pixelsPerMeter)),
            Mathf.Max(8, Mathf.RoundToInt(size.y * m_pixelsPerMeter))
        );

    private void BakePreview(Vector2 center, Vector2 size)
    {
        ClearPreview();
        m_preview = Bake(center, size);
        Repaint();
    }

    private Texture2D Bake(Vector2 center, Vector2 size)
    {
        Vector2Int resolution = ResolutionFor(size);

        var cameraObject = new GameObject("~MinimapAerialCamera")
        {
            hideFlags = HideFlags.HideAndDontSave,
        };

        RenderTexture target = null;
        RenderTexture previous = RenderTexture.active;

        // 굽는 동안만 안개를 끈다 — 두 맵 다 Linear Fog가 켜져 있어(Apocalypse 25~200m,
        // Cyberpunk 20~110m) 300m 위에서 내려찍으면 화면 전체가 안개 최대치라 단색이 된다.
        bool fogWasOn = RenderSettings.fog;
        RenderSettings.fog = false;

        List<GameObject> hidden = HideMarkers();

        try
        {
            Camera camera = cameraObject.AddComponent<Camera>();
            camera.orthographic = true;
            camera.orthographicSize = size.y * 0.5f; // 직교 크기 = 세로 절반. 가로는 타깃 비율이 낸다
            camera.nearClipPlane = 0.01f;
            camera.farClipPlane = m_cameraHeight * 2f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = m_background;
            camera.cullingMask = ~(1 << k_uiLayer);
            camera.useOcclusionCulling = false; // 맵 위 300m는 오클루전 볼륨 밖이다
            camera.enabled = false; // Render()로만 돈다

            // 바로 내려다본다 — X +90도면 화면 위가 월드 +Z, 오른쪽이 +X다.
            // MinimapViewer.WorldToMap의 u(X)·v(Z)와 방향이 그대로 맞는다.
            cameraObject.transform.position = new Vector3(center.x, m_cameraHeight, center.y);
            cameraObject.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

            target = new RenderTexture(resolution.x, resolution.y, 24, RenderTextureFormat.ARGB32);
            camera.targetTexture = target;
            camera.Render();
            camera.targetTexture = null;

            RenderTexture.active = target;
            var aerial = new Texture2D(resolution.x, resolution.y, TextureFormat.RGBA32, false);
            aerial.ReadPixels(new Rect(0f, 0f, resolution.x, resolution.y), 0, 0);
            aerial.Apply();
            return aerial;
        }
        finally
        {
            for (int i = 0; i < hidden.Count; i++)
            {
                if (hidden[i] != null)
                    hidden[i].SetActive(true);
            }

            RenderSettings.fog = fogWasOn;
            RenderTexture.active = previous;
            if (target != null)
            {
                target.Release();
                DestroyImmediate(target);
            }

            DestroyImmediate(cameraObject);
        }
    }

    private void Save()
    {
        if (!Directory.Exists(m_outputFolder))
        {
            Directory.CreateDirectory(m_outputFolder);
            AssetDatabase.Refresh();
        }

        string path = $"{m_outputFolder}/{SceneNameOf(m_viewer)}_Aerial.png";
        File.WriteAllBytes(path, m_preview.EncodeToPNG());
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

        // 스프라이트로 임포트해 둔다 — Image도 MapSelection.Entry.Preview도 Texture2D는 못 받는다
        if (AssetImporter.GetAtPath(path) is TextureImporter importer)
        {
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.mipmapEnabled = false;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.maxTextureSize = 4096;
            importer.SaveAndReimport();
        }

        Debug.Log($"[미니맵] 항공뷰 저장 — {path}");
        EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<Sprite>(path));
    }

    // 이름이 목록에 든 오브젝트를 끈다 — 껐던 것만 돌려주므로 원래 꺼져 있던 것은 건드리지 않는다.
    private List<GameObject> HideMarkers()
    {
        var hidden = new List<GameObject>();
        if (m_hidden == null || m_hidden.Count == 0)
            return hidden;

        GameObject[] all = FindObjectsByType<GameObject>(
            FindObjectsInactive.Exclude,
            FindObjectsSortMode.None
        );

        for (int i = 0; i < all.Length; i++)
        {
            if (all[i].activeSelf && m_hidden.Contains(all[i].name))
            {
                all[i].SetActive(false);
                hidden.Add(all[i]);
            }
        }

        return hidden;
    }

    private static string SceneNameOf(MinimapViewer viewer)
    {
        string name = viewer != null ? viewer.gameObject.scene.name : null;
        return string.IsNullOrEmpty(name) ? "Map" : name;
    }

    private void ClearPreview()
    {
        if (m_preview != null)
            DestroyImmediate(m_preview);

        m_preview = null;
    }
}
