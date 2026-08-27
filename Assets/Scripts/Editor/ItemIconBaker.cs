using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 아이템 아이콘 굽기 — 메뉴: Tools/아이템 아이콘 굽기 (#793, #843)
///
/// 인벤토리 핫바(<see cref="InventorySlotView"/>)와 약탈창(<see cref="LootSlotView"/>)이 쓰는
/// <see cref="ItemBase.ItemIcon"/>을 아이템 모델에서 직접 찍어 만든다. 손으로 그리지 않는 이유는
/// <see cref="EmoteIconBaker"/>와 같다 — 모델에서 찍으면 실제로 손에 들리는 물건과 어긋날 수 없다.
///
/// <b>찍는 대상은 아이템 프리팹이 아니라 <see cref="ItemBase.HeldModelPrefab"/>이다.</b> 아이템
/// 프리팹에는 렌더러가 없고(월드에 떨어진 모습도 손에 든 모습도 이 모델을 런타임에 붙인 것),
/// 모델을 안 걸어 둔 아이템은 그릴 것이 없어 건너뛴다.
///
/// <b>설치형은 아이템 프리팹이 없다</b> — 주문창 목록에 쓸 아이콘을 <see cref="ShopCatalog"/> 항목의
/// 진열 모델에서 찍어 그 항목의 <c>m_displayIcon</c>에 배선한다 (#843). 굽는 방식은 소지형과 같다.
///
/// 저장까지 하면 대상의 아이콘 필드까지 배선한다 — 구워 놓고 손으로 다시 끌어다 넣을 이유가 없다.
/// </summary>
public class ItemIconBaker : EditorWindow
{
    private const int k_resolution = 256; // 감정표현 아이콘과 같은 크기
    private const string k_defaultOutput = "Assets/Imported/Art/ItemIcons";
    private const string k_prefabSearchFolder = "Assets/Prefabs";

    // 아이템별 각도가 없을 때 쓰는 기본값.
    [SerializeField] private float m_yaw = 30f;
    [SerializeField] private float m_pitch = 20f;
    [SerializeField] private float m_padding = 1.2f;
    [SerializeField] private string m_outputFolder = k_defaultOutput;

    private readonly List<BakeTarget> m_targets = new List<BakeTarget>();

    // 대상별 각도 — 모델마다 정면으로 삼는 축이 달라 한 각도로 전부 찍으면 어떤 것은 뒤통수가 나온다.
    // 대상 식별자로 EditorPrefs에 남긴다: 굽는 사람이 눈으로 맞춘 값이라 다시 구울 때 되살아나야 하고,
    // 결과물(PNG)은 어차피 별도 저장소로 나가므로 각도까지 에셋으로 만들 값어치는 없다.
    private readonly Dictionary<string, Vector2> m_angles = new Dictionary<string, Vector2>();

    // 미리 구운 결과 — 저장 전에 눈으로 확인한다. 저장은 이 결과를 그대로 쓴다.
    private readonly Dictionary<string, Texture2D> m_preview = new Dictionary<string, Texture2D>();

    private Vector2 m_scroll;

    // ---- 굽는 대상 ----

    /// <summary>아이콘을 굽고 배선할 자리 하나. 소지형 아이템과 설치형 카탈로그 항목이 같은 창을 쓴다.</summary>
    private abstract class BakeTarget
    {
        /// <summary>각도·미리보기를 기억하는 열쇠. 에셋 GUID 기반이라 다시 모아도 같은 값이다.</summary>
        public abstract string Id { get; }
        public abstract string Label { get; }
        public abstract GameObject Model { get; }

        /// <summary>찍을 때 모델에 씌울 배율. 카탈로그가 정한 비례와 아이콘이 어긋나지 않게 한다.</summary>
        public virtual Vector3 ModelScale => Vector3.one;

        public abstract Sprite Current { get; }
        public abstract string FileName { get; }
        public abstract void Assign(Sprite sprite);
    }

    private class ItemTarget : BakeTarget
    {
        private readonly ItemBase m_item;

        public ItemTarget(ItemBase item) => m_item = item;

        public override string Id => AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(m_item));
        public override string Label => m_item.name;
        public override GameObject Model => m_item.HeldModelPrefab;
        public override Sprite Current => m_item.ItemIcon;
        public override string FileName => "Item_" + m_item.name + ".png";

        public override void Assign(Sprite sprite)
        {
            var so = new SerializedObject(m_item);
            so.FindProperty("m_itemIcon").objectReferenceValue = sprite;
            so.ApplyModifiedPropertiesWithoutUndo();

            PrefabUtility.SavePrefabAsset(m_item.gameObject);
        }
    }

    private class InstallableTarget : BakeTarget
    {
        private readonly ShopCatalog m_catalog;
        private readonly int m_index;

        public InstallableTarget(ShopCatalog catalog, int index)
        {
            m_catalog = catalog;
            m_index = index;
        }

        private ShopCatalog.Entry Entry => m_catalog.Get(m_index);

        public override string Id =>
            AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(m_catalog)) + ":" + m_index;

        public override string Label => Entry != null ? Entry.Installable.ToString() : "(빈 항목)";
        public override GameObject Model => Entry?.DisplayModel;

        // 설치형 모델은 카탈로그 배율이 곧 실물 비례다 — 사이렌 버튼처럼 납작하게 눌러 쓰는 것을
        // 배율 없이 찍으면 아이콘만 원래 구(球)로 나와 실물과 다른 물건처럼 보인다.
        public override Vector3 ModelScale => Entry != null ? Entry.DisplayScale : Vector3.one;

        public override Sprite Current => Entry?.Icon;
        public override string FileName => "Installable_" + Label + ".png";

        public override void Assign(Sprite sprite)
        {
            var so = new SerializedObject(m_catalog);
            SerializedProperty entries = so.FindProperty("m_entries");
            if (entries == null || m_index >= entries.arraySize)
                return;

            entries.GetArrayElementAtIndex(m_index).FindPropertyRelative("m_displayIcon").objectReferenceValue = sprite;
            so.ApplyModifiedPropertiesWithoutUndo();

            EditorUtility.SetDirty(m_catalog);
        }
    }

    [MenuItem("Tools/아이템 아이콘 굽기")]
    private static void Open() => GetWindow<ItemIconBaker>("아이템 아이콘");

    private void OnEnable() => CollectTargets();

    private void OnDisable() => ClearPreview();

    // Assets/Prefabs 아래의 ItemBase 프리팹 + 카탈로그의 설치형 항목을 모은다 — 목록을 손으로
    // 유지하면 아이템이 늘 때 빠진다.
    private void CollectTargets()
    {
        m_targets.Clear();

        foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { k_prefabSearchFolder }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
                continue;

            var item = prefab.GetComponent<ItemBase>();
            if (item != null)
                m_targets.Add(new ItemTarget(item));
        }

        foreach (string guid in AssetDatabase.FindAssets("t:ShopCatalog"))
        {
            var catalog = AssetDatabase.LoadAssetAtPath<ShopCatalog>(AssetDatabase.GUIDToAssetPath(guid));
            if (catalog == null)
                continue;

            // 소지형은 위에서 프리팹으로 이미 잡혔다 — 여기서는 프리팹이 없는 설치형만 더한다.
            for (int i = 0; i < catalog.Count; i++)
            {
                ShopCatalog.Entry entry = catalog.Get(i);
                if (entry != null && entry.IsInstallable)
                    m_targets.Add(new InstallableTarget(catalog, i));
            }
        }

        m_targets.Sort((a, b) => string.CompareOrdinal(a.Label, b.Label));

        foreach (BakeTarget target in m_targets)
        {
            string saved = EditorPrefs.GetString(AngleKey(target), string.Empty);
            string[] parts = saved.Split(';');
            if (parts.Length == 2
                && float.TryParse(parts[0], out float yaw)
                && float.TryParse(parts[1], out float pitch))
            {
                m_angles[target.Id] = new Vector2(yaw, pitch);
            }
        }
    }

    private void OnGUI()
    {
        m_yaw = EditorGUILayout.Slider("가로 회전(도)", m_yaw, -180f, 180f);
        m_pitch = EditorGUILayout.Slider("내려다보는 각(도)", m_pitch, -89f, 89f);
        m_padding = EditorGUILayout.Slider("여백 배율", m_padding, 1f, 2f);
        m_outputFolder = EditorGUILayout.TextField("저장 폴더", m_outputFolder);

        EditorGUILayout.Space();

        if (GUILayout.Button("목록 새로고침"))
            CollectTargets();

        if (GUILayout.Button("전부 미리 굽기"))
            BakeAll();

        using (new EditorGUI.DisabledScope(m_preview.Count == 0))
        {
            if (GUILayout.Button($"미리 구운 {m_preview.Count}장 저장 + 배선"))
                SaveAll();
        }

        EditorGUILayout.Space();
        DrawList();
    }

    private void DrawList()
    {
        m_scroll = EditorGUILayout.BeginScrollView(m_scroll);

        foreach (BakeTarget target in m_targets)
        {
            EditorGUILayout.BeginHorizontal("box");

            // 왼쪽: 지금 배선된 아이콘 / 오른쪽: 방금 구운 것 — 나란히 놓아야 나아졌는지 보인다
            DrawThumb(target.Current != null ? target.Current.texture : null, "현재");
            DrawThumb(m_preview.TryGetValue(target.Id, out Texture2D baked) ? baked : null, "구운 것");

            EditorGUILayout.BeginVertical();
            EditorGUILayout.LabelField(target.Label, EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                target.Model != null ? target.Model.name : "모델 없음 — 굽지 않는다",
                EditorStyles.miniLabel
            );

            Vector2 angle = ResolveAngle(target);
            float yaw = EditorGUILayout.Slider("가로 회전", angle.x, -180f, 180f);
            float pitch = EditorGUILayout.Slider("내려다보는 각", angle.y, -89f, 89f);

            if (!Mathf.Approximately(yaw, angle.x) || !Mathf.Approximately(pitch, angle.y))
                SetAngle(target, new Vector2(yaw, pitch));

            EditorGUILayout.BeginHorizontal();

            using (new EditorGUI.DisabledScope(target.Model == null))
            {
                if (GUILayout.Button("이것만 다시 굽기"))
                    BakeOne(target);
            }

            using (new EditorGUI.DisabledScope(!m_angles.ContainsKey(target.Id)))
            {
                if (GUILayout.Button("기본 각도로"))
                {
                    m_angles.Remove(target.Id);
                    EditorPrefs.DeleteKey(AngleKey(target));
                }
            }

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
        }

        EditorGUILayout.EndScrollView();
    }

    // ---- 대상별 각도 ----

    private static string AngleKey(BakeTarget target) => "ItemIconBaker.angle." + target.Id;

    /// <summary>이 대상에 맞춰 둔 각도. 없으면 창의 기본값.</summary>
    private Vector2 ResolveAngle(BakeTarget target) =>
        m_angles.TryGetValue(target.Id, out Vector2 angle) ? angle : new Vector2(m_yaw, m_pitch);

    private void SetAngle(BakeTarget target, Vector2 angle)
    {
        m_angles[target.Id] = angle;
        EditorPrefs.SetString(AngleKey(target), angle.x + ";" + angle.y);
    }

    private static void DrawThumb(Texture texture, string caption)
    {
        EditorGUILayout.BeginVertical(GUILayout.Width(72f));
        Rect rect = GUILayoutUtility.GetRect(64f, 64f, GUILayout.ExpandWidth(false));
        if (texture != null)
            GUI.DrawTexture(rect, texture, ScaleMode.ScaleToFit);
        EditorGUILayout.LabelField(caption, EditorStyles.miniLabel);
        EditorGUILayout.EndVertical();
    }

    // ---- 굽기 ----

    private void BakeAll()
    {
        ClearPreview();

        try
        {
            for (int i = 0; i < m_targets.Count; i++)
            {
                BakeTarget target = m_targets[i];
                if (target.Model == null)
                    continue;

                EditorUtility.DisplayProgressBar("아이템 아이콘", target.Label, (float)i / m_targets.Count);
                BakeOne(target);
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    private void BakeOne(BakeTarget target)
    {
        Vector2 angle = ResolveAngle(target);

        Texture2D icon = RenderModel(target.Model, angle.x, angle.y, target.ModelScale);
        if (icon == null)
            return;

        if (m_preview.TryGetValue(target.Id, out Texture2D old) && old != null)
            DestroyImmediate(old);

        m_preview[target.Id] = icon;
        Repaint();
    }

    // 모델 하나를 정투영으로 찍는다. 배경은 투명 — 슬롯 배경(선택 하이라이트가 색을 바꾼다) 위에 얹혀야 한다.
    private Texture2D RenderModel(GameObject modelPrefab, float yaw, float pitch, Vector3 scale)
    {
        var root = new GameObject("~ItemIconBake") { hideFlags = HideFlags.HideAndDontSave };
        root.transform.position = new Vector3(0f, -10000f, 0f); // 열려 있는 씬이 화면에 들어오지 않게 멀리 둔다

        RenderTexture target = null;
        RenderTexture previous = RenderTexture.active;

        try
        {
            var subject = (GameObject)PrefabUtility.InstantiatePrefab(modelPrefab, root.transform);
            subject.transform.localPosition = Vector3.zero;
            subject.transform.localRotation = Quaternion.Euler(pitch, yaw, 0f);
            subject.transform.localScale = scale;

            if (!TryGetBounds(subject, out Bounds bounds))
            {
                Debug.LogError($"ItemIconBaker: {modelPrefab.name}에 렌더러가 없다");
                return null;
            }

            var lightObject = new GameObject("~BakeLight");
            lightObject.transform.SetParent(root.transform, false);
            lightObject.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            Light light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.2f;

            var cameraObject = new GameObject("~BakeCamera");
            cameraObject.transform.SetParent(root.transform, false);

            Camera camera = cameraObject.AddComponent<Camera>();
            camera.orthographic = true;
            camera.orthographicSize = Mathf.Max(bounds.extents.x, bounds.extents.y) * m_padding;
            camera.nearClipPlane = 0.01f;
            camera.farClipPlane = 100f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.cullingMask = ~0;
            camera.enabled = false; // Render()로만 돈다

            // 카메라는 축에 맞춰 두고 대상을 돌린다 — 프레이밍을 월드 경계 그대로 쓸 수 있다.
            cameraObject.transform.position = bounds.center + Vector3.back * 10f;
            cameraObject.transform.rotation = Quaternion.identity;

            target = new RenderTexture(k_resolution, k_resolution, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 };

            // 흰 배경·검은 배경 두 번 찍어 알파를 역산한다 — URP 불투명 패스는 알파를 그대로 두지 않아
            // 투명 배경 한 번으로는 통째로 불투명하게 나온다. (MontageBakeRig.RenderPixels와 같은 방식)
            Texture2D onWhite = Capture(camera, target, Color.white);
            Texture2D onBlack = Capture(camera, target, Color.black);

            Color[] white = onWhite.GetPixels();
            Color[] black = onBlack.GetPixels();
            var pixels = new Color[white.Length];

            for (int i = 0; i < pixels.Length; i++)
            {
                float alpha = 1f - ((white[i].r - black[i].r) + (white[i].g - black[i].g) + (white[i].b - black[i].b)) / 3f;
                pixels[i] = alpha <= 0.004f
                    ? Color.clear
                    : new Color(black[i].r / alpha, black[i].g / alpha, black[i].b / alpha, Mathf.Clamp01(alpha));
            }

            DestroyImmediate(onWhite);
            DestroyImmediate(onBlack);

            var icon = new Texture2D(k_resolution, k_resolution, TextureFormat.RGBA32, false);
            icon.SetPixels(pixels);
            icon.Apply();
            return icon;
        }
        finally
        {
            RenderTexture.active = previous;

            if (target != null)
            {
                target.Release();
                DestroyImmediate(target);
            }

            DestroyImmediate(root);
        }
    }

    private static Texture2D Capture(Camera camera, RenderTexture target, Color background)
    {
        camera.backgroundColor = background;
        camera.targetTexture = target;
        camera.Render();
        camera.targetTexture = null;

        RenderTexture.active = target;
        var texture = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false);
        texture.ReadPixels(new Rect(0f, 0f, target.width, target.height), 0, 0);
        texture.Apply();
        return texture;
    }

    // 아이템 모델은 정적 프롭이라 Renderer.bounds가 그대로 실측이다 (스킨 메시의 부풀린 경계 문제 없음).
    private static bool TryGetBounds(GameObject subject, out Bounds bounds)
    {
        bounds = default;
        bool found = false;

        foreach (Renderer renderer in subject.GetComponentsInChildren<Renderer>(false))
        {
            if (renderer == null || renderer is ParticleSystemRenderer)
                continue;

            if (!found)
            {
                bounds = renderer.bounds;
                found = true;
                continue;
            }

            bounds.Encapsulate(renderer.bounds);
        }

        return found;
    }

    // ---- 저장 ----

    private void SaveAll()
    {
        if (!Directory.Exists(m_outputFolder))
        {
            Debug.LogError($"ItemIconBaker: 저장 폴더가 없다 — {m_outputFolder}");
            return;
        }

        int written = 0;

        foreach (BakeTarget target in m_targets)
        {
            if (!m_preview.TryGetValue(target.Id, out Texture2D baked) || baked == null)
                continue;

            // 저장 위치는 항상 이 폴더다. 배선된 아이콘의 경로를 따라가면 누가 임시로 꽂아 둔 팩 스프라이트를
            // 구운 그림으로 덮어쓸 수 있고(서드파티 에셋 수정 금지), 따라갈 이유도 없다 — 임포트 설정은
            // ApplySpriteImport가, 배선은 target.Assign이 직접 하고, 경로가 이름으로 결정적이라
            // 다시 구워도 같은 파일을 쳐서 GUID가 그대로 유지된다.
            string path = $"{m_outputFolder}/{target.FileName}";

            File.WriteAllBytes(path, baked.EncodeToPNG());
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            ApplySpriteImport(path);

            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            if (sprite == null)
            {
                Debug.LogError($"ItemIconBaker: {path}에서 스프라이트를 못 읽어 {target.Label} 배선을 건너뛴다");
                continue;
            }

            target.Assign(sprite);
            written++;
        }

        AssetDatabase.SaveAssets();
        Debug.Log($"[아이템] 아이콘 {written}장을 굽고 배선했다");
    }

    // 새로 만든 PNG는 기본이 Default 텍스처라 Image.sprite에 못 넣는다 — 스프라이트로 돌려놓는다.
    // spriteImportMode까지 Single로 박는 이유: 이 프로젝트의 기본값이 Multiple이라 그대로 두면
    // 스프라이트 시트로 잡혀 잘라낸 조각이 하나도 없고, LoadAssetAtPath<Sprite>가 null로 온다.
    private static void ApplySpriteImport(string path)
    {
        var importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer == null)
            return;

        if (importer.textureType == TextureImporterType.Sprite
            && importer.spriteImportMode == SpriteImportMode.Single)
            return;

        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Single;
        importer.alphaIsTransparency = true;
        importer.mipmapEnabled = false;
        importer.SaveAndReimport();
    }

    private void ClearPreview()
    {
        foreach (Texture2D texture in m_preview.Values)
        {
            if (texture != null)
                DestroyImmediate(texture);
        }

        m_preview.Clear();
    }
}
