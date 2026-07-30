using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// 수감(Jailed) 상태 — 인계존 판정에서 진범·경범죄로 확정된 NPC가 유치장까지 걸어가 그 자리에 멈춰 선다. (GDD 7-2, #228)
/// 이송("걸어가는 중")과 정지("도착해 멈춤")를 한 상태 안에서 처리한다 —
/// 연행(NpcEscortedState)이 추종과 근접 정지를 한 상태로 다루는 것과 같은 구조다 (#97).
/// 멈춰 선 뒤에는 스스로 다른 상태로 전이하지 않는 최종 상태다 (탈출 이벤트는 별도 이슈).
/// 이송이 시작되면 <b>플레이어가 몸으로 길을 막거나 밀어낼 수 없다</b> — 로컬 회피를 끄고(Enter),
/// 겹쳐 서지 않게 셀 안의 자리를 배정받는다(JailZone.ReserveCell).
///
/// 정산 수용 인원 계상(JailZone.Admit)은 판정 시점에 CustodyRouter가 이미 끝내므로(#340) 이 상태는
/// 계상에 관여하지 않는다 — 셀까지 걸어가 멈추는 연출만 담당한다.
/// </summary>
public class NpcJailedState : NpcStateBase
{
    // 수용 지점에 이만큼(m) 다가오면 도착으로 본다 — NavMesh 경로의 끝점 오차와 발 위치 차이를 흡수한다
    private const float k_arriveDistance = 0.5f;

    // 배정된 자리를 NavMesh에 스냅할 때의 탐색 반경(m) — 자리 간격(0.8m)보다 좁게 둬 옆자리로 넘어가지 않게
    private const float k_slotSnapRadius = 0.6f;

    private bool m_stopped; // 도착해 멈춤 — 정지 처리를 한 번만 하기 위한 래치

    // 이송 중의 회피 설정 — 셀에 세울 때 껐다가(이유는 StopAtCell 주석) Exit에서 이 값으로 되돌린다.
    // 상수로 박지 않는 이유는 NpcProneCollider가 서기 캡슐을 캡처하는 것과 같다: 프리팹마다 값이
    // 달라질 수 있고, 그때 복원값만 조용히 어긋나면 원인을 찾기 어렵다.
    // Enter가 항상 원본을 본다 — 같은 상태로의 재전이는 스킵되고(NpcStateMachine.ChangeState),
    // 다른 상태를 거쳐 돌아오면 그 사이 Exit이 이미 복원했다.
    private ObstacleAvoidanceType m_travelAvoidance;

    public NpcJailedState(NpcController owner) : base(owner) { }

