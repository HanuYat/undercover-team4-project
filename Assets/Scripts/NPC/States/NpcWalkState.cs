using UnityEngine;

public class NpcWalkState : NpcStateBase
{
    private const int k_maxSampleAttempts = 10;
    private const float k_arriveThreshold = 0.5f;

    // 한 판정 구간에 이 거리(m)도 못 갔으면 막힘 — 배회 속도 2m/s면 0.5초에 1m는 간다.
    // Agent.velocity로는 못 잡는다: 회피가 장애물 표면을 따라 미끄러져 속도가 0으로 떨어지지 않는다
    // (도주가 #400에서 같은 이유로 실이동거리로 갈아탔다). (#661)
    private const float k_stuckMinProgress = 0.3f;

    // 막힘이 이 횟수 이어지면 목적지를 재추첨한다 — StuckCheck 주기 0.5초 × 4 = 2초
    private const int k_stuckStrikesToRepick = 4;

    // 도달 가능성 확인 횟수 상한 — 샘플 시도 전부에 CalculatePath를 돌리면 갇힌 NPC 하나가
    // 재추첨마다 10번씩 경로를 계산한다. 도주가 후보 상한을 4로 둔 것과 같은 이유.
    private const int k_maxReachabilityProbes = 4;

    // 후보를 NavMesh 위로 끌어당길 때 허용하는 거리(m) — 배회는 가까이 뽑으므로 좁게 잡는다
    private const float k_navSampleMaxDistance = 2f;

    private Vector3 m_lastProgressPosition;
    private int m_stuckStrikes;

    private readonly NpcWalkConfig m_config;

    public NpcWalkState(NpcController owner, NpcWalkConfig config) : base(owner)
    {
        m_config = config;
    }

    public override void Enter()
    {
        m_owner.Repath.MarkDone(NpcRepathChannel.StuckCheck);
        m_lastProgressPosition = m_owner.transform.position;
        m_stuckStrikes = 0;

        SetNextWanderPoint();
    }

    public override void Tick()
    {
        // 목적지에 도착하면 Idle로 전환해 잠깐 쉬었다가 다시 걷는다
        if (!m_owner.Agent.pathPending &&
            m_owner.Agent.remainingDistance <= m_owner.Agent.stoppingDistance + k_arriveThreshold)
        {
            m_owner.StateMachine.ChangeState(NpcState.Idle);
            return;
        }

        TickStuckWatch();
    }

    public override void Exit()
    {
        if (m_owner.Agent.isOnNavMesh)
            m_owner.Agent.ResetPath();
    }

    /// <summary>
    /// 막힘 감시 — 실제 이동 거리를 보고 제자리면 목적지를 재추첨한다. 군중 교착이나 좁은 구석에
    /// 낀 NPC가 스스로 풀리게 하는 안전망이다. 주기는 도주와 같은 StuckCheck 채널을 탄다 (#573). (#661)
    /// </summary>
    private void TickStuckWatch()
    {
        if (!m_owner.Repath.Due(NpcRepathChannel.StuckCheck))
            return;

        Vector3 position = m_owner.transform.position;
        float progress = Vector3.Distance(position, m_lastProgressPosition);
        m_lastProgressPosition = position;

        if (progress >= k_stuckMinProgress)
        {
            m_stuckStrikes = 0;
            return;
        }

        if (++m_stuckStrikes < k_stuckStrikesToRepick)
            return;
        m_stuckStrikes = 0;

        // 경로 상태를 함께 남긴다 — 부분 경로면 NavMesh 빈틈, 온전한데 못 가면 군중 교착이다
        Debug.Log(
            $"배회 막힘 — 위치 {position:F1} / 목적지 {m_owner.Agent.destination:F1} / "
                + $"경로 {m_owner.Agent.pathStatus}, 지점 재추첨: {m_owner.name}",
            m_owner
        );

        SetNextWanderPoint();
    }

    // 다음 배회 지점 — 규칙(도로 제외·부분 경로 배제)은 질주와 공유한다 (NpcMovePoint, #805)
    private void SetNextWanderPoint()
    {
        if (!NpcMovePoint.TryPick(
                m_owner.Agent,
                m_config.MinWanderDistance,
                m_config.WanderRadius,
                k_navSampleMaxDistance,
                k_maxSampleAttempts,
                k_maxReachabilityProbes,
                out Vector3 point,
                out bool reachable
            ))
            return; // 주변에 NavMesh가 없다 — 목적지를 주지 못한다

        if (!reachable)
        {
            // 전부 도달 불가 — 이 NPC는 NavMesh 조각에 갇혀 있다(잘린 섬, 접근 불가 구역).
            // 좌표는 #660 조사 재료다. 차선 지점이라도 주고 막힘 감시에 맡긴다.
            Debug.Log(
                $"배회 지점 전부 도달 불가 — 위치 {m_owner.transform.position:F1}: {m_owner.name}",
                m_owner
            );
        }

        m_owner.Agent.SetDestination(point);
    }
}
