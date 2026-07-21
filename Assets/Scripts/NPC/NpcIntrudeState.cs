using UnityEngine;

/// <summary>
/// 침입(Intruding) 상태 — 돌발 이벤트가 스폰한 침입자가 목표 지점까지 걸어가 자물쇠를 연다. (GDD 6-4, #231/#261)
/// 이동과 도착을 한 상태 안에서 처리하는 구조는 수감(NpcJailedState, #228)과 같다.
///
/// 도착은 끝이 아니라 <b>해제 채널링의 시작</b>이다 — 그 자리에 멈춰 IntrudeUnlockSeconds를 채우는 동안이
/// 플레이어의 대응 구간이고, 다 채워야 도착(reached=true)을 통보한다. 채널링 도중 수갑에 걸리면 FSM이
/// 다른 상태로 전이하며 이 상태를 빠져나가므로 채널링은 자연히 취소된다 — 취소 통보는 하지 않는다.
/// (탈출 이벤트가 OnStateChanged로 이탈을 감지해 스스로 정리한다. 여기서 통보하면 정상 경로와 이중 발행이 된다)
///
/// 수감과 다른 점은 <b>경로 실패를 도착으로 처리하지 않는다</b>는 것이다 — 수감은 그 자리에서 수용해도
/// 결과가 같지만, 침입은 자물쇠에 닿지 못했는데 도착으로 치면 열리면 안 될 자물쇠가 열린다.
/// 실패는 실패로 통보하고 이벤트가 불발로 정리한다.
/// </summary>
public class NpcIntrudeState : NpcStateBase
{
    // 목표 지점에 이만큼(m) 다가오면 도착으로 본다 — NpcJailedState와 같은 기준
    private const float k_arriveDistance = 0.5f;

    private bool m_finished; // 도착·실패 통보를 한 번만 보내기 위한 래치
    private bool m_unlocking; // 도착해 해제 채널링에 들어갔는가 — 이동/해제 두 페이즈를 가른다
    private float m_unlockEndTime; // 해제 완료 예정 시각(Time.time)

    public NpcIntrudeState(NpcController owner) : base(owner) { }

    public override void Enter()
    {
        m_finished = false;
        m_unlocking = false;
        m_owner.Agent.isStopped = false;
        m_owner.Agent.stoppingDistance = 0f;

        if (m_owner.IntrudeTarget == null)
        {
            Debug.LogWarning($"NpcIntrudeState: 침입 목표가 없음 — 불발 처리: {m_owner.name}", m_owner);
            Finish(false);
            return;
        }

        // 경로를 못 잡으면(목표가 NavMesh 밖 등) 영원히 걷는 자세로 남는다 — 불발로 통보한다
        if (!m_owner.Agent.SetDestination(m_owner.IntrudeTarget.position))
        {
            Debug.LogWarning($"NpcIntrudeState: 침입 경로 실패 — 불발 처리: {m_owner.name}", m_owner);
            Finish(false);
        }
    }

    public override void Tick()
    {
        if (m_finished)
            return;

        // 해제 페이즈 — 그 자리에서 시간을 채운다. 이 구간이 플레이어가 달려와 막을 수 있는 시간이다
        if (m_unlocking)
        {
            if (Time.time >= m_unlockEndTime)
                Finish(true);
            return;
        }

        if (m_owner.Agent.pathPending)
            return;

        if (m_owner.Agent.remainingDistance > k_arriveDistance)
            return;

        BeginUnlock();
    }

    public override void Exit()
    {
        // 도주(StartFlee) 등으로 이 상태를 벗어날 때 이동을 복구한다 (NpcJailedState.Exit과 동일)
        if (m_owner.Agent.isOnNavMesh)
        {
            m_owner.Agent.isStopped = false;
            m_owner.Agent.ResetPath();
        }
    }

    // 자물쇠 앞 도착 — 멈춰 서서 해제 채널링을 시작하고 이벤트에 알린다(본부 경보가 여기서 울린다)
    private void BeginUnlock()
    {
        m_unlocking = true;
        m_unlockEndTime = Time.time + m_owner.IntrudeUnlockSeconds;

        StopAgent();
        m_owner.NotifyIntrudeUnlockStarted();
    }

    // 도착·실패 확정 — 그 자리에 세우고 이벤트에 결과를 알린다
    private void Finish(bool reached)
    {
        m_finished = true;

        StopAgent();
        m_owner.NotifyIntrudeFinished(reached);
    }

    private void StopAgent()
    {
        m_owner.Agent.isStopped = true;
        m_owner.Agent.velocity = Vector3.zero;
        if (m_owner.Agent.isOnNavMesh)
            m_owner.Agent.ResetPath();
    }
}
