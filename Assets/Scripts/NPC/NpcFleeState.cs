using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// 도주(Run) 상태 — 수갑 채널링 성공 순간 뿌리치고 추격하는 플레이어들에게서 달아난다. (GDD 6-1, #76)
/// 추적자와 충분히 멀어지면 도주 성공으로 보고 배회로 복귀한다.
/// 잡는 방법: 근접 제압 홀드(NpcSubdueInteractable) 또는 테이저(후속 아이템).
///
/// 도주 방향은 위협 1명이 아니라 <see cref="NpcController.ThreatSearchRadius"/> 안의 플레이어 전원을 보고 고른다 (#213) —
/// 협공하면 두 사람 사이로 뛰어드는 대신 옆으로 빠지고, 완전히 포위되면 저항으로 전환한다.
/// </summary>
public class NpcFleeState : NpcStateBase
{
    private const float k_arriveThreshold = 0.5f;

    // 후보 방향 개수 — 360도를 이 수로 등분해 훑는다. 16개면 22.5도 간격이라
    // 좌우 협공의 정답인 수직 방향도, 1:1 추격의 정답인 정반대 방향도 후보에 들어온다.
    private const int k_directionSampleCount = 16;

    // 후보 도착점을 NavMesh 위로 끌어당길 때 허용하는 최대 거리(m)
    private const float k_navSampleMaxDistance = 2f;

    // 경로(origin→point) 위에서 위협의 최근접점이 이 t(정규화 위치)보다 앞(interior)에 있을 때만
    // clearance 제약을 건다. t가 0에 가까우면 최근접점이 origin이라 그 위협을 '등지고' 뛰는 방향이므로,
    // 이미 위협에 붙어 있어 origin이 clearance 안이더라도 그 방향까지 막지 않는다 (#213 오판 수정).
    private const float k_pathClearanceMinT = 0.05f;

    // 추격 중 플레이어가 방향을 틀면 목적지 도착을 기다리지 않고 도주 방향을 다시 잡는다 (#96)
    // 매 프레임 재계산은 비싸므로 NpcEscortedState와 같은 스로틀링(주기 + 이동량 임계치)을 쓴다
    private const float k_repathInterval = 0.25f; // 재계산 최소 간격(초)
    private const float k_repathThreatMoveThreshold = 1f; // 추격자가 이만큼(m) 움직였을 때만 재계산

    // 서버에서만 Tick되므로 버퍼 공유 안전 — 매 재계산마다의 할당 방지 (NpcResistState와 같은 방식)
    private static readonly List<Transform> s_threatBuffer = new List<Transform>(8);

    private float m_baseSpeed;
    private float m_repathTimer;
    private Vector3 m_lastThreatPos;
    private float m_fleeStartTime;
    private bool m_transitioningToResist;

    public NpcFleeState(NpcController owner)
        : base(owner) { }

    public override void Enter()
    {
        m_owner.Agent.isStopped = false;
        m_baseSpeed = m_owner.Agent.speed;
        m_owner.Agent.speed = m_baseSpeed * m_owner.FleeSpeedMultiplier;

        m_repathTimer = 0f;
        m_fleeStartTime = Time.time;
        m_transitioningToResist = false;
        if (m_owner.ThreatTarget != null)
            m_lastThreatPos = m_owner.ThreatTarget.position;

        SetFleePoint();
    }

