using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// NPC 사망 래그돌 (#571) — 죽은 몸(<see cref="NpcState.Dead"/>)의 애니메이터를 끄고 뼈를 물리에 넘긴다.
///
/// <b>이 클래스가 쥔 것은 "누가 위치를 쥐나"다.</b> 뼈를 물리에 넘기고 되돌리는 일은
/// <see cref="RagdollRig"/>가 한다 — 그쪽은 네트워크·권위·이동 프록시를 모르는 순수 물리라
/// 플레이어와 NPC가 그대로 공유한다. 여기 남은 것은 전부 <b>NPC 고유</b>다:
/// NavMeshAgent를 대리값으로 쓰는 것, 서버 권한 NetworkTransform, 사망 폴링.
///
/// <b>표현 계층 전용이다.</b> 뼈를 <b>매 틱</b> 동기화하지 않는다 — 위치 판정은 서버 트랜스폼이
/// 계속 쥔다. 그래서 NetworkBehaviour가 아니고, 피어로 나가야 하는 한 줄(얼린 자세)만
/// <see cref="NpcDeath"/>를 통해 쏜다.
///
/// <b>권위가 두 구간으로 갈린다 (#571).</b> 이게 이 클래스를 읽는 열쇠다:
/// <list type="bullet">
///   <item><b><c>Ragdoll</c> — 뼈가 주인이다.</b> 물리가 몸을 만들고 루트가 그 밑을 따라간다
///   (<see cref="TickRootFollow"/>). 각 피어가 자기 로컬 물리를 돌리므로 결과가 조금씩 갈리고,
///   그 표류만 스트리밍된 루트로 잡아 준다(<see cref="TickAlignBonesToRoot"/>).</item>
///   <item><b><c>Frozen</c> — 루트가 주인이다.</b> 정착하는 순간 전 뼈를 키네마틱으로 얼린다.
///   키네마틱 뼈는 <b>부모 트랜스폼을 그대로 따라가므로</b>(동적일 때와 정반대) 루트를 옮기면 몸이
///   따라온다 — 유치장 수감이 <c>transform.position</c> 한 줄이 되는 이유다.</item>
/// </list>
///
/// <b>얼리면 동기화할 것이 없어진다.</b> 자세가 상수가 되므로 <b>얼리는 순간 1회</b>만 보내면
/// (뼈 로컬 회전 + 골반 로컬 위치, <see cref="RagdollRig.CaptureLocalPose"/>) 그 뒤로는 루트 하나만
/// 복제하면 된다. 매 틱 정렬로 뼈를 끌어당기던 예전 구조는 <b>원격의 리지드바디가 영영 잠들지 못해
/// 바닥에서 비벼졌고</b>, 루트의 지면 판정 오차가 그대로 몸의 높이 오차가 됐다 — 둘 다 사라진다.
///
/// ⚠ <b>얼린 시체는 스스로 바닥을 찾지 않는다.</b> 루트를 벽·바닥 안에 놓으면 그대로 박힌다.
/// 그래서 얼리는 시점은 "물리가 이미 정착시킨 순간"이고, 명시적 배치는 바닥에 스냅된 좌표를 받는다
/// (<see cref="JailZone.RandomRestPointInRoom"/>).
///
/// <b><see cref="PlayerRagdoll"/>과 갈리는 두 가지</b>
/// <list type="bullet">
///   <item><b>권위가 서버다.</b> 플레이어는 오너가 캡슐로 시체를 따라가고 그 루트를 스트리밍하지만,
///   NPC에는 오너가 없어 <b>서버가 그 역할</b>을 한다. 서버 외 전원이 원격이다.</item>
///   <item><b>부활이 없다.</b> 시체는 되살아나지 않으므로 기상 블렌드·기상 클립용 루트 yaw 정렬이
///   전부 필요 없고, <b>부활 오인 문제(506 §9-19)가 통째로 사라진다</b> — "살아 있는데 래그돌이면
///   부활"이라는 전제 자체가 없다.</item>
/// </list>
///
/// <b>붙이는 곳: NPC 프리팹 루트</b>(<see cref="NpcController"/>와 같은 오브젝트).
/// ⚠ <see cref="RagdollRig"/>는 여기가 아니라 <b>리그를 직속 자식으로 가진 오브젝트</b>(NPC는
/// <c>Model</c>)에 붙는다 — 그래서 <c>GetComponent</c>가 아니라 <c>GetComponentInChildren</c>으로 찾는다.
/// 부착은 <c>RagdollSetup</c>이 한다.
/// </summary>
public class NpcRagdoll : MonoBehaviour
{
    // 지면을 못 찾아도 결국은 정착시키는 최후 배수 — 맵 밖으로 떨어져 나간 시체가 Ragdoll 상태에
    // 영원히 갇히지 않게 하는 안전장치. 이 경로로 오면 시체는 허공에 굳지만 상태 기계는 계속 돈다.
    private const float k_lostBodyTimeoutFactor = 4f;

