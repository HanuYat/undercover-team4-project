using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 플레이어 래그돌 — 기능 정지(<see cref="IncapacitationCause.Die"/>) 또는 홈런 진압봉 비행
/// (<see cref="IncapacitationCause.Launched"/>, #815) 동안 애니메이터를 끄고 뼈를 물리에 넘긴 뒤,
/// 착지·정착하면 다시 애니메이터로 되돌린다. (#506) 사망은 부활 키트가 풀고, 비행은 정착 자체가
/// 복구 신호다 — <see cref="Settle"/>이 그 통보를 보낸다.
///
/// <b>이 클래스가 쥔 것은 "누가 위치를 쥐나"다.</b> 뼈를 물리에 넘기고 되돌리는 일 자체는
/// <see cref="RagdollRig"/>가, 밧줄 견인은 <see cref="RagdollRope"/>가 한다 — 둘 다 네트워크·권위를
/// 모르는 순수 물리라 NPC가 그대로 재사용한다. 여기 남은 것은 전부 <b>플레이어 고유</b>다:
/// CharacterController 캡슐을 대리값으로 쓰는 것, 오너 권한 NetworkTransform, 사망 폴링, 기상 블렌드.
///
/// <b>표현 계층 전용이다</b> — 뼈를 동기화하지 않고 모든 피어에서 로컬로 같은 규칙으로 돈다.
/// 그래서 NetworkBehaviour가 아니고, 매니저도 아니라 App 파사드와 무관하다.
///
/// <b>설계 근거·실측·되살리면 안 되는 것들은 <c>docs/player-ragdoll.md</c>에 있다.</b>
/// 이 파일을 고치기 전에 그쪽의 해당 항목을 먼저 볼 것 — 특히 §2(리그가 한 벌로 돌아왔다)와
/// §5(캡슐 추종이 FixedUpdate인 이유).
///
/// 붙이는 곳: <b>프리팹 루트</b>(CharacterController·PlayerIncapacitation과 같은 오브젝트).
/// ⚠ <see cref="RagdollRig"/>는 <b>자식</b>에, <see cref="RagdollPoseStreamer"/>는 <b>루트</b>에 있다.
/// </summary>
public partial class PlayerRagdoll : MonoBehaviour
{
    // 지면을 못 찾아도 결국은 정착시키는 최후 배수 — 맵 밖으로 떨어진 시체가 Ragdoll에 갇히지 않게.
    private const float k_lostBodyTimeoutFactor = 4f;


    // 사망·비행 원인 동기화를 기다려 주는 시간(초) — <b>안전망뿐</b>이다. 정상 경로에서는 걸리지 않는다 (docs §9).
    private const float k_causeSyncGraceSeconds = 1f;

    // 순간이동 뒤 줄을 다시 맬 운반자 거리(m) — 밧줄 길이(약 2m)보다 넉넉히 두되 운반 끊김 거리(8m)
    // 보다는 짧게. 넓게 잡아도 안전하다: 더 멀면 서버가 운반 자체를 정리한다. (#614)
    private const float k_ropeReattachRange = 5f;

    // 부활 블렌드가 물려 들어가는 상태 — PlayerAnimatorControllerBuilder의 k_groundState와 같아야 한다.
    private static readonly int s_groundStateHash = Animator.StringToHash("Knockdown_Ground");

    private enum RagdollState
    {
        Animated, // 평시 — 전 Rigidbody 키네마틱, 애니메이터가 포즈를 쥔다
        // 물리 중. <b>시체도 끝까지 이 상태다</b> — 정착은 상태가 아니라 m_settled 깃발이다 (docs §3).
        Ragdoll,
        BlendingToAnimator, // 정착 포즈 → 애니메이터 포즈 보간 (부활)
    }

    [Header("정착 판정")]
    [Tooltip("정착 판정 타임아웃(초) — 지형에 껴서 영원히 떨리는 경우의 안전장치")]
    [SerializeField] private float m_settleTimeoutSeconds = 5f;

    [Header("정착 후 정렬")]
    [Tooltip("시체 밑 지면을 찾는 레이캐스트 마스크 — 지형(Default). 래그돌 뼈는 다른 레이어라 걸리지 않는다")]
    [SerializeField] private LayerMask m_groundMask = 1;

    [Tooltip("골반 아래로 지면을 찾는 거리(m). 짧게 잡을 것 — 길면 얇은 실내 바닥을 뚫고 아래층 지면을 " +
             "찾아내 시체가 한 층 밑으로 순간이동한다. 못 찾으면 골반 높이를 쓴다")]
    [SerializeField] private float m_groundProbeDistance = 1.5f;

    [Tooltip("루트 yaw를 몸이 누운 방향에 맞춘다 — 기상 모션이 '루트 전방을 향해 누워 있다'를 " +
             "전제하므로. 비행 중에도 매 프레임 맞춘다 — 근거는 docs/player-ragdoll.md §11")]
    [SerializeField] private bool m_alignRootYawToBody = true;

    [Tooltip("루트 yaw 추종 감쇠율(1/초) — 0이면 즉시 대입.\n\n" +
             "⚠ 즉시 대입은 위험하다: 몸이 막 기우는 동안 방향값이 흔들려 루트에 매달린 것들" +
             "(이름표·아이템)이 한 프레임에 통째로 돈다 — docs/player-ragdoll.md §11")]
    [SerializeField] private float m_rootYawFollowSpeed = 8f;

    [Tooltip("몸 방향 대비 루트 yaw 보정(도) — Knockdown_StandUp 클립이 어느 쪽을 머리로 보는지에 " +
             "맞춘다. 아래 m_logRevivalYaw로 실측해 넣는 값이다")]
    [SerializeField] private float m_rootYawOffset;

    [Header("애니메이터 복귀")]
    [Tooltip("정착 포즈 → 애니메이터 포즈 보간 시간(초)")]
    [SerializeField] private float m_blendSeconds = 0.4f;

