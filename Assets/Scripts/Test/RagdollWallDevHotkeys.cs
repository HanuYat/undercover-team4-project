#if UNITY_EDITOR
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.InputSystem;

/// <summary>
/// 래그돌 벽 관통 테스트 단축키 (#980) — <b>에디터 전용</b>. 벽에 밀착한 NPC를 임펄스로 치는
/// 상황을 손으로 만들 필요 없이 재현한다.
///
/// <list type="bullet">
/// <item><c>,</c> — 내가 보는 벽에 NPC를 세우고 <b>팔을 벽 안으로 찔러 넣는다</b></item>
/// <item><c>.</c> — 그 NPC를 <b>벽 쪽으로</b> 홈런봉과 같은 세기로 날린다</item>
/// </list>
///
/// <b>팔을 벽에 넣는 것이 요점이다.</b> 캡슐만 벽에 붙여 두면 팔이 벽에 들어갈지가 애니메이션
/// 자세에 달려 운에 맡기게 된다(에이전트 반지름 0.35 &gt; 늘어뜨린 팔이 몸에서 벗어나는 거리).
/// 그래서 애니메이터를 떼고 어깨·팔꿈치를 직접 벽 쪽으로 돌린 뒤, <c>ComputePenetration</c>으로
/// <b>실제 침투 깊이를 재서 로그로 찍는다</b> — 재현 조건이 성립했는지를 눈이 아니라 숫자로 판단한다.
///
/// 살아 있는 동안에도 뼈 콜라이더는 <b>켜져 있다</b>. 다만 키네마틱이라 정적 지오메트리가 밀어내지
/// 못해 그대로 통과한다 — 그게 이 버그의 출발 조건이고, 이 도구가 재현하는 상태다.
///
/// ⚠ <b>가드 토글(<c>/</c>)은 빠져 있다</b> — 토글 대상이던 설정 필드가 되돌려졌다
/// (docs/980-ragdoll-wall-stuck.md §6). 대책을 다시 넣을 때 함께 살리면 A/B 비교가 편하다.
///
/// 관례는 <see cref="BombDevHotkeys"/>와 같다 — <c>UNITY_EDITOR</c>로 감싸 빌드에서 사라지고,
/// 키보드가 없는 구성에서는 조용히 넘어간다. 다만 이쪽은 RPC가 없다 — <b>서버·오프라인에서만</b>
/// 듣는다(FSM·발사 모두 서버 권위다). 씬의 아무 오브젝트에나 붙여 두면 된다.
/// </summary>
public class RagdollWallDevHotkeys : MonoBehaviour
{
    [Tooltip("끄면 단축키가 듣지 않는다 — 같은 키를 쓰는 다른 테스트를 할 때 잠깐 내린다")]
    [SerializeField] private bool m_enabled = true;

    [Header("키 (인스펙터 조절)")]
    [Tooltip("내가 보는 벽에 NPC를 세우고 팔을 벽 안으로 넣는다")]
    [SerializeField] private Key m_placeKey = Key.Comma;

    [Tooltip("세워 둔 NPC를 벽 쪽으로 날린다")]
    [SerializeField] private Key m_launchKey = Key.Period;

    [Tooltip("대상 NPC의 <b>팔 접기</b>(#980)를 켜고 끈다 — 같은 조건으로 A/B를 보는 키다")]
    [SerializeField] private Key m_wallFoldKey = Key.Slash;

    [Header("배치")]
    [Tooltip("이 거리(m) 안에서 벽을 찾는다 — 벽을 바라보고 누를 것")]
    [SerializeField] private float m_wallSearchDistance = 12f;

    [Tooltip("벽면과 에이전트 캡슐 사이에 남길 간격(m) — 0이면 캡슐이 벽에 닿는다.\n\n" +
             "<b>몸통</b>의 자리다. 팔은 아래 항목이 따로 밀어 넣는다")]
    [SerializeField] private float m_wallGap;