    // 원격의 "당겨오기가 끝났는지" 잔차 판정은 없어졌다 (#571) — 정착은 이제 권위 피어만 하고,
    // 원격은 그 결과(자세)를 받아 갈아끼우므로 스스로 정착 자격을 물을 일이 없다.

    private enum RagdollState
    {
        Animated, // 평시 — 전 Rigidbody 키네마틱, 애니메이터가 포즈를 쥔다
        Ragdoll, // 물리 중 — 애니메이터 정지, 무너지거나 날아가는 구간. <b>뼈가 루트를 끈다</b>
        Frozen, // 정착 완료 — 전 뼈 키네마틱으로 얼린다. <b>루트가 뼈를 끈다</b> (권위 반전, 아래 클래스 주석)
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

    [Tooltip("원격 피어가 착지한 시체를 서버 위치로 당겨오는 속도(m/s) — 수평만. " +
             "보정은 동력이 아니라 표류 방지다. 몸을 움직이는 것은 각 피어의 로컬 물리다")]
    [SerializeField] private float m_alignPullSpeed = 1.5f;

    [Tooltip("원격 피어가 비행 중 시체를 서버 골반으로 당겨오는 속도(m/s) — 3차원. " +
             "⚠ <b>시체의 비행 속도와 짝이다</b> — 폭발 임펄스를 연결하면 여기도 같이 올릴 것")]
    [SerializeField] private float m_flightAlignPullSpeed = 16f;

    [Tooltip("원격 시체가 스트리밍된 루트에서 이만큼(m) 벗어나면 보정을 스냅으로 바꾼다 — 안전망이다. " +
             "정상 동작에서는 걸리지 않아야 하고, 자주 걸리면 입력(임펄스)이 어긋난 것을 봐야 한다")]
    [SerializeField] private float m_alignSnapDistance = 2.5f;

    private NpcController m_owner;
    private RagdollRig m_rig; // 뼈 한 벌 — 물리 조작 전부를 여기 위임한다. 리그 소유자(Model)에 붙어 있다
    private RagdollRope m_rope; // 관절 밧줄 — 리그와 같은 오브젝트에 붙는다(RequireComponent)
    private Animator m_animator;
    private NavMeshAgent m_agent;

    private RagdollState m_state = RagdollState.Animated;
    private float m_stillTimer;
    private float m_elapsedInRagdoll;


    // 늦게 접속했는데 대상이 이미 죽어 있던 경우 — 이번 사망은 래그돌을 건너뛴다.
    // 그때의 물리 낙하는 "죽는 순간"이 아니라 이미 끝난 과거라, 재생하면 시체가 뒤늦게 한 번 더 무너진다.
    private bool m_skipThisEpisode;
    private bool m_polledOnce;

    /// <summary>래그돌이 애니메이터로부터 포즈를 빼앗고 있는가 — 표현 계층이 물러나는 판정에 쓴다.</summary>
    public bool IsRagdollActive => m_state != RagdollState.Animated;

    // 위치 권한 — 서버(또는 세션 없는 오프라인 Play)만 루트를 옮길 수 있다.
    // 클라가 옮겨봤자 서버 권한 NetworkTransform이 되돌린다.
    private bool HasMoveAuthority => !m_owner.IsSpawned || m_owner.IsServer;

