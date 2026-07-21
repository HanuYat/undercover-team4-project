using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// 1인칭 FP 팔 메시·프리팹을 현재 플레이어 모델의 오른팔에서 다시 뽑는 에디터 툴. (#265)
/// 지금 팔은 임시 캐릭터 모델에서 추출한 것이라, 정식 모델로 교체하면 이 메뉴로 재생성한다.
/// 메뉴: Tools/FP Arm/Regenerate Right Arm From Player Model
///
/// 재생성은 메시 에셋과 FPArm_Right.prefab만 갈아끼운다 — Player.prefab의 배선(m_handsModel 참조,
/// 카메라, 화면 위치)은 건드리지 않는다. PlayerHandView가 Viewmodel 레이어와 아이템 앵커·손가락 본을
/// 런타임에 다시 잡으므로, 팔 프리팹 내부 구조가 바뀌어도 재배선은 필요 없다.
/// </summary>
public static class FPArmGenerator
{
    private const string k_playerPrefabPath = "Assets/Prefabs/Player.prefab";
    private const string k_dir = "Assets/Prefabs/FPArm";
    private const string k_meshPath = k_dir + "/FPArm_Right_Mesh.asset";
    private const string k_armPrefabPath = k_dir + "/FPArm_Right.prefab";
    private const string k_shoulderBone = "Shoulder_R"; // 이 본 하위 전체를 '오른팔'로 본다 (Synty 리그)

    [MenuItem("Tools/FP Arm/Regenerate Right Arm From Player Model")]
    public static void Regenerate()
    {
        GameObject player = AssetDatabase.LoadAssetAtPath<GameObject>(k_playerPrefabPath);
        if (player == null)
        {
            Debug.LogError($"[FPArmGenerator] {k_playerPrefabPath} 를 찾지 못했다.");
            return;
        }

        SkinnedMeshRenderer body = FindBodyRenderer(player);
        if (body == null || body.sharedMesh == null)
        {
            Debug.LogError(
                "[FPArmGenerator] 플레이어 몸 SkinnedMeshRenderer를 찾지 못했다 (FPArm_Right 바깥의 SMR)."
            );
            return;
        }

        // 메시 에셋은 GUID를 유지해야 프리팹 SMR 참조가 안 끊긴다 — 기존 에셋이 있으면 내용만 채운다.
        bool isNewMesh = false;
        Mesh armMesh = AssetDatabase.LoadAssetAtPath<Mesh>(k_meshPath);
        if (armMesh == null)
        {
            armMesh = new Mesh();
            isNewMesh = true;
        }
        armMesh.name = "FPArm_Right_Mesh";

        if (!FillArmMesh(body, armMesh))
        {
            if (isNewMesh)
            {
                Object.DestroyImmediate(armMesh);
            }
            return; // FillArmMesh가 실패 이유를 로그로 남긴다
        }

        if (!AssetDatabase.IsValidFolder(k_dir))
        {
            AssetDatabase.CreateFolder("Assets/Prefabs", "FPArm");
        }
        if (isNewMesh)
        {
            AssetDatabase.CreateAsset(armMesh, k_meshPath);
        }
        AssetDatabase.SaveAssets();

        BuildArmPrefab(player, armMesh);

        Debug.Log(
            $"[FPArmGenerator] 재생성 완료 — verts={armMesh.vertexCount}, tris={armMesh.triangles.Length / 3}. "
                + $"{k_armPrefabPath} / {k_meshPath}"
        );
    }

    // FPArm_Right 하위가 아닌 첫 SkinnedMeshRenderer = 플레이어 몸. (FP 팔 자신을 다시 뽑지 않게 제외)
    private static SkinnedMeshRenderer FindBodyRenderer(GameObject root)
    {
        foreach (SkinnedMeshRenderer smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            bool underFPArm = false;
            for (Transform t = smr.transform; t != null; t = t.parent)
            {
                if (t.name == "FPArm_Right")
                {
                    underFPArm = true;
                    break;
                }
            }
            if (!underFPArm)
            {
                return smr;
            }
        }
        return null;
    }

