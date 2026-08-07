using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// #506 유니티 내장 Ragdoll Wizard가 만든 래그돌에 <b>위저드가 하지 않는 마무리</b>를 입힌다.
/// 메뉴: Tools > Player > Finish Ragdoll Setup
///
/// 위저드(<c>GameObject > 3D Object > Ragdoll…</c>)는 콜라이더와 CharacterJoint까지만 만든다.
/// 게임에 얹으려면 그 위에 다섯 가지가 더 필요한데 전부 위저드 범위 밖이다:
/// <list type="number">
///   <item><b>Ragdoll 전용 레이어</b> — 뼈가 Default로 남으면 <see cref="PlayerInteractor"/>의 조준 레이와
///     가시선 판정(Default 마스크)에 자기 뼈가 걸린다</item>
///   <item><b>충돌 매트릭스</b> — 지형만 충돌. 뼈끼리·상호작용물·아이템과 부딪히면 판정이 오염된다</item>
///   <item><b><c>isKinematic = true</c> 초기화</b> — 위저드는 켠 채로 두므로 그대로 두면 스폰 즉시 무너진다</item>
///   <item><b>Rigidbody 물리 설정</b> — 보간·CCD (solver 반복·겹침 탈출 속도는 직렬화되지 않아
///     <see cref="PlayerRagdoll"/>이 런타임에 건다 — 아래 상수 자리의 주석 참고)</item>
///   <item><b>관절 전처리(preprocessing) 해제</b> — 켜 두면 한계를 넘은 프레임에서 뼈가 튕겨 나간다</item>
/// </list>
///
/// <b>콜라이더 크기·관절 축·질량 분배는 일부러 건드리지 않는다.</b> 그건 위저드의 몫이다. 직접 계산하려
/// 들면 리그마다 다른 본 로컬 축을 눈대중으로 맞추게 되는데, 실제로 그러다 캡슐이 구로 찌그러져
/// (<c>height = max(길이, 2×반경)</c>에서 반경이 이긴다) 시체가 바닥에서 굴러가는 것을 겪었다.
/// 그래서 이 스크립트는 재실행해도 위저드의 산출물을 <b>보존</b>한다.
///
/// 대신 <b>검증</b>을 한다. 위저드는 뼈를 손으로 끌어다 넣는 방식이고 이 프리팹에는 뼈 이름이 완전히
/// 같은 리그가 두 벌 있어(<see cref="RagdollRig.k_defaultBoneRootName"/> 참고) 1인칭 팔 쪽을 집기 쉽다.
/// 그러면 사망 시 1인칭 팔이 물리로 풀려 바닥에 떨어진다 — 이것도 실제로 한 번 밟았다.
/// 전 뼈가 몸통 리그 아래인지 확인하고, 아니면 <b>아무것도 고치지 않고 중단한다.</b>
/// </summary>
public static class PlayerRagdollSetup
{
    private const string k_prefabPath = "Assets/Prefabs/Player.prefab";
    private const int k_layerSlot = 10; // 현재 비어 있는 슬롯

    // 래그돌이 충돌하는 레이어 — 지형(Default)뿐이다.
    //
    // ⚠ CharacterController 캡슐도 Default라(Player 프리팹 루트 m_Layer = 0) 매트릭스로는 지형과
    // 갈라낼 수 없다. 죽는 순간 래그돌은 자기 캡슐 <b>안에서</b> 출발하므로 그대로 두면 출발이 막힌다 —
    // 자기 캡슐과의 충돌 무시는 런타임에 PlayerRagdoll이 Physics.IgnoreCollision으로 건다.
    private static readonly string[] s_collidesWith = { "Default" };

