using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

/// <summary>
/// #44 플레이어 애니메이션용 AnimatorController(MoveX/MoveZ 2D 블렌드 트리)를 생성/갱신한다.
/// 가운데 Idle, 반경 k_walkParam(1)에 Walk 8방향, 반경 k_runParam(2)에 Run 8방향 배치.
/// 좌표는 속도와 무관한 정규화 값 — 실제 속도 환산은 PlayerAnimationDriver가 담당하므로
/// PlayerMovement의 이동 속도를 튜닝해도 재실행할 필요 없다.
/// 이미 컨트롤러가 있으면 Locomotion 블렌드 트리 내용만 갱신한다.
/// (수동으로 추가한 다른 상태/전환은 보존됨 — 이 스크립트는 삭제하지 말 것)
/// 메뉴: Tools > Player > Create Animator Controller
/// </summary>
public static class PlayerAnimatorControllerBuilder
{
    private const string k_outputPath = "Assets/Animation/Player.controller";
    private const string k_stateName = "Locomotion";

    private const string k_idleFbx =
        "Assets/Imported/Kevin Iglesias/Human Animations/Animations/Male/Idles/HumanM@Idle01.fbx";
    private const string k_walkFolder =
        "Assets/Imported/Kevin Iglesias/Human Animations/Animations/Male/Movement/Walk";
    private const string k_runFolder =
        "Assets/Imported/Kevin Iglesias/Human Animations/Animations/Male/Movement/Run";

    // 8방향 (클립 이름 접미사, 로컬 방향 벡터) — x = 좌우, z = 전후
    private static readonly (string suffix, Vector2 dir)[] s_directions =
    {
        ("Forward", new Vector2(0f, 1f)),
        ("ForwardRight", new Vector2(0.7071f, 0.7071f)),
        ("Right", new Vector2(1f, 0f)),
        ("BackwardRight", new Vector2(0.7071f, -0.7071f)),
        ("Backward", new Vector2(0f, -1f)),
        ("BackwardLeft", new Vector2(-0.7071f, -0.7071f)),
        ("Left", new Vector2(-1f, 0f)),
        ("ForwardLeft", new Vector2(-0.7071f, 0.7071f)),
    };

    [MenuItem("Tools/Player/Create Animator Controller")]
    public static void Create()
    {
        AnimationClip idle = LoadClip(k_idleFbx);
        if (idle == null) return;

        var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(k_outputPath);
        bool isNew = controller == null;
        if (isNew)
        {
            controller = AnimatorController.CreateAnimatorControllerAtPath(k_outputPath);
        }

        EnsureFloatParameter(controller, "MoveX");
        EnsureFloatParameter(controller, "MoveZ");

        BlendTree blendTree = FindOrCreateBlendTree(controller);
        blendTree.children = new ChildMotion[0]; // 재실행 시 기존 클립 배치를 비우고 다시 채움

        // 중앙 = Idle
        blendTree.AddChild(idle, Vector2.zero);

        // 반경 1 = Walk 8방향, 반경 2 = Run 8방향 (PlayerAnimationDriver의 정규화 좌표)
        foreach ((string suffix, Vector2 dir) in s_directions)
        {
            AnimationClip walk = LoadClip($"{k_walkFolder}/HumanM@Walk01_{suffix}.fbx");
            AnimationClip run = LoadClip($"{k_runFolder}/HumanM@Run01_{suffix}.fbx");
            if (walk == null || run == null) return;

            blendTree.AddChild(walk, dir * PlayerAnimationDriver.k_walkParam);
            blendTree.AddChild(run, dir * PlayerAnimationDriver.k_runParam);
        }

        EditorUtility.SetDirty(blendTree);
        EditorUtility.SetDirty(controller);
        AssetDatabase.SaveAssets();

        Debug.Log($"[PlayerAnimatorControllerBuilder] {(isNew ? "생성" : "갱신")} 완료: {k_outputPath} " +
                  $"(Idle 중앙 / Walk 반경 {PlayerAnimationDriver.k_walkParam} 8방향 / " +
                  $"Run 반경 {PlayerAnimationDriver.k_runParam} 8방향, 총 17모션)");

        Selection.activeObject = controller;
    }

    private static void EnsureFloatParameter(AnimatorController controller, string name)
    {
        foreach (AnimatorControllerParameter parameter in controller.parameters)
        {
            if (parameter.name == name) return;
        }

        controller.AddParameter(name, AnimatorControllerParameterType.Float);
    }

    /// <summary>
    /// Locomotion 상태의 블렌드 트리를 찾고, 없으면 만든다.
    /// 컨트롤러의 다른 상태/전환은 건드리지 않는다.
    /// </summary>
    private static BlendTree FindOrCreateBlendTree(AnimatorController controller)
    {
        AnimatorStateMachine stateMachine = controller.layers[0].stateMachine;

        AnimatorState state = null;
        foreach (ChildAnimatorState child in stateMachine.states)
        {
            if (child.state.name == k_stateName)
            {
                state = child.state;
                break;
            }
        }

        if (state == null)
        {
            state = stateMachine.AddState(k_stateName);
        }

        if (state.motion is BlendTree existing)
        {
            existing.blendType = BlendTreeType.FreeformDirectional2D;
            existing.blendParameter = "MoveX";
            existing.blendParameterY = "MoveZ";
            return existing;
        }

        var blendTree = new BlendTree
        {
            name = k_stateName,
            blendType = BlendTreeType.FreeformDirectional2D,
            blendParameter = "MoveX",
            blendParameterY = "MoveZ",
        };

        // 블렌드 트리를 컨트롤러 에셋 안에 포함시켜야 저장이 유지됨
        AssetDatabase.AddObjectToAsset(blendTree, controller);
        state.motion = blendTree;
        return blendTree;
    }

    private static AnimationClip LoadClip(string fbxPath)
    {
        foreach (Object asset in AssetDatabase.LoadAllAssetsAtPath(fbxPath))
        {
            if (asset is AnimationClip clip && !clip.name.StartsWith("__preview__"))
            {
                return clip;
            }
        }

        Debug.LogError($"[PlayerAnimatorControllerBuilder] 클립을 찾을 수 없음: {fbxPath}");
        return null;
    }
}
