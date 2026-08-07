using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// 반출 보행(Releasing) 상태 — 감옥에서 꺼내진 대상이 인도 지점까지 <b>스스로</b> 걸어간다. (#548)
/// 목적지는 <see cref="NpcCustody.ReleaseDestination"/>이고, 그 값을 정하는 것은 비밀 청탁
/// (<see cref="SecretFavorBroker"/>)이다 — 이 상태는 "어디로"만 받아 걷는다.
///
/// <b>도착이 끝이 아니다.</b> 여기 도착하면 그 자리에 서서 기다릴 뿐이고, 완수 판정(대상 소멸 + 보수)은
/// 예전 그대로 브로커가 굴린다 — 의뢰인도 함께 인도 범위 안에 있어야 완수이기 때문이다. 도착 통보를
/// 따로 보내지 않는 이유가 그것이다: 브로커는 이미 매 프레임 대상 좌표를 보고 있어 알릴 것이 없다.
///
/// <b>저지 구간이 이 상태의 존재 이유다.</b> 걷는 동안은 도주형 NPC와 같은 취급이라
/// 진압봉·테이저로 때리고(<see cref="NpcStateRules.CanBeDamaged"/>) 기절시킨 뒤 밧줄로 묶을 수 있다
/// (<see cref="NpcStateRules.CanArrest"/>). 수갑만 막힌다 — 도주(Run)·저항(Attack)과 같은 이유다.
/// 기절해 있는 동안은 코어의 스턴 게이트가 FSM Tick을 통째로 건너뛰므로 걸음도 함께 멈추고,
/// 풀리면 이 상태 그대로 다시 걷는다(반응·배회군이 아니라 도주로 전환되지 않는다, #292).
///
/// 이동/도착을 한 상태 안에서 처리하는 구조는 침입(<see cref="NpcIntrudeState"/>)·수감
/// (<see cref="NpcJailedState"/>)과 같다. 경로 실패를 도착으로 처리하지 않는 것도 침입과 같다 —
/// 인도 지점에 닿지도 못했는데 도착으로 치면 그 자리에서 청탁이 완수될 수 있다.
/// </summary>
public class NpcReleasingState : NpcStateBase
{
    // 목적지에 이만큼(m) 다가오면 도착으로 본다 — NpcIntrudeState·NpcJailedState와 같은 기준
    private const float k_arriveDistance = 0.5f;

    // 도착(또는 경로 실패)해 멈춰 섰는가 — 멈춘 뒤 매 프레임 다시 판정하지 않으려는 래치
    private bool m_stopped;

    public NpcReleasingState(NpcController owner)
        : base(owner) { }

    public override void Enter()
    {
        m_stopped = false;
        m_owner.Agent.isStopped = false;
        m_owner.Agent.stoppingDistance = 0f;

        // 경로를 아예 못 잡으면(목적지가 NavMesh 밖 등) 영원히 걷는 자세로 남는다 — 그 자리에 세운다.
        // 경로가 잡히되 중간에 끊기는 경우는 여기가 아니라 Tick의 RemainingDistance가 받는다.
        // 청탁은 만료로 접히므로(SecretFavorBroker.m_favorExpireSeconds) 여기서 따로 취소하지 않는다.
        if (!m_owner.Agent.SetDestination(m_owner.Custody.ReleaseDestination))
        {
            Debug.LogWarning(
                $"NpcReleasingState: 인도 지점 경로 실패 — 그 자리에 세운다: {m_owner.name}",
                m_owner
            );
            StopHere();
        }
    }

    public override void Tick()
    {
        if (m_stopped)
            return;

        if (m_owner.Agent.pathPending)
            return;

        if (RemainingDistance() > k_arriveDistance)
            return;

        StopHere();
    }

    /// <summary>
    /// 목적지까지 남은 거리 — <b>끊긴 경로를 함께 다룬다</b>.
    ///
    /// <see cref="NavMeshAgent.remainingDistance"/>는 경로가 <see cref="NavMeshPathStatus.PathPartial"/>이면
    /// 무한대를 돌려준다. 그대로 비교하면 도착 판정이 <b>영원히 성립하지 않아</b> 갈 수 있는 데까지 간 뒤에도
    /// 걷는 자세로 제자리에 남는다. 실측에서 실제로 걸렸다 (2026-08-07): Apocalypse 맵 3번 인도 지점은
    /// 한가운데가 인도 턱 위라 도시 쪽 NavMesh와 이어지지 않고, 경로가 1.5m 앞에서 끊긴다.
    ///
    /// 그럴 때는 <see cref="NavMeshAgent.pathEndPosition"/>(갈 수 있는 데까지의 끝점)까지로 잰다 —
    /// 목적지에 못 닿아도 <b>닿을 수 있는 만큼 갔으면 거기서 멈추는 것</b>이 맞다. 인도 범위는 점이 아니라
    /// 상자라(<see cref="SecretFavorDropoff"/>) 조금 못 미쳐 서도 완수 판정에는 들어간다.
    /// </summary>
    private float RemainingDistance()
    {
        float remaining = m_owner.Agent.remainingDistance;
        return float.IsInfinity(remaining)
            ? Vector3.Distance(m_owner.transform.position, m_owner.Agent.pathEndPosition)
            : remaining;
    }

    public override void Exit()
    {
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