    public override void Tick()
    {
        // 이탈 판정은 도주 방향 산출과 반경이 다르다 — 방향은 근처(ThreatSearchRadius) 플레이어만 보면 되지만,
        // 이탈은 FleeEscapeDistance(25m)까지 아무도 없어야 성립한다.
        CollectThreats(m_owner.FleeEscapeDistance);
        Transform nearest = NearestThreat(m_owner.transform.position);

        if (nearest == null)
        {
            // 추격권 안에 아무도 없다 — 위협이 사라졌든 다 따돌렸든 도망갈 이유가 없다
            m_owner.StateMachine.ChangeState(NpcState.Idle);
            return;
        }

        // 위협 대상이 사라졌으면(연결 종료 등) 가장 가까운 추격자로 폴백한다 —
        // 도주형에는 이 폴백이 없어 Idle로 빠지던 문제 (#213). NpcResistState의 폴백(#205)과 대칭.
        if (m_owner.ThreatTarget == null)
            m_owner.StartFlee(nearest);

        // 도주 지점에 도착했으면 다음 지점을 잡는다 (기존 동작)
        bool arrived =
            !m_owner.Agent.pathPending
            && m_owner.Agent.remainingDistance
                <= m_owner.Agent.stoppingDistance + k_arriveThreshold;

        // 추격자가 방향을 틀면 도착 전에도 도주 방향을 다시 잡는다 — 주기 + 이동량으로 스로틀링 (#96)
        // 기준은 고정된 위협 1명이 아니라 그 시점의 가장 가까운 추격자다.
        m_repathTimer += Time.deltaTime;
        bool threatMoved =
            (nearest.position - m_lastThreatPos).sqrMagnitude
            >= k_repathThreatMoveThreshold * k_repathThreatMoveThreshold;
        bool shouldRepath = m_repathTimer >= k_repathInterval && threatMoved;

        if (arrived || shouldRepath)
        {
            m_repathTimer = 0f;
            m_lastThreatPos = nearest.position;
            SetFleePoint();
        }
    }

    public override void Exit()
    {
        m_owner.Agent.speed = m_baseSpeed;
        if (m_owner.Agent.isOnNavMesh)
            m_owner.Agent.ResetPath();

        // 포위로 저항 전환하는 경우엔 위협을 남긴다 — StartResist가 세팅한 위협을 그 직후 이 Exit가
        // 지워버리면 저항이 유발자를 잃는다. NpcResistState가 ClearThreat를 Exit이 아니라
        // 체포 시점에 두는 것과 같은 이유. (#205, #213)
        if (!m_transitioningToResist)
            m_owner.ClearThreat();
    }

    /// <summary>
    /// 360도를 훑어 도주 지점을 고른다 — 경로가 플레이어를 스치는 방향은 버리고,
    /// 남은 후보 중 도착점에서 가장 가까운 플레이어까지의 거리가 최대인 쪽을 택한다.
    /// 남는 후보가 없으면 포위가 성립한 것이므로 저항으로 전환한다.
    /// </summary>
    private void SetFleePoint()
    {
        Vector3 origin = m_owner.transform.position;

        CollectThreats(m_owner.ThreatSearchRadius);
        if (s_threatBuffer.Count == 0)
        {
            // 회피 반경 안에 아무도 없다 — 추격자는 멀리 있으니(이탈 판정은 Tick이 담당) 아무 방향으로나 계속 뛴다
            if (m_owner.ThreatTarget != null)
                s_threatBuffer.Add(m_owner.ThreatTarget);
            else
                return;
        }

        float clearanceSqr = m_owner.FleeClearanceRadius * m_owner.FleeClearanceRadius;

        Vector3 bestPoint = Vector3.zero;
        float bestScore = float.NegativeInfinity; // 필터를 통과한 후보의 도착점 maximin
        Vector3 fallbackPoint = Vector3.zero;
        float fallbackClearance = float.NegativeInfinity; // 전부 탈락했을 때를 위한 '그나마 나은' 후보
        bool hasBest = false;
        bool hasFallback = false;

        for (int i = 0; i < k_directionSampleCount; i++)
        {
            // 월드 축 기준 등간격 스윕 — away 벡터를 기준축으로 삼지 않으므로
            // 좌우 대칭 협공에서 방향이 0벡터로 상쇄되는 문제가 아예 생기지 않는다
            float angle = 360f / k_directionSampleCount * i;
            Vector3 direction = Quaternion.Euler(0f, angle, 0f) * Vector3.forward;
            Vector3 candidate = origin + direction * m_owner.FleeStepDistance;

            if (
                !NavMesh.SamplePosition(
                    candidate,
                    out NavMeshHit hit,
                    k_navSampleMaxDistance,
                    NavMesh.AllAreas
                )
            )
                continue; // 벽 너머 등 갈 수 없는 방향

            Vector3 point = hit.position;
            float pathClearanceSqr = float.MaxValue; // 경로가 플레이어를 스치는 최단거리
            float arrivalNearestSqr = float.MaxValue; // 도착점에서 가장 가까운 플레이어까지 거리

            foreach (Transform threat in s_threatBuffer)
            {
                Vector3 threatPos = threat.position;
                arrivalNearestSqr = Mathf.Min(arrivalNearestSqr, (threatPos - point).sqrMagnitude);

                // 위협이 경로의 시작점(origin) 쪽(t≈0)에 있으면 이 방향은 그 위협을 등지고 뛰는 방향이다.
                // origin 근처라는 이유만으로 모든 방향을 탈락시키면(위협이 4m 안으로 붙는 순간)
                // 앞이 뻥 뚫려 있어도 포위로 오판한다 — 앞(interior)에 있는 위협만 clearance 제약에 넣는다.
                float t;
                float segSqr = SqrDistanceToSegment(threatPos, origin, point, out t);
                if (t > k_pathClearanceMinT)
                    pathClearanceSqr = Mathf.Min(pathClearanceSqr, segSqr);
            }

            if (pathClearanceSqr < clearanceSqr)
            {
                // 탈락 — 이 방향으로 가면 도중에 잡힌다. 다만 전부 탈락할 경우를 대비해 최선을 기억해 둔다.
                if (pathClearanceSqr > fallbackClearance)
                {
                    fallbackClearance = pathClearanceSqr;
                    fallbackPoint = point;
                    hasFallback = true;
                }
                continue;
            }

            if (arrivalNearestSqr > bestScore)
            {
                bestScore = arrivalNearestSqr;
                bestPoint = point;
                hasBest = true;
            }
        }

        if (hasBest)
        {
            m_owner.Agent.SetDestination(bestPoint);
            return;
        }

        // 통과 후보 0개 = 포위 성립. 다만 저항에서 막 넘어온 직후라면(#205 저항 승리 → 도주 전환)
        // 즉시 되돌아가 프레임마다 왕복하므로, 쿨다운 동안은 그나마 나은 방향으로 뚫고 나가려 시도한다. (#213)
        if (Time.time - m_fleeStartTime < m_owner.FleeResistCooldown)
        {
            if (hasFallback)
                m_owner.Agent.SetDestination(fallbackPoint);
            return;
        }

        Debug.Log($"도주로 차단(포위) — 저항 전환: {m_owner.name}");
        m_transitioningToResist = true;
        m_owner.StartResist(m_owner.ThreatTarget);
    }