    private RagdollRig m_rig; // 뼈 한 벌 — 물리 조작 전부를 여기 위임한다
    private bool m_lostBodyHidden; // 회수 불가 몸을 이미 감췄는가 (#819) — 매 프레임 렌더러를 훑지 않으려고
    // HideLostBody가 실제로 끈 렌더러만 담는다 — 되살릴 때 이 목록만 켜야, 평소 꺼져 있는 렌더러
    // (1인칭 팔 리그처럼 뼈 이름이 겹치는 여분 리그)까지 함께 켜는 사고를 피한다.
    private readonly List<Renderer> m_hiddenLostBodyRenderers = new List<Renderer>();
    private RagdollRope m_rope; // 밧줄 견인 (선택 — 없으면 운반이 물리로 안 끌린다)

    // 다시 맬 상대들 — 순간이동이 관절을 끊어도 남는다. 참가자별로 실제 운반이 끝날 때만 빠진다.
    // 여럿이 덧걸 수 있어(합류) 목록이다 (#614·다인 확장).
    private readonly List<Transform> m_ropeCarriers = new List<Transform>();
    private RagdollPoseBlend m_blend; // 부활 블렌드 — 리그가 한 벌이므로 그 리그를 섞는다

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

    // <b>정착은 상태가 아니라 국면을 적어 둔 깃발이다</b> — 뜻은 "물리가 잠들어 스트림을 끊었다"뿐이고
    // 뼈는 동적으로 남는다. 밟히거나 밀리면 Update의 깨어남 폴링이 스트림을 되살린다 (docs §3·§10).
    private bool m_settled;
    private float m_elapsedInRagdoll;

    // 늦게 접속했는데 대상이 이미 쓰러져(다운·사망) 있거나 비행 중이던 경우 — 이번 래그돌 원인은
    // 건너뛴다 (docs §9). ⚠ 다운→사망을 지나도 계속 참이다(!wantsRagdoll일 때만 내려간다) —
    // 그 피어에서는 유예가 끝나도 몸이 Knockdown_Ground 애니로 남는다. 이미 끝난 낙하를 뒤늦게
    // 재생하지 않는 것이 이 플래그의 뜻이라 의도된 동작이고, 위치·yaw는 루트 NT가 맞춘다.
    private bool m_skipThisEpisode;
    private bool m_polledOnce;

    // 이번 에피소드에서 <b>래그돌 원인(다운·사망·비행)을 한 번이라도 관측했는가</b> — 부활 판정의 전제다 (docs §9).
    // #815로 비행, #865로 다운이 늘어왔다 — 이름을 "원인"으로 고친 것이 그 셋을 함께 담기 위해서다.
    private bool m_sawCauseThisEpisode;
    private float m_awaitingCauseSeconds;

    /// <summary>
    /// 래그돌이 애니메이터로부터 포즈를 빼앗고 있는가 — <see cref="PlayerMovement.AddKnockback"/>·
    /// <see cref="PlayerAnimationDriver"/>·<see cref="PlayerHeadLook"/>이 각자 물러나는 판정에 쓴다.
    /// 정착 후에도, 부활 블렌드 중에도 참이다.
    /// </summary>
    public bool IsRagdollActive => m_state != RagdollState.Animated;

    /// <summary>
    /// 캡슐이 시체를 따라가야 하는 구간인가 — <see cref="PlayerMovement.Update"/>가 입력 이동을 접는 판정.
    /// </summary>
    internal bool IsCapsuleFollowingBody => m_state == RagdollState.Ragdoll;

    /// <summary>
    /// 물리가 정착했는가 — <see cref="PlayerIncapacitation.RequestLaunchSettled"/>가 비행(#815) 복구
    /// 판정에 쓴다. <see cref="m_settled"/>는 권위 게이트 뒤에서만 세워지므로 이 값이 참인 피어가
    /// 곧 그 통보를 보낼 오너다.
    /// </summary>
    internal bool IsSettled => m_settled;

    // 이동 권한 — 오너(또는 세션 없는 오프라인 Play)만 루트를 옮길 수 있다.
    private bool HasMoveAuthority =>
        m_netObject == null || !m_netObject.IsSpawned || m_netObject.IsOwner;

    // ⚠ #759 계측 — 원인이 닫혀 주석 처리했다(2026-08-20). 근거: docs/759-ragdoll-slowmotion-handoff.md
    //    계측 줄머리 TraceId — [리그물리]/[낙하속도]/[밧줄]만 쓰던 것이다.
    /*
    // 계측 줄머리 — <b>같은 시체를 피어마다 짝지으려면 이름만으로는 안 된다</b>(전부 Player(Clone)).
    // 오브젝트 id로 시체를, 오너 id로 "누구의 몸인가"를, 로컬 id로 "이 줄을 찍은 피어"를 가른다.
    private string TraceId
    {
        get
        {
            if (m_netObject == null || !m_netObject.IsSpawned)
                return name;

            ulong local = NetworkManager.Singleton != null
                ? NetworkManager.Singleton.LocalClientId
                : 0;

            return $"시체#{m_netObject.NetworkObjectId} 오너{m_netObject.OwnerClientId} 나{local}";
        }
    }
    */

    private void Awake()
    {
        // ⚠ 리그는 <b>자식</b>에 있다 — 비활성일 수 있으므로 includeInactive를 반드시 켠다.
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

        // 소유자보다 먼저 돌 수 있다 — 리그 수집은 멱등이라 여기서 보장해도 된다.
        m_rig.EnsureCollected();

        // 밧줄은 리그와 <b>같은 오브젝트</b>에 있다 — RagdollRope가 RagdollRig를 RequireComponent한다.
        m_rope = m_rig.GetComponent<RagdollRope>();

        // 리그가 한 벌이므로 섞는 대상도 그 리그다 — 애니메이터와 물리가 같은 뼈를 번갈아 쥔다.
        m_blend = new RagdollPoseBlend(m_rig.BoneRoot);

        m_animator = GetComponentInChildren<Animator>();
        m_controller = GetComponentInParent<CharacterController>();
        m_incapacitation = GetComponentInParent<PlayerIncapacitation>();
        m_movement = GetComponentInParent<PlayerMovement>();
        m_netObject = GetComponentInParent<NetworkObject>();

        // 판정의 주체는 CharacterController가 붙은 트랜스폼이다 — 이 컴포넌트가 프리팹 어디에 붙어도
        // 같은 것을 가리키게 한다.
        m_root = m_controller != null ? m_controller.transform : transform;

        // 스트리머는 <b>루트</b>에 있다 — NGO가 비활성 GameObject의 NetworkBehaviour를 스폰에서
        // 제외하므로 NetworkObject와 같은 오브젝트여야 한다.
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

        // 뼈 콜라이더는 평시에도 켜져 있으므로(격리는 Ragdoll 레이어와 충돌 매트릭스가 맡는다)
        // 캡슐 무시를 여기서 바로 건다.
        ReapplyCapsuleIgnore();
    }

