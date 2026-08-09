using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// 오검거 추격(Chasing) 상태 — 원한 구역에서 출동한 시민이 플레이어를 쫓는다. (#278)
/// 한 상태 안에서 네 페이즈를 다룬다:
///
/// - <b>추격</b>: 타겟을 향해 가속하며 쫓는다. 최고 속도는 플레이어 전력질주보다 낮아(ChaseMaxSpeed)
///   직선에서는 계속 달리면 벗어날 수 있다 — 대신 범위 이탈 시 아래 재타겟으로 페널티가 전가된다.
/// - <b>재타겟</b>: 타겟이 추격 범위(ChaseRange)를 벗어나거나 무력화되면, 범위 안의 플레이어 중
///   무작위 한 명으로 갈아탄다(잡히는 사람이 페널티 독박 — 부모 이슈 #276 확정 설계).
///   단 납치 임무(<see cref="NpcPenaltyAgent.IsAbductionDuty"/>)는 갈아타지 않는다 — 범위를 벗어나도 같은 표적을 계속 쫓는다 (#371).
/// - <b>사냥</b>: 범위 안에 아무도 없으면 배회하며 범위에 들어오는 플레이어를 기다린다.
/// - <b>격퇴/수렴</b>: 격퇴(ApplyChaseRepel, 호루라기 #250 예정)당하면 잠시 도주 후 사냥으로 복귀하고
///   그 플레이어에게 재추격 쿨다운을 건다. 누군가 포획되면(PenaltyConvergeTarget) 전원 그리로 모인다.
///
/// 포획은 <b>수평</b> 거리 판정 — WrongfulArrestPenalty가 OnPenaltyCaught를 구독해 수렴·호송(#279)을 지휘한다.
///
/// 추격 동안에는 조향(선회 속도·가속도·오토브레이킹)을 에이전트에 덮어쓰고 Exit에서 되돌린다 —
/// 시민 기본값으로는 선회 반경이 포획 거리보다 커서 표적 주위를 공전만 한다 (#568).
/// </summary>
public class NpcChaseState : NpcStateBase
{
    private const float k_repathInterval = 0.2f; // 경로 재계산 최소 간격(초) — NpcEscortedState와 동일
    private const float k_scanInterval = 0.5f; // 사냥 모드에서 범위 내 플레이어를 훑는 주기(초)
    private const float k_convergeStopDistance = 1.6f; // 수렴 시 포획된 플레이어 앞 정지 거리(m)
    private const float k_catchRetrySeconds = 3f; // 포획 통보 재시도 간격 — 매니저가 다른 호송 중이라 무시해도 스팸이 안 되게

    // 추격 중 경로 재계산 간격 — 붙었을 때만 촘촘히 본다. 총 계산 횟수는 고정 0.2초보다 오히려 줄면서
    // 접근전 정밀도만 오른다 (#568).
    private const float k_nearRepathInterval = 0.15f;
    private const float k_farRepathInterval = 0.4f;
    private const float k_nearRepathDistance = 8f; // 이 거리(m) 안쪽이면 촘촘한 쪽을 쓴다

    // 현재 타겟을 놓는 거리는 잡는 거리보다 넓다 (#568) — 경계에서 타겟이 깜빡이면 그때마다
    // 사냥(랜덤 배회)이 끼어들어 추격 도중 엉뚱한 방향으로 돈다.
    private const float k_releaseRangeMultiplier = 1.25f;

    // 부분 경로가 나왔을 때 목적지를 NavMesh 위로 끌어당기는 탐색 반경(m) — 표적이 연석·계단 모서리처럼
    // 카브가 안 된 곳에 서 있는 흔한 경우를 덮는다. 진짜 도달 불가(문 뒤·다른 층)는 이걸로도 안 붙는다.
    private const float k_destinationSnapRadius = 2f;

