using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 감정표현 아이콘 굽기 — 메뉴: Tools/감정표현 아이콘 굽기
///
/// 손으로 뽑아 둔 아이콘이 실제 동작과 어긋나 있었다: 전부 등을 보이고, 거의 중립 자세라 춤 18종이
/// 서로 구별되지 않고, 박수(clap)는 주먹 든 그림이었다. 클립에서 직접 찍으면 어긋날 수 없다.
///
/// <b>대표 프레임은 클립 길이의 비율로 고른다</b> — 시작·끝은 대개 중립 자세라 특징이 안 나온다.
/// 기본 50%로 굽고, 그 지점이 애매한 클립만 목록에서 비율을 조정한다.
///
/// 렌더 방식은 몽타주 굽기(<see cref="MontageBakeRig"/>, #607)와 같은 계열이지만 리그를 공유하지
/// 않는다 — 저쪽은 머리만 잡는 근접 카메라에 평면 머티리얼이고, 이쪽은 전신·조명 렌더다.
/// </summary>
public class EmoteIconBaker : EditorWindow
{
    private const int k_resolution = 256; // 기존 아이콘과 같은 크기 — .meta(스프라이트 설정)를 그대로 물려받는다
    private const float k_ringOuter = 124f; // 원형 테두리 바깥 반지름(px) — 기존 아이콘 실측
    private const float k_ringInner = 114f;

    private const string k_defaultOutput = "Assets/Imported/Art/EmoteIcons";

    [SerializeField] private EmoteCatalog m_catalog;
    [SerializeField] private GameObject m_subjectPrefab;

    [Tooltip("대표 프레임 = 클립 길이 × 이 비율")]
    [SerializeField] private float m_frameRatio = 0.5f;

    [Tooltip("대상을 이만큼 돌린 뒤 찍는다 — 정면이 안 나오면 조정")]
    [SerializeField] private float m_yaw;

    [Tooltip("전신 프레임 여백 배율")]
    [SerializeField] private float m_padding = 1.15f;

    [SerializeField] private string m_outputFolder = k_defaultOutput;

    // 프리팹 안에는 애니메이터가 돌리지 않는 사본이 함께 들어 있다 — 1인칭 팔(Camera 밑)과 시체
    // 래그돌(Corpse 밑). 그대로 두면 바인드 포즈 팔이 T자로 뻗은 채 함께 찍힌다.
    // NameTag(월드 캔버스)는 "Name" 글자가 그림 위에 얹히므로 함께 뺀다.
    [SerializeField]
    private List<string> m_excluded = new List<string> { "Camera", "Corpse", "NameTag", "Canvas" };

    // 클립별 비율 덮어쓰기 — 인덱스로 잡는다(카탈로그 인덱스가 곧 네트워크 계약이라 안정적이다)
    private readonly Dictionary<int, float> m_ratioOverrides = new Dictionary<int, float>();

    // 미리 구운 결과 — 저장 전에 눈으로 확인한다. 저장은 이 결과를 그대로 쓴다.
    private readonly Dictionary<int, Texture2D> m_preview = new Dictionary<int, Texture2D>();

    private Vector2 m_scroll;

    [MenuItem("Tools/감정표현 아이콘 굽기")]
    private static void Open() => GetWindow<EmoteIconBaker>("감정표현 아이콘");

    private void OnEnable()
    {
        if (m_catalog == null)
            m_catalog = AssetDatabase.LoadAssetAtPath<EmoteCatalog>("Assets/Scripts/Data/EmoteCatalog.asset");

        if (m_subjectPrefab == null)
            m_subjectPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Player.prefab");
    }

    private void OnDisable() => ClearPreview();

