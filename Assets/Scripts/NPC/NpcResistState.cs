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
    // 패배 판정 후 도주 위협 대상을 찾는 반경 배율 — 공격 범위보다 넓게 잡아
    // 멀리서 접근 중인 플레이어에게서도 도망칠 방향이 나온다
    private const float k_threatSearchRadiusMultiplier = 5f;
    private const int k_maxOverlapHits = 16;

    // 서버에서만 Tick되므로 버퍼 공유 안전 — 매 타격마다의 할당 방지
    private static readonly Collider[] s_overlapBuffer = new Collider[k_maxOverlapHits];
    private static readonly List<PlayerData> s_playerBuffer = new List<PlayerData>(8);

    private const float k_noPendingStrike = -1f;

    private float m_resistStartTime;
    private float m_nextAttackTime;
    // 스윙을 시작한 뒤 타격 프레임을 기다리는 예약 시각 — 데미지를 스윙 시작이 아니라 이 시점에 넣어
    // 눈에 보이는 타격과 HP 감소를 일치시킨다. k_noPendingStrike면 대기 중인 타격 없음. (#220)
    private float m_pendingStrikeTime = k_noPendingStrike;
    // 마지막 스윙을 시작한 시각 — 피격 로그에서 "스윙 후 몇 초 만에 맞았는지"(오프셋)를 보여주기 위함 (#220)
    private float m_lastSwingStartTime;

    public NpcResistState(NpcController owner) : base(owner) { }

    public override void Enter()
    {
        // 이동을 멈추고 그 자리에서 버틴다
        m_owner.Agent.isStopped = true;
        if (m_owner.Agent.isOnNavMesh)
            m_owner.Agent.ResetPath();

        // 표적을 직접 바라보도록 수동 회전할 것이므로 에이전트 자동 회전을 끈다 — 안 그러면 서로 방향을 다툰다 (#220)
        m_owner.Agent.updateRotation = false;

        m_owner.ResetSubdueGauge();
        m_resistStartTime = Time.time;
        m_nextAttackTime = Time.time + m_owner.ResistAttackInterval;
        m_pendingStrikeTime = k_noPendingStrike; // 직전 저항의 예약이 남아 첫 타격이 앞당겨지지 않게
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

        // 표적을 향해 돈다 — 정면 부채꼴 타격 판정의 기준 방향을 표적에 맞춘다 (#220)
        FaceTarget();

        // 주기적 스윙 — 애니메이션을 먼저 발행하고 데미지는 타격 프레임까지 미룬다.
        // 그래야 눈에 보이는 스윙 준비 동작과 실제 HP 감소 순간이 일치하고, 준비 중 벗어난 플레이어는 빗나간다 (#220)
        if (Time.time >= m_nextAttackTime)
        {
            m_nextAttackTime = Time.time + m_owner.ResistAttackInterval;
            m_lastSwingStartTime = Time.time;
            m_owner.RaiseAttackSwing();
            m_pendingStrikeTime = Time.time + m_owner.StrikeOffsetSeconds;
            Debug.Log($"[저항 스윙] {m_owner.name} 스윙 시작 (t={Time.time:F2}s, 타격 예정 +{m_owner.StrikeOffsetSeconds:F2}s)");
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

            int hpBefore = player.CurrentHp;
            ((IDamageable)player).TakeDamage(m_owner.ResistAttackDamage, m_owner.gameObject);

            // 피격 순간 로그 — 언제(스윙 시작 후 몇 초) 어디서(거리·정면각) 맞아 HP가 얼마가 됐는지 (#220)
            Vector3 to = player.transform.position - m_owner.transform.position;
            to.y = 0f;
            float dist = to.magnitude;
            Vector3 fwd = m_owner.transform.forward; fwd.y = 0f;
            float angle = Vector3.Angle(fwd, to);
            Debug.Log($"[저항 피격] {player.name} HP {hpBefore}→{player.CurrentHp} " +
                      $"(t={Time.time:F2}s, 스윙 후 {Time.time - m_lastSwingStartTime:F2}s | 거리 {dist:F2}m, 정면각 {angle:F0}°) ← {m_owner.name}");

            if (player.CurrentHp > 0)
                aliveCount++;
        }

        if (engaged == 0)
            return false; // 정면에 아무도 없으면 허공에 휘두를 뿐 — 패배 판정은 제한 시간이 담당

        return aliveCount == 0;
    }

    /// <summary>표적(위협 대상, 없으면 사거리 내 가장 가까운 플레이어)을 향해 몸을 돌린다. 서버(또는 오프라인) 전용.</summary>
    private void FaceTarget()
    {
        Transform target = m_owner.ThreatTarget != null
            ? m_owner.ThreatTarget
            : FindNearestPlayer(m_owner.ResistAttackRange);
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
        Transform threat = m_owner.ThreatTarget;
        if (threat == null)
            threat = FindNearestPlayer(m_owner.ResistAttackRange * k_threatSearchRadiusMultiplier);

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

    private Transform FindNearestPlayer(float radius)
    {
        CollectPlayersInRange(radius);

        Transform nearest = null;
        float nearestSqr = float.MaxValue;
        foreach (PlayerData player in s_playerBuffer)
        {
            float sqr = (player.transform.position - m_owner.transform.position).sqrMagnitude;
            if (sqr < nearestSqr)
            {
                nearestSqr = sqr;
                nearest = player.transform;
            }
        }
        return nearest;
    }
}
