using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// NPC 래그돌 — 죽거나 기절한 몸의 애니메이터를 끄고 뼈를 물리에 넘긴다. <b>표현 계층 전용</b>이라
/// NetworkBehaviour가 아니다(피어로 나가는 자세는 <see cref="RagdollPoseStreamer"/>가 전담).
///
/// <b>시뮬레이션은 하나뿐이다.</b> 권위 피어(서버)만 물리를 굴리고 원격의 뼈는 전부 키네마틱이라
/// 받은 자세를 입히기만 한다.
///
/// <b>상태 셋이 이 클래스를 읽는 열쇠다:</b>
/// <list type="bullet">
///   <item><c>Animated</c> — 애니메이터가 포즈를 쥔다</item>
///   <item><c>Ragdoll</c> — <b>뼈가 주인</b>. 물리가 몸을 만들고 루트가 그 밑을 따라간다</item>
/// </list>
///
/// <b>시체도 끝까지 <c>Ragdoll</c>이다.</b> 정착은 상태가 아니라 "물리가 잠들었다"는 국면이라
/// (<see cref="m_settled"/>) 뼈는 동적으로 남는다 — 그래서 밟히거나 폭발에 밀리면 알아서 다시
/// 움직이고, 스트림이 그것을 이어 받는다. 예전에는 여기에 <c>Frozen</c>이 있어 뼈를 키네마틱으로
/// 되돌렸고, 그 전이가 정착 순간의 어색함과 유치장 발사 사고의 뿌리였다.
///
/// <b>설계 근거·실측·되살리면 안 되는 것들은 <c>docs/npc-ragdoll.md</c>에 있다.</b>
/// 이 파일을 고치기 전에 그쪽의 해당 항목을 먼저 볼 것 — 특히 §4(순서가 중요한 자리)와 §5(지웠다).
///
/// 붙이는 곳: <b>NPC 프리팹 루트</b>(<see cref="NpcController"/>와 같은 오브젝트).
/// <see cref="RagdollRig"/>는 리그를 직속 자식으로 가진 <c>Model</c>에 붙으므로 자식에서 찾는다.
/// </summary>
public class NpcRagdoll : MonoBehaviour
{
    // 지면을 못 찾아도 결국 정착시키는 최후 배수 — 맵 밖으로 떨어진 시체가 Ragdoll에 갇히지 않게.
    private const float k_lostBodyTimeoutFactor = 4f;

    // "몸이 바닥에 있다"로 보는 골반 높이(m). 이 안이면 루트 높이를 골반이 아니라 지면이 준다.
    private const float k_groundedHipsHeight = 0.5f;

    // 기상 시 NavMesh를 다시 찾는 반경(m) — "누운 자리 바로 밑"을 뜻하는 값이라 상수다.
    private const float k_navMeshSampleDistance = 2f;

    private enum RagdollState
    {
        Animated,
        Ragdoll,
    }

    [Header("정착 판정")]
    [Tooltip("물리가 스스로 안 잠드는 몸을 강제로 재우는 시간(초) — 지형에 껴서 영원히 떨리는 " +
             "경우의 안전장치다.\n\n" +
             "<b>평시 정착은 이 값과 무관하다</b> — PhysX가 알아서 재운다. 여기까지 왔다는 것은 " +
             "몸이 무언가에 물려 떨고 있다는 뜻이고, 그때만 Sleep()을 대신 불러 준다")]
    [SerializeField] private float m_settleTimeoutSeconds = 5f;

    [Header("기상 블렌드")]
    [Tooltip("래그돌 자세에서 애니메이터 자세로 섞는 시간(초) — 0이면 즉시 복귀. " +
             "줄을 풀거나 기절이 끝나 일어설 때 몸이 한 프레임에 튀는 것을 없앤다")]
    [SerializeField] private float m_blendSeconds = 0.3f;

    [Header("정착 후 정렬")]
    [Tooltip("시체 밑 지면을 찾는 레이캐스트 마스크 — 지형(Default). 래그돌 뼈는 다른 레이어라 안 걸린다")]
    [SerializeField] private LayerMask m_groundMask = 1;

    [Tooltip("골반 아래로 지면을 찾는 거리(m). 짧게 잡을 것 — 길면 얇은 실내 바닥을 뚫고 아래층을 " +
             "찾아내 시체가 한 층 밑으로 순간이동한다")]
    [SerializeField] private float m_groundProbeDistance = 1.5f;

    private NpcController m_owner;
    private RagdollRig m_rig; // 뼈 한 벌 — 물리 조작 전부를 위임한다. 리그 소유자(Model)에 붙어 있다
    private RagdollRope m_rope; // 관절 밧줄 — 리그와 같은 오브젝트
    private Animator m_animator;
    private NavMeshAgent m_agent;
    private NpcAnimationDriver m_driver; // 기상 시점의 진실값 — IsProne
    private Unity.Netcode.Components.NetworkTransform m_rootNetTransform; // 복제되는 유일한 트랜스폼
    private RagdollPoseStreamer m_streamer; // 자세가 피어로 나가는 유일한 통로

    private RagdollPoseBlend m_blend; // 기상 블렌드 — 래그돌 자세 → 지금 애니메이터가 놓는 자세
    private bool m_blending;

    private RagdollState m_state = RagdollState.Animated;
    private float m_elapsedInRagdoll;

    // 물리가 잠들어 자세 스트림을 끊었는가 — <b>상태가 아니라 국면이다.</b>
    //
    // ⚠ 예전의 <c>Frozen</c>과 다르다. 그것은 뼈를 키네마틱으로 되돌리는 <b>되돌릴 수 없는 전이</b>라
    // 상태 셋의 하나였는데, 이것은 "지금 잠들어 있다"를 적어 둔 것뿐이라 뼈는 여전히 동적이다.
    // 그래서 밟히거나 폭발에 밀리면 물리가 알아서 깨어나고 이 깃발만 내려간다.
    private bool m_settled;

    // 늦게 접속했는데 대상이 이미 쓰러져 있던 경우 — 이번 에피소드는 건너뛴다.
    private bool m_skipThisEpisode;
    private bool m_polledOnce;

    /// <summary>래그돌이 애니메이터로부터 포즈를 빼앗고 있는가 — 표현 계층이 물러나는 판정에 쓴다.</summary>
    public bool IsRagdollActive => m_state != RagdollState.Animated;

    // 위치 권한 — 서버(또는 세션 없는 오프라인 Play)만 루트를 옮길 수 있다.
    private bool HasMoveAuthority => !m_owner.IsSpawned || m_owner.IsServer;

    private void Awake()
    {
        m_owner = GetComponent<NpcController>();
        m_agent = GetComponent<NavMeshAgent>();
        m_driver = GetComponent<NpcAnimationDriver>();
        m_rootNetTransform = GetComponent<Unity.Netcode.Components.NetworkTransform>();

        // ⚠ 리그·애니메이터·밧줄은 전부 자식(Model)에 있다 — GetComponent로 찾으면 항상 null이다.
        m_rig = GetComponentInChildren<RagdollRig>(true);
        if (m_rig == null)
        {
            Debug.LogWarning(
                $"NpcRagdoll: RagdollRig를 찾지 못해 래그돌을 끈다 — {name}. "
                    + "Tools > Ragdoll > Finish Setup 을 이 프리팹에 돌릴 것",
                this
            );
            enabled = false;
            return;
        }

        m_rig.EnsureCollected(); // 같은 오브젝트의 Awake 순서는 보장되지 않는다
        m_rope = m_rig.GetComponent<RagdollRope>();
        m_animator = GetComponentInChildren<Animator>(true);

        // 섞는 대상은 리그 최상단 이하 전 트랜스폼 — 물리를 안 받는 뼈(목·손가락·발)까지 넣어야
        // 블렌드 첫 프레임에 목과 손이 튀지 않는다.
        m_blend = new RagdollPoseBlend(m_rig.BoneRoot);

        // 스트리머는 루트에 있다 — NGO가 비활성 GameObject의 NetworkBehaviour를 스폰에서 제외한다.
        m_streamer = GetComponent<RagdollPoseStreamer>();
        if (m_streamer == null)
        {
            Debug.LogWarning(
                $"NpcRagdoll: RagdollPoseStreamer가 없다 — {name}. 원격에 자세가 가지 않아 "
                    + "시체가 진입 자세로 굳는다. RagdollSetup을 이 프리팹에 돌릴 것",
                this
            );
            return;
        }

        m_streamer.OnSettledPoseReceived += HandleSettledPoseReceived;
    }