    private void OnGUI()
    {
        m_catalog = (EmoteCatalog)EditorGUILayout.ObjectField("카탈로그", m_catalog, typeof(EmoteCatalog), false);
        m_subjectPrefab = (GameObject)
            EditorGUILayout.ObjectField("찍을 프리팹", m_subjectPrefab, typeof(GameObject), false);

        m_frameRatio = EditorGUILayout.Slider("대표 프레임 비율", m_frameRatio, 0f, 1f);
        m_yaw = EditorGUILayout.Slider("대상 회전(도)", m_yaw, -180f, 180f);
        m_padding = EditorGUILayout.Slider("여백 배율", m_padding, 1f, 2f);
        m_outputFolder = EditorGUILayout.TextField("저장 폴더", m_outputFolder);

        string excluded = EditorGUILayout.TextField("제외할 하위 오브젝트", string.Join(", ", m_excluded));
        m_excluded = new List<string>(excluded.Split(new[] { ',' }, System.StringSplitOptions.RemoveEmptyEntries));
        for (int i = 0; i < m_excluded.Count; i++)
            m_excluded[i] = m_excluded[i].Trim();

        EditorGUILayout.Space();

        using (new EditorGUI.DisabledScope(m_catalog == null || m_subjectPrefab == null))
        {
            if (GUILayout.Button("전부 미리 굽기"))
                BakeAll();

            using (new EditorGUI.DisabledScope(m_preview.Count == 0))
            {
                if (GUILayout.Button($"미리 구운 {m_preview.Count}장 저장 (기존 PNG 덮어씀)"))
                    SaveAll();
            }
        }

        if (m_catalog == null)
        {
            EditorGUILayout.HelpBox("카탈로그를 지정하라", MessageType.Info);
            return;
        }

        EditorGUILayout.Space();
        DrawList();
    }

