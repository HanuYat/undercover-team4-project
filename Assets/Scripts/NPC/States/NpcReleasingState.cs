using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// 반출 보행(Releasing) 상태 — 감옥에서 꺼내진 대상이 인도 지점까지 <b>스스로</b> 달려간다. (#548)
/// 목적지는 비밀 청탁(<see cref="SecretFavorBroker"/>)이 정하고 이 상태는 "어디로"만 받는다.
/// 구조는 침입(<see cref="NpcIntrudeState"/>)과 같다.
///
/// <b>완수 판정은 하지 않는다</b> — 목적지 앞에 서는 것까지만 맡고, 완수(대상 소멸 + 보수)는 브로커가
/// 인도 범위(박스)로 판정한다. 멈추는 기준(<see cref="k_arriveDistance"/>)과는 다른 자다.
///
/// <b>무산시키는 것은 밧줄뿐이다</b> (2026-08-12 확정). 맞으면 배정된 유형대로 도주·저항으로 돌아서지만
/// (<see cref="NpcStateRules.CanReactToDamage"/>) 목적지는 남아, 쓰러뜨려 재우면 깨어나 이 상태로
/// <b>돌아와 질주로</b> 간다(<see cref="NpcStun.ExitStun"/> · <see cref="NpcStunnedState"/>).
/// 타격은 묶을 창을 여는 수단이고, 목적지를 지우는 것은 밧줄·재수감·사망·의뢰 취소뿐이다
/// (<see cref="NpcController"/>의 상태 훅).
///
/// <b>길이 막혀도 포기하지 않는다</b> — 경로가 없거나 끊기면 서지 않고 계속 다시 건다. 도주
/// (<see cref="NpcFleeState"/>)가 길막에서 저항으로 돌아서는 것과 반대 규칙인데, 쫓기는 몸이 아니라
/// 갈 데가 있는 몸이라서다. 탈옥 방출은 목적지가 없어 Run으로 나가므로 이 예외를 받지 않는다.
/// </summary>
public class NpcReleasingState : NpcStateBase
{
    // 목적지에 이만큼(m) 다가오면 도착으로 본다 — NpcIntrudeState·NpcJailedState와 같은 기준
    private const float k_arriveDistance = 0.5f;

    // 온전하지 않은 경로를 다시 거는 간격(초) — 상한은 없다 (아래 Tick)
    private const float k_pathRetryInterval = 0.35f;

    // 도착해 멈춰 섰는가 — 매 프레임 다시 판정하지 않으려는 래치. 경로 실패로는 켜지지 않는다.
    private bool m_stopped;

    // 목적지를 아직 안 걸었다 — <b>진입 프레임에는 걸지 않는다</b>. 문 밖 워프와 같은 프레임에
    // SetDestination을 부르면 에이전트가 아직 워프 전(감옥 안)이라, 도시와 끊긴 감옥 섬(#537)에서
    // 경로가 잡혀 대상이 문 앞 몇 발짝 만에 '도착'해 굳는다. 한 프레임 미루면 제자리에서 잡힌다.
    private bool m_pendingDestination;

    private float m_nextRetryTime;     // 다음 경로 재계산 시각
    private bool m_brokenPathReported; // 끊긴 경로 경고는 한 번만

    private float m_baseSpeed; // 진입 전 원래 속도 — Exit 복원값

    // 도주와 같은 질주 배율을 읽는다 — 반출은 걸어 나가는 산책이 아니라 빠져나가는 길이다
    private readonly NpcFleeConfig m_fleeConfig;

    public NpcReleasingState(NpcController owner, NpcFleeConfig fleeConfig)
        : base(owner)
    {
        m_fleeConfig = fleeConfig;
    }

    public override void Enter()
    {
        m_stopped = false;
        m_pendingDestination = true; // 워프가 반영된 다음 틱에 건다 (필드 주석 참고)
        m_nextRetryTime = 0f;
        m_brokenPathReported = false;
        m_owner.Agent.isStopped = false;
        m_owner.Agent.stoppingDistance = 0f;

        // 걷지 않고 뛴다 (2026-08-11 확정) — 걸음 속도로 두면 저지가 "따라가서 때리면 그만"이 되어
        // 인도까지의 거리가 긴장을 만들지 못한다. 배율은 도주와 같은 것을 쓴다.
        m_baseSpeed = m_owner.Agent.speed;
        ApplyRunSpeed();
    }

    /// <summary>질주 배율을 씌운다 — 진입할 때와 <b>기절에서 깨어날 때</b>. (2026-08-12 확정)
    /// 깨어난 대상은 걸어가지 않고 뛴다: 쓰러뜨리기가 무산 수단이 아닌 이상, 느긋하게 걸어가면
    /// 저지에 실패한 쪽이 따라잡을 여지가 생긴다. 오버레이 기절은 상태를 안 바꿔 <see cref="Enter"/>가
    /// 다시 돌지 않으므로 여기서 확정한다. <see cref="m_baseSpeed"/> 기준이라 겹쳐 불려도 안전하다.</summary>
    private void ApplyRunSpeed()
    {
        m_owner.Agent.speed = m_baseSpeed * m_fleeConfig.SpeedMultiplier;
    }