    // 스냅해도 부분 경로가 이만큼(초) 이어지면 도달 불가로 확정한다 (#568). 짧게 잡으면 모퉁이를 도는
    // 순간의 한두 프레임짜리 부분 경로에도 표적을 놓아 추격이 툭툭 끊긴다.
    private const float k_unreachableSeconds = 1.8f;

    private readonly List<Transform> m_candidateBuffer = new List<Transform>();

    // 격퇴당한 플레이어별 재추격 금지 종료 시각(Time.time). 상태 인스턴스는 NPC마다 1개라 NPC별 기록이 된다.
    private readonly Dictionary<Transform, float> m_targetCooldowns =
        new Dictionary<Transform, float>();

    private float m_baseSpeed; // 진입 전 원래 속도 — 사냥 모드 속도이자 Exit 복원값
    private float m_targetAcquiredTime; // 현재 타겟 확보 시각 — 가속 기준점 (타겟이 바뀌면 리셋)
    private float m_repathTimer;
    private float m_scanTimer;
    private float m_nextCatchNotifyTime;
    private float m_handledRepelUntil; // 이미 쿨다운을 등록한 격퇴인지 — 같은 격퇴에 중복 등록 방지
    private bool m_hunting; // 사냥(배회) 모드 중인지 — 추격/사냥 간 속도·목적지 전환용

    // 진입 전 조향 값 — Exit에서 그대로 되돌린다. 시민의 개체차(속도 랜덤 등)를 덮어쓰지 않기 위해
    // 상수가 아니라 진입 시점의 실제 값을 기억한다.
    private float m_baseTurnSpeed;
    private float m_baseAcceleration;
    private bool m_baseAutoBraking;

    // 리드 조준용 — 직전 repath 때의 표적 위치와 그 시각. 표적이 바뀌면 리셋한다(엉뚱한 속도가 나온다).
    private Transform m_leadTarget;
    private Vector3 m_lastTargetPosition;
    private float m_lastTargetSampleTime;

    // 부분 경로가 시작된 시각 — 0이면 정상 경로. 이 상태가 k_unreachableSeconds 이어지면 도달 불가로 본다.
    private float m_partialSince;

    private readonly NpcChaseConfig m_config;
    private readonly NpcWalkConfig m_walkConfig;
    private readonly NpcFleeConfig m_fleeConfig;

    public NpcChaseState(
        NpcController owner,
        NpcChaseConfig config,
        NpcWalkConfig walkConfig,
        NpcFleeConfig fleeConfig
    )
        : base(owner)
    {
        m_config = config;
        m_walkConfig = walkConfig;
        m_fleeConfig = fleeConfig;
    }

    public override void Enter()
    {
        m_baseSpeed = m_owner.Agent.speed;
        m_targetAcquiredTime = Time.time;
        m_repathTimer = 0f;
        m_scanTimer = 0f;
        m_nextCatchNotifyTime = 0f;
        m_hunting = false;
        ClearLeadSample();
        ClearReachability();

        // 이전 임무의 격퇴 이력을 다음 추격까지 끌고 가지 않는다 (#568) — 남겨 두면 새 추격에서
        // 이유 없이 특정 플레이어가 후보에서 빠지고, 접속을 끊은 플레이어의 키도 계속 쌓인다.
        m_targetCooldowns.Clear();

        // 조향을 추격용으로 올린다 (#568). 시민 기본값(선회 240도/초)으로는 선회 반경이 포획 거리보다
        // 커서 플레이어가 옆으로 스텝만 밟아도 안쪽으로 못 꺾고 궤도를 돈다.
        m_baseTurnSpeed = m_owner.Agent.angularSpeed;
        m_baseAcceleration = m_owner.Agent.acceleration;
        m_baseAutoBraking = m_owner.Agent.autoBraking;

        ApplyChaseSteering(true);

        m_owner.Agent.isStopped = false;
        m_owner.Agent.stoppingDistance = 0f;
    }