    [Header("팔 찔러 넣기 — 재현의 핵심")]
    [Tooltip("끄면 캡슐만 벽에 붙이고 팔은 애니메이션 자세 그대로 둔다(구 동작). " +
             "켜면 애니메이터를 떼고 팔을 벽 쪽으로 돌려 아래 깊이만큼 넣는다")]
    [SerializeField] private bool m_poseArmIntoWall = true;

    [Tooltip("어느 쪽 팔을 넣을지 — 끄면 오른팔")]
    [SerializeField] private bool m_useLeftArm = true;

    [Tooltip("팔꿈치 뼈가 벽면 안으로 들어갈 목표 깊이(m).\n\n" +
             "HQ 벽 실측 두께가 0.038m라 그보다 크게 잡으면 <b>이미 반대편까지 나간</b> 상태가 되고, " +
             "작게 잡으면 걸친 상태가 된다. 둘 다 재현 가치가 있으니 바꿔 가며 볼 것")]
    [SerializeField] private float m_armTargetPenetration = 0.03f;

    [Header("발사 (홈런 진압봉 기본값과 맞춰 둠)")]
    [Tooltip("수평 발사 속도(m/s) — HomeRunBaton.m_launchSpeed와 같은 값이 기본")]
    [SerializeField] private float m_launchSpeed = 14f;

    [Tooltip("수평 대비 들어올림 비율 — 1.0이면 45도")]
    [SerializeField] private float m_liftRatio = 0.5f;

    [Tooltip("발사 후 누워 있는 시간(초)")]
    [SerializeField] private float m_stunSeconds = 6f;

    [Header("팔 접기 A/B (#980)")]
    [Tooltip("세우는 NPC에게 걸 <c>NpcRagdoll.WallFoldEnabled</c> — 끄면 옛 동작(박힌 채 정착)이 " +
             "그대로 보인다. 프리팹 기본은 켜짐이다")]
    [SerializeField] private bool m_wallFold = true;

    // 마지막으로 세워 둔 대상과 그때의 벽 — 발사 키가 같은 조건으로 다시 쏘려면 둘 다 필요하다.
    private NpcController m_subject;
    private Vector3 m_wallNormal;

    // 대상에게 손댄 것들 — 다음 대상을 세울 때 되돌린다(망가진 NPC를 씬에 남기지 않으려는 것).
    private Animator m_subjectAnimator;
    private NavMeshAgent m_subjectAgent;

    private void Update()
    {
        if (!m_enabled)
            return;

        // 키보드가 없는 구성(원격 데스크톱 등)에서는 조용히 넘어간다
        Keyboard keyboard = Keyboard.current;
        if (keyboard == null)
            return;

        if (keyboard[m_placeKey].wasPressedThisFrame)
            PlaceAgainstWall();
        if (keyboard[m_launchKey].wasPressedThisFrame)
            LaunchIntoWall();
        if (keyboard[m_wallFoldKey].wasPressedThisFrame)
            ToggleWallFold();
    }

    // ---- 팔 접기 A/B (#980) ----

    // 대상 NPC의 팔 접기를 켜고 끈다. 세워 둔 NPC가 없어도 값은 기억해 다음 배치에 실린다.
    private void ToggleWallFold()
    {
        m_wallFold = !m_wallFold;
        ApplyWallFold();

        string where = m_subject != null ? m_subject.name : "다음에 세울 NPC부터";
        Debug.Log(
            $"[래그돌벽/개발용] 팔 접기 {(m_wallFold ? "켜짐" : "꺼짐")} — {where}",
            this
        );
    }

    private void ApplyWallFold()
    {
        if (m_subject != null && m_subject.Ragdoll != null)
            m_subject.Ragdoll.WallFoldEnabled = m_wallFold;
    }

    // ---- 배치 ----