    private void DrawList()
    {
        m_scroll = EditorGUILayout.BeginScrollView(m_scroll);

        for (int i = 0; i < m_catalog.Count; i++)
        {
            EmoteDefinition definition = m_catalog.Get(i);
            if (definition == null)
                continue;

            EditorGUILayout.BeginHorizontal("box");

            // 왼쪽: 지금 배선된 아이콘 / 오른쪽: 방금 구운 것 — 나란히 놓아야 나아졌는지 보인다
            DrawThumb(definition.Icon != null ? definition.Icon.texture : null, "현재");
            DrawThumb(m_preview.TryGetValue(i, out Texture2D baked) ? baked : null, "구운 것");

            EditorGUILayout.BeginVertical();
            EditorGUILayout.LabelField($"{i:00}  {definition.FallbackName}", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                definition.Clip != null ? definition.Clip.name : "클립 없음 — 굽지 않는다",
                EditorStyles.miniLabel
            );

            float ratio = m_ratioOverrides.TryGetValue(i, out float over) ? over : m_frameRatio;
            float edited = EditorGUILayout.Slider("프레임", ratio, 0f, 1f);
            if (!Mathf.Approximately(edited, ratio))
                m_ratioOverrides[i] = edited;

            using (new EditorGUI.DisabledScope(definition.Clip == null || m_subjectPrefab == null))
            {
                if (GUILayout.Button("이것만 다시 굽기"))
                    BakeOne(i, definition);
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
        }

        EditorGUILayout.EndScrollView();
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
            for (int i = 0; i < m_catalog.Count; i++)
            {
                EmoteDefinition definition = m_catalog.Get(i);
                if (definition == null || definition.Clip == null)
                    continue;

                EditorUtility.DisplayProgressBar("감정표현 아이콘", definition.FallbackName, (float)i / m_catalog.Count);
                BakeOne(i, definition);
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    private void BakeOne(int index, EmoteDefinition definition)
    {
        float ratio = m_ratioOverrides.TryGetValue(index, out float over) ? over : m_frameRatio;

        // 배경(원판+테두리) 색은 지금 아이콘에서 뽑아 쓴다 — 자세만 갈아끼우고 색 규약은 건드리지 않는다
        SampleBackground(definition, out Color fill, out Color ring);

        // 원판 색을 <b>카메라 배경으로</b> 깔고 찍는다 — URP 렌더 타깃의 알파는 믿을 게 못 돼(MontageBakeRig가
        // 흰·검 2패스로 알파를 역산하는 이유), 어차피 불투명 원판 위에 얹을 그림이면 배경째 찍는 편이 낫다.
        Texture2D icon = RenderPose(definition.Clip, ratio, fill);
        if (icon == null)
            return;

        MaskCircle(icon, ring);

        if (m_preview.TryGetValue(index, out Texture2D old) && old != null)
            DestroyImmediate(old);

        m_preview[index] = icon;
        Repaint();
    }

    // 클립의 한 순간을 전신으로 찍는다 — 배경은 원판 색으로 채운 채.
    private Texture2D RenderPose(AnimationClip clip, float ratio, Color background)
    {
        var root = new GameObject("~EmoteIconBake") { hideFlags = HideFlags.HideAndDontSave };
        root.transform.position = new Vector3(0f, -10000f, 0f); // 열려 있는 씬이 화면에 들어오지 않게 멀리 둔다

        RenderTexture target = null;
        RenderTexture previous = RenderTexture.active;

        try
        {
            var subject = (GameObject)PrefabUtility.InstantiatePrefab(m_subjectPrefab, root.transform);
            subject.transform.localPosition = Vector3.zero;
            subject.transform.localRotation = Quaternion.Euler(0f, m_yaw, 0f);

            HideExcluded(subject);

            GameObject sampleTarget = ResolveAnimatorObject(subject);
            if (sampleTarget == null)
            {
                Debug.LogError($"EmoteIconBaker: {m_subjectPrefab.name}에 Animator가 없어 자세를 잡을 수 없다");
                return null;
            }

            // AnimationMode로 샘플한다 — clip.SampleAnimation은 휴머노이드 리타깃을 태우지 않아
            // 팔이 T-포즈 그대로 남는다(실측). 에디터가 애니메이션 창에서 쓰는 경로가 이쪽이다.
            //
            // ⚠ 끄는 순간 자세가 되돌아간다 — 찍기까지 끝낸 뒤(아래 finally) 꺼야 한다.
            AnimationMode.StartAnimationMode();
            AnimationMode.BeginSampling();
            AnimationMode.SampleAnimationClip(sampleTarget, clip, Mathf.Clamp01(ratio) * clip.length);
            AnimationMode.EndSampling();

            if (!TryGetBounds(subject, out Bounds bounds))
            {
                Debug.LogError($"EmoteIconBaker: {m_subjectPrefab.name}에 렌더러가 없다");
                return null;
            }

            // 카메라 쪽에서 비스듬히 내려 비춘다 — 몽타주 리그와 같은 배치라 결과 톤이 튀지 않는다
            var lightObject = new GameObject("~BakeLight");
            lightObject.transform.SetParent(root.transform, false);
            lightObject.transform.rotation = Quaternion.LookRotation(
                -subject.transform.forward + Vector3.down * 0.35f
            );

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
            camera.backgroundColor = background;
            camera.cullingMask = ~0;
            camera.enabled = false; // Render()로만 돈다

            // 정면에서 본다 — 대상의 앞쪽(+Z)에 서서 되돌아본다. 등을 찍던 것이 원래 문제였다.
            cameraObject.transform.position = bounds.center + subject.transform.forward * 10f;
            cameraObject.transform.rotation = Quaternion.LookRotation(-subject.transform.forward, Vector3.up);

            target = new RenderTexture(k_resolution, k_resolution, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 };

            camera.targetTexture = target;
            camera.Render();
            camera.targetTexture = null;

            RenderTexture.active = target;
            var pose = new Texture2D(k_resolution, k_resolution, TextureFormat.RGBA32, false);
            pose.ReadPixels(new Rect(0f, 0f, k_resolution, k_resolution), 0, 0);
            pose.Apply();
            return pose;
        }
        finally
        {
            if (AnimationMode.InAnimationMode())
                AnimationMode.StopAnimationMode();

            RenderTexture.active = previous;
            if (target != null)
            {
                target.Release();
                DestroyImmediate(target);
            }

            DestroyImmediate(root);
        }
    }

    // 제외 목록에 든 하위 오브젝트를 끈다 — 렌더에서도, 프레이밍 계산에서도 빠진다.
    private void HideExcluded(GameObject subject)
    {
        Transform[] all = subject.GetComponentsInChildren<Transform>(true);

        for (int i = 0; i < all.Length; i++)
        {
            if (all[i] == null || all[i] == subject.transform)
                continue;

            if (m_excluded.Contains(all[i].name))
                all[i].gameObject.SetActive(false);
        }
    }

    // Animator를 든 오브젝트 — 샘플링은 아바타를 든 쪽에 걸어야 휴머노이드 리타깃이 돈다
    private static GameObject ResolveAnimatorObject(GameObject subject)
    {
        Animator animator = subject.GetComponentInChildren<Animator>(true);
        return animator != null ? animator.gameObject : null;
    }

    // 자세의 실제 경계 — 프레이밍 기준이다.
    //
    // <see cref="Renderer.bounds"/>를 쓰면 안 된다: 스킨 메시의 경계는 어떤 자세에서도 안 잘리게
    // 부풀려 둔 값이라 실측이 1.5배 넘게 컸다(가로 1.07 vs 실제 0.56). 그만큼 인물이 작게 찍힌다.
    // 구운 메시에서 재면 그 프레임의 진짜 크기가 나온다.
    private static bool TryGetBounds(GameObject subject, out Bounds bounds)
    {
        bounds = default;

        Renderer[] renderers = subject.GetComponentsInChildren<Renderer>(false);
        var baked = new Mesh { hideFlags = HideFlags.HideAndDontSave };
        bool found = false;

        try
        {
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] == null || renderers[i] is ParticleSystemRenderer)
                    continue;

                Bounds world;

                if (renderers[i] is SkinnedMeshRenderer skinned && skinned.sharedMesh != null)
                {
                    skinned.BakeMesh(baked, true); // useScale — 스케일까지 먹인 로컬 좌표로 나온다
                    world = TransformBounds(skinned.transform, baked.bounds);
                }
                else
                {
                    world = renderers[i].bounds;
                }

                if (!found)
                {
                    bounds = world;
                    found = true;
                    continue;
                }

                bounds.Encapsulate(world);
            }
        }
        finally
        {
            DestroyImmediate(baked);
        }

        return found;
    }

