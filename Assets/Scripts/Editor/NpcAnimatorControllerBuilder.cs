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

    // 저항 NPC는 제자리에서 버티며 펀치만 얹으므로 루트모션이 없는 Inplace 클립을 쓴다 —
    // 원본(루트모션판)은 펀치할 때 앞으로 파고들어 NPC가 미끄러진다. (권투 모션 교체)
    private const string k_boxerAttackFolder =
        "Assets/Imported/Unleashed_boxer_AnimSet/Animation/Humanoid/Inplace";

    // 자물쇠 해제 모션 — 도착 순간 Begin, 채널링 동안 Loop. (#261)
    // Stop(마무리)은 쓰지 않는다: 해제가 끝나는 순간 자물쇠가 열리고 침입자는 곧바로 도주로 전이하므로
    // 재생될 틈이 없고, 억지로 끼우면 도주 시작이 그만큼 늦어진다.
    private const string k_openFolder =
        "Assets/Imported/Kevin Iglesias/Human Animations/Animations/Male/Misc/Open";
    private const string k_unlockBeginState = "Unlocking_Begin";
    private const string k_unlockLoopState = "Unlocking_Loop";

    // 한 번만 내지르는 단발 타격만 고른다 — attack01(원투 2연타)·attack06(4연타 콤보)은
    // 한 클립 안에 타격이 여러 번이라 제외했다. 스윙 오버레이(#220)는 1회성 타격을 전제로 한다.
    // (판별: 팔 완전 신전 횟수 + 손 속도 버스트 교차검증. 02·03은 직선 펀치, 04·05는 훅류 단발)
    private static readonly string[] s_swingClipFiles =
    {
        "attack02_inplace.fbx",
        "attack03_inplace.fbx",
        "attack04_inplace.fbx",
        "attack05_inplace.fbx",
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

        SetupUnlockStates(controller);

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

    /// <summary>
    /// 자물쇠 해제 상태를 구성한다 — Any State → Begin(1회), Any State → Loop(반복). (#261)
    /// Begin·Loop는 서로 <b>다른 번호</b>(<c>k_unlockingBeginAnimState</c>/<c>k_unlockingLoopAnimState</c>)로
    /// 진입한다 — 드라이버가 Begin 유지시간이 끝나면 번호를 Loop로 바꿔 Begin→Loop 전환을 직접 몬다.
    /// 두 전이 모두 기존 로코모션 전이(State == enum값)와 번호가 겹치지 않는다.
    /// 이탈은 따로 만들지 않는다 — 드라이버가 다른 번호를 넣는 순간 그쪽 Any State 전이가 걸린다.
    /// 재실행 시 기존 해제 상태/전환을 지우고 다시 만들어 중복을 막는다.
    /// </summary>
    private static void SetupUnlockStates(AnimatorController controller)
    {
        AnimationClip begin = LoadClip($"{k_openFolder}/HumanM@Opening01 - Begin.fbx");
        AnimationClip loop = LoadClip($"{k_openFolder}/HumanM@Opening01 - Loop.fbx");
        if (begin == null || loop == null)
        {
            Debug.LogError(
                "[NpcAnimatorControllerBuilder] 자물쇠 해제 클립을 불러오지 못해 해제 상태 구성을 건너뜀"
            );
            return;
        }

        AnimatorStateMachine stateMachine = controller.layers[0].stateMachine;
        RemoveUnlockStates(stateMachine);

        AnimatorState beginState = stateMachine.AddState(k_unlockBeginState);
        beginState.motion = begin;
        AnimatorState loopState = stateMachine.AddState(k_unlockLoopState);
        loopState.motion = loop;

        // Any State → Begin : 드라이버가 Begin 번호(100)를 넣는 순간 진입.
        // CanTransitionToSelf를 끄지 않으면 번호가 유지되는 매 프레임 Begin이 재시작돼 클립이 앞으로 못 나간다.
        AnimatorStateTransition toBegin = stateMachine.AddAnyStateTransition(beginState);
        toBegin.hasExitTime = false;
        toBegin.duration = 0.1f;
        toBegin.canTransitionToSelf = false;
        toBegin.AddCondition(
            AnimatorConditionMode.Equals,
            NpcAnimationDriver.k_unlockingBeginAnimState,
            "State"
        );

        // Any State → Loop : 드라이버가 Begin 유지시간이 끝나 Loop 번호(101)를 넣으면 진입.
        // Begin→Loop를 exit time 자동 전이가 아니라 이 조건 전이로 두는 것이 핵심이다 — Begin과 Loop가
        // 서로 다른 번호라 Loop에 들어간 뒤에는 State==100(Begin 조건)이 거짓이 되어 Begin으로 다시
        // 끌려가지 않는다. (단일 번호 + exit time이면 Loop 중에도 Any State→Begin 조건이 참이라
        // 매 프레임 Begin으로 되돌아가 해제 모션이 끊기듯 무한 재시작된다 — 이전 버그의 원인이었다.)
        AnimatorStateTransition toLoop = stateMachine.AddAnyStateTransition(loopState);
        toLoop.hasExitTime = false;
        toLoop.duration = 0.1f;
        toLoop.canTransitionToSelf = false;
        toLoop.AddCondition(
            AnimatorConditionMode.Equals,
            NpcAnimationDriver.k_unlockingLoopAnimState,
            "State"
        );
    }

    /// <summary>이전 실행이 만든 해제 상태와 그 상태를 가리키는 Any State 전이를 제거한다.</summary>
    private static void RemoveUnlockStates(AnimatorStateMachine stateMachine)
    {
        var staleTransitions = new List<AnimatorStateTransition>();
        foreach (AnimatorStateTransition transition in stateMachine.anyStateTransitions)
        {
            if (
                transition.destinationState != null
                && IsUnlockState(transition.destinationState.name)
            )
            {
                staleTransitions.Add(transition);
            }
        }
        foreach (AnimatorStateTransition transition in staleTransitions)
        {
            stateMachine.RemoveAnyStateTransition(transition);
        }

        foreach (ChildAnimatorState child in stateMachine.states)
        {
            if (IsUnlockState(child.state.name))
            {
                stateMachine.RemoveState(child.state);
            }
        }
    }

    private static bool IsUnlockState(string name) =>
        name == k_unlockBeginState || name == k_unlockLoopState;

    /// <summary>스윙 클립을 순서대로 불러온다. 없는 파일은 경고만 남기고 건너뛴다(일부 누락돼도 나머지로 동작).</summary>
    private static List<AnimationClip> LoadSwingClips()
    {
        var clips = new List<AnimationClip>(s_swingClipFiles.Length);
        foreach (string file in s_swingClipFiles)
        {
            AnimationClip clip = LoadClip($"{k_boxerAttackFolder}/{file}");
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