    private void PlaceAgainstWall()
    {
        Transform player = DevPlayerLookup.LocalPlayer();
        if (player == null)
        {
            Debug.LogWarning("[래그돌벽/개발용] 플레이어를 찾지 못했다");
            return;
        }

        if (!TryFindWall(player, out RaycastHit wall))
        {
            Debug.LogWarning(
                $"[래그돌벽/개발용] 앞 {m_wallSearchDistance}m 안에서 벽을 못 찾았다 — 벽을 보고 다시 누를 것"
            );
            return;
        }

        NpcController npc = FindNearestUsableNpc(player.position);
        if (npc == null)
        {
            Debug.LogWarning("[래그돌벽/개발용] 세울 만한 NPC가 없다 — 성하고 서 있는 NPC 근처에서 누를 것");
            return;
        }

        NavMeshAgent agent = npc.GetComponent<NavMeshAgent>();
        if (agent == null)
        {
            Debug.LogWarning($"[래그돌벽/개발용] {npc.name}에 NavMeshAgent가 없다", npc);
            return;
        }

        // 벽면 법선의 수평 성분 — 바닥·천장을 잘못 집었을 때 몸이 공중에 뜨지 않게.
        Vector3 normal = new Vector3(wall.normal.x, 0f, wall.normal.z);
        if (normal.sqrMagnitude < 0.0001f)
        {
            Debug.LogWarning("[래그돌벽/개발용] 수평 법선이 없다 — 바닥이나 천장을 본 것 같다");
            return;
        }
        normal.Normalize();

        RestorePreviousSubject(); // 앞 대상을 원래대로 돌려놓고 시작한다

        npc.SetFrozen(true); // 먼저 굳힌다 — 옮긴 뒤에 굳히면 그 사이 한 틱이 돌아 제자리를 떠난다

        // 트랜스폼을 우리가 쥔다 — 에이전트가 켜져 있으면 다음 프레임에 자기 내부 위치로 되돌린다.
        // 끄지 않고 갱신만 떼는 이유는 발사 경로(EnterRagdoll)가 에이전트를 켜져 있는 것으로 보기 때문.
        agent.updatePosition = false;
        agent.updateRotation = false;
        m_subjectAgent = agent;

        npc.transform.position = wall.point + normal * (agent.radius + m_wallGap);
        npc.transform.rotation = Quaternion.LookRotation(-normal); // 벽을 마주 본다
        Physics.SyncTransforms(); // ⚠ autoSyncTransforms=0이라 필수 — 안 하면 아래 질의가 옛 자리를 본다

        m_subject = npc;
        m_wallNormal = normal;

        ApplyWallFold();

        if (m_poseArmIntoWall)
            PoseArmIntoWall(npc, wall.collider, normal);
        else
            Debug.Log($"[래그돌벽/개발용] {npc.name}을 벽({wall.collider.name})에 붙여 세웠다 (팔 찔러넣기 꺼짐)", npc);
    }

