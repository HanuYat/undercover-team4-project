using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// 오검거 추격(Chasing) 상태 — 원한 구역에서 출동한 시민이 플레이어를 쫓는다. (#278)
/// 한 상태 안에서 네 페이즈를 다룬다:
///
/// - <b>추격</b>: 타겟을 향해 가속하며 쫓는다. 최고 속도는 플레이어 전력질주보다 낮아(ChaseMaxSpeed)
///   직선에서는 계속 달리면 벗어날 수 있다 — 대신 범위 이탈 시 아래 재타겟으로 페널티가 전가된다.
/// - <b>재타겟</b>: 표적이 포기 거리를 넘거나 무력화되면 범위 안의 <b>가장 가까운</b> 사람으로 갈아탄다
///   (잡히는 사람이 페널티 독박 — 부모 이슈 #276 확정 설계). 달리는 중이라면 눈에 띄게 더 가까운
///   후보가 나타났을 때도 바꾼다 (#568).
///   단 시민인 척하는 임무(<see cref="NpcDutyAgent.IsUndercoverDuty"/> — 납치 #371·소매치기 #303)는
///   갈아타지 않는다 — 범위를 벗어나도 같은 표적을 계속 쫓는다.
/// - <b>사냥</b>: 범위 안에 아무도 없으면 배회하며 범위에 들어오는 플레이어를 기다린다.
/// - <b>격퇴/수렴</b>: 격퇴(ApplyChaseRepel, 호루라기 #250 예정)당하면 잠시 도주 후 사냥으로 복귀하고
///   그 플레이어에게 재추격 쿨다운을 건다. 누군가 포획되면(PenaltyConvergeTarget) 전원 그리로 모인다.
///
/// 포획은 <b>수평</b> 거리 판정 — WrongfulArrestPenalty가 OnPenaltyCaught를 구독해 수렴·호송(#279)을 지휘한다.
///
/// <b>이 클래스는 국면 진행만 맡는다</b> (#568 후속). 세 가지는 부품이 가져갔다:
/// 조향·리드 조준은 <see cref="ChaseSteering"/>, 도달 가능성 누적은 <see cref="ChaseReachability"/>,
/// 표적 선정·쿨다운은 <see cref="ChaseTargeting"/>. 셋 다 FSM을 모르므로 따로 검증할 수 있다.
/// </summary>
public class NpcChaseState : NpcStateBase
{
    private const float k_convergeStopDistance = 1.6f; // 수렴 시 포획된 플레이어 앞 정지 거리(m)
    private const float k_catchRetrySeconds = 3f; // 포획 통보 재시도 간격 — 매니저가 다른 호송 중이라 무시해도 스팸이 안 되게

    // 추격 중 경로 재계산 간격 — 붙었을 때만 촘촘히 본다. 총 계산 횟수는 고정 0.2초보다 오히려 줄면서
    // 접근전 정밀도만 오른다 (#568).

    // 부분 경로가 나왔을 때 목적지를 NavMesh 위로 끌어당기는 탐색 반경(m) — 표적이 연석·계단 모서리처럼
    // 카브가 안 된 곳에 서 있는 흔한 경우를 덮는다. 진짜 도달 불가(문 뒤·다른 층)는 이걸로도 안 붙는다.
    private const float k_destinationSnapRadius = 2f;

    private readonly ChaseSteering m_steering;
    private readonly ChaseReachability m_reachability = new ChaseReachability();
    private readonly ChaseTargeting m_targeting;

    private float m_baseSpeed; // 진입 전 원래 속도 — 사냥 모드 속도이자 Exit 복원값
    private float m_targetAcquiredTime; // 현재 타겟 확보 시각 — 가속 기준점
    private float m_nextCatchNotifyTime;
    private float m_handledRepelUntil; // 이미 쿨다운을 등록한 격퇴인지 — 같은 격퇴에 중복 등록 방지
    private bool m_hunting; // 사냥(배회) 모드 중인지 — 추격/사냥 간 속도·목적지 전환용

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

