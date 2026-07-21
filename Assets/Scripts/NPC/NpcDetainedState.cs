using UnityEngine;

/// <summary>
/// 원한 구역 수용(Detained) 상태 — 오검거당한 시민이 석방 대신 전용 구역까지 걸어가 대기한다. (#277)
/// 수감(NpcJailedState)과 같은 "이송 + 도착 정지" 구조지만, 수갑은 판정 시 이미 반환됐고(#229)
/// 여기 모인 시민은 팀 오검거 카운트가 임계치를 넘는 순간 추격(Chasing)으로 출동한다(#278) —
/// 전이는 WrongfulArrestPenalty(서버)가 StartPenaltyChase로 건다.
/// 인계 방치 탈출(#230)은 Captured 상태의 타이머라 여기서는 자연히 돌지 않는다 —
/// 카운트와 구역 인원이 어긋나는 일이 없다(구역 인원 = 팀 카운트 불변식).
/// </summary>
public class NpcDetainedState : NpcStateBase
{
    // 수용 지점 도착 판정 거리(m) — NavMesh 끝점 오차 흡수 (NpcJailedState와 동일 기준).
    // 여러 명이 한 지점에 모이므로 겹침은 NavMesh 회피가 흩어 준다 (JailZone.ReserveCell 관례)
    private const float k_arriveDistance = 0.9f;

    private bool m_arrived; // 도착 정지 완료 — 반복 정지 처리 방지 래치

    public NpcDetainedState(NpcController owner)
        : base(owner) { }

    public override void Enter()
    {
        m_arrived = false;
        m_owner.Agent.isStopped = false;
        m_owner.Agent.stoppingDistance = 0f;

        // 구역이 배선되지 않은 씬 — 그 자리에서 수용된 것으로 처리한다 (SendToJail의 null cell 관례)
        if (m_owner.DetentionSpot == null)
        {
            Arrive();
            return;
        }

        // 경로를 못 잡으면(구역이 NavMesh 밖 등) 영원히 걷는 자세로 남는다 — 그 자리에서 수용 처리
        if (!m_owner.Agent.SetDestination(m_owner.DetentionSpot.position))
        {
            Debug.LogWarning(
                $"NpcDetainedState: 원한 구역 경로 실패 — 그 자리에서 수용 처리: {m_owner.name}",
                m_owner
            );
            Arrive();
        }
    }

    public override void Tick()
    {
        if (m_arrived)
            return; // 도착 후에는 출동(StartPenaltyChase) 전이를 기다리며 대기

        if (m_owner.Agent.pathPending)
            return;

        if (m_owner.Agent.remainingDistance > k_arriveDistance)
            return;

        Arrive();
    }

    public override void Exit()
    {
        // 출동(추격 전이) 등으로 나갈 때 이동을 복구한다
        if (m_owner.Agent.isOnNavMesh)
        {
            m_owner.Agent.isStopped = false;
            m_owner.Agent.ResetPath();
        }
    }

    // 도착 정지 — 그 자리에 서서 출동을 기다린다. 수용 인원 집계는 판정 시점(매니저 로스터)이 담당하므로 통보는 없다.
    private void Arrive()
    {
        m_arrived = true;

        m_owner.Agent.isStopped = true;
        m_owner.Agent.velocity = Vector3.zero; // 감속 관성으로 수용 지점을 지나쳐 밀리지 않게
        if (m_owner.Agent.isOnNavMesh)
            m_owner.Agent.ResetPath();
    }
}