    public override void Exit()
    {
        m_owner.Agent.speed = m_baseSpeed;
        m_owner.Agent.stoppingDistance = 0f; // 수렴 페이즈가 올린 정지 거리 원복 — 배회 복귀 시 목적지 앞 멈춤 방지

        ApplyChaseSteering(false); // 조향 원복 — 추격을 벗어난 시민이 팽이처럼 도는 것을 막는다

        if (m_owner.Agent.isOnNavMesh)
        {
            m_owner.Agent.isStopped = false;
            m_owner.Agent.ResetPath();
        }
    }

    public override void Tick()
    {
        m_repathTimer -= Time.deltaTime;

        // ---- 수렴: 포획 확정 — 전원 포획된 플레이어에게 모인다. 추격·격퇴보다 우선한다 (#279)
        Transform converge = m_owner.Penalty.PenaltyConvergeTarget;
        if (converge != null)
        {
            TickConverge(converge);
            return;
        }

        // ---- 격퇴: 호루라기(#250 예정)에 쫓겨나 잠시 도주 — 유예 창. 끝나면 사냥/재타겟으로 이어진다
        if (Time.time < m_owner.Penalty.ChaseRepelUntil)
        {
            TickRepelled();
            return;
        }

        // ---- 타겟 유효성: 사라짐·무력화·범위 이탈·쿨다운이면 범위 안 무작위 플레이어로 갈아탄다
        // 납치(#371)는 갈아타지도, 놓지도 않는다 — 표적이 범위를 벗어나거나 무력화돼도 같은 사람을 계속 쫓는다.
        // 갈아타면 "혼자 있는 사람을 노린다"는 그 이벤트의 유일한 규칙이 깨지고(동료 옆의 사람을 잡는다),
        // 반대로 놓아 버리면 뒤처진 납치범만 빠져나가 2인 호송이 1인으로 무너진다. 실패는 이벤트가
        // 자기 추격 상한(AbductionEvent.m_maxChaseSeconds)으로 끊는다 — 상태가 판단할 일이 아니다.
        Transform target = m_owner.Penalty.ChaseTarget;

        // 표적을 갈아타지 않는 임무인가 — 아래 도달 불가 처리도 이 값을 그대로 본다.
        // 판단을 한 곳에만 두는 이유: 갈아타지 않는 임무가 늘면(소매치기 #303) 이 줄만 고치면 된다.
        bool keepsTarget = m_owner.Penalty.IsAbductionDuty;

        if (keepsTarget)
        {
            // 표적이 사라졌다(접속 종료·파괴) — 여기서 재타겟으로 흘리면 아래 갈아타기 금지가 무의미해진다.
            // 배회로 두고 이벤트의 추격 상한이 끊게 한다.
            if (target == null)
            {
                TickHunt();
                return;
            }
        }
        else if (!IsChaseable(target))
        {
            target = PickRandomTargetInRange();
            m_owner.Penalty.SetChaseTarget(target);
            if (target != null)
            {
                m_targetAcquiredTime = Time.time; // 새 타겟 — 가속을 처음부터 다시 밟는다
                ClearReachability(); // 앞 표적의 도달 불가 누적을 물려받지 않는다
            }
        }

        // ---- 사냥: 범위 안에 아무도 없다 — 배회하며 기다린다 (걷는 속도)
        if (target == null)
        {
            TickHunt();
            return;
        }

        // ---- 추격: 가속하며 쫓고, 붙으면 포획을 통보한다
        m_hunting = false;
        ApplyChaseSteering(true); // 사냥에서 막 돌아왔을 수 있다
        float elapsed = Time.time - m_targetAcquiredTime;
        float accel = Mathf.Clamp01(elapsed / Mathf.Max(m_config.AccelSeconds, 0.01f));
        m_owner.Agent.speed = Mathf.Lerp(m_baseSpeed, m_config.MaxSpeed, accel);
        m_owner.Agent.stoppingDistance = 0f;

        float distance = FlatDistance(m_owner.transform.position, target.position);

        if (m_repathTimer <= 0f)
        {
            // 붙었을 때만 촘촘히 — 멀리서 0.2초마다 다시 재는 것은 낭비다
            m_repathTimer = distance <= k_nearRepathDistance
                ? k_nearRepathInterval
                : k_farRepathInterval;
            SetChaseDestination(target, distance);
        }

        // ---- 도달 불가: 문 뒤·다른 층이다 (#568). 스냅으로도 안 붙은 채 시간이 흘렀다.
        // 오검거는 표적을 놓고 다른 사람을 찾는다 — 벽면을 따라 좌우로 미끄러지며 비비지 않는다.
        // 납치(#371)는 갈아타지 않는 것이 이벤트의 유일한 규칙이라 계속 다가간 채로 둔다.
        // 실패는 이벤트가 자기 추격 상한(AbductionEvent.m_maxChaseSeconds)으로 끊는다.
        if (IsTargetUnreachable() && !keepsTarget)
        {
            // 곧바로 같은 사람을 다시 고르면 붙었다 놓기를 반복한다 — 잠시 후보에서 뺀다.
            // 격퇴 쿨다운과 같은 장부를 쓴다: "이 사람은 당분간 노리지 않는다"로 뜻이 같다.
            m_targetCooldowns[target] = Time.time + m_config.RetargetCooldown;
            m_owner.Penalty.SetChaseTarget(null);
            ClearReachability();
            ClearLeadSample();
            TickHunt();
            return;
        }

        if (distance <= m_config.CatchDistance && Time.time >= m_nextCatchNotifyTime)
        {
            // 매니저가 이미 다른 호송을 처리 중이면 통보가 무시된다 — 재시도 간격을 두고 계속 붙어 다닌다
            m_nextCatchNotifyTime = Time.time + k_catchRetrySeconds;
            m_owner.Penalty.NotifyPenaltyCaught(target);
        }
    }

