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

    // 감정표현 상태 머신 (#219) — 카탈로그 순서대로 상태를 자동 생성한다.
    // 클립 경로를 여기 적지 않는 이유: 어떤 감정표현이 있는지는 EmoteCatalog 하나가 정하고,
    // 빌더는 그걸 읽기만 한다. 두 곳에 목록을 두면 카탈로그에 추가하고 빌더를 안 고쳐
    // "휠에는 뜨는데 재생은 안 되는" 상태가 난다.
    private const string k_emoteParam = "Emote";
    private const string k_emoteIndexParam = "EmoteIndex";
    private const string k_emoteStatePrefix = "Emote_";
    private const string k_emoteCatalogPath = "Assets/Settings/Emote/EmoteCatalog.asset";

    // 기절(테이저 아군 오사)은 다운과 같은 Knockdown 상태 머신을 탄다 — 맞는 즉시 쓰러진다. (#252)
    // 한때 Stun01로 갈랐는데 그건 NPC '제압 그로기'(서서 헤롱거리는 루프)가 쓰는 클립이었다.
    // 선 채로 비틀대는 몸에 카메라만 바닥 높이로 내려가 어긋났고, NPC 기절도 실은 Knockdown01-Ground로
    // 쓰러져 있다 — "기절=쓰러진 자세"가 원래 규칙이다 (GDD 7-4).
    // 아래 두 이름은 그때 만든 상태·파라미터를 재실행으로 걷어내기 위해서만 남긴다.
    private const string k_stunParam = "Stunned";
    private const string k_stunState = "Stun";

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

    // 구조 채널링 모션 (#725) — NPC 탈옥 침입자의 자물쇠 해제(NpcAnimatorControllerBuilder의
    // Unlocking_Begin/Loop)와 같은 클립을 재사용한다. 저쪽은 Any State + int 파라미터로 드라이버가
    // Begin→Loop 전환 시점을 직접 몰지만, 여기는 다운 상태(Fall→Ground)와 같은 exitTime 자동 전환으로
    // 충분하다 — 정확한 임팩트 타이밍을 맞출 필요가 없는 루프 모션이라서다.
    private const string k_revivingParam = "Reviving";
    private const string k_revivingBeginState = "Reviving_Begin";
    private const string k_revivingLoopState = "Reviving_Loop";
    private const string k_revivingFolder =
        "Assets/Imported/Kevin Iglesias/Human Animations/Animations/Male/Misc/Open";

    // 타격(진압봉) 상체 레이어 — 이동·점프·앉기를 유지한 채 상체만 스윙으로 덮는다. (#217)
    // Base Layer에 상태로 넣지 않는 이유: 그러면 공격 중 하체가 멈춰 점프·앉은 채 공격이 불가능해진다.
    // 구조는 NPC.controller의 UpperBodyCuffed 레이어와 동일하다 — 마스크 + Override + weight 1 고정에,
    // 평소엔 모션 없는 빈 상태(None)에 머물러 Base Layer가 그대로 보이고, 트리거로만 스윙에 들어갔다 나온다.
    // (레이어 weight를 코드로 0↔1 흔드는 방식은 블렌딩을 직접 관리해야 해서 쓰지 않는다)
    private const string k_attackParam = "Attack";
    private const string k_attackLayer = "UpperBodyAttack";
    private const string k_attackEmptyState = "None";
    private const string k_attackState = "Attack_Baton";
    private const string k_attackMaskPath = "Assets/Animation/UpperBodyAttack.mask";

    // 반드시 _R(오른손) 클립일 것 — 아이템은 오른손 본(Hand_R/HeldItemAnchor)에 붙으므로
    // _L을 쓰면 왼손으로 휘두르는데 진압봉은 오른손에 붙어 따로 논다.
    // 에셋에 좌우 클립이 모두 있어 미러링은 필요 없다. 나머지 클립과 같은 남성(HumanM) 세트를 쓴다.
    //
    // 같은 _R 중에서도 01을 쓰는 이유가 두 가지 있다 (02·03은 왼쪽에서 오른쪽으로 긋는 백핸드):
    //  1. 1인칭 팔 스윙(PlayerHandView)이 오른쪽→왼쪽 정타라, 백핸드를 쓰면 내가 보는 팔과
    //     남이 보는 내 몸이 서로 반대로 휘두른다.
    //  2. 임팩트 시점(t≈0.30)에 봉 끝이 화면 정면 한가운데(좌우 +0.24m, 앞 1.90m)를 지난다 —
    //     타격 판정이 카메라 정면 스피어캐스트(m_range 2m)라 보이는 궤적과 맞는다.
    //     02는 같은 순간 봉이 오른쪽으로 1.07m 치우쳐 판정선과 어긋났다.
    private const string k_attackClip =
        "Assets/Imported/Kevin Iglesias/Human Animations/Animations/Male/Combat/1H/HumanM@Attack1H01_R.fbx";

    // 스윙 진입/복귀 블렌딩(초). 짧게 — 길면 타격 입력에 손이 늦게 반응하는 느낌이 난다.
    private const float k_attackBlend = 0.08f;

    // 스윙 클립의 몇 %가 지났을 때 빈 상태로 돌아가기 시작할지. 끝(1.0)까지 두면 마무리 잔동작이
    // 그대로 다 나와 다음 입력이 굼떠 보인다 — 임팩트 이후 회수 동작을 블렌딩으로 잘라낸다.
    //
    // 클립 안에서 임팩트(봉이 정면 최대 도달)가 일어나는 정규화 시점. 계측값이다 —
    // k_attackClip을 바꾸면 반드시 다시 재서 갱신할 것. 이 값이 틀리면 1인칭과 3인칭이 어긋난다.
    private const float k_attackClipImpactNormalized = 0.3f;

    // 스윙 재생 길이(exitTime)와 배속은 상수로 박지 않고 PlayerAnimationDriver의 공용 목표에서
    // 계산한다 — SetupAttackLayer 참고. 클립을 갈아끼워도 싱크가 저절로 맞는다.

    // 상체 레이어가 관할할 휴머노이드 부위. Body(척추)를 켜는 게 핵심이다 — 스윙은 척추 회전이
    // 동작의 대부분이라 팔만 켜면 팔을 휘적거리는 그림이 된다.
    // Root는 반드시 끈다 — 켜면 공중에서 자세가 튄다. 다리·IK도 하체 로코모션에 맡긴다.
    // (NPC의 UpperBodyCuffed와 Body 하나만 다르다. 그쪽은 '포즈'라 척추를 로코모션에 맡기는 게 맞고,
    //  같은 에셋을 공유하면 수갑 찬 NPC의 상체가 함께 굳으므로 마스크를 따로 둔다)
    private static readonly AvatarMaskBodyPart[] s_attackMaskParts =
    {
        AvatarMaskBodyPart.Body,
        AvatarMaskBodyPart.Head,
        AvatarMaskBodyPart.LeftArm,
        AvatarMaskBodyPart.RightArm,
        AvatarMaskBodyPart.LeftFingers,
        AvatarMaskBodyPart.RightFingers,
    };

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
        SetupRevivingState(controller); // 구조 채널링 모션 추가/갱신 (#725) — 다운 상태 뒤에 와야 Fall 전이를 걸 수 있다
        SetupEmoteStates(controller); // 감정표현 상태 추가/갱신 (#219) — 다운 상태 뒤에 와야 Fall 전이를 걸 수 있다
        RemoveLegacyStunStates(controller); // 구 기절 상태 제거 — 다운 상태 머신에 흡수됐다 (#252)
        SetupAttackLayer(controller); // 타격 상체 레이어 추가/갱신 (#217) — Base Layer가 아닌 레이어 1

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
                + $"Run 반경 {PlayerAnimationDriver.k_runParam} 8방향, 총 17모션 + 다운 3상태(기절 공용) "
                + $"+ 앉기 9모션 + 점프 3상태(공중 웅크림 9모션) + 타격 상체 레이어 1 + 구조 채널링 2상태)"
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

    private static void EnsureIntParameter(AnimatorController controller, string name)
    {
        foreach (AnimatorControllerParameter parameter in controller.parameters)
        {
            if (parameter.name == name)
                return;
        }

        controller.AddParameter(name, AnimatorControllerParameterType.Int);
    }

    private static void EnsureTriggerParameter(AnimatorController controller, string name)
    {
        foreach (AnimatorControllerParameter parameter in controller.parameters)
        {
            if (parameter.name == name)
                return;
        }

        controller.AddParameter(name, AnimatorControllerParameterType.Trigger);
    }

    /// <summary>
    /// 타격 상체 레이어(레이어 1)를 추가/갱신한다. (#217)
    /// 마스크로 상체만 관할하므로 하체는 Base Layer의 이동·점프·앉기가 그대로 재생된다 —
    /// 점프하면서 공격, 앉아서 공격이 별도 상태 없이 성립한다.
    ///
    /// 다른 Setup 메서드들과 달리 Base Layer(layers[0])를 건드리지 않는다. 덕분에 다운·점프 상태
    /// 머신과 전환 조합이 늘어나지 않는다 — 타격을 Base Layer 상태로 넣었다면 (지상/공중/앉기) ×
    /// (타격중/아님) 조합마다 전환을 깔아야 했다.
    /// </summary>
    private static void SetupAttackLayer(AnimatorController controller)
    {
        AnimationClip swing = LoadClip(k_attackClip);
        if (swing == null)
            return;

        EnsureTriggerParameter(controller, k_attackParam);

        AvatarMask mask = CreateOrUpdateAttackMask();
        AnimatorStateMachine stateMachine = FindOrCreateAttackLayer(controller, mask);

        // 재실행 시 중복 방지 — 이 레이어는 통째로 다시 만든다(Base Layer와 달리 수동 편집분이 없다).
        foreach (ChildAnimatorState child in stateMachine.states)
        {
            stateMachine.RemoveState(child.state);
        }

        // 모션 없는 빈 상태가 기본 — 이 상태에 있는 동안은 레이어가 아무 것도 쓰지 않아
        // 마스크 부위까지 Base Layer가 그대로 보인다. (NPC.controller의 UpperBodyCuffed와 같은 구성)
        AnimatorState empty = stateMachine.AddState(k_attackEmptyState);
        empty.motion = null;
        stateMachine.defaultState = empty;

        AnimatorState attack = stateMachine.AddState(k_attackState);
        attack.motion = swing;

        // ---- 1인칭과의 타이밍 정렬 (#217) ----
        // 클립을 그대로 틀면 임팩트가 자연 시점(여기선 0.42초)에 오는데, 1인칭 절차적 스윙은
        // PlayerAnimationDriver.k_swingImpactSeconds에 맞춰 때린다. 클립을 잘라서는 앞부분을
        // 당길 수 없으므로(임팩트는 중간에 있다) 재생 속도로 끌어온다.
        float naturalImpact = k_attackClipImpactNormalized * swing.length;
        attack.speed = naturalImpact / PlayerAnimationDriver.k_swingImpactSeconds;

        // 배속이 걸렸으니 종료 시점도 그만큼 뒤로 밀 수 있다 — 실시간 k_swingSeconds가 되는 지점.
        // (exitTime은 클립 기준 정규화값이라 speed를 곱해야 실시간으로 환산된다)
        float exitTime = PlayerAnimationDriver.k_swingSeconds * attack.speed / swing.length;
        if (exitTime > 1f)
        {
            // 클립이 목표 길이보다 짧다 — 끝까지 재생하고 남는 시간은 기본 자세로 서 있는다.
            Debug.LogWarning(
                $"[PlayerAnimatorControllerBuilder] 스윙 클립이 목표 길이보다 짧다 "
                    + $"(필요 exitTime={exitTime:F2}). 1.0으로 자른다 — 1인칭 스윙이 3인칭보다 늦게 끝난다."
            );
            exitTime = 1f;
        }

        AnimatorStateTransition toAttack = empty.AddTransition(attack);
        toAttack.hasExitTime = false; // 트리거 즉시 진입 — 대기 시간이 있으면 타격 판정과 어긋난다
        toAttack.hasFixedDuration = true;
        toAttack.duration = k_attackBlend;
        toAttack.AddCondition(AnimatorConditionMode.If, 0f, k_attackParam);

        AnimatorStateTransition toEmpty = attack.AddTransition(empty);
        toEmpty.hasExitTime = true; // 조건 없이 클립이 끝나가면 자동 복귀
        toEmpty.exitTime = exitTime;
        toEmpty.hasFixedDuration = true;
        toEmpty.duration = k_attackBlend;

        EditorUtility.SetDirty(stateMachine);
    }

    /// <summary>
    /// 타격 레이어용 아바타 마스크를 만들거나(없으면) 부위 구성을 다시 맞춘다.
    /// 휴머노이드 부위 단위로만 지정하고 transform 목록은 비워 둔다 — 리그가 바뀌어도 그대로 쓰인다.
    /// </summary>
    private static AvatarMask CreateOrUpdateAttackMask()
    {
        AvatarMask mask = AssetDatabase.LoadAssetAtPath<AvatarMask>(k_attackMaskPath);
        bool isNew = mask == null;
        if (isNew)
        {
            mask = new AvatarMask();
        }

        foreach (AvatarMaskBodyPart part in System.Enum.GetValues(typeof(AvatarMaskBodyPart)))
        {
            if (part == AvatarMaskBodyPart.LastBodyPart)
                continue;

            mask.SetHumanoidBodyPartActive(
                part,
                System.Array.IndexOf(s_attackMaskParts, part) >= 0
            );
        }

        if (isNew)
        {
            AssetDatabase.CreateAsset(mask, k_attackMaskPath);
        }
        else
        {
            EditorUtility.SetDirty(mask);
        }

        return mask;
    }

    /// <summary>
    /// 타격 레이어를 찾고 없으면 만든 뒤, 마스크·블렌딩·weight를 매번 다시 맞춰 반환한다.
    /// controller.layers는 복사본을 돌려주므로 배열을 통째로 다시 대입해야 변경이 반영된다.
    /// </summary>
    private static AnimatorStateMachine FindOrCreateAttackLayer(
        AnimatorController controller,
        AvatarMask mask
    )
    {
        bool exists = false;
        foreach (AnimatorControllerLayer existing in controller.layers)
        {
            if (existing.name == k_attackLayer)
            {
                exists = true;
                break;
            }
        }

        if (!exists)
        {
            controller.AddLayer(k_attackLayer); // 상태 머신도 함께 서브에셋으로 생성된다
        }

        AnimatorControllerLayer[] layers = controller.layers;
        AnimatorStateMachine stateMachine = null;
        foreach (AnimatorControllerLayer layer in layers)
        {
            if (layer.name != k_attackLayer)
                continue;

            layer.avatarMask = mask;
            layer.blendingMode = AnimatorLayerBlendingMode.Override;
            layer.defaultWeight = 1f; // 켜고 끄는 건 weight가 아니라 빈 상태↔스윙 상태 전환이다
            stateMachine = layer.stateMachine;
        }

        controller.layers = layers;
        return stateMachine;
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

    /// <summary>
    /// 구조 채널링 모션을 구성한다. (#725)
    /// Reviving(bool)=true면 Locomotion → Begin(1회) → Loop(반복), false가 되면(완료·취소 무관)
    /// 곧장 Locomotion으로 복귀한다 — Begin 도중 취소되는 경우도 있어 Begin→Locomotion도 열어 둔다.
    /// 재실행 시 기존 상태/전환을 지우고 다시 만들어 중복을 막는다.
    /// </summary>
    private static void SetupRevivingState(AnimatorController controller)
    {
        AnimationClip begin = LoadClip($"{k_revivingFolder}/HumanM@Opening01 - Begin.fbx");
        AnimationClip loop = LoadClip($"{k_revivingFolder}/HumanM@Opening01 - Loop.fbx");
        if (begin == null || loop == null)
            return;

        EnsureBoolParameter(controller, k_revivingParam);

        AnimatorStateMachine stateMachine = controller.layers[0].stateMachine;
        RemoveRevivingState(stateMachine);

        AnimatorState locomotion = FindState(stateMachine, k_stateName);
        AnimatorState beginState = stateMachine.AddState(k_revivingBeginState);
        beginState.motion = begin;
        AnimatorState loopState = stateMachine.AddState(k_revivingLoopState);
        loopState.motion = loop;

        if (locomotion == null)
            return;

        // Locomotion → Begin : E로 채널링을 시작하는 순간.
        AnimatorStateTransition toBegin = locomotion.AddTransition(beginState);
        toBegin.hasExitTime = false;
        toBegin.duration = 0.15f;
        toBegin.AddCondition(AnimatorConditionMode.If, 0f, k_revivingParam);

        // Begin → Loop : 시작 모션이 끝나면 자동으로 반복 자세로 (다운 Fall→Ground와 같은 방식)
        AnimatorStateTransition toLoop = beginState.AddTransition(loopState);
        toLoop.hasExitTime = true;
        toLoop.exitTime = 0.9f;
        toLoop.duration = 0.1f;

        // Begin/Loop → Locomotion : 완료·취소 무관하게 Reviving이 꺼지는 순간 복귀.
        // Begin에서 빠지는 경로가 필요한 이유는 채널링이 시작 모션 도중 취소될 수 있어서다
        // (이동·다른 입력·E 재입력 — PlayerReviver.HandleCancelTrigger).
        foreach (AnimatorState source in new[] { beginState, loopState })
        {
            AnimatorStateTransition toLocomotion = source.AddTransition(locomotion);
            toLocomotion.hasExitTime = false;
            toLocomotion.duration = 0.15f;
            toLocomotion.AddCondition(AnimatorConditionMode.IfNot, 0f, k_revivingParam);
        }
    }

    // 구조 채널링 상태(2종)와 그 상태로 향하는 전환을 모두 제거한다. (RemoveDownStates와 동일 구조)
    private static void RemoveRevivingState(AnimatorStateMachine stateMachine)
    {
        foreach (ChildAnimatorState child in stateMachine.states)
        {
            var toRemove = new System.Collections.Generic.List<AnimatorStateTransition>();
            foreach (AnimatorStateTransition transition in child.state.transitions)
            {
                if (
                    transition.destinationState != null
                    && IsRevivingState(transition.destinationState.name)
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

        foreach (ChildAnimatorState child in stateMachine.states)
        {
            if (IsRevivingState(child.state.name))
            {
                stateMachine.RemoveState(child.state);
            }
        }
    }

    private static bool IsRevivingState(string name) =>
        name == k_revivingBeginState || name == k_revivingLoopState;

    /// <summary>
    /// 감정표현 상태를 카탈로그 순서대로 만든다. (#219)
    ///
    /// <b>다운 상태 머신보다 나중에 불러야 한다</b> — 감정표현 중 쓰러질 때 Locomotion을
    /// 경유하지 않고 곧장 Knockdown_Fall로 가야 하는데, 그 전이를 걸려면 Fall 상태가 이미
    /// 있어야 한다. 경유하면 쓰러짐이 한 박자 늦게 보이고, Knockdown의 즉시성은
    /// PlayerAnimationDriver가 주석으로 거듭 강조하는 지점이다.
    ///
    /// 비루프 클립의 종료를 exit time에 맡기지 않는 이유: 재생 여부의 진실은 서버의
    /// NetworkVariable 하나이고, 애니메이터가 자기 판단으로 먼저 빠져나오면 서버는 아직
    /// 재생 중인데 화면만 멈춘 상태가 난다.
    /// </summary>
    private static void SetupEmoteStates(AnimatorController controller)
    {
        var catalog = AssetDatabase.LoadAssetAtPath<EmoteCatalog>(k_emoteCatalogPath);
        if (catalog == null)
        {
            Debug.LogWarning(
                $"[PlayerAnimatorControllerBuilder] 감정표현 카탈로그가 없어 건너뜁니다: {k_emoteCatalogPath}"
            );
            return;
        }

        EnsureBoolParameter(controller, k_emoteParam);
        EnsureIntParameter(controller, k_emoteIndexParam);

        AnimatorStateMachine stateMachine = controller.layers[0].stateMachine;
        RemoveEmoteStates(stateMachine); // 재실행 시 중복 방지 — 카탈로그가 줄었을 수도 있다

        AnimatorState locomotion = FindState(stateMachine, k_stateName);
        AnimatorState fallState = FindState(stateMachine, k_fallState);
        AnimatorState jumpBegin = FindState(stateMachine, k_jumpBeginState);

        int created = 0;
        for (int index = 0; index < catalog.Count; index++)
        {
            EmoteDefinition definition = catalog.Get(index);
            if (definition == null || definition.Clip == null)
                continue; // 이모지 전용 항목은 애니메이터에 자리가 필요 없다

            AnimatorState state = stateMachine.AddState($"{k_emoteStatePrefix}{index:00}");
            state.motion = definition.Clip;
            created++;

            // Locomotion → Emote_i : Emote가 켜지고 인덱스가 맞으면 즉시 진입.
            // 앉기·점프 상태에서는 감정표현을 시작할 수 없으므로(서버가 막는다) 진입은 Locomotion에서만 온다.
            if (locomotion != null)
            {
                AnimatorStateTransition enter = locomotion.AddTransition(state);
                enter.hasExitTime = false;
                enter.duration = 0.15f;
                enter.AddCondition(AnimatorConditionMode.If, 0f, k_emoteParam);
                enter.AddCondition(AnimatorConditionMode.Equals, index, k_emoteIndexParam);

                AnimatorStateTransition exit = state.AddTransition(locomotion);
                exit.hasExitTime = false;
                exit.duration = 0.15f;
                exit.AddCondition(AnimatorConditionMode.IfNot, 0f, k_emoteParam);
            }

            // Emote_i → Knockdown_Fall : 춤추다 맞고 쓰러지는 순간 곧장 넘어간다.
            if (fallState != null)
            {
                AnimatorStateTransition toFall = state.AddTransition(fallState);
                toFall.hasExitTime = false;
                toFall.duration = 0.1f;
                toFall.AddCondition(AnimatorConditionMode.If, 0f, k_downParam);
            }

            // Emote_i → 점프 시작 : 서버가 공중 진입과 함께 감정표현을 끊지만, 애니메이터가
            // Locomotion을 한 번 거치면 이륙 모션이 잘린다.
            if (jumpBegin != null)
            {
                AnimatorStateTransition toJump = state.AddTransition(jumpBegin);
                toJump.hasExitTime = false;
                toJump.duration = 0.1f;
                toJump.AddCondition(AnimatorConditionMode.If, 0f, k_airborneParam);
            }
        }

        Debug.Log($"[PlayerAnimatorControllerBuilder] 감정표현 상태 {created}개 생성");
    }

    // 재실행 시 이전 감정표현 상태를 걷어낸다 — 카탈로그에서 항목을 빼면 그만큼 상태가 줄어야 한다.
    // (RemoveDownStates와 같은 2단계 방식)
    private static void RemoveEmoteStates(AnimatorStateMachine stateMachine)
    {
        // 먼저 다른 상태(Locomotion 등)에서 감정표현 상태로 향하는 전환 제거
        // — SetupEmoteStates가 만드는 진입 전이는 Emote_i가 아니라 Locomotion이 소유하므로
        // 상태만 지우면 destination이 null인 죽은 전이가 Locomotion에 남는다.
        foreach (ChildAnimatorState child in stateMachine.states)
        {
            var toRemove = new System.Collections.Generic.List<AnimatorStateTransition>();
            foreach (AnimatorStateTransition transition in child.state.transitions)
            {
                if (
                    transition.destinationState != null
                    && transition.destinationState.name.StartsWith(k_emoteStatePrefix)
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

        // 감정표현 상태 자체 제거 (해당 상태의 나가는 전환도 함께 삭제됨)
        var statesToRemove = new System.Collections.Generic.List<AnimatorState>();
        foreach (ChildAnimatorState child in stateMachine.states)
        {
            if (child.state != null && child.state.name.StartsWith(k_emoteStatePrefix))
                statesToRemove.Add(child.state);
        }
        foreach (AnimatorState state in statesToRemove)
        {
            stateMachine.RemoveState(state);
        }
    }

    // 이전 빌드가 남긴 기절 상태·파라미터를 걷어낸다 — 기절은 이제 다운과 같은 Knockdown 상태 머신을
    // 타므로 전용 상태가 필요 없다. (#252)
    // 남겨 두면 Stunned가 영영 false인 채 죽은 상태로 컨트롤러에 붙어 있고, 나중에 다른 기능이 같은
    // 이름의 파라미터를 만들 때 충돌한다.
    private static void RemoveLegacyStunStates(AnimatorController controller)
    {
        RemoveStunStates(controller.layers[0].stateMachine);

        foreach (AnimatorControllerParameter parameter in controller.parameters)
        {
            if (parameter.name == k_stunParam)
            {
                controller.RemoveParameter(parameter);
                return;
            }
        }
    }

    // 기절 상태와 그 상태로 향하는 전환을 제거한다 (RemoveDownStates와 같은 방식).
    private static void RemoveStunStates(AnimatorStateMachine stateMachine)
    {
        foreach (ChildAnimatorState child in stateMachine.states)
        {
            var toRemove = new System.Collections.Generic.List<AnimatorStateTransition>();
            foreach (AnimatorStateTransition transition in child.state.transitions)
            {
                if (
                    transition.destinationState != null
                    && transition.destinationState.name == k_stunState
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

        // 기절 상태 자체 제거 (하나뿐이라 찾으면 끝낸다 — 컬렉션이 바뀌므로 계속 돌지 않는다)
        foreach (ChildAnimatorState child in stateMachine.states)
        {
            if (child.state.name == k_stunState)
            {
                stateMachine.RemoveState(child.state);
                return;
            }
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
