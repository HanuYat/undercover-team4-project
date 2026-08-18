using UnityEngine;
using UnityEngine.AI;

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

    // 경로 계산용 재사용 인스턴스 — NavMeshPath는 할당이 비싸다. 서버에서만 Tick되므로 공유 안전.
    private static NavMeshPath s_pathProbe;

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

    /// <summary>origin에서 point까지 <b>끊기지 않는</b> 경로가 있는가 — 부분 경로는 도달 불가로 본다.</summary>
    private bool IsReachable(Vector3 origin, Vector3 point)
    {
        s_pathProbe ??= new NavMeshPath();

        return NavMesh.CalculatePath(origin, point, m_owner.Agent.areaMask, s_pathProbe)
            && s_pathProbe.status == NavMeshPathStatus.PathComplete;
    }

    private void SetNextWanderPoint()
    {
        Vector3 origin = m_owner.transform.position;

        // 도달 가능한 지점이 하나도 없을 때를 위한 차선 — 목적지를 아예 안 주면 NPC가 그 자리에 굳는다
        Vector3? fallback = null;
        int probes = 0;

        for (int i = 0; i < k_maxSampleAttempts && probes < k_maxReachabilityProbes; i++)
        {
            // 최소~최대 거리 사이의 랜덤 방향 지점을 뽑는다 — 너무 가까운 지점을 배제해 한두 걸음 걷고 마는 이동을 방지
            float angle = Random.Range(0f, Mathf.PI * 2f);
            float distance = Random.Range(m_config.MinWanderDistance, m_config.WanderRadius);
            Vector3 direction = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
            Vector3 candidate = origin + direction * distance;

            // 통행 마스크로 샘플 — 에이전트가 못 가는 영역(Jail)을 뽑으면 경로가 문 앞에서 끊긴다 (#415).
            // 도로는 여기서만 뺀다 — 배회하다 도로 한복판을 목적지로 잡으면 거기 멈춰 서서 치인다.
            // 에이전트 마스크 자체는 그대로라 <b>건너가는 경로는 여전히 도로를 지난다</b> (#634).
            if (!NavMesh.SamplePosition(candidate, out NavMeshHit hit, 2f,
                    NpcNavAreas.ExcludeRoad(m_owner.Agent.areaMask)))
                continue;

            fallback ??= hit.position;

            // SamplePosition은 "거기에 NavMesh가 있는가"만 답하지 벽 너머인지는 모른다 — 그대로
            // SetDestination하면 부분 경로가 나와 그 끝에 붙어 제자리 걸음이 된다 (#661).
            // 추격이 #568에서 pathStatus로, 도주가 CalculatePath로 거른 것과 같은 방향이다.
            probes++;
            if (!IsReachable(origin, hit.position))
                continue;

            m_owner.Agent.SetDestination(hit.position);
            return;
        }

        // 전부 도달 불가 — 이 NPC는 NavMesh 조각에 갇혀 있다(잘린 섬, 접근 불가 구역).
        // 좌표는 #660 조사 재료다. 차선 지점이라도 주고 막힘 감시에 맡긴다.
        if (fallback == null)
            return;

        Debug.Log($"배회 지점 전부 도달 불가 — 위치 {origin:F1}: {m_owner.name}", m_owner);
        m_owner.Agent.SetDestination(fallback.Value);
    }
}