    public override void Tick()
    {
        if (m_stopped)
            return;

        if (m_pendingDestination)
        {
            m_pendingDestination = false;
            SetReleaseDestination();
            return;
        }

        if (m_owner.Agent.pathPending)
            return;

        // <b>온전한 경로가 아니면 될 때까지 다시 건다</b> (2026-08-12 확정) — 상한도 포기도 없다.
        // 들어오는 경우는 셋: 기절이 지운 경로(EnterStunned의 ResetPath), 끊긴 경로(워프 정착·동적
        // 장애물), 목적지를 NavMesh에 못 붙임. 어느 쪽이든 세우지 않는다 — 못 닿는 배치면 닿는 데까지
        // 가서 계속 다시 거는 모습이 되는데, 조용히 굳는 것보다 낫다(아래 경고가 그 배치를 짚어 준다).
        if (!m_owner.Agent.hasPath || m_owner.Agent.pathStatus != NavMeshPathStatus.PathComplete)
        {
            ReportBrokenPathOnce();

            if (Time.time >= m_nextRetryTime)
            {
                m_nextRetryTime = Time.time + k_pathRetryInterval;
                ApplyRunSpeed(); // 기절에서 깨어난 경로가 여기다 — 걸음이 아니라 질주로 다시 출발
                SetReleaseDestination();
            }
        }

        if (!HasArrived())
            return;

        StopHere();
    }

    /// <summary>인도 지점에 닿았는가 — <b>실제 목적지까지의 거리로만 판정한다</b> (2026-08-12 확정).
    /// 끊긴 경로의 끝점(<see cref="NavMeshAgent.pathEndPosition"/>)을 도착으로 치던 것이 <b>문 앞에서
    /// 굳는 원인</b>이었다: 감옥 섬 안에서 끝난 경로 탓에 몇 발짝 만에 '도착'해 완수 범위 밖에 섰다.
    /// 경로가 온전하면 경로 거리를, 아니면 직선 거리를 본다 — 후자는 목적지 위에 섰는데 질의만
    /// 실패하는 경우의 안전망이다.</summary>
    private bool HasArrived()
    {
        if (m_owner.Agent.pathStatus == NavMeshPathStatus.PathComplete && m_owner.Agent.hasPath)
            return m_owner.Agent.remainingDistance <= k_arriveDistance;

        return Vector3.Distance(m_owner.transform.position, m_owner.Custody.ReleaseDestination)
            <= k_arriveDistance;
    }

    // 끊긴 경로를 로그로 드러낸다 — 조용히 굳으면 "목적지를 못 찾는다"로만 보여 배치 문제인지
    // 타이밍인지 가릴 단서가 없다. 계속 다시 걸므로 구간당 한 번만 찍는다.
    private void ReportBrokenPathOnce()
    {
        if (m_brokenPathReported)
            return;
        m_brokenPathReported = true;

        Debug.LogWarning(
            $"NpcReleasingState: 인도 지점까지 경로가 온전하지 않다({m_owner.Agent.pathStatus}) — "
                + $"닿을 때까지 계속 다시 건다: {m_owner.name}",
            m_owner
        );
    }

    // 인도 지점을 목적지로 건다 — <b>실패해도 세우지 않는다</b>. 못 붙이는 것은 대개 그 순간의
    // 사정이라, 한 번 실패했다고 포기하면 방출한 대상이 길 위에 영영 굳는다. Tick이 계속 다시 부른다.
    private void SetReleaseDestination()
    {
        if (m_owner.Agent.SetDestination(m_owner.Custody.ReleaseDestination))
            return;

        ReportBrokenPathOnce();
    }

    public override void Exit()
    {
        // 목적지는 여기서 지우지 않는다 — Exit은 <b>어디로 나가는지를 알 수 없어</b>(ChangeState가
        // CurrentState 갱신 전에 부른다) 기절·반응만 골라 남길 수가 없다. NpcController의 상태 훅이 맡는다.
        m_owner.Agent.speed = m_baseSpeed; // 질주 배율 원복 (NpcFleeState.Exit과 같은 자리)

        // 밧줄에 묶이거나(Escorted) 기절해 이 상태를 벗어날 때 이동을 복구한다 (NpcIntrudeState.Exit과 동일)
        if (m_owner.Agent.isOnNavMesh)
        {
            m_owner.Agent.isStopped = false;
            m_owner.Agent.ResetPath();
        }
    }

    private void StopHere()
    {
        m_stopped = true;

        m_owner.Agent.isStopped = true;
        m_owner.Agent.velocity = Vector3.zero;
        if (m_owner.Agent.isOnNavMesh)
            m_owner.Agent.ResetPath();
    }
}
