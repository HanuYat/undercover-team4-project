using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// 원한 구역 수용(Detained) 상태 — 오검거당한 시민이 석방 대신 전용 구역까지 걸어가 대기한다. (#277)
/// 수감(NpcJailedState)과 같은 "이송 + 도착 정지" 구조지만, 수갑은 판정 시 이미 반환됐고(#229)
/// 여기 모인 시민은 팀 오검거 카운트가 임계치를 넘는 순간 추격(Chasing)으로 출동한다(#278) —
/// 전이는 WrongfulArrestPenalty(서버)가 StartPenaltyChase로 건다.
/// 인계 방치 탈출(#230)은 Captured 상태의 타이머라 여기서는 자연히 돌지 않는다 —
/// 카운트와 구역 인원이 어긋나는 일이 없다(구역 인원 = 팀 카운트 불변식).
/// 이송이 시작되면 <b>플레이어가 몸으로 길을 막거나 밀어낼 수 없다</b> — 수감과 같은 처리다(Enter):
/// 로컬 회피를 끄고, 겹쳐 서지 않게 구역 안의 자리를 배정받는다(WrongfulArrestPenalty).
/// </summary>
public class NpcDetainedState : NpcStateBase
{
    // 수용 지점 도착 판정 거리(m) — NavMesh 끝점 오차 흡수 (NpcJailedState와 동일 기준).
    // 여러 명이 한 지점에 모이지만 겹침은 회피가 아니라 배정된 자리가 막는다 (아래 SpotDestination)
    private const float k_arriveDistance = 0.9f;

    // 배정된 자리를 NavMesh에 스냅할 때의 탐색 반경(m) — 자리 간격(1.1m)보다 좁게 둬 옆자리로 넘어가지 않게
    private const float k_slotSnapRadius = 0.8f;

    private bool m_arrived; // 도착 정지 완료 — 반복 정지 처리 방지 래치

    // 이송 중의 회피 설정 — 도착해 세울 때 껐다가(이유는 Arrive 주석) Exit에서 이 값으로 되돌린다.
    // NpcJailedState와 같은 관례 — 프리팹 값이 달라져도 복원값이 조용히 어긋나지 않게 캡처해 둔다.
    private ObstacleAvoidanceType m_travelAvoidance;

    public NpcDetainedState(NpcController owner)
        : base(owner) { }

    public override void Enter()
    {
        m_arrived = false;
        m_travelAvoidance = m_owner.Agent.obstacleAvoidanceType;
        m_owner.Agent.isStopped = false;
        m_owner.Agent.stoppingDistance = 0f;

        // 로컬 회피를 끈다 — 이송 시작부터 구역에 선 뒤까지(복원은 Exit). 플레이어가 몸으로 길을
        // 막거나 밀어내지 못하게 하는 것이 목적이다. 원인·선택 근거와 대가는 NpcJailedState.Enter
        // 주석에 정리해 뒀다 — 여기도 같은 처리다.
        // 겹침 방지는 회피 대신 배정된 자리가 맡는다(WrongfulArrestPenalty → DetentionSlotOffset).
        m_owner.Agent.obstacleAvoidanceType = ObstacleAvoidanceType.NoObstacleAvoidance;

        // 구역이 배선되지 않은 씬 — 그 자리에서 수용된 것으로 처리한다 (SendToJail의 null cell 관례)
        if (m_owner.DetentionSpot == null)
        {
            Arrive();
            return;
        }

        // 경로를 못 잡으면(구역이 NavMesh 밖 등) 영원히 걷는 자세로 남는다 — 그 자리에서 수용 처리
        if (!m_owner.Agent.SetDestination(SpotDestination()))
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
        // 출동(추격 전이) 등으로 나갈 때 이동을 복구한다.
        // 회피 복원은 isOnNavMesh 가드 밖이다 — 넉백처럼 에이전트가 꺼진 순간에 나가면 가드에 걸려
        // 회피가 영구히 꺼진 채로 추격에 나선다(NpcJailedState.Exit과 같은 이유).
        m_owner.Agent.obstacleAvoidanceType = m_travelAvoidance;
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

    // 걸어갈 목적지 — 구역 지점 + 도착 순번으로 배정받은 자리(WrongfulArrestPenalty).
    // 유치장과 달리 영역 제한이 없다(원한 구역은 전용 NavMesh 영역이 아니다) — 전 영역에서 스냅한다.
    private Vector3 SpotDestination() =>
        GatherSlot.Resolve(
            m_owner.DetentionSpot.position,
            m_owner.DetentionSlotOffset,
            NavMesh.AllAreas,
            k_slotSnapRadius
        );
}