    public override void Enter()
    {
        m_stopped = false;
        m_travelAvoidance = m_owner.Agent.obstacleAvoidanceType;
        m_owner.Agent.isStopped = false;
        m_owner.Agent.stoppingDistance = 0f;

        // 로컬 회피를 끈다 — 이송을 시작하는 순간부터 셀에 선 뒤까지 계속(복원은 Exit).
        //
        // 플레이어가 몸으로 길을 막거나 밀어내는 것을 없앤다. 밀리는 게 물리가 아니라는 점이 핵심이다:
        // NPC의 Rigidbody는 kinematic이라 플레이어의 CharacterController가 밀 수 없고, 반대로 플레이어가
        // NPC 캡슐에 막힌다. 움직이는(또는 못 움직이는) 주체는 NPC 자신이다 — 플레이어 프리팹에 달린
        // NavMeshObstacle을 피하려고 에이전트가 스스로 비켜나거나 앞이 막혔다고 판단하기 때문이다.
        // isStopped는 경로 추종 속도만 죽이므로 이 변위를 막지 못한다.
        //
        // 회피를 꺼도 통과 대상은 플레이어뿐이다 — 이 프로젝트에서 NavMeshObstacle이 붙은 것은 플레이어
        // 프리팹 하나이고(유치장 문짝은 애초에 NavMesh 베이크에서 빠져 NPC가 통과한다, JailDoor 주석),
        // 벽·지형은 NavMesh 자체가 막으므로 그대로 걸린다.
        //
        // 밧줄 끌기(StartRopeDrag)처럼 에이전트를 통째로 끄지는 않는다: 그쪽은 위치를 직접 대입하려고
        // 제어권을 가져가는 것이라 목적이 반대이고, 껐다 켜면 NavMesh 재부착(Warp) 실패로 NPC가 그 자리에
        // 굳는 위험군을 물려받는다. 회피만 끄면 isOnNavMesh가 유지돼 탈출 시 Exit의 복구가 그냥 통한다.
        //
        // 대가는 NPC끼리도 서로 비켜나지 않는 것이다 — 그래서 같은 셀 지점에 겹쳐 서지 않게
        // 자리를 미리 배정받는다(JailZone.ReserveCell → JailSlotOffset, 아래 CellDestination).
        m_owner.Agent.obstacleAvoidanceType = ObstacleAvoidanceType.NoObstacleAvoidance;

        // 유치장 내부는 시민이 못 들어가는 별도 NavMesh 영역(Jail)이다 — 수감 대상만 이 순간 통행을 얻는다 (#415).
        // SetDestination보다 반드시 먼저 켜야 셀까지의 경로가 잡힌다 — 목적지인 셀 지점이 Jail 영역 안이라
        // 통행 없이 경로를 요청하면 그대로 실패한다.
        m_owner.SetJailAccess(true);

        // 유치장이 없는 테스트 씬 — 그 자리에서 멈춘 것으로 처리한다 (멍하니 걷는 자세로 남지 않게)
        if (m_owner.JailCell == null)
        {
            StopAtCell();
            return;
        }

        // 경로를 못 잡으면(수용 지점이 NavMesh 밖 등) 영원히 걷는 자세로 남는다 — 그 자리에서 멈춤 처리
        if (!m_owner.Agent.SetDestination(CellDestination()))
        {
            Debug.LogWarning($"NpcJailedState: 유치장 경로 실패 — 그 자리에서 정지 처리: {m_owner.name}", m_owner);
            StopAtCell();
        }
    }

    public override void Tick()
    {
        if (m_stopped)
            return; // 도착해 멈춤 — 최종 상태

        if (m_owner.Agent.pathPending)
            return;

        if (m_owner.Agent.remainingDistance > k_arriveDistance)
            return;

        StopAtCell();
    }

    public override void Exit()
    {
        // 탈출(별도 이슈) 등으로 풀려날 경우를 대비해 이동을 복구한다
        m_owner.Agent.obstacleAvoidanceType = m_travelAvoidance; // 회피 없이 풀려나면 군중을 뚫고 걷는다
        if (m_owner.Agent.isOnNavMesh)
        {
            m_owner.Agent.isStopped = false;
            m_owner.Agent.ResetPath();
        }
    }

    // 셀 도착 — 그 자리에 세운다. 정산 계상은 판정 시점에 이미 끝났으므로(#340) 여기서는 이동만 멈춘다.
    // (밀리지 않게 하는 회피 끄기는 Enter에서 이미 걸려 있다 — 이송 중에도 필요하기 때문)
    private void StopAtCell()
    {
        m_stopped = true;

        m_owner.Agent.isStopped = true;
        m_owner.Agent.velocity = Vector3.zero; // 감속 관성까지 끊어 수용 지점을 지나쳐 밀리지 않게
        if (m_owner.Agent.isOnNavMesh)
            m_owner.Agent.ResetPath();
    }

    // 걸어갈 목적지 — 배정받은 셀 지점 + 그 안에서의 자리 오프셋(JailZone.ReserveCell).
    // 스냅을 Jail 영역으로 제한한다: 오프셋이 창살 밖 바닥에 걸리면 수감자가 감옥 밖에 서 버린다.
    // Jail 영역이 없는 프로젝트(단독 테스트 씬)면 전 영역으로 폴백한다 — SetJailAccess와 같은 관례.
    private Vector3 CellDestination()
    {
        int mask = NpcController.JailAreaMask;
        return GatherSlot.Resolve(
            m_owner.JailCell.position,
            m_owner.JailSlotOffset,
            mask != 0 ? mask : NavMesh.AllAreas,
            k_slotSnapRadius
        );
    }
}