    // ---- 여기서 설정하지 <b>않는</b> 것 ----
    //
    // solver 반복 횟수(solverIterations·solverVelocityIterations)와 겹침 탈출 속도 상한
    // (maxDepenetrationVelocity)은 <b>Rigidbody의 직렬화 필드가 아니다.</b> Player.prefab의 Rigidbody
    // 블록은 m_CollisionDetection에서 끝나고 그런 항목이 없다 — 여기서 써 봤자 프리팹에 남지 않고,
    // 인스턴스가 만들어질 때마다 Physics 프로젝트 기본값(6 / 1 / 10)으로 되돌아온다.
    // 실제로 여기에 12 / 4 / 3을 쓰는 코드가 있었고, 프리팹은 계속 6 / 1 / 10이었다.
    // → <see cref="PlayerRagdoll"/>이 Awake에서 런타임에 건다. 여기로 다시 옮기지 말 것.

    // 관절 projection(늘어난 관절을 매 스텝 강제로 되당기는 기능)은 <b>끈다</b>. 위저드의 기본값도 꺼짐이고,
    // 여기서 켜 봤다가 두 번 물렸다:
    //  · 좁게(0.02m / 5°) 잡으면 매 스텝 보정이 걸려 몸이 폭주한다
    //  · 느슨하게(0.1m / 180°) 잡아도, projection은 <b>충돌을 무시하고 위치를 옮기는</b> 기능이라
    //    밧줄로 끌 때 관절이 늘어나는 순간 머리를 지형 안으로 밀어 넣는다
    // 대신 solver 반복 횟수(k_solverIterations)로 관절을 붙든다. 강한 임펄스에서 관절이 살짝 늘어나
    // 보일 수는 있지만 그건 스스로 회복하는 시각 문제이고, 지형을 뚫는 것보다 낫다.
    private const bool k_enableProjection = false;