    private void OnDestroy()
    {
        if (m_streamer != null)
            m_streamer.OnSettledPoseReceived -= HandleSettledPoseReceived;
    }

    // ---- 매 프레임 ----

    private void Update()
    {
        PollRagdollTriggers();

        if (m_state != RagdollState.Ragdoll)
            return;

        // 원격은 자세를 받을 뿐이다 — 물리가 돌지 않으므로 잠들 몸도, 따라갈 골반도 없다.
        if (!HasMoveAuthority)
            return;

        // 잠든 뒤에는 <b>깨어났는지만</b> 본다. 밟히거나 폭발에 밀리면 PhysX가 스스로 깨우므로
        // 이 한 줄이 그 모든 경로를 받는다 — 깨우는 쪽마다 알림을 심을 필요가 없다.
        //
        // 레이캐스트도 자세 캡처도 하지 않는다(<see cref="TickRootFollow"/>를 건너뛴다) — 잠든 몸은
        // 움직이지 않으니 루트가 다시 따라갈 곳이 없고, 시체가 쌓이는 라운드에서 이 생략이
        // 프레임당 비용을 시체 수에 비례하지 않게 만든다.
        if (m_settled)
        {
            if (!m_rig.AllAsleep)
                ServerResumeFromSleep();

            return;
        }

        TickRootFollow();

        // 끌리는 동안에는 재우지 않는다 — 끌리는 몸은 계속 움직이니 어차피 안 잠들지만,
        // 타임아웃까지 흐르면 끌고 가는 중에 Sleep()이 걸린다. 놓는 순간부터 다시 재야 하므로
        // 경과 시간을 0으로 되돌린다.
        // ⚠ IsAttached가 아니라 IsBeingCarried다 — 운반자가 사라져도 관절은 남는다.
        if (m_rope != null && m_rope.IsBeingCarried)
        {
            m_elapsedInRagdoll = 0f;
            return;
        }

        m_elapsedInRagdoll += Time.deltaTime;

        // <b>정착은 물리가 정한다.</b> 전 뼈가 하나도 안 남고 잠들어야 참이다 — 팔 하나가 아직
        // 흔들리고 있으면 그 팔이 전체를 붙잡는다. 옛 구조는 뼈 <b>평균</b> 속도를 봐서, 몸통이
        // 멈추면 흔들리는 팔을 키네마틱으로 한 프레임에 세워 버렸다(그것이 "갑자기 굳는" 어색함).
        if (m_rig.AllAsleep)
        {
            ServerSettleInPlace();
            return;
        }

        if (m_elapsedInRagdoll < m_settleTimeoutSeconds)
            return;

        // 아직 공중이다 — 여기서 재우면 <b>떠 있는 시체</b>가 된다. 폭발에 크게 날아간 몸은 5초
        // 뒤에도 비행 중일 수 있다. 다만 맵 밖으로 떨어진 몸이 영원히 Ragdoll에 갇히지 않게
        // 무한정 기다리지는 않는다.
        if (!HasGroundUnderHips()
            && m_elapsedInRagdoll < m_settleTimeoutSeconds * k_lostBodyTimeoutFactor)
            return;

        // 타임아웃 — 지형에 물려 스스로 못 잠드는 몸이다. 대신 재운다.
        //
        // ⚠ <b>키네마틱 얼림이 아니라 물리 수면이다.</b> 그래서 밟거나 밧줄을 걸면 위의
        // <c>AllAsleep</c> 검사가 그대로 깨어남을 받는다 — 강제로 재운 몸도 예외가 아니다.
        m_rig.SleepAll();
        ServerSettleInPlace();
    }

    private void LateUpdate()
    {
        // 기상 블렌드는 LateUpdate여야 한다 — 이번 프레임에 애니메이터가 놓은 자세가 곧 목표라,
        // 여기서 읽어야 재생 중인 기상 클립을 향해 살아있는 목표로 수렴한다.
        if (m_blending && m_blend.Tick(m_blendSeconds))
            m_blending = false;

        TickHoldPoseUntilStream();

        // TickRiseProbe(); // 진단 ⑦ (임시)
    }

    /// <summary>
    /// 원격에서 첫 자세 패킷이 오기 전 구간을 메운다 — 진입 시점의 월드 자세를 붙든다.
    /// 안 붙들면 키네마틱 뼈가 이미 움직이기 시작한 루트를 계층으로 따라가 몸이 통째로 뜬다.
    /// </summary>
    private void TickHoldPoseUntilStream()
    {
        if (m_streamer == null || HasMoveAuthority || m_state != RagdollState.Ragdoll)
            return;

        // ⚠ !IsStreamDriven으로 묻지 않는다 — 그것이 거짓인 경우가 "아직 안 왔다"와 "정착까지 다
        // 받고 끝났다" 둘인데, 후자에서 메우면 정착 자세가 진입 시점 자세로 덮인다.
        if (!m_streamer.IsAwaitingFirstPose)
            return;

        m_rig.RestoreCapturedPose();
    }

    /// <summary>
    /// 루트를 시체 밑으로 끌고 간다 — 서버 전용. 루트는 이름표·콜라이더가 매달린 자리이자
    /// NetworkTransform이 복제하는 유일한 값이라, 시체를 대표하지 못하면 원격이 위치를 알 수 없다.
    /// </summary>
    private void TickRootFollow()
    {
        if (!HasMoveAuthority || m_rig.Hips == null)
            return;

        // ⚠ 루트를 옮기기 전에 뼈를 잡아 두고 옮긴 뒤 되돌린다 — 안 감싸면 진입 프레임에 몸 전체가
        // 골반 높이(약 0.9m)만큼 떠서 한 프레임 그려진다. 이 대입은 렌더 전용이다.
        m_rig.CapturePose();

        Vector3 target = m_rig.Hips.position;

        // 몸이 바닥에 있으면 루트 높이는 지면이 준다 — 골반 높이를 쓰는 것은 공중에 있는 동안만이다.
        // 정착은 이제 아무것도 옮기지 않으므로, 루트가 지면에 있는 것은 <b>여기가 유일한 보장</b>이다.
        if (TryGroundUnder(target, out Vector3 ground) && target.y - ground.y <= k_groundedHipsHeight)
            target.y = ground.y;

        transform.position = target;

        m_rig.RestoreCapturedPose();
    }

    // ---- 진입 / 이탈 ----

    // 래그돌이어야 하는지를 폴링한다 — 읽는 값이 전부 동기화 값이라 전 피어가 같은 답을 얻는다.
    // 이벤트가 아닌 이유와 에피소드 리셋의 근거는 docs/npc-ragdoll.md §3.
    private void PollRagdollTriggers()
    {
        bool wants = WantsRagdoll();

        if (!m_polledOnce)
        {
            m_polledOnce = true;
            m_skipThisEpisode = wants;
        }

        if (!wants)
        {
            // ⚠ 에피소드가 끝나면 반드시 내린다 — 안 내리면 이후 모든 기절 래그돌을 영구히 건너뛴다.
            m_skipThisEpisode = false;
            ExitRagdoll();
            return;
        }

        if (!m_skipThisEpisode && m_state == RagdollState.Animated)
            EnterRagdoll(Vector3.zero); // 힘없이 무너진다. 폭발은 임펄스를 따로 준다
    }

