using UnityEngine;

/// <summary>
/// 밀수 운반(Smuggling) 상태 — 화물을 지고 거래 지점까지 걸어간다. 도착하면 그대로 빠져나간다.
/// (GDD 6-4, #991)
///
/// 목적지 하나를 향해 걷고 도착을 통보하는 구조는 침입(<see cref="NpcIntrudeState"/>)과 같다.
/// 다른 점은 <b>도착 뒤에 채널링이 없다</b>는 것이다 — 자물쇠를 여는 대응 구간이 있는 침입과 달리
/// 여기서는 거래 지점에 닿는 순간이 곧 실패라, 대응 구간은 도착 <b>전</b>의 이동 시간 전체다.
///
/// 경로 실패를 도착으로 처리하지 않는 것도 침입과 같다 — 닿지도 못한 화물이 빠져나가면 안 된다.
/// 실패는 실패로 통보하고 이벤트가 불발로 정리한다.
///
/// <b>위협에 반응하지 않는다.</b> <see cref="NpcState.Smuggling"/>이
/// <see cref="NpcStateRules.IsReactive"/>에 없어 스캔·타격이 도주·저항으로 갈아타지 못하고,
/// 같은 이유로 기절이 풀리면 아무 전이 없이 하던 운반을 이어서 한다.
/// </summary>
public class NpcSmuggleState : NpcStateBase
{
    // 맨홀 지점에 이만큼(m) 다가오면 도착으로 본다 — NpcIntrudeState와 같은 기준
    private const float k_arriveDistance = 0.5f;

    // 경로 재시도 최소 간격(초) — 목적지가 잠시 닿지 않을 때 매 프레임 경로를 계산하지 않게
    private const float k_repathInterval = 0.5f;

    private SmugglerCargo m_cargo;
    private float m_baseSpeed;
    private float m_nextRepathTime;
    private bool m_finished; // 도착·실패 통보를 한 번만 보내기 위한 래치

    public NpcSmuggleState(NpcController owner)
        : base(owner) { }

    public override void Enter()
    {
        m_finished = false;
        m_owner.SetAgentStopped(false);
        m_owner.Agent.stoppingDistance = 0f;

        // 화물 표식은 이벤트가 스폰 직후 붙인다 — 캐시하지 않는 이유는 기절·연행으로 이 상태를
        // 드나들 수 있어서다(붙는 시점이 이 상태의 생성보다 늦다).
        m_cargo = m_owner.GetComponent<SmugglerCargo>();

        // 짐이 무거워 보이게 기본 걸음보다 느리게 간다 — 개체별 속도 편차(스폰 시 추첨) 위에 곱한다
        m_baseSpeed = m_owner.Agent.speed;
        ApplySpeed();

        if (m_cargo == null || m_cargo.Destination == null)
        {
            Debug.LogWarning(
                $"NpcSmuggleState: 맨홀 지점이 없음 — 불발 처리: {m_owner.name}",
                m_owner
            );
            Finish(false);
            return;
        }

        // 경로를 못 잡으면(지점이 NavMesh 밖 등) 영원히 걷는 자세로 남는다 — 불발로 통보한다
        if (!m_owner.Agent.SetDestination(m_cargo.Destination.position))
        {
            Debug.LogWarning(
                $"NpcSmuggleState: 운반 경로 실패 — 불발 처리: {m_owner.name}",
                m_owner
            );
            Finish(false);
        }
    }

    public override void Tick()
    {
        // 기절·래그돌 동안에는 에이전트가 꺼져 있다 — 그때 remainingDistance를 읽으면 안 된다
        if (m_finished || !m_owner.AgentReady)
            return;

        // 배율을 매 틱 다시 건다 — 맞으면 도중에 올라간다(SmugglerCargo.ServerPanic, #991)
        ApplySpeed();

        if (m_owner.Agent.pathPending)
            return;

        // ⚠ <b>경로가 없으면 도착이 아니라 재경로다.</b> 기절에서 깨어나면 래그돌이 몸을 내려놓으며
        // 에이전트를 Warp하는데(NpcRagdoll), Warp는 경로를 버린다. 경로가 빈 에이전트의
        // remainingDistance는 0이라, 이 분기가 없으면 <b>일어나는 순간 도착으로 오판해</b>
        // 맨홀 근처도 아닌 자리에서 화물이 지하로 사라진다.
        // (질주 상태가 같은 자리에서 목적지를 다시 뽑는 것과 같은 처리다 — NpcSprintState)
        if (!m_owner.Agent.hasPath)
        {
            Repath();
            return;
        }

        if (m_owner.Agent.remainingDistance > k_arriveDistance)
            return;

        Finish(true);
    }

    // 목적지를 다시 건다 — 못 잡으면 다음 틱에 또 시도한다(재시도 간격으로 매 프레임 계산을 막는다).
    private void Repath()
    {
        if (m_cargo == null || m_cargo.Destination == null || Time.time < m_nextRepathTime)
            return;

        m_nextRepathTime = Time.time + k_repathInterval;
        m_owner.Agent.SetDestination(m_cargo.Destination.position);
    }

    private void ApplySpeed()
    {
        if (m_cargo != null)
            m_owner.Agent.speed = m_baseSpeed * m_cargo.SpeedMultiplier;
    }

    public override void Exit()
    {
        m_owner.Agent.speed = m_baseSpeed;

        // 도주·연행 등으로 이 상태를 벗어날 때 이동을 복구한다 (NpcIntrudeState.Exit과 동일)
        if (m_owner.Agent.isOnNavMesh)
        {
            m_owner.SetAgentStopped(false);
            m_owner.Agent.ResetPath();
        }
    }

    // 도착·실패 확정 — 그 자리에 세우고 이벤트에 결과를 알린다
    private void Finish(bool reached)
    {
        m_finished = true;

        m_owner.SetAgentStopped(true);
        m_owner.Agent.velocity = Vector3.zero;
        if (m_owner.Agent.isOnNavMesh)
            m_owner.Agent.ResetPath();

        if (m_cargo != null)
            m_cargo.NotifyFinished(m_owner, reached);
    }
}