    // 몸 메시에서 Shoulder_R 하위 본에 지배적으로 가중된 삼각형만 골라 팔 메시를 target에 채운다.
    private static bool FillArmMesh(SkinnedMeshRenderer body, Mesh target)
    {
        Mesh src = body.sharedMesh;
        Transform[] bones = body.bones;
        Transform shoulder = System.Array.Find(bones, b => b != null && b.name == k_shoulderBone);
        if (shoulder == null)
        {
            Debug.LogError(
                $"[FPArmGenerator] 리그에서 {k_shoulderBone} 본을 찾지 못했다. 다른 리그면 k_shoulderBone을 맞춰라."
            );
            return false;
        }

        var armSet = new HashSet<Transform>(shoulder.GetComponentsInChildren<Transform>());
        var armBoneIndices = new HashSet<int>();
        for (int i = 0; i < bones.Length; i++)
        {
            if (bones[i] != null && armSet.Contains(bones[i]))
            {
                armBoneIndices.Add(i);
            }
        }

        BoneWeight[] weights = src.boneWeights;
        bool[] isArm = new bool[src.vertexCount];
        for (int v = 0; v < src.vertexCount; v++)
        {
            BoneWeight w = weights[v];
            int dominant = w.boneIndex0;
            float max = w.weight0;
            if (w.weight1 > max)
            {
                max = w.weight1;
                dominant = w.boneIndex1;
            }
            if (w.weight2 > max)
            {
                max = w.weight2;
                dominant = w.boneIndex2;
            }
            if (w.weight3 > max)
            {
                dominant = w.boneIndex3;
            }
            isArm[v] = armBoneIndices.Contains(dominant);
        }

        Vector3[] verts = src.vertices;
        Vector3[] normals = src.normals;
        Vector4[] tangents = src.tangents;
        Vector2[] uvs = src.uv;
        bool hasNormals = normals != null && normals.Length == src.vertexCount;
        bool hasTangents = tangents != null && tangents.Length == src.vertexCount;
        bool hasUv = uvs != null && uvs.Length == src.vertexCount;

        var remap = new Dictionary<int, int>();
        var newVerts = new List<Vector3>();
        var newNormals = new List<Vector3>();
        var newTangents = new List<Vector4>();
        var newUvs = new List<Vector2>();
        var newWeights = new List<BoneWeight>();
        var newTris = new List<int>();

        for (int s = 0; s < src.subMeshCount; s++)
        {
            int[] tris = src.GetTriangles(s);
            for (int t = 0; t < tris.Length; t += 3)
            {
                int a = tris[t],
                    b = tris[t + 1],
                    c = tris[t + 2];
                if (!isArm[a] || !isArm[b] || !isArm[c])
                {
                    continue; // 세 꼭짓점이 모두 팔일 때만 — 어깨 이음새의 늘어진 삼각형 배제
                }
                foreach (int oldIndex in new[] { a, b, c })
                {
                    if (!remap.TryGetValue(oldIndex, out int newIndex))
                    {
                        newIndex = newVerts.Count;
                        remap[oldIndex] = newIndex;
                        newVerts.Add(verts[oldIndex]);
                        if (hasNormals)
                            newNormals.Add(normals[oldIndex]);
                        if (hasTangents)
                            newTangents.Add(tangents[oldIndex]);
                        if (hasUv)
                            newUvs.Add(uvs[oldIndex]);
                        newWeights.Add(weights[oldIndex]);
                    }
                    newTris.Add(newIndex);
                }
            }
        }

        if (newTris.Count == 0)
        {
            Debug.LogError("[FPArmGenerator] 팔 삼각형이 0개다 — 본 이름·스킨 가중치를 확인하라.");
            return false;
        }

        target.Clear();
        target.indexFormat = IndexFormat.UInt16;
        target.SetVertices(newVerts);
        if (hasNormals)
            target.SetNormals(newNormals);
        if (hasTangents)
            target.SetTangents(newTangents);
        if (hasUv)
            target.SetUVs(0, newUvs);
        target.boneWeights = newWeights.ToArray();
        target.bindposes = src.bindposes; // boneWeights가 원본 본 인덱스를 참조하므로 전체 bindpose 유지
        target.SetTriangles(newTris, 0);
        if (!hasNormals)
            target.RecalculateNormals();
        target.RecalculateBounds();
        return true;
    }

    // 몸 리그 스켈레톤 + 팔 메시만 남긴 정적 뷰모델 프리팹을 만든다 (애니메이터·스크립트 없음).
    private static void BuildArmPrefab(GameObject player, Mesh armMesh)
    {
        GameObject clone = Object.Instantiate(player);
        clone.name = "TMP_FPArmSource";
        try
        {
            SkinnedMeshRenderer arm = FindBodyRenderer(clone);
            arm.sharedMesh = armMesh;
            arm.shadowCastingMode = ShadowCastingMode.Off; // 뷰모델은 월드에 그림자 드리우지 않는다
            arm.receiveShadows = false;

            // 스켈레톤 루트(플레이어 루트의 직속 본 자식)와 팔 SMR만 새 루트로 옮긴다.
            Transform skeletonTop = arm.rootBone;
            while (skeletonTop != null && skeletonTop.parent != clone.transform)
            {
                skeletonTop = skeletonTop.parent;
            }
            if (skeletonTop == null)
            {
                Debug.LogError(
                    "[FPArmGenerator] 팔 SkinnedMeshRenderer에 rootBone이 없다 — "
                        + "SkinnedMeshRenderer의 Root Bone 할당을 확인하라. 프리팹 생성을 건너뛴다."
                );
                return;
            }

            GameObject newRoot = new GameObject("FPArm_Right");
            skeletonTop.SetParent(newRoot.transform, true);
            arm.transform.SetParent(newRoot.transform, true);

            // 어깨를 원점 근처로 옮겨 카메라 하위 배치를 쉽게 한다.
            Transform shoulder = System.Array.Find(
                arm.bones,
                b => b != null && b.name == k_shoulderBone
            );
            if (shoulder != null)
            {
                skeletonTop.position -= shoulder.position;
            }

            PrefabUtility.SaveAsPrefabAsset(newRoot, k_armPrefabPath);
            Object.DestroyImmediate(newRoot);
        }
        finally
        {
            Object.DestroyImmediate(clone);
        }
    }
}
