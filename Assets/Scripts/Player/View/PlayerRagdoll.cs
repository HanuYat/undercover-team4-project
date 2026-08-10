using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 사망 래그돌 — 기능 정지(<see cref="IncapacitationCause.Die"/>) 동안 애니메이터를 끄고 뼈를 물리에
/// 넘긴 뒤, 착지·정착하면 다시 애니메이터로 되돌린다. (#506)
///
/// <b>이 클래스가 쥔 것은 "누가 위치를 쥐나"다.</b> 뼈를 물리에 넘기고 되돌리는 일 자체는
/// <see cref="RagdollRig"/>가, 밧줄 견인은 <see cref="RagdollRope"/>가 한다 — 둘 다 네트워크·권위·
/// 이동 프록시를 모르는 순수 물리라 NPC가 그대로 재사용한다. 여기 남은 것은 전부 <b>플레이어 고유</b>다:
/// CharacterController 캡슐을 대리값으로 쓰는 것, 오너 권한 NetworkTransform, 사망 폴링, 기상 블렌드.
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
/// <b>프리팹 루트에 붙인다</b> — CharacterController·PlayerIncapacitation·RagdollRig와 같은 오브젝트.
/// <see cref="PlayerMovement"/>·<see cref="PlayerHeadLook"/>이 <c>GetComponent</c>로,
/// <see cref="PlayerAnimationDriver"/>가 <c>GetComponentInParent</c>로 찾는다.
/// </summary>
[RequireComponent(typeof(RagdollRig))]
public class PlayerRagdoll : MonoBehaviour
{
    // 지면을 못 찾아도 결국은 정착시키는 최후 배수 — m_settleTimeoutSeconds의 몇 배까지 기다릴지.
    // 맵 밖으로 떨어져 나간 시체가 Ragdoll 상태에 영원히 갇히지 않게 하는 안전장치일 뿐이다.
    // 이 경로로 들어오면 시체는 허공에 굳지만 상태 기계는 계속 돈다 —
    // 되살릴 때 판정을 쥔 것은 루트이므로 부활·라운드 리셋은 정상 동작한다.
    private const float k_lostBodyTimeoutFactor = 4f;

    // 원격 정렬이 "끝났다"로 보는 수평 잔차(m) — 이 안에 들어오면 정착해도 굳는 오프셋이 눈에 띄지 않는다.
    private const float k_alignedTolerance = 0.05f;

    // 사망 동기화를 기다려 주는 시간(초). 이 값이 하는 일은 <b>안전망뿐</b>이다 — 정상 경로에서는
    // 사망이 다음 몇 틱 안에 반드시 도착하므로 걸리지 않는다. 걸리는 경우는 대상이 아주 빠르게
    // 되살아나 이 피어가 Die를 <b>아예 못 보고</b> None만 받는 병리적 순서뿐이고, 그때 이게 없으면
    // 시체가 래그돌에 영구히 갇힌다(ExitToAnimator를 부르는 곳이 PollDeath 하나다). (§9-19)
    private const float k_deathSyncGraceSeconds = 1f;

    // 부활 블렌드가 물려 들어가는 상태 — PlayerAnimatorControllerBuilder의 k_groundState와 같아야 한다.
    private static readonly int s_groundStateHash = Animator.StringToHash("Knockdown_Ground");

