using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 저항(Attack) 상태 — 수갑 채널링 성공 순간 그 자리에서 버티며 싸운다. (GDD 6-1/7-4, #76/#79)
/// 표적을 바라보며 주기적으로 정면 부채꼴 타격을 휘둘러 사거리 안 플레이어의 HP를 깎고(선제 공격, 방향 판정 #220),
/// 제압 타격으로 체력이 0이 되면 기절(Stunned)한다 — 여럿이 때리면 빨리 끝난다(협동 인센티브).
/// 전이 자체는 NpcController.SetHp가 걸므로 이 상태 클래스는 체력을 보지 않는다 (#366).
/// 교전 중인 플레이어가 전원 무력화되면 플레이어 패배 — 도주형으로 전환되어 달아난다 (GDD 7-4 3항).
/// 제한 시간으로 뿌리치고 도주하던 경로는 폐지됐다 (#366) — 저항 NPC는 체력이 0이 될 때까지 버틴다.
/// 표적이 사라지면 NoTargetIdleSeconds 후 배회로 돌아간다(고착 방지).
/// </summary>
public class NpcResistState : NpcStateBase
{
    // 폭탄(BombDevice)·차량(TrafficVehicle)과 같은 크기. 16이었을 때 타격이 <b>가끔 빗나갔다</b> (#692) —
    // 아래 CollectPlayersInRange 주석 참고.
    private const int k_maxOverlapHits = 64;

    // 가시선 레이의 높이(m) — 발밑(transform.position) 기준으로 쏘면 연석 같은 낮은 지형에 걸린다.
    // 정확한 가슴 높이일 필요는 없다 — "벽이 있는가"만 가르면 된다 (#839).
    private const float k_losHeight = 1f;

    // 서버에서만 Tick되므로 버퍼 공유 안전 — 매 타격마다의 할당 방지
    private static readonly Collider[] s_overlapBuffer = new Collider[k_maxOverlapHits];

    private static int s_hitLayers; // 0 = 아직 조회 전

    /// <summary>
    /// 타격 판정용 레이어 마스크 — 래그돌 본을 뺀 전 레이어. (#692)
    /// ⚠ <see cref="LayerMask.NameToLayer"/>는 필드 초기화에서 호출이 금지돼 있어 첫 사용 시점에 늦게
    /// 조회한다 (<see cref="TrafficVehicle"/>·<see cref="NpcNavAreas.RoadMask"/>와 같은 패턴).
    /// </summary>
    private static int HitLayers
    {
        get
        {
            if (s_hitLayers == 0)
            {
                int ragdoll = LayerMask.NameToLayer("Ragdoll");
                s_hitLayers = ragdoll >= 0 ? ~(1 << ragdoll) : ~0; // 레이어가 없으면 전 레이어로 폴백
            }
            return s_hitLayers;
        }
    }
    private static readonly List<PlayerHealth> s_playerBuffer = new List<PlayerHealth>(8);

    private const float k_noPendingStrike = -1f;

    // 추격 이동 (#254)
    private const float k_stopDistanceFactor = 0.8f;       // 사거리 안쪽 이 비율 지점에 멈춰 타격 사거리를 유지
    private const float k_chaseRepathMoveThreshold = 0.5f; // 표적이 이만큼(m) 움직였을 때만 재계산
    private static readonly Vector3 k_noDestination = new Vector3(float.PositiveInfinity, 0f, 0f);

    // 표적을 찾지 못한 채 흐른 시간(초) — 표적이 다시 잡히면 0으로 리셋해 연속일 때만 누적한다 (#366)
    private float m_noTargetSeconds;
    private float m_nextAttackTime;
    // 스윙을 시작한 뒤 타격 프레임을 기다리는 예약 시각 — 데미지를 스윙 시작이 아니라 이 시점에 넣어
    // 눈에 보이는 타격과 HP 감소를 일치시킨다. k_noPendingStrike면 대기 중인 타격 없음. (#220)
    private float m_pendingStrikeTime = k_noPendingStrike;

    // 스윙 동안 이동을 멈추는 종료 시각 — 멈춰서 때린다. 0이면 홀드 중 아님 (팀 피드백: 스케이트 수정)
    private float m_swingHoldUntil;

    // 추격 재경로 스로틀 상태 (#254)
    private Vector3 m_lastChaseDestination;
    private float m_baseSpeed; // 진입 시점의 이동 속도 — 추격 질주 배율 적용 전 값(Exit에서 복원) (#254)
    private float m_baseAcceleration; // 진입 시점의 가속도 — 개체차를 덮어쓰지 않게 실제 값을 기억한다 (#568 후속)

    // 진입 시점의 도로 비용 — Exit에서 되돌린다. 인덱스 -1이면 도로 영역이 없는 구성. (#721)
    private int m_roadArea = -1;
    private float m_baseRoadCost = 1f;

    private readonly NpcResistConfig m_config;
    private readonly NpcFleeConfig m_fleeConfig;

    public NpcResistState(NpcController owner, NpcResistConfig config, NpcFleeConfig fleeConfig) : base(owner)
    {
        m_config = config;
        m_fleeConfig = fleeConfig;
    }

    public override void Enter()
    {
        // 표적을 추격하며 싸운다 — 이동을 멈추지 않고, 사거리 안으로 들어오면 stoppingDistance로 자연히 선다 (#254)
        m_owner.Agent.isStopped = false;
        m_owner.Agent.stoppingDistance = m_config.AttackRange * k_stopDistanceFactor;

        // 걸어오지 않고 달려온다 — 도주와 같은 질주 속도를 써서 공용 Run 클립이 발 미끄러짐 없이 맞는다 (#254)
        m_baseSpeed = m_owner.Agent.speed;
        m_owner.Agent.speed = m_baseSpeed * m_fleeConfig.SpeedMultiplier;

        // 가속도를 올려 선회 반경을 좁힌다 (#568 후속). 속도만 3배로 올리면 반경이 속도의 제곱으로
        // 커져(반경 = 속도²/가속도) 시민 기본값에서는 4.5m가 된다 — 정지 거리(1.6m)보다 크니
        // 표적이 원을 그리면 안쪽으로 못 꺾고 같이 공전한다.
        //
        // 각속도(angularSpeed)를 올리지 않는 이유: 바로 아래에서 updateRotation을 꺼 몸을 직접 돌리므로
        // (FaceTarget · AttackTurnSpeed) 각속도는 궤적에 관여하지 않는다.
        //
        // 오토브레이킹은 그대로 둔다 — 사거리 앞에 <b>서야</b> 하는 상태라, 끄면 표적을 밀고 지나간다.
        // 표적 발밑까지 가는 NpcChaseState(#568)가 끄는 것과 목적이 반대다.
        m_baseAcceleration = m_owner.Agent.acceleration;
        m_owner.Agent.acceleration = m_config.ChaseAcceleration;

        // 추격 중에는 도로를 피하지 않는다 (#721) — 전역 Road 비용 5(#634) 때문에 표적이 도로 위에 있으면
        // 최단 경로가 인도로 돌아 나가 쫓아오다 옆으로 샌다. 에이전트별 override라 배회 시민의 기피는 그대로다.
        m_roadArea = NpcNavAreas.RoadArea;
        if (m_roadArea >= 0)
        {
            m_baseRoadCost = m_owner.Agent.GetAreaCost(m_roadArea);
            m_owner.Agent.SetAreaCost(m_roadArea, 1f);
        }

        // 표적을 직접 바라보도록 수동 회전할 것이므로 에이전트 자동 회전을 끈다 — 안 그러면 서로 방향을 다툰다 (#220)
        m_owner.Agent.updateRotation = false;

        m_noTargetSeconds = 0f;

        // 첫 타격은 대기 없이 연다 (#692) — AttackInterval을 얹으면 사거리 안에서 저항에 들어가도
        // 1.5초를 쳐다보기만 했다. 연타가 빨라지진 않는다: 두 번째부터는 스윙 시점에 다시 얹는다(Tick).
        m_nextAttackTime = Time.time;

        m_pendingStrikeTime = k_noPendingStrike; // 직전 저항의 예약이 남아 첫 타격이 앞당겨지지 않게
        m_swingHoldUntil = 0f;

        m_lastChaseDestination = k_noDestination; // 첫 Tick에 무조건 목적지를 새로 잡게 한다
    }

    public override void Tick()
    {
        // 표적을 정하고(유발자 우선), 사거리 밖이면 추격·안이면 멈춰 타격, 그리고 표적을 향해 돈다 (#254·#220)
        Transform target = ResolveTarget();

        // 표적이 사라진 채로 일정 시간이 지나면 배회로 돌아간다 (#366 결정 7).
        // 제한시간 도주를 없애면서 Attack 상태의 시간 기반 출구가 사라졌는데,
        // 표적이 없으면 ChaseTarget이 에이전트를 세우고 사거리 밖이라 스윙도 하지 않아
        // 그대로 두면 NPC가 이 상태로 영구히 굳는다. 도주가 아니라 배회다 —
        // 때릴 상대가 사라진 NPC가 혼자 전력 질주할 이유가 없다. (기절 해제는 도주로
        // 복귀하지만 그쪽은 방금 맞은 직후라 상황이 다르다.)
        if (target == null)
        {
            m_noTargetSeconds += Time.deltaTime;
            if (m_noTargetSeconds >= m_config.NoTargetIdleSeconds)
            {
                Debug.Log($"저항 종료(표적 상실) — 배회 복귀: {m_owner.name}");
                m_owner.Reaction.ClearThreat();
                m_owner.StateMachine.ChangeState(NpcState.Idle);
                return;
            }
        }
        else
        {
            m_noTargetSeconds = 0f;
        }

        ChaseTarget(target);

        // 스윙 중에는 몸도 세운다 (#692) — 계속 돌면 540°/s가 부채꼴을 플레이어에 붙여 놔 #220의
        // 회피 창이 닫힌다. 구간은 스윙 홀드(0.9초 > 타격 프레임 0.73초).
        if (Time.time >= m_swingHoldUntil)
            FaceTarget(target);

        // 주기적 스윙 — 닿을 수 있을 때만(사거리 + 정면 부채꼴). 발행이 먼저, 데미지는 타격 프레임에 (#254·#220).
        // 부채꼴을 빼면 등 뒤에서 맞은 NPC가 9°만 돌고 허공을 친다 — 판정과 같은 각도를 쓴다 (#692).
        bool canStrike = target != null
            && (target.position - m_owner.transform.position).sqrMagnitude
                <= m_config.AttackRange * m_config.AttackRange
            && IsInFrontCone(target.position);
        if (canStrike && Time.time >= m_nextAttackTime)
        {
            m_nextAttackTime = Time.time + m_config.AttackInterval;
            // 변형을 서버에서 뽑아 전 피어에 넘긴다 — 데미지는 그 클립의 타격 오프셋에 맞춰 넣고(아래),
            // 같은 index가 애니메이션에도 가므로 화면 속 주먹이 닿는 순간과 HP 감소가 일치한다. (#220)
            int variant = Random.Range(0, m_config.SwingVariantCount);
            m_owner.Reaction.RaiseAttackSwing(variant);
            m_pendingStrikeTime = Time.time + m_config.SwingImpactOffset(variant);

            // 멈춰서 때린다 — 스윙 동안 추격 이동을 멈춰, 표적이 움직여도 미끄러지며 때리지 않는다 (팀 피드백)
            m_swingHoldUntil = Time.time + m_config.SwingHoldSeconds;
            if (m_owner.Agent.isOnNavMesh)
                m_owner.Agent.isStopped = true;
        }

        // 타격 프레임 도달 — 예약된 스윙의 데미지를 지금 넣는다. 범위 재수집도 이 순간에 하므로
        // 교전 중이던 플레이어가 전원 무력화됐으면 그 즉시 승리 (GDD 7-4 'HP 소진')
        if (m_pendingStrikeTime >= 0f && Time.time >= m_pendingStrikeTime)
        {
            m_pendingStrikeTime = k_noPendingStrike;
            if (SwingAttack())
            {
                Defeat("교전 플레이어 전원 무력화");
                return;
            }
        }
    }

    public override void Exit()
    {
        m_owner.Agent.isStopped = false;
        m_owner.Agent.updateRotation = true; // 이동 재개 시 에이전트가 다시 진행 방향으로 돈다
        m_owner.Agent.stoppingDistance = 0f; // 추격용으로 늘린 정지 거리를 원복 (#254)
        m_owner.Agent.speed = m_baseSpeed;   // 추격 질주 배율 원복 (#254)
        m_owner.Agent.acceleration = m_baseAcceleration; // 조향 원복 — 배회 시민이 급가속으로 튀지 않게 (#568 후속)

        // 도로 비용 원복 — 안 되돌리면 이 몸이 배회로 돌아간 뒤에도 차도를 지름길로 쓴다 (#721)
        if (m_roadArea >= 0)
            m_owner.Agent.SetAreaCost(m_roadArea, m_baseRoadCost);
    }

    /// <summary>
    /// 범위 타격 1회 — 사거리 안이면서 <b>정면 부채꼴 안</b>에 든 플레이어의 HP를 깎는다. (#220 방향 판정)
    /// 반환값: 정면에서 실제로 교전한 플레이어가 있었는데 전원 HP 0이면 true (플레이어 패배).
    /// </summary>
    private bool SwingAttack()
    {
        CollectPlayersInRange(m_config.AttackRange);

        int engaged = 0;    // 정면 부채꼴 안에서 실제로 노린 대상 수
        int aliveCount = 0; // 그중 타격 후에도 살아있는 수
        int struck = 0;     // 실제로 데미지가 들어간 수 — 타격음 판정용 (#817)
        foreach (PlayerHealth player in s_playerBuffer)
        {
            if (!IsInFrontCone(player.transform.position))
                continue; // 등 뒤·측면 — 스윙이 닿지 않는다

            if (!HasLineOfSight(player))
                continue; // 사이에 벽·소품이 있다 — 사거리·각도만으로는 걸러지지 않았다 (#839)

            engaged++;
            if (player.CurrentHp <= 0)
                continue;

            ((IDamageable)player).TakeDamage(m_config.AttackDamage, m_owner.gameObject);
            struck++;
            if (player.CurrentHp > 0)
                aliveCount++;
        }

        // engaged가 아니라 struck을 보는 이유: 쓰러진 몸은 위에서 건너뛰어 실제로는 맞지 않는다 (#817)
        if (struck > 0)
            m_owner.Reaction.RaiseAttackHit();

        if (engaged == 0)
            return false; // 정면에 아무도 없으면 허공에 휘두를 뿐 — 패배 판정은 제한 시간이 담당

        Debug.Log($"저항 범위 타격: {m_owner.name} → 정면 {engaged}명 (잔존 {aliveCount}명)");
        return aliveCount == 0;
    }

    /// <summary>
    /// 이번 틱의 표적 — 저항을 유발한 플레이어(<see cref="NpcReaction.ThreatTarget"/>)를 우선하고,
    /// 놓쳤으면 추격 반경(<see cref="NpcReaction.ThreatSearchRadius"/>) 안 가장 가까운 현장 플레이어로 폴백한다.
    /// 폴백 반경은 도주(#213)와 같은 값이라 "쫓을 상대"와 "피할 상대"의 기준이 어긋나지 않는다. 서버(또는 오프라인) 전용.
    /// </summary>
    private Transform ResolveTarget()
    {
        Transform threat = m_owner.Reaction.ThreatTarget;
        if (threat != null && IsStillEngaged(threat))
            return threat;

        PlayerHealth nearest = SuddenEventUtil.FindNearestFieldPlayer(
            m_owner.transform.position, m_owner.Reaction.ThreatSearchRadius);
        return nearest != null ? nearest.transform : null;
    }

    /// <summary>
    /// 유발자를 계속 표적으로 삼을 수 있는가 — <b>참조가 살아 있는 것만으로는 부족하다.</b>
    ///
    /// ThreatTarget은 오브젝트가 파괴될 때(연결 종료 등)만 null이 되므로, 이 검사가 없으면
    /// 유발자가 맵 끝까지 도망쳐도 표적이 계속 잡혀 <see cref="NpcResistConfig.NoTargetIdleSeconds"/>
    /// 복귀 타이머가 매 틱 리셋된다. 제한시간 도주(구 DefeatSeconds)를 없앤 뒤로는 그게 Attack의
    /// 유일한 출구라, 리셋되면 저항 NPC가 라운드 끝까지 질주로 따라붙는다.
    ///
    /// 다운된 유발자도 놓아준다 — 폴백 경로는 IsTargetable로 거르는데 이 경로만 통과하면
    /// 쓰러진 플레이어를 영구히 쫓는다.
    /// </summary>
    private bool IsStillEngaged(Transform threat)
    {
        float giveUpSqr = m_config.GiveUpDistance * m_config.GiveUpDistance;
        if ((threat.position - m_owner.transform.position).sqrMagnitude > giveUpSqr)
            return false;

        // 위협이 플레이어가 아니면(테스트용 더미 등) 거리 조건만 본다
        PlayerHealth player = threat.GetComponentInParent<PlayerHealth>();
        return player == null || player.IsTargetable;
    }

    /// <summary>
    /// 표적을 향해 이동한다 — 사거리 안(stoppingDistance)에 들면 NavMeshAgent가 스스로 멈춰 타격 사거리를 유지한다.
    /// 표적이 없으면 그 자리에 선다(제한 시간이 패배를 판정). 재경로는 NpcFleeState와 같은 스로틀로 묶는다. (#254)
    /// </summary>
    private void ChaseTarget(Transform target)
    {
        // 스윙 홀드 중엔 제자리 — 홀드가 끝나면 아래 경로가 isStopped를 되돌려 추격을 재개한다
        if (Time.time < m_swingHoldUntil)
            return;

        if (target == null)
        {
            if (m_owner.Agent.isOnNavMesh)
                m_owner.Agent.isStopped = true;
            return;
        }

        m_owner.Agent.isStopped = false;

        // 주기가 됐거나 목표가 충분히 움직였으면 — 둘 중 하나면 다시 잡는다 (기존 OR 동작 유지)
        bool moved = (target.position - m_lastChaseDestination).sqrMagnitude
            >= k_chaseRepathMoveThreshold * k_chaseRepathMoveThreshold;
        if (!moved && !m_owner.Repath.Due(NpcRepathChannel.Repath))
            return;

        m_owner.Repath.MarkDone(NpcRepathChannel.Repath);
        m_lastChaseDestination = target.position;
        if (m_owner.Agent.isOnNavMesh)
            m_owner.Agent.SetDestination(target.position);
    }

    /// <summary>표적을 향해 몸을 돌린다 — 정면 부채꼴 타격 판정의 기준 방향을 표적에 맞춘다. 서버(또는 오프라인) 전용. (#220)</summary>
    private void FaceTarget(Transform target)
    {
        if (target == null)
            return;

        Vector3 to = target.position - m_owner.transform.position;
        to.y = 0f; // 수평 회전(yaw)만 — NetworkTransform이 동기화하는 축과 일치 (SyncRotAngleY)
        if (to.sqrMagnitude < 0.0001f)
            return;

        Quaternion look = Quaternion.LookRotation(to);
        m_owner.transform.rotation = Quaternion.RotateTowards(
            m_owner.transform.rotation, look, m_config.AttackTurnSpeed * Time.deltaTime);
    }

    /// <summary>주어진 위치가 NPC 정면 부채꼴(AttackConeAngle) 안인지 — 수평 방향 기준.</summary>
    private bool IsInFrontCone(Vector3 position)
    {
        Vector3 to = position - m_owner.transform.position;
        to.y = 0f;
        if (to.sqrMagnitude < 0.0001f)
            return true; // 거의 겹쳐 있으면 방향이 무의미 — 명중으로 본다

        Vector3 forward = m_owner.transform.forward;
        forward.y = 0f;
        return Vector3.Angle(forward, to) <= m_config.AttackConeAngle * 0.5f;
    }

    /// <summary>
    /// NPC에서 그 플레이어까지 막힌 데 없이 닿는가 (#839). 사거리·부채꼴만으로는 사이의 벽·소품을
    /// 걸러내지 못해 관통 타격이 났었다 — 진압봉(<see cref="Baton"/>)의 <c>AimOcclusion</c>이
    /// 겹친 자기 콜라이더를 걸러내려고 제외 루트·피벗 거리까지 동원하는 것과 달리, 여기는
    /// <b>SphereCast가 아니라 Raycast</b>라 자기 겹침 문제 자체가 없다 — 레이캐스트는 원점이
    /// 이미 들어가 있는 콜라이더를 애초에 히트로 잡지 않는다(Unity 기본 동작). 단순 레이캐스트로 충분하다.
    ///
    /// <b>맞은 것이 그 플레이어 자신인지로 판정한다</b> — 벽이든 다른 대상이든 먼저 걸리면 막힌 것이다.
    /// 레이가 아무것도 못 맞히면(수치 오차로 표적 표면 바로 앞에서 거리가 끝나는 경우) 막힌 것으로
    /// 오판하지 않고 통과시킨다 — 오차단보다 오차히트가 낫다는 판단은 AimOcclusion과 같다.
    /// </summary>
    private bool HasLineOfSight(PlayerHealth player)
    {
        Vector3 origin = m_owner.transform.position + Vector3.up * k_losHeight;
        Vector3 target = player.transform.position + Vector3.up * k_losHeight;
        Vector3 toTarget = target - origin;
        float distance = toTarget.magnitude;
        if (distance < 0.01f)
            return true;

        if (
            !Physics.Raycast(
                origin, toTarget / distance, out RaycastHit hit, distance, HitLayers,
                QueryTriggerInteraction.Ignore)
        )
            return true;

        return hit.collider.GetComponentInParent<PlayerHealth>() == player;
    }

    /// <summary>플레이어 승리 실패 — 저항을 유발한 플레이어(없으면 근처 플레이어)를 위협 삼아 도주형으로 전환한다.</summary>
    private void Defeat(string reason)
    {
        Debug.Log($"저항 승리({reason}) — 도주 전환: {m_owner.name}");

        // 저항을 유발한 플레이어(수갑 채우려던 자)를 우선 위협으로 삼는다 — 제한 시간 내내 붙어 싸우던
        // 상대가 판정 직전 잠깐 멀어졌다고 도주를 포기하면 안 된다(그 순간 반경 재검색은 놓치기 쉽다, #205).
        // 유발자가 사라졌을 때(연결 종료 등)만 근처 플레이어로 폴백한다.
        // 폴백 반경은 NpcReaction.ThreatSearchRadius로 통일한다 — 도주가 회피 대상을 모으는 반경과
        // 같은 값이어야 "도망칠 상대"와 "피할 상대"의 기준이 어긋나지 않는다. (#213)
        Transform threat = m_owner.Reaction.ThreatTarget;
        if (threat == null)
        {
            PlayerHealth nearest = SuddenEventUtil.FindNearestFieldPlayer(
                m_owner.transform.position,
                m_owner.Reaction.ThreatSearchRadius
            );
            threat = nearest != null ? nearest.transform : null;
        }

        if (threat != null)
            m_owner.Reaction.StartFlee(threat);
        else
            m_owner.StateMachine.ChangeState(NpcState.Idle); // 유발자도 없고 주변에도 아무도 없으면 도망갈 이유가 없다
    }

    /// <summary>
    /// 반경 내 PlayerHealth를 중복 없이 s_playerBuffer에 모은다.
    ///
    /// <b>래그돌 본을 뺀다</b> (#692) — 사람 하나가 본만 11개라 16칸 버퍼는 때리는 자기 몸으로 먼저
    /// 찼다. 넘쳐도 잘린 개수만 돌아와 조용히 빗나간다(차량 치임 #673과 같은 함정).
    /// </summary>
    private void CollectPlayersInRange(float radius)
    {
        s_playerBuffer.Clear();
        int hitCount = Physics.OverlapSphereNonAlloc(
            m_owner.transform.position, radius, s_overlapBuffer, HitLayers);

        // 넘쳤으면 누군가는 잘렸다 — 조용히 안 맞는 것보다 로그가 남는 편이 낫다 (TrafficVehicle과 같은 방침)
        if (hitCount == s_overlapBuffer.Length)
            Debug.LogWarning($"저항 타격 판정 버퍼가 찼다 — 뒤로 밀린 대상이 잘렸을 수 있다: {m_owner.name}");

        for (int i = 0; i < hitCount; i++)
        {
            PlayerHealth player = s_overlapBuffer[i].GetComponentInParent<PlayerHealth>();
            if (player != null && !s_playerBuffer.Contains(player))
                s_playerBuffer.Add(player);
        }
    }
}
