using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 몽타주 레이어 굽기 (#607) — 메뉴: Tools/몽타주 레이어 굽기
///
/// 포트레이트는 축 값마다 그림 한 장을 베이스 위에 얹어 만든다. 그 그림들을 손으로 그리는 대신
/// AppearanceDatabase에 이미 배선된 프롭을 마네킹 머리에 하나씩 붙여 정면에서 렌더해 뽑는다 —
/// 화면의 NPC와 같은 프롭을 쓰므로 몽타주가 실물과 어긋날 수 없다 (appearance-montage.md §1).
///
/// 프롭 레이어는 평면 실루엣으로 굽고 색은 런타임에 입힌다(옵션 색 / 머리색). 프롭 원본 아틀라스가
/// 어두워 그대로 구우면 곱셈 틴트가 탁해지는 문제(NpcAppearance의 중립 베이스와 같은 이유)를 피한다.
///
/// SciFi 전용 값(가림·후드·풀헬멧·특수 안경)은 프롭이 없어 여기서 나오지 않는다 — 손으로 채워야 하고,
/// 채우기 전까지는 AppearanceDatabase.CanDepict가 그 축을 공개 후보에서 빼 준다.
/// </summary>
public class MontageLayerBaker : EditorWindow
{
    private const float k_featureSoftness = 0.08f; // 이목구비 컷오프 경계 폭 — 계단을 살짝 뭉개 톱니를 막는다

    private const float k_unknownHairCapRatio = 0.3f; // 형태 미상 머리가 정수리에서 덮는 깊이 (두상 높이 대비)

    [SerializeField] private AppearanceDatabase m_database;

    [SerializeField] private GameObject m_mannequinPrefab;

    [SerializeField] private Material m_flatMaterial;

    [SerializeField] private int m_resolution = 16;

    [SerializeField] private float m_orthoSize = 0.16f;

    // Synty 휴머노이드의 Head 본은 두개골 밑동에 있다 — 머리 중심은 거기서 9cm쯤 위다.
    // 낮게 잡으면 정수리가 잘리고 대신 목·어깨가 프레임에 들어와 이목구비 추출의 밝기 기준까지 흐린다.
    [SerializeField] private float m_headOffset = 0.09f;

    [SerializeField] private float m_cameraDistance = 1.5f;

    [SerializeField] private float m_featureThreshold = 0.25f;

    // 이미지는 전부 Imported 공유 저장소에 둔다 (2026-08-12 에셋 폴더 정리)
    [SerializeField] private string m_outputFolder = "Assets/Imported/Art/Montage/Layers";

    [SerializeField] private GameObject m_sciFiPrefab;

    [SerializeField] private int m_sciFiModelIndex = 13;

    [SerializeField] private Vector2 m_scroll;

    [MenuItem("Tools/몽타주 레이어 굽기")]
    private static void Open() => GetWindow<MontageLayerBaker>("몽타주 레이어");

    private void OnGUI()
    {
        m_scroll = EditorGUILayout.BeginScrollView(m_scroll);

        EditorGUILayout.HelpBox(
            "AppearanceDatabase의 프롭을 마네킹 머리에 붙여 정면 렌더로 레이어를 뽑고, 각 옵션의 MontageLayer에 바로 꽂는다.\n"
                + "마네킹 프리팹은 원하는 바디 하나만 활성인 상태여야 한다 (Generic 민머리 바디 권장).",
            MessageType.Info
        );

        m_database = (AppearanceDatabase)
            EditorGUILayout.ObjectField("외형 DB", m_database, typeof(AppearanceDatabase), false);
        m_mannequinPrefab = (GameObject)
            EditorGUILayout.ObjectField("마네킹 프리팹", m_mannequinPrefab, typeof(GameObject), false);
        m_flatMaterial = (Material)
            EditorGUILayout.ObjectField(
                new GUIContent("평면 머티리얼", "프롭 레이어를 실루엣으로 굽는 흰색 Unlit 머티리얼. 비우면 프롭 원본 머티리얼 그대로 굽는다"),
                m_flatMaterial,
                typeof(Material),
                false
            );

        m_resolution = EditorGUILayout.IntSlider("해상도", m_resolution, 16, 256);
        m_orthoSize = EditorGUILayout.FloatField(new GUIContent("프레임 크기", "직교 카메라 크기 — 작을수록 머리를 크게 잡는다"), m_orthoSize);
        m_headOffset = EditorGUILayout.FloatField(new GUIContent("머리 오프셋", "머리 본보다 이만큼 위를 화면 중심으로 잡는다"), m_headOffset);
        m_cameraDistance = EditorGUILayout.FloatField("카메라 거리", m_cameraDistance);
        m_featureThreshold = EditorGUILayout.Slider(
            new GUIContent("이목구비 문턱", "피부보다 이만큼 어두운 픽셀만 이목구비 레이어로 남긴다. 낮추면 명암까지 딸려오고, 높이면 눈·입이 사라진다"),
            m_featureThreshold,
            0.05f,
            0.7f
        );
        m_outputFolder = EditorGUILayout.TextField("저장 폴더", m_outputFolder);

        EditorGUILayout.Space();

        using (new EditorGUI.DisabledScope(m_database == null || m_mannequinPrefab == null))
        {
            if (GUILayout.Button("레이어 굽기", GUILayout.Height(30)))
                Bake();
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("SciFi 전용 값 레이어", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "후드·헬멧·발광렌즈는 Generic 프롭이 없어 위 굽기로 나오지 않는다. SciFi 모델은 머리·모자가 메시에 통짜로 구워져 있어 부위만 떼어낼 수 없으므로,\n"
                + "여기서 모델 하나를 같은 프레임으로 렌더한 뒤 이미지 편집기에서 그 부위만 남기고 지운다.",
            MessageType.None
        );

        m_sciFiPrefab = (GameObject)
            EditorGUILayout.ObjectField("SciFi 프리팹", m_sciFiPrefab, typeof(GameObject), false);
        m_sciFiModelIndex = EditorGUILayout.IntField(
            new GUIContent("모델 인덱스", "후드=13 / 헬멧=4,14 / 발광렌즈=11,16,19 (AppearanceModelCatalog 기준)"),
            m_sciFiModelIndex
        );

        using (new EditorGUI.DisabledScope(m_sciFiPrefab == null))
        {
            if (GUILayout.Button("SciFi 모델 렌더"))
                RenderSciFiModel();
        }

        EditorGUILayout.EndScrollView();
    }

    /// <summary>
    /// SciFi 모델 하나를 프롭 레이어와 같은 프레임으로 렌더해 PNG로 뽑는다 (#607).
    /// 통짜 메시라 부위를 코드로 떼어낼 수 없어 <b>사람이 지워서</b> 레이어를 만든다 —
    /// 그래도 화면의 그 모델을 그대로 찍은 그림이라 몽타주가 실물과 어긋나지 않는다.
    /// </summary>
    private void RenderSciFiModel()
    {
        var rig = new GameObject("~MontageSciFiRig") { hideFlags = HideFlags.HideAndDontSave };
        rig.transform.position = new Vector3(0f, -10000f, 0f);

        try
        {
            GameObject model = (GameObject)PrefabUtility.InstantiatePrefab(m_sciFiPrefab, rig.transform);
            model.transform.localPosition = Vector3.zero;
            model.transform.localRotation = Quaternion.identity;

            // NpcCatalogAppearance와 같은 규칙 — 계층 순서의 SkinnedMeshRenderer 하나만 켠다
            SkinnedMeshRenderer[] bodies = model.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (m_sciFiModelIndex < 0 || m_sciFiModelIndex >= bodies.Length)
            {
                Debug.LogError($"MontageLayerBaker: 모델 인덱스 {m_sciFiModelIndex}가 범위를 벗어났다 (바디 {bodies.Length}종)");
                return;
            }
            for (int i = 0; i < bodies.Length; i++)
                bodies[i].gameObject.SetActive(i == m_sciFiModelIndex);

            Transform head = ResolveHead(model.transform);
            if (head == null)
            {
                Debug.LogError("MontageLayerBaker: SciFi 모델에서 머리 본을 찾지 못했다");
                return;
            }

            Camera camera = CreateCamera(rig.transform, model.transform, head);
            CreateLight(rig.transform, model.transform);

            Directory.CreateDirectory(m_outputFolder);
            AssetDatabase.Refresh();

            string fileName = $"Montage_SciFi_{m_sciFiModelIndex:00}";
            SavePixels(RenderPixels(camera), fileName);
            Debug.Log($"[몽타주] SciFi 모델 {m_sciFiModelIndex} 렌더 → {m_outputFolder}/{fileName}.png — 필요한 부위만 남기고 지운 뒤 해당 옵션의 MontageLayer에 꽂을 것");
        }
        finally
        {
            DestroyImmediate(rig);
        }
    }

    private void Bake()
    {
        if (m_flatMaterial == null)
        {
            Debug.LogError("MontageLayerBaker: 평면 머티리얼이 없으면 실루엣을 오려낼 수 없다 — 흰색 URP/Unlit 머티리얼을 지정할 것");
            return;
        }

        Directory.CreateDirectory(m_outputFolder);
        AssetDatabase.Refresh(); // 새로 만든 폴더를 인식시켜야 아래 ImportAsset이 먹는다

        // DontSave — 굽는 동안만 존재하는 리그라 열려 있는 씬을 더럽히지 않는다
        var rig = new GameObject("~MontageBakeRig") { hideFlags = HideFlags.HideAndDontSave };
        rig.transform.position = new Vector3(0f, -10000f, 0f); // 씬의 다른 것이 화면에 들어오지 않게 멀리 둔다
        var baked = new List<string>();

        // 프롭을 굽는 동안 바디를 덮을 검정 머티리얼 — 가림만 남기고 색으로 걷어내기 위한 것
        Material occluder = m_flatMaterial != null ? new Material(m_flatMaterial) : null;
        if (occluder != null)
        {
            occluder.hideFlags = HideFlags.HideAndDontSave;
            // 셰이더에 따라 색 프로퍼티 이름이 갈린다 (URP는 _BaseColor, 레거시 Unlit은 _Color)
            if (occluder.HasProperty("_BaseColor"))
                occluder.SetColor("_BaseColor", Color.black);
            if (occluder.HasProperty("_Color"))
                occluder.SetColor("_Color", Color.black);
        }

        try
        {
            GameObject mannequin = (GameObject)PrefabUtility.InstantiatePrefab(m_mannequinPrefab, rig.transform);
            mannequin.transform.localPosition = Vector3.zero;
            mannequin.transform.localRotation = Quaternion.identity;

            Transform head = ResolveHead(mannequin.transform);
            if (head == null)
            {
                Debug.LogError("MontageLayerBaker: 마네킹에서 머리 본을 찾지 못했다 — 프리팹의 휴머노이드 리그 또는 'Head' 이름 자식을 확인할 것");
                return;
            }

            Camera camera = CreateCamera(rig.transform, mannequin.transform, head);
            Light light = CreateLight(rig.transform, mannequin.transform);

            Renderer[] bodyRenderers = mannequin.GetComponentsInChildren<Renderer>(true);

            // ① 이목구비 — 조명 렌더에서 주변 피부보다 어두운 픽셀만 남긴다.
            //    살과 분리해 둬야 살 레이어를 통짜로 피부색으로 칠할 수 있다.
            Sprite faceSprite = SavePixels(ExtractFeatures(RenderPixels(camera)), "Montage_Face");
            if (faceSprite != null)
            {
                SetPrivateSprite("m_montageFace", faceSprite);
                baked.Add("이목구비");
            }

            // ② 살 — 평면 실루엣. 피부색 곱셈 틴트가 원본 살색·명암에 눌리면 안 되므로 순백으로 굽는다
            ApplyMaterial(bodyRenderers, m_flatMaterial);
            light.enabled = false;

            Color[] basePixels = RenderPixels(camera);
            Sprite baseSprite = SavePixels(basePixels, "Montage_Base");
            if (baseSprite != null)
            {
                SetPrivateSprite("m_montageBase", baseSprite);
                baked.Add("살");
            }

            // ③ 형태 미상 머리 — 머리 프롭을 쓰지 않고 두상에서 뽑는다 (BuildUnknownHair 참고)
            Sprite unknownHair = SavePixels(BuildUnknownHair(basePixels), "Montage_HairUnknown");
            if (unknownHair != null)
            {
                SetPrivateSprite("m_montageUnknownHair", unknownHair);
                baked.Add("형태 미상 머리");
            }

            // ④ 프롭 레이어 — 바디를 끄지 않고 검정으로 남긴다.
            //    끄면 머리 뒤에 가려야 할 뒷머리·모자 뒤통수까지 찍혀서 얼굴 위를 덮는다.
            //    검정으로 두면 가림(depth)은 그대로 살고, 평면 렌더의 밝기로 프롭만 오려낼 수 있다.
            ApplyMaterial(bodyRenderers, occluder);
            light.enabled = true;

            foreach (AppearanceAxis axis in PropAxes())
            {
                int count = m_database.GetOptionCount(axis);
                for (int i = 0; i < count; i++)
                {
                    AppearanceDatabase.AppearanceOption option = m_database.GetOption(axis, i);
                    if (option?.PropPrefab == null)
                        continue;

                    Sprite sprite = SavePixels(BakeProp(camera, head, option, axis), $"Montage_{axis}_{i}");
                    if (sprite == null)
                        continue;

                    option.MontageLayer = sprite;
                    baked.Add($"{axis}[{i}]");
                }
            }

            EditorUtility.SetDirty(m_database);
            AssetDatabase.SaveAssets();
        }
        finally
        {
            DestroyImmediate(rig);
            if (occluder != null)
                DestroyImmediate(occluder);
        }

        Debug.Log($"[몽타주] 레이어 {baked.Count}장 구움 → {m_outputFolder}\n  {string.Join(", ", baked)}");
    }

    private static IEnumerable<AppearanceAxis> PropAxes()
    {
        yield return AppearanceAxis.HairStyle;
        yield return AppearanceAxis.FacialHair;
        yield return AppearanceAxis.Headwear;
        yield return AppearanceAxis.Eyewear;
    }

    /// <summary>
    /// 프롭 레이어 한 장 — 실루엣과 색을 따로 렌더해 합친다.
    ///
    /// 머리스타일만 흰 실루엣이다(머리색 축이 칠할 자리라). 나머지 프롭은 <b>실제 머티리얼에 옵션 색까지
    /// 얹어</b> 굽는다 — 노랑·검정 고글처럼 두 색으로 된 프롭을 단색 틴트로 칠하면 화면과 어긋난다.
    /// 색을 실물로 구우면 바디(검정)와 밝기로 구분할 수 없으므로, 같은 프롭을 흰색으로 한 번 더 찍어
    /// 그것을 오려내는 마스크로 쓴다.
    /// </summary>
    private Color[] BakeProp(Camera camera, Transform head, AppearanceDatabase.AppearanceOption option, AppearanceAxis axis)
    {
        bool silhouette = axis == AppearanceAxis.HairStyle;

        Color[] color = null;
        if (!silhouette)
        {
            GameObject lit = InstantiateProp(option.PropPrefab, head, null);
            TintProp(lit, option.Color);
            color = RenderPixels(camera);
            DestroyImmediate(lit);
        }

        GameObject flat = InstantiateProp(option.PropPrefab, head, m_flatMaterial);
        Color[] mask = RenderPixels(camera);
        DestroyImmediate(flat);

        return CutOutProp(mask, color);
    }

    // 실제 NPC와 같은 방식으로 붙여야 위치가 어긋나지 않는다 (NpcAppearance.ApplyPropAxis와 동일)
    private static GameObject InstantiateProp(GameObject prefab, Transform head, Material material)
    {
        GameObject prop = (GameObject)PrefabUtility.InstantiatePrefab(prefab, head);
        prop.transform.localPosition = Vector3.zero;
        prop.transform.localRotation = Quaternion.identity;
        prop.transform.localScale = Vector3.one;

        if (material != null)
            ApplyMaterial(prop.GetComponentsInChildren<Renderer>(true), material);
        return prop;
    }

    // NpcAppearance.TintRenderers와 같은 방식 — 공유 머티리얼을 건드리지 않는다
    private static void TintProp(GameObject prop, Color color)
    {
        var block = new MaterialPropertyBlock();
        foreach (Renderer renderer in prop.GetComponentsInChildren<Renderer>(true))
        {
            renderer.GetPropertyBlock(block);
            block.SetColor("_BaseColor", color);
            renderer.SetPropertyBlock(block);
        }
    }

    private static void ApplyMaterial(Renderer[] renderers, Material material)
    {
        foreach (Renderer renderer in renderers)
        {
            if (renderer == null)
                continue;

            var materials = new Material[renderer.sharedMaterials.Length];
            for (int i = 0; i < materials.Length; i++)
                materials[i] = material;
            renderer.sharedMaterials = materials;
        }
    }

    private Camera CreateCamera(Transform parent, Transform mannequin, Transform head)
    {
        var go = new GameObject("~BakeCamera");
        go.transform.SetParent(parent, false);

        Camera camera = go.AddComponent<Camera>();
        camera.orthographic = true;
        camera.orthographicSize = m_orthoSize;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.nearClipPlane = 0.01f;
        camera.farClipPlane = m_cameraDistance * 4f;
        camera.enabled = false; // Render()로만 돈다

        Vector3 focus = head.position + Vector3.up * m_headOffset;
        go.transform.position = focus + mannequin.forward * m_cameraDistance;
        go.transform.rotation = Quaternion.LookRotation(focus - go.transform.position, Vector3.up);
        return camera;
    }

    private static Light CreateLight(Transform parent, Transform mannequin)
    {
        var go = new GameObject("~BakeLight");
        go.transform.SetParent(parent, false);
        go.transform.rotation = Quaternion.LookRotation(-mannequin.forward + Vector3.down * 0.35f);

        Light light = go.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.2f;
        return light;
    }

    /// <summary>
    /// 한 장 렌더해 픽셀로 돌려준다.
    /// 배경을 흰색·검정 두 번 찍어 알파를 역산한다 — URP는 불투명 패스가 알파를 그대로 두지 않아
    /// 투명 배경으로 한 번 찍는 방식이 파이프라인 설정에 따라 통째로 불투명하게 나온다.
    /// </summary>
    private Color[] RenderPixels(Camera camera)
    {
        int size = m_resolution;
        var rt = new RenderTexture(size, size, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 };
        Texture2D onWhite = Capture(camera, rt, Color.white);
        Texture2D onBlack = Capture(camera, rt, Color.black);

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
        rt.Release();
        DestroyImmediate(rt);
        return pixels;
    }

    /// <summary>
    /// 조명 렌더에서 이목구비만 남긴다 — 얼굴 밝기의 중앙값을 피부로 보고, 그보다 문턱만큼 어두운
    /// 픽셀의 알파만 남긴다. 눈·눈썹·입은 피부보다 훨씬 어두워 살아남고 완만한 명암은 걸러진다.
    /// </summary>
    private Color[] ExtractFeatures(Color[] pixels)
    {
        var luminances = new List<float>(pixels.Length);
        foreach (Color pixel in pixels)
        {
            if (pixel.a > 0.5f)
                luminances.Add(Luminance(pixel));
        }

        if (luminances.Count == 0)
            return pixels;

        luminances.Sort();
        float skin = luminances[luminances.Count / 2];
        if (skin <= 0.001f)
            return pixels;

        var result = new Color[pixels.Length];
        for (int i = 0; i < pixels.Length; i++)
        {
            // 문턱 바로 위에서 불투명해지게 좁은 경계를 쓴다 — 위쪽 끝을 완전 검정(1)으로 잡으면
            // 눈·입의 어두움이 0.5~0.8이라 늘 반투명하게 나오고, 문턱은 농도만 흔드는 노브가 된다
            float darkness = (skin - Luminance(pixels[i])) / skin;
            float alpha = pixels[i].a * Mathf.InverseLerp(m_featureThreshold, m_featureThreshold + k_featureSoftness, darkness);
            result[i] = alpha <= 0.004f
                ? Color.clear
                : new Color(pixels[i].r, pixels[i].g, pixels[i].b, alpha);
        }
        return result;
    }

    private static float Luminance(Color color) => 0.2126f * color.r + 0.7152f * color.g + 0.0722f * color.b;

    /// <summary>
    /// 형태 미상 머리 — 머리 스타일이 미공개일 때 까는 레이어. 머리색을 얹을 자리를 만들되 어느 스타일도
    /// 지목하지 않아야 한다. 살 실루엣에서 정수리 쪽 일부를 떼어 캡으로 쓴다.
    ///
    /// 머리 프롭을 전부 겹친 합집합으로 굽던 것을 바꾼 것이다 — 합집합은 정의상 가장 바깥 포락선이라
    /// 긴머리 외곽선을 그대로 물려받고, 미상이 장발이라는 말한 적 없는 정보를 사칭했다. 정수리 캡은
    /// 어느 스타일에나 공통이라 길이를 주장하지 않는다.
    ///
    /// 캡은 채우지 않고 <b>윤곽만</b> 남긴다 — 통짜로 칠하면 이번엔 짧은머리 하나로 읽힌다. 바깥 한 겹은
    /// 체크무늬로 솎아 윤곽까지 흐린다(미공개 색을 반투명으로 두는 것과 같은 취지 — 농도로 말한다).
    /// 윤곽의 아랫변은 뺀다: 이마를 가로지르는 띠는 모자·이마밴드로 읽혀 Headwear 축과 섞이고,
    /// 머리 레이어가 이목구비 위에 깔리는 탓에 눈썹까지 덮는다.
    /// </summary>
    private Color[] BuildUnknownHair(Color[] basePixels)
    {
        int size = m_resolution;
        var head = new bool[basePixels.Length];
        int top = -1;
        int bottom = -1;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                if (basePixels[y * size + x].a <= 0.5f)
                    continue;

                head[y * size + x] = true;
                if (bottom < 0)
                    bottom = y;
                top = y;
            }
        }

        if (top < 0)
        {
            Debug.LogWarning("MontageLayerBaker: 살 레이어가 비어 형태 미상 머리를 만들 수 없다");
            return new Color[basePixels.Length];
        }

        // 깊이는 두상 높이의 비율로 잡는다 — 해상도를 바꿔도 캡이 이마 아래로 내려오지 않는다
        int depth = Mathf.Max(2, Mathf.RoundToInt((top - bottom + 1) * k_unknownHairCapRatio));

        int capBottom = Mathf.Max(0, top - depth + 1);
        var cap = new bool[basePixels.Length];
        for (int y = capBottom; y <= top; y++)
        {
            for (int x = 0; x < size; x++)
                cap[y * size + x] = head[y * size + x];
        }

        // 굽는 크기에 비례해 부풀려야 해상도를 올려도 윤곽 두께가 같아 보인다
        bool[] grown = (bool[])cap.Clone();
        int thickness = Mathf.Max(1, Mathf.RoundToInt(size / 16f));
        for (int step = 0; step < thickness; step++)
        {
            var next = (bool[])grown.Clone();
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    if (!grown[y * size + x])
                        continue;

                    if (y + 1 < size)
                        next[(y + 1) * size + x] = true;
                    if (x > 0)
                        next[y * size + x - 1] = true;
                    if (x + 1 < size)
                        next[y * size + x + 1] = true;
                }
            }
            grown = next;
        }

        bool[] capEdge = EdgeOf(cap, size);
        bool[] grownEdge = EdgeOf(grown, size);

        var result = new Color[basePixels.Length];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int i = y * size + x;
                bool outline = capEdge[i] && y > capBottom;
                bool fuzz = grownEdge[i] && ((x + y) & 1) == 0;
                result[i] = outline || fuzz ? Color.white : Color.clear; // 머리색으로 칠할 것이므로 흰색이다
            }
        }
        return result;
    }

    /// <summary>마스크의 테두리 한 겹 — 바깥과 맞닿은 픽셀만 남긴다.</summary>
    private static bool[] EdgeOf(bool[] mask, int size)
    {
        var edge = new bool[mask.Length];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                if (!mask[y * size + x])
                    continue;

                edge[y * size + x] =
                    x == 0
                    || x + 1 == size
                    || y == 0
                    || y + 1 == size
                    || !mask[y * size + x - 1]
                    || !mask[y * size + x + 1]
                    || !mask[(y - 1) * size + x]
                    || !mask[(y + 1) * size + x];
            }
        }
        return edge;
    }

    /// <summary>
    /// 흰색 렌더(mask)에서 <b>바디에 가려지지 않은</b> 부분만 오려낸다 — 바디는 검정으로 찍혀 오므로
    /// 밝은 픽셀이 곧 보이는 프롭이다. 머리 뒤로 넘어간 뒷머리·모자 뒤통수는 바디에 가려 걷힌다.
    /// color를 주면 그 색을, 안 주면 흰색(표시할 때 칠할 실루엣)을 쓴다.
    /// </summary>
    private static Color[] CutOutProp(Color[] mask, Color[] color)
    {
        var result = new Color[mask.Length];
        for (int i = 0; i < mask.Length; i++)
        {
            float alpha = Luminance(mask[i]) >= 0.5f ? mask[i].a : 0f;
            if (alpha <= 0.004f)
            {
                result[i] = Color.clear;
                continue;
            }

            result[i] = color != null
                ? new Color(color[i].r, color[i].g, color[i].b, alpha)
                : new Color(1f, 1f, 1f, alpha);
        }
        return result;
    }

    private Sprite SavePixels(Color[] pixels, string fileName)
    {
        var texture = new Texture2D(m_resolution, m_resolution, TextureFormat.RGBA32, false);
        texture.SetPixels(pixels);
        texture.Apply();

        string path = $"{m_outputFolder}/{fileName}.png";
        File.WriteAllBytes(path, texture.EncodeToPNG());
        DestroyImmediate(texture);

        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        ApplyImportSettings(path);
        return AssetDatabase.LoadAssetAtPath<Sprite>(path);
    }

    private static Texture2D Capture(Camera camera, RenderTexture rt, Color background)
    {
        camera.backgroundColor = background;
        camera.targetTexture = rt;
        camera.Render();
        camera.targetTexture = null;

        RenderTexture previous = RenderTexture.active;
        RenderTexture.active = rt;
        var texture = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
        texture.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        texture.Apply();
        RenderTexture.active = previous;
        return texture;
    }

    // 픽셀 몽타주라 확대해도 뭉개지지 않게 Point 필터·무압축으로 둔다
    private static void ApplyImportSettings(string path)
    {
        var importer = (TextureImporter)AssetImporter.GetAtPath(path);
        if (importer == null)
            return;

        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Single;
        importer.filterMode = FilterMode.Point;
        importer.mipmapEnabled = false;
        importer.alphaIsTransparency = true;
        importer.textureCompression = TextureImporterCompression.Uncompressed;
        importer.SaveAndReimport();
    }

    // 베이스·미상 머리는 옵션이 아니라 DB의 private 필드라 SerializedObject로 넣는다
    private void SetPrivateSprite(string fieldName, Sprite sprite)
    {
        var serialized = new SerializedObject(m_database);
        SerializedProperty property = serialized.FindProperty(fieldName);
        if (property == null)
        {
            Debug.LogWarning($"MontageLayerBaker: AppearanceDatabase에 {fieldName} 필드가 없다");
            return;
        }

        property.objectReferenceValue = sprite;
        serialized.ApplyModifiedProperties();
    }

    private static Transform ResolveHead(Transform root)
    {
        Animator animator = root.GetComponentInChildren<Animator>();
        if (animator != null && animator.isHuman)
        {
            Transform bone = animator.GetBoneTransform(HumanBodyBones.Head);
            if (bone != null)
                return bone;
        }

        foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
        {
            if (child.name.Contains("Head"))
                return child;
        }
        return null;
    }
}