        m_steering = new ChaseSteering(config);
        m_targeting = new ChaseTargeting(config, owner.Repath);
    }

    public override void Enter()
    {
        m_baseSpeed = m_owner.Agent.speed;
        m_targetAcquiredTime = Time.time;
        m_owner.Repath.ForceDue(NpcRepathChannel.Repath); // 진입 직후 1회는 바로 잡는다
        m_nextCatchNotifyTime = 0f;
        m_hunting = false;

        m_steering.ClearLeadSample();
        m_reachability.Clear();
        m_targeting.Reset(); // 이전 임무의 격퇴 이력을 다음 추격까지 끌고 가지 않는다 (#568)

        m_steering.CaptureBaseline(m_owner.Agent);
        m_steering.Apply(m_owner.Agent, true);

        m_owner.Agent.isStopped = false;
        m_owner.Agent.stoppingDistance = 0f;
    }

    public override void Exit()
    {
        // 소매치기는 추격을 벗어나면 평범한 도주형이 된다 (#303) — 표식을 들고 나가면 연행·수감 뒤에도
        // 때리거나 다시 묶을 수 있는 예외(NpcStateRules)가 열린 채로 남는다.
        m_owner.Penalty.ClearDuty(NpcDutyKind.Pickpocket);

        m_owner.Agent.speed = m_baseSpeed;
        m_owner.Agent.stoppingDistance = 0f; // 수렴 페이즈가 올린 정지 거리 원복 — 배회 복귀 시 목적지 앞 멈춤 방지

        m_steering.Apply(m_owner.Agent, false); // 조향 원복 — 추격을 벗어난 시민이 팽이처럼 도는 것을 막는다

        if (m_owner.Agent.isOnNavMesh)
        {
            m_owner.Agent.isStopped = false;
            m_owner.Agent.ResetPath();
        }
    }

    public override void Tick()
    {
        float now = Time.time;

        // ---- 수렴: 포획 확정 — 전원 포획된 플레이어에게 모인다. 추격·격퇴보다 우선한다 (#279)
        Transform converge = m_owner.Penalty.PenaltyConvergeTarget;
        if (converge != null)
        {
            TickConverge(converge);
            return;
        }

        // ---- 격퇴: 호루라기(#250 예정)에 쫓겨나 잠시 도주 — 유예 창. 끝나면 사냥/재타겟으로 이어진다
        if (now < m_owner.Penalty.ChaseRepelUntil)
        {
            TickRepelled(now);
            return;
        }

        // ---- 타겟 유효성: 사라짐·무력화·포기 거리·쿨다운이면 범위 안 가장 가까운 사람으로 갈아탄다
        // 납치(#371)는 갈아타지도, 놓지도 않는다 — 표적이 범위를 벗어나거나 무력화돼도 같은 사람을 계속 쫓는다.
        // 갈아타면 "혼자 있는 사람을 노린다"는 그 이벤트의 유일한 규칙이 깨지고(동료 옆의 사람을 잡는다),
        // 반대로 놓아 버리면 뒤처진 납치범만 빠져나가 2인 호송이 1인으로 무너진다. 실패는 이벤트가
        // 자기 추격 상한(AbductionEvent.m_maxChaseSeconds)으로 끊는다 — 상태가 판단할 일이 아니다.
        Transform target = m_owner.Penalty.ChaseTarget;

        // 표적을 갈아타지 않는 임무인가 — 아래 도달 불가 처리·근접 갈아타기 판정도 이 값을 그대로 본다.
        // 소매치기(#303)도 표적을 갈아타지 않는다 — 노리던 사람의 물건을 채는 것이 이벤트의 전부다.
        bool keepsTarget = m_owner.Penalty.IsUndercoverDuty;

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
        else if (!m_targeting.IsChaseable(m_owner.transform.position, target, now))
        {
            target = m_targeting.PickNearest(m_owner.transform.position, now);
            m_owner.Penalty.SetChaseTarget(target);
            if (target != null)
                AcquireTarget(now, restartAccel: m_hunting);
        }

        // ---- 사냥: 범위 안에 아무도 없다 — 배회하며 기다린다 (걷는 속도)
        if (target == null)
        {
            TickHunt();
            return;
        }

        // ---- 추격: 가속하며 쫓고, 붙으면 포획을 통보한다
        m_hunting = false;
        m_steering.Apply(m_owner.Agent, true); // 사냥에서 막 돌아왔을 수 있다

        // 소매치기는 가속하지 않는다 (#303) — 시민 걸음으로 다가가야 알아채지 못한다.
        // 달려들면 등 뒤에서 채는 그림이 아니라 그냥 추격이 되고, 정체가 걸음걸이로 새어 나간다.
        if (m_owner.Penalty.IsPickpocketDuty)
        {
            m_owner.Agent.speed = m_baseSpeed;
        }
        else
        {
            float elapsed = now - m_targetAcquiredTime;
            float accel = Mathf.Clamp01(elapsed / Mathf.Max(m_config.AccelSeconds, 0.01f));
            m_owner.Agent.speed = Mathf.Lerp(m_baseSpeed, m_config.MaxSpeed, accel);
        }

        m_owner.Agent.stoppingDistance = 0f;

        float distance = ChaseMath.FlatDistance(m_owner.transform.position, target.position);

        // ---- 갈아타기: 눈에 띄게 더 가까운 사람이 나타났다 (#568 후속)
        // 상대 비교라 왕복이 없다 — 바꾼 직후에는 새 표적이 더 가까워 되돌아갈 조건이 성립하지 않는다.
        // 가속 램프는 건드리지 않는다: 달리던 중의 표적 교체는 "다시 출발"이 아니다.
        if (!keepsTarget)
        {
            Transform closer = m_targeting.FindCloser(
                m_owner.transform.position, target, distance, now);
            if (closer != null)
            {
                target = closer;
                m_owner.Penalty.SetChaseTarget(target);
                AcquireTarget(now, restartAccel: false);
                distance = ChaseMath.FlatDistance(m_owner.transform.position, target.position);
            }
        }

        // 거리 티어는 스케줄러가 잡는다 (#573) — 붙었을 때만 촘촘히 도는 성질은 그대로다
        if (m_owner.Repath.Due(NpcRepathChannel.Repath))
            SetChaseDestination(target, distance, now);

        // ---- 도달 불가: 문 뒤·다른 층이다 (#568). 스냅으로도 안 붙은 채 시간이 흘렀다.
        // 오검거는 표적을 놓고 다른 사람을 찾는다 — 벽면을 따라 좌우로 미끄러지며 비비지 않는다.
        // 납치(#371)는 갈아타지 않는 것이 이벤트의 유일한 규칙이라 계속 다가간 채로 둔다.
        if (m_reachability.IsUnreachable(now) && !keepsTarget)
        {
            // 곧바로 같은 사람을 다시 고르면 붙었다 놓기를 반복한다 — 잠시 후보에서 뺀다.
            // 격퇴 쿨다운과 같은 장부를 쓴다: "이 사람은 당분간 노리지 않는다"로 뜻이 같다.
            m_targeting.PutOnCooldown(target, now);
            m_owner.Penalty.SetChaseTarget(null);
            m_reachability.Clear();
            m_steering.ClearLeadSample();
            TickHunt();
            return;
        }

        if (distance <= m_config.CatchDistance && now >= m_nextCatchNotifyTime)
        {
            // 매니저가 이미 다른 호송을 처리 중이면 통보가 무시된다 — 재시도 간격을 두고 계속 붙어 다닌다
            m_nextCatchNotifyTime = now + k_catchRetrySeconds;
            m_owner.Penalty.NotifyPenaltyCaught(target);
        }
    }

    /// <summary>새 표적을 문 직후의 정리 — 앞 표적의 도달 불가 누적을 물려받지 않는다.
    /// <paramref name="restartAccel"/>은 <b>사냥에서 돌아올 때만</b> 참이다: 달리던 중의 교체까지
    /// 리셋하면 걷는 속도로 떨어졌다 8초에 걸쳐 회복하기를 반복해 갈아탈수록 추격이 느려진다. (#568)</summary>
    private void AcquireTarget(float now, bool restartAccel)
    {
        if (restartAccel)
            m_targetAcquiredTime = now;

        m_reachability.Clear();
    }

    /// <summary>
    /// 목적지를 잡는다 — 리드 조준한 지점으로, 부분 경로면 표적 실제 위치로 물러난다. (#568)
    ///
    /// <b>예측점부터 버리는 이유</b>: 부분 경로의 흔한 원인이 리드 조준 자신이다. 표적이 대각선으로
    /// 움직이면 몇 걸음 앞을 조준한 지점이 연석·벽 너머로 넘어가기 쉽고, 그러면 우리가 만든 목적지
    /// 때문에 도달 불가로 오판한다. 실제 위치로 되돌린 뒤 그래도 안 되면 NavMesh 위로 스냅한다.
    /// </summary>
    private void SetChaseDestination(Transform target, float distance, float now)
    {
        Vector3 aim = m_steering.PredictAimPoint(target, distance, m_owner.Agent.speed, now);

        // pathPending 중에는 pathStatus가 직전 경로의 낡은 값이라 함께 가드한다.
        bool partial =
            !m_owner.Agent.pathPending
            && m_owner.Agent.pathStatus != NavMeshPathStatus.PathComplete;

        // 부분 경로일 때만 샘플링하므로 정상 경로에서는 프로퍼티 읽기 두 번이 전부다.
        if (partial)
        {
            aim = target.position;

            if (
                NavMesh.SamplePosition(
                    aim,
                    out NavMeshHit hit,
                    k_destinationSnapRadius,
                    m_owner.Agent.areaMask
                )
            )
                aim = hit.position;
        }

        m_owner.Agent.SetDestination(aim);
        m_reachability.Report(partial, now); // 스냅이 먹었으면 다음 주기에 정상 경로로 돌아와 누적이 풀린다
    }

    // 포획된 플레이어에게 모여 선다 — 도착 판정·호송 개시는 매니저(WrongfulArrestPenalty)가 거리로 지휘한다.
    private void TickConverge(Transform converge)
    {
        m_owner.Agent.speed = m_config.MaxSpeed; // 수렴은 전속 — 연출 대기를 줄인다
        m_owner.Agent.stoppingDistance = k_convergeStopDistance;
        // 수렴은 정해진 자리에 서는 이동이라 감속이 있어야 한다 — 추격용 조향(오토브레이킹 off)을 쓰면
        // 멈출 자리를 지나쳐 되돌아온다. 기존 동작 그대로 둔다.
        m_steering.Apply(m_owner.Agent, false);

        if (m_owner.Repath.Due(NpcRepathChannel.Repath))
            m_owner.Agent.SetDestination(converge.position);
    }

    // 격퇴 도주 — 격퇴한 플레이어 반대 방향으로 달아난다. 같은 격퇴당 한 번만 재추격 쿨다운을 등록한다.
    private void TickRepelled(float now)
    {
        if (m_handledRepelUntil != m_owner.Penalty.ChaseRepelUntil)
        {
            m_handledRepelUntil = m_owner.Penalty.ChaseRepelUntil;
            m_targeting.PutOnCooldown(m_owner.Penalty.ChaseRepelBy, now);
            m_owner.Penalty.SetChaseTarget(null); // 도주가 끝나면 재타겟부터 다시 — 쿨다운 대상은 후보에서 빠진다
        }

        m_owner.Agent.speed = m_config.MaxSpeed;
        m_owner.Agent.stoppingDistance = 0f;
        m_steering.Apply(m_owner.Agent, false); // 격퇴 도주는 이 이슈 범위 밖 — 기존 조향 그대로 둔다

        // 격퇴 대상을 먼저 본다 — 없으면 게이트를 소모하지 않는다
        if (m_owner.Penalty.ChaseRepelBy == null || !m_owner.Repath.Due(NpcRepathChannel.Repath))
            return;

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
        m_steering.Apply(m_owner.Agent, false); // 배회는 시민처럼 걷는 구간 — 급선회가 어울리지 않는다

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

        // 도로는 목적지에서 뺀다 — 배회이므로 <see cref="NpcWalkState"/>와 같은 규칙이다 (#634 후속).
        //
        // 여기가 특히 문제가 되는 자리다: 표적이 <b>다른 이유로</b> 죽으면(차 사고 등) 납치는 표적을
        // 갈아타지도 놓지도 않으므로 상태가 Chasing에 머물고, 이 사냥 배회가 최대 60초
        // (AbductionEvent.m_maxChaseSeconds) 계속 돈다. 그동안 통행 마스크는 전체라
        // NpcController의 도로 이탈이 걸리지 않는데(추격은 도로를 써도 되는 상태다),
        // 목적지까지 도로를 허용하면 <b>차도 한복판을 배회 지점으로 잡고 그 자리에 멈춰 선다.</b>
        //
        // 마스크 자체는 건드리지 않는다 — 순찰 중 도로를 건너는 것은 그대로다. 서지만 않게 한다.
        if (
            NavMesh.SamplePosition(
                candidate,
                out NavMeshHit hit,
                m_walkConfig.WanderRadius,
                NpcNavAreas.ExcludeRoad(m_owner.Agent.areaMask)
            )
        )
            m_owner.Agent.SetDestination(hit.position);
    }
}
