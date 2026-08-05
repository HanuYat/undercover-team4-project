using System.Text;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 사망 래그돌 — 기능 정지(<see cref="IncapacitationCause.Die"/>) 동안 애니메이터를 끄고 뼈를 물리에
/// 넘긴 뒤, 착지·정착하면 다시 애니메이터로 되돌린다. (#506)
///
/// <b>표현 계층 전용이다.</b> 뼈를 동기화하지 않는다 — 판정은 서버 트랜스폼(CharacterController +
/// 오너 권한 NetworkTransform)이 계속 쥐고, 이 컴포넌트는 모든 피어에서 <b>로컬로</b> 같은 규칙으로 돈다.
/// 그래서 NetworkBehaviour가 아니고, 매니저도 아니라 App 파사드와 무관하다(architecture.md R1~R8 해당 없음).
///
/// <b>진입 조건은 폭발이 아니라 사망이다.</b> 폭발·진압봉·납치 린치는 모두 HP 0 → Die로 수렴하므로
/// (<see cref="PlayerHealth.SetHp"/>, #524), 진입을 Die 하나로 잡으면 사망 경로가 몇 개든 전부 같은
/// 래그돌을 탄다. 폭발이 특별한 것은 <b>임펄스가 붙는다</b>는 점 하나뿐이다.
///
/// <b>Animator를 이 컴포넌트가 단독으로 켜고 끈다.</b> <see cref="PlayerAnimationDriver"/>는 파라미터만
/// 쓴다 — 같은 Animator를 두 컴포넌트가 만지므로 역할을 이렇게 갈라 둔다. 래그돌이 켜져 있는 동안
/// AnimationDriver는 <c>Down</c>을 내리지 못한다(<see cref="IsRagdollActive"/>를 보고 참으로 붙든다) —
/// 안 막으면 부활 블렌드 도중에 기상 모션이 먼저 시작된다.
///
/// <b>프리팹 루트에 붙인다</b> — CharacterController·PlayerIncapacitation과 같은 오브젝트.
/// <see cref="PlayerMovement"/>·<see cref="PlayerHeadLook"/>이 <c>GetComponent</c>로,
/// <see cref="PlayerAnimationDriver"/>가 <c>GetComponentInParent</c>로 찾는다.
/// </summary>
public class PlayerRagdoll : MonoBehaviour
{
    /// <summary>래그돌 뼈 콜라이더 전용 레이어 — <c>PlayerRagdollBuilder</c>가 만든다.</summary>
    public const string k_layerName = "Ragdoll";

    /// <summary>
    /// 몸통 리그 최상단 — 플레이어 루트의 <b>직속</b> 자식이다.
    ///
    /// <b>이름으로 뼈를 찾을 때는 반드시 이 아래에서만 찾아야 한다.</b> 프리팹에는 뼈 이름이 완전히
    /// 같은 리그가 <b>두 벌</b> 있다 — 1인칭 팔(<c>Camera/FPArm_Right/Root/...</c>)이 <c>FPArmGenerator</c>가
    /// 뽑은 리그 복사본이라 <c>Hips</c>·<c>Spine_02</c>·<c>Shoulder_R</c>가 그쪽에도 그대로 있다.
    /// 게다가 <c>Camera</c>가 자식 순서상 <c>Root</c>보다 앞이라, 프리팹 전체를 훑어 첫 매치를 집으면
    /// <b>전부 FP 팔 쪽이 걸린다</b>. 그러면 사망 시 1인칭 팔이 물리로 풀려 바닥으로 떨어진다(실제로 밟았다).
    /// </summary>
    public const string k_boneRootName = "Root";

    // ---- 프리팹에 저장할 수 없는 Rigidbody 물리값 ----
    //
    // ⚠ 이 셋은 <b>Rigidbody의 직렬화 필드가 아니다.</b> Player.prefab의 Rigidbody 블록을 열어 보면
    // m_CollisionDetection에서 끝나고 solver·depenetration 항목이 아예 없다 — 에디터 스크립트에서
    // 아무리 써 넣어도 저장되지 않고, 인스턴스가 만들어질 때마다 Physics 프로젝트 기본값
    // (DynamicsManager.asset: 6 / 1 / 10)으로 되돌아온다. 그래서 <b>런타임에</b> 건다.
    // (레이어·isKinematic·보간·CCD·관절 preprocessing/projection은 직렬화되므로 에디터 셋업이 맡는다)

    // 겹침 탈출 속도 상한 — 안 걸면 기본값 10m/s로 튕겨나간다. 착지 순간 지형에 깊게 파고든 뼈가
    // 하나만 있어도 그 한 번의 탈출이 관절을 타고 몸 전체로 퍼져 시체가 발작하듯 튄다.
    private const float k_maxDepenetrationVelocity = 3f;

    // 관절 projection을 껐기 때문에(PlayerRagdollSetup의 k_enableProjection 주석) 관절을 붙드는 일은
    // 전적으로 solver 반복이 맡는다 — 기본값 6/1로는 강한 임펄스에서 관절이 눈에 띄게 늘어난다.
    private const int k_solverIterations = 12;
    private const int k_solverVelocityIterations = 4;

    // 지면을 못 찾아도 결국은 정착시키는 최후 배수 — m_settleTimeoutSeconds의 몇 배까지 기다릴지.
    // 맵 밖으로 떨어져 나간 시체가 Ragdoll 상태에 영원히 갇히지 않게 하는 안전장치일 뿐이다.
    // 이 경로로 들어오면 시체는 허공에 굳지만(위 Update 주석) 상태 기계는 계속 돈다 —
    // 되살릴 때 판정을 쥔 것은 루트이므로 부활·라운드 리셋은 정상 동작한다.
    private const float k_lostBodyTimeoutFactor = 4f;

    // 원격 정렬이 "끝났다"로 보는 수평 잔차(m) — 이 안에 들어오면 정착해도 굳는 오프셋이 눈에 띄지 않는다.
    private const float k_alignedTolerance = 0.05f;

    // 캡슐 추종에서 "스윕이 도달했다"로 보는 잔차(m) — 이 아래면 텔레포트로 메우지 않는다.
    private const float k_followResidualEpsilon = 0.01f;

    // 부활 블렌드가 물려 들어가는 상태 — PlayerAnimatorControllerBuilder의 k_groundState와 같아야 한다.
    private static readonly int s_groundStateHash = Animator.StringToHash("Knockdown_Ground");

    private enum RagdollState
    {
        Animated, // 평시 — 전 Rigidbody 키네마틱, 애니메이터가 포즈를 쥔다
        Ragdoll, // 물리 중 — 애니메이터 정지, 임펄스로 날아가는 구간

        // 착지 정착 — 골반만 키네마틱으로 고정하고 나머지 뼈는 물리에 남긴다(PinHipsOnly).
        // 시체가 캡슐을 따라오면서도 끌려다니는 동안 몸이 계속 흔들린다.
        Settled,
        BlendingToAnimator, // 정착 포즈 → 애니메이터 포즈 보간 (부활)
    }

    [Header("임펄스")]
    [Tooltip("골반보다 높은 뼈에 얹는 추가 속도 비율(1/m) — 상체가 더 빨라 다리가 끌리는 텀블이 생긴다. " +
             "폭심 기준 AddExplosionForce 대신 이걸 쓰는 이유는 결정론이다(피어마다 같은 결과)")]
    [SerializeField] private float m_tumbleBias = 0.8f;

    [Header("정착 판정")]
    [Tooltip("뼈 평균 속도(m/s)가 이 아래로 내려가면 멈춘 것으로 본다")]
    [SerializeField] private float m_settleSpeedThreshold = 0.15f;

    [Tooltip("위 속도 조건이 이만큼 유지되어야 정착으로 확정한다(초) — 한 프레임 튀는 값에 속지 않게")]
    [SerializeField] private float m_settleHoldSeconds = 0.3f;

    [Tooltip("정착 판정 타임아웃(초) — 지형에 껴서 영원히 떨리는 경우의 안전장치")]
    [SerializeField] private float m_settleTimeoutSeconds = 5f;

    [Header("정착 후 정렬")]
    [Tooltip("시체 밑 지면을 찾는 레이캐스트 마스크 — 지형(Default). 래그돌 뼈는 다른 레이어라 걸리지 않는다")]
    [SerializeField] private LayerMask m_groundMask = 1;

    [Tooltip("골반 아래로 지면을 찾는 거리(m). 짧게 잡을 것 — 길면 얇은 실내 바닥을 뚫고 아래층 지면을 " +
             "찾아내 시체가 한 층 밑으로 순간이동한다. 못 찾으면 골반 높이를 쓰고 남은 차이는 중력이 메운다")]
    [SerializeField] private float m_groundProbeDistance = 1.5f;