    private void Awake()
    {
        m_owner = GetComponent<NpcController>();
        m_agent = GetComponent<NavMeshAgent>();

        // ⚠ 리그는 <b>자식</b>에 있다 — NPC는 몸이 Synty 프리팹의 중첩 인스턴스(Model)라 리그가
        // 그 안에 들어 있고, RagdollRig는 리그를 직속 자식으로 가진 오브젝트에 붙기 때문이다.
        // 플레이어처럼 GetComponent로 찾으면 항상 null이다.
        m_rig = GetComponentInChildren<RagdollRig>(true);
        if (m_rig == null)
        {
            Debug.LogWarning(
                $"NpcRagdoll: RagdollRig를 찾지 못해 사망 래그돌을 끈다 — {name}. "
                    + "Tools > Ragdoll > Finish Setup 을 이 프리팹에 돌릴 것",
                this
            );
            enabled = false;
            return;
        }

        m_rig.EnsureCollected(); // Awake 순서는 보장되지 않는다 — 아래에서 뼈를 요구한다

        // 밧줄도 리그와 같은 오브젝트(Model)에 있다 — RagdollRope가 RagdollRig를 RequireComponent한다.
        m_rope = m_rig.GetComponent<RagdollRope>();

        // 애니메이터도 리그 쪽(Model)에 있다.
        m_animator = GetComponentInChildren<Animator>(true);
    }

    // ---- 밧줄 파사드 (#571 시체 끌기) ----
    //
    // 실물은 RagdollRope가 쥔다. 여기 파사드를 두는 이유는 <see cref="PlayerRagdoll"/>과 같다:
    // 호출부(NpcRopeDrag)가 "래그돌인 대상에게 밧줄을 묶는다"를 표현하기 때문이다 — 밧줄 컴포넌트를
    // 직접 찾게 하면 "래그돌이 아닐 때는 묶으면 안 된다"는 조건과 "리그가 Model에 있다"는 배치 지식이
    // 둘 다 호출부로 새어 나간다.

    /// <summary>
    /// 시체에 밧줄을 묶는다 — <b>각 피어가 자기 로컬 시체에</b> 건다. 표현·물리 계층이다.
    ///
    /// <b>얼린 몸을 먼저 녹인다</b> (#571). 관절 밧줄은 골반 Rigidbody를 <b>물리로</b> 끄는 것이라
    /// 키네마틱인 채로는 장력이 하나도 안 걸린다(<see cref="RagdollRope.Attach"/>가 <c>WakeAll</c>까지
    /// 부르는 이유가 그것이다). 여기가 <c>Frozen → Ragdoll</c> 복귀의 유일한 문이고, 줄을 놓으면
    /// 정착 판정이 다시 돌아 알아서 얼어붙는다.
    /// </summary>
    /// <param name="carrier">밧줄을 쥔 쪽. 보통 운반자의 손 앵커.</param>
    public void BeginRopePull(Transform carrier)
    {
        Unfreeze();
        m_rope?.Attach(carrier);
    }

    /// <summary>밧줄을 푼다 — 내려놓기·줄 끊김·운반자 소실. <b>멱등</b>(안 묶여 있으면 무동작).
    /// 얼리지 않는다 — 놓은 몸은 마저 무너져야 하므로 정착 판정에 맡긴다.</summary>
    public void EndRopePull() => m_rope?.Detach();

    // ---- 얼림 / 녹임 (#571 권위 반전) ----

    /// <summary>지금 얼어 있는가 — 참이면 루트를 옮기는 것만으로 몸이 따라온다.</summary>
    public bool IsFrozen => m_state == RagdollState.Frozen;

    // 얼린다 — 전 뼈를 키네마틱으로 놓아 루트의 자식으로 되돌린다. 자세는 지금 그대로 굳는다.
    // 부르는 곳은 둘: 정착(ServerFreezeInPlace)과 원격의 포즈 수신(ApplyFrozenPose).
    private void Freeze()
    {
        m_rig.SetKinematic(true);
        m_state = RagdollState.Frozen;
    }

