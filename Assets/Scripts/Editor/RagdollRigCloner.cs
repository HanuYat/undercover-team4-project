using System.Collections.Generic;
using System.Text;
using Unity.Netcode.Components;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

/// <summary>
/// 완성된 래그돌 리그를 <b>같은 뼈대를 쓰는 다른 프리팹으로 복제한다</b>. (#571 5단계)
/// 메뉴: <c>Tools > Ragdoll > Clone Rig - NPC (Citizen 기준)</c>
///
/// <b><see cref="RagdollSetup"/>과 하는 일이 다르다.</b> 저쪽은 "위저드가 만든 리그의 뒷마무리"이고
/// 이쪽은 "위저드 대신"이다. 둘을 이어서 돌리는 것이 이 메뉴 하나다 — 복제 → 마무리 → <c>NpcRagdoll</c> 부착.
///
/// <b>왜 위저드를 프리팹마다 돌리지 않나.</b> 한때 "모델이 다르니 프리팹마다 위저드를 돌려야 한다"고
/// 적었는데, 재 보니 <b>Synty 캐릭터 넷이 뼈대를 공유한다</b> — <c>Model</c> 기준 뼈 로컬 좌표가
/// 네 프리팹에서 편차 <b>0.00cm</b>였다. 메시가 다른 <c>NPC_Streaker</c>
/// (<c>SM_Gen_Chr_Underwear_Male_01</c>)까지 그렇다. 뼈대가 같으면 위저드의 산출물(콜라이더 크기·
/// 관절 축·질량 분배)도 같은 값이 나오므로, 손으로 열세 칸을 다시 채우는 것은 같은 결과를 얻는
/// 더 틀리기 쉬운 방법이다.
///
/// ⚠ <b>그래서 뼈대가 같다는 것이 이 도구의 전제다.</b> 전제가 깨지면 조용히 어긋난 리그가 나오므로
/// (몸에 비해 큰 캡슐, 엉뚱한 데 걸리는 관절 한계) 복제 전에 <b>뼈마다 좌표를 대조하고, 하나라도
/// 어긋나면 그 프리팹은 아무것도 고치지 않고 건너뛴다.</b> 새 체형의 캐릭터가 들어오면 그때는
/// 위저드가 맞다 — 이 도구가 막아 준다.
/// </summary>
public static class RagdollRigCloner
{
    // 뼈 좌표 대조 허용 오차(m). 같은 뼈대면 정확히 0이 나오지만 임포트 설정 차이로 부동소수점
    // 끝자리가 갈릴 수 있어 1mm만 봐준다 — 체형이 다르면 cm 단위로 벌어지므로 이 값으로 갈린다.
    private const float k_boneMatchTolerance = 0.001f;

    [MenuItem("Tools/Ragdoll/Clone Rig - NPC (Citizen 기준)")]
    public static void CloneToNpcPrefabs()
    {
        GameObject source = PrefabUtility.LoadPrefabContents(RagdollSetup.k_npcCitizenPrefab);
        if (source == null)
        {
            Debug.LogError($"[래그돌 복제] 원본을 열 수 없다: {RagdollSetup.k_npcCitizenPrefab}");
            return;
        }

        List<string> cloned = new List<string>();
        try
        {
            Transform sourceBoneRoot = ResolveBoneRoot(source, RagdollSetup.k_npcCitizenPrefab);
            if (sourceBoneRoot == null)
                return;

            List<Transform> sourceBones = CollectRagdollBones(source, sourceBoneRoot);
            if (sourceBones.Count == 0)
            {
                Debug.LogError(
                    "[래그돌 복제] 원본에서 래그돌 뼈를 찾지 못했다 — "
                        + $"{RagdollSetup.k_npcCitizenPrefab}에 리그가 있는지 확인할 것"
                );
                return;
            }

            for (int i = 0; i < RagdollSetup.s_npcPrefabs.Length; i++)
            {
                string targetPath = RagdollSetup.s_npcPrefabs[i];
                if (targetPath == RagdollSetup.k_npcCitizenPrefab)
                    continue; // 원본 자신

                if (CloneInto(sourceBoneRoot, sourceBones, targetPath))
                    cloned.Add(targetPath);
            }
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(source);
        }

        // 마무리(레이어·물리값·RagdollRig)는 정본에 맡긴다 — 복제가 그 규칙을 두 벌 갖지 않게.
        // 복제에 성공한 프리팹만 돌린다: 건너뛴 프리팹에 돌려 봤자 뼈가 없어 에러만 한 줄 더 난다.
        for (int i = 0; i < cloned.Count; i++)
            RagdollSetup.Run(cloned[i], RagdollSetup.k_npcRigOwnerPath, stripHipsReplication: true);
    }

