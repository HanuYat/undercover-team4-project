using UnityEngine;

/// <summary>
/// 임시 거처 이송(Holding) 상태 — 경범죄 이벤트 NPC가 판정 후 임시 거처 지점까지 걸어가 도착하면
/// <see cref="NpcController.OnReachedHolding"/>을 발행한다(그걸 받아 SpawnedNpcEvent가 정리한다). (#291)
/// NpcDetainedState(#277)와 같은 "이송 + 도착" 구조지만, 도착 후 대기가 아니라 소멸 통보를 낸다.
/// 출구 탈출(<see cref="NpcController.HoldingSprint"/>, #310)이면 도주와 같은 배속으로 달린다 —
/// 모션은 NpcAnimationDriver의 속도 로코모션이 걷기/달리기를 가른다.
/// </summary>
public class NpcHoldingState : NpcStateBase
{
    // 도착 판정 거리(m) — NavMesh 끝점 오차 흡수 (NpcDetainedState와 동일 기준)
    private const float k_arriveDistance = 0.9f;

    private readonly NpcFleeConfig m_fleeConfig; // 질주 배속(SpeedMultiplier) 공유 — 도주·저항과 같은 값 (#310)

    private bool m_arrived;   // 도착 통보 완료 래치 — 중복 통보 방지
    private float m_baseSpeed; // 진입 시 원 속도 — 질주 배속을 걸었으면 Exit에서 되돌린다 (NpcFleeState와 동일 패턴)

    public NpcHoldingState(NpcController owner, NpcFleeConfig fleeConfig)
        : base(owner)
    {
        m_fleeConfig = fleeConfig;
    }

    public override void Enter()
    {
        m_arrived = false;
        m_owner.Agent.isStopped = false;
        m_owner.Agent.stoppingDistance = 0f;

        // 출구 탈출은 도주 배속으로 달린다 — 제압을 뿌리치고 떠나는 그림이라 어슬렁 걷지 않는다 (#310)
        m_baseSpeed = m_owner.Agent.speed;
        if (m_owner.HoldingSprint && m_fleeConfig != null)
            m_owner.Agent.speed = m_baseSpeed * m_fleeConfig.SpeedMultiplier;

        // 임시 거처가 배선되지 않은 씬 — 그 자리에서 도착(소멸) 처리 (SendToJail의 null cell 관례)
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
        m_owner.Agent.speed = m_baseSpeed; // 질주 배속 원복 — 재제압 후 다른 상태로 넘어가도 속도가 남지 않게

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