    // 애니메이터를 떼어낸다 — <b>얼리기 전에 반드시</b>. 키네마틱 뼈는 트랜스폼이 진실인데
    // 애니메이터도 같은 트랜스폼을 쓰므로, 켜 둔 채 얼리면 다음 프레임에 대기 포즈가 시체를
    // 덮어써 <b>죽은 몸이 서 있게 된다.</b> 이미 꺼져 있으면 무동작.
    private void StopAnimator()
    {
        if (m_state != RagdollState.Animated)
            return;

        if (m_animator != null)
            m_animator.enabled = false;

        m_rig.SetSkinsAlwaysVisible(true);
    }

    /// <summary>
    /// 녹인다 — 얼린 몸을 다시 물리에 넘긴다. 얼어 있지 않으면 무동작. (#571)
    ///
    /// <b>루트를 건드리지 않는다.</b> 뼈는 지금 서 있는 자리에서 그대로 동적으로 바뀌므로 화면은
    /// 이어지고, 그 순간부터 루트가 다시 몸을 따라간다(<see cref="TickRootFollow"/>).
    /// 얼린 채 루트로 끌려다니며 PhysX가 유도해 둔 속도는 <see cref="RagdollRig.SetKinematic"/>이
    /// 물리로 돌려주는 순간 지운다 — 안 지우면 놓는 순간 시체가 날아간다(그쪽 주석의 실측).
    /// </summary>
    public void Unfreeze()
    {
        if (m_state != RagdollState.Frozen)
            return;

        m_state = RagdollState.Ragdoll;
        m_stillTimer = 0f;
        m_elapsedInRagdoll = 0f;

        m_rig.SetKinematic(false);
    }

    /// <summary>
    /// 얼린 자세를 받아 그대로 재현한다 — <b>원격 피어 전용</b> 진입점. (#571)
    /// <see cref="NpcDeath"/>가 서버의 정착 브로드캐스트를 받아 부른다.
    ///
    /// 여기서 로컬 물리의 결과를 <b>버린다.</b> 각 피어가 따로 굴린 몸은 조금씩 다른 자리에
    /// 누워 있는데, 그 차이를 매 프레임 당겨서 좁히던 것이 예전 구조다(정렬). 이제는 서버가
    /// 확정한 자세로 한 번에 갈아끼우고 얼린다 — 그 뒤로는 어긋날 여지가 없다.
    /// </summary>
    public void ApplyFrozenPose(Quaternion[] boneRotations, Vector3 hipsLocalPosition)
    {
        if (m_rig == null || !m_rig.IsValid)
            return;

        // 아직 무너지지도 않은 몸(늦게 접속해 이번 사망을 건너뛴 피어)도 여기서 시체가 된다.
        StopAnimator();

        // <b>얼리는 것이 먼저다.</b> 동적인 채로 자세를 쓰면 다음 물리 스텝이 PhysX의 포즈로 덮는다 —
        // 키네마틱으로 바꾸고 나서야 트랜스폼이 진실이 된다. 아래가 실패해도 얼어 있는 편이 낫다
        // (그 피어의 로컬 물리 자세로 굳을 뿐, 계속 흔들리지는 않는다).
        Freeze();

        if (!m_rig.ApplyLocalPose(boneRotations, hipsLocalPosition))
        {
            Debug.LogWarning(
                $"NpcRagdoll: 받은 자세의 뼈 수가 맞지 않아 버린다 — {name} "
                    + $"(받음 {(boneRotations == null ? 0 : boneRotations.Length)}, 이 피어 {m_rig.BoneCount})",
                this
            );
        }
    }

    /// <summary>
    /// 시체를 통째로 옮긴다 — <b>서버(또는 오프라인) 전용.</b> 부르는 곳은 유치장 수감
    /// (<see cref="NpcCustody.SendCorpseToJail"/>) 하나다. (#571)
    ///
    /// <b>얼리고 나서 옮긴다.</b> 얼린 뼈는 루트의 키네마틱 자식이라 루트를 옮기면 딸려 오고,
    /// 그 루트는 NetworkTransform이 이미 복제하고 있다 — 그래서 <b>원격에 따로 보낼 것이 없다.</b>
    /// (예전에는 뼈를 피어마다 평행이동시키고 정렬을 0.5초 재워야 했다)
    ///
    /// 끌고 온 시체는 밧줄 때문에 녹아 있으므로(<see cref="BeginRopePull"/>) 여기서 다시 얼린다.
    /// 정착 판정을 기다리지 않는 이유는 <b>기다릴 이유가 없어서</b>다 — 어디에 눕힐지는 유치장이
    /// 이미 정했고, 그 좌표는 바닥에 스냅돼 있다.
    /// </summary>
    /// <param name="position">시체가 놓일 지면 지점 — 루트(발밑) 기준이다.</param>
    public void ServerPlaceCorpse(Vector3 position)
    {
        if (m_rig == null || !m_rig.IsValid)
        {
            transform.position = position;
            return;
        }

        if (!IsFrozen)
            ServerFreezeInPlace();

        transform.position = position;
    }