    // 로컬 경계를 월드로 — BakeMesh가 스케일을 이미 먹였으므로 위치·회전만 태운다.
    private static Bounds TransformBounds(Transform transform, Bounds local)
    {
        Matrix4x4 matrix = Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one);
        var result = new Bounds(matrix.MultiplyPoint3x4(local.center), Vector3.zero);

        for (int i = 0; i < 8; i++)
        {
            var corner = new Vector3(
                (i & 1) == 0 ? local.min.x : local.max.x,
                (i & 2) == 0 ? local.min.y : local.max.y,
                (i & 4) == 0 ? local.min.z : local.max.z
            );

            result.Encapsulate(matrix.MultiplyPoint3x4(corner));
        }

        return result;
    }

    // ---- 배경 ----

    // 지금 아이콘에서 원판·테두리 색을 읽는다 — 못 읽으면 기존 춤 아이콘 색으로 떨어진다.
    private static void SampleBackground(EmoteDefinition definition, out Color fill, out Color ring)
    {
        fill = new Color32(43, 46, 79, 255);
        ring = new Color32(122, 152, 240, 255);

        if (definition.Icon == null)
            return;

        string path = AssetDatabase.GetAssetPath(definition.Icon);
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return;

        // 임포트 설정(읽기 불가·압축)을 우회해 원본 파일에서 직접 읽는다
        var source = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        try
        {
            if (!source.LoadImage(File.ReadAllBytes(path)) || source.width < k_resolution)
                return;

            // 한 점만 찍으면 그 자리에 캐릭터가 있을 때 몸 색을 배경으로 착각한다 — 실제로 화난
            // 표현(#637 아이콘)은 치켜든 주먹이 테두리에 닿아 있다. 여러 각도로 찍어 최빈색을 쓴다.
            float scale = source.height / (float)k_resolution;
            ring = SampleRingMode(source, (k_ringInner + k_ringOuter) * 0.5f * scale);
            fill = SampleRingMode(source, (k_ringInner - 14f) * scale);
        }
        finally
        {
            DestroyImmediate(source);
        }
    }

    // 반지름 하나를 한 바퀴 돌며 찍어 가장 많이 나온 색을 고른다 — 캐릭터가 걸친 자리는 소수라 밀린다.
    private static Color SampleRingMode(Texture2D source, float radius)
    {
        const int k_samples = 24;

        var counts = new Dictionary<Color32, int>();
        float centerX = (source.width - 1) * 0.5f;
        float centerY = (source.height - 1) * 0.5f;
        Color32 best = default;
        int bestCount = 0;

        for (int i = 0; i < k_samples; i++)
        {
            float angle = i * Mathf.PI * 2f / k_samples;
            int x = Mathf.RoundToInt(centerX + Mathf.Cos(angle) * radius);
            int y = Mathf.RoundToInt(centerY + Mathf.Sin(angle) * radius);

            Color32 sample = source.GetPixel(x, y);
            counts.TryGetValue(sample, out int count);
            counts[sample] = ++count;

            if (count > bestCount)
            {
                best = sample;
                bestCount = count;
            }
        }

        return best;
    }

    // 찍은 그림을 원형으로 오린다 — 테두리 띠를 덮어 그리고 바깥은 투명. 기존 아이콘과 같은 구도다.
    // 경계 한 칸은 알파로 뭉갠다(안 그러면 원 둘레가 톱니로 보인다).
    private static void MaskCircle(Texture2D icon, Color ring)
    {
        Color[] pixels = icon.GetPixels();
        float center = (k_resolution - 1) * 0.5f;

        for (int y = 0; y < k_resolution; y++)
        {
            for (int x = 0; x < k_resolution; x++)
            {
                int i = y * k_resolution + x;
                float distance = Mathf.Sqrt((x - center) * (x - center) + (y - center) * (y - center));

                if (distance >= k_ringInner)
                    pixels[i] = ring;

                float alpha = Mathf.Clamp01(k_ringOuter - distance); // 바깥 경계 1px 페이드
                pixels[i].a = alpha;
            }
        }

        icon.SetPixels(pixels);
        icon.Apply();
    }

    // ---- 저장 ----

    private void SaveAll()
    {
        if (!Directory.Exists(m_outputFolder))
        {
            Debug.LogError($"EmoteIconBaker: 저장 폴더가 없다 — {m_outputFolder}");
            return;
        }

        int written = 0;

        foreach (KeyValuePair<int, Texture2D> entry in m_preview)
        {
            EmoteDefinition definition = m_catalog.Get(entry.Key);
            if (definition == null || entry.Value == null)
                continue;

            // 기존 파일에 덮어쓴다 — .meta가 남아 스프라이트 임포트 설정과 GUID 배선이 그대로 유지된다
            string path =
                definition.Icon != null
                    ? AssetDatabase.GetAssetPath(definition.Icon)
                    : $"{m_outputFolder}/Emote_{definition.Id}.png";

            File.WriteAllBytes(path, entry.Value.EncodeToPNG());
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            written++;
        }

        Debug.Log($"[감정표현] 아이콘 {written}장을 다시 구웠다");
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
