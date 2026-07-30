using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// 수감(Jailed) 상태 — 인계존 판정에서 진범·경범죄로 확정된 NPC가 유치장 좌석까지 걸어가 앉는다. (GDD 7-2, #228/#462)
/// 이송("걸어가는 중")·정렬("좌석 방향으로 돌기")·착석("앉아 있음")을 한 상태 안에서 처리한다 —
/// 연행(NpcEscortedState)이 추종과 근접 정지를 한 상태로 다루는 것과 같은 구조다 (#97).
/// 앉은 뒤에는 스스로 다른 상태로 전이하지 않는 최종 상태다 (탈옥 방출은 JailbreakEvent가 걸어 준다).
/// 이송이 시작되면 <b>플레이어가 몸으로 길을 막거나 밀어낼 수 없다</b> — 로컬 회피를 끈다(Enter).
///
/// <b>앉히는 이유(#462)</b> — 예전에는 셀 지점 몇 개를 돌려 쓰며 남는 인원을 계산된 오프셋(GatherSlot)으로
/// 밖으로 퍼뜨렸다. 인원이 늘면 그 자리가 문↔셀 통로 위에 떨어져 뒤따라 오는 NPC의 진입과 유치장 안
/// 플레이어의 탈출을 막았고, 자리를 아예 못 잡으면 그 자리(보통 입구)에 굳었다. 손으로 배치한 좌석만
/// 쓰면 통로 위에 설 자리가 존재하지 않는다 — 배정은 JailZone.ReserveSeat이 한다.
///
/// 정산 수용 인원 계상(JailZone.Admit)은 판정 시점에 CustodyRouter가 이미 끝내므로(#340) 이 상태는
/// 계상에 관여하지 않는다 — 좌석까지 걸어가 앉는 연출만 담당한다.
/// </summary>
public class NpcJailedState : NpcStateBase
{
    // 좌석에 이만큼(m) 다가오면 도착으로 본다 — NavMesh 경로의 끝점 오차를 흡수한다.
    // 예전(0.5m)보다 좁다: 남은 오차는 도착 순간 Warp로 좌석에 맞추므로, 눈에 띄는 스냅이 되지 않을 만큼만 남긴다
    private const float k_arriveDistance = 0.25f;

    // 앉는 방향으로 도는 속도(도/초)와 앉기를 시작할 정렬 오차(도).
    // 몸을 돌린 뒤 앉는 순서라야 벤치에 등을 대고 앉는 그림이 된다 — 도착 즉시 앉으면 걸어온 방향 그대로 앉는다
    private const float k_seatTurnDegreesPerSecond = 360f;
    private const float k_seatFacingTolerance = 6f;

    // 좌석까지 이 시간(초) 안에 도착하지 못하면 좌석으로 직접 옮겨 앉힌다.
    // 유치장은 넓지 않아 정상이면 몇 초다 — 이만큼 걸렸다는 건 경로가 끊겼다는 뜻이다 (아래 SeatByWarp)
    private const float k_travelTimeoutSeconds = 20f;

    // 수감 진행 단계 — 걷기 → 좌석 방향으로 돌기 → 앉음(최종)
    private enum SeatPhase
    {
        Walking,
        Turning,
        Seated,
    }

    private SeatPhase m_phase;

    // 이송 제한 시각 — 이때까지 도착하지 못하면 좌석으로 옮긴다
    private float m_travelDeadline;

    // 이송 중의 회피 설정 — 셀에 세울 때 껐다가(이유는 Enter 주석) Exit에서 이 값으로 되돌린다.
    // 상수로 박지 않는 이유는 NpcProneCollider가 서기 캡슐을 캡처하는 것과 같다: 프리팹마다 값이
    // 달라질 수 있고, 그때 복원값만 조용히 어긋나면 원인을 찾기 어렵다.
    // Enter가 항상 원본을 본다 — 같은 상태로의 재전이는 스킵되고(NpcStateMachine.ChangeState),
    // 다른 상태를 거쳐 돌아오면 그 사이 Exit이 이미 복원했다.
    private ObstacleAvoidanceType m_travelAvoidance;