    // ---- 진입 ----

    /// <summary>
    /// 래그돌 진입 — <b>멱등이다.</b> 이미 물리 중이면 임펄스만 누적하고, 정착했으면 무동작.
    ///
    /// 멱등이어야 하는 이유는 도착 순서다. 지금은 사망 폴링 하나뿐이라 순서 문제가 없지만,
    /// 폭발 임펄스를 연결하면(후속) 사망 사실과 임펄스가 서로 다른 오브젝트에서 와
    /// 순서가 보장되지 않는다 — 플레이어 쪽이 그 순서로 두 번 물렸다(506 §9-19).
    /// </summary>
    /// <param name="impulse">밀려나는 속도(m/s). 힘없이 무너지는 사망은 <see cref="Vector3.zero"/>.</param>
    public void EnterRagdoll(Vector3 impulse)
    {
        if (m_rig == null || !m_rig.IsValid)
            return;

        if (m_state == RagdollState.Ragdoll)
        {
            m_rig.ApplyImpulse(impulse); // 늦게 도착한 임펄스 — 누적한다
            return;
        }

        if (m_state != RagdollState.Animated)
            return; // 이미 정착했다 — 다시 날리지 않는다

        StopAnimator(); // 상태를 바꾸기 전에 — 이 함수는 Animated일 때만 도는 멱등 함수다

        m_state = RagdollState.Ragdoll;
        m_stillTimer = 0f;
        m_elapsedInRagdoll = 0f;

        // 에이전트는 서버에서 NpcDeath가 이미 껐고 클라에서는 애초에 꺼져 있다(NpcController.OnNetworkSpawn).
        // 그래도 여기서 한 번 더 확인한다 — 켜져 있으면 매 프레임 NavMesh 위로 끌어내려 시체가 못 눕는다.
        if (m_agent != null && m_agent.enabled)
            m_agent.enabled = false;

        m_rig.SetKinematic(false);
        m_rig.ApplyImpulse(impulse);
    }

    // ---- 매 프레임 ----

    private void Update()
    {
        PollDeath();

        // 얼어 있으면 볼 것이 없다 — 루트가 주인이고 자세는 상수다. 이 조기 반환이 곧
        // "정착한 시체는 매 프레임 아무 비용도 쓰지 않는다"는 뜻이다.
        if (m_state != RagdollState.Ragdoll)
            return;

        // 무너지는 동안에는 <b>뼈가 주인</b>이라 루트가 그 밑을 따라간다 — 플레이어의
        // TickCapsuleFollow에 대응한다. <b>이 컴포넌트가 직접 돌린다</b>: NpcController.Update는
        // 클라에서 즉시 return하고 서버에서도 사망 게이트에서 끊기므로 저기서는 부를 자리가 없다.
        //
        // 얼린 뒤에는 돌지 않는다 — 그때부터는 반대로 루트가 뼈를 끈다.
        TickRootFollow();

        // 정착 판정은 <b>권위 피어만</b> 한다 (#571). 예전에는 각 피어가 자기 물리로 따로 정착했고,
        // 그래서 피어마다 다른 자세로 굳은 뒤 그 차이를 매 프레임 정렬로 좁혀야 했다.
        // 이제는 서버가 정착시켜 그 자세를 한 번 뿌리고, 원격은 받아서 갈아끼운다.
        if (!HasMoveAuthority)
            return;

        m_elapsedInRagdoll += Time.deltaTime;

        m_stillTimer = m_rig.AverageSpeed <= m_settleSpeedThreshold
            ? m_stillTimer + Time.deltaTime
            : 0f;

        if (m_stillTimer < m_settleHoldSeconds && m_elapsedInRagdoll < m_settleTimeoutSeconds)
            return;

        if (!HasGroundUnderHips()
            && m_elapsedInRagdoll < m_settleTimeoutSeconds * k_lostBodyTimeoutFactor)
            return;

        ServerFreezeInPlace();
    }