    // ---- 래그돌 표현 전환 (리그 한 벌 — NPC와 같은 모양, #763 2단계) ----

    /// <summary>
    /// 리그를 물리에 넘긴다 — <b>사망 시 표현 전환의 전부.</b> 모델을 갈아 끼우지 않는다.
    /// ⚠ 애니메이터를 반드시 먼저 끈다 — 리그가 한 벌이라 켜 둔 채 넘기면 물리 결과를 매 프레임 덮는다.
    /// </summary>
    private void EnterRagdollPose()
    {
        if (m_animator != null)
            m_animator.enabled = false;

        // 컬링으로 사라지지 않게 — 무너진 뼈가 루트에서 멀어져도 그린다(NPC와 같다).
        m_rig.SetSkinsAlwaysVisible(true);

        // ⚠ 물리로 넘기기 <b>전에</b> 열어야 진입 프레임의 자세가 계측에 남는다.
        BeginEntryTrace();
        // BeginFallRateTrace();

        ReleaseBonesToPhysics();
    }

    /// <summary>
    /// 리그를 애니메이터에게 돌려줄 준비 — 부활·라운드 리셋. <b>애니메이터를 켜는 것은 호출부가 한다</b>
    /// (블렌드 출발점을 잡는 순서 때문 — <see cref="ExitToAnimator"/>).
    /// <c>RestoreBindPose</c>는 자세가 아니라 <b>뼈 길이</b>를 되돌린다 — 유일한 누적 방어다 (docs §8).
    /// </summary>
    private void ExitRagdollPose()
    {
        m_rig.SetSkinsAlwaysVisible(false);
        m_rig.RestoreBindPose();
    }

