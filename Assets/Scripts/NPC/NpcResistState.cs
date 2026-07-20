using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 저항(Attack) 상태 — 수갑 채널링 성공 순간 그 자리에서 버티며 싸운다. (GDD 6-1/7-4, #76/#79)
/// 표적을 바라보며 주기적으로 정면 부채꼴 타격을 휘둘러 사거리 안 플레이어의 HP를 깎고(선제 공격, 방향 판정 #220),
/// ApplySubdueHit로 제압 게이지가 0이 되면 체포(Captured)된다 — 여럿이 때리면 빨리 끝난다(협동 인센티브).
/// 제한 시간 안에 제압당하지 않거나 교전 중인 플레이어가 전원 무력화되면
/// 플레이어 패배 — 도주형으로 전환되어 달아난다 (GDD 7-4 3항).
/// </summary>
public class NpcResistState : NpcStateBase
{
    private const int k_maxOverlapHits = 16;

    // 서버에서만 Tick되므로 버퍼 공유 안전 — 매 타격마다의 할당 방지
    private static readonly Collider[] s_overlapBuffer = new Collider[k_maxOverlapHits];
    private static readonly List<PlayerData> s_playerBuffer = new List<PlayerData>(8);

    private const float k_noPendingStrike = -1f;

    // 추격 이동 (#254)
    private const float k_stopDistanceFactor = 0.8f;       // 사거리 안쪽 이 비율 지점에 멈춰 타격 사거리를 유지
    private const float k_chaseRepathInterval = 0.25f;     // 목적지 재계산 최소 간격(초) — NpcFleeState와 같은 스로틀
    private const float k_chaseRepathMoveThreshold = 0.5f; // 표적이 이만큼(m) 움직였을 때만 재계산
    private static readonly Vector3 k_noDestination = new Vector3(float.PositiveInfinity, 0f, 0f);

    private float m_resistStartTime;
    private float m_nextAttackTime;
    // 스윙을 시작한 뒤 타격 프레임을 기다리는 예약 시각 — 데미지를 스윙 시작이 아니라 이 시점에 넣어
    // 눈에 보이는 타격과 HP 감소를 일치시킨다. k_noPendingStrike면 대기 중인 타격 없음. (#220)
    private float m_pendingStrikeTime = k_noPendingStrike;

    // 추격 재경로 스로틀 상태 (#254)
    private float m_repathTimer;
    private Vector3 m_lastChaseDestination;
    private float m_baseSpeed; // 진입 시점의 이동 속도 — 추격 질주 배율 적용 전 값(Exit에서 복원) (#254)

    public NpcResistState(NpcController owner) : base(owner) { }

    public override void Enter()
    {
        // 표적을 추격하며 싸운다 — 이동을 멈추지 않고, 사거리 안으로 들어오면 stoppingDistance로 자연히 선다 (#254)
        m_owner.Agent.isStopped = false;
        m_owner.Agent.stoppingDistance = m_owner.ResistAttackRange * k_stopDistanceFactor;

        // 걸어오지 않고 달려온다 — 도주와 같은 질주 속도를 써서 공용 Run 클립이 발 미끄러짐 없이 맞는다 (#254)
        m_baseSpeed = m_owner.Agent.speed;
        m_owner.Agent.speed = m_baseSpeed * m_owner.FleeSpeedMultiplier;

        // 표적을 직접 바라보도록 수동 회전할 것이므로 에이전트 자동 회전을 끈다 — 안 그러면 서로 방향을 다툰다 (#220)
        m_owner.Agent.updateRotation = false;

        m_owner.ResetSubdueGauge();
        m_resistStartTime = Time.time;
        m_nextAttackTime = Time.time + m_owner.ResistAttackInterval;
        m_pendingStrikeTime = k_noPendingStrike; // 직전 저항의 예약이 남아 첫 타격이 앞당겨지지 않게

        m_repathTimer = 0f;
        m_lastChaseDestination = k_noDestination; // 첫 Tick에 무조건 목적지를 새로 잡게 한다
    }

    public override void Tick()
    {
        // 게이지가 다 깎이면 제압 성공 — 체포
        if (m_owner.SubdueGauge <= 0f)
        {
            Debug.Log($"저항 제압됨: {m_owner.name}");
            // 체포로 반응이 끝나므로 위협 참조를 여기서 정리한다. Exit()에 넣으면 안 된다 —
            // Defeat()의 StartFlee()가 세팅한 위협을 그 직후 Exit()가 지워 도주 전환이 깨진다 (#205).
            m_owner.ClearThreat();
            m_owner.StateMachine.ChangeState(NpcState.Captured);
            return;
        }

        // 표적을 정하고(유발자 우선), 사거리 밖이면 추격·안이면 멈춰 타격, 그리고 표적을 향해 돈다 (#254·#220)
        Transform target = ResolveTarget();
        ChaseTarget(target);
        FaceTarget(target);

        // 주기적 스윙 — 표적이 사거리 안일 때만 휘두른다. 추격 중(사거리 밖)엔 스윙하지 않아
        // 헛스윙·스윙 중 미끄러짐을 막는다 (#254). 애니메이션을 먼저 발행하고 데미지는 타격 프레임까지
        // 미뤄, 눈에 보이는 준비 동작과 HP 감소 순간을 맞추고 준비 중 벗어난 플레이어는 빗나가게 한다 (#220)
        bool targetInRange = target != null
            && (target.position - m_owner.transform.position).sqrMagnitude
                <= m_owner.ResistAttackRange * m_owner.ResistAttackRange;
        if (targetInRange && Time.time >= m_nextAttackTime)
        {
            m_nextAttackTime = Time.time + m_owner.ResistAttackInterval;
            // 변형을 서버에서 뽑아 전 피어에 넘긴다 — 데미지는 그 클립의 타격 오프셋에 맞춰 넣고(아래),
            // 같은 index가 애니메이션에도 가므로 화면 속 주먹이 닿는 순간과 HP 감소가 일치한다. (#220)
            int variant = m_owner.NextSwingVariant();
            m_owner.RaiseAttackSwing(variant);
            m_pendingStrikeTime = Time.time + m_owner.SwingImpactOffset(variant);
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

        // 제한 시간 안에 못 꺾었으면 제압 실패 — 뿌리치고 도주 (GDD 7-4 '제압 실패')
        if (Time.time - m_resistStartTime > m_owner.ResistDefeatSeconds)
        {
            Defeat("제압 제한 시간 초과");
        }
    }

    public override void Exit()
    {
        m_owner.Agent.isStopped = false;
        m_owner.Agent.updateRotation = true; // 이동 재개 시 에이전트가 다시 진행 방향으로 돈다
        m_owner.Agent.stoppingDistance = 0f; // 추격용으로 늘린 정지 거리를 원복 (#254)
        m_owner.Agent.speed = m_baseSpeed;   // 추격 질주 배율 원복 (#254)
    }

    /// <summary>
    /// 범위 타격 1회 — 사거리 안이면서 <b>정면 부채꼴 안</b>에 든 플레이어의 HP를 깎는다. (#220 방향 판정)
    /// 반환값: 정면에서 실제로 교전한 플레이어가 있었는데 전원 HP 0이면 true (플레이어 패배).
    /// </summary>
    private bool SwingAttack()
    {
        CollectPlayersInRange(m_owner.ResistAttackRange);

        int engaged = 0;    // 정면 부채꼴 안에서 실제로 노린 대상 수
        int aliveCount = 0; // 그중 타격 후에도 살아있는 수
        foreach (PlayerData player in s_playerBuffer)
        {
            if (!IsInFrontCone(player.transform.position))
                continue; // 등 뒤·측면 — 스윙이 닿지 않는다

            engaged++;
            if (player.CurrentHp <= 0)
                continue;

            ((IDamageable)player).TakeDamage(m_owner.ResistAttackDamage, m_owner.gameObject);
            if (player.CurrentHp > 0)
                aliveCount++;
        }

        if (engaged == 0)
            return false; // 정면에 아무도 없으면 허공에 휘두를 뿐 — 패배 판정은 제한 시간이 담당

        Debug.Log($"저항 범위 타격: {m_owner.name} → 정면 {engaged}명 (잔존 {aliveCount}명)");
        return aliveCount == 0;
    }

    /// <summary>
    /// 이번 틱의 표적 — 저항을 유발한 플레이어(<see cref="NpcController.ThreatTarget"/>)를 우선하고,
    /// 사라졌으면 추격 반경(<see cref="NpcController.ThreatSearchRadius"/>) 안 가장 가까운 현장 플레이어로 폴백한다.
    /// 폴백 반경은 도주(#213)와 같은 값이라 "쫓을 상대"와 "피할 상대"의 기준이 어긋나지 않는다. 서버(또는 오프라인) 전용.
    /// </summary>
    private Transform ResolveTarget()
    {
        if (m_owner.ThreatTarget != null)
            return m_owner.ThreatTarget;

        PlayerData nearest = SuddenEventUtil.FindNearestFieldPlayer(
            m_owner.transform.position, m_owner.ThreatSearchRadius);
        return nearest != null ? nearest.transform : null;
    }

    /// <summary>
    /// 표적을 향해 이동한다 — 사거리 안(stoppingDistance)에 들면 NavMeshAgent가 스스로 멈춰 타격 사거리를 유지한다.
    /// 표적이 없으면 그 자리에 선다(제한 시간이 패배를 판정). 재경로는 NpcFleeState와 같은 스로틀로 묶는다. (#254)
    /// </summary>
    private void ChaseTarget(Transform target)
    {
        if (target == null)
        {
            if (m_owner.Agent.isOnNavMesh)
                m_owner.Agent.isStopped = true;
            return;
        }

        m_owner.Agent.isStopped = false;

        m_repathTimer += Time.deltaTime;
        bool moved = (target.position - m_lastChaseDestination).sqrMagnitude
            >= k_chaseRepathMoveThreshold * k_chaseRepathMoveThreshold;
        if (m_repathTimer < k_chaseRepathInterval && !moved)
            return;

        m_repathTimer = 0f;
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
            m_owner.transform.rotation, look, m_owner.AttackTurnSpeed * Time.deltaTime);
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
        return Vector3.Angle(forward, to) <= m_owner.AttackConeAngle * 0.5f;
    }

    /// <summary>플레이어 승리 실패 — 저항을 유발한 플레이어(없으면 근처 플레이어)를 위협 삼아 도주형으로 전환한다.</summary>
    private void Defeat(string reason)
    {
        Debug.Log($"저항 승리({reason}) — 도주 전환: {m_owner.name}");

        // 저항을 유발한 플레이어(수갑 채우려던 자)를 우선 위협으로 삼는다 — 제한 시간 내내 붙어 싸우던
        // 상대가 판정 직전 잠깐 멀어졌다고 도주를 포기하면 안 된다(그 순간 반경 재검색은 놓치기 쉽다, #205).
        // 유발자가 사라졌을 때(연결 종료 등)만 근처 플레이어로 폴백한다.
        // 폴백 반경은 NpcController.ThreatSearchRadius로 통일한다 — 도주가 회피 대상을 모으는 반경과
        // 같은 값이어야 "도망칠 상대"와 "피할 상대"의 기준이 어긋나지 않는다. (#213)
        Transform threat = m_owner.ThreatTarget;
        if (threat == null)
        {
            PlayerData nearest = SuddenEventUtil.FindNearestFieldPlayer(
                m_owner.transform.position,
                m_owner.ThreatSearchRadius
            );
            threat = nearest != null ? nearest.transform : null;
        }

        if (threat != null)
            m_owner.StartFlee(threat);
        else
            m_owner.StateMachine.ChangeState(NpcState.Idle); // 유발자도 없고 주변에도 아무도 없으면 도망갈 이유가 없다
    }

    /// <summary>반경 내 PlayerData를 중복 없이 s_playerBuffer에 모은다.</summary>
    private void CollectPlayersInRange(float radius)
    {
        s_playerBuffer.Clear();
        int hitCount = Physics.OverlapSphereNonAlloc(m_owner.transform.position, radius, s_overlapBuffer);
        for (int i = 0; i < hitCount; i++)
        {
            PlayerData player = s_overlapBuffer[i].GetComponentInParent<PlayerData>();
            if (player != null && !s_playerBuffer.Contains(player))
                s_playerBuffer.Add(player);
        }
    }
}