    private void LateUpdate()
    {
        // 원격의 시체를 스트리밍된 루트에 맞춘다. LateUpdate인 이유는 NetworkTransform이 이번
        // 프레임에 적용한 루트 위치를 읽어야 한 프레임 늦지 않기 때문이다.
        //
        // <b>무너지는 동안에만 돈다</b> (#571). 얼린 뒤에는 뼈가 루트의 키네마틱 자식이라 계층이
        // 정확히 붙여 주고, 자세는 서버가 뿌린 그대로다 — 좁힐 차이가 없다.
        // 예전에는 정착 후에도 계속 당겼는데, 그것이 원격 리지드바디를 <b>영영 못 자게 만들어</b>
        // 바닥에서 비벼지는 원인이었다.
        if (!HasMoveAuthority && m_state == RagdollState.Ragdoll)
            TickAlignBonesToRoot();
    }

    // 사망 여부를 폴링한다 — 상태 enum이 동기화 값이라 전 피어가 같은 값을 본다.
    //
    // 이벤트가 아니라 폴링인 이유는 플레이어 쪽과 같다(PlayerRagdoll.PollDeath): 표현 계층이
    // 동기화 값을 매 프레임 읽는 편이 도착 순서에 기대지 않아 단순하다.
    //
    // <b>부활 분기가 없다.</b> 시체는 되살아나지 않으므로 "살아 있는데 래그돌이면 부활"이라는
    // 판정 자체가 필요 없고, 그 전제가 원격에서 깨져 생기던 문제(506 §9-19)도 함께 사라진다.
    private void PollDeath()
    {
        bool dead = m_owner.Death.IsDead;

        // 접속 직후 이미 죽어 있었다면 이번 사망은 건너뛴다 — 낙하는 이미 끝난 과거다.
        if (!m_polledOnce)
        {
            m_polledOnce = true;
            m_skipThisEpisode = dead;
        }

        if (!dead)
            return;

        if (!m_skipThisEpisode && m_state == RagdollState.Animated)
            EnterRagdoll(Vector3.zero); // 힘없이 무너지는 사망. 폭발은 임펄스를 따로 준다(후속)
    }

    // ---- 루트 추종 (서버 전용) ----

    /// <summary>
    /// 루트를 시체 밑으로 끌고 간다 — <b>서버(또는 오프라인) 전용.</b>
    ///
    /// <b>왜 필요한가.</b> 이걸 안 하면 루트는 죽은 자리에 그대로 남고 시체만 굴러간다. 루트는
    /// 스캔·이름표·상호작용 콜라이더가 매달린 자리이자 <b>NetworkTransform이 복제하는 유일한 값</b>이라,
    /// 루트가 시체를 대표하지 못하면 원격 피어는 시체가 어디 있는지 알 방법이 없다.
    ///
    /// 스윕을 쓰지 않고 위치를 직접 대입한다 — 대리값은 지형을 존중할 이유가 없다. 에이전트가
    /// 꺼져 있으므로(EnterRagdoll) 대입이 곧 이동이다.
    ///
    /// <b>yaw는 건드리지 않는다.</b> 플레이어는 기상 클립이 "루트 전방을 향해 누워 있다"를 전제해
    /// 루트를 몸 방향으로 돌려야 했지만(FollowBodyYaw), 시체는 일어나지 않으므로 그 이유가 없다.
    /// 돌리면 오히려 손해다 — 리지드바디가 없는 뼈(Neck·손·발)만 계층을 따라 돌아 목이 비틀린다.
    ///
    /// <b>얼린 뒤에는 돌지 않는다</b> (#571 권위 반전). 무너지는 동안에만 뼈가 주인이고, 정착해
    /// 얼고 나면 반대로 루트가 뼈를 끈다. 밧줄로 끌 때 몸만 가고 루트가 남던 문제(실측: 시체 8.26m,
    /// 루트 0.00m)는 <b>밧줄이 몸을 녹이기</b> 때문에 그대로 막힌다 — 끄는 동안은 항상 이 상태다.
    /// </summary>
    private void TickRootFollow()
    {
        if (!HasMoveAuthority || m_rig.Hips == null)
            return;

        // 골반 높이를 그대로 쓴다 — 지면 보정은 얼리는 순간 한 번만 한다(ServerFreezeInPlace).
        // 매 프레임 지면을 찾아 루트 높이를 고치던 예전 처리는 <b>원격에서 그 오차가 곧 몸의 높이
        // 오차</b>가 됐다(정렬이 루트를 따라가므로). 지금은 원격이 자세를 통째로 받으므로 필요 없다.
        transform.position = m_rig.Hips.position;
    }