    /// <summary>
    /// 지금 이 몸이 래그돌이어야 하는가 — 진입과 이탈을 같은 식 하나로 답한다.
    /// ⚠ 진입 조건과 유지 조건을 갈라 묻는다 — 누워 있어야 하는 이유가 진입 이유보다 오래 간다.
    /// </summary>
    private bool WantsRagdoll()
    {
        // 사망은 영구다 — 시체는 일어나지 않으므로 이 분기가 곧 "이탈 없음"이다.
        if (m_owner.Death.IsDead)
            return true;

        // 이미 래그돌이면 묻는 것은 하나다 — 아직 바닥에 있어야 하는가. IsProne이 답을 통째로 든다
        // (기절해 누움 · 줄에 눕혀짐 · 일어나기 대기가 전부 참이고, 기상 모션이 나가는 순간 거짓).
        if (IsRagdollActive)
            return m_driver == null || m_driver.IsProne;

        // 아직 애니메이터가 쥐고 있다 — 새로 태울 이유가 있는지 묻는다.
        // ⚠ IsStunned가 아니라 HasStunOverlay다 — 넉백 착지 KO는 에이전트 소유권이 정면으로 부딪힌다.
        if (!m_owner.Stun.HasStunOverlay)
            return false;

        // 기상 모션이 이미 나간 뒤라면 태우지 않는다 — 일어나는 몸을 다시 눕힌다.
        return m_driver == null || m_driver.IsProne;
    }

    /// <summary>
    /// 래그돌 진입 — <b>멱등</b>. 이미 물리 중이면 임펄스만 누적하고, 정착했으면 무동작.
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

        SampleAnimatorClock(); // 진단 ④ (임시) — ⚠ 애니메이터를 끄기 전에 읽어야 한다

        // ⚠ 애니메이터를 끄기 전에 — 안에서 강제 평가를 한다.
        SnapToAnimatorPoseIfBlending();

        StopAnimator(); // 상태를 바꾸기 전에 — Animated일 때만 도는 멱등 함수다

        m_state = RagdollState.Ragdoll;
        m_settled = false;
        m_elapsedInRagdoll = 0f;

        // 블렌드 중에 다시 쓰러지면 섞던 것을 버린다 — 안 버리면 무너지는 몸을 애니메이터 자세로
        // 도로 끌어당긴다.
        m_blending = false;

        // LogUnstreamedBones(); // 진단 ④ (임시) — ⚠ 못박기 전 값이어야 피어 간 차이가 보인다

        // 말단 뼈(손·발·손가락)만 바인드로 못박는다 — 전 피어가 각자 부른다. 스트림이 안 싣는 뼈라
        // 그냥 두면 피어마다 애니메이터가 마지막에 놓은 손발 모양이 남는다.
        // ⚠ 몸 모양을 만드는 체인 뼈는 여기서 손대지 않는다 — 그쪽은 스트림이 싣는다(그쪽 주석의 사고).
        m_rig.RestoreUnstreamedBonesToBind();

        // 진단 ⑥ (임시) — ⚠ <b>물리에 넘기기 전</b>이어야 한다. 애니메이터가 남긴 자세가 이미
        // 얼마나 파고들어 있었는지가 이 로그의 요점이다.
        // if (HasMoveAuthority)
            // LogFloorPenetration("진입");

        // LogBoneLengthDrift("진입"); // 진단 ⑧ (임시)

        m_riseProbeFrame = -1; // 진단 ⑦ (임시) — 진입 로그가 이 프레임을 이미 찍었다

        ReleaseAgentForRagdoll();
        ReleaseBonesToPhysics();
        m_rig.ApplyImpulse(impulse);