    /// <summary>
    /// 뼈를 물리로 놓아준다. <b>자세를 스트림으로 받는 피어는 전 뼈 키네마틱</b>이라 물리를 아예
    /// 돌리지 않고, 첫 패킷 전까지 붙들 자세만 잡아 둔다.
    /// 권위 피어는 정착해도 얼리지 않는다 — 그 근거와 앞서 버린 세 방식은 docs §7.
    /// </summary>
    private void ReleaseBonesToPhysics()
    {
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
    /// 원격이 정착 자세를 받았다 — 깃발만 세운다. 자세는 스트리머가 이미 입혔고, 권위 쪽
    /// <see cref="Settle"/>의 나머지는 자기 물리를 정리하는 일이라 여기서 하면 안 된다.
    /// </summary>
    private void HandleSettledPoseReceived()
    {
        if (m_state == RagdollState.Animated)
            return; // 이번 사망을 건너뛴 피어 — 살아있는 몸을 시체 상태로 밀지 않는다

        m_holdPoseUntilStream = false;
        m_settled = true;
        DumpSettleTrace();
    }

    private void OnDestroy()
    {
        if (m_streamer != null)
            m_streamer.OnSettledPoseReceived -= HandleSettledPoseReceived;
    }

    // ---- 캡슐(대리값) 다루기 — 여기부터가 플레이어 고유다 ----

    // 자기 CharacterController 캡슐과의 충돌을 끈다 — 죽는 순간 래그돌은 자기 캡슐 <b>안에서</b>
    // 출발하므로 그대로 두면 겹침 탈출에 몸이 튄다.
    //
    // ⚠ 콜라이더를 껐다 켜면 이 상태가 초기화된다(Unity 사양). 그래서 재적용을 경로마다 흩지 않고
    // <b>캡슐을 켜는 통로 하나</b>에 걸었다 — PlayerMovement.SetCapsuleEnabled가 켜는 순간 부른다.
    // 근거와 옛 폴러가 놓친 구멍은 docs/player-ragdoll.md §6.
    internal void ReapplyCapsuleIgnore()
    {
        if (m_controller == null)
            return;

        m_rig.IgnoreCollisionWith(m_controller, true);
    }

    // 캡슐을 켜고 끈다 — <b>쓰기는 PlayerMovement에 맡긴다</b>(캡슐의 주인이 저쪽이고, 켜는 순간의
    // 무시 재적용도 저쪽 통로가 책임진다). 직접 쓰는 것은 배선이 없는 구성뿐이다.
    private void SetControllerEnabled(bool value)
    {
        if (m_movement != null)
        {
            m_movement.SetControllerEnabled(value);
            return;
        }

        if (m_controller == null)
            return;

        m_controller.enabled = value;
        if (value)
            ReapplyCapsuleIgnore();
    }

    // ---- 밧줄 파사드 (#365 운반 / #398 드래그) ----
    //
    // 실물은 RagdollRope가 쥔다. 파사드를 두는 이유는 호출부(PlayerTowedMotion)가 "래그돌인 대상에게
    // 밧줄을 묶는다"를 표현하기 때문이다 — 직접 찾게 하면 조건이 호출부로 새어 나간다. (docs §12)

    /// <summary>관절 밧줄의 길이(m) — 리그가 아직 안 잡혔으면 0. 묻는 쪽이 대신 쓸 값을 정한다. (#644)</summary>
    public float RopeLength => m_rope != null ? m_rope.Length : 0f;

    /// <summary>
    /// 밧줄을 시체에 <b>한 가닥</b> 묶는다 — <see cref="PlayerTowedMotion.BeginDraggedFollow"/>가
    /// 래그돌인 대상에게, 참가자(합류)마다 부른다. <b>권위 피어만 묶는다</b> — 전 피어가 각자 묶던
    /// 옛 배선이 견인 발산의 근원이었다 (docs §12).
    /// </summary>
    /// <param name="carrier">운반자(밧줄을 쥔 쪽) — 이미 목록에 있으면 멱등.</param>
    public void BeginRopePull(Transform carrier)
    {
        // BeginRopeTrace();

        // ⚠ 권위 가드보다 <b>앞</b>에 기억한다 — 이 호출은 전 피어에 오지만(운반 RPC가 SendTo.Everyone),
        // 사망 중 소유권이 넘어가면 지금 권위가 아닌 피어가 나중에 권위가 될 수 있다. (#614)
        if (carrier != null && !m_ropeCarriers.Contains(carrier))
            m_ropeCarriers.Add(carrier);

        if (!HasMoveAuthority)
            return;

        // 깨우기만 한다 — 스트림 재개는 <see cref="Update"/>의 깨어남 폴링이 받는다.
        // (NpcRagdoll.WakeCorpse와 같은 모양. 재개 경로를 하나로 모으는 것이 요점이다)
        m_rig.WakeAll();
        if (m_settled)
            ResumeFromSleep();

        m_rope?.Attach(carrier);
    }

    /// <summary>이 참가자의 가닥만 푼다 — 줄다리기에서 한 명이 손을 뗄 때. 없으면 무동작(멱등).
    /// <b>다시 맬 상대에서도 뺀다</b> — <see cref="PlaceBodyBy"/>가 남기는 것과 갈리는 지점이 여기다. (#614)</summary>
    public void EndRopePull(Transform carrier)
    {
        m_ropeCarriers.Remove(carrier);
        m_rope?.Detach(carrier);
    }

    // 회수 불가로 확정된 몸을 화면에서 지운다 (#819). 전 피어가 각자 부르는 자리다 —
    // IsBodyLost가 복제되므로 서버 지시 없이도 같은 결과가 난다.
    // <see cref="ShowLostBody"/>와 대칭 — 같은 오브젝트가 라운드를 넘어 재사용되므로
    // (PlayerHealth.ServerResetState가 HP만 초기화하고 despawn하지 않는다) 되돌리지 않으면
    // 다음 라운드부터 이 플레이어가 영구히 투명해진다.
    private void HideLostBody()
    {
        if (m_lostBodyHidden)
            return;

        m_lostBodyHidden = true;
        foreach (Renderer renderer in GetComponentsInChildren<Renderer>(true))
        {
            if (!renderer.enabled)
                continue;

            renderer.enabled = false;
            m_hiddenLostBodyRenderers.Add(renderer);
        }
    }

    // HideLostBody가 끈 렌더러만 되살린다 — IsBodyLost가 풀리는 유일한 경로(PlayerIncapacitation
    // .SetBodyLost(false))는 부활 RPC 안에서 돌고, 그 직후 PollRagdollCause가 ExitToAnimator를 부른다.
    private void ShowLostBody()
    {
        if (!m_lostBodyHidden)
            return;

        m_lostBodyHidden = false;
        foreach (Renderer renderer in m_hiddenLostBodyRenderers)
        {
            if (renderer != null)
                renderer.enabled = true;
        }
        m_hiddenLostBodyRenderers.Clear();
    }

    /// <summary>밧줄을 전부 푼다 — 내려놓기·부활·운반자 전원 소실. <b>다시 맬 상대도 전부 잊는다</b>
    /// — 순간이동이 잠깐 끊는 것(<see cref="PlaceBodyBy"/>)과 갈리는 지점이 여기다. (#614)</summary>
    public void EndRopePull()
    {
        m_ropeCarriers.Clear();
        m_rope?.Detach();
    }

    /// <summary>
    /// 순간이동이 끊어 둔 줄을 <b>참가자가 실제로 가까워지면</b> 각자 다시 맨다 — 권위 피어 전용. (#614)
    ///
    /// <b>왜 바로 못 매는가.</b> 운반자와 이 몸은 <b>오너가 서로 다른 피어</b>라 두 순간이동이 각자
    /// 도착한다 — 이 피어가 옮겨진 직후에는 운반자가 아직 <b>옛 자리</b>로 보인다. 그때 매면 앵커가
    /// 거기 생기고 다음 물리 스텝에 그 거리만큼 위반이 터진다(<see cref="PlaceBodyBy"/>가 줄을 끊고
    /// 가는 이유와 같은 사고).
    ///
    /// <b>참가자마다 따로 판정한다</b> — <c>m_rope.IsAttached</c>("한 가닥이라도")로 한 번만 보면
    /// 다인 운반에서 A가 먼저 붙는 순간 B의 재부착이 영영 막힌다. 각자 자기 가닥이 이미 붙었는지
    /// (<see cref="RagdollRope.IsAttachedTo"/>)와 자기 거리만 본다.
    ///
    /// 영영 안 매인 채 남지 않는 근거는 서버에 있다 — 그만큼 멀면 <see cref="PlayerCarrier"/>의 거리
    /// 검사(또는 목줄 완화식)가 유예가 끝난 뒤 그 참가자의 운반을 정리한다.
    /// </summary>
    private void TickRopeReattach()
    {
        if (m_rope == null || m_ropeCarriers.Count == 0)
            return;

        // 이 프레임에 다시 매는 참가자가 있을 수 있어 스냅샷을 돈다 — BeginRopePull이 같은 목록을
        // 다시 건드리지는 않지만(이미 들어 있으면 멱등), 방어적으로 인덱스 역순 순회를 쓴다.
        for (int i = m_ropeCarriers.Count - 1; i >= 0; i--)
        {
            Transform carrier = m_ropeCarriers[i];
            if (carrier == null || m_rope.IsAttachedTo(carrier))
                continue;

            Vector3 delta = carrier.position - m_root.position;
            if (delta.sqrMagnitude > k_ropeReattachRange * k_ropeReattachRange)
                continue;

            BeginRopePull(carrier);
        }
    }

    // ---- 순간이동 (#614) ----

    /// <summary>
    /// 순간이동한 루트에 뼈를 <b>같은 델타로</b> 따라 옮긴다 — 오너 전용. 래그돌이 아니면 무동작.
    /// 부르는 곳은 <see cref="PlayerMovement.SetPose"/> 하나다.
    ///
    /// <b>루트는 건드리지 않는다</b> — 저쪽이 이미 옮겼다. 여기서 또 옮기면 델타가 두 번 실린다.
    /// 뼈는 동적 리지드바디라 루트를 따라오지 않으므로(계층이 아니라 물리가 자리를 쥔다) 이 보정이
    /// 없으면 <see cref="TickCapsuleFollow"/>가 다음 물리 스텝에 루트를 <b>도로 시체 자리로</b> 끌어간다.
    ///
    /// NPC의 <c>NpcRagdoll.ServerPlaceCorpse</c>와 같은 일을 하지만 <b>도는 피어가 반대다</b> —
    /// 저쪽은 서버, 이쪽은 그 몸의 오너다(루트 NetworkTransform·자세 스트림이 둘 다 오너 권한).
    /// </summary>
    /// <param name="delta">루트가 옮겨 간 거리 — 옮기기 <b>전에</b> 재야 한다.</param>
    public void PlaceBodyBy(Vector3 delta)
    {
        if (!HasMoveAuthority || m_state != RagdollState.Ragdoll)
            return;
        if (m_rig == null || !m_rig.IsValid)
            return;

        // ⚠ <b>줄을 먼저 끊는다.</b> 관절의 앵커는 운반자를 따라가는 별개 오브젝트라 이 델타로 같이
        // 움직이지 않는다 — 매인 채 옮기면 위반이 그 거리만큼 생기고 솔버가 그것을 메우며 몸을
        // <b>발사한다</b>(NPC 실측 237 m/s). 다시 매는 것은 운반자가 가까워진 뒤다
        // (<see cref="TickRopeReattach"/>) — 여기서 바로 매면 아직 옛 자리에 있는 운반자에게 걸린다.
        //
        // ⚠ <see cref="EndRopePull()"/>이 아니라 관절만 끊는다 — 저쪽은 "운반이 끝났다"라 다시 맬
        // 상대까지 잊는다. 여기서는 운반이 계속되는 중이므로 <see cref="m_ropeCarriers"/>를 남긴다.
        m_rope?.Detach();

        // 전 뼈를 한 델타로 — 상대 자세·속도·관절이 보존돼 도착지에서 솔버가 메울 것이 없다.
        m_rig.TranslateBy(delta);

        // ⚠ <b>보간 없이 쏜다 — 안 쏘면 원격의 몸이 출발지에 남는다.</b> 평범한 스냅샷으로 보내면
        // 원격이 두 지점 사이를 보간하며 몸이 맵을 가로질러 날아간다. 이 패킷에는 뼈 길이도 실려
        // 원격이 바인드 골격으로 그리는 것도 함께 막는다.
        m_streamer?.SendTeleportPose();

        // 옮긴 몸은 깨어난 것으로 본다 — 도착지에서 다시 무너져 잠드는 과정이 원격에도 흘러야 한다.
        m_rig.WakeAll();
        if (m_settled)
            ResumeFromSleep();
    }

    // ---- 진입 / 이탈 ----

    /// <summary>
    /// 래그돌 진입 — <b>멱등이다.</b> 이미 물리 중이면 임펄스만 누적하고, 블렌드 중이면 무동작.
    /// 원격의 도착 순서가 보장되지 않기 때문이다 — 반대 순서는 <see cref="PollRagdollCause"/>가 막는다 (docs §8·§9).
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
        m_settled = false;
        m_elapsedInRagdoll = 0f;

        // 슬라이드 넉백과 이중으로 밀리지 않게 CharacterController 쪽 외력을 지운다.
        m_movement?.ClearExternalVelocity();

        // <b>사망 중에는 캡슐을 끈다</b> — 뼈를 물리에 넘기기 <b>전에</b>. 순서를 뒤집으면 뼈가 캡슐
        // 안에서 겹친 채 한 프레임을 보내고 그 탈출 임펄스에 몸이 튄다. (docs §8)
        SetControllerEnabled(false);

        EnterRagdollPose();
        m_rig.ApplyImpulse(impulse);

        // 자세를 흘려보내기 시작한다 — 권위가 아니면 스스로 무동작이다(그쪽 주석).
        m_streamer?.BeginStreaming();
    }

