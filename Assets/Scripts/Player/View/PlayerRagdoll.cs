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
/// <b>Animator를 끄지 않는다</b> (#571). 사망 모델을 분리한 뒤로는 애니메이터와 물리가 <b>서로 다른
/// 리그</b>를 쥐므로 싸울 일이 없다 — 살아있는 뼈는 보이지 않는 채 계속 애니메이션되고, 그 포즈가
/// 부활 블렌드의 목표가 된다(<see cref="RagdollPoseBlend"/>). 예전에는 같은 리그를 번갈아 쥐었기
/// 때문에 이 컴포넌트가 Animator를 단독으로 켜고 껐다.
///
/// 래그돌이 켜져 있는 동안 <see cref="PlayerAnimationDriver"/>는 <c>Down</c>을 내리지 못한다
/// (<see cref="IsRagdollActive"/>를 보고 참으로 붙든다) — 안 막으면 부활 블렌드 도중에 기상 모션이
/// 먼저 시작된다.
///
/// <b>프리팹 루트에 붙인다</b> — CharacterController·PlayerIncapacitation과 같은 오브젝트.
/// <see cref="PlayerMovement"/>·<see cref="PlayerHeadLook"/>이 <c>GetComponent</c>로,
/// <see cref="PlayerAnimationDriver"/>가 <c>GetComponentInParent</c>로 찾는다.
///
/// ⚠ <see cref="RagdollRig"/>는 여기가 아니라 <b>사망 전용 모델</b>(<c>Corpse</c>)에 붙는다 —
/// 그래서 <c>GetComponent</c>가 아니라 <c>GetComponentInChildren</c>으로 찾는다. NPC와 같은 배치다
/// (<see cref="NpcRagdoll"/>).
/// </summary>
public class PlayerRagdoll : MonoBehaviour
{
    // 지면을 못 찾아도 결국은 정착시키는 최후 배수 — m_settleTimeoutSeconds의 몇 배까지 기다릴지.
    // 맵 밖으로 떨어져 나간 시체가 Ragdoll 상태에 영원히 갇히지 않게 하는 안전장치일 뿐이다.
    // 이 경로로 들어오면 시체는 허공에 굳지만 상태 기계는 계속 돈다 —
    // 되살릴 때 판정을 쥔 것은 루트이므로 부활·라운드 리셋은 정상 동작한다.
    private const float k_lostBodyTimeoutFactor = 4f;


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
             "늦게 도착해 시체가 루트를 축으로 휙 돈다.\n\n" +
             "몸이 아직 서 있는 동안은 RagdollRig.TryGetBodyYaw가 방향을 내지 않으므로 기존 yaw가 " +
             "유지된다 — 그래서 이 추종은 몸이 실제로 기운 뒤에 시작한다")]
    [SerializeField] private bool m_alignRootYawToBody = true;

    [Tooltip("루트 yaw 추종 감쇠율(1/초) — 0이면 즉시 대입(예전 동작)이다.\n\n" +
             "⚠ <b>즉시 대입은 위험하다.</b> 몸 방향은 골반→머리를 투영해 얻는데 그 값은 몸이 " +
             "막 기우는 동안 아직 흔들린다. 그때 루트를 그대로 슬램하면 루트에 매달린 것들 " +
             "(이름표·상호작용·들고 있던 아이템 모델)이 한 프레임에 통째로 돌아간다.\n\n" +
             "감쇠는 그 흔들림을 흡수한다. 목표가 안정된 뒤에는 곧 수렴하므로 정착 정렬 " +
             "(ResolveSettledRootPose)에 남는 잔차는 작다")]
    [SerializeField] private float m_rootYawFollowSpeed = 8f;

    [Tooltip("몸 방향 대비 루트 yaw 보정(도) — Knockdown_StandUp 클립이 어느 쪽을 머리로 보는지에 맞춘다. " +
             "Editor에서 부활을 눌러 보며 조정할 값이다. 리그가 내는 '몸 방향'은 순수한 값이고 " +
             "(RagdollRig.TryGetBodyYaw) 클립 사정인 이 보정만 여기서 얹는다")]
    [SerializeField] private float m_rootYawOffset;

    [Header("애니메이터 복귀")]
    [Tooltip("정착 포즈 → 애니메이터 포즈 보간 시간(초)")]
    [SerializeField] private float m_blendSeconds = 0.4f;

    [Tooltip("부활 순간의 yaw를 실측해 콘솔에 남긴다 — m_rootYawOffset을 맞추기 위한 계측이다. " +
             "값이 확정되면 끈다")]
    [SerializeField] private bool m_logRevivalYaw;

    [Header("사망 전용 모델 (#571 분리)")]
    [Tooltip("살아있는 몸의 스킨 — 사망 중에만 끈다.\n\n" +
             "⚠ <b>뼈(Root)는 끄지 않는다.</b> Animator의 아바타 바인딩이 경로 기반이라 리그를 끄거나 " +
             "한 단 더 깊이 옮기면 살아있는 애니메이션이 끊긴다. 안 보이는 뼈가 계속 애니메이션되는 " +
             "것은 무해하고, 부활 블렌드가 그 포즈를 목표로 삼으므로 오히려 필요하다")]
    [SerializeField] private GameObject m_liveSkin;

    [Tooltip("살아있는 리그 최상단(Root) — 사망 시 이 포즈를 시체로 넘기고, 부활 시 시체의 정착 포즈를 " +
             "여기로 되돌린다. 시체 리그와 같은 서브트리의 복제본이어야 한다(RagdollPose의 전제)")]
    [SerializeField] private Transform m_liveBoneRoot;

    private RagdollRig m_rig; // 뼈 한 벌 — 물리 조작 전부를 여기 위임한다. 시체 모델에 붙어 있다
    private RagdollRope m_rope; // 밧줄 견인 (선택 — 없으면 운반이 물리로 안 끌린다)

    private GameObject m_corpse; // 시체 모델 = 리그가 붙어 있는 오브젝트. <b>항상 활성</b> (아래 주석)
    private RagdollPoseBlend m_blend; // 부활 블렌드 — <b>살아있는</b> 리그를 섞는다
    private int m_expectedPoseBones; // 포즈 복사가 전부 닿았는지 대조할 기대치 (CopyPose)
    private bool m_corpseVisible; // 시체가 지금 보이고 물리에 참여하는가 (오브젝트 활성 여부가 아니다)


    [Tooltip("정착 순간 앞뒤 20프레임을 한 줄씩 찍는다 — <b>정착할 때 몸이 아래로 내려갔다 올라오는</b> " +
             "현상을 잡는 계측이다.\n\n" +
             "딥은 한순간이라 사후에 찍으면 이미 지나가 있다. 그래서 매 프레임을 링버퍼에 담아 두고 " +
             "정착하는 순간 <b>이전 20프레임 + 이후 20프레임</b>을 함께 쏟는다.\n\n" +
             "읽는 법 — 프레임 0이 정착이다:\n" +
             "· <b>루트Y가 0에서 뚝 떨어지면</b> Settle이 루트를 지면으로 내린 낙차다. 원격의 " +
             "키네마틱 뼈가 계층으로 그것을 따라가면 골반월드Y도 같이 내려간다\n" +
             "· <b>골반월드Y만 내려갔다 올라오면</b> 스트림 종료와 최종 자세 적용 사이의 빈 구간이다\n" +
             "· <b>골반로컬Y가 튀면</b> 최종 자세(로컬 좌표)가 옛 루트 기준으로 적용된 것이다\n\n" +
             "확정되면 끈다")]
    [SerializeField] private bool m_logSettleTrace;

    // 전 뼈 자세 스트림 — 이 컴포넌트가 피어로 내보내는 유일한 통로다.
    // NPC와 같은 부품을 그대로 쓰고, 갈리는 것은 <b>권위뿐</b>이다(프리팹에서 Owner로 박는다).
    private RagdollPoseStreamer m_streamer;

    // 첫 패킷이 오기 전까지 붙들 자세를 잡았는가 — <see cref="TickHoldPoseUntilStream"/>.
    private bool m_holdPoseUntilStream;

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
        // ⚠ 리그는 <b>자식</b>(Corpse)에 있다 — 사망 전용 모델을 분리하면서 옮겼다.
        // 비활성 오브젝트라 includeInactive를 반드시 켠다.
        m_rig = GetComponentInChildren<RagdollRig>(true);
        if (m_rig == null)
        {
            Debug.LogWarning(
                $"PlayerRagdoll: RagdollRig를 찾지 못해 사망 래그돌을 끈다 — {name}. "
                    + "Corpse 오브젝트에 RagdollRig가 붙어 있는지 확인할 것",
                this
            );
            enabled = false;
            return;
        }

        // 시체는 평시 비활성이라 <see cref="RagdollRig.Awake"/>가 아직 돌지 않았다 — 여기서 보장한다.
        // (비활성 오브젝트에서도 transform 탐색·GetComponentsInChildren(true)는 정상 동작한다)
        m_rig.EnsureCollected();

        m_corpse = m_rig.gameObject;

        // 시체는 <b>항상 활성</b>이어야 한다 — NGO가 비활성 GameObject의 NetworkBehaviour를 스폰에서
        // 제외하고 나중에 만회하지 않으므로(RagdollRig.SetBoneCollidersEnabled 주석) 골반의
        // NetworkTransform이 영구히 죽는다. 그래서 평시 숨김은 렌더러·콜라이더로 한다.
        if (m_corpse != null && !m_corpse.activeSelf)
            m_corpse.SetActive(true);

        SetCorpseVisible(false); // 평시 — 안 보이고 물리에도 참여하지 않는다

        // 시체 리그는 런타임에 자식이 늘지 않으므로(RagdollPose 주석) 이 수가 곧 포즈 복사의 기대치다.
        m_expectedPoseBones = m_rig.BoneRoot != null
            ? m_rig.BoneRoot.GetComponentsInChildren<Transform>(true).Length
            : 0;

        // 밧줄도 리그와 같은 오브젝트에 있다 — RagdollRope가 RagdollRig를 RequireComponent한다.
        m_rope = m_rig.GetComponent<RagdollRope>();

        // 블렌드는 <b>살아있는</b> 리그를 섞는다 — 부활 시점의 시체는 이미 꺼져 있다.
        m_blend = new RagdollPoseBlend(m_liveBoneRoot);
        if (m_liveBoneRoot == null || m_liveSkin == null)
        {
            Debug.LogWarning(
                $"PlayerRagdoll: 살아있는 모델 참조가 비어 있다 — {name} "
                    + $"(m_liveBoneRoot={(m_liveBoneRoot == null ? "없음" : m_liveBoneRoot.name)}, "
                    + $"m_liveSkin={(m_liveSkin == null ? "없음" : m_liveSkin.name)}). "
                    + "사망 시 모델 교체가 동작하지 않는다",
                this
            );
        }

        m_animator = GetComponentInChildren<Animator>();
        m_controller = GetComponentInParent<CharacterController>();
        m_incapacitation = GetComponentInParent<PlayerIncapacitation>();
        m_movement = GetComponentInParent<PlayerMovement>();
        m_netObject = GetComponentInParent<NetworkObject>();

        // 판정의 주체는 CharacterController가 붙은 트랜스폼이다 — 이 컴포넌트가 프리팹 어디에 붙어도
        // 같은 것을 가리키게 한다.
        m_root = m_controller != null ? m_controller.transform : transform;

        // 스트리머는 <b>루트</b>에 있다 — NGO가 비활성 GameObject의 NetworkBehaviour를 스폰에서
        // 제외하므로 <c>Corpse</c> 같은 하위가 아니라 NetworkObject와 같은 오브젝트여야 한다.
        m_streamer = m_root.GetComponent<RagdollPoseStreamer>();
        if (m_streamer == null)
        {
            Debug.LogWarning(
                $"PlayerRagdoll: RagdollPoseStreamer가 없다 — {name}. 원격 피어에 자세가 가지 않아 "
                    + "시체가 진입 자세로 굳는다. Player 프리팹 루트에 붙일 것",
                this
            );
        }
        else
        {
            // 정착 자세가 도착하면 원격도 그 자리에서 정착으로 넘긴다 — <b>원격의 종착 상태다.</b>
            m_streamer.OnSettledPoseReceived += HandleSettledPoseReceived;
        }

        // ⚠ 여기서 캡슐 무시를 걸지 않는다 — 시체가 비활성이라 뼈 콜라이더도 비활성이고,
        // <c>Physics.IgnoreCollision</c>은 비활성 콜라이더에 대해 에러를 뱉는다.
        // 걸 수 있는 유일한 시점은 시체를 켜는 순간이다(<see cref="ShowCorpse"/>).
    }

    // ---- 모델 교체 (#571 사망 전용 모델 분리) ----

    /// <summary>
    /// 시체를 켠다 — <b>사망 시 표현 전환의 전부.</b>
    ///
    /// <b>애니메이터를 끄지 않는다.</b> 예전에는 같은 리그를 애니메이터와 물리가 번갈아 쥐었기 때문에
    /// 사망 순간 애니메이터를 꺼야 했다. 모델이 갈린 뒤로는 서로 다른 리그를 쥐므로 싸울 일이 없다 —
    /// 살아있는 뼈는 보이지 않는 채 계속 애니메이션되고, 그 포즈는 부활 블렌드의 목표로 쓰인다.
    ///
    /// <b>포즈를 켜기 전에 넘긴다.</b> 켠 뒤에 넘기면 그 사이 한 물리 스텝이 프리팹 기본 포즈로
    /// 시뮬레이션돼 시체가 엉뚱한 자세에서 출발한다.
    /// </summary>
    private void ShowCorpse()
    {
        CopyPose(m_liveBoneRoot, m_rig.BoneRoot);

        SetCorpseVisible(true);
        if (m_liveSkin != null)
            m_liveSkin.SetActive(false);

        m_rig.SetSkinsAlwaysVisible(true);
        IgnoreOwnCapsule(); // 뼈 콜라이더가 이제 켜졌다 — 걸 수 있는 첫 시점이다

        // ⚠ 물리로 넘기기 <b>전에</b> 열어야 복사가 옮긴 것과 물리가 바꿔 놓은 것이 갈린다.
        BeginEntryTrace();
        BeginFallRateTrace();

        ReleaseBonesToPhysics();
    }

    /// <summary>
    /// 시체를 끄고 살아있는 몸으로 되돌린다 — 부활·라운드 리셋.
    ///
    /// <b>정착 포즈를 살아있는 리그로 넘기는 것이 핵심이다.</b> 그러지 않으면 살아있는 뼈는 사망
    /// 내내 애니메이터가 놓아 둔 자세이므로, 시체를 끄는 프레임에 몸이 누운 자세에서 그 자세로 툭 튄다.
    /// 넘겨 두면 <see cref="RagdollPoseBlend"/>가 그 자리에서 기상 자세로 이어 준다.
    /// </summary>
    private void HideCorpse()
    {
        CopyPose(m_rig.BoneRoot, m_liveBoneRoot);

        m_rig.SetSkinsAlwaysVisible(false);

        SetCorpseVisible(false);
        if (m_liveSkin != null)
            m_liveSkin.SetActive(true);

        // 시체가 쉬는 동안 <b>뼈 길이를 프리팹 값으로 되돌린다</b> — 자세를 되돌리는 것이 아니다.
        //
        // 물리가 관절을 늘린 채 정착하면 그 길이가 뼈의 로컬 위치에 굳는데, 관절의
        // <c>connectedAnchor</c>는 <b>바인드 포즈 기준으로 구워져 있다</b>(프리팹이
        // <c>AutoConfigureConnectedAnchor = 1</c>). 그대로 두면 다음 사망이 <b>관절이 위반된 채</b>
        // 출발해 사지가 고무처럼 늘어나며 바닥을 뚫는다(실측: 1차 0.0055m → 2차 0.0624m로 누적,
        // 2차에서 발이 띄워 올린 바닥을 지나 허공에 매달렸다).
        //
        // <b><see cref="RagdollPose.Copy"/>의 짝이다.</b> 저쪽이 <b>리그 사이 전파</b>를 막고,
        // 이쪽이 시체 <b>자신의 누적</b>을 끊는다. 하나만으로는 다른 경로로 되돌아온다.
        //
        // <b>맨 뒤여야 한다.</b> 위 CopyPose가 정착 포즈를 살아있는 리그로 넘긴 뒤여야 하고
        // (블렌드의 출발점), 뼈는 ExitToAnimator가 이미 키네마틱으로 돌려놓아 물리에 덮이지 않는다.
        m_rig.RestoreBindPose();
    }

    // 살아있는 리그에서 뼈를 이름으로 찾는다 — 진단용. 살아있는 리그에는 리지드바디가 없으므로
    // RagdollRig 의 수집 방식(관절 없는 뼈 = 골반)을 쓸 수 없다.
    private Transform FindLiveBone(string boneName)
    {
        if (m_liveBoneRoot == null)
            return null;

        Transform[] bones = m_liveBoneRoot.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < bones.Length; i++)
        {
            if (bones[i].name == boneName)
                return bones[i];
        }

        return null;
    }

    /// <summary>
    /// 시체를 보이게/숨기게 한다 — <b>GameObject를 끄지 않는다.</b>
    ///
    /// 시체 오브젝트는 항상 활성이어야 한다(<see cref="RagdollRig.SetBoneCollidersEnabled"/> 주석의
    /// NGO 사정). 그래서 "없는 것처럼" 만드는 일을 렌더러와 콜라이더가 나눠 맡는다 — 렌더러는 보이지
    /// 않게, 콜라이더는 세계와 부딪히지 않게. 뼈는 평시 키네마틱이라 그 자체로는 아무 일도 하지 않는다.
    /// </summary>
    private void SetCorpseVisible(bool visible)
    {
        m_corpseVisible = visible;
        m_rig.SetSkinsEnabled(visible);
        m_rig.SetBoneCollidersEnabled(visible);
    }

    /// <summary>
    /// 뼈를 물리로 놓아준다 — <b>골반만은 비권위 피어에서 키네마틱으로 남긴다.</b>
    ///
    /// 골반을 NetworkTransform이 복제하는 구성에서는 원격의 골반이 <b>물리가 아니라 스트림</b>의
    /// 소유물이다. <c>NetworkRigidbody</c>의 <c>AutoUpdateKinematicState</c>는 스폰·소유권 변경
    /// 시점에만 도는 값이라 래그돌의 토글과 어긋나므로 꺼 두고 여기서 직접 관리한다.
    ///
    /// <see cref="RagdollRig"/>가 아니라 여기서 하는 이유는 분리의 기준이다 — 저쪽에는
    /// <c>IsOwner</c>·<c>NetworkObject</c>가 한 번도 나오지 않는다.
    /// </summary>
    private void ReleaseBonesToPhysics()
    {
        // ⚠ <b>자세를 스트림으로 받는 피어는 물리를 아예 돌리지 않는다 — 전 뼈 키네마틱.</b>
        //
        // 골반 하나만 고정하고 나머지 열을 동적으로 두던 것이 지금까지의 구성인데, 그러면 골반이
        // 크게 움직일 때마다 관절이 위반돼 솔버가 <b>중력으로는 나올 수 없는 속도</b>를 먹인다
        // (NPC에서 실측 6261 m/s). 전부 키네마틱이면 <b>위반될 관절이 원리적으로 없다</b> —
        // 관절은 동적 바디 사이에서만 힘을 만든다.
        //
        // 로컬 물리가 만들던 흐느적임을 잃지만, <b>그 흐느적임이 곧 피어마다 다른 몸이었다.</b>
        if (m_streamer != null && !HasMoveAuthority)
        {
            m_rig.SetKinematic(true);

            // 첫 패킷이 오기 전까지 붙들 자세를 잡아 둔다 — 그 사이에도 루트는 이미 움직인다.
            m_rig.CapturePose();
            m_holdPoseUntilStream = true;
            return;
        }

        m_rig.SetKinematic(false);
    }

    /// <summary>
    /// 원격이 정착 자세를 받았다 — 그 자리에서 정착으로 넘긴다. (전 뼈 스트리밍)
    ///
    /// <b>자세는 스트리머가 이미 입혔고, 여기서 하는 것은 상태 전이뿐이다.</b> 권위 피어의
    /// <see cref="Settle"/>이 하는 나머지(루트 포즈 확정·물리 복귀)는 <b>여기서 하면 안 된다</b> —
    /// 그건 자기 물리의 결과를 정리하는 일이고, 원격에는 그 물리가 없다.
    ///
    /// <b>NPC와 갈리는 지점</b>: 저쪽은 정착이 곧 얼림이라 <c>Freeze()</c>를 불렀지만, 플레이어의
    /// 원격 뼈는 <b>이미 상시 키네마틱</b>이라 따로 얼릴 것이 없다. 남는 것은 상태뿐이다.
    /// </summary>
    private void HandleSettledPoseReceived()
    {
        if (m_state == RagdollState.Animated)
            return; // 이번 사망을 건너뛴 피어 — 살아있는 몸을 시체 상태로 밀지 않는다

        m_holdPoseUntilStream = false;
        m_state = RagdollState.Settled;
        DumpSettleTrace();
    }

    private void OnDestroy()
    {
        if (m_streamer != null)
            m_streamer.OnSettledPoseReceived -= HandleSettledPoseReceived;
    }

    // 포즈를 옮기고 <b>전부 닿았는지 대조한다.</b>
    //
    // ⚠ 이 대조가 없어서 한 번 크게 물렸다. 아이템 모델이 살아있는 손 본 밑에 인스턴스화되면서 두
    // 리그의 자식 수가 갈렸는데, 그때의 복사 함수는 그것을 실패로 보고 <b>조용히 아무것도 하지
    // 않았다</b> — 시체가 첫 사망에 바인드 포즈로, 이후 사망에 직전 누운 포즈로 나타났다.
    // 지금 함수는 이름으로 짝지어 그 상황에 영향받지 않지만, <b>조용히 지나갈 수 있는 실패는 다시
    // 만들지 않는다.</b>
    private void CopyPose(Transform from, Transform to)
    {
        int copied = RagdollPose.Copy(from, to);
        if (copied == m_expectedPoseBones)
            return;

        Debug.LogWarning(
            $"PlayerRagdoll: 포즈 복사가 {copied}/{m_expectedPoseBones} 뼈에만 닿았다 — {name}. "
                + "두 리그가 같은 서브트리의 복제본인지 확인할 것 (뼈 이름이 갈리면 그 아래가 통째로 빠진다)",
            this
        );
    }

    // ---- 캡슐(대리값) 다루기 — 여기부터가 플레이어 고유다 ----

    // 자기 CharacterController 캡슐과의 충돌을 끈다.
    //
    // 충돌 매트릭스로는 못 한다 — 캡슐이 지형과 같은 Default 레이어다(Player 프리팹 루트 m_Layer = 0).
    // 지형 충돌을 켜면 캡슐 충돌도 같이 켜지는데, 죽는 순간 래그돌은 자기 캡슐 <b>안에서</b> 출발하므로
    // 그대로 두면 깊게 겹친 상태로 시작하고, 그걸 밀어내는 힘에 몸이 발작처럼 튄다(실제로 밟았다).
    //
    // ⚠ <b>이 상태는 콜라이더를 껐다 켜면 초기화된다</b>(Unity 사양). 시체를 켤 때 한 번 걸어 두는
    // 것으로는 부족하다 — 우리 밖에서 CharacterController를 껐다 켜는 경로가 여럿이다:
    //  · <see cref="PlayerMovement"/>의 스폰 포즈 적용·텔레포트(SetPose) — <b>같은 프레임 안에서</b>
    //    껐다 켜므로 폴링으로는 전이를 볼 수도 없다
    //  · 호송·운반(PlayerTowedMotion, #279/#365) — 여러 프레임 동안 꺼 둔다
    // 그래서 세 곳에서 다시 건다: 시체를 켜는 순간, 뼈를 다시 물리로 놓아줄 때, 그리고 캡슐이 꺼졌다
    // 켜진 것이 관측될 때(Update).
    //
    // <b>모델을 분리해도 이 처리는 남는다.</b> 사망 중 캡슐은 꺼져 있지만(§10-0) 위 경로들이 그 사이에
    // 캡슐을 되살릴 수 있고, 그때 시체 뼈가 캡슐 안에 있으면 예전 증상이 그대로 재현된다.
    //
    // 남의 캡슐은 그대로 둔다 — 시체가 통행을 방해하는 것은 오히려 자연스럽고, 무엇보다 죽는 순간
    // 남의 캡슐이 내 몸 안에 겹쳐 있는 경우는 없다.
    private void IgnoreOwnCapsule()
    {
        // 시체가 숨어 있으면 뼈 콜라이더도 꺼져 있다 — Physics.IgnoreCollision은 비활성 콜라이더에
        // 에러를 뱉으므로 조건 없이 부르면 콘솔이 도배된다.
        if (!m_corpseVisible)
            return;

        m_rig.IgnoreCollisionWith(m_controller, true);
    }

    // 여러 프레임에 걸쳐 꺼져 있던 캡슐(호송·운반)이 다시 켜지는 순간을 잡아 무시를 다시 건다.
    // 같은 프레임 안에서 껐다 켜는 경로(PlayerMovement.SetPose)는 여기서 볼 수 없으므로,
    // 그쪽은 시체를 켜는 시점의 적용이 담당한다.
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
        ReleaseBonesToPhysics();
    }

    // ---- 밧줄 파사드 (#365 운반 / #398 드래그) ----
    //
    // 실물은 RagdollRope가 쥔다. 여기 파사드를 두는 이유는 호출부(PlayerTowedMotion)가 "래그돌인
    // 대상에게 밧줄을 묶는다"를 표현하기 때문이다 — 밧줄 컴포넌트를 직접 찾게 하면 "래그돌이 아닐 때는
    // 묶으면 안 된다"는 조건이 호출부로 새어 나간다.

    /// <summary>관절 밧줄의 길이(m) — 리그가 아직 안 잡혔으면 0. 묻는 쪽이 대신 쓸 값을 정한다. (#644)</summary>
    public float RopeLength => m_rope != null ? m_rope.Length : 0f;

    /// <summary>
    /// 밧줄을 시체에 묶는다 — <see cref="PlayerTowedMotion.BeginDraggedFollow"/>가 래그돌인 대상에게만 부른다.
    ///
    /// <b>골반이 스트림으로 오면 권위 피어만 묶는다.</b> 이 갈림이 견인 발산의 근원을 없앤다:
    /// 지금까지는 <c>BeginDraggedRpc</c>가 <c>SendTo.Everyone</c>이라 <b>전 피어가 각자 밧줄을
    /// 묶었고</b>, 같은 관절에 <b>서로 다른 입력</b>이 들어갔다 — 앵커가 운반자의 손 본이라 그 위치가
    /// 피어마다 다르게 계산되기 때문이다(원격이면 NetworkTransform 보간값 + 애니메이터가 얹는 걸음
    /// 흔들림, 그것도 피어마다 따로 평가된다). 강성 1500 스프링에 다른 입력을 넣으면 다른 궤적이
    /// 나오고, 그 차이를 보정이 쫓다가 미끄러짐으로 보였다.
    ///
    /// 골반을 직접 복제하면 <b>원격은 끌 이유가 없다</b> — 권위 피어가 굴린 결과가 그대로 온다.
    /// 시뮬레이션이 하나뿐이므로 갈릴 것이 애초에 없다.
    ///
    /// ⚠ <b>골반 복제가 없으면 예전대로 전원이 묶어야 한다.</b> 그때는 원격 시체에 끄는 힘이 아예
    /// 없어 물리를 켜 둬도 따라오지 않는다(<see cref="PlayerCarrier"/>의 RPC 주석). 그래서 조건이
    /// <see cref="m_hipsIsNetworkSynced"/>이고, 배선을 빼면 자동으로 옛 동작으로 돌아간다.
    /// </summary>
    /// <param name="carrier">운반자(밧줄을 쥔 쪽).</param>
    public void BeginRopePull(Transform carrier)
    {
        // 끌리기 시작은 몸이 다시 움직인다는 뜻이다 — 정착하며 끊은 스트림을 여기서 재개한다.
        // 안 재개하면 끌려가는 시체가 원격에서 마지막 정착 자세로 굳는다.
        // (NPC는 같은 자리에서 녹이기까지 하지만 플레이어는 얼지 않아 녹일 것이 없다)
        m_streamer?.BeginStreaming();
        BeginRopeTrace();

        if (!HasMoveAuthority)
            return;

        m_rope?.Attach(carrier);
    }

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
        if (m_rig == null || !m_rig.IsValid)
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

        // <b>사망 중에는 캡슐을 끈다</b> (§10-0). 대리값이 물리 오브젝트일 이유가 없고, 켜 두면
        // ① 뼈와 서로 충돌해 §9-1의 재적용 싸움이 필요하고 ② 스윕이 지형에 막혀 추종이 실패한다.
        // 호송(#279)이 같은 패턴이다 — SetControllerEnabled(false) 후 트랜스폼 직접 대입.
        //
        // <b>시체를 켜기 전에 끈다.</b> 순서를 뒤집으면 뼈가 캡슐 안에서 겹친 채 한 프레임을 보내고
        // 그 탈출 임펄스에 몸이 튄다.
        SetControllerEnabled(false);

        ShowCorpse();
        m_rig.ApplyImpulse(impulse);

        // 자세를 흘려보내기 시작한다 — 권위가 아니면 스스로 무동작이다(그쪽 주석).
        m_streamer?.BeginStreaming();
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
        DumpFallRate("이탈"); // 창이 닫히기 전에 부활했다 — 남은 값으로라도 마감한다
        if (m_state == RagdollState.Animated || m_rig == null || !m_rig.IsValid)
            return;

        // 에피소드가 여기서 끝난다 — 다음 사망은 자기 사망을 다시 관측해야 부활할 수 있다 (PollDeath).
        m_sawDeathThisEpisode = false;
        m_awaitingDeathSeconds = 0f;

        // 스트림을 <b>아무것도 보내지 않고</b> 끓는다 — 기상에는 종착 자세가 없다.
        // <b>전 피어가 각자 부른다</b>: 부활 판정은 동기화된 값을 읽는 폴링이라 원격도 같은
        // 프레임에 여기 온다 — 그래서 재생 정지에 RPC가 필요 없다. 안 끓으면 스트림이
        // 기상 블렌드를 매 프레임 덮어쓴다.
        m_streamer?.StopStreaming();
        m_holdPoseUntilStream = false;

        if (m_state == RagdollState.Ragdoll)
            Settle(); // 날아가는 중이면 먼저 포즈를 확정한다

        // 정착 상태에서는 뼈가 전부 물리에 있다(<see cref="RestToPhysics"/>) — 애니메이터로 돌아가려면
        // 전부 멈춰야 한다. 안 멈추면 블렌드가 놓는 포즈를 물리가 매 스텝 덮는다.
        // 밧줄을 먼저 끊는다: 키네마틱 바디에는 관절이 안 먹지만, 순서를 뒤집으면 한 스텝 동안
        // 스프링이 블렌드 시작 포즈를 당긴다.
        EndRopePull();

        // 시체가 실제로 누운 방향 — 얼리고 옮기기 <b>전에</b> 잰다. 아래 yaw 진단이 쓴다.
        bool haveCorpseYaw = m_rig.TryGetBodyYaw(out float corpseYaw);

        m_rig.SetKinematic(true);

        // 시체 → 살아있는 몸. <b>캡슐을 켜기 전에</b> 해야 한다 — 그 순간 뼈 콜라이더가 이미 사라져
        // 있으므로 캡슐과 겹칠 일이 없다.
        HideCorpse();

        // 캡슐을 되살린다 (§10-0 — 사망 중 꺼 뒀다). 이 시점의 루트는 정착 정렬이 지면 위에
        // 놓아 둔 자리이므로(ResolveSettledRootPose가 CapsuleBottomOffset까지 보정) 그대로 켜면 된다.
        SetControllerEnabled(true);
        m_movement?.ClearExternalVelocity(); // 꺼져 있던 동안 쌓인 값이 도착지에서 바닥을 파고들지 않게 (#189)

        // <b>애니메이터를 켜는 줄이 없다</b> — 모델을 분리한 뒤로는 애초에 끄지 않는다(ShowCorpse).
        if (!blend
            || m_animator == null
            || m_blend == null
            || !m_blend.IsValid
            || m_state == RagdollState.BlendingToAnimator)
        {
            m_state = RagdollState.Animated;
            return;
        }

        // 시체에서 넘겨받은 정착 포즈를 출발점으로 잡아 둔다 — 아래 LateUpdate가 애니메이터 포즈로
        // 끌고 간다. HideCorpse가 그 포즈를 살아있는 리그에 이미 입혀 놓았다.
        m_blend.Begin();

        // Down은 아직 참이다(AnimationDriver가 IsRagdollActive를 보고 붙들고 있다) — 바닥 대기 자세로
        // 물려 들어가야 정착 포즈와의 거리가 가장 짧다. 여기서 Ground를 직접 찍는 이유다.
        // 애니메이터가 실제로 뼈를 썼는지 보려면 쓰기 전 값을 들고 있어야 한다 — HideCorpse가 방금
        // 시체 포즈를 입혔으니, Update(0f) 뒤에도 이 값이면 애니메이터가 아무것도 안 쓴 것이다.
        bool haveLiveBefore = TryLiveBodyYaw(out float liveBefore);

        m_animator.Play(s_groundStateHash, 0, 0f);
        m_animator.Update(0f); // 이번 프레임 LateUpdate에서 바로 섞으려면 포즈가 이미 평가돼 있어야 한다

        // 여기가 재는 자리다 — 클립이 평가된 직후이고 블렌드가 아직 섞기 전이다.
        if (m_logRevivalYaw)
            LogRevivalYaw(haveCorpseYaw, corpseYaw, haveLiveBefore, liveBefore);

        m_state = RagdollState.BlendingToAnimator;
    }

    /// <summary>
    /// 씬 진입 재배치를 위해 즉시 일으킨다 — <see cref="PlayerMovement"/>의 재배치 경로 전용. (#656)
    ///
    /// <see cref="ExitToAnimator"/>만으로는 부족하다. 재배치는 서버가 RPC로 지시하는데 <b>사망 해제
    /// (HP)는 NetworkVariable이라 도착 순서가 갈릴 수 있다</b> — RPC가 먼저 오면 몸을 일으킨 직후
    /// 프레임에 <see cref="PollDeath"/>가 아직 dead를 보고 <see cref="EnterRagdoll"/>로 다시 눕힌다.
    /// 스폰 지점에서 한 번 더 무너지는 그림이다.
    ///
    /// 그래서 <see cref="m_skipThisEpisode"/>를 함께 세운다 — "이번 사망은 래그돌을 건너뛴다"가
    /// 정확히 이 플래그의 뜻이고, 사망 해제가 도착하는 순간 PollDeath가 스스로 내린다.
    /// </summary>
    public void ExitForReposition()
    {
        ExitToAnimator(blend: false);
        m_skipThisEpisode = true;
    }

    // ---- 지면 파고듦 진단 (Hips 동기화 검증) ----

    private float m_sinkLogTimer;



    // ---- yaw 진단 (m_rootYawOffset 캘리브레이션) ----

    /// <summary>
    /// 부활 순간의 yaw를 실측해 남긴다 — <b>눈대중으로 오프셋을 맞추지 않기 위한 계측이다.</b>
    ///
    /// 재는 것은 두 방향의 차이다:
    /// <list type="bullet">
    ///   <item><b>시체 방향</b> — 물리가 실제로 눕힌 방향(골반→머리)</item>
    ///   <item><b>클립 방향</b> — <c>Knockdown_Ground</c>가 눕히는 방향. 애니메이터가 방금 평가한
    ///   살아있는 리그에서 같은 계산으로 잰다</item>
    /// </list>
    ///
    /// 클립은 루트 로컬로 작성돼 있으므로 <c>클립 − 루트</c>는 클립의 상수다. 두 방향을 맞추려면
    /// <c>루트 = 시체 − 상수</c>여야 하고, 지금 루트는 <c>시체 + 오프셋</c>이므로
    /// <b>필요한 오프셋 = 루트 − 클립</b>이다. 현재 오프셋 값과 무관하게 나오는 절대값이라 그대로 넣으면 된다.
    /// </summary>
    private void LogRevivalYaw(
        bool haveCorpseYaw,
        float corpseYaw,
        bool haveLiveBefore,
        float liveBefore
    )
    {
        float rootYaw = m_root.eulerAngles.y;

        if (!haveCorpseYaw)
        {
            Debug.Log(
                $"[래그돌 부활 yaw] 시체가 거의 수직이라 누운 방향을 못 쟀다 — {name} "
                    + $"(루트 {rootYaw:F1}°). 다시 눕혀서 죽여 볼 것",
                this
            );
            return;
        }

        if (!TryLiveBodyYaw(out float clipYaw))
        {
            Debug.Log(
                $"[래그돌 부활 yaw] 살아있는 리그에서 Hips/Head를 못 찾아 클립 방향을 못 쟀다 — {name}",
                this
            );
            return;
        }

        // 애니메이터가 정말 뼈를 썼는가 — 안 썼으면 '클립'은 클립 방향이 아니라 방금 입힌 시체
        // 방향이고, 그 위에서 계산한 오프셋은 전부 무의미하다.
        bool animatorWrote =
            !haveLiveBefore || Mathf.Abs(Mathf.DeltaAngle(liveBefore, clipYaw)) > 0.05f;

        float needed = Mathf.DeltaAngle(clipYaw, rootYaw);
        Debug.Log(
            $"[래그돌 부활 yaw] 시체 {corpseYaw:F1}° / 클립 {clipYaw:F1}° / 루트 {rootYaw:F1}° "
                + $"→ m_rootYawOffset = {needed:F1}° (현재 {m_rootYawOffset:F1}°, "
                + $"어긋남 {Mathf.DeltaAngle(clipYaw, corpseYaw):F1}°) "
                + $"| 권한={HasMoveAuthority} 애니메이터기록={animatorWrote} "
                + $"컬링={m_animator.cullingMode} 상태={m_state}",
            this
        );
    }

    // 살아있는 리그가 누운 방향 — RagdollRig.TryGetBodyYaw와 같은 계산이다. 저쪽은 관절 없는 뼈로
    // 골반을 찾지만 살아있는 리그에는 리지드바디가 없으므로(모델 분리) 이름으로 찾는다.
    private bool TryLiveBodyYaw(out float yaw)
    {
        yaw = 0f;
        if (m_liveBoneRoot == null)
            return false;

        Transform hips = FindLiveBone("Hips");
        Transform head = FindLiveBone("Head");
        if (hips == null || head == null)
            return false;

        Vector3 lengthwise = head.position - hips.position;
        lengthwise.y = 0f;
        if (lengthwise.sqrMagnitude < 0.0004f)
            return false;

        yaw = Quaternion.LookRotation(lengthwise.normalized).eulerAngles.y;
        return true;
    }

    // ---- 매 프레임 ----

    private void Update()
    {
        RefreshCapsuleIgnoreOnReenable();

        // 진입 추적의 기준선을 담는다 — <b>Update 시작</b>이 직전 프레임의 최종 자세를 읽는 유일하게
        // 안전한 지점이다. LateUpdate 끝에 담으려면 PlayerHeadLook보다 뒤에 돌아야 하는데 스크립트
        // 실행 순서는 정해져 있지 않고, Animator는 아직 이번 프레임을 평가하지 않았다.
        SampleEntryBaseline();

        PollDeath();

        if (m_state != RagdollState.Ragdoll)
            return;

        // ⚠ <b>정착 판정은 권위만 돌린다.</b> 이 게이트가 없으면 전 뼈 스트리밍에서
        // <b>즉시 버그가 된다</b>: 원격의 뼈는 키네마틱이라 <c>AverageSpeed</c>가 항상 0이고,
        // 그러면 아래 정지 타이머가 바로 차서 <b>무너지지도 전에</b> 0.3초 뒤 정착해 버린다.
        //
        // 원격의 종착 상태는 권위가 보내는 정착 자세가 준다(<see cref="HandleSettledPoseReceived"/>).
        //
        // <b>NPC에는 이 게이트가 이미 있었다</b> — 저쪽은 정착을 서버만 판정해서다.
        // 여기는 <c>MonoBehaviour</c>라 전 피어에서 돌고, 그게 플레이어 고유의 함정이다.
        if (!HasMoveAuthority)
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
    /// ⚠ <b>②(원격의 당겨오기 완료 대기)가 사라졌다.</b> 그 가드는 원격이 <b>자기 물리로 정착을
    /// 판정하던</b> 시절의 것이다 — 매 프레임 루트 쪽으로 당겨 오는 도중에 정착하면 남은 델타가
    /// 그대로 굳어서, 다 당겨졌는지 물어봐야 했다. 지금은 <b>정착 판정 자체를 권위만 돈다</b>
    /// (<see cref="Update"/>의 게이트). 원격은 정착 자세를 받아 상태만 넘기므로 물을 것이 없다.
    /// </summary>
    private bool IsReadyToSettle() => HasGroundUnderHips();

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
        //
        // <b>섞는 대상은 살아있는 리그다</b> (#571) — 시체는 HideCorpse가 이미 껐다. 그래서 스킨 컬링을
        // 되돌리는 줄도 여기 없다(그쪽도 HideCorpse가 한다).
        if (m_state == RagdollState.BlendingToAnimator && m_blend.Tick(m_blendSeconds))
            m_state = RagdollState.Animated;

        // 스트림이 아직 몸을 쥐기 전이면 진입 시점의 자세를 붙든다 — <b>루트에 끌려가지 않게.</b>
        TickHoldPoseUntilStream();

        // 붙들기까지 끝난 뒤에 담는다 — 여기가 렌더 직전이라 화면에 보이는 값과 같다.
        TickSettleTrace();

        // ⚠ <b>LateUpdate여야 한다.</b> PlayerHeadLook이 시선을 얻는 시점이 여기라,
        // Update에서 재면 이 계측이 물으려는 시점 차를 지나치게 된다.
        TickEntryTrace();

        // 물리가 실시간을 따라갔는지 적립한다 — 프레임 시간을 재는 계측이라 렌더 주기에 붙인다.
        TickFallRate();
        TickRopeTrace();
    }

    /// <summary>
    /// 원격에서 <b>첫 자세 패킷이 오기 전</b> 구간을 메운다 — 진입 시점의 월드 자세를 그대로 붙든다.
    ///
    /// <b>왜 빈 구간이 생기나.</b> 래그돌 진입은 동기화된 값을 읽는 폴링이라 전 피어가 거의 같은
    /// 프레임에 들어가지만, 자세는 <b>왕복 지연 + 보간 지연</b>만큼 늦게 온다. 그 사이 원격의 뼈는
    /// 이미 키네마틱인데 루트는 이미 움직이고 있다 — <b>키네마틱 뼈는 계층을 따라가므로 몸이
    /// 통째로 딸려 간다.</b>
    ///
    /// <b>플레이어에서 그 움직임을 만드는 것은 <see cref="TickCapsuleFollow"/>다</b> — 캡슐을 골반
    /// 위치로 3차원 추종시키므로 진입 프레임에 발밑에서 골반까지, 서 있는 몸 기준 약 0.9m를 뛴다.
    /// (NPC는 <c>TickRootFollow</c>가 같은 점프를 만든다 — 이름만 다르고 증상이 같다)
    ///
    /// ⚠ <b><see cref="RagdollPoseStreamer.IsStreamDriven"/>의 반대로 묻지 않는다.</b> 그것이 거짓인
    /// 경우가 둘인데 뜻이 정반대다 — <b>아직 안 왔다</b>(메워야 한다)와 <b>정착까지 다 받고
    /// 끝났다</b>(확정이라 건드리면 안 된다). NPC에서 하나로 물었다가 <b>다 쓰러진 시체가 마지막에
    /// 벌떡 선 자세로 바뀌었다.</b>
    ///
    /// <b>렌더 전용이다</b> — 이 프로젝트는 <c>m_AutoSyncTransforms = 0</c>이라 여기 쓴 값이 PhysX로
    /// 넘어가지 않는다. 원격의 뼈는 물리에 참여하지 않으므로 잃는 것도 없다.
    /// </summary>
    private void TickHoldPoseUntilStream()
    {
        if (!m_holdPoseUntilStream || m_streamer == null || HasMoveAuthority)
            return;

        if (!m_streamer.IsAwaitingFirstPose)
        {
            m_holdPoseUntilStream = false;
            return;
        }

        m_rig.RestoreCapturedPose();
    }

    // ---- 진입 자세 추적 (m_logEntryHeadTrace) — ⚠ 임시 계측, 원인이 잡히면 지운다 ----

    [Tooltip("래그돌 진입 직후 6프레임을 <b>루트 한 줄 + 뼈 한 줄</b>로 찍는다 — 쓰러지는 순간 몸이 " +
             "죽기 직전 자세에서 <b>뜨고 돌아 버리는</b> 현상을 잡는 계측이다.\n\n" +
             "⚠ <b>권한=True로 찍어야 한다.</b> 원격의 뼈는 키네마틱이고 첫 자세 패킷이 오기 전까지 " +
             "TickHoldPoseUntilStream이 진입 자세에 못박아 두므로, 원격에서는 시체가 아예 " +
             "움직이지 않는다(실측: 단차가 6프레임 전부 0.0°).\n\n" +
             "<b>기준선은 죽기 직전 프레임에 화면에 나온 살아있는 몸</b>이다 — 매 프레임 Update " +
             "시작에 담고 진입 순간의 값을 얼린다. 뜸·yawΔ는 전부 그것과의 차이다.\n\n" +
             "읽는 법 — f=0이 시체를 켠 프레임이다:\n" +
             "· <b>루트뜸이 f=+1에 0.9m 근처로 뛰면</b> TickCapsuleFollow가 캡슐 밑면에서 골반으로 " +
             "루트를 올린 그 점프다(그 함수 주석의 실측과 같은 값)\n" +
             "· <b>루트yawΔ가 크면</b> FollowBodyYaw가 루트를 돌린 것이다. 같은 줄의 <b>수평</b>을 " +
             "함께 볼 것 — 서 있는 몸의 골반→머리는 거의 수직이라 수평 성분이 몇 cm뿐이고, 그 " +
             "방향은 몸이 향한 쪽이 아니라 <b>미세한 기울어짐</b>이다. 수평이 작은데 유효=True면 " +
             "RagdollRig.TryGetBodyYaw의 거절 가드(2cm)가 노이즈를 통과시킨 것이다\n" +
             "· <b>뼈 줄에서 물리 뼈와 아닌 뼈가 갈리면</b> 그것이 비틀림의 정체다. 루트가 움직일 때 " +
             "RestoreCapturedPose는 물리 뼈만 되돌리고, 리지드바디가 없는 뼈는 계층을 따라간다 " +
             "(Hips·Head는 물리 뼈, Spine_01·Neck은 아니다)\n" +
             "· <b>골반간격</b>은 두 리그가 어긋난 거리다 — 0이면 복사는 결백하다\n" +
             "· <b>팝</b>은 기준선 대비 머리 회전차, <b>단차</b>는 직전 프레임 대비 시체 머리 " +
             "회전차다. 첫 스텝의 단차만 유독 크면 관절 위반을 물리가 되잡은 것이다\n\n" +
             "<b>지면 줄은 기준이 다르다.</b> 위 두 줄은 죽기 직전 자세를 기준으로 한 상대값이라 " +
             "몸 전체가 바닥에서 떠 있어도 0으로 보인다 — 그것을 보려면 이 줄을 읽는다:\n" +
             "· <b>최저뼈-지면이 계속 양수로 남으면</b> 시체가 공중에 떠 있는 것이다\n" +
             "· <b>루트-지면만 크고 최저뼈-지면은 0 근처면</b> 몸은 바닥에 있고 루트만 떠 있는 " +
             "것이다(캡슐 추종의 설계상 점프). 그때 뜨는 것은 몸이 아니라 <b>루트에 매달린 것들</b>" +
             "이다 — 이름표·상호작용 표시\n" +
             "· <b>찾음=False면</b> 골반 밑에서 지면을 못 찾은 것이다. 그 프레임의 지면 기준값은 " +
             "전부 의미가 없고, 정착 판정도 같은 탐색을 쓰므로 그쪽도 함께 막혀 있다\n\n" +
             "확정되면 끈다")]
    [SerializeField] private bool m_logEntryHeadTrace;

    // 쓰러져 바닥에 닿기까지를 담아야 "언제 뜨나"를 볼 수 있다 — 6프레임으로는 몸이 아직 서 있다.
    private const int k_entryTraceFrames = 30;

    // 볼 뼈 — <b>물리 뼈와 아닌 뼈를 섞어</b> 담는다. 루트가 움직일 때 리지드바디가 없는 뼈만
    // 계층을 따라가면 그 차이가 곧 비틀림이고, 섞어 두지 않으면 그것을 못 본다.
    private static readonly string[] s_entryTraceBones = { "Hips", "Spine_01", "Neck", "Head" };

    private const int k_entryTraceHeadIndex = 3; // 팝·단차를 재는 뼈 = s_entryTraceBones의 "Head"

    private PlayerHeadLook m_headLook; // 죽기 직전에 얹혀 있던 시선 기울기를 묻는다

    private Transform[] m_liveTraceBones;
    private Transform[] m_corpseTraceBones;

    private int m_entryTraceLeft;
    private int m_entryFrame;

    // 직전 프레임의 최종 자세 — 매 프레임 갱신하고, 진입 순간의 값을 기준선으로 얼린다.
    private float[] m_lastBoneY;
    private float[] m_lastBoneYaw;
    private Quaternion m_lastHeadRotation;
    private float m_lastRootY;
    private float m_lastRootYaw;
    private bool m_haveLast;

    // 진입 기준선 — 죽기 직전 프레임에 화면에 나온 몸. 뜸·yawΔ·팝은 전부 이것과의 차이다.
    private float[] m_baselineBoneY;
    private float[] m_baselineBoneYaw;
    private Quaternion m_baselineHeadRotation;
    private float m_baselineRootY;
    private float m_baselineRootYaw;
    private bool m_haveBaseline;

    private Quaternion m_prevCorpseHead;
    private bool m_havePrevCorpseHead;

    // 이름으로 찾는다 — 리그가 두 벌이라 각자의 서브트리에서 따로 집어야 한다.
    private static Transform FindBone(Transform root, string name)
    {
        if (root == null)
            return null;

        if (root.name == name)
            return root;

        for (int i = 0; i < root.childCount; i++)
        {
            Transform found = FindBone(root.GetChild(i), name);
            if (found != null)
                return found;
        }

        return null;
    }

    private void ResolveTraceBones()
    {
        if (m_liveTraceBones != null)
            return;

        m_liveTraceBones = new Transform[s_entryTraceBones.Length];
        m_corpseTraceBones = new Transform[s_entryTraceBones.Length];
        m_lastBoneY = new float[s_entryTraceBones.Length];
        m_lastBoneYaw = new float[s_entryTraceBones.Length];
        m_baselineBoneY = new float[s_entryTraceBones.Length];
        m_baselineBoneYaw = new float[s_entryTraceBones.Length];

        for (int i = 0; i < s_entryTraceBones.Length; i++)
        {
            m_liveTraceBones[i] = FindBone(m_liveBoneRoot, s_entryTraceBones[i]);
            m_corpseTraceBones[i] = FindBone(m_rig.BoneRoot, s_entryTraceBones[i]);
        }
    }

    // 직전 프레임의 자세를 담아 둔다 — <see cref="Update"/> 시작에서만 정직한 값이다. LateUpdate
    // 끝에 담으려면 PlayerHeadLook보다 뒤에 돌아야 하는데 스크립트 실행 순서는 정해져 있지 않고,
    // Animator는 아직 이번 프레임을 평가하지 않았다.
    private void SampleEntryBaseline()
    {
        if (!m_logEntryHeadTrace)
            return;

        ResolveTraceBones();

        for (int i = 0; i < m_liveTraceBones.Length; i++)
        {
            Transform bone = m_liveTraceBones[i];
            if (bone == null)
                continue;

            m_lastBoneY[i] = bone.position.y;
            m_lastBoneYaw[i] = bone.eulerAngles.y;
        }

        Transform head = m_liveTraceBones[k_entryTraceHeadIndex];
        if (head != null)
            m_lastHeadRotation = head.rotation;

        m_lastRootY = m_root.position.y;
        m_lastRootYaw = m_root.eulerAngles.y;
        m_haveLast = true;
    }

    // 시체를 켜는 프레임에 연다 — 복사 <b>직후</b>의 값이 첫 줄이 되어야 복사가 옮긴 것과
    // 그 뒤 프레임이 바꿔 놓은 것이 갈린다.
    private void BeginEntryTrace()
    {
        if (!m_logEntryHeadTrace)
            return;

        ResolveTraceBones();
        m_headLook ??= GetComponentInParent<PlayerHeadLook>();

        m_entryFrame = Time.frameCount;
        m_entryTraceLeft = k_entryTraceFrames;
        m_havePrevCorpseHead = false;

        // ⚠ 기준선은 <b>직전 프레임</b>의 값이다 — 지금 살아있는 몸을 읽으면 안 된다. CopyPose가
        // 방금 그 자세를 시체에 넘겼으므로, 지금 값으로 재면 차이가 정의상 0이 되어 버린다.
        m_haveBaseline = m_haveLast;
        System.Array.Copy(m_lastBoneY, m_baselineBoneY, m_lastBoneY.Length);
        System.Array.Copy(m_lastBoneYaw, m_baselineBoneYaw, m_lastBoneYaw.Length);
        m_baselineHeadRotation = m_lastHeadRotation;
        m_baselineRootY = m_lastRootY;
        m_baselineRootYaw = m_lastRootYaw;

        float tilt = m_headLook != null ? m_headLook.LastAppliedTilt : float.NaN;

        Debug.Log(
            $"[진입추적] ===== 시체 켬 (권한={HasMoveAuthority} 프레임={m_entryFrame}) — 시선={tilt:F1}° "
                + $"기준선={m_haveBaseline} 기준루트Y={m_baselineRootY:F3} "
                + $"기준루트yaw={m_baselineRootYaw:F1}° / 복사직후 ↓ =====",
            this
        );

        LogEntrySample();
    }

    private void TickEntryTrace()
    {
        if (m_entryTraceLeft <= 0)
            return;

        m_entryTraceLeft--;
        LogEntrySample();
    }

    // 루트 한 줄 + 뼈 한 줄이다 — 한 줄에 다 넣으면 MPPM 로그에서 잘린다.
    private void LogEntrySample()
    {
        if (m_corpseTraceBones == null)
            return;

        string frame = (Time.frameCount - m_entryFrame).ToString("+0;-0;0");

        // ---- 루트 ----
        float rootLift = m_haveBaseline ? m_root.position.y - m_baselineRootY : float.NaN;
        float rootYawDelta = m_haveBaseline
            ? Mathf.DeltaAngle(m_baselineRootYaw, m_root.eulerAngles.y)
            : float.NaN;

        // TryGetBodyYaw가 무엇을 보고 판단했는지 같이 남긴다 — 서 있는 몸의 수평 성분은 몇 cm뿐이고,
        // 그 크기가 곧 그 함수의 거절 가드가 옳게 걸렸는지의 근거다.
        float bodyYaw = float.NaN;
        bool haveBodyYaw = false;
        if (m_rig != null && m_rig.TryGetBodyYaw(out float measuredYaw))
        {
            haveBodyYaw = true;
            bodyYaw = measuredYaw;
        }

        Transform corpseHips = m_corpseTraceBones[0];
        Transform corpseHead = m_corpseTraceBones[k_entryTraceHeadIndex];
        float horizontalCm = float.NaN;
        if (corpseHips != null && corpseHead != null)
        {
            Vector3 lengthwise = corpseHead.position - corpseHips.position;
            lengthwise.y = 0f;
            horizontalCm = lengthwise.magnitude * 100f;
        }

        Debug.Log(
            $"[진입루트] 권한={HasMoveAuthority} f={frame} 루트뜸={rootLift:+0.000;-0.000;0.000}m "
                + $"루트yawΔ={rootYawDelta:+0.0;-0.0;0.0}° 몸yaw={bodyYaw:F1}° 수평={horizontalCm:F1}cm "
                + $"유효={haveBodyYaw} 상태={m_state}",
            this
        );

        // ---- 뼈 ----
        var bones = new System.Text.StringBuilder();
        for (int i = 0; i < m_corpseTraceBones.Length; i++)
        {
            Transform bone = m_corpseTraceBones[i];
            if (bone == null)
                continue;

            float lift = m_haveBaseline ? bone.position.y - m_baselineBoneY[i] : float.NaN;
            float yawDelta = m_haveBaseline
                ? Mathf.DeltaAngle(m_baselineBoneYaw[i], bone.eulerAngles.y)
                : float.NaN;

            bones.Append(
                $"{s_entryTraceBones[i]}={lift:+0.000;-0.000;0.000}m/{yawDelta:+0.0;-0.0;0.0}° "
            );
        }

        // 두 리그가 어긋난 거리 — 0이면 복사는 결백하다.
        Transform liveHips = m_liveTraceBones[0];
        float rigSpan =
            liveHips != null && corpseHips != null
                ? (liveHips.position - corpseHips.position).magnitude
                : float.NaN;

        float pop =
            m_haveBaseline && corpseHead != null
                ? Quaternion.Angle(m_baselineHeadRotation, corpseHead.rotation)
                : float.NaN;

        float step = 0f;
        if (corpseHead != null)
        {
            step = m_havePrevCorpseHead
                ? Quaternion.Angle(m_prevCorpseHead, corpseHead.rotation)
                : 0f;
            m_prevCorpseHead = corpseHead.rotation;
            m_havePrevCorpseHead = true;
        }

        Debug.Log(
            $"[진입뼈] 권한={HasMoveAuthority} f={frame} {bones}골반간격={rigSpan:F3}m "
                + $"팝={pop:F1}° 단차={step:F1}°",
            this
        );

        // ---- 지면 ----
        //
        // <b>"떠 있다"를 직접 재는 줄이다.</b> 위 두 줄은 전부 죽기 직전 자세를 기준으로 한 상대값이라,
        // 몸 전체가 바닥에서 떨어져 있어도 0으로 보인다. 기준을 <b>지면</b>으로 바꿔야 그것이 보인다.
        //
        // 지면 탐색은 정착 판정·정착 정렬과 같은 것을 쓴다(<see cref="TryGroundUnder"/>) — 다른 것을
        // 쓰면 "여기가 바닥이다"의 정의가 갈려서 이 계측이 그 둘을 검증하지 못한다.
        float groundY = float.NaN;
        bool haveGround = false;
        if (corpseHips != null && TryGroundUnder(corpseHips.position, out Vector3 groundPoint))
        {
            haveGround = true;
            groundY = groundPoint.y;
        }

        float lowestAbove = haveGround && m_rig != null ? m_rig.LowestBoneY - groundY : float.NaN;
        float hipsAbove = haveGround && corpseHips != null
            ? corpseHips.position.y - groundY
            : float.NaN;
        float rootAbove = haveGround ? m_root.position.y - groundY : float.NaN;

        Debug.Log(
            $"[진입지면] 권한={HasMoveAuthority} f={frame} 지면Y={groundY:F3} 찾음={haveGround} "
                + $"최저뼈-지면={lowestAbove:+0.000;-0.000;0.000}m "
                + $"골반-지면={hipsAbove:F3}m 루트-지면={rootAbove:+0.000;-0.000;0.000}m",
            this
        );
    }

    // ---- 밧줄 견인 계측 (m_logRopePull) — ⚠ 임시 계측, #759가 닫히면 지운다 ----

    [Tooltip("밧줄로 끌기 시작한 순간과 그 1.5초 뒤를 두 줄로 찍는다 — <b>끌리는 시체가 원격에서 " +
             "제자리에 남는</b> 증상의 판정용이다.\n\n" +
             "<b>가르는 것은 골반이동 대 루트이동이다.</b> 루트만 움직이고 골반이 0에 가까우면 " +
             "<b>권위 피어에서 몸 자체가 안 끌린 것</b>이다 — 원격은 마지막 자세를 월드로 붙들고 " +
             "있을 뿐이라 전송은 결백하다. 골반이 제대로 움직였는데도 원격이 제자리면 그때가 " +
             "전송 문제다.\n\n" +
             "함께 찍는 <b>키네마틱·잠듦</b>이 원인을 바로 지목한다 — 키네마틱 뼈에는 밧줄 관절이 " +
             "힘을 만들지 못하고, 잠든 뼈는 장력을 받지 못한다(RagdollRope.Tick).\n\n" +
             "확정되면 끈다")]
    [SerializeField] private bool m_logRopePull;

    private const float k_ropeTraceSeconds = 1.5f;

    private bool m_ropeTraceActive;
    private float m_ropeTraceStart;
    private static readonly float[] k_ropeMarks = { 0.5f, 1f, 1.5f };

    private readonly float[] m_ropeMoved = new float[3];
    private int m_ropeMarkIndex;
    private float m_ropePeakSpeed;
    private Vector3 m_ropePrevHips;

    private Vector3 m_ropeTraceHipsStart;
    private Vector3 m_ropeTraceRootStart;

    // 끌기 시작 — 이 순간의 물리 상태가 곧 "왜 안 끌리는가"의 답이다.
    private void BeginRopeTrace()
    {
        m_ropeTraceActive = m_logRopePull;
        if (!m_ropeTraceActive)
            return;

        m_ropeTraceStart = Time.unscaledTime;
        m_ropeTraceHipsStart = m_rig.Hips != null ? m_rig.Hips.position : Vector3.zero;
        m_ropeTraceRootStart = m_root.position;
        m_ropePrevHips = m_ropeTraceHipsStart;
        m_ropeMarkIndex = 0;
        m_ropePeakSpeed = 0f;
        for (int i = 0; i < m_ropeMoved.Length; i++)
            m_ropeMoved[i] = float.NaN;

        Rigidbody hips = m_rig.HipsBody;
        Debug.Log(
            $"[밧줄] {name} 권한={HasMoveAuthority} 상태={m_state} 밧줄길이={RopeLength:F2}m "
                + $"골반키네마틱={(hips != null ? hips.isKinematic.ToString() : "없음")} "
                + $"잠듦={(hips != null ? hips.IsSleeping().ToString() : "없음")} "
                + $"평균속도={m_rig.AverageSpeed:F2}m/s",
            this
        );
    }

    private void TickRopeTrace()
    {
        if (!m_ropeTraceActive)
            return;

        // 끌려온 거리의 <b>모양</b>과 최고 속도 — 총량만 보면 "느리게 끌렸다"를 놓친다.
        Vector3 hipsNow = m_rig.Hips != null ? m_rig.Hips.position : m_ropePrevHips;
        if (Time.unscaledDeltaTime > 0.0001f)
        {
            m_ropePeakSpeed = Mathf.Max(
                m_ropePeakSpeed,
                Vector3.Distance(hipsNow, m_ropePrevHips) / Time.unscaledDeltaTime
            );
        }

        m_ropePrevHips = hipsNow;

        float ropeElapsed = Time.unscaledTime - m_ropeTraceStart;
        while (m_ropeMarkIndex < k_ropeMarks.Length && ropeElapsed >= k_ropeMarks[m_ropeMarkIndex])
        {
            m_ropeMoved[m_ropeMarkIndex] = Vector3.Distance(hipsNow, m_ropeTraceHipsStart);
            m_ropeMarkIndex++;
        }

        if (ropeElapsed < k_ropeTraceSeconds)
            return;

        m_ropeTraceActive = false;

        Rigidbody hips = m_rig.HipsBody;
        float hipsMoved = m_rig.Hips != null
            ? Vector3.Distance(m_rig.Hips.position, m_ropeTraceHipsStart)
            : float.NaN;

        Debug.Log(
            $"[밧줄추적] {name} 권한={HasMoveAuthority} t={k_ropeTraceSeconds:F1}s "
                + $"골반이동={hipsMoved:F2}m "
                + $"루트이동={Vector3.Distance(m_root.position, m_ropeTraceRootStart):F2}m "
                + $"평균속도={m_rig.AverageSpeed:F2}m/s "
                + $"골반키네마틱={(hips != null ? hips.isKinematic.ToString() : "없음")} "
                + $"잠듦={(hips != null ? hips.IsSleeping().ToString() : "없음")} "
                + $"최고속도={m_ropePeakSpeed:F2}m/s 견인곡선=[{DescribeRopeCurve()}]",
            this
        );
    }

    private string DescribeRopeCurve()
    {
        System.Text.StringBuilder line = new System.Text.StringBuilder();
        for (int i = 0; i < m_ropeMarkIndex && i < k_ropeMarks.Length; i++)
        {
            if (line.Length > 0)
                line.Append(' ');
            line.Append($"{k_ropeMarks[i]:0.##}s:{m_ropeMoved[i]:F2}m");
        }

        return line.Length > 0 ? line.ToString() : "표본없음";
    }

    // ---- 낙하 속도 계측 (m_logFallRate) — ⚠ 임시 계측, #759가 닫히면 지운다 ----

    [Tooltip("쓰러지는 동안 <b>물리가 실시간을 따라갔는가</b>를 한 줄로 찍는다 — #759 ③(권위 피어의 " +
             "시뮬레이션이 느리다) 판정용이다.\n\n" +
             "<b>비율 = 물리시간 ÷ 실시간.</b> 1.00이 정상이고 <b>0.60이면 40% 느리게 쓰러진 것</b>이다 " +
             "— 슬로모션의 정의가 이 값이다. 물리시간은 Time.fixedTime으로 잰다(실행된 고정 스텝만 " +
             "누적되는 시계라 스텝을 따로 셀 필요가 없다).\n\n" +
             "<b>최장프레임이 333ms를 넘으면</b> Maximum Allowed Timestep 클램프에 걸린 것이라 원인이 " +
             "<b>프레임 히칭으로 확정</b>된다. 비율은 낮은데 최장프레임이 그보다 작으면 클램프가 " +
             "아니므로 겹침 해소(RagdollRig의 최대 침투 해소 속도)를 다음으로 의심한다.\n\n" +
             "<b>권한 피어에서 읽는다</b> — 원격은 뼈가 키네마틱이라 잴 물리가 없다. 그래도 함께 " +
             "찍는 이유는 두 피어의 프레임 사정을 같은 화면에서 비교하기 위해서다.\n\n" +
             "확정되면 끈다")]
    [SerializeField] private bool m_logFallRate;

    // 무너짐 한 번을 덮기에 넉넉한 창 — 이 안에 정착하면 그쪽이 먼저 마감한다.
    private const float k_fallRateWindowSeconds = 3f;


    // 낙하 곡선 표본 시각(초) — 정상 낙하는 0.5s 안에 거의 끝난다. 느린 판은 이 곡선이 기어간다.
    private static readonly float[] k_fallMarks = { 0.25f, 0.5f, 1f, 2f, 3f };

    private readonly float[] m_fallDrops = new float[5];
    private int m_fallMarkIndex;
    private float m_fallPeakDescent;
    private float m_fallPrevHipsY;

    private bool m_fallRateActive;
    private float m_fallRateRealStart;
    private float m_fallRatePhysicsStart;
    private float m_fallRateHipsStartY;
    private int m_fallRateFrames;
    private float m_fallRateWorstFrame;

    // 시체를 켜는 순간 = 낙하의 시작. 두 시계를 나란히 찍어 두고 그 뒤로 벌어지는 차를 본다.
    private void BeginFallRateTrace()
    {
        m_fallRateActive = m_logFallRate;
        if (!m_fallRateActive)
            return;

        m_fallRateRealStart = Time.unscaledTime;
        m_fallRatePhysicsStart = Time.fixedTime;
        m_fallRateHipsStartY = m_rig.Hips != null ? m_rig.Hips.position.y : float.NaN;
        m_fallRateFrames = 0;
        m_fallRateWorstFrame = 0f;

        m_fallMarkIndex = 0;
        m_fallPeakDescent = 0f;
        m_fallPrevHipsY = m_fallRateHipsStartY;
        for (int i = 0; i < m_fallDrops.Length; i++)
            m_fallDrops[i] = float.NaN;

        LogRigPhysics();
    }

    // 진입 시점의 물리 설정 1회 — "시계는 정상인데 몸이 느리다"의 원인은 거의 여기 있다.
    private void LogRigPhysics()
    {
        Rigidbody hips = m_rig.HipsBody;
        if (hips == null)
            return;

        Debug.Log(
            $"[리그물리] {name} 뼈={m_rig.BoneCount} 골반질량={hips.mass:F1}kg "
                + $"선형감쇠={hips.linearDamping:F2} 각감쇠={hips.angularDamping:F2} "
                + $"중력={hips.useGravity} 침투해소상한={hips.maxDepenetrationVelocity:F2}m/s "
                + $"솔버={hips.solverIterations}/{hips.solverVelocityIterations} "
                + $"중력크기={Physics.gravity.magnitude:F2}m/s²",
            this
        );
    }

    private void TickFallRate()
    {
        if (!m_fallRateActive)
            return;

        m_fallRateFrames++;
        m_fallRateWorstFrame = Mathf.Max(m_fallRateWorstFrame, Time.unscaledDeltaTime);

        // 골반이 내려간 속도와 곡선 — 낙차 총량은 포화되므로 <b>모양</b>을 봐야 느림이 보인다.
        float hipsY = m_rig.Hips != null ? m_rig.Hips.position.y : m_fallPrevHipsY;
        if (Time.unscaledDeltaTime > 0.0001f)
        {
            m_fallPeakDescent = Mathf.Max(
                m_fallPeakDescent,
                (m_fallPrevHipsY - hipsY) / Time.unscaledDeltaTime
            );
        }

        m_fallPrevHipsY = hipsY;

        float elapsed = Time.unscaledTime - m_fallRateRealStart;
        while (m_fallMarkIndex < k_fallMarks.Length && elapsed >= k_fallMarks[m_fallMarkIndex])
        {
            m_fallDrops[m_fallMarkIndex] = m_fallRateHipsStartY - hipsY;
            m_fallMarkIndex++;
        }

        if (Time.unscaledTime - m_fallRateRealStart >= k_fallRateWindowSeconds)
            DumpFallRate("창종료");
    }

    // 한 줄로 마감한다 — 창이 닫히거나 정착한 시점, 둘 중 먼저 오는 쪽이 부른다.
    private void DumpFallRate(string reason)
    {
        if (!m_fallRateActive)
            return;

        m_fallRateActive = false;

        float real = Time.unscaledTime - m_fallRateRealStart;
        float physics = Time.fixedTime - m_fallRatePhysicsStart;
        float ratio = real > 0.0001f ? physics / real : float.NaN;
        float fps = real > 0.0001f ? m_fallRateFrames / real : float.NaN;
        float drop = m_rig.Hips != null ? m_fallRateHipsStartY - m_rig.Hips.position.y : float.NaN;
        // 경계는 상수로 박지 않고 런타임 값을 읽는다 — ProjectSettings가 바뀌면 판정도 따라간다.
        string verdict = m_fallRateWorstFrame > Time.maximumDeltaTime
            ? "⚠ 클램프에 걸린 프레임 있음"
            : "클램프 없음";

        Debug.Log(
            $"[낙하속도] 권한={HasMoveAuthority} 종료={reason} 실시간={real:F2}s 물리={physics:F2}s "
                + $"비율={ratio:F2}(1.00이 정상) 프레임={m_fallRateFrames} 평균={fps:F1}fps "
                + $"최장프레임={m_fallRateWorstFrame * 1000f:F0}ms({verdict}) 골반낙차={drop:F2}m "
                + $"최고하강={m_fallPeakDescent:F2}m/s 낙하곡선=[{DescribeFallCurve()}]",
            this
        );
    }

    // 표본이 찍힌 구간만 적는다 — 일찍 끝난 국면에서 빈 칸이 0으로 보이면 오독한다.
    private string DescribeFallCurve()
    {
        System.Text.StringBuilder line = new System.Text.StringBuilder();
        for (int i = 0; i < m_fallMarkIndex && i < k_fallMarks.Length; i++)
        {
            if (line.Length > 0)
                line.Append(' ');
            line.Append($"{k_fallMarks[i]:0.##}s:{m_fallDrops[i]:F2}m");
        }

        return line.Length > 0 ? line.ToString() : "표본없음";
    }

    // ---- 정착 딥 추적 (m_logSettleTrace) — ⚠ 임시 계측, 원인이 잡히면 지운다 ----

    // "몸이 바닥에 있다"로 보는 골반 높이(m) — 이 안이면 루트 높이를 골반이 아니라 <b>지면</b>이
    // 준다(<see cref="TickCapsuleFollow"/>). 누운 시체의 골반은 약 0.2m이고 서 있거나 날아가는 몸은
    // 그보다 훨씬 높다 — 정확한 경계가 필요한 값이 아니라 <b>그 둘을 가르기만</b> 하면 된다.
    // <c>NpcRagdoll</c>의 같은 이름 상수와 같은 값이다.
    private const float k_groundedHipsHeight = 0.5f;

    private const int k_settleTraceFrames = 20;

    private struct SettleTraceSample
    {
        public int Frame;
        public float RootY;
        public float HipsWorldY;
        public float HipsLocalY;
        public bool Driven;
        public RagdollState State;
    }

    private SettleTraceSample[] m_trace;
    private int m_traceHead;
    private int m_traceFilled;
    private int m_traceAfter; // 정착 뒤로 더 찍을 프레임 수 (0이면 안 찍는 중)
    private int m_traceSettleFrame;

    // 매 프레임 담아만 둔다 — 찍는 것은 정착하는 순간이다. 딥이 한순간이라 사후 관측이 불가능하다.
    private void TickSettleTrace()
    {
        if (!m_logSettleTrace || m_rig == null || !m_rig.IsValid || m_rig.Hips == null)
            return;

        if (m_state == RagdollState.Animated)
            return;

        if (m_trace == null || m_trace.Length != k_settleTraceFrames)
        {
            m_trace = new SettleTraceSample[k_settleTraceFrames];
            m_traceHead = 0;
            m_traceFilled = 0;
        }

        SettleTraceSample sample = new SettleTraceSample
        {
            Frame = Time.frameCount,
            RootY = m_root != null ? m_root.position.y : float.NaN,
            HipsWorldY = m_rig.Hips.position.y,
            HipsLocalY = m_rig.Hips.localPosition.y,
            Driven = m_streamer != null && m_streamer.IsStreamDriven,
            State = m_state,
        };

        m_trace[m_traceHead] = sample;
        m_traceHead = (m_traceHead + 1) % k_settleTraceFrames;
        if (m_traceFilled < k_settleTraceFrames)
            m_traceFilled++;

        // 정착 이후 구간 — 실시간으로 이어 찍는다.
        if (m_traceAfter > 0)
        {
            m_traceAfter--;
            LogTraceSample(sample);
        }
    }

    // 정착하는 순간 링버퍼를 쏟고, 이후 구간을 이어 찍도록 예약한다. 양쪽 피어가 같은 함수를 쓴다.
    private void DumpSettleTrace()
    {
        if (!m_logSettleTrace)
            return;

        m_traceSettleFrame = Time.frameCount;
        Debug.Log(
            $"[정착추적] ===== 정착 (권한={HasMoveAuthority} 프레임={m_traceSettleFrame}) — "
                + $"이전 {m_traceFilled}프레임 ↓ =====",
            this
        );

        int start = (m_traceHead - m_traceFilled + k_settleTraceFrames) % k_settleTraceFrames;
        for (int i = 0; i < m_traceFilled; i++)
            LogTraceSample(m_trace[(start + i) % k_settleTraceFrames]);

        m_traceAfter = k_settleTraceFrames;
    }

    // 한 샘플이 한 줄이다 — 여러 줄로 쓰면 MPPM 로그에서 잘린다.
    private void LogTraceSample(SettleTraceSample sample)
    {
        Debug.Log(
            $"[정착추적] 권한={HasMoveAuthority} f={sample.Frame - m_traceSettleFrame:+0;-0;0} "
                + $"루트Y={sample.RootY:F3} 골반월드Y={sample.HipsWorldY:F3} "
                + $"골반로컬Y={sample.HipsLocalY:F3} 스트림={sample.Driven} 상태={sample.State}",
            this
        );
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
        if (m_movement == null || m_rig == null || m_rig.Hips == null)
            return;

        // 골반 위치를 <b>3차원</b>으로 따라간다 — 수평만 맞추면 시체가 공중에 있는 동안 루트가 시체를
        // 대표하지 못하고, 이름표·운반 조준·부활 히트박스가 전부 루트에 붙어 있어 그만큼 어긋난다.
        Vector3 target = m_rig.Hips.position;

        // <b>단 몸이 바닥에 있으면 캡슐이 지면에 앉는다.</b> 수평은 그대로 골반을 따라가되
        // 높이만 지면이 준다.
        //
        // 비행 중처럼 골반 높이(지면 위 약 0.3m)에 붙여 두면 두 가지가 깨진다:
        //  · 캡슐이 내내 <c>공중</c>이라 CharacterController가 접지를 못 본다
        //  · <b>끌리며 시체가 위아래로 튀는 것이 그대로 루트의 높이가 되어 스트림에 실린다.</b>
        //
        // ⚠ <b>예전에는 <c>Settled</c>일 때만 앉혔다 — 무너지는 동안까지 넓혔다.</b> (실측 2026-08-19)
        //
        // 그러면 정착하는 순간 루트가 골반 높이에서 지면으로 <b>한 방에</b> 떨어진다. 권위 피어는
        // 아래 <c>CapturePose</c>/<c>RestoreCapturedPose</c>가 그 프레임을 감싸 무사하지만,
        // <b>원격은 그 낙차를 루트 NetworkTransform으로 받는다</b> — 원격의 뼈는 키네마틱이라
        // 계층을 그대로 따라가므로 몸이 통째로 내려갔다 올라온다.
        // 실측: 루트 0.402 → 0.173(<b>23cm</b>), 골반월드 0.403 → 0.300 → 0.401(<b>10cm 딥, 5프레임</b>).
        //
        // 미리 지면에 앉혀 두면 정착 낙차가 0이라 끌 것이 애초에 없다.
        // <c>NpcRagdoll.TickRootFollow</c>가 같은 이유로 같은 처리를 하고 있고
        // (<c>k_groundedHipsHeight</c>), 플레이어만 안 받고 있었다.
        //
        // <b>공중에서는 앉히지 않는다</b> — 날아가는 동안 루트가 시체를 대표해야 이름표·운반
        // 조준·부활 히트박스가 따라간다. 그래서 <b>골반이 낮을 때만</b>이다.
        //
        // 지면 판정은 정착 정렬(ResolveSettledRootPose)과 같은 것을 쓴다 — 두 곳이 다른 높이를
        // 내면 정착하는 순간 캡슐이 튄다.
        bool haveGround = TryGroundUnder(m_rig.Hips.position, out Vector3 ground);
        bool bodyIsGrounded =
            m_state == RagdollState.Settled
            || (haveGround && m_rig.Hips.position.y - ground.y <= k_groundedHipsHeight);

        if (haveGround && bodyIsGrounded)
            target.y = ground.y - CapsuleBottomOffset;

        // ⚠ <b>루트를 옮기기 전에 뼈를 잡아 두고, 아래에서 되돌린다.</b>
        //
        // "동적 리지드바디는 부모 트랜스폼을 따라가지 않는다"는 이 파일의 전제는 <b>다음 물리
        // 스텝이 포즈를 되써 준 뒤부터</b> 참이다. PhysX가 월드 포즈를 써 넣으면 Unity는 그것을
        // <b>그 시점의 부모 기준 로컬</b>로 저장하므로, 그 뒤 Update에서 부모를 옮기면 자식의
        // 월드는 부모 × 로컬로 <b>같이 끌려간다.</b> 렌더는 Update·LateUpdate 다음이라 그 어긋난
        // 몸이 한 프레임 그려지고, 다음 FixedUpdate에서 되쓰이며 툭 내려온다.
        //
        // <b>진입 프레임이 그 한 번이다.</b> 평소 이 함수는 잔차 몇 cm를 따라가지만 진입 때는
        // 루트가 캡슐 밑면(y≈0)에서 골반(y≈0.9)으로 <b>한 방에 뛴다</b> — 그 프레임에 몸 전체가
        // 골반 높이만큼 떠서 그려진다. 물리를 거치지 않으므로 겹침 탈출 속도 상한
        // (<see cref="RagdollRig"/>의 k_maxDepenetrationVelocity)으로는 줄지 않는다.
        //
        // <c>NpcRagdoll.TickRootFollow</c>와 같은 패턴이다 — 저쪽은 루트가 골반을 따라가는 매
        // 프레임의 같은 왕복(실측 14.6cm)을 이 방식으로 잡는다.
        //
        // <b>아래 <see cref="FollowBodyYaw"/>까지 감싼다</b> — 회전도 계층을 타고 자식에게
        // 전해지므로, 골반 높이만큼 떨어져 있는 몸이 루트 원점을 축으로 휜다.
        //
        // 되돌리는 대입은 <b>렌더 전용</b>이다: 이 프로젝트는 <c>m_AutoSyncTransforms = 0</c>이라
        // 트랜스폼에 쓴 값이 액터로 넘어가지 않는다. PhysX의 포즈는 손대지 않은 채, 화면에
        // 그려지는 자리만 제자리로 돌린다.
        m_rig.CapturePose();

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

        // 위 CapturePose의 짝 — 루트를 옮기고 돌린 뒤 뼈를 원래 월드 포즈로 되돌린다.
        m_rig.RestoreCapturedPose();
    }

    // 목표 yaw로 <b>감쇠 추종</b>한다 — 슬램하지 않는다.
    //
    // <b>왜 슬램이 안 되나.</b> 목표는 골반→머리를 지면에 투영해 얻으므로, 몸이 서 있다가 기우는
    // 구간에서는 투영이 짧아 값이 아직 흔들린다. 그 값을 그대로 대입하면 <b>루트에 매달린 것들이
    // 한 프레임에 통째로 돈다</b> — 뼈는 <see cref="TickCapsuleFollow"/>가 월드 자세로 되돌리므로
    // 시체 자체는 멀쩡한데, 이름표·상호작용·들고 있던 아이템 모델(살아있는 리그의 손에 붙어 있고
    // 사망 시 꺼지는 것은 스킨뿐이다)은 되돌려지지 않는다.
    //
    // 프레임률 독립 지수 감쇠다 — <c>PlayerHeadLook</c>의 pitch 추종과 같은 형태.
    private void FollowBodyYaw()
    {
        if (!m_alignRootYawToBody || !TryGetRootYaw(out float yaw))
            return;

        float current = m_root.eulerAngles.y;
        float eased = m_rootYawFollowSpeed > 0f
            ? Mathf.LerpAngle(current, yaw, 1f - Mathf.Exp(-m_rootYawFollowSpeed * Time.deltaTime))
            : yaw;

        m_root.rotation = Quaternion.Euler(0f, eased, 0f);
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
        DumpFallRate("정착"); // 무너짐이 끝난 지점 — 창이 닫히기 전이면 여기가 마감이다
        m_rig.CapturePose();
        Vector3 landedHips = m_rig.Hips.position;

        m_rig.SetKinematic(true);

        // 캡슐은 진입 때 이미 꺼져 있다(§10-0) — 여기서 토글할 것이 없다. 켜져 있으면 지면 레이가
        // 자기 캡슐에 걸리고(캡슐도 Default 레이어다) 트랜스폼 대입도 내부 캐시가 되돌린다.

        Vector3 rootPosition = m_root.position;
        Quaternion rootRotation = m_root.rotation;
        float rootYawBefore = m_root.eulerAngles.y;
        ResolveSettledRootPose(landedHips, ref rootPosition, ref rootRotation);

        if (HasMoveAuthority)
        {
            m_root.SetPositionAndRotation(rootPosition, rootRotation);

            // 텔레포트로 도착했으니 쌓인 수직 속도를 지운다 — <see cref="PlayerMovement.SetPose"/>가
            // 같은 이유로 하는 처리다(#189). 여기는 SetPose를 거치지 않고 트랜스폼을 직접 옮기므로
            // 그 짝이 필요하다.
            m_movement?.ClearExternalVelocity();
        }

        // 루트 yaw 정렬이 실제로 닿는지 잰다 — 부활 회전 진단의 절반이 여기다. 부활 시점의 루트가
        // 오프셋을 바꿔도 안 변한다면, 원인은 클립 상수가 아니라 <b>이 대입이 안 먹는 것</b>이다.
        if (m_logRevivalYaw)
        {
            bool haveYaw = m_rig.TryGetBodyYaw(out float bodyYaw);
            Debug.Log(
                $"[래그돌 정착 yaw] 권한={HasMoveAuthority} "
                    + $"몸={(haveYaw ? bodyYaw.ToString("F1") : "실패")}° "
                    + $"정렬={m_alignRootYawToBody} 오프셋={m_rootYawOffset:F1}° "
                    + $"→ 목표 {rootRotation.eulerAngles.y:F1}° / "
                    + $"루트 {rootYawBefore:F1}° → {m_root.eulerAngles.y:F1}°",
                this
            );
        }

        m_rig.RestoreCapturedPose();

        // 루트가 더 이상 나중에 점프하지 않으므로 양쪽 모두 곧장 물리로 놓아준다 — 누운 몸이 계속
        // 흔들리게. 원격의 릴리스 타이밍을 재던 유예 구간은 이 설계에서 사라졌다.
        RestToPhysics();

        m_state = RagdollState.Settled;
        DumpSettleTrace();

        // 스트림을 끊고 <b>마지막 자세를 로컬 좌표로</b> 한 번 보낸다 — 원격의 종착 상태다.
        //
        // ⚠ <b>얼림과 스트림 종료가 여기서 갈린다.</b> NPC는 정착 = 얼림이라 둘이 붙어 있었지만,
        // 플레이어는 정착해도 권위 피어의 뼈가 물리에 남는다(<see cref="RestToPhysics"/>의 네 가지
        // 실패 기록). 스트리머가 요구하는 것은 얼림이 아니라 <b>"권위의 몸이 더 이상
        // 유의미하게 움직이지 않는다"</b>뿐이라 그것만 떼어 붙이면 된다.
        m_streamer?.EndStreaming();
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