    // ---- 복제 ----

    /// <summary>
    /// 플레이어 프리팹 <b>안에서</b> 시체 리그(<c>Corpse/Root</c>)를 살아있는 리그(<c>Root</c>)로
    /// 복제한다 — 모델 단일화의 첫 걸음이다. (#763 2단계 B-1)
    ///
    /// 위의 NPC 복제와 다른 점은 <b>원본과 대상이 같은 프리팹</b>이라는 것뿐이다. 뼈 좌표 대조·기존
    /// 리그 제거·세 패스는 그대로 쓴다 — 두 리그가 같은 Synty 뼈대라는 전제가 깨지면 그 대조가 막는다.
    ///
    /// <b>컴포넌트를 붙이지 않는다</b> — <c>PlayerRagdoll</c>은 이미 루트에 있고, <c>RagdollRig</c>·
    /// <c>RagdollRope</c>를 옮기는 것은 손으로 하는 프리팹 작업이다(계획서 §8-2 B-2).
    ///
    /// ⚠ <b>복제만 한다.</b> 레이어·충돌 매트릭스·<c>isKinematic</c> 초기화·보간·관절 전처리는
    /// 이어서 <c>Tools > Ragdoll > Finish Setup - Player</c>가 한다.
    /// </summary>
    [MenuItem("Tools/Ragdoll/Clone Rig - Player (Corpse → 살아있는 리그)")]
    public static void CloneToPlayerLiveRig()
    {
        string path = RagdollSetup.k_playerPrefab;
        GameObject prefab = PrefabUtility.LoadPrefabContents(path);
        if (prefab == null)
        {
            Debug.LogError($"[래그돌 복제] 프리팹을 열 수 없다: {path}");
            return;
        }

        try
        {
            string boneRootName = RagdollRig.k_defaultBoneRootName;
            Transform source = prefab.transform.Find($"Corpse/{boneRootName}");
            Transform target = prefab.transform.Find(boneRootName);

            if (source == null || target == null)
            {
                Debug.LogError(
                    $"[래그돌 복제] 리그를 찾지 못했다 — 시체 리그 'Corpse/{boneRootName}'="
                        + $"{(source == null ? "없음" : "있음")}, 살아있는 리그 '{boneRootName}'="
                        + $"{(target == null ? "없음" : "있음")}. 이미 단일화됐다면 이 메뉴는 쓸 일이 없다"
                );
                return;
            }

            List<Transform> sourceBones = CollectRagdollBones(prefab, source);
            if (sourceBones.Count == 0)
            {
                Debug.LogError("[래그돌 복제] 시체 리그에서 래그돌 뼈를 찾지 못했다 — 원본이 비었다");
                return;
            }

            if (!MapBones(source, sourceBones, target, path, out List<Transform> targetBones))
                return; // 뼈대가 어긋난다 — 아무것도 고치지 않는다

            StripExistingRagdoll(targetBones);

            // 패스가 셋인 이유는 관절이 상대 Rigidbody를 요구하기 때문이다 (CloneInto와 같다).
            for (int i = 0; i < sourceBones.Count; i++)
                CopyIfPresent(sourceBones[i].GetComponent<Rigidbody>(), targetBones[i]);

            for (int i = 0; i < sourceBones.Count; i++)
                foreach (Collider collider in sourceBones[i].GetComponents<Collider>())
                    CopyIfPresent(collider, targetBones[i]);

            for (int i = 0; i < sourceBones.Count; i++)
                CopyJoint(sourceBones[i], targetBones[i], sourceBones, targetBones);

            PrefabUtility.SaveAsPrefabAsset(prefab, path);
            Debug.Log(
                $"[래그돌 복제] {path} — 시체 리그의 뼈 {sourceBones.Count}개를 살아있는 리그로 "
                    + "복제했다. 이어서 Tools > Ragdoll > Finish Setup - Player를 실행할 것"
            );
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(prefab);
        }
    }