        m_streamer?.BeginStreaming(); // 권위가 아니면 스스로 무동작이다
    }

    /// <summary>
    /// 기상 블렌드 중이면 <b>애니메이터를 한 번 강제로 평가해</b> 그 자세를 물리에 넘긴다.
    /// 블렌드 중이 아니면 무동작.
    ///
    /// <b>블렌드 중간 자세는 물리에 넘기면 안 된다.</b> 블렌드는 <b>로컬 회전</b>을 섞으므로
    /// (<see cref="RagdollPoseBlend"/>) 중간 경로가 양 끝점 어느 쪽도 아닌 자세다 — 회전 보간은
    /// 팔다리 <b>위치</b>를 보존하지 않는다. 실측(2026-08-19)으로 블렌드 24프레임 중
    /// <b>13~17프레임(55~70%)에서 발끝이 바닥 19.7cm 아래로 내려간다.</b> 끝점 둘은 관통 0인데
    /// 사이만 파고드는 봉우리다.
    ///
    /// 그 프레임에 몸을 물리에 넘기면 <b>정강이 캡슐이 박힌 채로 출발</b>하고, 겹침 탈출 상한
    /// (0.5m/s, 중력을 빼면 실질 0.3m/s = 스텝당 6mm)으로는 빠져나오지 못한다. 발·발끝에는
    /// 애초에 콜라이더가 없어 그쪽은 영영 안 나온다.
    ///
    /// ⚠ <b>"블렌드를 끝낸다"로는 안 된다.</b> 이 함수가 도는 <c>Update</c> 시점에는 애니메이터가
    /// 아직 이번 프레임을 평가하지 않아 뼈에 <b>지난 프레임의 섞인 자세</b>가 남아 있다
    /// (평가는 Update와 LateUpdate 사이다). <see cref="RagdollPoseBlend.Tick"/>은 "지금 뼈에 있는
    /// 값"을 목표로 삼으므로 t=1로 끝내면 그 섞인 자세를 그대로 확정할 뿐이다. 클립 자세를
    /// <b>직접 받아야</b> 한다.
    ///
    /// ⚠ <b>정착 자세로 되감지 않는다.</b> 물리가 만든 자세라 관통이 없는 것은 맞지만, 기상이
    /// 이미 <c>ServerReattachToNavMesh</c>로 루트를 워프했을 수 있어 그때의 <b>로컬</b> 자세를
    /// 되돌리면 옮겨진 루트 기준으로 몸이 엉뚱한 곳에 놓인다. 클립 자세는 루트 기준으로 authoring된
    /// 것이라 루트가 어디 있든 맞는다.
    ///
    /// <b>전 피어가 부른다</b> — 원격도 이 자세를 첫 패킷 전까지 붙들므로
    /// (<see cref="TickHoldPoseUntilStream"/>) 같은 자세여야 한다.
    /// </summary>
    private void SnapToAnimatorPoseIfBlending()
    {
        if (!m_blending || m_animator == null || !m_animator.enabled)
            return;

        m_animator.Update(0f); // 시간을 진행시키지 않고 지금 클립 자세만 뼈에 쓴다
    }

    /// <summary>
    /// 애니메이터에게 몸을 돌려준다 — 기절에서 깨어나는 유일한 문. 이미 <c>Animated</c>면 무동작.
    /// 뼈·애니메이터는 전 피어가 되돌리고, NavMesh 재부착은 권위 피어만 한다.
    /// </summary>
    private void ExitRagdoll()
    {
        if (m_state == RagdollState.Animated)
            return;

        // 아무것도 보내지 않고 끊는다 — 기상에는 종착 자세가 없다(보내면 원격에서 기상 블렌드와
        // 래그돌 자세가 같은 프레임을 두고 싸운다). 전 피어가 각자 부르므로 RPC가 필요 없다.
        m_streamer?.StopStreaming();

        // 키네마틱이 먼저다 — 동적인 채로 포즈를 쓰면 다음 물리 스텝이 PhysX 결과로 덮는다.
        m_rig.SetKinematic(true);

        // ⚠ 블렌드 출발점은 지금 이 래그돌 자세다 — 아래 두 줄보다 반드시 먼저 잡는다.
        bool blending = m_blendSeconds > 0f && m_blend != null && m_blend.IsValid;
        if (blending)
            m_blend.Begin();

        // ⚠ 뼈 길이를 되돌린다 — 애니메이터는 회전만 쓰므로 물리가 늘려 놓은 localPosition을 고쳐
        // 주지 않는다. 안 되돌리면 기절할 때마다 누적되다 사지가 늘어나며 바닥을 뚫는다.
        m_rig.RestoreBindPose();

        if (m_animator != null)
            m_animator.enabled = true;

        m_rig.SetSkinsAlwaysVisible(false);

        m_state = RagdollState.Animated;
        m_blending = blending;
        m_settled = false;
        m_elapsedInRagdoll = 0f;

        if (HasMoveAuthority)
            ServerReattachToNavMesh();

        // BeginRiseProbe(); // 진단 ⑦ (임시)
    }

    // 뼈를 물리로 놓아준다 — 단, 원격에서는 놓아주지 않는다. 원격은 자세를 받아 입히기만 하므로
    // 물리가 아예 돌지 않고, 그래서 위반될 관절이 원리적으로 없다.
    private void ReleaseBonesToPhysics()
    {
        if (!HasMoveAuthority)
        {
            m_rig.SetKinematic(true);
            m_rig.CapturePose(); // 첫 패킷이 오기 전까지 붙들 자세 (TickHoldPoseUntilStream)
            return;
        }

        m_rig.SetKinematic(false);
    }

    // 애니메이터를 떼어낸다 — ⚠ 뼈를 물리에 넘기기 전에 반드시. 켜 둔 채 넘기면 다음 프레임에
    // 대기 포즈가 시체를 덮어써 죽은 몸이 서 있게 된다. 이미 꺼져 있으면 무동작.
    private void StopAnimator()
    {
        if (m_state != RagdollState.Animated)
            return;

        if (m_animator != null)
            m_animator.enabled = false;

        m_rig.SetSkinsAlwaysVisible(true);
    }

    // ---- 정착 / 기상 ----
    //
    // <b>정착은 상태 전이가 아니다.</b> 뼈는 처음부터 끝까지 동적으로 남고, 여기서 하는 일은
    // "물리가 잠들었다"를 적어 두고 스트림을 끊는 것뿐이다. 그래서 정착은 <b>화면을 바꾸지 않는다</b>
    // — 그것이 사양이다.

    /// <summary>
    /// 물리가 잠들었다 — 서버 전용. 스트림을 끊고 마지막 자세를 한 번 더 보낸다.
    ///
    /// <b>몸을 건드리지 않는다.</b> 키네마틱 전환도, 뼈 길이 복원도, 지면 재정렬도 없다 — 잠든
    /// 몸은 이미 물리가 놓은 자리에 있고 원격은 이미 그 자세를 그리고 있다. 옛 구조가 이 자리에서
    /// 하던 일들(<c>SetKinematic</c>·<c>RestoreBindBoneLengths</c>·최저뼈 재정렬)은 전부
    /// <b>키네마틱 얼림이 만든 문제를 되받는 것</b>이었고, 얼림이 없으니 함께 사라졌다.
    /// 근거는 docs/npc-ragdoll.md §4.
    /// </summary>
    private void ServerSettleInPlace()
    {
        if (!HasMoveAuthority || m_settled)
            return;

        StopAnimator(); // 무너지지 않은 몸을 그대로 재우는 경로(유치장 배치)가 있다

        // 그 경로는 EnterRagdoll을 안 지나므로 말단을 여기서 한 번 더 못박는다. 이미 못박혀 있으면
        // 무동작이다. ⚠ 말단은 리지드바디가 없어 물리가 덮지 않으므로 이 대입은 남는다.
        m_rig.RestoreUnstreamedBonesToBind();

        m_settled = true;

        // LogFloorPenetration("정착"); // 진단 ⑥ (임시)
        // LogBoneLengthDrift("정착"); // 진단 ⑧ (임시)

        // 스트림을 끊고 마지막 자세를 한 번 더 보낸다 — 이것이 원격의 종착 상태다.
        // ⚠ <b>좌표계는 바뀌지 않는다 — 스트리밍과 같은 월드다.</b> 그래서 원격은 이 패킷을 받아도
        // 화면이 변하지 않는다. 옛 구조는 여기서 로컬로 갈아타 몸을 루트에 매달았고, 그 전환이
        // 정착 순간의 점프였다.
        m_streamer?.EndStreaming();

        // 진단 ⑨ (임시) — 방금 보낸 <b>종착 자세</b>가 원격에서 어떤 몸이 되는가. 이 시체는 여기서
        // 멈추므로, 이 한 줄이 곧 클라 화면에 남는 차이다.
        // LogRemoteReconstructionError("정착");
    }

    // 잠든 몸이 다시 움직이기 시작했다 — 서버 전용. 밟힘·폭발·밧줄 어느 쪽이든 여기로 모인다.
    // 깨우는 쪽은 물리가 이미 했으므로(<c>WakeUp</c>) 여기서는 스트림만 되살린다.
    private void ServerResumeFromSleep()
    {
        m_settled = false;
        m_elapsedInRagdoll = 0f;
        m_streamer?.ResumeStreaming();
    }

    /// <summary>
    /// 잠든 시체를 깨운다 — <b>멱등</b>. 밧줄을 묶는 쪽이 부른다.
    ///
    /// 관절 장력만으로는 잠든 몸이 안 깨어날 수 있어 명시적으로 깨운다. 예전의 <c>Unfreeze</c>와
    /// 달리 <b>상태를 바꾸지 않는다</b> — 뼈는 애초에 키네마틱이 된 적이 없으므로 되돌릴 것이 없다.
    /// </summary>
    public void WakeCorpse()
    {
        if (m_state != RagdollState.Ragdoll || !HasMoveAuthority)
            return;

        m_rig.WakeAll();
        ServerResumeFromSleep();
    }

    // 원격이 정착 자세를 받았다 — 자세는 스트리머가 이미 입혔으므로 여기서는 배선만 맞춘다.
    // StopAnimator가 먼저인 것은 도착 순서 때문이다(사망 폴링보다 이 패킷이 먼저 온 피어가 있다).
    //
    // ⚠ <b>뼈를 건드리지 않는다.</b> 원격의 뼈는 진입 때부터 키네마틱이고 스트리머가 정착 뒤에도
    // 매 프레임 자세를 못박으므로, 여기서 얼릴 것도 되돌릴 것도 없다.
    private void HandleSettledPoseReceived()
    {
        StopAnimator();

        // 권위 쪽 ServerSettleInPlace와 짝 — 이 피어가 EnterRagdoll을 안 지났어도 말단을 맞춘다.
        m_rig.RestoreUnstreamedBonesToBind();

        // 도착 순서가 뒤집힌 피어를 위한 보정 — 사망 폴링이 아직 안 왔으면 상태가 Animated다.
        m_state = RagdollState.Ragdoll;
        m_settled = true;

        // LogRemoteSettledClearance(); // 진단 ⑤ (임시)
        // LogFloorPenetration("정착·원격"); // 진단 ⑥ (임시)
    }

    // ---- 밧줄 파사드 ----
    //
    // 실물은 RagdollRope가 쥔다. 파사드를 두는 이유는 호출부(NpcRopeDrag)가 "래그돌인 대상에게
    // 밧줄을 묶는다"를 표현하기 때문이다 — 직접 찾게 하면 배치 지식이 호출부로 샌다.

    /// <summary>관절 밧줄의 길이(m) — 리그가 아직 안 잡혔으면 0.</summary>
    public float RopeLength => m_rope != null ? m_rope.Length : 0f;

    /// <summary>
    /// 시체에 밧줄을 묶는다. 잠들어 있으면 먼저 깨운다 — 관절 장력만으로는 안 깨어날 수 있다.
    ///
    /// ⚠ <b>원격에는 아무것도 할 것이 없다.</b> 예전에는 여기서 전 피어가 <c>Unfreeze</c>를 불러
    /// 원격을 <c>Frozen</c>에서 꺼내야 했고, 그래서 권위 가드가 그 뒤에 있었다. 지금 원격의 몸은
    /// 스트림이 쥐고 있을 뿐이고 새 스냅샷이 오면 그대로 따라가므로 꺼낼 상태가 없다.
    /// </summary>
    /// <param name="carrier">밧줄을 쥔 쪽. 보통 운반자의 손 앵커.</param>
    public void BeginRopePull(Transform carrier)
    {
        if (!HasMoveAuthority)
            return;

        WakeCorpse();
        m_rope?.Attach(carrier);

        // LogBoneLengthDrift("밧줄부착"); // 진단 ⑧ (임시)
    }

    /// <summary>이 사람이 쥔 가닥만 푼다 — 줄다리기에서 한 명이 손을 뗄 때. <b>멱등</b>.</summary>
    public void EndRopePull(Transform carrier)
    {
        m_rope?.Detach(carrier);

        // LogBoneLengthDrift("밧줄해제"); // 진단 ⑧ (임시)
    }

    /// <summary>걸린 밧줄을 전부 푼다 — 내려놓기·줄 끊김·운반자 소실. <b>멱등</b>.
    /// 재우지 않는다 — 놓은 몸은 마저 무너져야 하고, 다 무너지면 물리가 알아서 잠든다.</summary>
    public void EndRopePull()
    {
        m_rope?.Detach();

        // LogBoneLengthDrift("밧줄전체해제"); // 진단 ⑧ (임시)
    }

    // ---- 배치 (유치장 수감 / 퇴장) ----

    /// <summary>
    /// 시체를 통째로 옮긴다 — 서버 전용. 부르는 곳은 <see cref="NpcCustody.SendCorpseToJail"/> 하나다.
    ///
    /// <b>루트와 뼈를 같은 델타로 따로 옮긴다.</b> 뼈는 동적이라 루트를 따라오지 않으므로 저절로
    /// 딸려오는 것이 없다 — 대신 <see cref="RagdollRig.TranslateBy"/>가 전 뼈를 <b>한 델타로</b>
    /// 옮기므로 관절 위반이 0이고, 도착지에서 솔버가 메울 것이 없다.
    ///
    /// ⚠ 예전에는 얼린 뼈가 루트의 키네마틱 자식이라 루트 한 줄로 왔고, 그래서 <b>도착지에서
    /// 녹이면 시체가 발사됐다</b>(실측 264·417 m/s — docs/npc-ragdoll.md §5). 그 사고의 원인은
    /// 얼린 동안 벌어진 관절 위반이었고, 여기서는 위반이 생기지 않으므로 녹인 채로 끝낸다.
    /// <b>도착지에서 다시 무너져 잠드는 것이 사양이다</b> — 배치점이 어긋나도 몸이 알아서 눕는다.
    /// </summary>
    /// <param name="position">시체가 놓일 지면 지점 — 루트(발밑) 기준이다.</param>
    public void ServerPlaceCorpse(Vector3 position)
    {
        if (m_rig == null || !m_rig.IsValid)
        {
            transform.position = position;
            return;
        }

        // ⚠ 줄을 먼저 끊는다 — 묶인 채 수백 m 옮기면 관절이 그만큼 위반되고 솔버가 그것을 메우며
        // 시체를 발사한다(실측 237 m/s). 호출부가 무엇을 놓쳤든 상관없게 여기서 한 번 더 보장한다.
        EndRopePull();

        // ⚠ 아직 애니메이터가 쥐고 있으면 먼저 물리로 넘긴다 — <b>뼈가 키네마틱인 채로 아래
        // <see cref="RagdollRig.TranslateBy"/>를 부르면 두 번 옮겨진다.</b> 키네마틱 뼈는 루트의
        // 자식으로 딸려오므로 루트 대입만으로 이미 델타가 실리고, 거기에 한 번 더 얹히기 때문이다.
        //
        // 사망 폴링보다 배치가 먼저 도착한 프레임에서만 지나는 길이다(옛 구조에서 이 자리가
        // <c>if (!IsFrozen) ServerFreezeInPlace()</c>였던 것과 같은 이유).
        if (m_state != RagdollState.Ragdoll)
            EnterRagdoll(Vector3.zero);

        // LogBoneLengthDrift("배치전"); // 진단 ⑧ (임시)

        Vector3 delta = position - transform.position;

        transform.position = position;

        // ⚠ <b>두 대입이 서로 더해지지 않는 것은 <c>Physics.autoSyncTransforms = 0</c>이기 때문이다.</b>
        // 위의 루트 대입은 다음 물리 스텝까지 물리 포즈에 닿지 않으므로, 여기서 읽는 <c>rb.position</c>은
        // 아직 옮기기 전 값이고 델타가 한 번만 실린다. 그 설정을 켜면 뼈가 <b>두 배로</b> 날아간다.
        m_rig.TranslateBy(delta);

        ServerTeleportNetTransforms();

        // ⚠ <b>자세를 다시 쏜다 — 안 쏘면 원격의 몸이 죽은 자리에 남는다.</b>
        //
        // 원격은 정착 뒤에도 마지막 스냅샷의 <b>월드</b> 골반으로 매 프레임 몸을 못박는다
        // (<see cref="RagdollPoseStreamer.EndStreaming"/>). 그래서 루트만 옮기면 이름표와
        // 콜라이더만 유치장으로 가고 몸은 그대로다.
        //
        // 보간 없이 나가야 한다 — 평범한 스냅샷으로 보내면 원격이 출발지와 도착지 사이를
        // 보간하며 시체가 맵을 가로질러 날아간다(실측 573.94m을 21프레임).
        m_streamer?.SendTeleportPose();

        // 진단 ⑧⑨ (임시) — ⚠ <b>방금 보낸 그 자세</b>를 재는 자리다. 아래 WakeAll이 물리를 깨우면
        // 다음 스텝부터 몸이 달라지므로 여기서 재야 원격이 받은 것과 같은 자세를 잰다.
        // LogBoneLengthDrift("배치후");
        // LogRemoteReconstructionError("배치후");

        // 옮긴 몸은 깨어난 것으로 본다 — 도착지에서 다시 무너져 잠드는 과정이 원격에도 흘러야 한다.
        // 이미 잠들어 있었다면 다음 Update가 곧바로 다시 재우고 종착 패킷을 한 번 더 보낸다.
        m_rig.WakeAll();
        ServerResumeFromSleep();
    }

    // 순간이동을 보간 없이 원격에 보낸다 — 안 쓰면 원격이 이 거리를 여러 프레임에 걸쳐 보간한다
    // (실측 573.94m을 21프레임). 보내는 것은 루트 하나뿐이고 뼈는 계층으로 딸려 온다.
    private void ServerTeleportNetTransforms()
    {
        if (m_owner == null || !m_owner.IsSpawned)
            return; // 오프라인 Play에서는 위의 대입이 곧 이동의 전부다

        if (m_rootNetTransform != null)
            m_rootNetTransform.Teleport(transform.position, transform.rotation, transform.localScale);
    }

    // ---- NavMeshAgent ----

    /// <summary>
    /// 에이전트에게서 몸을 넘겨받는다 — 눕기 전에 반드시.
    /// ⚠ 기절은 <b>끄지 않고 손만 뗀다</b>(통째로 끄면 그 사이 상태 전이가 예외로 깨진다).
    /// 사망은 반대로 통째로 끈다 — 시체는 NavMesh로 돌아가지 않는다.
    /// </summary>
    private void ReleaseAgentForRagdoll()
    {
        if (m_agent == null)
            return;

        if (m_owner.Death.IsDead)
        {
            if (m_agent.enabled)
                m_agent.enabled = false;
            return;
        }

        m_agent.updatePosition = false;
        m_agent.updateRotation = false;
    }

    /// <summary>
    /// 깨어난 몸을 NavMesh에 다시 붙인다 — 서버 전용. 규칙은 "뗀 쪽이 되돌린다"라
    /// <see cref="ReleaseAgentForRagdoll"/>의 짝이 여기다.
    /// </summary>
    private void ServerReattachToNavMesh()
    {
        if (m_agent == null)
            return;

        // ⚠ 플래그는 무조건 되돌린다 — 아래 가드보다 먼저다. 떼어 둔 채 넘기면 그 NPC는 영영 걷지 못한다.
        m_agent.updatePosition = true;
        m_agent.updateRotation = true;

        // 위치는 다른 구간이 쥐고 있으면 손대지 않는다 — 그쪽이 끝날 때 자기 자리에서 붙인다.
        if (m_owner.Rope.IsRoped || m_owner.Knockback.IsKnockedBack)
            return;

        // ⚠ 샘플에 실패해도 켠다 — 회수 안전망이 enabled == false인 구간을 건너뛴다.
        m_agent.enabled = true;

        // 기준 마스크로 착지점을 찾는다 — 현재 통행 마스크는 배회 중 도로가 빠져 있어 차도 위에
        // 쓰러진 몸이 깨어날 자리를 못 찾는다. 못 가는 영역(Jail)에 Warp되면 경로가 안 잡혀 고착된다.
        if (NavMesh.SamplePosition(
                transform.position,
                out NavMeshHit ground,
                k_navMeshSampleDistance,
                m_owner.BaseAreaMask
            ))
        {
            m_agent.Warp(ground.position);
        }

        if (!m_agent.isOnNavMesh)
        {
            Debug.LogWarning(
                "NpcRagdoll: 기절에서 깨어난 자리를 NavMesh에 붙이지 못했다 — 회수 대기: "
                    + $"{name} @{transform.position.ToString("F1")}",
                this
            );
        }
    }

    // ---- 지면 탐색 ----

    private bool HasGroundUnderHips() => TryGroundUnder(m_rig.Hips.position, out _);

    /// <summary>
    /// 시체 밑 여유 — <b>최저뼈Y − 바닥Y.</b> 음수면 몸이 바닥을 파고들었다.
    /// 바닥을 못 찾으면 <see cref="float.NaN"/>이다 — 호출부는 "판단 불가"로 다뤄야 한다.
    /// </summary>
    private float LowestBoneClearance()
    {
        if (!TryGroundUnder(m_rig.Hips.position, out Vector3 ground))
            return float.NaN;

        return m_rig.LowestBoneY - ground.y;
    }


    // 지면을 못 찾으면 골반 높이를 그대로 쓴다.
    private Vector3 GroundUnder(Vector3 hipsPosition)
        => TryGroundUnder(hipsPosition, out Vector3 point) ? point : hipsPosition;

    // 골반 밑 지면 탐색 — 정착 자격 판정과 정착 정렬이 <b>같은 것</b>을 써야 한다(다르면 그 차이가
    // 얼리는 순간 낙차로 남는다). 탐색 거리를 짧게 잡을 것 — 근거는 m_groundProbeDistance 툴팁.
    // 진단 ⑥ 보조 (임시) — 그 자리에서 지면으로 잡히는 <b>오브젝트 이름</b>. 위 탐색과 같은 레이다.
    private string TryGroundColliderUnder(Vector3 from)
    {
        const float k_probeLift = 0.5f;

        return Physics.Raycast(
            from + Vector3.up * k_probeLift,
            Vector3.down,
            out RaycastHit hit,
            k_probeLift + m_groundProbeDistance,
            m_groundMask,
            QueryTriggerInteraction.Ignore
        )
            ? hit.collider.name
            : "없음";
    }

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

    // ---- 진단 (임시 — 기상 중 사망 시 클라 시체가 어긋나는 증상 추적용. 갈리면 통째로 지운다) ----
    //
    // ⚠ <b>2026-08-20 — 호출부를 전부 주석 처리했다. 코드는 남긴다.</b> 토글이 없어 항상 켜지는
    // 구조라 다른 팀원이 Play하면 콘솔이 시끄러웠다. 다시 재야 하면 해당 호출 줄의 <c>//</c>만
    // 풀면 된다 — 무엇을 재는 계측인지는 아래 각 주석에 그대로 남아 있다.
    //
    // 지운 계측이 무엇을 재던 것인지는 docs/npc-ragdoll.md §5에 있다. 진단 ①(권위 쪽 정착 상태)과
    // ②(정착 자세와 루트의 도착 시차)는 <b>재던 대상이 사라져</b> 함께 지웠다 — 정착이 뼈 길이를
    // 되돌리지도, 지면을 재정렬하지도, 몸을 루트에 매달지도 않는다.

    // 진단 ⑤ — 원격의 정착 결과. 호스트의 같은 값과 <b>같아야 한다.</b> 다르면 골격이 갈려 있는
    // 것이고(스트림이 뼈 길이를 안 싣는다 — docs/npc-ragdoll.md §7), 음수면 몸이 바닥에 박혀 있다.
    private void LogRemoteSettledClearance()
    {
        float clearance = LowestBoneClearance();
        Debug.Log(
            $"[진단5 클라여유] {name} 여유={clearance:F3}"
                + $"{(float.IsNaN(clearance) ? " ⚠바닥을 못 찾았다" : clearance < -0.02f ? " ⚠바닥에 박혔다" : clearance > 0.15f ? " ⚠떠 있다" : " (정상)")}",
            this
        );
    }

    // 진단 ⑥ (임시) — <b>바닥을 파고든 뼈를 뼈 단위로 집는다.</b>
    //
    // 부분 잠김("상체가 잠기거나 하체가 잠김, 전신은 아니다")의 원인이 두 층으로 갈리는데
    // <b>처방이 정반대다.</b> 어느 쪽인지 이 한 줄이 가른다:
    //
    //  · <b>콜라이더 없는 뼈</b>(손·발·발가락·목·쇄골) — 물리가 막을 것이 없어 원래 통과한다.
    //    프리팹 실측으로 리지드바디 12개 = 콜라이더 12개이므로 <b>나머지 리그 전부</b>가 여기다.
    //    처방은 RagdollSetup에 캡슐을 더하는 프리팹 작업이고 코드로 할 일이 없다
    //  · <b>콜라이더 있는 뼈</b> — 진입 자세가 이미 파고들어 있었고
    //    <c>RagdollRig</c>의 겹침 탈출 속도 상한(0.5m/s = 50Hz에서 스텝당 6mm, 중력을 빼면 실질
    //    0.3m/s)으로는 못 빠져나온 것이다. 처방은 그 상수와 접촉 오프셋 튜닝이다
    //
    // <b>진입과 정착을 둘 다 찍는 것이 핵심이다.</b> 깊이가 줄지 않았으면 물리가 손을 못 댄 것이고,
    // 그러면 겹침 탈출이 아니라 <b>진입 자세 자체</b>를 봐야 한다는 뜻이다.
    //
    // 지면은 <b>뼈마다 따로</b> 잰다 — 골반 하나로 재면 경사·계단에 걸친 몸이 통째로 틀린다.
    private Transform[] m_penetrationProbe;

    private void LogFloorPenetration(string phase, int detail = 6)
    {
        if (m_rig == null || m_rig.BoneRoot == null)
            return;

        if (m_penetrationProbe == null)
            m_penetrationProbe = m_rig.BoneRoot.GetComponentsInChildren<Transform>(true);

        const float k_reportBelow = -0.01f; // 1cm 아래부터 — 그보다 얕으면 접촉 오차다

        var sunk = new System.Collections.Generic.List<(string Name, float Depth, bool Covered)>();
        int covered = 0;

        // 가장 깊은 뼈가 <b>무엇을</b> 지면으로 잡았는지 — 도로가 아니라 인도 턱·잔해가 나오면
        // 이 측정이 "바닥 깊이"가 아니라 <b>모서리에 물린 것</b>을 재고 있다는 뜻이다.
        string deepestHit = "?";
        float deepest = 0f;

        for (int i = 0; i < m_penetrationProbe.Length; i++)
        {
            Transform bone = m_penetrationProbe[i];
            if (bone == null || !TryGroundUnder(bone.position, out Vector3 ground))
                continue;

            float clearance = bone.position.y - ground.y;
            if (clearance >= k_reportBelow)
                continue;

            bool hasCollider = bone.GetComponent<Collider>() != null;
            if (hasCollider)
                covered++;

            if (clearance < deepest)
            {
                deepest = clearance;
                deepestHit = TryGroundColliderUnder(bone.position);
            }

            sunk.Add((bone.name, clearance, hasCollider));
        }

        sunk.Sort((a, b) => a.Depth.CompareTo(b.Depth)); // 깊은 것부터

        var line = new System.Text.StringBuilder();
        line.Append($"[진단6 관통] {name} {phase} 파고든뼈 {sunk.Count}");
        line.Append($" (콜라이더유 {covered} / 무 {sunk.Count - covered})");

        int shown = Mathf.Min(sunk.Count, detail);
        for (int i = 0; i < shown; i++)
            line.Append($" | {sunk[i].Name} {sunk[i].Depth:F3}{(sunk[i].Covered ? "(유)" : "(무)")}");

        if (sunk.Count > shown)
            line.Append($" …+{sunk.Count - shown}");

        if (sunk.Count > 0)
            line.Append($" | 최심지면={deepestHit}");

        Debug.Log(line.ToString(), this);
    }

    // 진단 ⑦ (임시) — <b>기상 창을 프레임 단위로 훑는다.</b>
    //
    // 가설: 부활 블렌드가 뼈를 되돌리는 <b>도중</b>에 몸이 땅속으로 들어가는 구간이 있고, 그때
    // 재래그돌되면 물리가 파묻힌 자세를 그대로 받는다.
    //
    // 블렌드는 <b>로컬 회전</b>을 섞는다(<see cref="RagdollPoseBlend"/>). 출발점은 물리가 만든
    // 임의의 엎드린 자세이고 목표는 authoring된 기상 클립 자세인데, 두 자세가 다르면 <b>그 사이
    // 보간 경로는 어느 쪽 끝점도 아닌 자세들</b>이다 — 회전 보간은 팔다리 위치를 보존하지 않으므로
    // 중간에 양 끝점보다 더 낮은 자세가 나올 수 있다. 가설이 맞다면 여기서 <b>깊이가 솟다 가라앉는
    // 봉우리</b>가 보인다.
    //
    // 창은 블렌드(0.3초)보다 넉넉히 잡는다 — 딥이 블렌드 뒤 클립 초반일 수도 있다.
    // 권위 쪽만 찍는다: 물리가 도는 곳이 여기고, 로그 양이 절반이 된다.
    private const int k_riseProbeFrames = 30;

    private int m_riseProbeFrame = -1;

    private void BeginRiseProbe() => m_riseProbeFrame = HasMoveAuthority ? 0 : -1;

    private void TickRiseProbe()
    {
        if (m_riseProbeFrame < 0 || m_riseProbeFrame >= k_riseProbeFrames)
            return;

        // 상세는 1개만 — 30줄이 나가므로 봉우리의 <b>모양</b>을 읽을 수 있어야 한다.
        LogFloorPenetration($"기상+{m_riseProbeFrame:00}{(m_blending ? " 블렌드" : "")}", detail: 1);
        m_riseProbeFrame++;
    }

    // 진단 ④ — <b>스트림에 실리지 않는 뼈</b>가 피어마다 같은 자세인가.
    //
    // 리그에는 Rigidbody가 없는 뼈가 섞여 있고(Spine_01 · Spine_03 · Neck · Clavicle), 스트림은
    // <c>m_bodies</c>만 실으므로 그 뼈들은 <b>각 피어의 애니메이터가 마지막에 놓은 자세에 그대로
    // 멈춘다.</b> 그런데 <c>Hips → Spine_01 → Spine_02</c>라 <b>상체 전체가 Spine_01에 매달려
    // 있다</b> — 여기가 피어마다 다르면 스트림된 회전이 전부 같아도 몸이 다른 자세로 굳는다.
    //
    // 기상 클립은 척추가 빠르게 펴지는 구간이고, 기상 시작이 ClientRpc + 로컬 시계라
    // (<c>NpcAnimationDriver.HandleStandUp</c>) 피어마다 <b>클립 시간이 RTT만큼 어긋난다.</b>
    // 서 있다 죽으면 양쪽 다 같은 대기 자세라 차이가 작고, 기상 중에만 크게 벌어져야 가설이 맞다.
    //
    // 읽는 법: 같은 시체에 대해 <b>호스트 줄과 클라 줄을 나란히 놓고</b> 각도를 비교한다.
    private static readonly string[] k_unstreamedProbeBones =
    {
        "Spine_01",
        "Spine_03",
        "Neck",
        "Clavicle_L",
    };

    private Transform[] m_unstreamedProbe;
    private float m_probeClipTime = -1f;

    // ⚠ 애니메이터를 끄기 전에 부른다 — 꺼진 뒤에는 클립 시간을 못 읽는다.
    private void SampleAnimatorClock()
    {
        m_probeClipTime =
            m_animator != null && m_animator.enabled && m_animator.runtimeAnimatorController != null
                ? m_animator.GetCurrentAnimatorStateInfo(0).normalizedTime
                : -1f;
    }

    private void LogUnstreamedBones()
    {
        if (m_rig == null || m_rig.BoneRoot == null)
            return;

        if (m_unstreamedProbe == null)
        {
            m_unstreamedProbe = new Transform[k_unstreamedProbeBones.Length];
            Transform[] all = m_rig.BoneRoot.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < k_unstreamedProbeBones.Length; i++)
            {
                for (int j = 0; j < all.Length; j++)
                {
                    if (all[j].name != k_unstreamedProbeBones[i])
                        continue;

                    m_unstreamedProbe[i] = all[j];
                    break;
                }
            }
        }

        var line = new System.Text.StringBuilder();
        line.Append($"[진단4 비스트림뼈] {name} 권위={HasMoveAuthority} ");
        line.Append(m_probeClipTime >= 0f ? $"클립t={m_probeClipTime:F3}" : "클립t=?");

        for (int i = 0; i < m_unstreamedProbe.Length; i++)
        {
            Transform bone = m_unstreamedProbe[i];
            if (bone == null)
            {
                line.Append($" | {k_unstreamedProbeBones[i]}=없음");
                continue;
            }

            Vector3 euler = bone.localRotation.eulerAngles;
            line.Append(
                $" | {bone.name}=({Mathf.DeltaAngle(0f, euler.x):F1},"
                    + $"{Mathf.DeltaAngle(0f, euler.y):F1},{Mathf.DeltaAngle(0f, euler.z):F1})"
            );
        }

        Debug.Log(line.ToString(), this);
    }

    // 진단 ⑧ (임시) — <b>물리가 뼈를 얼마나 늘려 놨는가.</b>
    //
    // 스트림은 뼈 <b>길이</b>를 싣지 않는다 — "관절이 유지하므로 상수"가 그 설계의 전제다
    // (docs/728-ragdoll-pose-streaming.md §1-2). 그런데 관절 projection이 꺼져 있어 붙드는 일이
    // 전적으로 solver 반복 몫이고(<c>RagdollRig</c>의 solver 상수 주석), 강한 임펄스나 밧줄 장력에서는
    // 눈에 띄게 늘어난다. <b>게다가 시체는 그 길이를 되돌릴 곳이 없다</b> — <c>RestoreBindPose</c>는
    // <c>ExitRagdoll</c>에서만 도는데 시체는 그 문을 영영 지나지 않고,
    // <c>RagdollRig.RestoreBindBoneLengths</c>는 지금 <b>아무도 부르지 않는다</b>(옛 키네마틱 정착과
    // 함께 호출부가 사라졌다).
    //
    // <b>읽는 법.</b> 진입에서 0에 가깝다가 밧줄·배치를 지나며 커지면, 그 값이 곧 원격과 갈릴 수 있는
    // 폭의 상한이다. 끝까지 0에 가까우면 이 가설은 기각이고 진단 ⑨도 함께 0으로 나온다.
    private System.Collections.Generic.List<(string Bone, float Drift)> m_driftProbe;

    private void LogBoneLengthDrift(string phase)
    {
        if (!HasMoveAuthority || m_rig == null || !m_rig.IsValid)
            return;

        // <b>어느 뼈인지가 값보다 중요해졌다</b> — 밧줄부착 시점에 이미 0.127m였고 끌기·배치를
        // 지나도 상수라(실측 2026-08-19), 물리가 그때그때 늘리는 것이 아니라 <b>이미 굳어 있는
        // 오프셋</b>이다. 뼈 이름이 그것이 어느 관절에서 생겼는지를 가른다.
        if (m_driftProbe == null)
            m_driftProbe = new System.Collections.Generic.List<(string Bone, float Drift)>();

        m_rig.CollectBindPositionDrift(m_driftProbe);
        m_driftProbe.Sort((a, b) => b.Drift.CompareTo(a.Drift));

        float drift = m_driftProbe.Count > 0 ? m_driftProbe[0].Drift : 0f;

        var line = new System.Text.StringBuilder();
        line.Append($"[진단8 뼈길이] {name} {phase} 최대드리프트={drift:F3}m 뼈={m_driftProbe.Count}");

        for (int i = 0; i < Mathf.Min(m_driftProbe.Count, 3); i++)
            line.Append($" | {m_driftProbe[i].Bone} {m_driftProbe[i].Drift:F3}");

        line.Append(drift > 0.02f ? " ⚠늘어났다" : " (정상)");

        Debug.Log(line.ToString(), this);
    }

    // 진단 ⑨ (임시) — <b>원격이 그리는 몸과 내 몸의 차를 뼈 단위로, 호스트 혼자서 잰다.</b>
    //
    // 스트림이 싣는 것은 골반 <b>월드 위치</b>와 뼈 <b>로컬 회전</b>뿐이다
    // (<see cref="RagdollPoseStreamer"/>). 원격은 그 회전을 <b>자기 바인드 길이</b> 골격에 얹어 몸을
    // 다시 만드는데, 권위 쪽 뼈는 물리가 늘려 놓아 길이가 다르다(진단 ⑧). 여기서 재는 것이 정확히
    // 그 차 — <b>클라 화면과 호스트 화면이 벌어지는 폭</b>이다.
    //
    // <b>원격 로그 없이 판별되는 것이 요점이다</b>(MPPM 가상 플레이어는 콘솔이 안 잡힌다). 원격이 할
    // 재구성을 호스트에서 그대로 따라 해 실제 뼈 위치와 뺀다: 골반은 <b>보내는 값</b>에 못박고,
    // 자식으로 내려가며 <c>부모월드 × (바인드 길이 · 지금 로컬 회전)</c>으로 위치를 다시 만든다.
    // 체인 끝(팔·손)일수록 오차가 쌓이므로 <b>상위 뼈 이름이 어디서 벌어졌는지</b>를 말해 준다.
    //
    // ⚠ 재지 <b>않는</b> 것 둘: ① 골반 위쪽 뼈 — 원격에서는 루트 계층이 놓는다, ② 원격 루트 NT의
    // 보간 오차 — 루트 회전은 양쪽이 같다고 본다. 그쪽이 의심되면 진단 ⑤(클라 여유)와 함께 읽는다.
    //
    // ⚠⚠ <b>고친 뒤로 이 값은 "원격이 그리게 될 몸"이 아니다.</b> 신뢰 1회 패킷이 뼈 길이를 함께
    // 나르므로(docs/npc-ragdoll.md §8) 원격은 더 이상 바인드 길이로 재구성하지 않는다. 지금 이 값이
    // 뜻하는 것은 <b>"길이를 안 실었다면 얼마나 갈렸을까"</b> — 즉 드리프트가 커지고 있는지 보는
    // 감시 지표다. 화면이 맞는지는 이 로그가 아니라 <b>호스트와 클라 화면을 나란히 놓고</b> 본다.
    private Vector3[] m_reconPositions;
    private Quaternion[] m_reconRotations;

    private void LogRemoteReconstructionError(string phase, int detail = 5)
    {
        if (!HasMoveAuthority || m_rig == null || !m_rig.IsValid)
            return;

        Transform[] bones = m_rig.PoseBones;
        Transform hips = m_rig.Hips;
        if (bones == null || bones.Length == 0 || hips == null)
            return;

        if (m_reconPositions == null || m_reconPositions.Length != bones.Length)
        {
            m_reconPositions = new Vector3[bones.Length];
            m_reconRotations = new Quaternion[bones.Length];
        }

        var diffs = new System.Collections.Generic.List<(string Name, float Error)>();
        float worst = 0f;
        float sum = 0f;

        for (int i = 0; i < bones.Length; i++)
        {
            Transform bone = bones[i];
            if (bone == null || !bone.IsChildOf(hips))
                continue; // 골반 위쪽 — 원격에서는 루트 계층이 놓는 자리라 잴 대상이 아니다

            if (bone == hips)
            {
                // 원격이 월드로 못박는 값 그대로다 — 여기서는 오차가 0이라 세지 않는다.
                m_reconPositions[i] = hips.position;
                m_reconRotations[i] = hips.rotation;
                continue;
            }

            int parent = IndexOfBone(bones, bone.parent);
            if (parent < 0 || !m_rig.TryGetBindLocalPosition(bone, out Vector3 bindLocal))
                continue;

            // 계층 수학 그대로 — 원격이 하는 일이 이것이다(회전은 받은 것, 길이는 자기 바인드).
            m_reconRotations[i] = m_reconRotations[parent] * bone.localRotation;
            m_reconPositions[i] =
                m_reconPositions[parent]
                + m_reconRotations[parent] * Vector3.Scale(bindLocal, bone.parent.lossyScale);

            float error = Vector3.Distance(m_reconPositions[i], bone.position);
            sum += error;
            if (error > worst)
                worst = error;

            diffs.Add((bone.name, error));
        }

        diffs.Sort((a, b) => b.Error.CompareTo(a.Error)); // 큰 것부터

        var line = new System.Text.StringBuilder();
        line.Append($"[진단9 재구성] {name} {phase} 최대={worst:F3}m");
        line.Append($" 평균={(diffs.Count > 0 ? sum / diffs.Count : 0f):F3} 뼈={diffs.Count}");

        int shown = Mathf.Min(diffs.Count, detail);
        for (int i = 0; i < shown; i++)
            line.Append($" | {diffs[i].Name} {diffs[i].Error:F3}");

        line.Append(worst > 0.05f ? " ⚠원격과 갈린다" : worst > 0.02f ? " ⚠벌어지는 중" : " (정상)");

        Debug.Log(line.ToString(), this);
    }

    // 진단 ⑨ 보조 (임시) — 뼈 배열에서 부모의 자리를 찾는다. 리그당 20개 남짓이라 선형으로 충분하다.
    private static int IndexOfBone(Transform[] bones, Transform bone)
    {
        if (bone == null)
            return -1;

        for (int i = 0; i < bones.Length; i++)
        {
            if (bones[i] == bone)
                return i;
        }

        return -1;
    }
}
