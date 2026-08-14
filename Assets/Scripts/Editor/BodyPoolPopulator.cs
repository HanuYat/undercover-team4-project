using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 바디 풀 배선 (#619, docs §13-17) — 메뉴: Tools/바디 풀 배선
///
/// Apocalypse·PoliceStation 팩 바디를 <c>NPC_Citizen_Generic</c>의 Model 아래에 Generic 스켈레톤으로
/// 다시 바인딩해 붙인다. 옷은 몽타주 축이 아니라 화면 다양성만 오른다.
///
/// 팩 프리팹 하나에 그 팩 바디 전체가 한 스켈레톤 아래 들어 있어(우리 프리팹과 같은 꼴) 팩마다
/// 아무 프리팹 하나를 기증자로 열면 된다. 그래서 표에 적는 것은 <b>넣을 것이 아니라 뺄 것</b>이다.
/// 다시 돌려도 안전하다 — <c>SM_Gen_</c>이 아닌 바디를 걷어내고 새로 붙인다.
/// </summary>
public static class BodyPoolPopulator
{
    private const string k_targetPrefab = "Assets/Prefabs/NPC/NPC_Citizen_Generic.prefab";

    // 원래 바디의 접두사 — 이게 아니면 이 툴이 붙인 것으로 보고 걷어낸다
    private const string k_genericPrefix = "SM_Gen_";

    private readonly struct Pack
    {
        public readonly string DonorPrefab;

        /// <summary>붙인 바디 이름의 접두사 — 두 팩에 같은 이름(SM_Chr_Criminal_Male_01)이 있어 갈라 둔다.</summary>
        public readonly string Rename;

        public readonly string[] Excluded;

        public Pack(string donorPrefab, string rename, params string[] excluded)
        {
            DonorPrefab = donorPrefab;
            Rename = rename;
            Excluded = excluded;
        }
    }

    private static readonly Pack[] s_packs =
    {
        new Pack(
            "Assets/Imported/Synty/PolygonApocalypse/Prefabs/Characters/SM_Chr_Biker_Male_01.prefab",
            "SM_Apo_",
            // 좀비는 비인간이라 뺀다 — 도시 시민으로 섞이면 톤이 깨진다
            "SM_Chr_Zombie_Male_01",
            "SM_Chr_Zombie_Male_02",
            "SM_Chr_Zombie_Female_01",
            "SM_Chr_Zombie_Female_02"
        ),
        new Pack(
            "Assets/Imported/Synty/PolygonPoliceStation/Prefabs/Characters/SM_Chr_Officer_Male_01.prefab",
            "SM_Pol_"
            // 옷에 POLICE 글자가 있는 바디를 여기 적어 뺀다 — 글자는 아틀라스에 구워져 있어
            // 코드로는 못 갈라내니 붙인 뒤 눈으로 보고 채운다
        ),
    };

    [MenuItem("Tools/바디 풀 배선")]
    private static void Populate()
    {
        GameObject root = PrefabUtility.LoadPrefabContents(k_targetPrefab);
        if (root == null)
        {
            Debug.LogError($"[바디 풀] {k_targetPrefab}을 열지 못했다");
            return;
        }

        try
        {
            Transform model = root.transform.Find("Model");
            Transform skeleton = model != null ? model.Find("Root") : null;
            if (skeleton == null)
            {
                Debug.LogError("[바디 풀] Model/Root 스켈레톤을 찾지 못했다 — 프리팹 계층이 바뀌었는지 확인할 것");
                return;
            }

            int removed = RemoveForeignBodies(model);

            var bones = new Dictionary<string, Transform>();
            foreach (Transform bone in skeleton.GetComponentsInChildren<Transform>(true))
                bones[bone.name] = bone;

            var missing = new List<string>();
            var log = new System.Text.StringBuilder();
            int added = 0;
            foreach (Pack pack in s_packs)
                added += AddPack(pack, model, skeleton, bones, missing, log);

            if (missing.Count > 0)
            {
                Debug.LogError(
                    $"[바디 풀] Generic 스켈레톤에서 짝을 못 찾은 본 {missing.Count}개 — 배선 중단\n  {string.Join("\n  ", missing)}"
                );
                return;
            }

            PrefabUtility.SaveAsPrefabAsset(root, k_targetPrefab);
            Debug.Log(
                $"[바디 풀] 팩 바디 {added}개 배선 완료 (이전 {removed}개 걷어냄){log}"
            );
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    /// <summary>지난번에 붙인 팩 바디를 걷어낸다 — 원래 바디(<c>SM_Gen_</c>)는 건드리지 않는다.</summary>
    private static int RemoveForeignBodies(Transform model)
    {
        var doomed = new List<GameObject>();
        foreach (SkinnedMeshRenderer body in model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (!body.name.StartsWith(k_genericPrefix))
                doomed.Add(body.gameObject);
        }

        foreach (GameObject go in doomed)
            Object.DestroyImmediate(go);
        return doomed.Count;
    }

    private static int AddPack(
        in Pack pack,
        Transform model,
        Transform skeleton,
        Dictionary<string, Transform> bones,
        List<string> missing,
        System.Text.StringBuilder log
    )
    {
        GameObject donor = AssetDatabase.LoadAssetAtPath<GameObject>(pack.DonorPrefab);
        if (donor == null)
        {
            missing.Add($"기증자 프리팹 없음: {pack.DonorPrefab}");
            return 0;
        }

        var excluded = new HashSet<string>(pack.Excluded);
        int added = 0;
        foreach (SkinnedMeshRenderer source in donor.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (source.sharedMesh == null || excluded.Contains(source.name))
                continue;

            string name = pack.Rename + StripPrefix(source.name);
            var go = new GameObject(name);
            go.transform.SetParent(model, false);
            go.layer = model.gameObject.layer;

            SkinnedMeshRenderer body = go.AddComponent<SkinnedMeshRenderer>();
            body.sharedMesh = source.sharedMesh;
            body.sharedMaterials = source.sharedMaterials;
            body.quality = source.quality;
            body.updateWhenOffscreen = source.updateWhenOffscreen;
            body.rootBone =
                source.rootBone != null && bones.TryGetValue(source.rootBone.name, out Transform packRoot)
                    ? packRoot
                    : skeleton;
            body.localBounds = source.localBounds;

            Transform[] sourceBones = source.bones;
            var mapped = new Transform[sourceBones.Length];
            for (int i = 0; i < sourceBones.Length; i++)
                mapped[i] = ResolveBone(sourceBones[i], bones, name, missing);
            body.bones = mapped;

            go.SetActive(false); // 하나를 켜는 것은 NpcAppearance.ApplyModel이 한다
            added++;
            log.Append("\n  ").Append(name);
        }
        return added;
    }

    /// <summary>
    /// 팩 본에 대응하는 Generic 본. 이름이 같으면 그대로고, 없으면 좌우를 계층으로 판정한다 —
    /// Apocalypse 손가락만 이름에 좌우가 없어(<c>Finger_01</c>/<c>Finger_01 1</c>) 조상
    /// <c>Hand_L</c>/<c>Hand_R</c>에서 접미사를 가져온다.
    /// </summary>
    private static Transform ResolveBone(
        Transform sourceBone,
        Dictionary<string, Transform> bones,
        string bodyName,
        List<string> missing
    )
    {
        if (sourceBone == null)
            return null;

        if (bones.TryGetValue(sourceBone.name, out Transform direct))
            return direct;

        for (Transform t = sourceBone; t != null; t = t.parent)
        {
            if (!t.name.StartsWith("Hand_"))
                continue;

            string sided = StripDuplicateSuffix(sourceBone.name) + t.name.Substring(t.name.Length - 2);
            if (bones.TryGetValue(sided, out Transform resolved))
                return resolved;
            break;
        }

        missing.Add($"{bodyName}: {sourceBone.name}");
        return null;
    }

    /// <summary>"Finger_01 1" → "Finger_01" (유니티가 중복 이름에 붙인 꼬리).</summary>
    private static string StripDuplicateSuffix(string name)
    {
        int space = name.IndexOf(' ');
        return space >= 0 ? name.Substring(0, space) : name;
    }

    /// <summary>"SM_Chr_Biker_Male_01" → "Chr_Biker_Male_01" — 팩 접두사 자리를 비운다.</summary>
    private static string StripPrefix(string name) =>
        name.StartsWith("SM_") ? name.Substring("SM_".Length) : name;
}