    private static bool CloneInto(
        Transform sourceBoneRoot,
        List<Transform> sourceBones,
        string targetPath
    )
    {
        GameObject target = PrefabUtility.LoadPrefabContents(targetPath);
        if (target == null)
        {
            Debug.LogError($"[래그돌 복제] 프리팹을 열 수 없다: {targetPath}");
            return false;
        }

        try
        {
            Transform targetBoneRoot = ResolveBoneRoot(target, targetPath);
            if (targetBoneRoot == null)
                return false;

            // 대상 뼈를 원본 뼈 순서 그대로 모은다 — 아래 세 패스가 같은 인덱스를 공유한다.
            if (!MapBones(sourceBoneRoot, sourceBones, targetBoneRoot, targetPath,
                    out List<Transform> targetBones))
                return false;

            StripExistingRagdoll(targetBones);

            // 패스가 셋인 이유는 <b>관절이 상대 Rigidbody를 요구</b>하기 때문이다. 뼈 순서대로
            // 한 번에 만들면 아직 만들어지지 않은 부모를 connectedBody로 지목하게 된다.
            for (int i = 0; i < sourceBones.Count; i++)
                CopyIfPresent(sourceBones[i].GetComponent<Rigidbody>(), targetBones[i]);

            for (int i = 0; i < sourceBones.Count; i++)
                foreach (Collider collider in sourceBones[i].GetComponents<Collider>())
                    CopyIfPresent(collider, targetBones[i]);

            for (int i = 0; i < sourceBones.Count; i++)
                CopyJoint(sourceBones[i], targetBones[i], sourceBones, targetBones);

            string ragdollNote = EnsureNpcRagdoll(target);

            PrefabUtility.SaveAsPrefabAsset(target, targetPath);
            Debug.Log(
                $"[래그돌 복제] {targetPath} — 뼈 {sourceBones.Count}개 복제 완료 "
                    + $"({RagdollSetup.k_npcCitizenPrefab} 기준). {ragdollNote}"
            );
            return true;
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(target);
        }
    }

    /// <summary>
    /// 원본 뼈 하나하나에 대응하는 대상 뼈를 찾고 <b>좌표까지 대조한다</b>.
    /// 이름만 맞고 위치가 다르면 체형이 다른 것이므로 복제하면 안 된다 — 그때는 전부 중단한다.
    /// </summary>
    private static bool MapBones(
        Transform sourceBoneRoot,
        List<Transform> sourceBones,
        Transform targetBoneRoot,
        string targetPath,
        out List<Transform> targetBones
    )
    {
        targetBones = new List<Transform>(sourceBones.Count);
        List<string> problems = new List<string>();

        for (int i = 0; i < sourceBones.Count; i++)
        {
            string relative = RelativePath(sourceBoneRoot, sourceBones[i]);
            Transform match = targetBoneRoot.Find(relative);
            if (match == null)
            {
                problems.Add($"  {relative} — 대상에 없다");
                targetBones.Add(null);
                continue;
            }

            // 리그 최상단 기준 좌표로 비교한다 — 프리팹마다 Model의 월드 위치가 다르므로
            // 월드 좌표로 재면 전부 어긋난 것으로 나온다.
            Vector3 expected = sourceBoneRoot.InverseTransformPoint(sourceBones[i].position);
            Vector3 actual = targetBoneRoot.InverseTransformPoint(match.position);
            float gap = Vector3.Distance(expected, actual);
            if (gap > k_boneMatchTolerance)
                problems.Add($"  {relative} — {gap * 100f:F2}cm 어긋남");

            targetBones.Add(match);
        }

        if (problems.Count == 0)
            return true;

        Debug.LogError(
            $"[래그돌 복제] {targetPath} — 뼈대가 원본과 달라 <b>건너뛴다</b> (아무것도 고치지 않았다).\n"
                + string.Join("\n", problems)
                + "\n\n체형이 다른 캐릭터다 — 이 프리팹은 내장 Ragdoll Wizard로 리그를 직접 만들고 "
                + "Tools > Ragdoll > Finish Setup - NPC (전체)를 돌릴 것."
        );
        return false;
    }