    // 조향을 추격용/평상시로 오간다 (#568). 진입 전 값을 기억해 두고 되돌리므로 개체차(시민마다 다른
    // 속도·회피 우선순위)를 덮어쓰지 않는다. 오토브레이킹은 추격 중에만 끈다 — 목적지가 표적 발밑이라
    // 켜져 있으면 제동거리(≈3m)부터 감속하다 목적지를 지나쳐 돌아 나온다.
    private void ApplyChaseSteering(bool chasing)
    {
        m_owner.Agent.angularSpeed = chasing ? m_config.TurnSpeed : m_baseTurnSpeed;
        m_owner.Agent.acceleration = chasing ? m_config.Acceleration : m_baseAcceleration;
        m_owner.Agent.autoBraking = !chasing && m_baseAutoBraking;
    }

    // 표적이 조금 뒤에 있을 자리를 조준한다 (#568). 도달 불가로 부분 경로가 돌아왔으면 목적지를
    // NavMesh 위로 끌어당겨 본다 — 표적 좌표만 살짝 벗어난 흔한 경우가 여기서 걷힌다.
    private void SetChaseDestination(Transform target, float distance)
    {
        Vector3 aim = PredictAimPoint(target, distance);

        // pathPending 중에는 pathStatus가 직전 경로의 낡은 값이라 함께 가드한다.
        bool partial =
            !m_owner.Agent.pathPending
            && m_owner.Agent.pathStatus != NavMeshPathStatus.PathComplete;

        // 부분 경로일 때만 샘플링하므로 정상 경로에서는 프로퍼티 읽기 두 번이 전부다.
        if (
            partial
            && NavMesh.SamplePosition(
                aim,
                out NavMeshHit hit,
                k_destinationSnapRadius,
                m_owner.Agent.areaMask
            )
        )
            aim = hit.position;

        m_owner.Agent.SetDestination(aim);

        // 스냅이 먹었으면 다음 주기에 정상 경로로 돌아와 누적이 풀린다
        if (partial)
        {
            if (m_partialSince <= 0f)
                m_partialSince = Time.time;
        }
        else
        {
            m_partialSince = 0f;
        }
    }