    /// <summary>반경 내 행동 가능한 플레이어를 공유 버퍼에 모은다.</summary>
    private void CollectThreats(float radius)
    {
        SuddenEventUtil.CollectFieldPlayers(m_owner.transform.position, radius, s_threatBuffer);
    }

    /// <summary>공유 버퍼에서 기준점에 가장 가까운 위협 — 비어 있으면 null.</summary>
    private static Transform NearestThreat(Vector3 origin)
    {
        Transform nearest = null;
        float nearestSqr = float.MaxValue;
        foreach (Transform threat in s_threatBuffer)
        {
            float sqr = (threat.position - origin).sqrMagnitude;
            if (sqr < nearestSqr)
            {
                nearestSqr = sqr;
                nearest = threat;
            }
        }
        return nearest;
    }

    /// <summary>
    /// 점과 선분(a→b) 사이 최단거리의 제곱 — 수평면(XZ) 기준. 도주 경로가 플레이어를 스치는지 판정한다.
    /// <paramref name="t"/>는 최근접점의 선분 위 정규화 위치([0,1]) — 0이면 시작점(a), 1이면 끝점(b)이다.
    /// </summary>
    private static float SqrDistanceToSegment(Vector3 point, Vector3 a, Vector3 b, out float t)
    {
        Vector2 p = new Vector2(point.x, point.z);
        Vector2 start = new Vector2(a.x, a.z);
        Vector2 end = new Vector2(b.x, b.z);

        Vector2 segment = end - start;
        float sqrLength = segment.sqrMagnitude;
        if (sqrLength < Mathf.Epsilon)
        {
            t = 0f;
            return (p - start).sqrMagnitude; // 선분이 점으로 뭉개진 경우
        }

        // 선분 위로의 정사영을 [0,1]로 잘라 최근접점을 구한다
        t = Mathf.Clamp01(Vector2.Dot(p - start, segment) / sqrLength);
        Vector2 closest = start + segment * t;
        return (p - closest).sqrMagnitude;
    }
}