    // 애니메이터를 떼고 어깨·팔꿈치를 벽 쪽으로 돌린 뒤, 목표 깊이가 될 때까지 몸을 조금씩 밀어 넣는다.
    // 마지막에 <b>실제 침투 깊이를 재서</b> 로그로 찍는다 — 재현 조건 성립 여부는 이 숫자가 말한다.
    private void PoseArmIntoWall(NpcController npc, Collider wall, Vector3 normal)
    {
        RagdollRig rig = npc.GetComponentInChildren<RagdollRig>(true);
        if (rig == null || !rig.IsValid)
        {
            Debug.LogWarning($"[래그돌벽/개발용] {npc.name}에서 RagdollRig를 찾지 못했다", npc);
            return;
        }

        // 뼈 탐색 범위를 리그 최상단으로 좁힌다 — 프리팹에 이름이 같은 리그가 둘일 수 있다(1인칭 팔).
        Transform boneRoot = rig.Hips != null ? rig.Hips.parent : null;
        string side = m_useLeftArm ? "_L" : "_R";
        Transform shoulder = FindBone(boneRoot, "Shoulder" + side);
        Transform elbow = FindBone(boneRoot, "Elbow" + side);
        if (shoulder == null || elbow == null)
        {
            Debug.LogWarning($"[래그돌벽/개발용] Shoulder{side}/Elbow{side} 뼈를 찾지 못했다", npc);
            return;
        }

        // 애니메이터가 살아 있으면 다음 프레임에 자세를 도로 덮는다.
        m_subjectAnimator = npc.GetComponentInChildren<Animator>(true);
        if (m_subjectAnimator != null)
            m_subjectAnimator.enabled = false;

        Vector3 into = -normal;
        AimBoneAlong(shoulder, elbow, into); // 위팔을 벽 쪽으로
        if (elbow.childCount > 0)
            AimBoneAlong(elbow, elbow.GetChild(0), into); // 아래팔까지 펴서 더 깊이 닿게
        Physics.SyncTransforms();

        Collider elbowCol = elbow.GetComponent<Collider>();
        if (elbowCol == null)
        {
            Debug.LogWarning($"[래그돌벽/개발용] Elbow{side}에 콜라이더가 없다", npc);
            return;
        }

        // 목표 깊이에 못 미치면 부족한 만큼 몸을 벽 쪽으로 민다. 팔이 비스듬하면 몸이 1cm 가도
        // 팔은 1cm 안 들어가므로 몇 번 반복한다.
        //
        // ⚠ <b>부족분의 절반씩만 간다.</b> 한 번에 부족분을 다 가면 넘어간다 — 캡슐은 끝이 둥글어
        // 얕게 걸친 구간에서 몸이 1cm 갈 때 침투가 그보다 빨리 깊어진다(실측: 목표 3.0에 6.6이 나왔다).
        // ⚠ <b>관통해 버리면 멈춘다.</b> 겹침 0은 "안 닿음"과 "이미 뚫고 나감" 둘 다라, 이 검사가
        // 없으면 뚫린 팔꿈치를 '아직 0'으로 읽고 계속 밀어 <b>어깨가 대신 깊이 박힌다</b>
        // (실측: 팔꿈치 0.0인데 어깨가 8.5cm 들어갔다).
        const int k_maxNudges = 24;
        const float k_minStep = 0.002f;
        float depth = MeasurePenetration(elbowCol, wall);
        for (int i = 0; i < k_maxNudges && depth < m_armTargetPenetration; i++)
        {
            if (IsThroughWall(elbow, rig.Hips, wall))
                break; // 이미 반대편 — 더 밀면 몸까지 넣는다

            float deficit = m_armTargetPenetration - depth;
            npc.transform.position += into * Mathf.Max(deficit * 0.5f, k_minStep);
            Physics.SyncTransforms();

            float next = MeasurePenetration(elbowCol, wall);
            if (next <= depth + 0.00001f && !IsThroughWall(elbow, rig.Hips, wall))
                break; // 더 이상 안 깊어지고 관통도 아니다 — 다른 것에 막혔다
            depth = next;
        }

        Collider shoulderCol = shoulder.GetComponent<Collider>();
        float shoulderDepth = shoulderCol != null ? MeasurePenetration(shoulderCol, wall) : -1f;

        // 몸통(골반)이 벽 안으로 들어갔는지도 같이 잰다 — 팔만 넣으려던 것이 몸까지 넣은 것은 아닌지.
        // bounds.ClosestPoint는 건물 전체 바운즈라 의미가 없다(실측에서 0.0cm만 나왔다) — 실제
        // 골반 콜라이더의 침투를 직접 잰다.
        Collider hipsCol = rig.Hips != null ? rig.Hips.GetComponent<Collider>() : null;
        float hipsDepth = hipsCol != null ? MeasurePenetration(hipsCol, wall) : 0f;

        Debug.Log(
            $"[래그돌벽/개발용] {npc.name} 배치 완료 — 벽 {wall.gameObject.name}\n"
                + $"  팔꿈치(Elbow{side}) {DescribeDepth(depth, elbow, rig.Hips, wall)} (목표 {m_armTargetPenetration * 100f:F1}cm)\n"
                + $"  어깨(Shoulder{side}) {DescribeDepth(shoulderDepth, shoulder, rig.Hips, wall)}\n"
                + $"  골반 침투 {hipsDepth * 100f:F1}cm ← 0이어야 '팔만 박힌' 재현이다\n"
                + $"  → {m_launchKey}로 벽 쪽 발사",
            npc
        );
    }