    private enum RagdollState
    {
        Animated, // 평시 — 전 Rigidbody 키네마틱, 애니메이터가 포즈를 쥔다
        Ragdoll, // 물리 중 — 애니메이터 정지, 임펄스로 날아가는 구간
        Settled, // 착지 정착 — 뼈를 전부 물리에 둔 채 그대로 둔다 (RestToPhysics)
        BlendingToAnimator, // 정착 포즈 → 애니메이터 포즈 보간 (부활)
    }

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
             "찾아내 시체가 한 층 밑으로 순간이동한다. 못 찾으면 골반 높이를 쓴다")]
    [SerializeField] private float m_groundProbeDistance = 1.5f;

    [Tooltip("루트 yaw를 몸이 누운 방향에 맞춘다 — 기상 모션이 '루트 전방을 향해 누워 있다'를 전제하므로. " +
             "비행 중에도 매 프레임 맞춘다(FollowBodyYaw) — 정착 때 한 번에 돌리면 그 회전이 원격에 " +
             "늦게 도착해 시체가 루트를 축으로 휙 돈다")]
    [SerializeField] private bool m_alignRootYawToBody = true;

    [Tooltip("몸 방향 대비 루트 yaw 보정(도) — Knockdown_StandUp 클립이 어느 쪽을 머리로 보는지에 맞춘다. " +
             "Editor에서 부활을 눌러 보며 조정할 값이다. 리그가 내는 '몸 방향'은 순수한 값이고 " +
             "(RagdollRig.TryGetBodyYaw) 클립 사정인 이 보정만 여기서 얹는다")]
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

    [Header("애니메이터 복귀")]
    [Tooltip("정착 포즈 → 애니메이터 포즈 보간 시간(초)")]
    [SerializeField] private float m_blendSeconds = 0.4f;

    private RagdollRig m_rig; // 뼈 한 벌 — 물리 조작 전부를 여기 위임한다
    private RagdollRope m_rope; // 밧줄 견인 (선택 — 없으면 운반이 물리로 안 끌린다)

    private Animator m_animator;
    private CharacterController m_controller;
    private PlayerIncapacitation m_incapacitation;
    private PlayerMovement m_movement;
    private NetworkObject m_netObject;

    private Transform m_root; // CharacterController가 붙은 트랜스폼 = 판정·동기화의 주체

    private RagdollState m_state = RagdollState.Animated;
    private float m_stillTimer;
    private float m_elapsedInRagdoll;

    // 늦게 접속했는데 대상이 이미 죽어 있던 경우 — 이번 사망은 래그돌을 건너뛴다.
    // 그때의 물리 낙하는 "죽는 순간"이 아니라 이미 끝난 과거라, 재생하면 시체가 뒤늦게 한 번 더 무너진다.
    // (PlayerIncapacitation.RefreshAimHitbox가 스폰 시 한 번 상태를 맞추는 것과 같은 계열의 처리)
    private bool m_skipThisEpisode;
    private bool m_polledOnce;

    // 이번 래그돌 에피소드에서 <b>사망을 한 번이라도 관측했는가.</b> 부활 판정의 전제다 —
    // PollDeath의 주석에 이유가 적혀 있다. (§9-19)
    private bool m_sawDeathThisEpisode;
    private float m_awaitingDeathSeconds;

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
        m_rig = GetComponent<RagdollRig>();
        m_rig.EnsureCollected(); // Awake 순서는 보장되지 않는다 — 아래 IgnoreOwnCapsule이 뼈를 요구한다
        m_rope = GetComponent<RagdollRope>();

        m_animator = GetComponentInChildren<Animator>();
        m_controller = GetComponentInParent<CharacterController>();
        m_incapacitation = GetComponentInParent<PlayerIncapacitation>();
        m_movement = GetComponentInParent<PlayerMovement>();
        m_netObject = GetComponentInParent<NetworkObject>();

        // 판정의 주체는 CharacterController가 붙은 트랜스폼이다 — 이 컴포넌트가 프리팹 어디에 붙어도
        // 같은 것을 가리키게 한다.
        m_root = m_controller != null ? m_controller.transform : transform;

        IgnoreOwnCapsule();
    }

    // ---- 캡슐(대리값) 다루기 — 여기부터가 플레이어 고유다 ----

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
    private void IgnoreOwnCapsule() => m_rig.IgnoreCollisionWith(m_controller, true);

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

    /// <summary>
    /// 정착 = 완전 정지가 아니다. <b>뼈를 전부 물리에 두고, 그대로 둔다.</b>
    ///
    /// 네 번째 방식이다. 앞의 셋은 각각 반쪽만 얻었다:
    ///  · <b>전부 키네마틱</b> — 운반은 되지만 몸이 하나의 자세로 굳는다 (처음 구현)
    ///  · <b>골반만 키네마틱</b> — 둘 다 얻은 것처럼 보였지만 <b>§9-7의 원인이었다</b> (아래)
    ///  · <b>전부 물리 + 골반을 캡슐에 스프링으로</b> — 세게 잡으면 시체가 떠오르고, 약하게 잡으면
    ///    마찰(412N)을 못 이겨 안 끌린다. <b>수직으로 지고 수평으로 이기는 스프링은 없다</b>
    ///
    /// <b>골반만 키네마틱이 왜 틀렸나.</b> 키네마틱 골반은 리그 루트의 자식이라 <b>프레임 클럭</b>
    /// (원격은 네트워크 보간)으로 움직이고, 나머지는 <b>물리 클럭</b>이다. 관절이 그 두 클럭을
    /// 이으므로 스텝마다의 양자화 차이가 곧 관절 위반이 되고, 솔버가 한 스텝에 해소하며 사지를
    /// 채찍처럼 당긴다 — <b>몸이 찢어진다.</b> 보간 모드도 갈려(동적 Interpolate / 키네마틱 None)
    /// 3m/s면 골반 이음새에 상시 약 6cm 어긋남이 생겼다.
    ///
    /// 그리고 <b>키네마틱은 무한 강성이다</b>. 골반이 지면과 안 맞는 높이에 고정되면 거기 매달린
    /// <c>Spine_02</c>가 지면으로 밀려 들어가고, 물리는 키네마틱 골반을 밀어낼 수 없어 매 스텝
    /// 싸운다 — 실측 <c>Spine_02 ← 충격 121</c>이 수십 스텝 지속되며 <b>허리가 땅에 박힌 채
    /// 몸이 부들부들 떨렸다.</b>
    ///
    /// <b>그래서 정착은 아무것도 붙들지 않는다.</b> 시체는 그냥 물리에 놓인 뼈다. 끌고 가는 것은
    /// <see cref="BeginRopePull"/>이 붙이는 <b>밧줄</b>이 하고, 그동안 캡슐은
    /// <see cref="TickCapsuleFollow"/>로 시체를 따라간다 — <b>사망 구간 내내 주인은 시체다.</b>
    ///
    /// <b>전 피어가 물리를 유지한다.</b> 원격에서 뼈를 키네마틱으로 굳혔다가 되돌렸다 — 굳히면
    /// 시체가 루트 높이 하나에 매달린 조각상이 되어, 그 높이가 조금이라도 틀리면 흡수할 수단이 없어
    /// 바닥에 박히거나 공중에 뜬다. 물리가 있으면 중력·접촉이 흡수한다. (§10-3)
    /// </summary>
    private void RestToPhysics()
    {
        IgnoreOwnCapsule(); // 뼈를 다시 물리로 놓아주기 전에 — 정착 중 캡슐 토글이 무시를 지웠을 수 있다
        m_rig.SetKinematic(false);
    }

    // ---- 밧줄 파사드 (#365 운반 / #398 드래그) ----
    //
    // 실물은 RagdollRope가 쥔다. 여기 파사드를 두는 이유는 호출부(PlayerTowedMotion)가 "래그돌인
    // 대상에게 밧줄을 묶는다"를 표현하기 때문이다 — 밧줄 컴포넌트를 직접 찾게 하면 "래그돌이 아닐 때는
    // 묶으면 안 된다"는 조건이 호출부로 새어 나간다.

    /// <summary>
    /// 밧줄을 시체에 묶는다 — <see cref="PlayerTowedMotion.BeginDraggedFollow"/>가 래그돌인 대상에게만 부른다.
    /// </summary>
    /// <param name="carrier">운반자(밧줄을 쥔 쪽). 보통 손 앵커다.</param>
    public void BeginRopePull(Transform carrier) => m_rope?.Attach(carrier);

    /// <summary>밧줄을 푼다 — 내려놓기·부활·운반자 소실.</summary>
    public void EndRopePull() => m_rope?.Detach();

    // ---- 진입 / 이탈 ----

    /// <summary>
    /// 래그돌 진입 — <b>멱등이다.</b> 이미 물리 중이면 임펄스만 누적하고, 정착·블렌드 중이면 무동작.
    ///
    /// 멱등이어야 하는 이유는 원격 클라의 도착 순서다. 사망 사실은
    /// <see cref="PlayerIncapacitation"/>의 NetworkVariable로, 폭발 사망자 목록은
    /// <see cref="BombDevice"/>의 ClientRpc로 온다 — 서로 다른 오브젝트라 같은 틱에 실려 와도
    /// 콜백 순서가 보장되지 않는다. 순서를 맞추려 들지 말고 어느 쪽이 먼저 와도 결과가 같게 만든다.
    /// <b>반대 순서(임펄스가 먼저)</b>는 <see cref="PollDeath"/>가 막는다 — §9-19.
    /// </summary>
    /// <param name="impulse">폭심에서 밀려나는 속도(m/s). 힘없이 무너지는 사망은 <see cref="Vector3.zero"/>.</param>
    public void EnterRagdoll(Vector3 impulse)
    {
        if (!m_rig.IsValid)
            return;

        if (m_state == RagdollState.Ragdoll)
        {
            m_rig.ApplyImpulse(impulse); // 늦게 도착한 폭발 정보 — 누적한다
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

        m_rig.SetSkinsAlwaysVisible(true);

        // <b>사망 중에는 캡슐을 끈다</b> (§10-0). 대리값이 물리 오브젝트일 이유가 없고, 켜 두면
        // ① 뼈와 서로 충돌해 §9-1의 재적용 싸움이 필요하고 ② 스윕이 지형에 막혀 추종이 실패한다.
        // 호송(#279)이 같은 패턴이다 — SetControllerEnabled(false) 후 트랜스폼 직접 대입.
        SetControllerEnabled(false);
        IgnoreOwnCapsule(); // 캡슐이 꺼져 있으면 사실상 무동작이지만, 전이 순간의 안전망으로 남긴다
        m_rig.SetKinematic(false);
        m_rig.ApplyImpulse(impulse);
    }

    /// <summary>
    /// 날아가는 구간을 즉시 끝내고 정착 상태로 넘긴다 — 운반 시작(#365)처럼 외부 사정으로 몸을 캡슐에
    /// 붙여야 할 때. 정착해도 몸이 굳지는 않는다(<see cref="RestToPhysics"/>).
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
        if (m_state == RagdollState.Animated || !m_rig.IsValid)
            return;

        // 에피소드가 여기서 끝난다 — 다음 사망은 자기 사망을 다시 관측해야 부활할 수 있다 (PollDeath).
        m_sawDeathThisEpisode = false;
        m_awaitingDeathSeconds = 0f;

        if (m_state == RagdollState.Ragdoll)
            Settle(); // 날아가는 중이면 먼저 포즈를 확정한다

        // 정착 상태에서는 뼈가 전부 물리에 있다(<see cref="RestToPhysics"/>) — 애니메이터로 돌아가려면
        // 전부 멈춰야 한다. 안 멈추면 블렌드가 놓는 포즈를 물리가 매 스텝 덮는다.
        // 밧줄을 먼저 끊는다: 키네마틱 바디에는 관절이 안 먹지만, 순서를 뒤집으면 한 스텝 동안
        // 스프링이 블렌드 시작 포즈를 당긴다.
        EndRopePull();
        m_rig.SetKinematic(true);

        // 캡슐을 되살린다 (§10-0 — 사망 중 꺼 뒀다). 이 시점의 루트는 정착 정렬이 지면 위에
        // 놓아 둔 자리이므로(ResolveSettledRootPose가 CapsuleBottomOffset까지 보정) 그대로 켜면 된다.
        SetControllerEnabled(true);
        m_movement?.ClearExternalVelocity(); // 꺼져 있던 동안 쌓인 값이 도착지에서 바닥을 파고들지 않게 (#189)

        if (!blend || m_animator == null || m_state == RagdollState.BlendingToAnimator)
        {
            if (m_animator != null)
                m_animator.enabled = true;
            m_state = RagdollState.Animated;
            m_rig.SetSkinsAlwaysVisible(false);
            return;
        }

        // 정착 포즈를 출발점으로 잡아 둔다 — 아래 LateUpdate가 애니메이터 포즈로 끌고 간다
        m_rig.BeginBlend();

        m_animator.enabled = true;
        // Down은 아직 참이다(AnimationDriver가 IsRagdollActive를 보고 붙들고 있다) — 바닥 대기 자세로
        // 물려 들어가야 정착 포즈와의 거리가 가장 짧다. 여기서 Ground를 직접 찍는 이유다.
        m_animator.Play(s_groundStateHash, 0, 0f);
        m_animator.Update(0f); // 이번 프레임 LateUpdate에서 바로 섞으려면 포즈가 이미 평가돼 있어야 한다

        m_state = RagdollState.BlendingToAnimator;
    }

    // ---- 매 프레임 ----

    private void Update()
    {
        RefreshCapsuleIgnoreOnReenable();
        PollDeath();

        if (m_state != RagdollState.Ragdoll)
            return;

        m_elapsedInRagdoll += Time.deltaTime;

        m_stillTimer = m_rig.AverageSpeed <= m_settleSpeedThreshold
            ? m_stillTimer + Time.deltaTime
            : 0f;

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

        Vector3 delta = m_root.position - m_rig.Hips.position;
        delta.y = 0f;
        return delta.sqrMagnitude <= k_alignedTolerance * k_alignedTolerance;
    }

    // 골반 밑에 지면이 있는가 — 정착 자격과 원격 정렬이 함께 쓴다. 탐색 거리는 정착 정렬과 같은 값을
    // 쓴다 (다른 값을 쓰면 "정착해도 된다"고 판단한 뒤 정렬이 지면을 못 찾는 모순이 생긴다).
    private bool HasGroundUnderHips() => TryGroundUnder(m_rig.Hips.position, out _);

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
                ExitToAnimator(blend: true); // 부활 — 정착 포즈에서 기상으로 잇는다
            }
            return;
        }

        if (!m_skipThisEpisode && m_state == RagdollState.Animated)
            EnterRagdoll(Vector3.zero); // 힘없이 무너지는 사망(진압봉·린치). 폭발은 임펄스를 따로 준다
    }

    private void LateUpdate()
    {
        // 블렌드는 LateUpdate에서 돈다 — 이 시점의 뼈 로컬값이 곧 애니메이터가 평가한 포즈다.
        // 완료되면 Animated로 돌아가고, 그때서야 AnimationDriver가 Down을 내려 기상 모션이 시작된다.
        if (m_state == RagdollState.BlendingToAnimator && m_rig.TickBlend(m_blendSeconds))
        {
            m_state = RagdollState.Animated;
            m_rig.SetSkinsAlwaysVisible(false);
        }

        // 원격의 시체를 스트리밍된 루트에 맞춘다 — 오너 쪽 짝(TickCapsuleFollow)은 PlayerMovement가
        // 돌린다(그쪽은 이 컴포넌트가 비활성인 원격에서도 순서를 보장해야 하는 시점·이동과 얽혀 있다).
        // 여기서 하는 이유: 원격에서는 PlayerMovement가 꺼져 있고(오너만 켜진다), NetworkTransform이
        // 이번 프레임에 적용한 루트 위치를 LateUpdate에서 읽어야 한 프레임 늦지 않는다.
        //
        // <b>정착 후에도 계속 맞춘다.</b> §9-7로 골반 용접을 없앤 뒤로는 정착 후 원격의 뼈를 붙들어
        // 주는 것이 아무것도 없어서, 밧줄로 끌면 <b>루트(이름표·파티클)만 가고 모델은 제자리에 남았다.</b>
        //
        // ⚠ 이건 힘 튜닝이 아니다 — <b>원격의 시체는 판정의 주인이 아니라 표시</b>이고, 권위 있는
        // 위치는 스트리밍된 루트다. 원격의 로컬 물리는 <b>포즈</b>만 만들고 <b>궤적</b>은 오너에게서
        // 받는다. 단 <b>동력이 아니라 표류 방지</b>다 — 몸을 움직이는 것은 각 피어의 로컬 물리
        // (임펄스·밧줄)이고, 여기서 하는 일은 그 결과가 루트에서 서서히 벗어나는 것을 막는 것뿐이다.
        // 입력이 같아지면 잔차가 작아 보정이 눈에 띄지 않는다. (§10-5)
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
    ///    바닥에 눌러앉아 관절이 늘어났다 되튕겼다(§9-5). 언제 도착할지는 두 피어의 물리 발산이
    ///    정하므로 상한이 없어, 기다리는 방식으로는 맞출 수 없었다
    ///
    /// 매 프레임 따라가게 하면 텔레포트가 <b>cm 단위 잔차</b>로 줄고, 원격은 점프 대신 연속
    /// 스트림을 받는다. 수렴·릴리스 유예·되붙듦이 전부 필요 없어진다.
    /// </summary>
    internal void TickCapsuleFollow()
    {
        if (m_movement == null || m_rig.Hips == null)
            return;

        // 골반 위치를 <b>3차원</b>으로 따라간다 — 수평만 맞추면 시체가 공중에 있는 동안 루트가 시체를
        // 대표하지 못하고, 이름표·운반 조준·부활 히트박스가 전부 루트에 붙어 있어 그만큼 어긋난다.
        Vector3 target = m_rig.Hips.position;

        // <b>단 정착 후에는 캡슐이 지면에 앉는다.</b> 수평은 그대로 골반을 따라가되 높이만 지면이 준다.
        //
        // 비행 중처럼 골반 높이(지면 위 약 0.3m)에 붙여 두면 두 가지가 깨진다:
        //  · 캡슐이 내내 <c>공중</c>이라 CharacterController가 접지를 못 본다
        //  · <b>끌리며 시체가 위아래로 튀는 것이 그대로 루트의 높이가 되어 스트림에 실린다.</b>
        //
        // 지면 판정은 정착 정렬(ResolveSettledRootPose)과 같은 것을 쓴다 — 두 곳이 다른 높이를
        // 내면 정착하는 순간 캡슐이 튄다.
        if (
            m_state == RagdollState.Settled
            && TryGroundUnder(m_rig.Hips.position, out Vector3 ground)
        )
            target.y = ground.y - CapsuleBottomOffset;

        // 사망 중에는 CharacterController가 꺼져 있으므로(EnterRagdoll) 대입이 곧 이동이다.
        // 스윕은 쓰지 않는다 — 서 있는 1.8m 캡슐과 굴러가는 탄도 시체는 갈 수 있는 곳이 다르고,
        // 지형이 갈리는 순간 캡슐이 뒤처져 동기화 위치가 시체를 대표하지 못한다
        // (실측: 시체 38.8m / 캡슐 0.23m, §9-11). 대리값은 지형을 존중할 이유가 없다 (§10-0).
        m_root.position = target;

        // ⚠ <b>yaw는 비행 중에만 따라간다.</b> 정착 후에도 돌리면 가만히 누운 시체의 머리가 계속
        // 돈다 — 루트가 돌면 <b>리지드바디가 없는 뼈</b>(Neck·Spine_01/03·손·발)는 계층을 따라
        // 같이 도는데 Head는 동적이라 안 따라가므로 목이 그 사이에서 비틀린다. 게다가 yaw를
        // 머리 위치에서 계산하므로 같은 프레임에 되먹임 고리가 생겨 회전이 멎지 않는다.
        //
        // 정착 후의 yaw는 ResolveSettledRootPose가 이미 확정했다 — 시체는 방향을 바꾸지 않는다.
        if (m_state == RagdollState.Ragdoll)
            FollowBodyYaw();
    }

    private void FollowBodyYaw()
    {
        if (!m_alignRootYawToBody || !TryGetRootYaw(out float yaw))
            return;

        m_root.rotation = Quaternion.Euler(0f, yaw, 0f);
    }

    // 루트가 향해야 할 yaw — 리그가 내는 순수한 몸 방향에 기상 클립 보정을 얹은 값.
    // 비행 중 추종(FollowBodyYaw)과 정착 정렬(ResolveSettledRootPose)이 같은 계산을 써야
    // 정착 순간에 회전이 안 튄다.
    private bool TryGetRootYaw(out float yaw)
    {
        if (!m_rig.TryGetBodyYaw(out yaw))
            return false;

        yaw += m_rootYawOffset;
        return true;
    }

    // ---- 원격 정렬 ----

    /// <summary>
    /// 원격 피어의 시체를 스트리밍된 루트에 맞춘다 — <b>원격 전용.</b>
    ///
    /// 오너의 캡슐이 골반을 따라오므로(<see cref="TickCapsuleFollow"/>) <b>스트리밍된 루트의 수평
    /// 위치가 곧 오너 골반의 수평 위치다</b> — 뼈를 따로 동기화하지 않고도 원격이 오너의 궤적을
    /// 받는다(#506 결정 4 "뼈를 동기화하지 않는다"를 지킨다).
    ///
    /// 리그 루트 오프셋으로는 못 고친다 — <b>동적 리지드바디는 부모 트랜스폼을 따르지 않는다.</b>
    /// 그래서 뼈의 <c>position</c>에 직접 델타를 더한다(<see cref="RagdollRig.TranslateBy"/>).
    /// </summary>
    private void TickAlignBonesToRoot()
    {
        // <b>비행 중에도 당겨온다.</b> §9-10이 "비행 중 정렬하면 시체 궤적이 캡슐 궤적으로 덮인다"고
        // 결론 냈지만, 그 측정은 <b>캡슐이 시체를 못 따라가던 시절</b>의 것이다(시체 38.8m / 캡슐
        // 0.23m). 진짜 교훈은 "보정하지 마라"가 아니라 <b>"접지된 캡슐을 기준으로 보정하지 마라"</b>
        // 였다 — 그때 루트는 지면을 타는 CharacterController였으니 그걸 향해 당기면 포물선이 평평해진다.
        //
        // 캡슐 추종이 들어온 뒤로 <b>스트리밍된 루트 = 오너 골반</b>이다(실측 시체 55.28m / 루트
        // 55.24m). 기준이 시체의 궤적 자체가 됐으므로 당겨오는 것이 이제는 옳다.
        bool grounded = HasGroundUnderHips();

        Vector3 delta = m_root.position - m_rig.Hips.position;

        // 착지 후에만 높이를 뺀다 — 그때는 각 피어의 지형 충돌이 높이의 주인이고, 같은 지형이므로
        // 편차가 작다. 반대로 <b>비행 중에는 3차원으로 맞춘다</b>: 공중에는 높이를 정해 줄 지형 접촉이
        // 없고, 오너 골반의 고도(=포물선의 정점)를 받아야 원격도 같은 궤적을 그린다. 여기서 y를 지우면
        // 원격 시체만 뜨지 않는 §9-10의 증상이 그대로 재현된다.
        if (grounded)
            delta.y = 0f;

        float distance = delta.magnitude;
        if (distance < 1e-4f)
            return;

        // 어느 쪽이든 <b>스냅이 아니라 당겨오기</b>다 — 상한이 있어야 루트가 병리적으로 튈 때 그
        // 점프가 뼈로 전달되지 않는다. 접지 후에는 느린 상한 — 보정은 동력이 아니라 표류 방지다
        // (§10-5). 몸은 밧줄이 끈다. 상한을 올려 "끌어오게" 만들려던 시도가 §9-16에서 발산으로 끝났다.
        float maxStep = (grounded ? m_alignPullSpeed : m_flightAlignPullSpeed) * Time.deltaTime;

        // 잔차가 임계를 넘으면 스냅한다 — 그 상태는 이미 눈에 띄게 틀렸으므로 포즈 보존이 의미가
        // 없고, 느린 상한으로는 영구히 못 따라잡는다(§10-5 규칙 6). 안전망이므로 봉투 바깥에 둔다.
        if (distance > m_alignSnapDistance)
            maxStep = distance;
        if (distance > maxStep)
            delta *= maxStep / distance;

        // <b>몸 전체를 같은 델타로 옮긴다.</b> 끄는 지점이 없으므로 강체 평행이동이 맞다: 포즈는
        // 이미 이 피어의 임펄스가 로컬로 만들고 있고, 여기서 하는 일은 그 궤적을 오너 것에 맞추는
        // 것뿐이다. 뼈마다 다르게 옮기면 만들어 둔 텀블이 깨진다.
        //
        // ⚠ <b>"골반만 옮겨 흐느적임을 만든다"를 여기서 시도했다가 되돌렸다.</b> 골반(10.9kg)만
        // 옮기면 나머지 59kg이 관절로 되당겨서 <b>질량비만큼(실측 12~21%)밖에 안 움직인다</b> —
        // 몸을 5m/s로 옮기려면 골반에 32m/s를 명령해야 하고 그러면 §9-5의 채찍질이 재현된다.
        // 실측에서 원격 격차가 <b>12.6m까지 발산</b>했다. 위치 추적을 이 방식으로는 못 한다.
        m_rig.TranslateBy(ClampByWall(delta));
    }

    private Vector3 ClampByWall(Vector3 delta)
    {
        const float k_skin = 0.02f; // 벽에 딱 붙이지 않고 살짝 띄운다 — 겹치면 탈출 임펄스가 생긴다

        Vector3 from = m_rig.Hips.position;
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
    // ③은 비행 중 캡슐이 이미 따라와 있으므로 <b>cm 단위 잔차</b>만 남는다. 원격은 아무것도 옮기지
    // 않는다: 루트는 비행 내내 오너 값을 스트리밍받았고 뼈는 그 루트에 맞춰져 있다
    // (<see cref="TickAlignBonesToRoot"/>). 흡수할 어긋남이 없으니 수렴도 유예도 없다.
    private void Settle()
    {
        m_rig.CapturePose();
        Vector3 landedHips = m_rig.Hips.position;

        m_rig.SetKinematic(true);

        // 캡슐은 진입 때 이미 꺼져 있다(§10-0) — 여기서 토글할 것이 없다. 켜져 있으면 지면 레이가
        // 자기 캡슐에 걸리고(캡슐도 Default 레이어다) 트랜스폼 대입도 내부 캐시가 되돌린다.

        Vector3 rootPosition = m_root.position;
        Quaternion rootRotation = m_root.rotation;
        ResolveSettledRootPose(landedHips, ref rootPosition, ref rootRotation);

        if (HasMoveAuthority)
        {
            m_root.SetPositionAndRotation(rootPosition, rootRotation);

            // 텔레포트로 도착했으니 쌓인 수직 속도를 지운다 — <see cref="PlayerMovement.SetPose"/>가
            // 같은 이유로 하는 처리다(#189). 여기는 SetPose를 거치지 않고 트랜스폼을 직접 옮기므로
            // 그 짝이 필요하다.
            m_movement?.ClearExternalVelocity();
        }

        m_rig.RestoreCapturedPose();

        // 루트가 더 이상 나중에 점프하지 않으므로 양쪽 모두 곧장 물리로 놓아준다 — 누운 몸이 계속
        // 흔들리게. 원격의 릴리스 타이밍을 재던 유예 구간은 이 설계에서 사라졌다.
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
        // 돌기 전까지 isGrounded가 거짓이라 그동안 중력이 쌓이고, 그 낙하가 시체를 끌어내린다.
        position = GroundUnder(landedHips) - Vector3.up * CapsuleBottomOffset;

        // 비행 중 이미 맞춰 온 값이라 보통 잔차만 남는다 — 추종이 꺼져 있거나 오프라인일 때가 본작업.
        if (m_alignRootYawToBody && TryGetRootYaw(out float yaw))
            rotation = Quaternion.Euler(0f, yaw, 0f);
    }

    // 루트 원점에서 캡슐 밑면까지의 높이 — 위 ResolveSettledRootPose 주석 참고.
    private float CapsuleBottomOffset =>
        m_controller == null ? 0f : m_controller.center.y - m_controller.height * 0.5f;

    // 정착 정렬용 지면 — 여기까지 왔다면 보통 지면이 있다(Update가 없으면 정착을 미룬다).
    //
    // ⚠ 못 찾는 경우는 <b>맵 밖으로 떨어진 시체</b>뿐이고, 그때는 골반 높이를 쓴다. 예전 주석은
    // "CharacterController의 중력이 남은 차이를 메운다"고 적었지만 <b>그건 거짓이다</b> — 원격은
    // PlayerMovement가 꺼져 있어 중력이 돌지 않는다. 그래서 이 경로로 오지 않게 막는 것이
    // Update의 지면 판정이다.
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
}