    // 다시 돌려도 같은 결과가 나오게 대상의 기존 래그돌 부품을 걷는다.
    // 관절부터 지운다 — 관절이 붙은 채로 Rigidbody를 지우면 연결이 끊긴 관절이 남는다.
    private static void StripExistingRagdoll(List<Transform> targetBones)
    {
        for (int i = 0; i < targetBones.Count; i++)
            foreach (CharacterJoint joint in targetBones[i].GetComponents<CharacterJoint>())
                Object.DestroyImmediate(joint, true);

        // 골반 복제(#572)를 Rigidbody보다 <b>먼저</b> 걷는다 — NetworkRigidbody가 Rigidbody를
        // RequireComponent하므로, 남겨 두면 아래에서 Rigidbody를 지우지 못하고 조용히 막힌다.
        // RagdollSetup이 마무리 단계에서 다시 붙이므로 결과는 같다.
        for (int i = 0; i < targetBones.Count; i++)
        {
            foreach (NetworkRigidbody netBody in targetBones[i].GetComponents<NetworkRigidbody>())
                Object.DestroyImmediate(netBody, true);
            foreach (NetworkTransform netTransform in targetBones[i].GetComponents<NetworkTransform>())
                Object.DestroyImmediate(netTransform, true);
        }

        for (int i = 0; i < targetBones.Count; i++)
        {
            foreach (Collider collider in targetBones[i].GetComponents<Collider>())
                Object.DestroyImmediate(collider, true);
            foreach (Rigidbody body in targetBones[i].GetComponents<Rigidbody>())
                Object.DestroyImmediate(body, true);
        }
    }

    // 직렬화 필드를 손으로 옮겨 적지 않고 복사·붙여넣기를 쓴다 — 필드를 하나 빠뜨려도 컴파일은
    // 통과하고 Play에서만 드러나는 종류의 값들이라(관절 한계·축·질량 분배) 열거하지 않는 편이 낫다.
    private static void CopyIfPresent(Component source, Transform target)
    {
        if (source == null)
            return;

        ComponentUtility.CopyComponent(source);
        ComponentUtility.PasteComponentAsNew(target.gameObject);
    }

    /// <summary>
    /// 관절을 복제하고 <b><c>connectedBody</c>를 대상 프리팹 안쪽으로 다시 묶는다.</b>
    ///
    /// 붙여넣기는 참조를 그대로 옮겨 오므로 그냥 두면 관절이 <b>원본 프리팹의 뼈</b>를 가리킨다.
    /// 프리팹을 저장하는 순간 그 참조는 끊기고(프리팹 밖 참조는 직렬화되지 않는다) 관절 없는 뼈가
    /// 되어, 죽는 순간 사지가 몸에서 떨어져 나간다.
    /// </summary>
    private static void CopyJoint(
        Transform sourceBone,
        Transform targetBone,
        List<Transform> sourceBones,
        List<Transform> targetBones
    )
    {
        CharacterJoint sourceJoint = sourceBone.GetComponent<CharacterJoint>();
        if (sourceJoint == null)
            return; // 래그돌 루트(Hips) — 관절이 없는 것이 정상이다

        Rigidbody sourceConnected = sourceJoint.connectedBody;
        int index = sourceConnected == null
            ? -1
            : sourceBones.IndexOf(sourceConnected.transform);

        // ⚠ <b>연결을 끊어 두고 복사한다.</b> 붙여넣기는 참조를 그대로 옮기므로, 그냥 두면 대상
        // 프리팹의 관절이 <b>원본 프리팹의 뼈</b>를 잠시 가리킨다. 두 프리팹은 서로 다른 임시
        // 물리 씬에 열려 있어서 PhysX가 뼈마다 경고를 한 줄씩 뱉는다("belong to a different
        // physics scenes"). 바로 아래에서 올바른 상대로 다시 묶으니 결과는 어차피 같지만,
        // 그 경고 무더기를 그대로 두면 다음 사람이 리그가 깨진 줄 안다.
        sourceJoint.connectedBody = null;
        CopyIfPresent(sourceJoint, targetBone);
        sourceJoint.connectedBody = sourceConnected; // 원본은 다음 대상에도 쓰인다 — 되돌려 놓는다

        CharacterJoint targetJoint = targetBone.GetComponent<CharacterJoint>();
        if (targetJoint == null)
            return;

        targetJoint.connectedBody =
            index >= 0 ? targetBones[index].GetComponent<Rigidbody>() : null;

        if (index < 0)
        {
            Debug.LogWarning(
                $"[래그돌 복제] {sourceBone.name}의 관절이 리그 밖 Rigidbody를 가리킨다 — "
                    + "연결을 비워 뒀다. 원본 리그를 확인할 것"
            );
        }
    }