    // 뼈를 돌려 (뼈 -> 자식) 방향을 want에 맞춘다. 회전만 건드리므로 뼈 길이가 틀어지지 않는다 —
    // 위치를 직접 대입하면 관절 앵커가 어긋나 래그돌이 위반 상태로 출발한다(RagdollRig docs §5).
    private static void AimBoneAlong(Transform bone, Transform child, Vector3 want)
    {
        Vector3 current = child.position - bone.position;
        if (current.sqrMagnitude < 0.000001f)
            return;

        bone.rotation = Quaternion.FromToRotation(current, want) * bone.rotation;
    }

    /// <summary>
    /// 깊이를 사람이 읽을 문장으로 — <b>침투 0에는 뜻이 둘</b>이라 반드시 갈라 줘야 한다.
    ///
    /// 벽에 아예 안 닿아도 0이고, <b>반대편으로 완전히 뚫고 나가도</b> 겹침이 없어 0이다. 실측에서
    /// "팔꿈치 침투 0.0cm"를 '재현 실패'로 읽었는데 실제로는 <b>이미 관통해 있던</b> 상태였다.
    /// 골반(=확실히 벽 밖)에서 그 뼈까지 레이를 쏴서 중간에 벽이 있으면 관통으로 판정한다.
    /// </summary>
    private static string DescribeDepth(float depth, Transform bone, Transform hips, Collider wall)
    {
        if (depth > 0.0001f)
            return $"침투 {depth * 100f:F1}cm";

        return IsThroughWall(bone, hips, wall)
            ? "<b>완전 관통 — 벽 반대편에 있다</b> (겹침 0)"
            : "침투 0.0cm (벽 밖)";
    }

    // 골반(=확실히 벽 밖)에서 그 뼈로 가는 길에 이 벽이 끼어 있으면, 뼈는 벽 반대편에 있다.
    private static bool IsThroughWall(Transform bone, Transform hips, Collider wall)
    {
        if (bone == null || hips == null || wall == null)
            return false;

        Vector3 from = hips.position;
        Vector3 to = bone.position;
        float distance = Vector3.Distance(from, to);
        if (distance < 0.0001f)
            return false;

        RaycastHit[] hits = Physics.RaycastAll(
            from,
            (to - from) / distance,
            distance,
            ~0,
            QueryTriggerInteraction.Ignore
        );
        for (int i = 0; i < hits.Length; i++)
            if (hits[i].collider == wall)
                return true;

        return false;
    }

    // 두 콜라이더의 겹침 깊이(m) — 안 겹치면 0. 비볼록 MeshCollider에서도 동작하는 것을 확인했다.
    private static float MeasurePenetration(Collider a, Collider b)
    {
        return Physics.ComputePenetration(
            a,
            a.transform.position,
            a.transform.rotation,
            b,
            b.transform.position,
            b.transform.rotation,
            out _,
            out float distance
        )
            ? distance
            : 0f;
    }

    private static Transform FindBone(Transform root, string boneName)
    {
        if (root == null)
            return null;

        Transform[] all = root.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < all.Length; i++)
            if (all[i].name == boneName)
                return all[i];

