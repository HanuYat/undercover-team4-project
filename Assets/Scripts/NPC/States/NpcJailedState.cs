using UnityEngine;

/// <summary>
/// 수감(Jailed) 상태 — 배정된 배치 지점에 서 있는다. (GDD 7-2, #228/#462/#492/#537)
///
/// <b>걷기가 사라졌다</b> (#537). 감옥이 도시에서 분리된 격리 공간이 되면서 진입이 순간이동으로
/// 바뀌었고(<see cref="JailIntake"/>), 예전의 "놓인 자리에서 좌석까지 1.5~5.6m를 걸어가 앉는다"는
/// 구간과 그 구간이 실패할 때 쓰던 안전망(경로 실패·경로 상실·20초 타임아웃 → 좌석으로 워프)이
/// 통째로 필요 없어졌다 — 실패할 이동이 없다.
///
/// <b>좌석도 폐기됐다</b> (#537). 앉기 모션과 착석 플래그가 사라지고 배치 지점에 <b>서 있는다</b>.
///
/// 그래서 이 상태가 하는 일은 셋뿐이다: 이동을 끊고, 지점 위로 옮기고, 지점이 보는 방향으로 돌린다.
/// 매 프레임 할 일이 없어 <see cref="Tick"/>도 비어 있다.
///
/// 진입 경로는 <see cref="JailIntake"/>다 — 문 앞 E로 판정을 통과하면 그 순간 여기로 온다.
/// 빠져나가는 경로는 둘: 탈옥 방출(<see cref="JailbreakEvent"/>)과 플레이어의 반출(JailIntake.ServerExtract).
/// </summary>
public class NpcJailedState : NpcStateBase
{
    // 배치 지점에서 이만큼(m) 안에서만 어슬렁거린다 — 방이 좁아(내부 7.5m) 넓게 잡으면
    // 여럿이 같은 자리로 몰려 서로를 밀어낸다.
    private const float k_wanderRadius = 1.6f;

    // 도착 판정 여유(m) — stoppingDistance에 더해 쓴다. 딱 맞추려 들면 미세하게 떨며 멈추지 못한다.
    private const float k_arriveSlack = 0.15f;

    // 다음 목적지를 고르기까지 서 있는 시간(초) 범위 — 계속 걷기만 하면 우리를 도는 로봇처럼 보인다.
    private const float k_pauseSecondsMin = 1.5f;
    private const float k_pauseSecondsMax = 5f;

    // 다음에 움직일 시각. 서 있는 동안만 의미가 있다.
    private float m_nextMoveTime;

    public NpcJailedState(NpcController owner) : base(owner) { }

    public override void Enter()
    {
        StopMoving();

        if (m_owner.JailSpot == null)
            return; // 감옥이 배선되지 않은 테스트 씬 — 그 자리에 세운 것으로 처리한다

        // Warp = 위치를 즉시 옮기고 NavMesh에 다시 붙이는 것. 대상이 어디에 있었든(문 앞·도시 한복판)
        // 감옥 안 배치 지점으로 건너오는 유일한 수단이다 — 두 NavMesh 섬 사이에 경로가 없기 때문이다.
        //
        // 에이전트를 끄지 않으므로 재부착 실패로 굳을 위험은 없다. 반환값을 보는 이유는 실패가 조용하기
        // 때문이다: 지점이 NavMesh 밖이면 워프가 실패하고 대상은 <b>문 앞에 그대로 남는다</b> —
        // 눈으로는 "수감이 안 됐네"로만 보여 씬 배치 실수를 놓치기 쉽다.
        if (!m_owner.Agent.Warp(m_owner.JailSpot.position))
        {
            Debug.LogWarning(
                $"NpcJailedState: 배치 지점으로 워프 실패 — 감옥 밖에 남는다. "
                    + $"지점이 감옥 NavMesh 위에 있는지 확인할 것: {m_owner.JailSpot.name}",
                m_owner
            );
            return;
        }

        m_owner.transform.rotation = SpotRotation();
    }

