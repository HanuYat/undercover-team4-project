using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

/// <summary>
/// NPC 공격 스윙을 단발 클립 여러 개 중 무작위로 뽑아 재생하도록 NPC.controller를 갱신한다.
/// 스윙이 매번 같은 모션이라 반복이 눈에 띄던 것을 완화한다 (#220 단발 스윙 위에 얹는 변형).
///
/// <b>Attack 상태의 모션만 블렌드 트리로 교체한다</b> — 상태를 7개로 늘리지 않는 이유는,
/// 이 컨트롤러의 로코모션 전이가 전부 Any State(<c>State == N</c>)라 같은 조건(State==3)을 가진
/// 전이가 여러 개 생기면 우선순위에 따라 엉뚱한 것이 먼저 걸리기 때문이다. 기존 전이·상태는
/// 그대로 두고 <c>Attack</c> 상태가 물고 있는 모션 하나만 바꾸면 그 위험이 없다.
///
/// 블렌드 트리는 <c>SwingVariant</c>(float) 1D이며 자식 임계값이 0,1,2…로 정수다.
/// 드라이버가 정확히 정수를 넣으므로 그 자식만 가중치 1을 받아 사실상 discrete 선택이 된다
/// (블렌딩이 아니라 '고르기'로 쓰는 구조 — Adjust Time Scale은 끈 채로 둬야 각 클립이 제 길이로 돈다).
///
/// 재실행하면 블렌드 트리 내용만 다시 만든다. 다른 상태/전환은 보존된다.
/// 메뉴: Tools > NPC > Rebuild Attack Swing Variants
/// </summary>
public static class NpcAnimatorControllerBuilder
{
    private const string k_controllerPath = "Assets/Animation/NPC.controller";
    private const string k_attackStateName = "Attack";

    // 블렌드 트리 선택 파라미터 — 드라이버가 스윙 직전에 정수를 넣는다.
    // 이름은 런타임 쪽 상수를 그대로 쓴다(PlayerAnimatorControllerBuilder가 PlayerAnimationDriver의
    // 상수를 참조하는 것과 같은 방향) — 한쪽만 고쳐 조용히 어긋나는 사고를 막는다.
    private const string k_swingVariantParam = NpcAnimationDriver.k_swingVariantParam;

    private const string k_attack1HFolder =
        "Assets/Imported/Kevin Iglesias/Human Animations/Animations/Male/Combat/1H";

    // 단발 스윙만 고른다 — 쌍수(AttackDW)·패링(Parry)·전투 진입/이탈(CombatEnter/Exit)은 제외.
    // 전부 31~42프레임(약 1.0~1.4초)이라 콤보가 섞여 있지 않다.
    private static readonly string[] s_swingClipFiles =
    {
        "HumanM@Attack1H01_L.fbx",
        "HumanM@Attack1H01_R.fbx",
        "HumanM@Attack1H02_L.fbx",
        "HumanM@Attack1H02_R.fbx",
        "HumanM@Attack1H03_L.fbx",
        "HumanM@Attack1H03_R.fbx",
        "HumanM@Attack1H04_R.fbx",
    };

    [MenuItem("Tools/NPC/Rebuild Attack Swing Variants")]
    public static void Rebuild()
    {
        var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(k_controllerPath);
        if (controller == null)
        {
            Debug.LogError(
                $"[NpcAnimatorControllerBuilder] 컨트롤러를 찾을 수 없음: {k_controllerPath}"
            );
            return;
        }

        List<AnimationClip> clips = LoadSwingClips();
        if (clips.Count == 0)
        {
            Debug.LogError("[NpcAnimatorControllerBuilder] 스윙 클립을 하나도 불러오지 못해 중단");
            return;
        }

        AnimatorState attack = FindState(controller.layers[0].stateMachine, k_attackStateName);
        if (attack == null)
        {
            Debug.LogError(
                $"[NpcAnimatorControllerBuilder] '{k_attackStateName}' 상태가 없음 — 컨트롤러 구조 확인 필요"
            );
            return;
        }

        EnsureFloatParameter(controller, k_swingVariantParam);

        // 이전 실행이 만든 블렌드 트리는 컨트롤러 에셋의 하위로 남아 있으므로 지우고 새로 만든다.
        // (남겨두면 재실행할 때마다 고아 트리가 쌓인다)
        if (attack.motion is BlendTree oldTree && AssetDatabase.IsSubAsset(oldTree))
        {
            Object.DestroyImmediate(oldTree, true);
        }

        var tree = new BlendTree
        {
            name = "AttackSwingVariants",
            blendType = BlendTreeType.Simple1D,
            blendParameter = k_swingVariantParam,
            useAutomaticThresholds = false,
        };
        AssetDatabase.AddObjectToAsset(tree, controller);

        for (int i = 0; i < clips.Count; i++)
        {
            tree.AddChild(clips[i], i);
        }

        attack.motion = tree;

        EditorUtility.SetDirty(tree);
        EditorUtility.SetDirty(controller);
        AssetDatabase.SaveAssets();

        Debug.Log(
            $"[NpcAnimatorControllerBuilder] 갱신 완료: {k_controllerPath} "
                + $"— Attack 상태에 단발 스윙 {clips.Count}종 블렌드 트리 연결 "
                + $"(파라미터 {k_swingVariantParam}, 임계값 0~{clips.Count - 1})"
        );

        Selection.activeObject = controller;
    }

    /// <summary>스윙 클립을 순서대로 불러온다. 없는 파일은 경고만 남기고 건너뛴다(일부 누락돼도 나머지로 동작).</summary>
    private static List<AnimationClip> LoadSwingClips()
    {
        var clips = new List<AnimationClip>(s_swingClipFiles.Length);
        foreach (string file in s_swingClipFiles)
        {
            AnimationClip clip = LoadClip($"{k_attack1HFolder}/{file}");
            if (clip != null)
            {
                clips.Add(clip);
            }
        }
        return clips;
    }

    private static void EnsureFloatParameter(AnimatorController controller, string name)
    {
        foreach (AnimatorControllerParameter parameter in controller.parameters)
        {
            if (parameter.name == name)
                return;
        }

        controller.AddParameter(name, AnimatorControllerParameterType.Float);
    }

    private static AnimatorState FindState(AnimatorStateMachine stateMachine, string name)
    {
        foreach (ChildAnimatorState child in stateMachine.states)
        {
            if (child.state.name == name)
                return child.state;
        }
        return null;
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

        Debug.LogWarning($"[NpcAnimatorControllerBuilder] 클립을 찾을 수 없음(건너뜀): {fbxPath}");
        return null;
    }
}