    [Tooltip("루트 yaw를 몸이 누운 방향에 맞춘다 — 기상 모션이 '루트 전방을 향해 누워 있다'를 전제하므로. " +
             "비행 중에도 매 프레임 맞춘다(FollowBodyYaw) — 정착 때 한 번에 돌리면 그 회전이 원격에 " +
             "늦게 도착해 시체가 루트를 축으로 휙 돈다")]
    [SerializeField] private bool m_alignRootYawToBody = true;

    [Tooltip("몸 방향 대비 루트 yaw 보정(도) — Knockdown_StandUp 클립이 어느 쪽을 머리로 보는지에 맞춘다. " +
             "Editor에서 부활을 눌러 보며 조정할 값이다")]
    [SerializeField] private float m_rootYawOffset;

    [Tooltip("원격 피어가 착지한 시체를 오너 위치로 당겨오는 속도(m/s) — 비행 중에는 당기지 않는다. " +
             "크게 잡으면 스냅처럼 보이고 시체 궤적을 캡슐 궤적으로 덮는다(TickAlignBonesToRoot 주석)")]
    [SerializeField] private float m_alignPullSpeed = 1.5f;



    [Header("애니메이터 복귀")]
    [Tooltip("정착 포즈 → 애니메이터 포즈 보간 시간(초)")]
    [SerializeField] private float m_blendSeconds = 0.4f;

    [Header("진단")]
    [Tooltip("래그돌 진입·정착 시점의 위치·지면 판정을 Console에 남긴다 — 원인 추적용 임시 스위치")]
    [SerializeField] private bool m_debugLog = true;

    [Tooltip("뼈 속도가 한 물리 스텝에서 튀는 순간을 그때의 접촉 상대·루트 이동과 함께 남긴다 " +
             "— '마지막에 몇 번 튄다'의 원인 추적용 임시 스위치")]
    [SerializeField] private bool m_diagnoseBounce = true;

    [Tooltip("한 물리 스텝(0.02초)에서 속도가 이만큼(m/s) 이상 변하면 튐으로 보고 리포트한다")]
    [SerializeField] private float m_bounceReportThreshold = 1f;

    private Animator m_animator;
    private CharacterController m_controller;
    private PlayerIncapacitation m_incapacitation;
    private PlayerMovement m_movement;
    private NetworkObject m_netObject;

    private Transform m_root; // CharacterController가 붙은 트랜스폼 = 판정·동기화의 주체
    private Transform m_boneRoot; // 리그 최상단('Root') — 뼈·스킨 수집 범위를 여기로 못박는다

    private Rigidbody[] m_bodies; // 래그돌 레이어의 뼈 Rigidbody만 (손에 든 아이템의 rb가 섞이지 않게)
    private Transform m_hipsBone; // 관절이 없는 뼈 = 래그돌 루트
    private Transform m_headBone; // 누운 방향(yaw) 계산용
    private Transform[] m_allBones; // 리그 전체 — 부활 블렌드는 물리를 안 받은 뼈까지 보간해야 한다

    // 몸통 스킨드 메시 — 래그돌 동안 컬링 바운즈를 매 프레임 재계산시켜야 한다(아래 SetSkinsAlwaysVisible)
    private SkinnedMeshRenderer[] m_skins;
    private bool[] m_skinUpdateWhenOffscreen;

    private Vector3[] m_capturedPositions; // 정착 순간의 뼈 월드 위치 (재정렬 전후를 잇는다)
    private Quaternion[] m_capturedRotations;
    private Quaternion[] m_blendFromRotations; // 정착 포즈(로컬) — 부활 블렌드의 출발점
    private Vector3 m_blendFromHipsLocalPosition;

    private RagdollState m_state = RagdollState.Animated;
    private float m_stillTimer;
    private float m_elapsedInRagdoll;
    private float m_blendTimer;

    // 늦게 접속했는데 대상이 이미 죽어 있던 경우 — 이번 사망은 래그돌을 건너뛴다.
    // 그때의 물리 낙하는 "죽는 순간"이 아니라 이미 끝난 과거라, 재생하면 시체가 뒤늦게 한 번 더 무너진다.
    // (PlayerIncapacitation.RefreshAimHitbox가 스폰 시 한 번 상태를 맞추는 것과 같은 계열의 처리)
    private bool m_skipThisEpisode;
    private bool m_polledOnce;
    private bool m_capsuleWasEnabled = true; // 캡슐 충돌 무시 재적용 판정 (IgnoreOwnCapsule 주석 참고)
    private bool m_reportedFollowStall; // 캡슐 추종 실패를 이번 사망에서 이미 찍었는가 (임시 진단)

    // 사망 시점의 루트 위치 — 정착 로그가 "캡슐이 시체를 따라 얼마나 갔나"를 찍는 기준.
    //
    // 이 값이 필요한 이유는 진단 경로가 반쪽이기 때문이다. 캡슐 추종은 오너에서만 돌고
    // (TickCapsuleFollow) 그 실패 로그도 오너 콘솔에만 남는데, MPPM 가상 플레이어의 콘솔은 MCP로
    // 읽히지 않는다(§9-8). 원격 콘솔에서 루트 이동량을 볼 수 있으면 <b>클라가 죽은 경우에도
    // 호스트 콘솔만으로</b> 추종 성공 여부가 갈린다 — 시체가 5m 날았는데 루트 이동이 0이면 추종 실패다.
    private Vector3 m_entryRootPosition;

    // 진단 전용 (아래 '임시 진단' 구역 참고)
    private Vector3[] m_diagPreviousVelocities;
    private string[] m_diagContacts;
    private Vector3 m_diagPreviousRootPosition;

    /// <summary>
    /// 래그돌이 애니메이터로부터 포즈를 빼앗고 있는가 — <see cref="PlayerMovement.AddKnockback"/>·
    /// <see cref="PlayerAnimationDriver"/>·<see cref="PlayerHeadLook"/>이 각자 물러나는 판정에 쓴다.
    /// 정착 후에도, 부활 블렌드 중에도 참이다 — 그 구간에도 뼈의 주인은 이쪽이다.
    /// </summary>
    public bool IsRagdollActive => m_state != RagdollState.Animated;

    /// <summary>
    /// 캡슐이 시체를 따라가야 하는 구간인가 — <see cref="PlayerMovement.Update"/>가 입력 이동 대신
    /// <see cref="TickCapsuleFollow"/>를 돌리는 판정. 호송·운반(<c>PlayerTowedMotion</c>)이 입력 이동을
    /// 대신하는 것과 같은 자리이고, 몸을 끄는 주체가 남이 아니라 <b>자기 뼈 물리</b>라는 점만 다르다.
    /// </summary>
    internal bool IsCapsuleFollowingBody => m_state == RagdollState.Ragdoll && HasMoveAuthority;

    // 이동 권한 — 오너(또는 세션 없는 오프라인 Play)만 루트를 옮길 수 있다.
    // 서버가 남의 캐릭터를 옮겨봤자 오너 권한 NetworkTransform이 되돌린다(BombExplosionView 주석과 같은 논리).
    private bool HasMoveAuthority =>
        m_netObject == null || !m_netObject.IsSpawned || m_netObject.IsOwner;

    private void Awake()
    {
        m_animator = GetComponentInChildren<Animator>();
        m_controller = GetComponentInParent<CharacterController>();
        m_incapacitation = GetComponentInParent<PlayerIncapacitation>();
        m_movement = GetComponentInParent<PlayerMovement>();
        m_netObject = GetComponentInParent<NetworkObject>();

        // 판정의 주체는 CharacterController가 붙은 트랜스폼이다 — 이 컴포넌트가 프리팹 어디에 붙어도
        // 같은 것을 가리키게 한다.
        m_root = m_controller != null ? m_controller.transform : transform;

        CollectBones();
    }