    /// <summary>
    /// 감옥 안 배회 — 배치 지점 둘레를 어슬렁거린다. 서버(또는 오프라인)에서만 실제로 움직이고,
    /// 클라이언트는 NetworkTransform이 실어다 주는 결과만 본다.
    ///
    /// <b>가둬 둔 사람도 살아 있어야 한다</b> — 배치 지점에 못 박아 두면 마네킹으로 보이고,
    /// 본부 CCTV로 감옥을 볼 때 화면이 정지 화면과 구분되지 않는다.
    ///
    /// 돌아다니는 범위를 <see cref="k_wanderRadius"/>로 묶는 이유는 방이 좁아서다(내부 7.5m).
    /// 방 전체를 목표로 삼으면 여럿이 같은 문 앞에 몰려 서로를 밀어낸다 — 자기 자리 근처만
    /// 맴돌게 하면 인원이 늘어도 고르게 흩어진 채로 남는다.
    /// </summary>
    public override void Tick()
    {
        if (m_owner.JailSpot == null || !m_owner.Agent.isOnNavMesh)
            return;

        // 걷는 중 — 도착했는지만 본다
        if (!m_owner.Agent.isStopped)
        {
            if (m_owner.Agent.pathPending)
                return;

            if (m_owner.Agent.remainingDistance > m_owner.Agent.stoppingDistance + k_arriveSlack)
                return;

            BeginPause();
            return;
        }

        // 쉬는 중 — 시간이 되면 다음 목적지를 고른다
        if (Time.time < m_nextMoveTime)
            return;

        BeginWander();
    }

    public override void Exit()
    {
        // 탈옥·반출로 풀려날 경우를 대비해 이동을 복구한다
        if (m_owner.Agent.isOnNavMesh)
        {
            m_owner.Agent.isStopped = false;
            m_owner.Agent.ResetPath();
        }
    }

    // 배치 지점 둘레에서 갈 수 있는 한 점을 골라 걷기 시작한다. 못 고르면 그냥 더 쉰다.
    private void BeginWander()
    {
        Vector2 offset = Random.insideUnitCircle * k_wanderRadius;
        Vector3 target = m_owner.JailSpot.position + new Vector3(offset.x, 0f, offset.y);

        // 방 밖으로 새지 않게 NavMesh 위로 스냅한다 — 감옥은 별도 섬이라 이 표본이 곧 방 안이다
        if (!UnityEngine.AI.NavMesh.SamplePosition(
                target, out UnityEngine.AI.NavMeshHit hit, k_wanderRadius, m_owner.Agent.areaMask))
        {
            BeginPause();
            return;
        }

        m_owner.Agent.isStopped = false;
        if (!m_owner.Agent.SetDestination(hit.position))
            BeginPause(); // 경로를 못 잡았다 — 다음 차례에 다시 고른다
    }

    // 잠시 선다 — 계속 걷기만 하면 우리 안을 도는 로봇처럼 보인다.
    private void BeginPause()
    {
        StopMoving();
        m_nextMoveTime = Time.time + Random.Range(k_pauseSecondsMin, k_pauseSecondsMax);
    }

    // 이동을 끊는다 — 끌려오던 관성이 남아 배치 지점에서 밀려나지 않게 속도까지 지운다.
    private void StopMoving()
    {
        m_owner.Agent.velocity = Vector3.zero;
        if (m_owner.Agent.isOnNavMesh)
        {
            m_owner.Agent.isStopped = true;
            m_owner.Agent.ResetPath();
        }
    }

    // 서서 바라볼 방향 — 지점 forward의 수평 성분만 쓴다(지점이 기울어 배치돼도 몸은 안 기운다).
    private Quaternion SpotRotation()
    {
        Vector3 forward = m_owner.JailSpot.forward;
        forward.y = 0f;
        return forward.sqrMagnitude > 0.0001f
            ? Quaternion.LookRotation(forward)
            : m_owner.transform.rotation;
    }
}