    // 스냅해도 부분 경로가 계속된다 — 표적이 있는 곳으로 갈 길 자체가 없다.
    private bool IsTargetUnreachable() =>
        m_partialSince > 0f && Time.time - m_partialSince >= k_unreachableSeconds;

    private void ClearReachability() => m_partialSince = 0f;

    // 직전 표본과의 차이로 표적 속도를 재고, 도달에 걸릴 시간만큼 앞을 조준한다.
    // 현재 위치를 그대로 조준하면 최대 한 주기(0.15~0.4초) 뒤처진 지점을 쫓아 꼬리만 물게 된다.
    private Vector3 PredictAimPoint(Transform target, float distance)
    {
        Vector3 current = target.position;
        float span = Time.time - m_lastTargetSampleTime;

        // 표적이 바뀌었거나 첫 표본 — 속도를 알 수 없다
        if (m_leadTarget != target || span <= 0f)
        {
            RememberLeadSample(target, current);
            return current;
        }

        Vector3 velocity = (current - m_lastTargetPosition) / span;
        velocity.y = 0f; // 계단·경사에서 위를 조준하지 않게
        RememberLeadSample(target, current);

        // 상한이 없으면 급반전할 때 지나간 방향으로 크게 헛돈다
        float speed = Mathf.Max(m_owner.Agent.speed, 0.1f);
        float lead = Mathf.Min(distance / speed, m_config.MaxLeadSeconds);
        return current + velocity * lead;
    }

    private void RememberLeadSample(Transform target, Vector3 position)
    {
        m_leadTarget = target;
        m_lastTargetPosition = position;
        m_lastTargetSampleTime = Time.time;
    }

    private void ClearLeadSample()
    {
        m_leadTarget = null;
        m_lastTargetSampleTime = 0f;
    }

