using UnityEngine;

/// <summary>
/// 연행(Escorted) 상태 — 체포 성공 직후 진입해 체포한 플레이어를 따라 이동한다. (이슈 #59)
/// 플레이어와 너무 멀어지면 그 자리에서 Captured로 돌아가 멈춘다.
/// 이번 빌드는 순응형(즉시 연행) 기준 — 도주형/저항형 반응은 후속 빌드(GDD 6-1).
/// </summary>
public class NpcEscortedState : NpcStateBase
{
    private const float k_repathInterval = 0.2f;      // 경로 재계산 최소 간격(초) — 매 프레임 재계산 방지
    private const float k_repathMoveThreshold = 0.5f; // 목표가 이만큼(m) 움직였을 때만 재계산

    private float m_repathTimer;
    private Vector3 m_lastTargetPos;
    private float m_baseSpeed;

    public NpcEscortedState(NpcController owner) : base(owner) { }

    public override void Enter()
    {
        m_owner.Agent.isStopped = false;
        // 플레이어 등에 딱 붙지 않도록 추종 거리만큼 앞에서 멈춘다
        m_owner.Agent.stoppingDistance = m_owner.EscortFollowDistance;
        m_baseSpeed = m_owner.Agent.speed;
        m_repathTimer = 0f;

        if (m_owner.EscortTarget != null)
        {
            m_lastTargetPos = m_owner.EscortTarget.position;
            m_owner.Agent.SetDestination(m_owner.EscortTarget.position);
        }
    }

    public override void Tick()
    {
        Transform target = m_owner.EscortTarget;
        if (target == null)
        {
            // 대상 소실(플레이어 파괴 등) — 그 자리에서 체포 상태로 멈춘다
            m_owner.StopEscort();
            return;
        }

        float distance = Vector3.Distance(m_owner.transform.position, target.position);

        // 너무 멀어지면 연행이 풀리고 그 자리에서 체포된 채 멈춘다
        if (distance > m_owner.EscortBreakDistance)
        {
            Debug.Log($"연행 해제 — 거리 이탈 ({distance:F1}m): {m_owner.name}");
            m_owner.StopEscort();
            return;
        }

        // 뒤처지면 속도를 올려 따라잡는다
        m_owner.Agent.speed = distance > m_owner.EscortBoostDistance
            ? m_baseSpeed * m_owner.EscortBoostMultiplier
            : m_baseSpeed;

        // 경로 재계산은 "주기 경과 + 목표가 충분히 움직임" 둘 다 만족할 때만 (비용 절약)
        m_repathTimer += Time.deltaTime;
        if (m_repathTimer >= k_repathInterval &&
            (target.position - m_lastTargetPos).sqrMagnitude >= k_repathMoveThreshold * k_repathMoveThreshold)
        {
            m_repathTimer = 0f;
            m_lastTargetPos = target.position;
            m_owner.Agent.SetDestination(target.position);
        }
    }

    public override void Exit()
    {
        // 연행 중 바꿨던 값들을 원복한다
        m_owner.Agent.speed = m_baseSpeed;
        m_owner.Agent.stoppingDistance = 0f;
        if (m_owner.Agent.isOnNavMesh)
            m_owner.Agent.ResetPath();
    }
}
