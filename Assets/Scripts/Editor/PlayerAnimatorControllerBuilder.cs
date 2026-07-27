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

    // 다운(무력화) 상태 머신 — Knockdown 3단계: 쓰러짐 → 바닥 대기(루프) → 기상. (#105)
    private const string k_downParam = "Down";
    private const string k_fallState = "Knockdown_Fall";
    private const string k_groundState = "Knockdown_Ground";
    private const string k_standUpState = "Knockdown_StandUp";
    private const string k_combatFolder =
        "Assets/Imported/Kevin Iglesias/Human Animations/Animations/Male/Combat";

    // 앉기 상태 — Crouch(bool)=true면 서기 블렌드 트리에서 앉기 블렌드 트리로 짧게 전환한다. (#236)
    // 서기↔앉기 전환 클립은 에셋에 없어(Crouch는 Idle/이동만 제공) 전환 클립 대신 짧은 블렌딩으로 처리한다.
    private const string k_crouchParam = "Crouch";
    private const string k_crouchState = "Crouch";
    private const string k_crouchFolder =
        "Assets/Imported/Kevin Iglesias/Human Animations/Animations/Male/Movement/Crouch";

    // 점프 상태 — Airborne(bool)=true면 이륙 → 체공(루프)까지만 재생하고, 착지하면 지상 상태로 직행한다. (#189)
    // 루트 모션이 꺼진 프리팹이라 제자리 클립([RM] 없는 쪽)을 쓴다 — 이동은 CharacterController가 담당.
    private const string k_airborneParam = "Airborne";
    private const string k_jumpBeginState = "Jump_Begin";
    private const string k_jumpAirState = "Jump_Air";
    private const string k_jumpFolder =
        "Assets/Imported/Kevin Iglesias/Human Animations/Animations/Male/Movement/Jump";

    // 착지 블렌딩 시간(초) — 전용 착지 상태를 두지 않으므로 이 블렌딩이 곧 착지 연출이다.
    // 착지 클립(Jump01 - Land, 0.60초)을 끼우면 그 길이만큼 다음 동작이 밀려 조작이 굼떠 보인다.
    // 대신 체공 자세에서 다음 동작(Idle/이동/앉기)으로 짧게 이어붙인다.
    // 너무 짧으면(0.03초 근처) 자세가 튀어 보이므로 살짝 여유를 둔다.
    private const float k_landBlend = 0.1f;

    // 공중에서 앉기 키를 누르면 자세만 웅크린다 — 실제 앉기(콜라이더·속도)는 착지 시점에
    // PlayerCrouch가 건다. 덕분에 공중에서 콜라이더를 줄여 좁은 틈을 통과하는 크라우치 점프가 막힌다.
    // 지상 앉기와 같은 구성의 블렌드 트리(Crouch Idle + CrouchWalk 8방향)를 쓴다 — 단일 Idle 클립이면
    // 공중에서 좌우로 움직이는 동안 몸이 굳어 보인다. MoveX/MoveZ는 위치 변화량 기반이라 공중에서도 돈다.
    private const string k_jumpAirCrouchState = "Jump_Air_Crouch";

    // 더 이상 만들지 않는 구 착지 상태 — 이전 빌드가 남긴 것을 재실행 시 지우기 위해서만 쓴다.
    private const string k_legacyJumpLandState = "Jump_Land";

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
        if (idle == null)
            return;

        var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(k_outputPath);
        bool isNew = controller == null;
        if (isNew)
        {
            controller = AnimatorController.CreateAnimatorControllerAtPath(k_outputPath);
        }

        EnsureFloatParameter(controller, "MoveX");
        EnsureFloatParameter(controller, "MoveZ");

        BlendTree blendTree = FindOrCreateBlendTree(controller, k_stateName);
        blendTree.children = new ChildMotion[0]; // 재실행 시 기존 클립 배치를 비우고 다시 채움

        // 중앙 = Idle
        blendTree.AddChild(idle, Vector2.zero);

        // 반경 1 = Walk 8방향, 반경 2 = Run 8방향 (PlayerAnimationDriver의 정규화 좌표)
        foreach ((string suffix, Vector2 dir) in s_directions)
        {
            AnimationClip walk = LoadClip($"{k_walkFolder}/HumanM@Walk01_{suffix}.fbx");
            AnimationClip run = LoadClip($"{k_runFolder}/HumanM@Run01_{suffix}.fbx");
            if (walk == null || run == null)
                return;

            blendTree.AddChild(walk, dir * PlayerAnimationDriver.k_walkParam);
            blendTree.AddChild(run, dir * PlayerAnimationDriver.k_runParam);
        }

        BlendTree crouchTree = SetupCrouchState(controller); // 앉기 상태 추가/갱신 (#236) — 다운 전환보다 먼저
        BlendTree airCrouchTree = SetupJumpStates(controller); // 점프 상태 머신 추가/갱신 (#189) — 다운 전환보다 먼저
        SetupDownStates(controller); // 다운(무력화) 상태 머신 추가/갱신 (#105)

        EditorUtility.SetDirty(blendTree);
        if (crouchTree != null)
        {
            EditorUtility.SetDirty(crouchTree);
        }
        if (airCrouchTree != null)
        {
            EditorUtility.SetDirty(airCrouchTree);
        }
        EditorUtility.SetDirty(controller);
        AssetDatabase.SaveAssets();

        Debug.Log(
            $"[PlayerAnimatorControllerBuilder] {(isNew ? "생성" : "갱신")} 완료: {k_outputPath} "
                + $"(Idle 중앙 / Walk 반경 {PlayerAnimationDriver.k_walkParam} 8방향 / "
                + $"Run 반경 {PlayerAnimationDriver.k_runParam} 8방향, 총 17모션 + 다운 3상태 "
                + $"+ 앉기 9모션 + 점프 3상태(공중 웅크림 9모션))"
        );

        Selection.activeObject = controller;
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

    private static void EnsureBoolParameter(AnimatorController controller, string name)
    {
        foreach (AnimatorControllerParameter parameter in controller.parameters)
        {
            if (parameter.name == name)
                return;
        }

        controller.AddParameter(name, AnimatorControllerParameterType.Bool);
    }

    /// <summary>
    /// 앉기 상태를 구성한다. (#236)
    /// 중앙에 Crouch Idle, 반경 k_walkParam(1)에 CrouchWalk 8방향을 배치한 블렌드 트리를 만들고,
    /// Locomotion과 Crouch(bool) 조건으로 짧게(PlayerCrouch.k_blendDuration) 양방향 전환한다.
    /// 앉기에는 달리기 단계가 없어(에셋 미제공) 반경 k_runParam 링은 만들지 않는다 —
    /// PlayerAnimationDriver가 앉기 중엔 앉기 속도를 k_walkParam에 대응시킨다.
    /// 재실행 시 Locomotion↔Crouch 전환을 지우고 다시 만들어 중복을 막는다.
    /// </summary>
    private static BlendTree SetupCrouchState(AnimatorController controller)
    {
        EnsureBoolParameter(controller, k_crouchParam);

        BlendTree crouchTree = FindOrCreateBlendTree(controller, k_crouchState);
        if (!FillCrouchBlendTree(crouchTree))
            return null;

        AnimatorStateMachine stateMachine = controller.layers[0].stateMachine;
        AnimatorState locomotion = FindState(stateMachine, k_stateName);
        AnimatorState crouch = FindState(stateMachine, k_crouchState);
        if (locomotion == null || crouch == null)
            return crouchTree;

        RemoveTransitionsBetween(locomotion, crouch); // 재실행 시 중복 방지

        // Locomotion → Crouch : Ctrl을 누르는 순간 짧게 블렌딩하며 앉는다 (전환 클립 없음 — 의도된 설계)
        AnimatorStateTransition toCrouch = locomotion.AddTransition(crouch);
        toCrouch.hasExitTime = false;
        toCrouch.duration = PlayerCrouch.k_blendDuration;
        toCrouch.AddCondition(AnimatorConditionMode.If, 0f, k_crouchParam);

        // Crouch → Locomotion : Ctrl을 떼면 같은 시간으로 기립
        AnimatorStateTransition toStand = crouch.AddTransition(locomotion);
        toStand.hasExitTime = false;
        toStand.duration = PlayerCrouch.k_blendDuration;
        toStand.AddCondition(AnimatorConditionMode.IfNot, 0f, k_crouchParam);

        return crouchTree;
    }

    /// <summary>
    /// 앉기 클립을 블렌드 트리에 채운다 — 중앙 Crouch Idle, 반경 k_walkParam에 CrouchWalk 8방향.
    /// 지상 앉기(Crouch)와 공중 웅크림(Jump_Air_Crouch)이 같은 구성을 쓰므로 한 곳에 모은다. (#236, #189)
    /// 두 상태가 <b>같은 트리를 공유하지 않고 각자 하나씩</b> 갖는 이유는 SetupJumpStates 주석 참고.
    /// 클립을 못 찾으면 false — 호출부가 상태 구성을 중단한다.
    /// </summary>
    private static bool FillCrouchBlendTree(BlendTree tree)
    {
        AnimationClip crouchIdle = LoadClip($"{k_crouchFolder}/HumanM@Crouch01_Idle.fbx");
        if (crouchIdle == null)
            return false;

        tree.children = new ChildMotion[0]; // 재실행 시 기존 클립 배치를 비우고 다시 채움
        tree.AddChild(crouchIdle, Vector2.zero);

        foreach ((string suffix, Vector2 dir) in s_directions)
        {
            AnimationClip crouchWalk = LoadClip(
                $"{k_crouchFolder}/CrouchWalk/HumanM@Crouch01_Walk_{suffix}.fbx"
            );
            if (crouchWalk == null)
                return false;

            tree.AddChild(crouchWalk, dir * PlayerAnimationDriver.k_walkParam);
        }

        return true;
    }

    // 두 상태 사이의 기존 전환을 양방향으로 제거한다 — 빌더 재실행 시 전환이 중복 누적되는 것을 막는다.
    private static void RemoveTransitionsBetween(AnimatorState a, AnimatorState b)
    {
        RemoveTransitionsTo(a, b);
        RemoveTransitionsTo(b, a);
    }

    private static void RemoveTransitionsTo(AnimatorState from, AnimatorState destination)
    {
        var toRemove = new System.Collections.Generic.List<AnimatorStateTransition>();
        foreach (AnimatorStateTransition transition in from.transitions)
        {
            if (transition.destinationState == destination)
            {
                toRemove.Add(transition);
            }
        }
        foreach (AnimatorStateTransition transition in toRemove)
        {
            from.RemoveTransition(transition);
        }
    }

    /// <summary>
    /// 점프 상태 머신을 구성한다. (#189)
    /// Airborne(bool)=true면 Locomotion/Crouch → Begin(이륙) → Air(체공, 루프),
    /// 착지해 Airborne=false가 되면 <b>전용 착지 상태 없이</b> 곧장 Locomotion(또는 앉기를 누르고
    /// 있으면 Crouch)으로 이어붙인다. 착지 클립을 한 번 끼우면 그 길이만큼 다음 동작이 밀려
    /// 조작이 굼떠 보여서, 착지는 짧은 블렌딩으로만 처리한다.
    ///
    /// 트리거가 아닌 bool로 구동하는 이유: 공중 여부는 서버 권위 NetworkVariable로 전파되는데
    /// (PlayerJump.IsAirborne) 트리거는 원격 피어에서 유실·중복되기 쉽다. bool은 폴링이라 안전하다.
    /// 재실행 시 기존 점프 상태/전환을 지우고 다시 만들어 중복을 막는다.
    ///
    /// 공중 웅크림(Jump_Air_Crouch)만 예외로 상태를 지우지 않고 재사용한다 — 지상 앉기와 같은
    /// 블렌드 트리를 들고 있어서, 상태를 지우면 트리 서브에셋이 컨트롤러에 고아로 남기 때문이다.
    /// 지상 앉기와 트리를 <b>공유하지 않고 따로 하나 더</b> 두는 것도 같은 이유다 — 공유하면 한쪽
    /// 상태를 지울 때 다른 쪽 모션까지 날아갈 위험이 있다. 구성은 FillCrouchBlendTree가 한곳에서 맞춘다.
    /// 반환값은 그 공중 웅크림 트리(호출부가 SetDirty용으로 받는다).
    /// </summary>
    private static BlendTree SetupJumpStates(AnimatorController controller)
    {
        AnimationClip begin = LoadClip($"{k_jumpFolder}/HumanM@Jump01 - Begin.fbx");
        AnimationClip air = LoadClip($"{k_jumpFolder}/HumanM@Fall01.fbx");
        if (begin == null || air == null)
            return null;

        EnsureBoolParameter(controller, k_airborneParam);

        AnimatorStateMachine stateMachine = controller.layers[0].stateMachine;
        RemoveJumpStates(stateMachine); // 재실행 시 중복 방지 (구 Jump_Land도 여기서 정리된다)

        AnimatorState locomotion = FindState(stateMachine, k_stateName);
        AnimatorState crouch = FindState(stateMachine, k_crouchState);

        AnimatorState beginState = stateMachine.AddState(k_jumpBeginState);
        beginState.motion = begin;
        AnimatorState airState = stateMachine.AddState(k_jumpAirState);
        airState.motion = air;

        // 공중 웅크림 — 전용 턱(tuck) 클립이 에셋에 없어 앉기 클립을 그대로 쓴다.
        // 단일 Idle이 아니라 앉기와 같은 블렌드 트리라, 공중에서 움직이면 앉은 채 걷는 모션이 나온다.
        // (공중 이동 속도는 앉기 속도가 아니라 걷기/달리기 속도지만 — PlayerCrouch.IsCrouching이
        //  공중에선 false — 발이 땅에 없어 발 미끄러짐이 눈에 띄지 않는다)
        BlendTree airCrouchTree = FindOrCreateBlendTree(controller, k_jumpAirCrouchState);
        if (!FillCrouchBlendTree(airCrouchTree))
            return null;

        AnimatorState airCrouchState = FindState(stateMachine, k_jumpAirCrouchState);

        // Crouch → Air_Crouch : 앉은 채 점프하면 이륙 상태를 건너뛰고 곧장 웅크린 체공으로 간다.
        // Begin(선 자세 이륙)을 거치면 발이 떨어지는 순간 일어섰다 다시 앉는 장면이 70ms쯤 스친다
        // (계측: Crouch→Begin 0.017s, Begin 노출 0.073s, Begin→Air_Crouch 0.092s).
        // 아래 Crouch → Begin보다 먼저 달아야 우선권을 갖는다.
        if (crouch != null)
        {
            AnimatorStateTransition crouchToTuck = crouch.AddTransition(airCrouchState);
            crouchToTuck.hasExitTime = false;
            crouchToTuck.duration = 0.05f;
            crouchToTuck.AddCondition(AnimatorConditionMode.If, 0f, k_airborneParam);
            crouchToTuck.AddCondition(AnimatorConditionMode.If, 0f, k_crouchParam);
        }

        // Locomotion/Crouch → Begin : 발이 떨어지는 순간 이륙.
        // Crouch 쪽은 앉기를 뗀 채 난간을 걸어 내려가는 경우의 보루다 — 앉은 채 점프는 위에서 가로챈다.
        foreach (AnimatorState source in new[] { locomotion, crouch })
        {
            if (source == null)
                continue;

            AnimatorStateTransition toBegin = source.AddTransition(beginState);
            toBegin.hasExitTime = false;
            toBegin.duration = 0.05f; // 입력 반응이 무겁지 않게 짧게
            toBegin.AddCondition(AnimatorConditionMode.If, 0f, k_airborneParam);
        }

        // 착지 — 공중 3상태 전부에서 지상 상태로 직행한다.
        // 다른 전환보다 먼저 달아야 우선권을 갖는다: 착지 프레임에 앉기 키를 누르거나 떼도
        // 공중 상태끼리(Air↔Air_Crouch) 한 번 들렀다 오는 군더더기가 생기지 않는다.
        foreach (AnimatorState source in new[] { beginState, airState, airCrouchState })
        {
            if (crouch != null)
            {
                // 앉기를 누른 채 착지 — 실제 앉기(콜라이더·속도)도 이 시점에 걸려 자세와 상태가 맞는다.
                AnimatorStateTransition toCrouch = source.AddTransition(crouch);
                toCrouch.hasExitTime = false;
                toCrouch.duration = k_landBlend;
                toCrouch.AddCondition(AnimatorConditionMode.IfNot, 0f, k_airborneParam);
                toCrouch.AddCondition(AnimatorConditionMode.If, 0f, k_crouchParam);
            }

            if (locomotion != null)
            {
                AnimatorStateTransition toLocomotion = source.AddTransition(locomotion);
                toLocomotion.hasExitTime = false;
                toLocomotion.duration = k_landBlend;
                toLocomotion.AddCondition(AnimatorConditionMode.IfNot, 0f, k_airborneParam);
                toLocomotion.AddCondition(AnimatorConditionMode.IfNot, 0f, k_crouchParam);
            }
        }

        // Begin/Air → Air_Crouch : 공중에서 앉기 키를 누르면 자세만 웅크린다.
        // Begin이 체공 시간 대부분을 차지하므로 Begin에서도 받아야 실제로 보인다.
        foreach (AnimatorState source in new[] { beginState, airState })
        {
            AnimatorStateTransition toTuck = source.AddTransition(airCrouchState);
            toTuck.hasExitTime = false;
            toTuck.duration = 0.1f;
            toTuck.AddCondition(AnimatorConditionMode.If, 0f, k_crouchParam);
        }

        // Air_Crouch → Air : 공중에서 키를 떼면 다시 편다
        AnimatorStateTransition tuckToAir = airCrouchState.AddTransition(airState);
        tuckToAir.hasExitTime = false;
        tuckToAir.duration = 0.1f;
        tuckToAir.AddCondition(AnimatorConditionMode.IfNot, 0f, k_crouchParam);

        // Begin → Air : 이륙 모션이 끝나면 체공 루프로 (착지·웅크림보다 뒤 — 그쪽이 우선)
        AnimatorStateTransition toAir = beginState.AddTransition(airState);
        toAir.hasExitTime = true;
        toAir.exitTime = 0.8f;
        toAir.duration = 0.1f;

        return airCrouchTree;
    }

    // 점프 상태와 그 상태로 향하는 전환을 모두 제거한다. (RemoveDownStates와 동일 구조)
    // Jump_Air_Crouch만 상태를 남기고 전환만 비운다 — 이유는 SetupJumpStates 주석 참고.
    private static void RemoveJumpStates(AnimatorStateMachine stateMachine)
    {
        foreach (ChildAnimatorState child in stateMachine.states)
        {
            var toRemove = new System.Collections.Generic.List<AnimatorStateTransition>();
            foreach (AnimatorStateTransition transition in child.state.transitions)
            {
                if (
                    transition.destinationState != null
                    && IsJumpState(transition.destinationState.name)
                )
                {
                    toRemove.Add(transition);
                }
            }
            foreach (AnimatorStateTransition transition in toRemove)
            {
                child.state.RemoveTransition(transition);
            }
        }

        // 살아남는 Jump_Air_Crouch의 나가는 전환(착지 → Locomotion/Crouch)은 위 루프가 못 지운다 —
        // 목적지가 점프 상태가 아니기 때문. 재실행 시 중복되지 않도록 여기서 비운다.
        AnimatorState airCrouch = FindState(stateMachine, k_jumpAirCrouchState);
        if (airCrouch != null)
        {
            foreach (AnimatorStateTransition transition in airCrouch.transitions)
            {
                airCrouch.RemoveTransition(transition);
            }
        }

        foreach (ChildAnimatorState child in stateMachine.states)
        {
            if (IsRemovableJumpState(child.state.name))
            {
                stateMachine.RemoveState(child.state);
            }
        }
    }

    // 전환 정리 대상 — 구 Jump_Land를 포함해야 예전 빌드의 잔재까지 청소된다.
    private static bool IsJumpState(string name)
    {
        return name == k_jumpBeginState
            || name == k_jumpAirState
            || name == k_jumpAirCrouchState
            || name == k_legacyJumpLandState;
    }

    // 상태 자체를 지울 대상 — 블렌드 트리를 들고 재사용되는 Jump_Air_Crouch만 제외한다.
    private static bool IsRemovableJumpState(string name)
    {
        return name != k_jumpAirCrouchState && IsJumpState(name);
    }

    /// <summary>
    /// 다운(무력화) 상태 머신을 구성한다. (#105, GDD 7-5)
    /// Down(bool)=true면 Locomotion → Fall(쓰러짐) → Ground(바닥 대기, 루프),
    /// 구조로 Down=false가 되면 Ground → StandUp(기상) → Locomotion으로 복귀한다.
    /// 재실행 시 기존 다운 상태/전환을 지우고 다시 만들어 중복을 막는다.
    /// </summary>
    private static void SetupDownStates(AnimatorController controller)
    {
        AnimationClip fall = LoadClip($"{k_combatFolder}/HumanM@Knockdown01 - Fall.fbx");
        AnimationClip ground = LoadClip($"{k_combatFolder}/HumanM@Knockdown01 - Ground.fbx");
        AnimationClip standUp = LoadClip($"{k_combatFolder}/HumanM@Knockdown01 - StandUp.fbx");
        if (fall == null || ground == null || standUp == null)
            return;

        EnsureBoolParameter(controller, k_downParam);

        AnimatorStateMachine stateMachine = controller.layers[0].stateMachine;
        RemoveDownStates(stateMachine); // 재실행 시 중복 방지

        AnimatorState locomotion = FindState(stateMachine, k_stateName);
        AnimatorState fallState = stateMachine.AddState(k_fallState);
        fallState.motion = fall;
        AnimatorState groundState = stateMachine.AddState(k_groundState);
        groundState.motion = ground;
        AnimatorState standUpState = stateMachine.AddState(k_standUpState);
        standUpState.motion = standUp;

        // Locomotion/Crouch/점프 → Fall : 다운되는 순간(Down=true) 즉시 쓰러짐.
        // 앉은 채로 다운될 수 있으므로 앉기 상태에서도 나가는 전환이 필요하고 (#236),
        // 공중에서 맞을 수도 있어 점프 3상태에서도 빠져나올 길이 있어야 한다 (#189).
        AnimatorState[] sources =
        {
            locomotion,
            FindState(stateMachine, k_crouchState),
            FindState(stateMachine, k_jumpBeginState),
            FindState(stateMachine, k_jumpAirState),
            FindState(stateMachine, k_jumpAirCrouchState),
        };
        foreach (AnimatorState source in sources)
        {
            if (source == null)
                continue;

            AnimatorStateTransition toFall = source.AddTransition(fallState);
            toFall.hasExitTime = false;
            toFall.duration = 0.1f;
            toFall.AddCondition(AnimatorConditionMode.If, 0f, k_downParam);
        }

        // Fall → Ground : 쓰러짐 모션이 끝나면 자동으로 바닥 대기(루프)
        AnimatorStateTransition toGround = fallState.AddTransition(groundState);
        toGround.hasExitTime = true;
        toGround.exitTime = 0.9f;
        toGround.duration = 0.1f;

        // Ground → StandUp : 구조로 Down=false가 되면 기상
        AnimatorStateTransition toStandUp = groundState.AddTransition(standUpState);
        toStandUp.hasExitTime = false;
        toStandUp.duration = 0.1f;
        toStandUp.AddCondition(AnimatorConditionMode.IfNot, 0f, k_downParam);

        // StandUp → Locomotion : 기상 모션이 끝나면 이동 상태로 복귀
        if (locomotion != null)
        {
            AnimatorStateTransition toLocomotion = standUpState.AddTransition(locomotion);
            toLocomotion.hasExitTime = true;
            toLocomotion.exitTime = 0.9f;
            toLocomotion.duration = 0.1f;
        }
    }

    // 다운 상태(3종)와 그 상태로 향하는 전환을 모두 제거한다.
    private static void RemoveDownStates(AnimatorStateMachine stateMachine)
    {
        // 먼저 다른 상태(Locomotion 등)에서 다운 상태로 향하는 전환 제거
        foreach (ChildAnimatorState child in stateMachine.states)
        {
            var toRemove = new System.Collections.Generic.List<AnimatorStateTransition>();
            foreach (AnimatorStateTransition transition in child.state.transitions)
            {
                if (
                    transition.destinationState != null
                    && IsDownState(transition.destinationState.name)
                )
                {
                    toRemove.Add(transition);
                }
            }
            foreach (AnimatorStateTransition transition in toRemove)
            {
                child.state.RemoveTransition(transition);
            }
        }

        // 다운 상태 자체 제거 (해당 상태의 나가는 전환도 함께 삭제됨)
        foreach (ChildAnimatorState child in stateMachine.states)
        {
            if (IsDownState(child.state.name))
            {
                stateMachine.RemoveState(child.state);
            }
        }
    }

    private static bool IsDownState(string name)
    {
        return name == k_fallState || name == k_groundState || name == k_standUpState;
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

    /// <summary>
    /// 지정한 상태의 MoveX/MoveZ 블렌드 트리를 찾고, 없으면 상태와 함께 만든다.
    /// 컨트롤러의 다른 상태/전환은 건드리지 않는다.
    /// </summary>
    private static BlendTree FindOrCreateBlendTree(AnimatorController controller, string stateName)
    {
        AnimatorStateMachine stateMachine = controller.layers[0].stateMachine;

        AnimatorState state = FindState(stateMachine, stateName);

        if (state == null)
        {
            state = stateMachine.AddState(stateName);
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
            name = stateName,
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