    [MenuItem("Tools/Player/Finish Ragdoll Setup")]
    public static void Run()
    {
        int layer = EnsureLayer();
        if (layer < 0)
            return;

        ConfigureLayerCollisions(layer);

        GameObject root = PrefabUtility.LoadPrefabContents(k_prefabPath);
        if (root == null)
        {
            Debug.LogError($"[래그돌 셋업] 프리팹을 열 수 없다: {k_prefabPath}");
            return;
        }

        try
        {
            if (Apply(root, layer))
                PrefabUtility.SaveAsPrefabAsset(root, k_prefabPath);
        }
        finally
        {
            // 실패로 빠져나가도 임시 씬이 남지 않게 한다 (LoadPrefabContents는 숨은 씬을 만든다)
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    // ---- 프로젝트 설정 ----

    // Ragdoll 레이어를 확보한다 — 이미 있으면 그 슬롯을 그대로 쓴다(재실행 안전).
    private static int EnsureLayer()
    {
        int existing = LayerMask.NameToLayer(RagdollRig.k_layerName);
        if (existing >= 0)
            return existing;

        Object[] assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset");
        if (assets.Length == 0)
        {
            Debug.LogError("[래그돌 셋업] TagManager.asset을 열 수 없다 — 레이어를 만들지 못했다");
            return -1;
        }

        SerializedObject tagManager = new SerializedObject(assets[0]);
        SerializedProperty layers = tagManager.FindProperty("layers");
        if (layers == null || k_layerSlot >= layers.arraySize)
        {
            Debug.LogError("[래그돌 셋업] TagManager의 layers 배열을 읽을 수 없다");
            return -1;
        }

        SerializedProperty slot = layers.GetArrayElementAtIndex(k_layerSlot);
        if (!string.IsNullOrEmpty(slot.stringValue))
        {
            Debug.LogError(
                $"[래그돌 셋업] 레이어 슬롯 {k_layerSlot}이 이미 '{slot.stringValue}'로 쓰이고 있다 — "
                    + "k_layerSlot을 빈 슬롯으로 바꿔서 다시 실행할 것"
            );
            return -1;
        }

        slot.stringValue = RagdollRig.k_layerName;
        tagManager.ApplyModifiedProperties();
        AssetDatabase.SaveAssets();
        Debug.Log($"[래그돌 셋업] 레이어 생성 — 슬롯 {k_layerSlot} = {RagdollRig.k_layerName}");

        return LayerMask.NameToLayer(RagdollRig.k_layerName);
    }

    // 충돌 매트릭스에서 Ragdoll 행을 s_collidesWith만 켠 상태로 만든다.
    // 벽 관통 걱정이 없는 근거가 여기다 — 지형과는 충돌하므로 래그돌은 벽을 넘지 않는다.
    //
    // Physics.IgnoreLayerCollision은 에디터에서 프로젝트 설정(DynamicsManager.asset)을 직접 고친다 —
    // 런타임 API처럼 보이지만 여기서는 영구 변경이고, 그게 이 호출의 목적이다.
    private static void ConfigureLayerCollisions(int layer)
    {
        HashSet<int> allowed = new HashSet<int>();
        for (int i = 0; i < s_collidesWith.Length; i++)
        {
            int other = LayerMask.NameToLayer(s_collidesWith[i]);
            if (other < 0)
            {
                Debug.LogWarning($"[래그돌 셋업] 레이어 '{s_collidesWith[i]}'가 없다 — 건너뛴다");
                continue;
            }

            allowed.Add(other);
        }

        for (int other = 0; other < 32; other++)
            Physics.IgnoreLayerCollision(layer, other, !allowed.Contains(other));

        Object[] assets = AssetDatabase.LoadAllAssetsAtPath(
            "ProjectSettings/DynamicsManager.asset"
        );
        if (assets.Length > 0)
            EditorUtility.SetDirty(assets[0]);
        AssetDatabase.SaveAssets();
    }

    // ---- 프리팹 ----

    private static bool Apply(GameObject root, int layer)
    {
        Transform boneRoot = root.transform.Find(RagdollRig.k_defaultBoneRootName);
        if (boneRoot == null)
        {
            Debug.LogError(
                $"[래그돌 셋업] 몸통 리그 '{RagdollRig.k_defaultBoneRootName}'를 프리팹 루트의 직속 자식에서"
                    + " 찾지 못했다 — 리그 구조가 바뀌었으면 RagdollRig.k_defaultBoneRootName을 맞출 것"
            );
            return false;
        }

        if (!CollectRagdollBodies(root, boneRoot, out List<Rigidbody> bodies))
            return false;

        StringBuilder report = new StringBuilder();
        report.AppendLine($"[래그돌 셋업] 뼈 {bodies.Count}개 마무리 — 레이어 {RagdollRig.k_layerName}, "
            + "전부 isKinematic=true (콜라이더·관절·질량은 위저드 값 그대로)");

        for (int i = 0; i < bodies.Count; i++)
        {
            Rigidbody body = bodies[i];
            body.gameObject.layer = layer;

            body.isKinematic = true; // 위저드는 켠 채로 둔다 — 그대로면 스폰 즉시 무너진다
            body.useGravity = true;

            // 키네마틱인 동안에는 보간을 끈다. 켜 두면 물리가 스텝 사이 보간 포즈를 트랜스폼에 써서
            // <b>애니메이터가 방금 놓은 포즈를 한 스텝 늦은 값으로 덮는다</b> — 팔다리가 끌리고
            // 스킨드 메시가 늘어난다. 래그돌이 켜지는 순간 PlayerRagdoll이 Interpolate로 올린다.
            body.interpolation = RigidbodyInterpolation.None;

            // 폭발 임펄스는 순간 속도가 커서 이산 충돌로는 지형을 뚫는다 — 투기적 CCD로 막는다(가볍다)
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;

            CharacterJoint joint = body.GetComponent<CharacterJoint>();
            if (joint != null)
            {
                joint.enablePreprocessing = false; // 켜 두면 한계를 넘은 프레임에서 뼈가 튕겨 나간다
                joint.enableProjection = k_enableProjection; // 위 상수 주석 — 끄는 이유가 적혀 있다
            }

            report.AppendLine(Describe(root.transform, body, joint));
        }

        Debug.Log(report.ToString());
        return true;
    }

    /// <summary>
    /// 위저드가 만든 래그돌 뼈를 모은다 — <b>CharacterJoint를 기준</b>으로 찾는다.
    /// (관절이 있는 Rigidbody + 그 관절이 연결한 상대 Rigidbody = 래그돌 전체)
    ///
    /// 그냥 Rigidbody 전체를 긁지 않는 이유: 프리팹에 래그돌과 무관한 Rigidbody가 있을 수 있어
    /// "리그 밖에 붙었다"는 검증이 오탐을 낸다. 관절 기준이면 위저드 산출물만 정확히 잡힌다.
    /// </summary>
    private static bool CollectRagdollBodies(
        GameObject root,
        Transform boneRoot,
        out List<Rigidbody> bodies
    )
    {
        bodies = new List<Rigidbody>();

        HashSet<Rigidbody> set = new HashSet<Rigidbody>();
        CharacterJoint[] joints = root.GetComponentsInChildren<CharacterJoint>(true);
        for (int i = 0; i < joints.Length; i++)
        {
            Rigidbody own = joints[i].GetComponent<Rigidbody>();
            if (own != null)
                set.Add(own);
            if (joints[i].connectedBody != null)
                set.Add(joints[i].connectedBody);
        }

        if (set.Count == 0)
        {
            Debug.LogError(
                "[래그돌 셋업] CharacterJoint를 하나도 찾지 못했다 — 내장 Ragdoll Wizard를 먼저 돌릴 것.\n"
                    + $"{k_prefabPath}를 Prefab 모드로 열고 GameObject > 3D Object > Ragdoll… 에서 "
                    + $"{RagdollRig.k_defaultBoneRootName}/Hips 이하의 뼈를 지정한다."
            );
            return false;
        }

        // 여기가 이 스크립트의 존재 이유 절반이다 — 위저드에 1인칭 팔의 동명 뼈를 끌어다 넣으면
        // 컴파일·프리팹 검증은 전부 통과하고 Play에서만 드러난다.
        List<string> outside = new List<string>();
        foreach (Rigidbody body in set)
        {
            if (body.transform.IsChildOf(boneRoot))
                bodies.Add(body);
            else
                outside.Add(PathOf(root.transform, body.transform));
        }

        if (outside.Count > 0)
        {
            Debug.LogError(
                "[래그돌 셋업] 몸통 리그 밖에 붙은 래그돌 뼈가 있어 중단한다 — 위저드에서 1인칭 팔"
                    + $"({RagdollRig.k_defaultBoneRootName}이 아닌 쪽)의 동명 뼈를 집은 것이다.\n"
                    + string.Join("\n", outside)
                    + $"\n\n해당 뼈의 Rigidbody·Collider·CharacterJoint를 지우고, {k_prefabPath}의 "
                    + $"'{RagdollRig.k_defaultBoneRootName}/Hips' 이하만 지정해 위저드를 다시 돌릴 것."
            );
            bodies.Clear();
            return false;
        }

        // 계층 순서(부모 먼저)로 정렬해 로그가 리그 모양대로 읽히게 한다
        bodies.Sort(
            (a, b) =>
                PathOf(root.transform, a.transform)
                    .CompareTo(PathOf(root.transform, b.transform))
        );
        return true;
    }

    // ---- 리포트 ----

    private static string Describe(Transform root, Rigidbody body, CharacterJoint joint)
    {
        Collider collider = body.GetComponent<Collider>();
        string shape = collider switch
        {
            CapsuleCollider capsule =>
                $"Capsule r={capsule.radius:F3} h={capsule.height:F3} "
                    + $"원통={(capsule.height - capsule.radius * 2f):F3}",
            BoxCollider box => $"Box {box.size.x:F3}×{box.size.y:F3}×{box.size.z:F3}",
            SphereCollider sphere => $"Sphere r={sphere.radius:F3}",
            null => "콜라이더 없음",
            _ => collider.GetType().Name,
        };

        string parent = joint == null
            ? "루트(관절 없음)"
            : joint.connectedBody == null
                ? "연결 끊김!"
                : joint.connectedBody.name;

        return $"  {PathOf(root, body.transform)}  |  질량 {body.mass:F1}  |  {shape}  |  부모 {parent}";
    }

    private static string PathOf(Transform root, Transform target)
    {
        string path = target.name;
        Transform cursor = target;
        while (cursor.parent != null && cursor != root)
        {
            cursor = cursor.parent;
            path = cursor.name + "/" + path;
        }

        return path;
    }
}