    // 회전 주도권도 같은 이유로 캡처한다 — 앉는 방향을 맞추는 동안만 에이전트에서 넘겨받는다
    private bool m_travelUpdateRotation;

    public NpcJailedState(NpcController owner) : base(owner) { }

    public override void Enter()
    {
        m_phase = SeatPhase.Walking;
        m_travelAvoidance = m_owner.Agent.obstacleAvoidanceType;
        m_travelUpdateRotation = m_owner.Agent.updateRotation;
        m_owner.Agent.isStopped = false;
        m_owner.Agent.stoppingDistance = 0f;

        // 로컬 회피를 끈다 — 이송을 시작하는 순간부터 앉은 뒤까지 계속(복원은 Exit).
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
        // 대가는 NPC끼리도 서로 비켜나지 않는 것이다 — 그래서 겹쳐 서지 않게 좌석을 하나씩 배정받는다
        // (JailZone.ReserveSeat). 앉은 수감자를 지나 걸어가는 이송 NPC는 그 몸을 통과한다.
        m_owner.Agent.obstacleAvoidanceType = ObstacleAvoidanceType.NoObstacleAvoidance;

        // 유치장 내부는 시민이 못 들어가는 별도 NavMesh 영역(Jail)이다 — 수감 대상만 이 순간 통행을 얻는다 (#415).
        // SetDestination보다 반드시 먼저 켜야 좌석까지의 경로가 잡힌다 — 목적지인 좌석이 Jail 영역 안이라
        // 통행 없이 경로를 요청하면 그대로 실패한다.
        m_owner.SetJailAccess(true);

        // 유치장이 없는 테스트 씬 — 그 자리에 앉은 것으로 처리한다 (멍하니 걷는 자세로 남지 않게)
        if (m_owner.JailSeat == null)
        {
            SitDown();
            return;
        }

        m_travelDeadline = Time.time + k_travelTimeoutSeconds;

        // 경로를 못 잡으면(좌석이 NavMesh 밖 등) 영원히 걷는 자세로 남는다 — 좌석으로 옮겨 앉힌다
        if (!m_owner.Agent.SetDestination(m_owner.JailSeat.position))
        {
            Debug.LogWarning(
                $"NpcJailedState: 유치장 좌석 경로 실패 — 좌석으로 옮겨 앉힌다: {m_owner.name}",
                m_owner
            );
            SeatByWarp();
        }
    }

    public override void Tick()
    {
        switch (m_phase)
        {
            case SeatPhase.Walking:
                TickWalk();
                break;

            case SeatPhase.Turning:
                TickTurn();
                break;

            // Seated는 최종 상태 — 매 프레임 할 일이 없다
        }
    }

    public override void Exit()
    {
        // 앉은 자세를 먼저 푼다 — 곧 이어지는 상태 전이(도주 등)가 새 모션을 시드할 수 있게 (#462)
        m_owner.SetSeated(false);

        // 탈출(#231) 등으로 풀려날 경우를 대비해 이동을 복구한다
        m_owner.Agent.obstacleAvoidanceType = m_travelAvoidance; // 회피 없이 풀려나면 군중을 뚫고 걷는다
        m_owner.Agent.updateRotation = m_travelUpdateRotation;
        if (m_owner.Agent.isOnNavMesh)
        {
            m_owner.Agent.isStopped = false;
            m_owner.Agent.ResetPath();
        }
    }

    // 좌석까지 걷는 중 — 도착하면 정렬로, 제 시간에 못 가면 좌석으로 옮긴다.
    private void TickWalk()
    {
        if (m_owner.Agent.pathPending)
            return;

        // 경로를 잃었다(재계산 실패 등) — 이 상태에서 remainingDistance는 0으로 보고되므로 도착으로 오인된다.
        // 먼저 걸러 내지 않으면 문 밖에서 도착 처리가 돌아 좌석까지 순간이동한다.
        if (!m_owner.Agent.hasPath)
        {
            Debug.LogWarning(
                $"NpcJailedState: 좌석까지의 경로를 잃음 — 좌석으로 옮겨 앉힌다: {m_owner.name}",
                m_owner
            );
            SeatByWarp();
            return;
        }

        if (m_owner.Agent.remainingDistance <= k_arriveDistance)
        {
            ArriveAtSeat();
            return;
        }

        // 제 시간에 도착하지 못했다 — 문이 닫혀 경로가 끊겼거나 목적지가 닿지 않는 경우다.
        // 걷는 자세로 영원히 남거나 통로 한복판에 서 있게 두지 않는다 (#462의 입구 고착)
        if (Time.time >= m_travelDeadline)
        {
            Debug.LogWarning(
                $"NpcJailedState: 좌석까지 {k_travelTimeoutSeconds}초 안에 도착하지 못함 — 좌석으로 옮겨 앉힌다: {m_owner.name}",
                m_owner
            );
            SeatByWarp();
        }
    }

