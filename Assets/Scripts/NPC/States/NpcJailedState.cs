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

    public override void Tick()
    {
        // 최종 상태 — 서 있기만 한다. 감옥 안 배회는 붙이지 않았다 (#537 후속)
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