    /// <summary>
    /// <c>NpcRagdoll</c>을 <b>프리팹 루트</b>에 보장한다 — <b>멱등</b>.
    ///
    /// <see cref="RagdollSetup"/>이 이걸 하지 않는 이유는 자리가 다르기 때문이다:
    /// <see cref="RagdollRig"/>는 리그 소유자(<c>Model</c>)에, <c>NpcRagdoll</c>은 NPC 루트에 붙는다.
    /// 게다가 저쪽은 대상이 플레이어인지 NPC인지 모른다. 여기는 NPC 전용이라 알 수 있다.
    /// </summary>
    private static string EnsureNpcRagdoll(GameObject target)
    {
        if (target.GetComponent<NpcRagdoll>() != null)
            return "NpcRagdoll — 이미 있음";

        target.AddComponent<NpcRagdoll>();
        return "NpcRagdoll — 새로 붙임";
    }

    // ---- 리그 훑기 ----

    private static Transform ResolveBoneRoot(GameObject root, string prefabPath)
    {
        Transform rigOwner = root.transform.Find(RagdollSetup.k_npcRigOwnerPath);
        if (rigOwner == null)
        {
            Debug.LogError(
                $"[래그돌 복제] 리그 소유자 '{RagdollSetup.k_npcRigOwnerPath}'를 {prefabPath}에서 "
                    + "찾지 못했다 — 프리팹 구조가 바뀌었으면 RagdollSetup.k_npcRigOwnerPath를 맞출 것"
            );
            return null;
        }

        Transform boneRoot = rigOwner.Find(RagdollRig.k_defaultBoneRootName);
        if (boneRoot == null)
        {
            Debug.LogError(
                $"[래그돌 복제] 몸통 리그 '{RagdollRig.k_defaultBoneRootName}'를 "
                    + $"{prefabPath}의 '{RagdollSetup.k_npcRigOwnerPath}' 직속 자식에서 찾지 못했다"
            );
        }

        return boneRoot;
    }

    // 래그돌 뼈를 CharacterJoint 기준으로 모은다 — RagdollSetup.CollectRagdollBodies와 같은 규칙이다.
    // (관절이 있는 Rigidbody + 그 관절이 연결한 상대 = 래그돌 전체. 리그와 무관한 Rigidbody가
    //  프리팹에 섞여 있어도 — NPC 루트에 실제로 하나 있다 — 걸리지 않는다.)
    //
    // 계층 순서(부모 먼저)로 정렬한다: 로그가 리그 모양대로 읽히고, 대상 쪽 순서와도 맞는다.
    private static List<Transform> CollectRagdollBones(GameObject root, Transform boneRoot)
    {
        HashSet<Transform> set = new HashSet<Transform>();
        foreach (CharacterJoint joint in root.GetComponentsInChildren<CharacterJoint>(true))
        {
            if (joint.GetComponent<Rigidbody>() != null)
                set.Add(joint.transform);
            if (joint.connectedBody != null)
                set.Add(joint.connectedBody.transform);
        }

        List<Transform> bones = new List<Transform>();
        foreach (Transform bone in set)
        {
            if (bone.IsChildOf(boneRoot))
                bones.Add(bone);
        }

        bones.Sort(
            (a, b) => RelativePath(boneRoot, a).CompareTo(RelativePath(boneRoot, b))
        );
        return bones;
    }

    private static string RelativePath(Transform root, Transform target)
    {
        StringBuilder path = new StringBuilder(target.name);
        Transform cursor = target;
        while (cursor.parent != null && cursor.parent != root)
        {
            cursor = cursor.parent;
            path.Insert(0, cursor.name + "/");
        }

        return path.ToString();
    }
}