    /// <summary>
    /// 날아가는 구간을 즉시 끝내고 정착으로 넘긴다 — 운반 시작(#365)처럼 외부 사정이 있을 때.
    /// 정착해도 몸이 굳지는 않는다 (docs §7).
    /// </summary>
    public void ForceSettle()
    {
        if (m_state == RagdollState.Ragdoll)
            Settle();
    }

    /// <summary>
    /// 애니메이터로 되돌린다.
    /// <paramref name="blend"/>가 참이면 정착 포즈에서 기상 자세로 보간하고(본부 부활),
    /// 거짓이면 즉시 되돌린다(라운드 리셋·씬 전환·despawn).
    ///
    /// ⚠ <b>순서가 그대로 결과를 바꾼다</b> — 특히 블렌드 출발점은 뼈 길이 복원보다 먼저다. docs §8.
    /// </summary>
    public void ExitToAnimator(bool blend)
    {
        // DumpFallRate("이탈"); // 창이 닫히기 전에 부활했다 — 남은 값으로라도 마감한다
        if (m_state == RagdollState.Animated || m_rig == null || !m_rig.IsValid)
            return;

        // 회수 불가로 감춰졌던 몸이라면 애니메이터로 돌아가는 이 시점에 되살린다 — HideLostBody 참고.
        ShowLostBody();

        // 에피소드가 여기서 끝난다 — 다음 래그돌은 자기 원인을 다시 관측해야 부활할 수 있다 (PollRagdollCause).
        m_settled = false;
        m_sawCauseThisEpisode = false;
        m_awaitingCauseSeconds = 0f;

        // 스트림을 <b>아무것도 보내지 않고</b> 끊는다 — 기상에는 종착 자세가 없다. 전 피어가 각자
        // 부르므로 RPC가 필요 없다. 안 끊으면 스트림이 기상 블렌드를 매 프레임 덮어쓴다.
        m_streamer?.StopStreaming();
        m_holdPoseUntilStream = false;

        // 뼈는 전부 물리에 있다 — 애니메이터로 돌아가려면 전부 멈춰야 한다.
        // 밧줄을 먼저 끊는다: 순서를 뒤집으면 한 스텝 동안 스프링이 블렌드 시작 포즈를 당긴다.
        EndRopePull();

        // 시체가 실제로 누운 방향 — 얼리기 <b>전에</b> 잰다. 아래 yaw 진단이 쓴다.
        bool haveCorpseYaw = m_rig.TryGetBodyYaw(out float corpseYaw);

        m_rig.SetKinematic(true);

        // ⚠ <b>블렌드 출발점은 지금 이 래그돌 자세다 — 뼈 길이 복원보다 반드시 먼저 잡는다.</b>
        // 뒤에 잡으면 ExitRagdollPose의 RestoreBindPose가 누운 자세를 지워 몸이 툭 선다.
        bool blending =
            blend
            && m_animator != null
            && m_blend != null
            && m_blend.IsValid
            && m_state != RagdollState.BlendingToAnimator;

        if (blending)
            m_blend.Begin();

        ExitRagdollPose();

        // 캡슐을 되살린다. 이 시점의 루트는 캡슐 추종이 매 프레임 지면 위에 놓아 둔 자리라 그대로
        // 켜면 된다 — 그쪽이 <see cref="CapsuleBottomOffset"/>까지 보정한다.
        SetControllerEnabled(true);
        m_movement?.ClearExternalVelocity(); // 꺼져 있던 동안 쌓인 값이 도착지에서 바닥을 파고들지 않게 (#189)

        // <b>애니메이터를 여기서 켠다</b> — 진입에서 껐고(EnterRagdollPose), 아래 Update(0f)가 클립을
        // 평가하려면 켜져 있어야 한다.
        if (m_animator != null)
            m_animator.enabled = true;

        if (!blending)
        {
            m_state = RagdollState.Animated;
            return;
        }

        // Down은 아직 참이므로 바닥 대기 자세로 물려 들어가야 정착 포즈와의 거리가 가장 짧다.
        // 쓰기 전 값을 들고 있어야 애니메이터가 정말 뼈를 썼는지 판정할 수 있다(진단).
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
    /// <see cref="ExitToAnimator"/>만으로는 부족해 <see cref="m_skipThisEpisode"/>를 함께 세운다 (docs §8).
    /// </summary>
    public void ExitForReposition()
    {
        ExitToAnimator(blend: false);
        m_skipThisEpisode = true;
    }

    // ---- 매 프레임 ----

    /// <summary>
    /// 캡슐 추종은 <b>물리 스텝에 묶는다 — 프레임이 아니다.</b> (#759)
    /// 프레임마다 돌면 트랜스폼 주입이 스텝당 1회를 넘어 솔버가 관절 오차를 못 따라잡는다
    /// (100fps 호스트에서 발산 — 이것이 슬로모션의 원인이었다).
    /// ⚠ <b>PlayerMovement가 아니라 여기서 돈다</b> — 사망 중 소유권이 넘어가도 권위 피어가 돌게.
    /// 근거는 docs/player-ragdoll.md §5.
    /// </summary>
    private void FixedUpdate()
    {
        if (m_state != RagdollState.Ragdoll || !HasMoveAuthority)
            return;

        TickCapsuleFollow();
    }

    private void Update()
    {
        // 진입 추적의 기준선을 담는다 — Update 시작이 직전 프레임의 최종 자세를 읽는 유일한 지점이다.
        SampleEntryBaseline();

        PollRagdollCause();

        if (m_state != RagdollState.Ragdoll)
            return;

        // 회수 불가로 확정된 몸은 권위와 무관하게 전 피어가 각자 즉시 재우고 감춘다 — 원격은
        // HasMoveAuthority 게이트에 걸려 이 자리에 못 오면 몸이 계속 남아 보인다. (#775/#819)
        if (m_incapacitation != null && m_incapacitation.IsBodyLost)
        {
            m_rig.SleepAll();
            HideLostBody();
            Settle();
            return;
        }

        // ⚠ <b>정착 판정은 권위만 돌린다.</b> 원격의 뼈는 키네마틱이라 속도가 항상 0이고, 이 게이트가
        // 없으면 무너지기도 전에 정착해 버린다. 원격의 종착 상태는 받는 정착 자세가 준다. (docs §10)
        if (!HasMoveAuthority)
            return;

        // ⚠ <b>정착 게이트보다 앞이다</b> — 순간이동 뒤 줄이 끊긴 채 잠든 몸도 다시 매여야 하고,
        // 매는 순간 BeginRopePull이 깨우기까지 한다. 뒤로 내리면 잠든 몸은 영영 안 매인다.
        TickRopeReattach();

        // 잠든 뒤에는 <b>깨어났는지만</b> 본다 — 밟히거나 밀리면 PhysX가 스스로 깨우므로 이 한 줄이
        // 그 모든 경로를 받는다. 이것이 없던 것이 #763의 뿌리다. (docs §10)
        if (m_settled)
        {
            if (!m_rig.AllAsleep)
                ResumeFromSleep();

            return;
        }

        // 끌리는 동안에는 재우지 않는다 — 놓는 순간부터 다시 센다. (NpcRagdoll과 같은 자리)
        if (m_rope != null && m_rope.IsBeingCarried)
        {
            m_elapsedInRagdoll = 0f;
            return;
        }

        m_elapsedInRagdoll += Time.deltaTime;

        // <b>정착은 물리가 정한다</b> — 전 뼈가 하나도 안 남고 잠들어야 참이다. 옛 평균속도 판정이
        // "흔들거리다 갑자기 굳는" 어색함의 정체였다. (docs §10)
        if (m_rig.AllAsleep)
        {
            Settle();
            return;
        }

        if (m_elapsedInRagdoll < m_settleTimeoutSeconds)
            return;

        // 아직 공중이다 — 여기서 재우면 <b>떠 있는 시체</b>가 된다. 다만 맵 밖으로 떨어진 몸이
        // 영원히 갇히지 않게 무한정 기다리지는 않는다.
        if (!HasGroundUnderHips()
            && m_elapsedInRagdoll < m_settleTimeoutSeconds * k_lostBodyTimeoutFactor)
            return;

        // 타임아웃 — 지형에 물려 스스로 못 잠드는 몸이다. 대신 재운다.
        // ⚠ 키네마틱 얼림이 아니라 <b>물리 수면</b>이라, 밟거나 밧줄을 걸면 깨어남 폴링이 그대로 받는다.
        m_rig.SleepAll();
        Settle();
    }


    // 골반 밑에 지면이 있는가 — 정착 자격과 정착 정렬이 <b>같은 탐색</b>을 써야 모순이 안 생긴다.
    private bool HasGroundUnderHips() => TryGroundUnder(m_rig.Hips.position, out _);

    // 래그돌 진입 사유(다운·사망·비행)를 폴링한다 — 이벤트로는 잡을 수 없다(원인만 바뀌면 안 울린다).
    // 사유는 셋이지만 <see cref="PlayerIncapacitation.Cause"/>는 동시에 하나만 참일 수 있어 겹치지 않는다.
    // NPC의 <see cref="NpcRagdoll.WantsRagdoll"/>과 같은 자리다 — #815로 둘, #865로 셋이 됐다.
    //
    // ⚠ <b>다운→사망은 사유가 바뀌는데 래그돌은 이어진다</b> — 셋 중 유일한 "래그돌 중 원인 전이"이고,
    // 그래서 그 전이에 소유권(=물리 권위)이 움직이지 않아야 한다. 근거는 docs/865-down-ragdoll.md.
    // 부활은 <b>원인을 본 뒤에만</b> 성립한다 — 그 인과 가드의 근거는 docs/player-ragdoll.md §9.
    private void PollRagdollCause()
    {
        if (m_incapacitation == null)
            return;

        // 진입 사유는 셋이다 (#506 Die → #815 Launched → #865 Down). IsDowned || IsDead를 나열하지
        // 않고 IsOutOfAction을 쓰는 이유는 <b>조준 히트박스와 술어를 하나로 묶기 위해서</b>다 —
        // IsAimTargetable이 IsOutOfAction 기반이므로, 뼈가 물리로 넘어가는 순간과 히트박스가 켜지는
        // 순간이 같은 값을 본다. 갈라지면 "래그돌인데 조준이 안 잡히는" 방향으로 #857이 되살아난다.
        bool wantsRagdoll = m_incapacitation.IsOutOfAction || m_incapacitation.IsLaunched;

        // 접속 직후 이미 사망·비행 중이었다면 이번 원인은 건너뛴다 — 낙하는 이미 끝난 과거다.
        if (!m_polledOnce)
        {
            m_polledOnce = true;
            m_skipThisEpisode = wantsRagdoll;
        }

        if (wantsRagdoll)
        {
            m_sawCauseThisEpisode = true;
            m_awaitingCauseSeconds = 0f;
        }
        else if (IsRagdollActive && !m_sawCauseThisEpisode)
        {
            m_awaitingCauseSeconds += Time.deltaTime;
        }

        if (!wantsRagdoll)
        {
            m_skipThisEpisode = false;
            bool revivalIsReal =
                m_sawCauseThisEpisode || m_awaitingCauseSeconds >= k_causeSyncGraceSeconds;
            if (m_state == RagdollState.Ragdoll && revivalIsReal)
            {
                ExitToAnimator(blend: true); // 부활 — 정착 포즈에서 기상으로 잇는다
            }
            return;
        }

        if (!m_skipThisEpisode && m_state == RagdollState.Animated)
            EnterRagdoll(Vector3.zero); // 힘없이 무너지는 사망(진압봉·납치)·비행 진입. 임펄스는 각자 RPC로 따로 온다
    }

    private void LateUpdate()
    {
        // 블렌드는 LateUpdate에서 돈다 — 이 시점의 뼈 로컬값이 곧 애니메이터가 평가한 포즈다.
        // 완료되면 Animated로 돌아가고, 그때서야 AnimationDriver가 Down을 내려 기상 모션이 시작된다.
        if (m_state == RagdollState.BlendingToAnimator && m_blend.Tick(m_blendSeconds))
            m_state = RagdollState.Animated;

        // 스트림이 아직 몸을 쥐기 전이면 진입 시점의 자세를 붙든다 — <b>루트에 끌려가지 않게.</b>
        TickHoldPoseUntilStream();

        // 붙들기까지 끝난 뒤에 담는다 — 여기가 렌더 직전이라 화면에 보이는 값과 같다.
        TickSettleTrace();

        // ⚠ <b>LateUpdate여야 한다</b> — PlayerHeadLook이 시선을 얻는 시점이 여기다.
        TickEntryTrace();

        // // 물리가 실시간을 따라갔는지 적립한다 — 프레임 시간을 재는 계측이라 렌더 주기에 붙인다.
        // TickFallRate();
        // TickRopeTrace();
    }

    /// <summary>
    /// 원격에서 <b>첫 자세 패킷이 오기 전</b> 구간을 메운다 — 진입 시점의 월드 자세를 붙든다.
    /// 안 붙들면 키네마틱 뼈가 이미 움직이기 시작한 루트를 계층으로 따라가 몸이 통째로 딸려 간다.
    ///
    /// ⚠ <c>IsStreamDriven</c>의 반대로 묻지 않는다 — 그것이 거짓인 경우가 "아직 안 왔다"와 "정착까지
    /// 다 받고 끝났다" 둘인데 뜻이 정반대다. 이 대입이 안전한 이유는 원격의 뼈가 키네마틱이라는 것뿐이고,
    /// <b>"렌더 전용이라 PhysX로 안 간다"는 이유를 다시 붙이지 말 것</b> (docs §5).
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

    // "몸이 바닥에 있다"로 보는 골반 높이(m) — 이 안이면 루트 높이를 골반이 아니라 <b>지면</b>이
    // 준다(<see cref="TickCapsuleFollow"/>). NpcRagdoll의 같은 이름 상수와 같은 값이다.
    private const float k_groundedHipsHeight = 0.5f;

    // ---- 캡슐 추종 (#506 — 이 설계의 중심) ----

    /// <summary>
    /// 캡슐을 시체 밑으로 끌고 간다 — <b>권위 피어 전용</b>이고 <see cref="FixedUpdate"/>가 부른다.
    ///
    /// <b>왜 이게 중심인가.</b> 안 하면 캡슐이 사망 지점에 남았다가 정착 순간 1.15m를 텔레포트하고,
    /// 그 한 번의 늦은 점프가 이 기능의 거의 모든 버그의 뿌리였다. 매 스텝 따라가면 cm 단위 잔차로
    /// 줄고 원격은 점프 대신 연속 스트림을 받는다 — 실측과 옛 증상은 docs/player-ragdoll.md §4.
    /// </summary>
    internal void TickCapsuleFollow()
    {
        if (m_movement == null || m_rig == null || m_rig.Hips == null)
            return;

        // 골반 위치를 <b>3차원</b>으로 따라간다 — 수평만 맞추면 공중에 있는 동안 루트가 시체를
        // 대표하지 못한다(이름표·운반 조준·부활 히트박스가 전부 루트에 붙어 있다).
        Vector3 target = m_rig.Hips.position;

        // <b>단 몸이 바닥에 있으면 높이는 지면이 준다</b> — 정착 순간의 낙차를 없애기 위해 무너지는
        // 동안까지 넓혔다. 공중에서는 앉히지 않는다. (docs §4)
        bool haveGround = TryGroundUnder(m_rig.Hips.position, out Vector3 ground);
        bool bodyIsGrounded =
            m_settled
            || (haveGround && m_rig.Hips.position.y - ground.y <= k_groundedHipsHeight);

        if (haveGround && bodyIsGrounded)
            target.y = ground.y - CapsuleBottomOffset;

        // ⚠ <b>루트를 옮기기 전에 뼈를 잡아 두고, 아래에서 되돌린다</b> — 안 감싸면 진입 프레임에 몸
        // 전체가 골반 높이(약 0.9m)만큼 떠서 한 프레임 그려진다. 아래 FollowBodyYaw까지 감싼다.
        //
        // ⚠ <b>이 대입은 "렌더 전용"이 아니다 — 그 전제가 #759의 원인이었다.</b> 그래서 이 함수는
        // FixedUpdate에서만 돈다(스텝당 1회). 근거는 docs/player-ragdoll.md §5.
        m_rig.CapturePose();

        // 사망 중에는 CharacterController가 꺼져 있으므로 대입이 곧 이동이다. 스윕은 쓰지 않는다 —
        // 대리값은 지형을 존중할 이유가 없다 (docs §4).
        m_root.position = target;

        // ⚠ <b>yaw는 비행 중에만 따라간다</b> — 정착 후에도 돌리면 목이 비틀리고 되먹임 고리가 생긴다.
        // 상태가 아니라 <b>깃발</b>로 물어야 한다(시체도 끝까지 Ragdoll이다). (docs §11)
        if (!m_settled)
            FollowBodyYaw();

        // 위 CapturePose의 짝 — 루트를 옮기고 돌린 뒤 뼈를 원래 월드 포즈로 되돌린다.
        m_rig.RestoreCapturedPose();
    }

    // 목표 yaw로 <b>감쇠 추종</b>한다 — 슬램하면 루트에 매달린 것들(이름표·아이템)이 한 프레임에
    // 통째로 돈다. 프레임률 독립 지수 감쇠이고, ⚠ 기준은 <b>fixedDeltaTime</b>이다(#759 — 부르는
    // 쪽이 물리 스텝마다 돈다). 근거는 docs/player-ragdoll.md §11.
    private void FollowBodyYaw()
    {
        if (!m_alignRootYawToBody || !TryGetRootYaw(out float yaw))
            return;

        float current = m_root.eulerAngles.y;
        float eased = m_rootYawFollowSpeed > 0f
            ? Mathf.LerpAngle(
                current,
                yaw,
                1f - Mathf.Exp(-m_rootYawFollowSpeed * Time.fixedDeltaTime)
            )
            : yaw;

        m_root.rotation = Quaternion.Euler(0f, eased, 0f);
    }

    // 루트가 향해야 할 yaw — 리그가 내는 순수한 몸 방향에 기상 클립 보정을 얹은 값.
    private bool TryGetRootYaw(out float yaw)
    {
        if (!m_rig.TryGetBodyYaw(out yaw))
            return false;

        yaw += m_rootYawOffset;
        return true;
    }

    // ---- 정착 ----

    // 잠든 몸이 다시 움직이기 시작했다 — 스트림을 되살린다. (NpcRagdoll.ServerResumeFromSleep와 짝)
    private void ResumeFromSleep()
    {
        m_settled = false;
        m_elapsedInRagdoll = 0f;
        m_streamer?.ResumeStreaming();
    }

    /// <summary>
    /// 정착 — <b>몸도 루트도 건드리지 않는다.</b> 깃발을 세우고 스트림을 끊는 것뿐이다.
    /// 루트 정렬이 사라져도 되는 이유는 <see cref="TickCapsuleFollow"/>가 이미 매 스텝 하고 있기
    /// 때문이다. 옛 4단계 정착이 무엇이었고 왜 지웠는지는 docs/player-ragdoll.md §10.
    /// </summary>
    private void Settle()
    {
        if (m_settled)
            return;

        // DumpFallRate("정착");

        m_settled = true;
        DumpSettleTrace();

        // 스트림을 끊고 마지막 자세를 한 번 더 보낸다 — 원격의 종착 상태다.
        // ⚠ 좌표계는 바뀌지 않으므로 원격 화면은 변하지 않는다. 그것이 사양이다. (docs §10)
        m_streamer?.EndStreaming();

        // 사망(Die)은 부활 키트가 별도로 풀지만, 비행(Launched)은 정착 자체가 복구 신호다 — 여기서
        // 서버에 알린다(#815). 이 함수는 권위 피어에서만 도므로(Update의 HasMoveAuthority 게이트,
        // ForceSettle은 아직 호출부가 없다) 곧 그 오너가 통보를 보낸다.
        if (m_incapacitation != null && m_incapacitation.IsLaunched)
            m_incapacitation.RequestLaunchSettled();
    }

    // 루트 원점에서 캡슐 밑면까지의 높이 — 지면 점에 루트를 그대로 놓으면 캡슐이 떠서 출발한다 (docs §4).
    private float CapsuleBottomOffset =>
        m_controller == null ? 0f : m_controller.center.y - m_controller.height * 0.5f;

    // 골반 밑 지면 탐색 — 정착 자격 판정·정착 정렬·진입 계측이 <b>같은 것</b>을 쓴다.
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
