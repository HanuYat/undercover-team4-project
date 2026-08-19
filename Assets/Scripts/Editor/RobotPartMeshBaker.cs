using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 로봇 메시를 부위별 서브메시 3개로 갈라 굽는다 (#432). 원본은 서브메시가 하나라 몸 전체가
/// 한 색으로만 칠해진다 — 삼각형을 <see cref="EBodyPart"/>별로 나눠 담으면
/// <c>SetPropertyBlock(block, materialIndex)</c>로 부위마다 다른 색을 넣을 수 있다.
///
/// UV 마스크로는 못 나눈다 — Synty 아틀라스는 UV 섬을 부위끼리 재사용한다. 그래서 본으로 나눈다.
/// 서브메시 순서 = EBodyPart 순서. 결과는 Assets/Meshes/에 새 에셋으로 나가고 원본(Imported)은 건드리지 않는다.
/// </summary>
public static class RobotPartMeshBaker
{
    private const string k_sourcePath = "Assets/Imported/Synty/PolygonGeneric/Prefabs/Characters/SM_Gen_Chr_Robot_01.prefab";
    private const string k_outputPath = "Assets/Meshes/SM_Gen_Chr_Robot_01_Parts.asset";

    // 본 이름 앞부분으로 부위를 정한다 — 목록에 없는 본은 상체로 본다
    private static readonly string[] s_headBones = { "Head", "Neck", "Eye", "Jaw" };
    private static readonly string[] s_legBones = { "Hips", "UpperLeg", "LowerLeg", "Ankle", "Ball", "Toe" };

    [MenuItem("Tools/Undercover/로봇 부위 메시 굽기 (#432)")]
    public static void Bake()
    {
        var source = AssetDatabase.LoadAssetAtPath<GameObject>(k_sourcePath);
        if (source == null)
        {
            Debug.LogError($"[{nameof(RobotPartMeshBaker)}] 원본 프리팹을 찾지 못했습니다: {k_sourcePath}");
            return;
        }

        SkinnedMeshRenderer renderer = source.GetComponentInChildren<SkinnedMeshRenderer>();
        Mesh mesh = renderer != null ? renderer.sharedMesh : null;
        if (mesh == null)
        {
            Debug.LogError($"[{nameof(RobotPartMeshBaker)}] 스킨드 메시를 찾지 못했습니다.");
            return;
        }

        Mesh baked = Object.Instantiate(mesh);
        baked.name = "SM_Gen_Chr_Robot_01_Parts";

        EBodyPart[] vertexParts = ClassifyVertices(mesh, renderer.bones);
        int[] triangles = mesh.triangles;

        var buckets = new List<int>[3];
        for (int i = 0; i < buckets.Length; i++)
            buckets[i] = new List<int>();

        // 삼각형은 정점 셋의 다수결로 정한다 — 경계에서 한 조각이 옆 부위로 넘어가도 티가 나지 않는다
        for (int t = 0; t < triangles.Length; t += 3)
        {
            EBodyPart part = MajorityPart(
                vertexParts[triangles[t]],
                vertexParts[triangles[t + 1]],
                vertexParts[triangles[t + 2]]
            );

            List<int> bucket = buckets[(int)part];
            bucket.Add(triangles[t]);
            bucket.Add(triangles[t + 1]);
            bucket.Add(triangles[t + 2]);
        }

        baked.subMeshCount = buckets.Length;
        for (int i = 0; i < buckets.Length; i++)
            baked.SetTriangles(buckets[i], i);

        if (!AssetDatabase.IsValidFolder("Assets/Meshes"))
            AssetDatabase.CreateFolder("Assets", "Meshes");

        AssetDatabase.DeleteAsset(k_outputPath);
        AssetDatabase.CreateAsset(baked, k_outputPath);
        AssetDatabase.SaveAssets();

        Debug.Log(
            $"[{nameof(RobotPartMeshBaker)}] {k_outputPath} — 머리 {buckets[0].Count / 3} · "
                + $"상체 {buckets[1].Count / 3} · 하체 {buckets[2].Count / 3} 삼각형"
        );
    }

    private static EBodyPart[] ClassifyVertices(Mesh mesh, Transform[] bones)
    {
        BoneWeight[] weights = mesh.boneWeights;
        var parts = new EBodyPart[mesh.vertexCount];

        for (int i = 0; i < parts.Length; i++)
        {
            Transform bone = weights.Length > i ? bones[weights[i].boneIndex0] : null;
            parts[i] = PartOf(bone != null ? bone.name : string.Empty);
        }

        return parts;
    }

    private static EBodyPart PartOf(string boneName)
    {
        foreach (string prefix in s_headBones)
        {
            if (boneName.StartsWith(prefix))
                return EBodyPart.Head;
        }

        foreach (string prefix in s_legBones)
        {
            if (boneName.StartsWith(prefix))
                return EBodyPart.Legs;
        }

        return EBodyPart.Torso;
    }

    private static EBodyPart MajorityPart(EBodyPart a, EBodyPart b, EBodyPart c)
    {
        if (a == b || a == c)
            return a;

        return b == c ? b : a;
    }
}
