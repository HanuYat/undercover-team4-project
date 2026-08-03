using UnityEngine;

/// <summary>
/// 임시 거처 이송(Holding) 상태 — 경범죄 이벤트 NPC가 판정 후 임시 거처 지점까지 걸어가 도착하면
/// <see cref="NpcController.OnReachedHolding"/>을 발행한다(그걸 받아 SpawnedNpcEvent가 정리한다). (#291)
/// NpcDetainedState(#277)와 같은 "이송 + 도착" 구조지만, 도착 후 대기가 아니라 소멸 통보를 낸다.
/// 대응 Animator 상태가 없어 NpcAnimationDriver가 Walk 모션을 대여한다.
/// </summary>
public class NpcHoldingState : NpcStateBase
{
    // 도착 판정 거리(m) — NavMesh 끝점 오차 흡수 (NpcDetainedState와 동일 기준)
    private const float k_arriveDistance = 0.9f;

    private bool m_arrived; // 도착 통보 완료 래치 — 중복 통보 방지

    public NpcHoldingState(NpcController owner)
        : base(owner) { }

    public override void Enter()
    {
        m_arrived = false;
        m_owner.Agent.isStopped = false;
        m_owner.Agent.stoppingDistance = 0f;

        // 임시 거처가 배선되지 않은 씬 — 그 자리에서 도착(소멸) 처리 (SendToJail의 null seat 관례)
        if (m_owner.HoldingSpot == null)
        {
            Arrive();
            return;
        }

        // 경로 실패(지점이 NavMesh 밖 등) — 영원히 걷는 자세로 남지 않게 그 자리에서 도착 처리
        if (!m_owner.Agent.SetDestination(m_owner.HoldingSpot.position))
        {
            Debug.LogWarning(
                $"NpcHoldingState: 임시 거처 경로 실패 — 그 자리에서 소멸 통보: {m_owner.name}",
                m_owner
            );
            Arrive();
        }
    }

    public override void Tick()
    {
        if (m_arrived)
            return;
        if (m_owner.Agent.pathPending)
            return;
        if (m_owner.Agent.remainingDistance > k_arriveDistance)
            return;

        Arrive();
    }

    public override void Exit()
    {
        if (m_owner.Agent.isOnNavMesh)
        {
            m_owner.Agent.isStopped = false;
            m_owner.Agent.ResetPath();
        }
    }

    // 도착 — 정지 후 소멸 통보. 통보를 받은 SpawnedNpcEvent는 다음 틱에 despawn한다(파괴-중-틱 회피).
    private void Arrive()
    {
        m_arrived = true;

        if (m_owner.Agent.isOnNavMesh)
        {
            m_owner.Agent.isStopped = true;
            m_owner.Agent.velocity = Vector3.zero;
            m_owner.Agent.ResetPath();
        }

        m_owner.NotifyReachedHolding();
    }
}