    private bool HasGroundUnderHips() => TryGroundUnder(m_rig.Hips.position, out _);

    // ---- 원격 정렬 ----

    /// <summary>
    /// 원격 피어의 시체를 스트리밍된 루트에 맞춘다 — <b>원격(= 서버 아닌 전원) 전용.</b>
    ///
    /// 서버의 루트가 골반을 따라오므로(<see cref="TickRootFollow"/>) <b>스트리밍된 루트가 곧 서버
    /// 골반의 위치다</b> — 뼈를 따로 동기화하지 않고도 원격이 서버의 궤적을 받는다.
    ///
    /// 리그 루트 오프셋으로는 못 고친다 — <b>동적 리지드바디는 부모 트랜스폼을 따르지 않는다.</b>
    /// 그래서 뼈의 <c>position</c>에 직접 델타를 더한다(<see cref="RagdollRig.TranslateBy"/>).
    ///
    /// ⚠ <b>동력이 아니라 표류 방지다.</b> 몸을 움직이는 것은 각 피어의 로컬 물리이고, 여기서 하는
    /// 일은 그 결과가 루트에서 서서히 벗어나는 것을 막는 것뿐이다. 상한을 올려 "끌어오게" 만들려던
    /// 시도가 플레이어 쪽에서 발산으로 끝났다(506 §9-16).
    /// </summary>
    private void TickAlignBonesToRoot()
    {
        bool grounded = HasGroundUnderHips();

        Vector3 delta = transform.position - m_rig.Hips.position;

        // 착지 후에만 높이를 뺀다 — 그때는 각 피어의 지형 충돌이 높이의 주인이고 같은 지형이라
        // 편차가 작다. 반대로 <b>공중에서는 3차원으로 맞춘다</b>: 높이를 정해 줄 접촉이 없어
        // 서버 골반의 고도를 받아야 원격도 같은 궤적을 그린다.
        if (grounded)
            delta.y = 0f;

        float distance = delta.magnitude;
        if (distance < 1e-4f)
            return;

        float maxStep = (grounded ? m_alignPullSpeed : m_flightAlignPullSpeed) * Time.deltaTime;

        // 잔차가 임계를 넘으면 스냅한다 — 이미 눈에 띄게 틀렸으므로 포즈 보존이 의미가 없고,
        // 느린 상한으로는 영구히 못 따라잡는다. 안전망이라 정상 동작에서는 걸리지 않아야 한다.
        if (distance > m_alignSnapDistance)
            maxStep = distance;
        if (distance > maxStep)
            delta *= maxStep / distance;

        // <b>몸 전체를 같은 델타로 옮긴다.</b> 포즈는 이미 이 피어의 로컬 물리가 만들고 있고,
        // 여기서 하는 일은 그 궤적을 서버 것에 맞추는 것뿐이다. 뼈마다 다르게 옮기면 포즈가 깨진다.
        // (골반만 옮겨 흐느적임을 만들려던 시도는 질량비 때문에 실패한다 — 506 §10-5)
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

    // ---- 정착 = 얼림 (#571 권위 반전) ----

    /// <summary>
    /// 지금 자세 그대로 얼린다 — <b>서버(또는 오프라인) 전용.</b> 정착 판정과 유치장 배치가 부른다.
    ///
    /// <b>"정착 = 물리를 계속 돌리되 그대로 두기"에서 "정착 = 자세를 확정하고 멈추기"로 바뀌었다.</b>
    /// 얼리면 세 가지가 한꺼번에 끝난다: 바닥에서 비벼질 접촉이 사라지고, 뼈가 루트의 자식으로
    /// 되돌아와 <b>루트만 옮기면 몸이 따라오고</b>, 자세가 상수가 되어 원격에 1회만 보내면 된다.
    ///
    /// 순서를 지키지 않으면 몸이 튄다. <b>키네마틱 뼈는 루트를 따라가므로</b>(동적일 때와 정반대):
    /// <list type="number">
    ///   <item>전 뼈의 월드 포즈를 캡처</item>
    ///   <item>전 rb를 키네마틱으로 전환 — 이 순간부터 뼈가 루트에 매인다</item>
    ///   <item>루트를 골반 밑 지면으로 이동 (뼈가 딸려 간다)</item>
    ///   <item>캡처한 월드 포즈를 뼈에 다시 적용 → 화면은 그대로, 루트만 지면에 앉았다</item>
    /// </list>
    ///
    /// 예전에는 ⑤로 <b>다시 물리에 풀어 줬다</b>(<c>RestToPhysics</c> — 플레이어 쪽에는 아직 남아 있다).
    /// 그 근거는 "골반을 붙들면 몸이
    /// 찢어진다"(506 §9-7)였는데, 그건 <b>일부만</b> 키네마틱으로 붙들 때의 이야기다 — 전부 한꺼번에
    /// 얼리면 서로 당기는 관절이 없어 자세가 그대로 굳는다.
    /// </summary>
    private void ServerFreezeInPlace()
    {
        if (!HasMoveAuthority)
            return;

        StopAnimator(); // 무너지지 않은 몸을 그대로 얼리는 경로(유치장 배치)가 있다

        m_rig.CapturePose();
        Vector3 landedHips = m_rig.Hips.position;

        m_rig.SetKinematic(true);
        transform.position = GroundUnder(landedHips);
        m_rig.RestoreCapturedPose();

        m_state = RagdollState.Frozen;

        ServerBroadcastPose();
    }

    // 얼린 자세를 원격에 1회 보낸다 — 세션이 아니면(오프라인 Play) 보낼 곳이 없다.
    // 배선은 NpcDeath가 쥔다: 이 컴포넌트는 NetworkBehaviour가 아니다(클래스 주석).
    private void ServerBroadcastPose()
    {
        if (m_owner == null || !m_owner.IsSpawned)
            return;

        if (m_poseBuffer == null || m_poseBuffer.Length != m_rig.BoneCount)
            m_poseBuffer = new Quaternion[m_rig.BoneCount];

        if (m_rig.CaptureLocalPose(m_poseBuffer, out Vector3 hipsLocal))
            m_owner.Death.ServerSendFrozenPose(m_poseBuffer, hipsLocal);
    }

    // 보낼 자세를 담는 버퍼 — 시체당 한 번 쓰지만 매번 새로 할당할 이유도 없다.
    private Quaternion[] m_poseBuffer;

    // 정착 정렬용 지면 — 여기까지 왔다면 보통 지면이 있다(Update가 없으면 정착을 미룬다).
    // 못 찾는 경우는 맵 밖으로 떨어진 시체뿐이고, 그때는 골반 높이를 그대로 쓴다.
    //
    // ⚠ 플레이어의 CapsuleBottomOffset 보정이 여기 없다 — 그건 CharacterController 캡슐 밑면이
    // 루트 원점보다 위에 있어서 필요했던 것이고, NPC 루트 원점은 발밑이라 지면 점이 곧 루트다.
    private Vector3 GroundUnder(Vector3 hipsPosition)
    {
        bool hitGround = TryGroundUnder(hipsPosition, out Vector3 point);
        return hitGround ? point : hipsPosition;
    }

    // 골반 밑 지면 탐색 — 정착 자격 판정과 정착 정렬이 공유한다.
    // 탐색 거리를 짧게 잡는 것이 중요하다: 길게 쏘면 얇은 실내 바닥을 뚫고 아래층을 찾아내
    // 시체가 정착하는 순간 한 층 밑으로 순간이동한다.
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