    // 래그돌 뼈를 모은다.
    //
    // 범위를 몸통 리그(k_boneRootName)로 못박는 것이 핵심이다 — 1인칭 팔이 뼈 이름이 같은 리그
    // 복사본이라, 플레이어 전체를 훑으면 FP 팔 뼈가 섞인다(k_boneRootName 주석 참고).
    // 그 위에 레이어로 한 번 더 거른다 — 손에 든 아이템(HeldItemAnchor 아래)의 Rigidbody가
    // 섞여 들어와 아이템이 래그돌의 일부가 되는 것을 막는다.
    private void CollectBones()
    {
        m_bodies = new Rigidbody[0];

        int layer = LayerMask.NameToLayer(k_layerName);
        if (layer < 0)
        {
            Debug.LogError(
                $"[래그돌] 레이어 '{k_layerName}'가 없다 — Tools > Player > Build Ragdoll을 먼저 실행할 것",
                this
            );
            return;
        }

        m_boneRoot = m_root.Find(k_boneRootName);
        if (m_boneRoot == null)
        {
            Debug.LogError(
                $"[래그돌] 몸통 리그 '{k_boneRootName}'를 플레이어 루트의 직속 자식에서 찾지 못했다",
                this
            );
            return;
        }

        Rigidbody[] all = m_boneRoot.GetComponentsInChildren<Rigidbody>(true);
        int count = 0;
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i].gameObject.layer == layer)
                count++;
        }

        m_bodies = new Rigidbody[count];
        int next = 0;
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i].gameObject.layer != layer)
                continue;

            m_bodies[next++] = all[i];

            // 관절이 없는 뼈가 래그돌 루트(골반)다 — 빌더가 하나만 그렇게 만든다
            if (m_hipsBone == null && all[i].GetComponent<CharacterJoint>() == null)
                m_hipsBone = all[i].transform;
            if (all[i].name == "Head")
                m_headBone = all[i].transform;
        }

        m_capturedPositions = new Vector3[count];
        m_capturedRotations = new Quaternion[count];

        // 골반(관절 없는 뼈)이 없으면 정착 재정렬·임펄스 기준이 없다 — 반쯤 도는 것보다 끄는 편이 낫다
        if (count == 0 || m_hipsBone == null)
        {
            Debug.LogError(
                $"[래그돌] {m_boneRoot.name} 아래에서 래그돌 뼈를 제대로 찾지 못했다"
                    + $" (뼈 {count}개, 골반 {(m_hipsBone == null ? "없음" : m_hipsBone.name)})"
                    + " — Tools > Player > Build Ragdoll을 실행할 것",
                this
            );
            m_bodies = new Rigidbody[0];
            return;
        }

        ApplyRuntimePhysics(); // 프리팹이 들고 있을 수 없는 값 — 위 상수 주석 참고
        SetUpBounceDiagnostics();
        SetKinematic(true); // 평시는 애니메이터가 포즈를 쥔다

        // 부활 블렌드는 물리를 받지 않은 뼈(척추 사이·목·손가락·발)까지 보간해야 한다 — 그것들은
        // 애니메이터가 꺼진 순간의 포즈에 멈춰 있어, 안 섞으면 블렌드 시작 프레임에 목과 손이 튄다.
        m_allBones = m_boneRoot.GetComponentsInChildren<Transform>(true);
        m_blendFromRotations = new Quaternion[m_allBones.Length];

        CollectSkins();
        IgnoreOwnCapsule();
    }

    // 이 리그가 구동하는 스킨드 메시를 모은다 — rootBone이 몸통 리그 안에 있는 것만.
    // 메시(SM_Gen_Chr_Robot_01)는 리그의 자식이 아니라 <b>형제</b>라 계층으로는 못 찾고,
    // 1인칭 팔도 같은 이름의 메시를 들고 있어 이름으로도 못 가른다.
    private void CollectSkins()
    {
        SkinnedMeshRenderer[] all = m_root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        int count = 0;
        for (int i = 0; i < all.Length; i++)
        {
            if (DrivenByBodyRig(all[i]))
                count++;
        }

        m_skins = new SkinnedMeshRenderer[count];
        m_skinUpdateWhenOffscreen = new bool[count];
        int next = 0;
        for (int i = 0; i < all.Length; i++)
        {
            if (!DrivenByBodyRig(all[i]))
                continue;
            m_skins[next] = all[i];
            m_skinUpdateWhenOffscreen[next] = all[i].updateWhenOffscreen;
            next++;
        }
    }

    private bool DrivenByBodyRig(SkinnedMeshRenderer skin) =>
        skin.rootBone != null
        && (skin.rootBone == m_boneRoot || skin.rootBone.IsChildOf(m_boneRoot));

    /// <summary>
    /// 래그돌 동안 컬링 바운즈를 매 프레임 재계산시킨다.
    ///
    /// <b>안 하면 시체가 화면에서 사라진다.</b> SkinnedMeshRenderer의 컬링 바운즈는 <c>rootBone</c>
    /// 기준으로 한 번 계산된 값인데, 래그돌은 <c>Hips</c> 이하만 움직이고 rootBone(리그 최상단)은
    /// 사망 지점에 그대로 남는다 — 몸이 날아간 뒤에도 바운즈는 사망 지점에 있어서, 화면 안에 있는
    /// 몸이 컬링돼 통째로 안 보이게 된다. 래그돌의 고전적인 함정이다.
    ///
    /// 비용이 있으므로(매 프레임 스킨 바운즈 계산) 래그돌이 켜져 있는 동안만 올린다.
    /// </summary>
    private void SetSkinsAlwaysVisible(bool always)
    {
        if (m_skins == null)
            return;

        for (int i = 0; i < m_skins.Length; i++)
            m_skins[i].updateWhenOffscreen = always || m_skinUpdateWhenOffscreen[i];
    }

    // 자기 CharacterController 캡슐과의 충돌을 끈다.
    //
    // 충돌 매트릭스로는 못 한다 — 캡슐이 지형과 같은 Default 레이어다(Player 프리팹 루트 m_Layer = 0).
    // 지형 충돌을 켜면 캡슐 충돌도 같이 켜지는데, 죽는 순간 래그돌은 자기 캡슐 <b>안에서</b> 출발하므로
    // 그대로 두면 깊게 겹친 상태로 시작하고, 그걸 밀어내는 힘에 몸이 발작처럼 튄다(실제로 밟았다).
    //
    // ⚠ <b>이 상태는 콜라이더를 껐다 켜면 초기화된다</b>(Unity 사양). Awake에서 한 번 걸어 두는 것으로는
    // 부족하다 — 우리 밖에서 CharacterController를 껐다 켜는 경로가 여럿이다:
    //  · <see cref="PlayerMovement"/>의 스폰 포즈 적용·텔레포트(SetPose) — <b>같은 프레임 안에서</b>
    //    껐다 켜므로 폴링으로는 전이를 볼 수도 없다
    //  · 호송·운반(PlayerTowedMotion, #279/#365) — 여러 프레임 동안 꺼 둔다
    // 그래서 세 곳에서 다시 건다: 래그돌 진입 직전, 뼈를 다시 물리로 놓아줄 때, 그리고 캡슐이 꺼졌다
    // 켜진 것이 관측될 때(Update).
    //
    // 남의 캡슐은 그대로 둔다 — 시체가 통행을 방해하는 것은 오히려 자연스럽고, 무엇보다 죽는 순간
    // 남의 캡슐이 내 몸 안에 겹쳐 있는 경우는 없다.
    private void IgnoreOwnCapsule()
    {
        if (m_controller == null || m_bodies == null)
            return;

        for (int i = 0; i < m_bodies.Length; i++)
        {
            Collider bone = m_bodies[i].GetComponent<Collider>();
            if (bone != null)
                Physics.IgnoreCollision(bone, m_controller, true);
        }
    }

    // 여러 프레임에 걸쳐 꺼져 있던 캡슐(호송·운반)이 다시 켜지는 순간을 잡아 무시를 다시 건다.
    // 같은 프레임 안에서 껐다 켜는 경로(PlayerMovement.SetPose)는 여기서 볼 수 없으므로,
    // 그쪽은 래그돌 진입 시점의 재적용이 담당한다.
    private void RefreshCapsuleIgnoreOnReenable()
    {
        if (m_controller == null)
            return;

        bool enabledNow = m_controller.enabled;
        if (enabledNow && !m_capsuleWasEnabled)
            IgnoreOwnCapsule();
        m_capsuleWasEnabled = enabledNow;
    }

    // CharacterController를 껐다 켜면 IgnoreCollision 상태가 초기화된다(Unity 사양) — 켤 때마다 다시 건다.
    private void SetControllerEnabled(bool value)
    {
        if (m_controller == null)
            return;

        m_controller.enabled = value;
        if (value)
            IgnoreOwnCapsule();
    }

    // 직렬화되지 않는 Rigidbody 값을 인스턴스마다 다시 건다 — 상수 주석에 이유가 적혀 있다.
    private void ApplyRuntimePhysics()
    {
        for (int i = 0; i < m_bodies.Length; i++)
        {
            m_bodies[i].maxDepenetrationVelocity = k_maxDepenetrationVelocity;
            m_bodies[i].solverIterations = k_solverIterations;
            m_bodies[i].solverVelocityIterations = k_solverVelocityIterations;
        }
    }

    private void SetKinematic(bool kinematic)
    {
        for (int i = 0; i < m_bodies.Length; i++)
            SetBodyKinematic(m_bodies[i], kinematic);
    }

    // 보간을 키네마틱 여부와 함께 갈아탄다.
    //  · 물리 중 — Interpolate. 안 켜면 물리 틱(50Hz)이 그대로 보여 몸이 떨린다.
    //  · 키네마틱 — None. 켜 두면 물리가 스텝 사이 보간 포즈를 트랜스폼에 써서 애니메이터가 방금
    //    놓은 포즈를 한 스텝 늦은 값으로 덮는다 — 팔다리가 끌리고 스킨드 메시가 늘어난다.
    //
    // 속도는 <b>양쪽 전이에서 모두</b> 지운다 — 키네마틱으로 넘기기 <b>전에</b>, 물리로 돌려줄 때는
    // 돌려준 <b>뒤에</b>. (키네마틱 상태에서의 속도 대입은 적용이 보장되지 않아 순서가 갈린다)
    //
    //  · 넘기기 전 — 정착(<see cref="Settle"/>)이 한 프레임 안에서 키네마틱을 왕복하므로
    //    (SetKinematic(true) → <see cref="PinHipsOnly"/>) 안 지우면 정착 직전 속도가 되살아난다.
    //  · 돌려준 뒤 — <b>키네마틱인 동안에도 트랜스폼이 움직이면 PhysX는 그 이동에서 속도를
    //    유도하고, 그 속도는 isKinematic = false 시점에 살아난다.</b> 실측에서 접촉 없이 머리
    //    7.1m/s, 어깨·팔꿈치 6.0m/s가 나왔다 — 리그 루트에서 먼 뼈일수록 회전 반경이 커서 더
    //    빨랐다는 순서가 이 원인을 가리켰다. 캡슐 추종(<see cref="TickCapsuleFollow"/>)이
    //    들어와 큰 이동 자체가 사라졌지만, 운반·밧줄로 시체가 끌려다니는 동안에도 같은 조건이
    //    성립하므로 이 짝은 그대로 필요하다.
    private static void SetBodyKinematic(Rigidbody body, bool kinematic)
    {
        if (kinematic && !body.isKinematic)
        {
            body.linearVelocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
        }

        body.isKinematic = kinematic;
        body.interpolation = kinematic
            ? RigidbodyInterpolation.None
            : RigidbodyInterpolation.Interpolate;

        if (kinematic)
            return;

        body.linearVelocity = Vector3.zero;
        body.angularVelocity = Vector3.zero;
    }

    /// <summary>
    /// 정착 = 완전 정지가 아니다. <b>골반만 키네마틱으로 고정하고 나머지 뼈는 물리에 남긴다.</b>
    ///
    /// 골반은 관절이 없는 래그돌 루트이고 리그 루트의 자식이라, 키네마틱으로 두면 캡슐이 움직일 때
    /// 계층을 따라 함께 이동한다 — 동료가 시체를 운반하거나(#365) 밧줄로 끌 때(#398) 골반이 캡슐을
    /// 따라가고, 나머지 뼈는 관절에 매달려 끌려오며 자연스럽게 흔들린다.
    ///
    /// 양 극단은 각각 반쪽만 얻는다:
    ///  · 전부 키네마틱 — 운반은 되지만 몸이 하나의 자세로 굳는다(처음 구현이 이랬다)
    ///  · 전부 물리 — 흔들리지만 캡슐만 끌려가고 몸은 바닥에 남는다
    /// 골반만 고정하면 둘을 동시에 얻는다.
    /// </summary>
    private void PinHipsOnly()
    {
        IgnoreOwnCapsule(); // 뼈를 다시 물리로 놓아주기 전에 — 정착 중 캡슐 토글이 무시를 지웠을 수 있다

        for (int i = 0; i < m_bodies.Length; i++)
            SetBodyKinematic(m_bodies[i], m_bodies[i].transform == m_hipsBone);
    }

    // ---- 진입 / 이탈 ----

    /// <summary>
    /// 래그돌 진입 — <b>멱등이다.</b> 이미 물리 중이면 임펄스만 누적하고, 정착·블렌드 중이면 무동작.
    ///
    /// 멱등이어야 하는 이유는 원격 클라의 도착 순서다. 사망 사실은
    /// <see cref="PlayerIncapacitation"/>의 NetworkVariable로, 폭발 사망자 목록은
    /// <see cref="BombDevice"/>의 ClientRpc로 온다 — 서로 다른 오브젝트라 같은 틱에 실려 와도
    /// 콜백 순서가 보장되지 않는다. 순서를 맞추려 들지 말고 어느 쪽이 먼저 와도 결과가 같게 만든다.
    /// </summary>
    /// <param name="impulse">폭심에서 밀려나는 속도(m/s). 힘없이 무너지는 사망은 <see cref="Vector3.zero"/>.</param>
    public void EnterRagdoll(Vector3 impulse)
    {
        if (m_bodies == null || m_bodies.Length == 0)
            return;

        if (m_state == RagdollState.Ragdoll)
        {
            ApplyImpulse(impulse); // 늦게 도착한 폭발 정보 — 누적한다
            return;
        }

        if (m_state != RagdollState.Animated)
            return; // 이미 정착했거나 일어나는 중 — 다시 날리지 않는다

        m_state = RagdollState.Ragdoll;
        m_stillTimer = 0f;
        m_elapsedInRagdoll = 0f;
        m_reportedFollowStall = false;
        m_entryRootPosition = m_root.position;

        // 슬라이드 넉백과 이중으로 밀리지 않게 CharacterController 쪽 외력을 지운다.
        // "죽은 사람은 건너뛴다"는 판정을 BombExplosionView에 두지 않는 이유가 위 순서 문제다 —
        // 들어와도 무해하게 만드는 쪽이 순서와 무관하게 항상 옳다.
        m_movement?.ClearExternalVelocity();

        if (m_animator != null)
            m_animator.enabled = false;

        SetSkinsAlwaysVisible(true);
        IgnoreOwnCapsule(); // 여기가 결정적인 지점이다 — 캡슐 안에서 출발하므로 (IgnoreOwnCapsule 주석 참고)
        SetKinematic(false);
        ApplyImpulse(impulse);

        if (m_debugLog)
            Debug.Log(
                $"[래그돌] 진입 — {name} 뼈 {m_bodies.Length}개, 루트 {m_root.position.ToString("F2")},"
                    + $" 골반 {m_hipsBone.position.ToString("F2")}, 임펄스 {impulse.ToString("F2")},"
                    + $" 권한 {(HasMoveAuthority ? "오너" : "원격")}",
                this
            );
    }

    /// <summary>
    /// 날아가는 구간을 즉시 끝내고 정착 상태로 넘긴다 — 운반 시작(#365)처럼 외부 사정으로 몸을 캡슐에
    /// 붙여야 할 때. 정착해도 몸이 굳지는 않는다(<see cref="PinHipsOnly"/>) — 골반만 캡슐에 붙고
    /// 나머지는 계속 흔들린다.
    /// </summary>
    public void ForceSettle()
    {
        if (m_state == RagdollState.Ragdoll)
            Settle();
    }

    /// <summary>
    /// 애니메이터로 되돌린다.
    /// <paramref name="blend"/>가 참이면 정착 포즈에서 기상 자세로 보간하고(본부 부활),
    /// 거짓이면 즉시 되돌린다(라운드 리셋·씬 전환·despawn — 볼 사람이 없거나 봐서는 안 되는 경로).
    /// </summary>
    public void ExitToAnimator(bool blend)
    {
        if (m_state == RagdollState.Animated || m_bodies == null || m_bodies.Length == 0)
            return;

        if (m_state == RagdollState.Ragdoll)
            Settle(); // 날아가는 중이면 먼저 포즈를 확정한다

        // 정착 상태에서는 골반만 고정돼 있고 나머지 뼈는 물리에 남아 있다(<see cref="PinHipsOnly"/>) —
        // 애니메이터로 돌아가려면 전부 멈춰야 한다. 안 멈추면 블렌드가 놓는 포즈를 물리가 매 스텝 덮는다.
        SetKinematic(true);

        if (!blend || m_animator == null || m_state == RagdollState.BlendingToAnimator)
        {
            if (m_animator != null)
                m_animator.enabled = true;
            m_state = RagdollState.Animated;
            SetSkinsAlwaysVisible(false);
            return;
        }

        // 정착 포즈를 출발점으로 잡아 둔다 — 아래 LateUpdate가 애니메이터 포즈로 끌고 간다
        for (int i = 0; i < m_allBones.Length; i++)
            m_blendFromRotations[i] = m_allBones[i].localRotation;
        m_blendFromHipsLocalPosition = m_hipsBone.localPosition;

        m_animator.enabled = true;
        // Down은 아직 참이다(AnimationDriver가 IsRagdollActive를 보고 붙들고 있다) — 바닥 대기 자세로
        // 물려 들어가야 정착 포즈와의 거리가 가장 짧다. 여기서 Ground를 직접 찍는 이유다.
        m_animator.Play(s_groundStateHash, 0, 0f);
        m_animator.Update(0f); // 이번 프레임 LateUpdate에서 바로 섞으려면 포즈가 이미 평가돼 있어야 한다

        m_state = RagdollState.BlendingToAnimator;
        m_blendTimer = 0f;
    }

    // ---- 매 프레임 ----

    private void Update()
    {
        RefreshCapsuleIgnoreOnReenable();
        PollDeath();

        if (m_state != RagdollState.Ragdoll)
            return;

        m_elapsedInRagdoll += Time.deltaTime;

        float speed = 0f;
        for (int i = 0; i < m_bodies.Length; i++)
            speed += m_bodies[i].linearVelocity.magnitude;
        speed /= m_bodies.Length;

        m_stillTimer = speed <= m_settleSpeedThreshold ? m_stillTimer + Time.deltaTime : 0f;

        if (m_stillTimer < m_settleHoldSeconds && m_elapsedInRagdoll < m_settleTimeoutSeconds)
            return;

        if (!IsReadyToSettle()
            && m_elapsedInRagdoll < m_settleTimeoutSeconds * k_lostBodyTimeoutFactor)
            return;

        Settle();
    }

    /// <summary>
    /// 정착해도 되는가 — ① 골반 밑에 지면이 있다 ② 원격이면 오너 위치로 당겨오기가 끝났다.
    ///
    /// 두 조건은 같은 함정의 앞뒷면이다. <b>정착은 골반을 루트에 용접하므로, 그 순간 시체가 루트에서
    /// 떨어져 있으면 그 오프셋이 영구히 굳는다</b> — 원격은 루트를 옮길 권한이 없고 골반은 로컬
    /// 오프셋을 물고 키네마틱이 된다.
    ///
    /// ① 실측: 임펄스가 과했을 때 9m 위에서 타임아웃이 터져 골반이 루트로부터 <b>+8.95m</b>로 고정돼
    /// 시체가 허공에 매달렸다. <see cref="GroundUnder"/>의 옛 주석은 "못 찾으면 중력이 남은 차이를
    /// 메운다"고 했지만 원격에서는 거짓이다 — <see cref="PlayerMovement"/>가 꺼져 있어 중력이 돌지 않는다.
    ///
    /// ② 당겨오기는 <c>m_alignPullSpeed</c>(1.5m/s)로 제한되므로 실측 1.2m면 0.8초가 걸린다. 정지
    /// 판정은 0.3초라, 이 가드가 없으면 <b>거의 항상 당겨오는 도중에 정착</b>해 남은 델타가 굳는다.
    /// </summary>
    private bool IsReadyToSettle()
    {
        if (!HasGroundUnderHips())
            return false;
        if (HasMoveAuthority)
            return true; // 오너는 캡슐이 이미 시체를 따라와 있다 (TickCapsuleFollow)

        Vector3 delta = m_root.position - m_hipsBone.position;
        delta.y = 0f;
        return delta.sqrMagnitude <= k_alignedTolerance * k_alignedTolerance;
    }

    // 골반 밑에 지면이 있는가 — 정착 자격과 원격 정렬이 함께 쓴다. 탐색 거리는 정착 정렬과 같은 값을
    // 쓴다 (다른 값을 쓰면 "정착해도 된다"고 판단한 뒤 정렬이 지면을 못 찾는 모순이 생긴다).
    private bool HasGroundUnderHips() => TryGroundUnder(m_hipsBone.position, out _);

    // 사망 여부를 폴링한다 — 이벤트로는 잡을 수 없다.
    //
    // OnIncapacitatedChanged는 bool만 넘기고 <b>원인만 바뀌면 울리지 않는다</b>. 납치 린치 사망은
    // Abducted/Lynched → Die 전이라 bool이 그대로여서 이벤트가 아예 안 온다
    // (PlayerIncapacitation.HandleSyncedChanged 참고). PlayerAnimationDriver가 IsProne을 Update에서
    // 폴링하는 것과 같은 방식으로 맞춘다 — 전 피어가 같은 동기화값을 보므로 원격 뷰도 동일하게 돈다.
    private void PollDeath()
    {
        if (m_incapacitation == null)
            return;

        bool dead = m_incapacitation.IsDead;

        // 접속 직후 이미 죽어 있었다면 이번 사망은 건너뛴다 — 낙하는 이미 끝난 과거다.
        // 이 경우 AnimationDriver가 Down을 그대로 켜 Knockdown_Ground로 눕힌다(기존 동작).
        if (!m_polledOnce)
        {
            m_polledOnce = true;
            m_skipThisEpisode = dead;
        }

        if (!dead)
        {
            m_skipThisEpisode = false;
            if (m_state == RagdollState.Ragdoll || m_state == RagdollState.Settled)
                ExitToAnimator(blend: true); // 부활 — 정착 포즈에서 기상으로 잇는다
            return;
        }

        if (!m_skipThisEpisode && m_state == RagdollState.Animated)
            EnterRagdoll(Vector3.zero); // 힘없이 무너지는 사망(진압봉·린치). 폭발은 임펄스를 따로 준다
    }

    // ---- 임시 진단: 정착 무렵의 튐 추적 (#506) ----
    //
    // "자연스럽게 쓰러진 뒤 마지막에 한두 번 튄다"의 범인을 좁히기 위한 코드다. <b>확정되면 지운다.</b>
    // 한 물리 스텝에서 속도가 튄 순간을 잡아, 같은 스텝의 세 가지를 한 줄에 모아 찍는다 —
    // 셋 중 무엇이 같이 움직였는지가 판별점이다:
    //  · 어느 뼈가 얼마나 튀었는지 (Δv)
    //  · 그 뼈가 무엇과 부딪히고 있었는지 — <b>자기 캡슐이면 상대 이름이 Player로 찍힌다</b>
    //    (= Physics.IgnoreCollision이 풀렸다는 직접 증거)
    //  · 그 스텝에 루트(CharacterController)가 얼마나 움직였는지 — 크면 캡슐이 시체를 끌고 간 것이다
    //    (정착 후 골반은 키네마틱이라 루트의 자식으로 매달려 있다)
    private sealed class BoneContactProbe : MonoBehaviour
    {
        internal PlayerRagdoll m_owner;
        internal int m_index;

        private void OnCollisionEnter(Collision collision) =>
            m_owner.ReportContact(m_index, collision);

        private void OnCollisionStay(Collision collision) =>
            m_owner.ReportContact(m_index, collision);
    }

    private void SetUpBounceDiagnostics()
    {
        m_diagPreviousVelocities = new Vector3[m_bodies.Length];
        m_diagContacts = new string[m_bodies.Length];

        if (!m_diagnoseBounce)
            return;

        for (int i = 0; i < m_bodies.Length; i++)
        {
            BoneContactProbe probe = m_bodies[i].gameObject.AddComponent<BoneContactProbe>();
            probe.m_owner = this;
            probe.m_index = i;
        }
    }

    private void ReportContact(int index, Collision collision)
    {
        if (m_diagContacts == null || index < 0 || index >= m_diagContacts.Length)
            return;

        // 자기 캡슐은 이름으로 못 가른다 — 남의 캡슐도 "Player(Clone)"이다. 남의 캡슐과 닿는 것은
        // 정상이지만 자기 캡슐이면 §9-1(IgnoreCollision 소실)이 재발한 것이므로 갈라 찍는다.
        // 캡슐 추종(TickCapsuleFollow) 이후로는 시체가 항상 자기 캡슐 안에 있어 더 중요해졌다.
        string other = collision.collider == (Collider)m_controller
            ? "자기캡슐⚠"
            : collision.collider.name;

        m_diagContacts[index] =
            $"{other}(레이어 {collision.collider.gameObject.layer}, 충격 {collision.impulse.magnitude:F2})";
    }

    // FixedUpdate는 물리 스텝 <b>앞</b>에서 돈다 — 이 시점의 linearVelocity와 m_diagContacts는
    // 직전 스텝의 결과다. 그래서 "직전 스텝에서 속도가 얼마나 변했고 그때 무엇과 닿아 있었나"가
    // 같은 프레임에 맞아떨어진다.
    private void FixedUpdate()
    {
        if (!m_diagnoseBounce || m_bodies == null || m_bodies.Length == 0)
            return;

        if (m_state == RagdollState.Ragdoll || m_state == RagdollState.Settled)
            ReportBounce();

        for (int i = 0; i < m_bodies.Length; i++)
        {
            m_diagPreviousVelocities[i] = m_bodies[i].linearVelocity;
            m_diagContacts[i] = null;
        }

        m_diagPreviousRootPosition = m_root.position;
    }

    private void ReportBounce()
    {
        int worst = -1;
        float worstDelta = m_bounceReportThreshold;
        for (int i = 0; i < m_bodies.Length; i++)
        {
            float delta = (m_bodies[i].linearVelocity - m_diagPreviousVelocities[i]).magnitude;
            if (delta <= worstDelta)
                continue;

            worstDelta = delta;
            worst = i;
        }

        if (worst < 0)
            return;

        Vector3 rootStep = m_root.position - m_diagPreviousRootPosition;
        string capsule = m_controller == null
            ? "없음"
            : !m_controller.enabled
                ? "꺼짐"
                : m_controller.isGrounded ? "접지" : "공중";
        bool hipsKinematic =
            m_hipsBone.TryGetComponent(out Rigidbody hipsBody) && hipsBody.isKinematic;

        // ⚠ <b>줄바꿈을 넣지 말 것.</b> Unity 콘솔 목록은 앞 두 줄을 보여주지만 MCP로 읽으면
        // <b>첫 줄만</b> 온다 — 뼈별 Δv와 접촉 상대를 둘째 줄에 두면 정작 판별점이 잘려나간다.
        StringBuilder report = new StringBuilder();
        report.Append($"[래그돌/튐] t={Time.fixedTime:F2} {m_state}/{(HasMoveAuthority ? "오너" : "원격")}")
            .Append($" | 루트Δ y={rootStep.y * 1000f:+0.0;-0.0}mm")
            .Append($" 수평={new Vector2(rootStep.x, rootStep.z).magnitude * 1000f:F1}mm")
            .Append($" 수직속도={(m_movement != null ? m_movement.DiagnosticVerticalVelocity.ToString("F2") : "?")}")
            .Append($" 캡슐={capsule}")
            .Append($" | 골반키네={hipsKinematic} 루트↔골반={(m_hipsBone.position - m_root.position).magnitude:F2}m")
            .Append(" | ");

        for (int i = 0; i < m_bodies.Length; i++)
        {
            float delta = (m_bodies[i].linearVelocity - m_diagPreviousVelocities[i]).magnitude;
            if (delta < 0.5f && m_diagContacts[i] == null)
                continue;

            report.Append($"{m_bodies[i].name} Δv{delta:F2}");
            if (m_diagContacts[i] != null)
                report.Append($"←{m_diagContacts[i]}");
            report.Append("; ");
        }

        Debug.Log(report.ToString(), this);
    }

    private void LateUpdate()
    {
        if (m_state == RagdollState.BlendingToAnimator)
            TickBlend();

        // 원격의 시체를 스트리밍된 루트에 맞춘다 — 오너 쪽 짝(TickCapsuleFollow)은 PlayerMovement가
        // 돌린다(그쪽은 이 컴포넌트가 비활성인 원격에서도 순서를 보장해야 하는 시점·이동과 얽혀 있다).
        // 여기서 하는 이유: 원격에서는 PlayerMovement가 꺼져 있고(오너만 켜진다), NetworkTransform이
        // 이번 프레임에 적용한 루트 위치를 LateUpdate에서 읽어야 한 프레임 늦지 않는다.
        if (m_state == RagdollState.Ragdoll && !HasMoveAuthority)
            TickAlignBonesToRoot();
    }

    // ---- 캡슐 추종 (#506 — 이 설계의 중심) ----

    /// <summary>
    /// 비행 중 캡슐을 시체 밑으로 끌고 간다 — <b>오너 전용</b>이고
    /// <see cref="PlayerMovement.Update"/>가 입력 이동 대신 매 프레임 부른다.
    ///
    /// <b>왜 이게 중심인가.</b> 이걸 안 하면 캡슐은 사망 지점에 그대로 남고(실측: 비행 중 루트 이동
    /// 0.0mm, 루트↔골반 0.87→1.21m), 정착 순간 <b>한 번에 1.15m 텔레포트</b>한다. 그 한 번의 늦은
    /// 점프가 이 기능의 거의 모든 버그의 뿌리였다:
    ///  · 오너 — 텔레포트가 캡슐을 밀고, 키네마틱 골반이 캡슐에 매달려 시체를 통째로 끌어갔다(§9-4)
    ///  · 원격 — 그 점프가 NetworkTransform으로 <b>늦게</b> 도착해, 골반만 끌려가고 나머지 뼈는
    ///    바닥에 눌러앉아 관절 10개가 늘어났다 되튕겼다(§9-5). 언제 도착할지는 두 피어의 물리
    ///    발산이 정하므로 상한이 없어, 기다리는 방식으로는 맞출 수 없었다
    ///
    /// 매 프레임 따라가게 하면 텔레포트가 <b>cm 단위 잔차</b>로 줄고, 원격은 점프 대신 연속
    /// 스트림을 받는다. 수렴·릴리스 유예·되붙듦이 전부 필요 없어진다.
    ///
    /// 이동은 <b>스윕 우선 + 막힌 잔차만 텔레포트</b>다(아래 ①②). 스윕만으로는 원리적으로 못 따라가고,
    /// 항상 텔레포트하면 갈 수 있는 구간에서도 캡슐이 지형을 무시한다 — 둘을 합치면 평지에서는 지형을
    /// 존중하고 난간·공중에서만 텔레포트가 개입해 추종을 보장한다.
    /// </summary>
    internal void TickCapsuleFollow()
    {
        if (m_movement == null || m_hipsBone == null)
            return;

        // 골반 위치를 <b>3차원</b>으로 따라간다 — 수평만 맞추면 시체가 공중에 있는 동안 루트가 시체를
        // 대표하지 못하고, 이름표·운반 조준·부활 히트박스가 전부 루트에 붙어 있어 그만큼 어긋난다.
        Vector3 target = m_hipsBone.position;

        // ① 스윕으로 갈 수 있는 만큼 — 갈 수 있는 구간에서는 캡슐이 지형을 존중한다.
        m_movement.SweepTo(target);

        // ② 막힌 잔차는 텔레포트로 메운다.
        //
        // <b>스윕만으로는 원리적으로 못 따라간다.</b> 서 있는 1.8m 캡슐과 굴러가는 탄도 시체는 갈 수
        // 있는 곳이 다르다 — 난간 너머·공중·좁은 틈. 지형이 갈리는 순간 캡슐이 뒤처지고, 그때부터
        // 동기화되는 위치가 시체를 대표하지 않는다(실측: 시체 38.8m / 캡슐 0.23m, §9-11).
        //
        // 죽은 동안 <b>몸은 뼈</b>다 — 뼈가 지형과 충돌하고 물리에 구속된다. 캡슐은 "이 플레이어가
        // 어디 있나"를 기록하는 대리값이므로, 몸이 있는 자리에 놓는 것은 물리를 속이는 것이 아니다.
        // 호송(#279)이 CharacterController를 끄고 트랜스폼을 직접 옮기는 것과 같은 범주다.
        Vector3 residual = target - m_root.position;
        if (residual.sqrMagnitude > k_followResidualEpsilon * k_followResidualEpsilon)
        {
            SetControllerEnabled(false); // 켠 채로 옮기면 내부 캐시가 되돌린다 (SetPose와 같은 사정)
            m_root.position = target;
            SetControllerEnabled(true); // IgnoreCollision 재적용까지 여기서 (SetControllerEnabled 주석)
            ReportResidualTeleport(residual);
        }

        FollowBodyYaw();
    }

    // 스윕이 막혀 텔레포트로 메운 첫 순간을 찍는다 — 추종 자체는 보장되지만, 이 줄이 자주 보이면
    // 임펄스가 지형에 비해 과하다는 신호다(캡슐이 시체를 스윕으로 못 쫓아갈 만큼 멀리 난다).
    // (임시 진단 — §9-8 목록)
    private void ReportResidualTeleport(Vector3 residual)
    {
        if (!m_debugLog || m_reportedFollowStall)
            return;

        m_reportedFollowStall = true;
        Debug.Log(
            $"[래그돌] 캡슐 잔차 텔레포트 — {name} 스윕이 막혀 {residual.magnitude:F2}m를 메웠다."
                + " 추종은 유지된다",
            this
        );
    }

    // 캡슐의 yaw도 몸이 누운 방향에 맞춰 둔다.
    //
    // 정착 때 한 번에 돌리면 그 회전이 원격에 늦게 도착해 <b>위치 점프와 똑같은 문제가 회전으로</b>
    // 재현된다 — 정착 후 뼈는 루트의 자식이므로 루트가 돌면 시체가 루트를 축으로 휙 돈다.
    // 비행 중 계속 맞춰 두면 정착 시점에 이미 맞아 있어 돌릴 것이 없다.
    //
    // 캡슐은 yaw 대칭이라 물리적으로는 무해하다. 기상 모션(Knockdown_StandUp)이 "루트 전방을 향해
    // 누워 있다"를 전제하므로 부활에도 이 값이 필요하다 — 같은 계산을 ResolveSettledRootPose와
    // 공유한다(BodyYaw).
    private void FollowBodyYaw()
    {
        if (!m_alignRootYawToBody || !TryGetBodyYaw(out float yaw))
            return;

        m_root.rotation = Quaternion.Euler(0f, yaw, 0f);
    }

    // 몸이 누운 방향의 yaw — 골반→머리를 지면에 투영한 값. 비행 중 추종(FollowBodyYaw)과
    // 정착 정렬(ResolveSettledRootPose)이 같은 계산을 써야 정착 순간에 회전이 안 튄다.
    private bool TryGetBodyYaw(out float yaw)
    {
        yaw = 0f;
        if (m_headBone == null || m_hipsBone == null)
            return false;

        Vector3 lengthwise = m_headBone.position - m_hipsBone.position;
        lengthwise.y = 0f;
        if (lengthwise.sqrMagnitude < 0.0004f)
            return false; // 거의 수직으로 서 있다 — 방향을 못 정하니 기존 yaw를 유지한다

        yaw = Quaternion.LookRotation(lengthwise.normalized).eulerAngles.y + m_rootYawOffset;
        return true;
    }

    /// <summary>
    /// 원격 피어의 시체를 스트리밍된 루트에 맞춘다 — <b>원격 전용</b>, 비행 중 매 프레임.
    ///
    /// 오너의 캡슐이 골반을 따라오므로(<see cref="TickCapsuleFollow"/>) <b>스트리밍된 루트의 수평
    /// 위치가 곧 오너 골반의 수평 위치다</b> — 뼈를 따로 동기화하지 않고도 원격이 오너의 궤적을
    /// 받는다(#506 결정 4 "뼈를 동기화하지 않는다"를 지킨다).
    ///
    /// 리그 루트 오프셋으로는 못 고친다 — <b>동적 리지드바디는 부모 트랜스폼을 따르지 않는다.</b>
    /// 그래서 뼈마다 같은 델타를 더해 <b>강체로 평행이동</b>한다. 포즈·상대속도·관절이 보존되고,
    /// 동적 바디의 <c>position</c> 대입은 텔레포트라 속도가 유도되지 않는다(키네마틱과 반대 — §9-5).
    ///
    /// <b>매 프레임 보정하므로 발산이 누적되지 않는다.</b> 델타가 계속 작게 유지되는 것이 핵심이다 —
    /// 그래서 마지막에 1.2m를 흡수하는 구간이 아예 생기지 않는다. 피어별 착지점 편차를 감수하기로
    /// 한 #506 본문의 합의를 이 방식이 불필요하게 만든다.
    /// </summary>
    private void TickAlignBonesToRoot()
    {
        // ⚠ <b>비행 중에는 정렬하지 않는다.</b> 원격의 루트는 오너의 캡슐이고, 캡슐은 Move() 스윕으로
        // 지형을 타는 <b>접지된 CharacterController</b>다. 반면 시체는 포물체다 — <b>포물체와 지면
        // 스위퍼는 매 프레임 크게 갈라지는 것이 정상</b>이라, 그 차이를 전부 되돌리면 시체의 궤적이
        // 캡슐의 궤적으로 덮인다. 실측: 원격에서 수직 임펄스 13.3m/s를 정상으로 받았는데도 5초간
        // 수평 9cm만 이동했고 위로 뜨지 않았다(29m/s면 프레임당 0.5m씩 되돌리는 셈이라, 뼈가 매
        // 프레임 지형으로 밀려 들어가며 접촉·관절 풀이가 수직 성분까지 빼갔다).
        //
        // 정렬이 필요한 것은 <b>최종 정착 위치</b>뿐이다 — 비행 중 피어 간 차이는 #506이 감수하기로
        // 한 항목이고 눈에 보이지 않는다. 그래서 착지한 뒤에만 돌린다(판정은 정착 자격과 같은 것을
        // 써서 "정렬은 안 했는데 정착은 된다"가 생기지 않게 한다).
        // 비행 중에는 당기지 않는다 (위 ⚠ 참고) — 착지한 뒤에만 흡수한다.
        // 캡슐 추종이 루트를 시체에 붙여 두므로(TickCapsuleFollow) 비행 중 보정할 이유가 없고,
        // <b>보정하지 않는 것이 벽 통과를 막는 유일한 방법</b>이다 — 아래 주석 참고.
        if (!HasGroundUnderHips())
            return;

        Vector3 delta = m_root.position - m_hipsBone.position;
        delta.y = 0f; // 높이는 각 피어의 지형 충돌이 정한다 — 같은 지형이므로 편차가 작다
        float distance = delta.magnitude;
        if (distance < 1e-4f)
            return;

        // 스냅이 아니라 <b>당겨오기</b>다. 착지 후 구르는 중의 델타는 실측 0.15~0.23m이므로 이 속도면
        // 눈에 띄지 않게 흡수된다. 상한이 없으면 착지 직후 남은 수평 속도만큼을 매 프레임 되돌리게 되어
        // 위 함정이 작은 규모로 되풀이된다.
        float maxStep = m_alignPullSpeed * Time.deltaTime;
        if (distance > maxStep)
            delta *= maxStep / distance;

        SnapBonesBy(ClampByWall(delta));
    }

    /// <summary>
    /// 뼈를 옮기는 양을 벽에 막히는 지점까지로 깎는다.
    ///
    /// ⚠ <b>뼈의 <c>position</c> 대입은 텔레포트라 충돌을 정의상 무시한다.</b> 뼈 자체의 물리는
    /// 레이어 매트릭스(Ragdoll×Default)와 <c>ContinuousSpeculative</c> CCD로 벽을 뚫지 않지만,
    /// <b>우리가 옮기는 경로에는 그 둘이 관여하지 않는다</b> — 여기가 유일한 구멍이었다.
    /// 그래서 옮기기 전에 경로를 한 번 쏴 본다. 마스크는 지면 판정과 같은 것을 쓴다(지형 = Default).
    /// </summary>
    private Vector3 ClampByWall(Vector3 delta)
    {
        const float k_skin = 0.02f; // 벽에 딱 붙이지 않고 살짝 띄운다 — 겹치면 탈출 임펄스가 생긴다

        Vector3 from = m_hipsBone.position;
        if (
            !Physics.Linecast(
                from,
                from + delta,
                out RaycastHit hit,
                m_groundMask,
                QueryTriggerInteraction.Ignore
            )
        )
            return delta;

        return delta.normalized * Mathf.Max(0f, hit.distance - k_skin);
    }

    // 전 뼈를 같은 델타로 강체 평행이동한다 — 포즈·상대속도·관절이 보존된다.
    // 동적 바디의 position 대입은 텔레포트라 속도가 유도되지 않는다(키네마틱과 반대 — SetBodyKinematic 주석).
    private void SnapBonesBy(Vector3 delta)
    {
        for (int i = 0; i < m_bodies.Length; i++)
            m_bodies[i].position += delta;
    }

    // ---- 임펄스 ----

    // 전 뼈에 같은 속도를 주고(몸 전체가 예측 가능하게 날아간다), 골반보다 높은 뼈에만 조금 더 얹어
    // 텀블을 만든다. 폭심 기준 AddExplosionForce를 쓰지 않는 이유는 결정론이다 — 임펄스 벡터 하나만
    // 받으면 폭심·반경을 몰라도 되고, 난수 없이 전 피어가 같은 회전을 낸다.
    private void ApplyImpulse(Vector3 velocity)
    {
        if (velocity == Vector3.zero)
            return;

        float hipsHeight = m_hipsBone.position.y;
        for (int i = 0; i < m_bodies.Length; i++)
        {
            float lift = m_bodies[i].worldCenterOfMass.y - hipsHeight;
            m_bodies[i].linearVelocity += velocity * (1f + m_tumbleBias * lift);
        }
    }

    // ---- 정착 ----

    // 정착 순서를 지키지 않으면 몸이 튄다. 뼈는 루트의 자식이므로 루트를 옮기면 뼈도 딸려 간다:
    //   ① 전 뼈의 월드 포즈를 캡처
    //   ② 전 rb를 키네마틱으로 전환
    //   ③ 루트를 골반 밑 지면으로 이동 (오너 또는 오프라인만)
    //   ④ 캡처한 월드 포즈를 뼈에 다시 적용  → 여기까지 화면은 그대로다
    //
    // ②가 이 설계의 핵심이다. 정착 후 뼈가 다시 부모를 따라가므로 동료가 시체를 운반할 때(#365)
    // 시체가 같이 따라온다 — 없으면 캡슐만 끌려가고 몸은 바닥에 남는다.
    //
    // ③은 비행 중 캡슐이 이미 따라와 있으므로(<see cref="TickCapsuleFollow"/>) <b>cm 단위 잔차</b>만
    // 남는다 — 지면 높이 보정과, 지형 때문에 캡슐이 시체를 놓친 만큼이다. 원격은 아무것도 옮기지
    // 않는다: 루트는 비행 내내 오너 값을 스트리밍받았고 뼈는 그 루트에 맞춰져 있다
    // (<see cref="TickAlignBonesToRoot"/>). 흡수할 어긋남이 없으니 수렴도 유예도 없다.
    private void Settle()
    {
        for (int i = 0; i < m_bodies.Length; i++)
        {
            m_capturedPositions[i] = m_bodies[i].transform.position;
            m_capturedRotations[i] = m_bodies[i].transform.rotation;
        }

        Vector3 landedHips = m_hipsBone.position;

        SetKinematic(true);

        // 지면 판정과 루트 이동을 <b>CharacterController를 끈 상태에서</b> 함께 한다.
        //  · 켜진 채로 transform을 옮기면 내부 캐시가 위치를 되돌린다 (PlayerMovement.SetPose와 같은 사정)
        //  · 켜진 채로 지면 레이를 쏘면 자기 캡슐에 걸린다 (캡슐도 Default 레이어다)
        SetControllerEnabled(false);

        Vector3 rootPosition = m_root.position;
        Quaternion rootRotation = m_root.rotation;
        ResolveSettledRootPose(landedHips, ref rootPosition, ref rootRotation);

        if (m_debugLog)
            Debug.Log(
                $"[래그돌] 정착 — {name} 골반 {landedHips.ToString("F2")} → 루트"
                    + $" {m_root.position.ToString("F2")} → {rootPosition.ToString("F2")}"
                    + $" (루트 텔레포트 {(rootPosition - m_root.position).magnitude:F2}m,"
                    // 비행 중 시체가 간 거리 vs 캡슐이 따라간 거리 — 두 값이 비슷해야 추종이 성공한 것이다.
                    // 시체는 갔는데 루트가 0이면 캡슐이 못 따라갔다는 뜻이고, 그때부터 루트는 시체를
                    // 대표하지 않는다(§3-4의 전제가 깨진다).
                    + $" 시체 이동 {HorizontalDistance(landedHips, m_entryRootPosition):F2}m,"
                    + $" 루트 이동 {HorizontalDistance(m_root.position, m_entryRootPosition):F2}m,"
                    + $" 경과 {m_elapsedInRagdoll:F2}s"
                    + $"{(m_elapsedInRagdoll >= m_settleTimeoutSeconds ? "/타임아웃" : "/정지판정")},"
                    + $" 권한 {(HasMoveAuthority ? "오너" : "원격")})",
                this
            );

        if (HasMoveAuthority)
        {
            m_root.SetPositionAndRotation(rootPosition, rootRotation);

            // 텔레포트로 도착했으니 쌓인 수직 속도를 지운다 — <see cref="PlayerMovement.SetPose"/>가
            // 같은 이유로 하는 처리다(#189: "낙하 도중 텔레포트되면 쌓인 수직 속도가 그대로 남아
            // 도착지에서 바닥을 파고들거나 튀어오른다"). 여기는 SetPose를 거치지 않고 트랜스폼을
            // 직접 옮기므로 그 짝이 필요하다 — 남은 속도가 캡슐을 밀면 <b>키네마틱 골반이
            // 매달려 있어 시체가 통째로 끌려간다.</b>
            m_movement?.ClearExternalVelocity();
        }

        SetControllerEnabled(true);
        RestoreCapturedWorldPoses();

        // 루트가 더 이상 나중에 점프하지 않으므로(위 주석) 양쪽 모두 곧장 물리로 놓아준다 —
        // 누운 몸이 계속 흔들리게. 원격의 릴리스 타이밍을 재던 유예 구간은 이 설계에서 사라졌다.
        PinHipsOnly();

        m_state = RagdollState.Settled;
    }

    // 정착 후 루트가 있어야 할 포즈 — 골반 밑 지면 위, 몸이 누운 방향을 향해.
    //
    // yaw까지 맞추는 이유는 기상 모션이다. Knockdown_StandUp은 "루트 전방을 향해 등을 대고 누워
    // 있다"를 전제하므로, 래그돌이 옆으로 굴러 있으면 부활 블렌드에서 몸이 휙 돌아간다.
    private void ResolveSettledRootPose(
        Vector3 landedHips,
        ref Vector3 position,
        ref Quaternion rotation
    )
    {
        // 지면 점에 루트 원점을 그대로 놓으면 안 된다 — 루트 원점은 캡슐 밑면이 아니다.
        // 캡슐은 center.y ± height/2 범위라 밑면이 루트보다 (center.y - height/2)만큼 위에 있고
        // (이 프리팹은 3cm), 그대로 두면 캡슐이 떠서 출발한다. CharacterController는 Move()를 한 번
        // 돌기 전까지 isGrounded가 거짓이라 그동안 중력이 쌓이고, 키네마틱 골반이 캡슐에 매달려
        // 있으므로 그 낙하가 시체를 통째로 끌어내린다.
        position = GroundUnder(landedHips) - Vector3.up * CapsuleBottomOffset;

        // 비행 중 이미 맞춰 온 값이라 보통 잔차만 남는다 — 추종이 꺼져 있거나 오프라인일 때가 본작업.
        if (m_alignRootYawToBody && TryGetBodyYaw(out float yaw))
            rotation = Quaternion.Euler(0f, yaw, 0f);
    }

    // 수평 거리 — 진단 로그에서 "얼마나 갔나"를 잴 때 높이를 섞지 않기 위해.
    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        Vector3 delta = a - b;
        delta.y = 0f;
        return delta.magnitude;
    }

    // 루트 원점에서 캡슐 밑면까지의 높이 — 위 ResolveSettledRootPose 주석 참고.
    private float CapsuleBottomOffset =>
        m_controller == null ? 0f : m_controller.center.y - m_controller.height * 0.5f;

    // 정착 정렬용 지면 — 여기까지 왔다면 보통 지면이 있다(Update가 없으면 정착을 미룬다).
    //
    // ⚠ 못 찾는 경우는 <b>맵 밖으로 떨어진 시체</b>뿐이고, 그때는 골반 높이를 쓴다. 예전 주석은
    // "CharacterController의 중력이 남은 차이를 메운다"고 적었지만 <b>그건 거짓이다</b> — 원격은
    // PlayerMovement가 꺼져 있어 중력이 돌지 않고, 정착 후 골반은 로컬 오프셋을 물고 키네마틱이
    // 되므로 시체가 허공에 굳는다. 그래서 이 경로로 오지 않게 막는 것이 Update의 지면 판정이다.
    private Vector3 GroundUnder(Vector3 hipsPosition)
    {
        bool hitGround = TryGroundUnder(hipsPosition, out Vector3 point);

        if (m_debugLog)
            Debug.Log(
                hitGround
                    ? $"[래그돌] 지면 판정 — y={point.y:F2} (골반 y={hipsPosition.y:F2})"
                    : $"[래그돌] 지면 판정 실패 — 골반 높이({hipsPosition.y:F2})를 쓴다."
                        + $" 정착 대기 {k_lostBodyTimeoutFactor}배까지 넘겼다는 뜻이다"
                        + " (맵 밖으로 떨어진 시체 / 마스크·탐색거리 확인)",
                this
            );

        return hitGround ? point : hipsPosition;
    }

    // 골반 밑 지면 탐색 — 정착 자격 판정(HasGroundUnderHips)과 정착 정렬(GroundUnder)이 공유한다.
    //
    // 탐색 거리를 짧게(m_groundProbeDistance) 잡는 것이 중요하다. 길게 쏘면 얇은 실내 바닥을 뚫고
    // 아래층·지면을 찾아내, 시체가 정착하는 순간 한 층 밑으로 순간이동한다.
    private bool TryGroundUnder(Vector3 hipsPosition, out Vector3 point)
    {
        const float k_probeLift = 0.5f; // 골반이 바닥에 파묻혀 있어도 레이가 지면 위에서 출발하게

        bool hitGround = Physics.Raycast(
            hipsPosition + Vector3.up * k_probeLift,
            Vector3.down,
            out RaycastHit hit,
            k_probeLift + m_groundProbeDistance,
            m_groundMask,
            QueryTriggerInteraction.Ignore
        );

        point = hitGround ? hit.point : hipsPosition;
        return hitGround;
    }

    private void RestoreCapturedWorldPoses()
    {
        // 부모가 먼저 와야 자식의 월드 포즈 대입이 헛되지 않는다 —
        // GetComponentsInChildren이 계층 순서(부모 먼저)로 주므로 그 순서를 그대로 쓴다.
        for (int i = 0; i < m_bodies.Length; i++)
            m_bodies[i].transform.SetPositionAndRotation(
                m_capturedPositions[i],
                m_capturedRotations[i]
            );
    }

    // ---- 부활 블렌드 ----

    // LateUpdate에서 돈다 — 이 시점의 뼈 로컬값이 곧 애니메이터가 평가한 포즈다. 정착 포즈에서
    // 그쪽으로 끌고 가면 "누운 자세에서 바닥 대기 자세로 스르륵" 이 된다.
    // 완료되면 Animated로 돌아가고, 그때서야 AnimationDriver가 Down을 내려 기상 모션이 시작된다.
    private void TickBlend()
    {
        m_blendTimer += Time.deltaTime;
        float t = m_blendSeconds <= 0f ? 1f : Mathf.Clamp01(m_blendTimer / m_blendSeconds);

        for (int i = 0; i < m_allBones.Length; i++)
            m_allBones[i].localRotation = Quaternion.Slerp(
                m_blendFromRotations[i],
                m_allBones[i].localRotation,
                t
            );

        m_hipsBone.localPosition = Vector3.Lerp(
            m_blendFromHipsLocalPosition,
            m_hipsBone.localPosition,
            t
        );

        if (t >= 1f)
        {
            m_state = RagdollState.Animated;
            SetSkinsAlwaysVisible(false);
        }
    }
}