    // 앉는 방향으로 도는 중 — 다 돌면 앉는다.
    private void TickTurn()
    {
        Quaternion target = SeatRotation();
        Transform body = m_owner.transform;

        body.rotation = Quaternion.RotateTowards(
            body.rotation,
            target,
            k_seatTurnDegreesPerSecond * Time.deltaTime
        );

        if (Quaternion.Angle(body.rotation, target) > k_seatFacingTolerance)
            return;

        body.rotation = target; // 남은 오차를 지운다 — 앉은 자세가 벤치와 비뚤어지지 않게
        SitDown();
    }

    // 좌석 도착 — 이동을 끊고 좌석 위에 정확히 세운 뒤 앉는 방향으로 돌기 시작한다.
    private void ArriveAtSeat()
    {
        StopMoving();

        // 회전 주도권을 넘겨받는다 — 에이전트가 쥔 채로 돌리면 되돌려져 정렬이 끝나지 않는다
        m_owner.Agent.updateRotation = false;

        // 경로 끝점 오차(최대 k_arriveDistance)를 좌석 위로 흡수한다 — 앉는 자세는 좌석과 몇 cm만 어긋나도
        // 벤치에 걸터앉은 것처럼 보인다. Warp는 에이전트를 끄지 않으므로 NavMesh 재부착 실패 위험이 없다
        // (밧줄 끌기가 물려받는 그 위험군 — Enter 주석).
        if (m_owner.JailSeat != null)
            m_owner.Agent.Warp(m_owner.JailSeat.position);

        m_phase = SeatPhase.Turning;
    }

    // 좌석으로 직접 옮겨 앉힌다 — 경로가 없거나 제 시간에 도착하지 못한 경우의 마지막 수단.
    // 그 자리에 세우던 옛 처리는 보통 입구 근처에서 걸려 뒤따라 오는 NPC와 플레이어의 동선을 막았다 (#462).
    private void SeatByWarp()
    {
        if (m_owner.JailSeat != null)
        {
            if (m_owner.Agent.Warp(m_owner.JailSeat.position))
                m_owner.transform.rotation = SeatRotation();
            else
                Debug.LogWarning(
                    $"NpcJailedState: 좌석으로 워프 실패 — 그 자리에서 앉힌다: {m_owner.name}",
                    m_owner
                );
        }

        SitDown();
    }

    // 앉는다 — 최종 상태. 앉은 모션은 동기화 플래그(NpcController.IsSeated)를 타고 전 피어에서 재생된다.
    private void SitDown()
    {
        StopMoving();
        m_phase = SeatPhase.Seated;
        m_owner.SetSeated(true);
    }

    // 이동을 끊는다 — 좌석을 지나쳐 밀리지 않게 감속 관성까지 끊는다.
    private void StopMoving()
    {
        m_owner.Agent.isStopped = true;
        m_owner.Agent.velocity = Vector3.zero;
        if (m_owner.Agent.isOnNavMesh)
            m_owner.Agent.ResetPath();
    }

    // 앉아서 바라볼 방향 — 좌석의 Z축(forward)에서 수평 성분만 쓴다.
    // 좌석 지점이 기울어져 배치돼도 몸이 같이 기울지 않게 한다.
    private Quaternion SeatRotation()
    {
        if (m_owner.JailSeat == null)
            return m_owner.transform.rotation;

        Vector3 forward = m_owner.JailSeat.forward;
        forward.y = 0f;
        return forward.sqrMagnitude > 0.0001f
            ? Quaternion.LookRotation(forward)
            : m_owner.transform.rotation;
    }
}
