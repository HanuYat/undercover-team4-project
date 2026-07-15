using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 저항(Attack) 상태 — 수갑 채널링 성공 순간 그 자리에서 버티며 싸운다. (GDD 6-1/7-4, #76/#79)
/// 주기적으로 범위 타격을 휘둘러 근처 플레이어의 HP를 깎고(선제 공격),
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

    private float m_resistStartTime;
    private float m_nextAttackTime;

    public NpcResistState(NpcController owner) : base(owner) { }

    public override void Enter()
    {
        // 이동을 멈추고 그 자리에서 버틴다
        m_owner.Agent.isStopped = true;
        if (m_owner.Agent.isOnNavMesh)
            m_owner.Agent.ResetPath();

        m_owner.ResetSubdueGauge();
        m_resistStartTime = Time.time;
        m_nextAttackTime = Time.time + m_owner.ResistAttackInterval;
    }

    public override void Tick()
    {
        // 게이지가 다 깎이면 제압 성공 — 체포
        if (m_owner.SubdueGauge <= 0f)
        {
            Debug.Log($"저항 제압됨: {m_owner.name}");
            m_owner.StateMachine.ChangeState(NpcState.Captured);
            return;
        }

        // 주기적 범위 타격 — 교전 중이던 플레이어가 전원 무력화됐으면 그 즉시 승리 (GDD 7-4 'HP 소진')
        if (Time.time >= m_nextAttackTime)
        {
            m_nextAttackTime = Time.time + m_owner.ResistAttackInterval;
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
    }

    /// <summary>
    /// 범위 타격 1회 — 반경 내 모든 플레이어의 HP를 깎는다.
    /// 반환값: 범위 안에 플레이어가 있었는데 전원 HP 0이면 true (플레이어 패배).
    /// </summary>
    private bool SwingAttack()
    {
        CollectPlayersInRange(m_owner.ResistAttackRange);
        if (s_playerBuffer.Count == 0)
            return false; // 아무도 안 붙어 있으면 허공에 휘두를 뿐 — 패배 판정은 제한 시간이 담당

        int aliveCount = 0;
        foreach (PlayerData player in s_playerBuffer)
        {
            if (player.CurrentHp <= 0)
                continue;

            ((IDamageable)player).TakeDamage(m_owner.ResistAttackDamage, m_owner.gameObject);
            if (player.CurrentHp > 0)
                aliveCount++;
        }

        Debug.Log($"저항 범위 타격: {m_owner.name} → {s_playerBuffer.Count}명 (잔존 {aliveCount}명)");
        return aliveCount == 0;
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
