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

    // 부활 블렌드가 물려 들어가는 상태 — PlayerAnimatorControllerBuilder의 k_groundState와 같아야 한다.
    private static readonly int s_groundStateHash = Animator.StringToHash("Knockdown_Ground");

    private enum RagdollState
    {
        Animated, // 평시 — 전 Rigidbody 키네마틱, 애니메이터가 포즈를 쥔다
        Ragdoll, // 물리 중 — 애니메이터 정지, 임펄스로 날아가는 구간

        // 착지 정착 — 11개 뼈를 전부 물리에 두고 골반만 캡슐 앵커에 관절로 매단다(RestToPhysics).
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

    [Tooltip("원격 피어가 착지한 시체를 오너 위치로 당겨오는 속도(m/s) — 수평만. 크게 잡으면 " +
             "스냅처럼 보인다. 착지 후 구르는 중의 델타는 실측 0.15~0.23m라 이 속도로 충분하다")]
    [SerializeField] private float m_alignPullSpeed = 1.5f;

    [Tooltip("원격 피어가 비행 중 시체를 오너 골반으로 당겨오는 속도(m/s) — 3차원. 착지 후 값(1.5)으로는 " +
             "따라붙지 못한다. 병리적 루트 점프를 뼈로 넘기지 않기 위한 상한이라 정상 비행에서는 " +
             "걸리지 않는다(진입 시 델타 0에서 출발). " +
             "⚠ <b>시체의 비행 속도와 짝이다</b> — BombDevice.m_ragdollImpulseScale을 올리면 여기도 " +
             "같이 올릴 것. 16은 폭심 임펄스(약 10.3m/s)에 대한 여유다")]
    [SerializeField] private float m_flightAlignPullSpeed = 16f;

    [Tooltip("원격 시체가 스트리밍된 루트에서 이만큼(m) 벗어나면 보정을 스냅으로 바꾼다 — 안전망이다. " +
             "정상 동작에서는 걸리지 않아야 하고, 자주 걸리면 잔차가 큰 것이므로 보정을 세게 할 게 " +
             "아니라 입력(임펄스·밧줄)이 어긋난 것을 봐야 한다")]
    [SerializeField] private float m_alignSnapDistance = 2.5f;

    [Tooltip("밧줄 길이(m) — 운반자의 손과 시체 골반 사이의 최대 거리. 이 안에서는 시체가 자유롭고, " +
             "넘어가면 아래 강성·감쇠가 잡는다. 밧줄은 상시 작용하는 스프링이 아니라 거리 제한이고, " +
             "끌리는 모양은 물리가 낸다. 길게 잡으면 장력이 덜 걸려 전체적으로 순해진다. " +
             "⚠ 앵커가 손(약 1.1m)이라 손 높이보다 짧으면 시체가 바닥에 닿지 못하고 매달린다. " +
             "바닥에 누운 채 끌리는 수평거리 = √(길이² − (손높이 − 골반높이)²) — " +
             "2.0이면 약 1.8m 뒤에서 끌린다")]
    [SerializeField] private float m_ropeLength = 2f;

    [Tooltip("밧줄이 한계를 넘었을 때 되당기는 강성 — <b>한계 바깥에서만</b> 작동한다(늘어져 있으면 " +
             "힘이 0이라 시체를 들어올리지 못한다). 0이면 하드 리밋이 되어 위반량을 한 스텝에 " +
             "해소하며 시체를 11m/s로 튕긴다. 시체 70kg을 마찰(약 412N)에 맞서 끌려면 1500에서 " +
             "약 27cm 늘어난다 — 밧줄이 하중을 받아 늘어나는 정도라 자연스럽다")]
    [SerializeField] private float m_ropeLimitSpring = 1500f;

    [Tooltip("같은 한계의 감쇠 — <b>과감쇠로 둔다.</b> 임계는 약 2√(강성×질량) = 2√(1500×70) ≈ 650이고 " +
             "1000이면 ζ≈1.5다. 부족감쇠(예전 3000/600, ζ≈0.65)면 팽팽해질 때마다 오버슛으로 속도를 " +
             "얹는데, 늘어진 반주기에는 이 감쇠가 0이라 뺄 방법이 없다 — 운반자가 제자리에서 돌면 " +
             "회전 주기마다 에너지가 쌓여 시체가 점점 빨라지고 놓는 순간 날아간다 (§9-17)")]
    [SerializeField] private float m_ropeLimitDamper = 1000f;

    [Tooltip("밧줄에 묶인 동안 뼈에 거는 선형 감쇠(1/s) — <b>늘어진 구간의 유일한 에너지 배출구다.</b> " +
             "한계 감쇠는 밧줄이 팽팽할 때만 작동하므로 이것이 없으면 넣기만 하고 빼지 않는 펌프가 된다. " +
             "0.6이면 시정수 약 1.7초. 끌리는 저항이 늘어 밧줄이 조금 더 늘어난다(2m/s에서 약 84N)")]
    [SerializeField] private float m_dragLinearDamping = 0.6f;

    [Tooltip("같은 구간의 각 감쇠 — 팽이처럼 계속 도는 것을 잡는다. 평시 뼈 값은 0.05로 사실상 없다. " +
             "너무 올리면 끌릴 때 몸이 뻣뻣해져 흐느적임이 죽으므로 선형 감쇠부터 올려 볼 것")]
    [SerializeField] private float m_dragAngularDamping = 0.6f;

    [Tooltip("밧줄에 묶인 동안 뼈 속도의 <b>하드 상한</b>(m/s) — 슬링 차단용이다. 0이면 끈다. " +
             "감쇠로는 못 막는다: 운반자가 달리며 원을 그리면 장력이 하는 일이 ω²로 커지는데 " +
             "감쇠 배출은 v에 비례해, 빨리 돌수록 입력이 이긴다(§9-18). 기본 8은 스프린트 속도와 " +
             "같다 — 끌려가는 시체가 끄는 사람보다 빠를 이유는 없고, 넘는 만큼은 전부 슬링이다")]
    [SerializeField] private float m_ropeMaxSpeed = 8f;

    // ⚠ <b>감쇠는 밧줄에 묶인 동안에만 건다.</b> 상시로 걸면 사망 직후의 비행이 같이 죽는다 —
    // 그쪽은 탄도로 남아야 하고(임펄스가 유일한 입력), 오히려 더 날려야 하는 방향이다.
    // 걸고 푸는 자리는 밧줄의 수명과 정확히 같다: ApplyRopeTuning ↔ DetachRope.



    [Header("애니메이터 복귀")]
    [Tooltip("정착 포즈 → 애니메이터 포즈 보간 시간(초)")]
    [SerializeField] private float m_blendSeconds = 0.4f;

    private Animator m_animator;
    private CharacterController m_controller;
    private PlayerIncapacitation m_incapacitation;
    private PlayerMovement m_movement;
    private NetworkObject m_netObject;

    private Transform m_root; // CharacterController가 붙은 트랜스폼 = 판정·동기화의 주체
    private Transform m_boneRoot; // 리그 최상단('Root') — 뼈·스킨 수집 범위를 여기로 못박는다

    private Rigidbody[] m_bodies; // 래그돌 레이어의 뼈 Rigidbody만 (손에 든 아이템의 rb가 섞이지 않게)
    private float[] m_baseLinearDamping; // 밧줄을 풀 때 되돌릴 평시 값 — 프리팹이 진실이라 상수로 박지 않는다
    private float[] m_baseAngularDamping;
    private Transform m_hipsBone; // 관절이 없는 뼈 = 래그돌 루트
    private Rigidbody m_hipsBody; // 같은 뼈의 rb — 트레일 관절이 여기 붙는다 (§9-7)
    private Rigidbody m_ropeAnchor; // 운반자 손을 따라가는 키네마틱 앵커 — 밧줄의 끝
    private GameObject m_ropeAnchorObject; // 파괴용 — 앵커는 부모가 없어 씬에 남는다
    private ConfigurableJoint m_ropeJoint; // 골반 ↔ 앵커, 거리 제한(= 밧줄)
    private Transform m_ropeCarrier; // 밧줄을 쥔 쪽
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

    // 이번 래그돌 에피소드에서 <b>사망을 한 번이라도 관측했는가.</b> 부활 판정의 전제다 —
    // 아래 PollDeath의 주석에 이유가 적혀 있다.
    private bool m_sawDeathThisEpisode;
    private float m_awaitingDeathSeconds;

    // 사망 동기화를 기다려 주는 시간(초). 이 값이 하는 일은 <b>안전망뿐</b>이다 — 정상 경로에서는
    // 사망이 다음 몇 틱 안에 반드시 도착하므로 걸리지 않는다. 걸리는 경우는 대상이 아주 빠르게
    // 되살아나 이 피어가 Die를 <b>아예 못 보고</b> None만 받는 병리적 순서뿐이고, 그때 이게 없으면
    // 시체가 래그돌에 영구히 갇힌다(ExitToAnimator를 부르는 곳이 여기 하나다).
    private const float k_deathSyncGraceSeconds = 1f;
    private bool m_capsuleWasEnabled = true; // 캡슐 충돌 무시 재적용 판정 (IgnoreOwnCapsule 주석 참고)


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
    internal bool IsCapsuleFollowingBody =>
        (m_state == RagdollState.Ragdoll || m_state == RagdollState.Settled) && HasMoveAuthority;

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
            {
                m_hipsBone = all[i].transform;
                m_hipsBody = all[i];
            }
            if (all[i].name == "Head")
                m_headBone = all[i].transform;
        }

        m_capturedPositions = new Vector3[count];
        m_capturedRotations = new Quaternion[count];

        // 밧줄 감쇠를 풀 때 되돌릴 자리 — 프리팹 값(0 / 0.05)을 상수로 박으면 프리팹이 바뀌었을 때
        // 조용히 덮어쓴다. 여기서 읽어 두면 항상 프리팹이 진실이다.
        m_baseLinearDamping = new float[count];
        m_baseAngularDamping = new float[count];
        for (int i = 0; i < count; i++)
        {
            m_baseLinearDamping[i] = m_bodies[i].linearDamping;
            m_baseAngularDamping[i] = m_bodies[i].angularDamping;
        }

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
        SetUpRopeAnchor(); // 밧줄 끝이 될 손잡이. 관절은 밧줄을 묶을 때 만든다 (§9-7)
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
    //    (SetKinematic(true) → <see cref="RestToPhysics"/>) 안 지우면 정착 직전 속도가 되살아난다.
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
    /// 정착 = 완전 정지가 아니다. <b>11개 뼈를 전부 물리에 두고, 그대로 둔다.</b>
    ///
    /// 네 번째 방식이다. 앞의 셋은 각각 반쪽만 얻었다:
    ///  · <b>전부 키네마틱</b> — 운반은 되지만 몸이 하나의 자세로 굳는다 (처음 구현)
    ///  · <b>골반만 키네마틱</b> — 둘 다 얻은 것처럼 보였지만 <b>§9-7의 원인이었다</b> (아래)
    ///  · <b>전부 물리 + 골반을 캡슐에 스프링으로</b> — 세게 잡으면 시체가 떠오르고, 약하게 잡으면
    ///    마찰(412N)을 못 이겨 안 끌린다. <b>수직으로 지고 수평으로 이기는 스프링은 없다</b>
    ///
    /// <b>골반만 키네마틱이 왜 틀렸나.</b> 키네마틱 골반은 리그 루트의 자식이라 <b>프레임 클럭</b>
    /// (원격은 네트워크 보간)으로 움직이고, 나머지 10개는 <b>물리 클럭</b>이다. 관절이 그 두 클럭을
    /// 이으므로 스텝마다의 양자화 차이가 곧 관절 위반이 되고, 솔버가 한 스텝에 해소하며 사지를
    /// 채찍처럼 당긴다 — <b>몸이 찢어진다.</b> 보간 모드도 갈려(동적 Interpolate / 키네마틱 None)
    /// 3m/s면 골반 이음새에 상시 약 6cm 어긋남이 생겼다.
    ///
    /// 그리고 <b>키네마틱은 무한 강성이다</b>. 골반이 지면과 안 맞는 높이에 고정되면 거기 매달린
    /// <c>Spine_02</c>가 지면으로 밀려 들어가고, 물리는 키네마틱 골반을 밀어낼 수 없어 매 스텝
    /// 싸운다 — 실측 <c>Spine_02 ← 충격 121</c>이 수십 스텝 지속되며 <b>허리가 땅에 박힌 채
    /// 몸이 부들부들 떨렸다.</b>
    ///
    /// <b>그래서 정착은 아무것도 붙들지 않는다.</b> 시체는 그냥 물리에 놓인 11개 뼈다. 끌고 가는
    /// 것은 <see cref="BeginRopePull"/>이 붙이는 <b>밧줄</b>이 하고, 그동안 캡슐은
    /// <see cref="TickCapsuleFollow"/>로 시체를 따라간다 — <b>사망 구간 내내 주인은 시체다.</b>
    /// </summary>
    private void RestToPhysics()
    {
        IgnoreOwnCapsule(); // 뼈를 다시 물리로 놓아주기 전에 — 정착 중 캡슐 토글이 무시를 지웠을 수 있다

        // <b>전 피어가 물리를 유지한다.</b> 원격에서 뼈를 키네마틱으로 굳혔다가 되돌렸다 —
        // 굳히면 시체가 루트 높이 하나에 매달린 조각상이 되어, 그 높이가 조금이라도 틀리면
        // 흡수할 수단이 없어 바닥에 박히거나 공중에 뜬다. 물리가 있으면 중력·접촉이 흡수한다.
        // 그리고 R4(끌릴 때 흐느적)는 원격에서 물리가 돌아야 성립한다. (§10-3)
        SetKinematic(false);
    }

    // ---- 밧줄 (#365 운반 / #398 드래그) ----

    /// <summary>
    /// 밧줄을 시체에 <b>묶는다</b> — 운반자가 움직이면 물리가 시체를 끌어온다.
    /// <see cref="PlayerTowedMotion.BeginDraggedFollow"/>가 래그돌인 대상에게만 부른다.
    ///
    /// <b>거리 제한이지 스프링이 아니다.</b> <c>linearLimit</c>만 걸고, 힘은 <b>한계 바깥에서만</b>
    /// 생긴다 — 밧줄 길이 안에서는 시체가 완전히 자유롭고(중력대로 눕고 구른다), 길이를 넘는
    /// 순간에만 관절이 붙잡는다. 실제 밧줄이 그렇게 동작한다.
    ///
    /// 이 구분이 예산 문제를 없앤다. 상시 작용하는 스프링으로 끌려다니게 만들려던 앞의 시도는
    /// 마찰(412N)·골반 무게(107N)·질량(70kg) 사이에서 답이 없었다 — 세면 뜨고 약하면 안 끌린다.
    /// 거리 제한은 수평으로는 마찰을 이기면서 수직으로는 늘어져 있는 한 아무 힘도 주지 않는다.
    ///
    /// 한계에 걸리는 <b>방식</b>(강성·감쇠·길이)과 뼈 감쇠는 <see cref="ApplyRopeTuning"/>이 쥔다 —
    /// 거기가 이 밧줄의 유일한 튜닝 지점이고, 왜 그 조합이어야 하는지도 그쪽에 적혀 있다.
    ///
    /// 회전은 <b>구속</b>하지 않는다 — 시체는 끌리면서 자유롭게 굴러야 한다. 각 감쇠는 구속이
    /// 아니라 마찰이므로 별개다(팽이처럼 도는 것만 잡는다).
    /// </summary>
    /// <param name="carrier">운반자(밧줄을 쥔 쪽). 매 프레임 이 위치를 따라 앵커가 움직인다.</param>
    public void BeginRopePull(Transform carrier)
    {
        m_ropeCarrier = carrier;

        if (carrier == null || m_hipsBody == null || m_ropeAnchor == null)
            return;

        DetachRope(); // 멱등 — 운반자가 바뀌면 다시 묶는다

        // 앵커를 운반자 자리에 먼저 옮긴다. 관절은 만들어진 순간의 상대 포즈를 기준으로 삼으므로
        // 순서가 뒤바뀌면 엉뚱한 기준이 굳는다 (그렇게 만들었다가 시체가 0.88m 떠올랐다).
        m_ropeAnchor.position = carrier.position;

        m_ropeJoint = m_hipsBody.gameObject.AddComponent<ConfigurableJoint>();
        m_ropeJoint.autoConfigureConnectedAnchor = false;
        m_ropeJoint.anchor = Vector3.zero; // 골반 피벗
        m_ropeJoint.connectedAnchor = Vector3.zero; // 앵커 원점
        m_ropeJoint.connectedBody = m_ropeAnchor;

        // 전 축 Limited + 반경 = 밧줄 길이. 구면 안에서는 자유, 표면에서 잡힌다.
        m_ropeJoint.xMotion = ConfigurableJointMotion.Limited;
        m_ropeJoint.yMotion = ConfigurableJointMotion.Limited;
        m_ropeJoint.zMotion = ConfigurableJointMotion.Limited;

        m_ropeJoint.angularXMotion = ConfigurableJointMotion.Free;
        m_ropeJoint.angularYMotion = ConfigurableJointMotion.Free;
        m_ropeJoint.angularZMotion = ConfigurableJointMotion.Free;

        m_ropeJoint.projectionMode = JointProjectionMode.None; // §9-2 — projection은 충돌을 무시한다
        m_ropeJoint.enableCollision = false;

        ApplyRopeTuning(); // 길이·강성·감쇠 — 관절 생성과 분리해 Play 중에도 다시 적용할 수 있게
        WakeBodies(); // 잠든 시체는 관절 힘만으로는 안 깨어날 수 있다
    }

    /// <summary>
    /// 밧줄의 튜닝 값(길이·한계 스프링·뼈 감쇠)을 지금 값으로 적용한다.
    ///
    /// 관절 생성과 분리해 둔 이유는 <b>Play 중 인스펙터 조정</b>이다 — 예전에는 이 값들이
    /// <see cref="BeginRopePull"/> 안에 인라인이라 밧줄을 다시 잡아야 새 값이 먹었다.
    ///
    /// <b>한계 스프링은 한계 "바깥"에서만 작동한다</b> — 앞서 폐기한 xDrive 스프링과 범주가 다르다.
    /// 그건 상시 작용해서 시체를 들어올리거나 지면과 싸웠지만, 이건 밧줄이 늘어져 있는 동안
    /// (한계 안)에는 힘이 정확히 0이다. 그래서 세게 잡아도 시체가 뜨지 않는다.
    /// spring=0(하드 리밋)으로 두면 위반량을 솔버가 한 스텝에 해소하며 큰 속도를 실어준다 —
    /// 실측: 시체가 <b>11.6m/s</b>로 튀어 운반자를 지나쳐 손↔골반 2.12m → 0.33m까지 오버슛했고,
    /// 그 야크가 캡슐(=루트)에 실려 원격으로 나가 격차가 14.65m까지 벌어졌다.
    ///
    /// <b>뼈 감쇠가 여기 같이 있는 이유</b>(§9-17): 한계 감쇠는 팽팽할 때만 일하므로, 늘어진
    /// 반주기에는 에너지를 뺄 수단이 하나도 없다. 운반자가 제자리에서 도는 동안 밧줄은 팽팽↔늘어짐을
    /// 반복하는데, 팽팽 구간에서 부족감쇠 오버슛이 속도를 얹고 늘어짐 구간에서 그대로 유지되면
    /// <b>회전 주기마다 에너지가 쌓이는 펌프</b>가 된다 — 돌릴수록 빨라지다 놓는 순간 날아간다.
    /// 한계를 과감쇠로 만들어 주입을 없애고(위 툴팁), 뼈 감쇠로 배출구를 연다. 둘은 짝이다.
    ///
    /// 흐느적임은 이 스프링의 오버슛이 아니라 <b>손의 걸음 흔들림</b>이 만든다
    /// (<c>PlayerTowedMotion.ResolveRopeAnchor</c>) — 과감쇠로 바꿔도 그쪽은 그대로 남는다.
    /// </summary>
    private void ApplyRopeTuning()
    {
        if (m_ropeJoint == null)
            return;

        m_ropeJoint.linearLimit = new SoftJointLimit { limit = Mathf.Max(0.1f, m_ropeLength) };
        m_ropeJoint.linearLimitSpring = new SoftJointLimitSpring
        {
            spring = m_ropeLimitSpring,
            damper = m_ropeLimitDamper,
        };

        SetBoneDamping(m_dragLinearDamping, m_dragAngularDamping);

        m_appliedRopeTuning = new Vector4(
            m_ropeLength, m_ropeLimitSpring, m_ropeLimitDamper, m_dragLinearDamping);
        m_appliedDragAngularDamping = m_dragAngularDamping;
    }

    // 마지막으로 적용한 튜닝 값 — 인스펙터에서 바뀐 프레임에만 다시 쓰기 위한 비교용.
    // (관절 프로퍼티 대입과 뼈 11개 순회를 매 물리 스텝 돌리지 않는다)
    private Vector4 m_appliedRopeTuning;
    private float m_appliedDragAngularDamping;

    private bool RopeTuningChanged =>
        m_appliedRopeTuning
            != new Vector4(m_ropeLength, m_ropeLimitSpring, m_ropeLimitDamper, m_dragLinearDamping)
        || m_appliedDragAngularDamping != m_dragAngularDamping;

    // 밧줄에 묶인 동안에만 거는 감쇠 — 푸는 쪽은 프리팹에서 읽어 둔 평시 값으로 되돌린다.
    // 사망 비행(Ragdoll 상태)에는 절대 걸지 않는다: 그 구간은 임펄스만이 입력인 탄도여야 한다.
    private void SetBoneDamping(float linear, float angular)
    {
        if (m_bodies == null || m_baseLinearDamping == null)
            return;

        for (int i = 0; i < m_bodies.Length; i++)
        {
            if (m_bodies[i] == null)
                continue;
            m_bodies[i].linearDamping = linear;
            m_bodies[i].angularDamping = angular;
        }
    }

    private void RestoreBoneDamping()
    {
        if (m_bodies == null || m_baseLinearDamping == null)
            return;

        for (int i = 0; i < m_bodies.Length; i++)
        {
            if (m_bodies[i] == null)
                continue;
            m_bodies[i].linearDamping = m_baseLinearDamping[i];
            m_bodies[i].angularDamping = m_baseAngularDamping[i];
        }
    }

    /// <summary>밧줄을 푼다 — 내려놓기·부활·운반자 소실.</summary>
    public void EndRopePull()
    {
        m_ropeCarrier = null;
        DetachRope();
    }

    private void DetachRope()
    {
        if (m_ropeJoint == null)
            return;

        Destroy(m_ropeJoint);
        m_ropeJoint = null;
        RestoreBoneDamping(); // 감쇠는 밧줄의 수명과 같다 — 풀면 다시 탄도로 돌아간다
    }

    // 앵커를 운반자 손 위치로 옮긴다 — 물리 스텝마다. 앵커는 키네마틱이라 이 이동이 곧 밧줄의
    // 장력이 되고, 시체는 그 장력에 끌린다. 우리가 시체 위치를 계산하지 않는 것이 핵심이다.
    private void TickRopeAnchor()
    {
        if (m_ropeAnchor == null || m_ropeCarrier == null)
            return;

        // Play 중 인스펙터에서 값을 바꾸면 밧줄을 다시 잡지 않아도 바로 먹는다 (튜닝용).
        if (RopeTuningChanged)
            ApplyRopeTuning();

        m_ropeAnchor.MovePosition(m_ropeCarrier.position);

        // 밧줄이 팽팽해지는 순간 시체가 자고 있으면 장력을 못 받는다.
        //
        // <b>골반만 깨우면 안 된다.</b> 골반만 깨어 있고 사지가 자고 있으면 관절이 당기는 힘을
        // 사지가 받지 않아 <b>몸이 한 덩어리로 끌려온다</b> — 흐느적임이 통째로 사라진다.
        if (m_hipsBody != null && m_hipsBody.IsSleeping())
            WakeBodies();

        ClampBoneSpeed(); // 슬링 차단 — 장력을 적용한 뒤에 자른다
    }

    /// <summary>
    /// 밧줄에 묶인 동안 뼈 속도에 <b>하드 상한</b>을 건다 — 방향은 그대로 두고 크기만 자른다.
    ///
    /// <b>왜 감쇠가 아니라 캡인가</b>(§9-18). 운반자가 달리며 원을 그리면 밧줄은 투석기가 된다 —
    /// 장력은 항상 앵커 쪽(반경 방향)이지만 <b>앵커가 움직이면 그 장력이 일을 하기 때문에</b>
    /// 시체가 가속된다. 이때 넣는 힘은 ω²로 커지는데 선형 감쇠가 빼는 양은 v에 비례하므로,
    /// 빠르게 돌수록 입력이 이긴다 — <b>감쇠를 아무리 올려도 임계 회전속도만 밀릴 뿐 못 막는다.</b>
    /// 현실에서 그 역할을 하는 지면 마찰도 장력이 시체를 손 높이로 들어올리면 사라진다.
    ///
    /// 그래서 ω와 무관하게 성립하는 상한이 필요하다. 이건 물리를 흉내 내는 값이 아니라
    /// <b>봉투(envelope)</b>다 — k_maxDepenetrationVelocity·m_alignSnapDistance와 같은 계열이고,
    /// 정상적인 끌기에서는 걸리지 않아야 한다. 자주 걸린다면 상한을 올릴 게 아니라 왜 시체가
    /// 스프린트보다 빠른지를 봐야 한다.
    ///
    /// <b>사망 비행에는 걸리지 않는다</b> — 이 함수는 밧줄이 묶여 있을 때만 도는
    /// <see cref="TickRopeAnchor"/>에서 불린다. 그쪽은 임펄스만이 입력인 탄도로 남아야 한다.
    /// </summary>
    private void ClampBoneSpeed()
    {
        if (m_ropeMaxSpeed <= 0f || m_bodies == null)
            return;

        float maxSqr = m_ropeMaxSpeed * m_ropeMaxSpeed;
        for (int i = 0; i < m_bodies.Length; i++)
        {
            Rigidbody body = m_bodies[i];
            if (body == null || body.isKinematic)
                continue;

            Vector3 velocity = body.linearVelocity;
            float sqr = velocity.sqrMagnitude;
            if (sqr > maxSqr)
                body.linearVelocity = velocity * (m_ropeMaxSpeed / Mathf.Sqrt(sqr));
        }
    }

    // 전 뼈를 깨운다 — 흐느적임은 사지가 깨어 있어야 나온다 (TickRopeAnchor 주석 참고).
    private void WakeBodies()
    {
        if (m_bodies == null)
            return;

        for (int i = 0; i < m_bodies.Length; i++)
        {
            if (m_bodies[i] != null && !m_bodies[i].isKinematic)
                m_bodies[i].WakeUp();
        }
    }

    /// <summary>
    /// 밧줄 앵커만 미리 만들어 둔다 — <b>부모 없는 키네마틱 Rigidbody.</b>
    ///
    /// 시체의 루트에 매달지 않는다 — 앵커가 따라가야 하는 것은 <b>운반자</b>이지 시체 자신이
    /// 아니다. 자기 루트의 자식으로 두면 "시체가 자기를 끄는" 꼴이 된다.
    /// 콜라이더는 붙이지 않는다 — 세계와 부딪히지 않는 순수한 손잡이다.
    ///
    /// 관절은 여기서 만들지 않는다 (<see cref="BeginRopePull"/>의 순서 주석 참고).
    /// </summary>
    private void SetUpRopeAnchor()
    {
        if (m_hipsBody == null)
            return;

        GameObject anchor = new GameObject($"RopeAnchor ({name})");
        m_ropeAnchor = anchor.AddComponent<Rigidbody>();
        m_ropeAnchor.isKinematic = true;
        m_ropeAnchor.useGravity = false;
        m_ropeAnchorObject = anchor;
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
            ReportEntry("누적", impulse);
            return;
        }

        if (m_state != RagdollState.Animated)
            return; // 이미 정착했거나 일어나는 중 — 다시 날리지 않는다

        m_state = RagdollState.Ragdoll;
        m_stillTimer = 0f;
        m_elapsedInRagdoll = 0f;

        // 슬라이드 넉백과 이중으로 밀리지 않게 CharacterController 쪽 외력을 지운다.
        // "죽은 사람은 건너뛴다"는 판정을 BombExplosionView에 두지 않는 이유가 위 순서 문제다 —
        // 들어와도 무해하게 만드는 쪽이 순서와 무관하게 항상 옳다.
        m_movement?.ClearExternalVelocity();

        if (m_animator != null)
            m_animator.enabled = false;

        SetSkinsAlwaysVisible(true);

        // <b>사망 중에는 캡슐을 끈다</b> (§10-0). 대리값이 물리 오브젝트일 이유가 없고, 켜 두면
        // ① 뼈와 서로 충돌해 §9-1의 재적용 싸움이 필요하고 ② 스윕이 지형에 막혀 추종이 실패한다.
        // 호송(#279)이 같은 패턴이다 — SetControllerEnabled(false) 후 트랜스폼 직접 대입.
        SetControllerEnabled(false);
        IgnoreOwnCapsule(); // 캡슐이 꺼져 있으면 사실상 무동작이지만, 전이 순간의 안전망으로 남긴다
        SetKinematic(false);
        ApplyImpulse(impulse);
        ReportEntry("신규", impulse);

        m_diagStartHips = m_hipsBone.position;
        m_diagImpulse = impulse;
        m_diagApexY = m_hipsBone.position.y;
        m_diagTracking = true;
    }

    // ---- 임시 진단: 사망 비행 실측 (#506 — 값을 정하면 지운다) ----
    //
    // 앞선 진단(임펄스·속도·10스텝 이동)은 <b>전부 정상</b>으로 끝났다 — 실측 876mm/0.18s가
    // 순수 탄도 예측과 일치했다. 그래서 그쪽은 걷어내고, 남은 미지수 하나만 잰다:
    //
    //   <b>탄도 공식이 예측한 거리와 실제로 떨어진 거리가 얼마나 다른가.</b>
    //
    // 공식에는 <b>구르는 사지가 먹는 지면 마찰</b>이 없다. 시체는 골반이 0.9m에서 출발하는데
    // 다리는 이미 바닥에 있어서, 이론 사거리의 얼마가 실제로 나오는지는 재 보기 전에는 모른다.
    // 이 값을 모르면 "세기를 얼마나 올려야 하는가"에 답할 수 없다.
    //
    // 함께 갈라야 하는 것 — <b>안 나는 것인지, 안 뜨는 것인지, 뜨기만 하는 것인지</b>:
    //  · 정점은 높은데 수평이 짧다     → 발사각 문제. m_ragdollLiftRatio를 내린다
    //  · 정점·수평 둘 다 짧다          → 세기 문제. m_ragdollImpulseScale을 올린다
    //  · 수평은 나오는데 체감이 없다   → 임펄스가 아니라 연출(카메라·VFX·시간) 문제다
    private void ReportEntry(string kind, Vector3 impulse)
    {
        Rigidbody hips = m_hipsBody;
        Debug.Log(
            $"[래그돌/진입] {kind} {(HasMoveAuthority ? "오너" : "원격")}"
                + $" | 임펄스 {impulse.magnitude:F2} {impulse.ToString("F1")}"
                + $" | 골반v {(hips != null ? hips.linearVelocity.magnitude : -1f):F2}"
                + $" 키네={(hips != null && hips.isKinematic)}"
                // ⬇ 이 두 값이 가설의 핵심이다. 임펄스가 사망 동기화보다 먼저 도착했으면
                //   여기서 사망=False가 찍히고, 같은 프레임의 PollDeath가 래그돌을 취소한다.
                + $" | 사망={DiagIsDead} 원인={DiagCause}"
                + $" | 뼈 {(m_bodies != null ? m_bodies.Length : 0)}개"
                + $" 캡슐={(m_controller != null && m_controller.enabled ? "켜짐" : "꺼짐")}",
            this
        );
    }

    private bool DiagIsDead => m_incapacitation != null && m_incapacitation.IsDead;
    private string DiagCause =>
        m_incapacitation != null ? m_incapacitation.Cause.ToString() : "없음";

    private Vector3 m_diagStartHips;
    private Vector3 m_diagImpulse;
    private float m_diagApexY;
    private bool m_diagTracking;

    // 비행 중 최고점을 따라간다 — Update의 Ragdoll 분기에서 부른다.
    private void TickFlightApex()
    {
        if (m_diagTracking && m_hipsBone != null && m_hipsBone.position.y > m_diagApexY)
            m_diagApexY = m_hipsBone.position.y;
    }

    // 정착하는 순간 한 번 — 이 한 줄이 튜닝 방향을 정한다.
    //
    // 이론값은 골반이 0.9m에서 출발하는 포물선으로 계산한 것이다:
    //   체공 t = (vy + √(vy² + 2·9.81·0.9)) / 9.81,  수평 = vx·t
    // 실측/이론 비율이 곧 "마찰이 먹는 몫"이고, 그만큼을 세기에 얹어야 원하는 그림이 나온다.
    private void ReportLanding()
    {
        if (!m_diagTracking || m_hipsBone == null)
            return;

        m_diagTracking = false;

        Vector3 flat = m_hipsBone.position - m_diagStartHips;
        flat.y = 0f;

        float vx = new Vector2(m_diagImpulse.x, m_diagImpulse.z).magnitude;
        float vy = m_diagImpulse.y;
        float predictedTime = vy <= 0f
            ? 0f
            : (vy + Mathf.Sqrt(vy * vy + 2f * 9.81f * 0.9f)) / 9.81f;
        float predictedFlat = vx * predictedTime;

        Debug.Log(
            $"[래그돌/착지] {(HasMoveAuthority ? "오너" : "원격")}"
                + $" | 임펄스 {m_diagImpulse.magnitude:F2} (수평 {vx:F2} 상승 {vy:F2})"
                + $" | 실측 수평 {flat.magnitude:F2}m 정점 +{m_diagApexY - m_diagStartHips.y:F2}m"
                + $" 체공 {m_elapsedInRagdoll:F2}s"
                + $" | 이론 수평 {predictedFlat:F2}m 체공 {predictedTime:F2}s"
                + $" | 실측/이론 {(predictedFlat > 0.01f ? flat.magnitude / predictedFlat : -1f):P0}",
            this
        );
    }

    /// <summary>
    /// 날아가는 구간을 즉시 끝내고 정착 상태로 넘긴다 — 운반 시작(#365)처럼 외부 사정으로 몸을 캡슐에
    /// 붙여야 할 때. 정착해도 몸이 굳지는 않는다(<see cref="RestToPhysics"/>) — 골반만 관절로 캡슐에 매달리고
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

        // 에피소드가 여기서 끝난다 — 다음 사망은 자기 사망을 다시 관측해야 부활할 수 있다 (PollDeath).
        m_sawDeathThisEpisode = false;
        m_awaitingDeathSeconds = 0f;

        if (m_state == RagdollState.Ragdoll)
            Settle(); // 날아가는 중이면 먼저 포즈를 확정한다

        // 정착 상태에서는 11개 뼈가 전부 물리에 있다(<see cref="RestToPhysics"/>) — 애니메이터로
        // 돌아가려면 전부 멈춰야 한다. 안 멈추면 블렌드가 놓는 포즈를 물리가 매 스텝 덮는다.
        // 트레일 결합을 먼저 끊는다: 키네마틱 바디에는 관절이 안 먹지만, 순서를 뒤집으면 한 스텝
        // 동안 스프링이 블렌드 시작 포즈를 당긴다.
        EndRopePull();
        SetKinematic(true);

        // 캡슐을 되살린다 (§10-0 — 사망 중 꺼 뒀다). 이 시점의 루트는 정착 정렬이 지면 위에
        // 놓아 둔 자리이므로(ResolveSettledRootPose가 CapsuleBottomOffset까지 보정) 그대로 켜면 된다.
        SetControllerEnabled(true);
        m_movement?.ClearExternalVelocity(); // 꺼져 있던 동안 쌓인 값이 도착지에서 바닥을 파고들지 않게 (#189)

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
        TickFlightApex(); // 임시 진단

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

        // <b>부활은 죽음을 본 뒤에만 성립한다.</b> (§9-19)
        //
        // 아래 <c>!dead</c> 분기는 "살아 있는데 래그돌이면 부활한 것"이라는 전제였는데, 그 전제가
        // 원격 피어에서 깨진다. 사망 사실은 <see cref="PlayerIncapacitation"/>의 NetworkVariable로,
        // 폭발 임펄스는 <see cref="BombDevice"/>의 ClientRpc로 온다 — <b>다른 오브젝트라 도착 순서가
        // 보장되지 않는다.</b> 임펄스가 먼저 오면 이 피어는 "아직 살아 있는 대상이 래그돌 중"인
        // 상태를 보고, 방금 시작한 비행을 부활로 오인해 취소한다. 그 뒤 사망이 도착하면 임펄스 0으로
        // 다시 들어가 <b>제자리에서 무너진다.</b> (실측: 취소가 진입 후 0.00초에, 사망=False 원인=None)
        //
        // <see cref="EnterRagdoll"/>이 멱등으로 막아 둔 것은 "사망 → 임펄스" 순서뿐이었다.
        // 반대 순서는 여기가 뚫려 있었다.
        //
        // 시간으로 맞추지 않는다 — 지연은 상한이 없다. <b>인과로</b> 막는다: 죽는 것을 한 번도 못 본
        // 대상은 되살아날 수도 없다. 임펄스는 서버가 사망을 확정한 대상에게만 나가므로
        // (<c>BombDevice.m_deathBuffer</c>) 사망 동기화는 반드시 뒤따라 온다.
        if (dead)
        {
            m_sawDeathThisEpisode = true;
            m_awaitingDeathSeconds = 0f;
        }
        else if (IsRagdollActive && !m_sawDeathThisEpisode)
        {
            m_awaitingDeathSeconds += Time.deltaTime;
        }

        if (!dead)
        {
            m_skipThisEpisode = false;
            bool revivalIsReal =
                m_sawDeathThisEpisode || m_awaitingDeathSeconds >= k_deathSyncGraceSeconds;
            if ((m_state == RagdollState.Ragdoll || m_state == RagdollState.Settled)
                && revivalIsReal)
            {
                // ---- 임시 진단 (#506) ----
                // 고친 뒤에는 <b>정상 부활에서만</b> 떠야 한다 — 사망관측=True로.
                // 사망관측=False로 뜬다면 유예(k_deathSyncGraceSeconds)로 빠져나온 것이고,
                // 그건 사망 동기화가 1초 넘게 안 왔거나 아예 안 온 병리적 경우다.
                Debug.Log(
                    $"[래그돌/취소] {(HasMoveAuthority ? "오너" : "원격")}"
                        + $" 상태={m_state} 진입후 {m_elapsedInRagdoll:F2}s"
                        + $" | 사망={DiagIsDead} 원인={DiagCause}"
                        + $" 사망관측={m_sawDeathThisEpisode} 대기 {m_awaitingDeathSeconds:F2}s"
                        + " → ExitToAnimator(부활로 처리)",
                    this
                );
                ExitToAnimator(blend: true); // 부활 — 정착 포즈에서 기상으로 잇는다
            }
            return;
        }

        if (!m_skipThisEpisode && m_state == RagdollState.Animated)
            EnterRagdoll(Vector3.zero); // 힘없이 무너지는 사망(진압봉·린치). 폭발은 임펄스를 따로 준다
    }

    private void OnDestroy()
    {
        if (m_ropeAnchorObject != null)
            Destroy(m_ropeAnchorObject);
    }

    private void FixedUpdate()
    {
        TickRopeAnchor();
    }

    private void LateUpdate()
    {
        if (m_state == RagdollState.BlendingToAnimator)
            TickBlend();

        // 원격의 시체를 스트리밍된 루트에 맞춘다 — 오너 쪽 짝(TickCapsuleFollow)은 PlayerMovement가
        // 돌린다(그쪽은 이 컴포넌트가 비활성인 원격에서도 순서를 보장해야 하는 시점·이동과 얽혀 있다).
        // 여기서 하는 이유: 원격에서는 PlayerMovement가 꺼져 있고(오너만 켜진다), NetworkTransform이
        // 이번 프레임에 적용한 루트 위치를 LateUpdate에서 읽어야 한 프레임 늦지 않는다.
        //
        // <b>정착 후에도 계속 맞춰야 한다.</b> 예전에는 Ragdoll 상태만 맞췄는데, 그때는 정착이
        // 골반을 루트에 키네마틱으로 용접해서 <b>트랜스폼 계층이</b> 시체를 끌어 줬다. §9-7로 그
        // 용접을 없앤 뒤로는 정착 후 원격의 뼈를 붙들어 주는 것이 아무것도 없어서, 밧줄로 끌면
        // <b>루트(이름표·파티클)만 가고 모델은 제자리에 남았다.</b>
        //
        // ⚠ 이건 힘 튜닝이 아니다 — <b>원격의 시체는 판정의 주인이 아니라 표시</b>이고, 권위 있는
        // 위치는 스트리밍된 루트다. 오너 쪽에서 스프링으로 물리를 합성하려던 것(§9-7에서 폐기)과는
        // 범주가 다르다. 원격의 로컬 물리는 <b>포즈</b>만 만들고 <b>궤적</b>은 오너에게서 받는다.
        // <b>비행 중에만 정렬한다.</b> 정착 후에는 원격의 뼈가 키네마틱이라(RestToPhysics) 계층이
        // 루트를 따라간다 — 여기서 또 옮기면 그 위에 오프셋이 얹혀 이중으로 움직인다.
        // 비행·정착 양쪽에서 돈다. 단 <b>동력이 아니라 표류 방지</b>다 — 몸을 움직이는 것은 각 피어의
        // 로컬 물리(임펄스·밧줄)이고, 여기서 하는 일은 그 결과가 스트리밍된 루트에서 서서히 벗어나는
        // 것을 막는 것뿐이다. 입력이 같아지면 잔차가 작아 보정이 눈에 띄지 않는다. (§10-5)
        if (!HasMoveAuthority && (m_state == RagdollState.Ragdoll || m_state == RagdollState.Settled))
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

        // <b>단 정착 후에는 캡슐이 지면에 앉는다.</b> 수평은 그대로 골반을 따라가되 높이만 지면이 준다.
        //
        // 비행 중처럼 골반 높이(지면 위 약 0.3m)에 붙여 두면 두 가지가 깨진다:
        //  · 캡슐이 내내 <c>공중</c>이라 CharacterController가 접지를 못 본다
        //  · <b>끌리며 시체가 위아래로 튀는 것이 그대로 루트의 높이가 되어 스트림에 실린다.</b>
        //    원격은 정착 후 뼈가 키네마틱이라(RestToPhysics) 그 높이를 <b>통째로</b> 타고,
        //    시체가 바닥에서 떨어져 공중에 뜬 채로 끌려온다
        //
        // 지면 판정은 정착 정렬(ResolveSettledRootPose)과 같은 것을 쓴다 — 두 곳이 다른 높이를
        // 내면 정착하는 순간 캡슐이 튄다.
        if (
            m_state == RagdollState.Settled
            && TryGroundUnder(m_hipsBone.position, out Vector3 ground)
        )
            target.y = ground.y - CapsuleBottomOffset;

        // ① 스윕으로 갈 수 있는 만큼 — 갈 수 있는 구간에서는 캡슐이 지형을 존중한다.
        // 사망 중에는 CharacterController가 꺼져 있으므로(EnterRagdoll) 대입이 곧 이동이다.
        // 스윕은 쓰지 않는다 — 서 있는 1.8m 캡슐과 굴러가는 탄도 시체는 갈 수 있는 곳이 다르고,
        // 지형이 갈리는 순간 캡슐이 뒤처져 동기화 위치가 시체를 대표하지 못한다
        // (실측: 시체 38.8m / 캡슐 0.23m, §9-11). 대리값은 지형을 존중할 이유가 없다 (§10-0).
        m_root.position = target;

        // ⚠ <b>yaw는 비행 중에만 따라간다.</b> 정착 후에도 돌리면 가만히 누운 시체의 머리가 계속
        // 돈다 — 루트가 돌면 <b>리지드바디가 없는 뼈</b>(Neck·Spine_01/03·손·발)는 계층을 따라
        // 같이 도는데 Head는 동적이라 안 따라가므로 목이 그 사이에서 비틀린다. 게다가 yaw를
        // headBone.position에서 계산하므로 같은 프레임에 되먹임 고리가 생겨 회전이 멎지 않는다.
        //
        // 정착 후의 yaw는 ResolveSettledRootPose가 이미 확정했다 — 시체는 방향을 바꾸지 않는다.
        if (m_state == RagdollState.Ragdoll)
            FollowBodyYaw();
    }

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
    /// 그래서 뼈의 <c>position</c>에 직접 델타를 더한다. 동적 바디의 <c>position</c> 대입은
    /// 텔레포트라 속도가 유도되지 않는다(키네마틱과 반대 — §9-5).
    ///
    /// <b>어느 뼈에 더하느냐가 갈린다</b> — 그게 포즈를 만드느냐 죽이느냐를 정한다(아래 본문):
    ///  · <b>비행 중</b> — 전 뼈에 같은 델타. 강체 평행이동이라 포즈·상대속도·관절이 보존된다
    ///  · <b>착지 후(끌림)</b> — <b>골반에만.</b> 나머지는 관절에 끌려오면서 흐느적거린다
    ///
    /// <b>매 프레임 보정하므로 발산이 누적되지 않는다.</b> 델타가 계속 작게 유지되는 것이 핵심이다 —
    /// 그래서 마지막에 1.2m를 흡수하는 구간이 아예 생기지 않는다. 피어별 착지점 편차를 감수하기로
    /// 한 #506 본문의 합의를 이 방식이 불필요하게 만든다.
    /// </summary>
    private void TickAlignBonesToRoot()
    {
        // <b>비행 중에도 당겨온다.</b> §9-10이 "비행 중 정렬하면 시체 궤적이 캡슐 궤적으로 덮인다"고
        // 결론 냈지만, 그 측정은 <b>캡슐이 시체를 못 따라가던 시절</b>의 것이다(시체 38.8m / 캡슐
        // 0.23m). 진짜 교훈은 "보정하지 마라"가 아니라 <b>"접지된 캡슐을 기준으로 보정하지 마라"</b>
        // 였다 — 그때 루트는 지면을 타는 CharacterController였으니 그걸 향해 당기면 포물선이 평평해진다.
        //
        // 캡슐 추종(TickCapsuleFollow)이 들어온 뒤로 <b>스트리밍된 루트 = 오너 골반</b>이다(실측 시체
        // 55.28m / 루트 55.24m). 기준이 시체의 궤적 자체가 됐으므로 당겨오는 것이 이제는 옳다.
        //
        // 안 당기면 남는 비용이 실제로 보였다: 원격은 자기 임펄스(BombDevice가 각 피어에서 자기 쪽
        // victim 위치로 계산 — 감쇠 때문에 세기부터 갈린다)로 따로 날아 다른 자리에 착지하고, 그
        // 차이를 착지 후에 1.5m/s로만 흡수한다 → <b>시체가 1~2초간 바닥을 스르륵 미끄러진다.</b>
        // 비행 중에 붙여 두면 흡수할 거리가 애초에 생기지 않는다.
        bool grounded = HasGroundUnderHips();

        Vector3 delta = m_root.position - m_hipsBone.position;

        // 착지 후에만 높이를 뺀다 — 그때는 각 피어의 지형 충돌이 높이의 주인이고, 같은 지형이므로
        // 편차가 작다. 반대로 <b>비행 중에는 3차원으로 맞춘다</b>: 공중에는 높이를 정해 줄 지형 접촉이
        // 없고, 오너 골반의 고도(=포물선의 정점)를 받아야 원격도 같은 궤적을 그린다. 여기서 y를 지우면
        // 원격 시체만 뜨지 않는 §9-10의 증상이 그대로 재현된다.
        if (grounded)
            delta.y = 0f;

        float distance = delta.magnitude;
        if (distance < 1e-4f)
            return;

        // 어느 쪽이든 <b>스냅이 아니라 당겨오기</b>다 — 상한이 있어야 루트가 병리적으로 튈 때(추종이
        // 지형에 막혀 잔차 텔레포트가 크게 들어오는 경우) 그 점프가 뼈로 전달되지 않는다.
        //
        // 느린 상한(1.5m/s)은 <b>착지 직후 구르는 구간</b>에만 쓴다. 그때 델타는 실측 0.15~0.23m라
        // 눈에 띄지 않게 흡수되고, 상한이 낮아야 남은 수평 속도를 매 프레임 되돌리는 §9-10이 작은
        // 규모로 되풀이되지 않는다.
        //
        // 빠른 상한이 필요한 두 경우 — 어느 쪽도 1.5m/s로는 따라붙지 못하고 뒤로 처진다:
        //  · <b>비행 중</b> — 시체가 6m/s로 난다 (다만 델타가 0에서 출발해 작게 유지되므로 잘 안 걸린다)
        //  · <b>정착 후</b> — 밧줄로 끌면 운반자가 약 2m/s로 간다
        // 접지 후에는 느린 상한 — 보정은 동력이 아니라 표류 방지다(§10-5). 몸은 밧줄이 끈다.
        // 상한을 올려 "끌어오게" 만들려던 시도가 §9-16에서 발산으로 끝났다.
        float maxStep = (grounded ? m_alignPullSpeed : m_flightAlignPullSpeed) * Time.deltaTime;

        // 잔차가 임계를 넘으면 스냅한다 — 그 상태는 이미 눈에 띄게 틀렸으므로 포즈 보존이 의미가
        // 없고, 느린 상한으로는 영구히 못 따라잡는다(§10-5 규칙 6). 안전망이므로 봉투 바깥에 둔다.
        if (distance > m_alignSnapDistance)
            maxStep = distance;
        if (distance > maxStep)
            delta *= maxStep / distance;

        Vector3 step = ClampByWall(delta);

        // <b>몸 전체를 같은 델타로 옮긴다.</b> 끄는 지점이 없으므로 강체 평행이동이 맞다: 포즈는
        // 이미 이 피어의 임펄스가 로컬로 만들고 있고, 여기서 하는 일은 그 궤적을 오너 것에 맞추는
        // 것뿐이다. 뼈마다 다르게 옮기면 만들어 둔 텀블이 깨진다.
        //
        // ⚠ <b>"골반만 옮겨 흐느적임을 만든다"를 여기서 시도했다가 되돌렸다.</b> 골반(10.9kg)만
        // 옮기면 나머지 59kg이 관절로 되당겨서 <b>질량비만큼(실측 12~21%)밖에 안 움직인다</b> —
        // 몸을 5m/s로 옮기려면 골반에 32m/s를 명령해야 하고 그러면 §9-5의 채찍질이 재현된다.
        // 실측에서 원격 격차가 <b>12.6m까지 발산</b>했다. 위치 추적을 이 방식으로는 못 한다.
        SnapBonesBy(step);
    }


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
        ReportLanding(); // 임시 진단 — 뼈를 옮기기 전에 실제 착지 지점을 읽는다

        for (int i = 0; i < m_bodies.Length; i++)
        {
            m_capturedPositions[i] = m_bodies[i].transform.position;
            m_capturedRotations[i] = m_bodies[i].transform.rotation;
        }

        Vector3 landedHips = m_hipsBone.position;

        SetKinematic(true);

        // 캡슐은 진입 때 이미 꺼져 있다(§10-0) — 여기서 토글할 것이 없다. 켜져 있으면 지면 레이가
        // 자기 캡슐에 걸리고(캡슐도 Default 레이어다) 트랜스폼 대입도 내부 캐시가 되돌린다.

        Vector3 rootPosition = m_root.position;
        Quaternion rootRotation = m_root.rotation;
        ResolveSettledRootPose(landedHips, ref rootPosition, ref rootRotation);

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

        RestoreCapturedWorldPoses();

        // 루트가 더 이상 나중에 점프하지 않으므로(위 주석) 양쪽 모두 곧장 물리로 놓아준다 —
        // 누운 몸이 계속 흔들리게. 원격의 릴리스 타이밍을 재던 유예 구간은 이 설계에서 사라졌다.
        RestToPhysics();

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