    // 포획·사거리는 수평 거리로 잰다 (#568) — Y를 포함하면 계단·경사면이나 피벗 높이차만으로도
    // 수평으로 밀착한 상태가 포획 거리(1.3m)를 넘겨, 다 따라잡고도 판정이 안 붙는다.
    private static float FlatDistance(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    // 포획된 플레이어에게 모여 선다 — 도착 판정·호송 개시는 매니저(WrongfulArrestPenalty)가 거리로 지휘한다.
    private void TickConverge(Transform converge)
    {
        m_owner.Agent.speed = m_config.MaxSpeed; // 수렴은 전속 — 연출 대기를 줄인다
        m_owner.Agent.stoppingDistance = k_convergeStopDistance;
        // 수렴은 정해진 자리에 서는 이동이라 감속이 있어야 한다 — 추격용 조향(오토브레이킹 off)을 쓰면
        // 멈출 자리를 지나쳐 되돌아온다. 기존 동작 그대로 둔다.
        ApplyChaseSteering(false);

        if (m_repathTimer <= 0f)
        {
            m_repathTimer = k_repathInterval;
            m_owner.Agent.SetDestination(converge.position);
        }
    }

    // 격퇴 도주 — 격퇴한 플레이어 반대 방향으로 달아난다. 같은 격퇴당 한 번만 재추격 쿨다운을 등록한다.
    private void TickRepelled()
    {
        if (m_handledRepelUntil != m_owner.Penalty.ChaseRepelUntil)
        {
            m_handledRepelUntil = m_owner.Penalty.ChaseRepelUntil;
            if (m_owner.Penalty.ChaseRepelBy != null)
                m_targetCooldowns[m_owner.Penalty.ChaseRepelBy] = Time.time + m_config.RetargetCooldown;
            m_owner.Penalty.SetChaseTarget(null); // 도주가 끝나면 재타겟부터 다시 — 쿨다운 대상은 후보에서 빠진다
        }

        m_owner.Agent.speed = m_config.MaxSpeed;
        m_owner.Agent.stoppingDistance = 0f;
        ApplyChaseSteering(false); // 격퇴 도주는 이 이슈 범위 밖 — 기존 조향 그대로 둔다

        if (m_repathTimer > 0f || m_owner.Penalty.ChaseRepelBy == null)
            return;

        m_repathTimer = k_repathInterval;
        Vector3 away = (m_owner.transform.position - m_owner.Penalty.ChaseRepelBy.position).normalized;
        if (away.sqrMagnitude < 0.01f)
            away = m_owner.transform.forward;

        Vector3 candidate = m_owner.transform.position + away * m_fleeConfig.StepDistance;
        if (
            NavMesh.SamplePosition(
                candidate,
                out NavMeshHit hit,
                m_fleeConfig.StepDistance,
                m_owner.Agent.areaMask
            )
        )
            m_owner.Agent.SetDestination(hit.position);
    }

    // 사냥 모드 — 걷는 속도로 배회하며 범위에 들어오는 플레이어를 기다린다 (재타겟은 Tick 상단이 주기 스캔으로 처리).
    private void TickHunt()
    {
        m_owner.Agent.speed = m_baseSpeed;
        m_owner.Agent.stoppingDistance = 0f;
        ApplyChaseSteering(false); // 배회는 시민처럼 걷는 구간 — 급선회가 어울리지 않는다

        if (!m_hunting)
        {
            m_hunting = true;
            PickWanderPoint();
            return;
        }

        // 배회 지점 도착 — 다음 지점을 뽑는다
        if (!m_owner.Agent.pathPending && m_owner.Agent.remainingDistance < 0.6f)
            PickWanderPoint();
    }

    private void PickWanderPoint()
    {
        Vector2 dir = Random.insideUnitCircle.normalized;
        float dist = Random.Range(m_walkConfig.MinWanderDistance, m_walkConfig.WanderRadius);
        Vector3 candidate = m_owner.transform.position + new Vector3(dir.x, 0f, dir.y) * dist;
        if (
            NavMesh.SamplePosition(
                candidate,
                out NavMeshHit hit,
                m_walkConfig.WanderRadius,
                m_owner.Agent.areaMask
            )
        )
            m_owner.Agent.SetDestination(hit.position);
    }

    // 현재 타겟을 계속 쫓아도 되는가 — 존재·행동 가능·추격 범위 안·재추격 쿨다운 아님.
    // 놓는 거리는 잡는 거리(Range)보다 넓다 — 경계에서 깜빡이면 사냥(랜덤 배회)이 끼어든다 (#568).
    private bool IsChaseable(Transform target)
    {
        if (target == null)
            return false;
        if (IsOnCooldown(target))
            return false;

        PlayerHealth health = target.GetComponent<PlayerHealth>();
        if (health == null || !health.IsTargetable)
            return false;

        return FlatDistance(m_owner.transform.position, target.position)
            <= m_config.Range * k_releaseRangeMultiplier;
    }

    // 추격 범위 안의 행동 가능한 플레이어 중 무작위 — 쿨다운 대상 제외. 없으면 null(사냥 모드).
    // 주기 스캔(k_scanInterval)으로 스로틀한다 — 사냥 중 매 프레임 전 플레이어 순회 방지.
    private Transform PickRandomTargetInRange()
    {
        if (m_scanTimer > Time.time)
            return null;
        m_scanTimer = Time.time + k_scanInterval;

        SuddenEventUtil.CollectFieldPlayers(
            m_owner.transform.position,
            m_config.Range,
            m_candidateBuffer
        );
        for (int i = m_candidateBuffer.Count - 1; i >= 0; i--)
        {
            if (IsOnCooldown(m_candidateBuffer[i]))
                m_candidateBuffer.RemoveAt(i);
        }

        if (m_candidateBuffer.Count == 0)
            return null;

        return m_candidateBuffer[Random.Range(0, m_candidateBuffer.Count)];
    }

    private bool IsOnCooldown(Transform target) =>
        m_targetCooldowns.TryGetValue(target, out float until) && Time.time < until;
}
