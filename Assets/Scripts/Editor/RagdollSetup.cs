using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 유니티 내장 Ragdoll Wizard가 만든 래그돌에 <b>위저드가 하지 않는 마무리</b>를 입힌다. (#506 → #571 일반화)
/// 메뉴: <c>Tools > Ragdoll > …</c>
///
/// 위저드(<c>GameObject > 3D Object > Ragdoll…</c>)는 콜라이더와 CharacterJoint까지만 만든다.
/// 게임에 얹으려면 그 위에 다섯 가지가 더 필요한데 전부 위저드 범위 밖이다:
/// <list type="number">
///   <item><b>Ragdoll 전용 레이어</b> — 뼈가 Default로 남으면 <see cref="PlayerInteractor"/>의 조준 레이와
///     가시선 판정(Default 마스크)에 자기 뼈가 걸린다</item>
///   <item><b>충돌 매트릭스</b> — 지형만 충돌. 뼈끼리·상호작용물·아이템과 부딪히면 판정이 오염된다</item>
///   <item><b><c>isKinematic = true</c> 초기화</b> — 위저드는 켠 채로 두므로 그대로 두면 스폰 즉시 무너진다</item>
///   <item><b>Rigidbody 물리 설정</b> — 보간·CCD (solver 반복·겹침 탈출 속도는 직렬화되지 않아
///     <see cref="RagdollRig"/>가 런타임에 건다 — 아래 상수 자리의 주석 참고)</item>
///   <item><b>관절 전처리(preprocessing) 해제</b> — 켜 두면 한계를 넘은 프레임에서 뼈가 튕겨 나간다</item>
/// </list>
///
/// <b>콜라이더 크기·관절 축·질량 분배는 일부러 건드리지 않는다.</b> 그건 위저드의 몫이다. 직접 계산하려
/// 들면 리그마다 다른 본 로컬 축을 눈대중으로 맞추게 되는데, 실제로 그러다 캡슐이 구로 찌그러져
/// (<c>height = max(길이, 2×반경)</c>에서 반경이 이긴다) 시체가 바닥에서 굴러가는 것을 겪었다.
/// 그래서 이 스크립트는 재실행해도 위저드의 산출물을 <b>보존</b>한다.
///
/// 대신 <b>검증</b>을 한다. 위저드는 뼈를 손으로 끌어다 넣는 방식이라 엉뚱한 리그를 집기 쉽다 —
/// 플레이어 프리팹에는 뼈 이름이 완전히 같은 리그가 두 벌 있어(1인칭 팔) 그쪽을 집으면 사망 시
/// 팔이 물리로 풀려 바닥에 떨어진다(실제로 밟았다). 전 뼈가 몸통 리그 아래인지 확인하고,
/// 아니면 <b>아무것도 고치지 않고 중단한다.</b>
///
/// <b>개명 이력</b>: <c>PlayerRagdollSetup</c> → NPC에도 쓰게 되면서 이름이 실제 역할과 어긋나
/// #571에서 바꿨다. 함께 프리팹 경로 하드코딩도 걷어냈다.
/// </summary>
public static class RagdollSetup
{
    private const int k_layerSlot = 10; // 현재 비어 있는 슬롯

    // 래그돌이 충돌하는 레이어 — 지형(Default)뿐이다.
    //
    // ⚠ 플레이어의 CharacterController 캡슐도 Default라(Player 프리팹 루트 m_Layer = 0) 매트릭스로는
    // 지형과 갈라낼 수 없다. 죽는 순간 래그돌은 자기 캡슐 <b>안에서</b> 출발하므로 그대로 두면 출발이
    // 막힌다 — 자기 캡슐과의 충돌 무시는 런타임에 PlayerRagdoll이 Physics.IgnoreCollision으로 건다.
    //
    // <b>NPC에는 그 문제가 없다</b> — NPC 루트는 Interactable 레이어라 이 매트릭스가 이미 갈라 준다.
    private static readonly string[] s_collidesWith = { "Default" };

    // ---- 여기서 설정하지 <b>않는</b> 것 ----
    //
    // solver 반복 횟수(solverIterations·solverVelocityIterations)와 겹침 탈출 속도 상한
    // (maxDepenetrationVelocity)은 <b>Rigidbody의 직렬화 필드가 아니다.</b> 프리팹의 Rigidbody
    // 블록은 m_CollisionDetection에서 끝나고 그런 항목이 없다 — 여기서 써 봤자 프리팹에 남지 않고,
    // 인스턴스가 만들어질 때마다 Physics 프로젝트 기본값(6 / 1 / 10)으로 되돌아온다.
    // 실제로 여기에 12 / 4 / 3을 쓰는 코드가 있었고, 프리팹은 계속 6 / 1 / 10이었다.
    // → <see cref="RagdollRig"/>가 수집 시점에 런타임으로 건다. 여기로 다시 옮기지 말 것.

    // 관절 projection(늘어난 관절을 매 스텝 강제로 되당기는 기능)은 <b>끈다</b>. 위저드의 기본값도 꺼짐이고,
    // 여기서 켜 봤다가 두 번 물렸다:
    //  · 좁게(0.02m / 5°) 잡으면 매 스텝 보정이 걸려 몸이 폭주한다
    //  · 느슨하게(0.1m / 180°) 잡아도, projection은 <b>충돌을 무시하고 위치를 옮기는</b> 기능이라
    //    밧줄로 끌 때 관절이 늘어나는 순간 머리를 지형 안으로 밀어 넣는다
    // 대신 solver 반복 횟수(RagdollRig의 k_solverIterations)로 관절을 붙든다. 강한 임펄스에서 관절이
    // 살짝 늘어나 보일 수는 있지만 그건 스스로 회복하는 시각 문제이고, 지형을 뚫는 것보다 낫다.
    private const bool k_enableProjection = false;

    // ---- 대상 ----
    //
    // <b>리그가 프리팹 어디에 있는지가 개체마다 다르다</b> — 그게 이 스크립트를 일반화하며 드러난
    // 유일한 실질 차이다(#571). 나머지 처리는 전부 같다.
    //
    //  · 플레이어 — 리그가 프리팹 루트에 풀려 있다. 리그 소유자 = 루트("").
    //  · NPC — 몸이 Synty 캐릭터 프리팹의 <b>중첩 인스턴스</b>(Model)이고 리그가 그 안에 있다.
    //    리그 소유자 = "Model". 이 경우 아래 수정은 중첩 인스턴스의 오버라이드로 저장된다.
    //
    // 리그 소유자란 <see cref="RagdollRig"/>가 붙는 오브젝트다 — 그 컴포넌트가
    // <c>transform.Find(m_boneRootName)</c>으로 <b>직속 자식</b>에서 리그를 찾기 때문에,
    // "Root"를 직속 자식으로 가진 오브젝트여야 한다.

    private const string k_playerPrefab = "Assets/Prefabs/Player.prefab";

    /// <summary>NPC 리그 소유자 — 전 NPC 프리팹이 몸을 <c>Model</c> 중첩 인스턴스로 들고 있다. (#571)</summary>
    public const string k_npcRigOwnerPath = "Model";

    /// <summary>리그를 복제해 온 원본. <see cref="RagdollRigCloner"/>가 기준으로 삼는다.</summary>
    public const string k_npcCitizenPrefab = "Assets/Prefabs/NPC/NPC_Citizen.prefab";

    /// <summary>
    /// 리그가 필요한 NPC 프리팹 전부 — <b>이 목록이 정본이다.</b> 복제(<see cref="RagdollRigCloner"/>)와
    /// 마무리가 같은 목록을 봐야 한 쪽만 갱신되는 일이 없다.
    ///
    /// <c>NPC_Abductor</c>는 <b>없다</b> — <c>NPC_Citizen</c>의 Variant라 리그·<see cref="RagdollRig"/>·
    /// <c>NpcRagdoll</c>을 전부 상속한다. 여기 넣으면 상속을 오버라이드로 덮어써서, 앞으로
    /// Citizen 리그를 고쳐도 Abductor만 옛 값에 남는다.
    /// </summary>
    public static readonly string[] s_npcPrefabs =
    {
        k_npcCitizenPrefab,
        "Assets/Prefabs/NPC/NPC_Citizen_Generic.prefab",
        "Assets/Prefabs/NPC/NPC_Rioter.prefab",
        "Assets/Prefabs/NPC/NPC_Streaker.prefab",
    };

    [MenuItem("Tools/Ragdoll/Finish Setup - Player")]
    public static void RunPlayer() => Run(k_playerPrefab, rigOwnerPath: "");

    [MenuItem("Tools/Ragdoll/Finish Setup - NPC (전체)")]
    public static void RunAllNpc()
    {
        for (int i = 0; i < s_npcPrefabs.Length; i++)
            Run(s_npcPrefabs[i], k_npcRigOwnerPath);
    }

    /// <summary>
    /// 마무리를 실행한다. 서브 메뉴가 아니라 여기로 대상을 넘긴다 — 새 개체는 메뉴 한 줄만 늘리면 된다.
    /// </summary>
    /// <param name="prefabPath">대상 프리팹.</param>
    /// <param name="rigOwnerPath">
    /// 프리팹 루트 기준, <see cref="RagdollRig"/>가 붙을 오브젝트의 경로. 빈 문자열이면 루트다.
    /// 이 오브젝트의 <b>직속 자식</b>이 리그 최상단(<see cref="RagdollRig.k_defaultBoneRootName"/>)이어야 한다.
    /// </param>
    public static void Run(string prefabPath, string rigOwnerPath)
    {
        int layer = EnsureLayer();
        if (layer < 0)
            return;

        ConfigureLayerCollisions(layer);

        GameObject root = PrefabUtility.LoadPrefabContents(prefabPath);
        if (root == null)
        {
            Debug.LogError($"[래그돌 셋업] 프리팹을 열 수 없다: {prefabPath}");
            return;
        }

        try
        {
            if (Apply(root, prefabPath, rigOwnerPath, layer))
                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
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

    private static bool Apply(GameObject root, string prefabPath, string rigOwnerPath, int layer)
    {
        Transform rigOwner = ResolveRigOwner(root.transform, prefabPath, rigOwnerPath);
        if (rigOwner == null)
            return false;

        Transform boneRoot = rigOwner.Find(RagdollRig.k_defaultBoneRootName);
        if (boneRoot == null)
        {
            Debug.LogError(
                $"[래그돌 셋업] 몸통 리그 '{RagdollRig.k_defaultBoneRootName}'를 "
                    + $"'{DescribeOwner(rigOwnerPath)}'의 직속 자식에서 찾지 못했다 — "
                    + "리그 위치가 다르면 rigOwnerPath를, 리그 이름이 바뀌었으면 "
                    + "RagdollRig.k_defaultBoneRootName을 맞출 것"
            );
            return false;
        }

        if (!CollectRagdollBodies(root, boneRoot, prefabPath, out List<Rigidbody> bodies))
            return false;

        StringBuilder report = new StringBuilder();
        report.AppendLine(
            $"[래그돌 셋업] {prefabPath} — 뼈 {bodies.Count}개 마무리 "
                + $"(리그 소유자 {DescribeOwner(rigOwnerPath)}, 레이어 {RagdollRig.k_layerName}, "
                + "전부 isKinematic=true · 콜라이더·관절·질량은 위저드 값 그대로)"
        );

        report.AppendLine(EnsureRig(rigOwner));

        for (int i = 0; i < bodies.Count; i++)
        {
            Rigidbody body = bodies[i];
            body.gameObject.layer = layer;

            body.isKinematic = true; // 위저드는 켠 채로 둔다 — 그대로면 스폰 즉시 무너진다
            body.useGravity = true;

            // 키네마틱인 동안에는 보간을 끈다. 켜 두면 물리가 스텝 사이 보간 포즈를 트랜스폼에 써서
            // <b>애니메이터가 방금 놓은 포즈를 한 스텝 늦은 값으로 덮는다</b> — 팔다리가 끌리고
            // 스킨드 메시가 늘어난다. 래그돌이 켜지는 순간 RagdollRig가 Interpolate로 올린다.
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

    // 리그 소유자를 찾는다 — 빈 경로면 프리팹 루트다.
    private static Transform ResolveRigOwner(Transform root, string prefabPath, string rigOwnerPath)
    {
        if (string.IsNullOrEmpty(rigOwnerPath))
            return root;

        Transform owner = root.Find(rigOwnerPath);
        if (owner == null)
        {
            Debug.LogError(
                $"[래그돌 셋업] 리그 소유자 '{rigOwnerPath}'를 {prefabPath}에서 찾지 못했다 — "
                    + "프리팹 구조가 바뀌었으면 메뉴 호출부의 rigOwnerPath를 맞출 것"
            );
        }

        return owner;
    }

    private static string DescribeOwner(string rigOwnerPath) =>
        string.IsNullOrEmpty(rigOwnerPath) ? "프리팹 루트" : rigOwnerPath;

    /// <summary>
    /// <see cref="RagdollRig"/>를 리그 소유자에 보장한다 — <b>멱등</b>.
    ///
    /// 손으로 붙이게 두지 않는 이유는 <b>붙일 자리를 틀리기 쉽기 때문</b>이다. 리그는 개체마다 다른
    /// 깊이에 있는데(플레이어는 루트, NPC는 Model) 컴포넌트가 엉뚱한 곳에 붙으면 조용히 실패한다 —
    /// <c>transform.Find("Root")</c>가 null이 되어 런타임 에러 한 줄로만 드러난다.
    /// 이 함수는 방금 검증한 그 오브젝트에 붙이므로 자리가 틀릴 수 없다.
    ///
    /// <see cref="RagdollRope"/>도 같은 자리에 함께 보장한다 — <c>RagdollRig</c>를
    /// <c>RequireComponent</c>하므로 붙일 곳이 애초에 여기뿐이다.
    ///
    /// <b>한때는 붙이지 않았다</b> ("시체를 밧줄로 끄는 개체에만 필요한 선택 부품 — 플레이어는 운반
    /// 대상이라 필요하고 NPC는 아니다", #571 4단계). <b>그 전제가 뒤집혔다</b>: 시체 끌기가 들어오면서
    /// (#571 6단계) NPC도 끌리는 쪽이 됐다. 이제 래그돌이 있는 개체는 전부 밧줄 대상이라 갈래가 없다.
    /// </summary>
    private static string EnsureRig(Transform rigOwner)
    {
        string rig = rigOwner.GetComponent<RagdollRig>() != null ? "이미 있음" : "새로 붙임";
        if (rigOwner.GetComponent<RagdollRig>() == null)
            rigOwner.gameObject.AddComponent<RagdollRig>();

        string rope = rigOwner.GetComponent<RagdollRope>() != null ? "이미 있음" : "새로 붙임";
        if (rigOwner.GetComponent<RagdollRope>() == null)
            rigOwner.gameObject.AddComponent<RagdollRope>();

        return $"  RagdollRig — {rig} / RagdollRope — {rope} ({rigOwner.name})";
    }

    /// <summary>
    /// 위저드가 만든 래그돌 뼈를 모은다 — <b>CharacterJoint를 기준</b>으로 찾는다.
    /// (관절이 있는 Rigidbody + 그 관절이 연결한 상대 Rigidbody = 래그돌 전체)
    ///
    /// 그냥 Rigidbody 전체를 긁지 않는 이유: 프리팹에 래그돌과 무관한 Rigidbody가 있을 수 있어
    /// ("리그 밖에 붙었다"는 검증이 오탐을 낸다). NPC 루트에 실제로 하나 있다 — 관절 기준이면
    /// 위저드 산출물만 정확히 잡힌다.
    /// </summary>
    private static bool CollectRagdollBodies(
        GameObject root,
        Transform boneRoot,
        string prefabPath,
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
                    + $"{prefabPath}를 Prefab 모드로 열고 GameObject > 3D Object > Ragdoll… 에서 "
                    + $"{PathOf(root.transform, boneRoot)}/Hips 이하의 뼈를 지정한다."
            );
            return false;
        }

        // 여기가 이 스크립트의 존재 이유 절반이다 — 위저드에 다른 리그의 동명 뼈를 끌어다 넣으면
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
                "[래그돌 셋업] 몸통 리그 밖에 붙은 래그돌 뼈가 있어 중단한다 — 위저드에서 다른 리그"
                    + $"({PathOf(root.transform, boneRoot)}이 아닌 쪽)의 동명 뼈를 집은 것이다.\n"
                    + string.Join("\n", outside)
                    + $"\n\n해당 뼈의 Rigidbody·Collider·CharacterJoint를 지우고, {prefabPath}의 "
                    + $"'{PathOf(root.transform, boneRoot)}/Hips' 이하만 지정해 위저드를 다시 돌릴 것."
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