        return null;
    }

    // 앞 대상에게 손댄 것을 되돌린다 — 애니메이터와 에이전트 갱신. 자세는 애니메이터가 알아서 덮는다.
    private void RestorePreviousSubject()
    {
        if (m_subjectAnimator != null)
            m_subjectAnimator.enabled = true;
        m_subjectAnimator = null;

        if (m_subjectAgent != null)
        {
            m_subjectAgent.updatePosition = true;
            m_subjectAgent.updateRotation = true;
        }
        m_subjectAgent = null;

        if (m_subject != null)
            m_subject.SetFrozen(false);
    }

    // 벽 = 캐릭터가 아닌 콜라이더. SweepHitsObstacle과 같은 규칙으로 거른다
    // (플레이어 몸통이 환경과 같은 Default 레이어라 마스크로는 못 가른다).
    private bool TryFindWall(Transform player, out RaycastHit wall)
    {
        wall = default;

        // 눈높이에서 쏜다 — 발밑에서 쏘면 연석·화단이 먼저 걸린다.
        Vector3 origin = player.position + Vector3.up * 1.5f;
        Vector3 forward = new Vector3(player.forward.x, 0f, player.forward.z).normalized;

        RaycastHit[] hits = Physics.RaycastAll(
            origin,
            forward,
            m_wallSearchDistance,
            ~0,
            QueryTriggerInteraction.Ignore
        );

        bool found = false;
        for (int i = 0; i < hits.Length; i++)
        {
            Collider hit = hits[i].collider;
            if (hit == null)
                continue;
            if (hit.GetComponentInParent<PlayerHealth>() != null)
                continue; // 플레이어 — 벽이 아니다
            if (hit.GetComponentInParent<NpcController>() != null)
                continue; // NPC — 벽이 아니다

            if (!found || hits[i].distance < wall.distance)
            {
                wall = hits[i];
                found = true;
            }
        }

        return found;
    }

    // 지금 세워도 되는 NPC 중 가장 가까운 하나. AgentReady가 거짓이면 이미 래그돌·비행·끌기 중이다.
    // 스폰물이라 R1(FindFirstObjectByType 금지)의 대상이 아니다 — DevPlayerLookup과 같은 사정이다.
    private static NpcController FindNearestUsableNpc(Vector3 origin)
    {
        NpcController[] all = Object.FindObjectsByType<NpcController>(FindObjectsSortMode.None);

        NpcController best = null;
        float bestSqr = float.MaxValue;
        for (int i = 0; i < all.Length; i++)
        {
            NpcController npc = all[i];
            if (npc == null || !npc.AgentReady)
                continue;

            NpcState state = npc.StateMachine.CurrentState;
            if (state == NpcState.Dead || state == NpcState.Jailed || state == NpcState.Intruding)
                continue;

            float sqr = (npc.transform.position - origin).sqrMagnitude;
            if (sqr < bestSqr)
            {
                bestSqr = sqr;
                best = npc;
            }
        }

        return best;
    }

    // ---- 발사 ----

    private void LaunchIntoWall()
    {
        if (m_subject == null)
        {
            Debug.LogWarning($"[래그돌벽/개발용] 세워 둔 NPC가 없다 — {m_placeKey}로 먼저 세울 것");
            return;
        }

        // 벽 법선의 반대 = 벽 안쪽. HomeRunBaton.ServerOnHitLanded와 같은 조립이다.
        Vector3 into = -m_wallNormal;
        Vector3 impulse = into * m_launchSpeed + Vector3.up * (m_launchSpeed * m_liftRatio);

        // 에이전트 갱신을 돌려놓고 굳힌 것을 푼다 — 안 풀면 발사 후 착지·기상 처리가 멈춘 채로 남는다.
        // 애니메이터는 그대로 둔다: EnterRagdoll이 어차피 떼고, 지금 켜면 만들어 둔 자세가 덮인다.
        if (m_subjectAgent != null)
        {
            m_subjectAgent.updatePosition = true;
            m_subjectAgent.updateRotation = true;
        }
        m_subject.SetFrozen(false);

        m_subject.Knockback.ServerLaunchRagdoll(impulse, m_stunSeconds, DevPlayerLookup.LocalPlayer());

        // ⚠ 이 값은 <b>요청값</b>이다 — 발사 경로에 임펄스를 깎는 가드가 붙어 있으면 실제로 뼈에
        // 실린 값은 다르다. 둘을 같은 것으로 읽으면 "가드가 안 먹었다"고 오해한다(실제로 그렇게 읽혔다).
        Debug.Log(
            $"[래그돌벽/개발용] {m_subject.name} 벽 쪽으로 발사 — 요청 임펄스 {impulse:F2}",
            m_subject
        );
    }

}
#endif
