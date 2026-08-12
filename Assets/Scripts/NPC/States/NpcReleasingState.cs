using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// 반출 보행(Releasing) 상태 — 감옥에서 꺼내진 대상이 인도 지점까지 <b>스스로</b> 달려간다. (#548)
/// 목적지는 <see cref="NpcCustody.ReleaseDestination"/>이고 그 값은 비밀 청탁(<see cref="SecretFavorBroker"/>)이
/// 정한다 — 이 상태는 "어디로"만 받아 달린다. 구조는 침입(<see cref="NpcIntrudeState"/>)과 같다.
///
/// <b>완수 판정은 하지 않는다.</b> 여기서는 목적지 앞에 서는 것까지만 맡고, 완수(대상 소멸 + 보수)는
/// 브로커가 인도 범위로 판정한다 — 멈추는 기준(<see cref="k_arriveDistance"/>)과 완수 기준(범위 박스)은
/// 다른 자다. 브로커가 이미 매 프레임 대상 좌표를 보므로 도착 통보도 보내지 않는다.
///
/// <b>저지 구간이 이 상태의 존재 이유다.</b> 달리는 동안은 도주형과 같은 취급이라 때리고 기절시켜
/// 밧줄로 묶을 수 있다(수갑만 막힌다). <b>쓰러지지 않는 타격 한 대면 반출은 무산된다</b>
/// (2026-08-11 확정) — <see cref="NpcStateRules.CanReactToDamage"/>가 그 규칙이고, 배정된 유형대로
/// 달아나거나 맞선다. 스캔으로는 꿈쩍하지 않는다: 쳐다봤다고 그만두면 저지가 너무 싸진다.
///
/// <b>쓰러뜨리는 것은 무산 조건이 아니라 붙잡기 위한 수단이다</b> (2026-08-12 확정). 기절 경로
/// (테이저·넉다운·넉백 KO)에서 깨어난 대상은 도주로 갈아타지 않고 <b>가던 길을 잇는다</b> —
/// 눕혀 놓고 지켜보기만 하면 일어나 다시 걸어가므로, 저지하려면 그 창에 밧줄로 묶어 끌고 가야 한다.
/// 목적지가 기절을 견디는 것도 그래서다(<see cref="NpcController"/>의 상태 훅) — 이 상태를 벗어나며
/// 목적지를 지우는 것은 <b>무산 경로들뿐</b>이다: 도주·저항 전환, 밧줄 묶기, 재수감, 사망, 의뢰 취소.
/// </summary>
public class NpcReleasingState : NpcStateBase
{
    // 목적지에 이만큼(m) 다가오면 도착으로 본다 — NpcIntrudeState·NpcJailedState와 같은 기준
    private const float k_arriveDistance = 0.5f;

    // 도착(또는 경로 실패)해 멈춰 섰는가 — 멈춘 뒤 매 프레임 다시 판정하지 않으려는 래치
    private bool m_stopped;

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
        m_owner.Agent.isStopped = false;
        m_owner.Agent.stoppingDistance = 0f;

        // 걷지 않고 뛴다 (2026-08-11 확정) — 풀려난 대상은 여유롭게 걸어 나가지 않는다.
        // 걸음 속도로 두면 저지가 "따라가서 때리면 그만"이 되어 인도까지의 거리가 긴장을 만들지 못한다.
        // 배율은 도주와 같은 것을 쓴다 — 같은 "달아나는 걸음"이라 따로 조율할 값을 늘리지 않는다.
        m_baseSpeed = m_owner.Agent.speed;
        m_owner.Agent.speed = m_baseSpeed * m_fleeConfig.SpeedMultiplier;

        SetDestinationOrStop();
    }

    public override void Tick()
    {
        if (m_stopped)
            return;

        if (m_owner.Agent.pathPending)
            return;

        // <b>기절이 지운 경로를 다시 건다</b> (2026-08-12 확정). 오버레이 기절(테이저·넉다운)은
        // 상태를 안 바꿔 Enter가 다시 불리지 않으므로, 깨어나 걸음을 잇는 자리는 여기뿐이다
        // (NpcStun.EnterStunned가 ResetPath로 경로를 버린다).
        // 없으면 경로 없는 에이전트의 남은 거리가 0으로 읽혀 <b>깨어난 그 자리가 곧 도착</b>이 된다 —
        // 대상이 길 한복판에 굳어 완수도 무산도 아닌 상태로 남는다.
        if (!m_owner.Agent.hasPath)
        {
            SetDestinationOrStop();
            return;
        }

        if (RemainingDistance() > k_arriveDistance)
            return;

        StopHere();
    }

    // 인도 지점을 목적지로 건다 — 경로를 아예 못 잡으면(목적지가 NavMesh 밖 등) 영원히 걷는 자세로
    // 남으므로 그 자리에 세운다. 경로가 잡히되 중간에 끊기는 경우는 여기가 아니라 Tick의
    // RemainingDistance가 받는다.
    // 청탁은 만료로 접히므로(SecretFavorBroker.m_favorExpireSeconds) 여기서 따로 취소하지 않는다.
    private void SetDestinationOrStop()
    {
        if (m_owner.Agent.SetDestination(m_owner.Custody.ReleaseDestination))
            return;

        Debug.LogWarning(
            $"NpcReleasingState: 인도 지점 경로 실패 — 그 자리에 세운다: {m_owner.name}",
            m_owner
        );
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
        // <b>목적지는 여기서 지우지 않는다</b> (2026-08-12 확정). 나가는 곳마다 지우던 것을
        // <see cref="NpcController"/>의 상태 훅 한 곳으로 모았다 — 넉백 착지 KO(Stunned)로 나가는
        // 것만은 반출이 이어져야 하는데, Exit은 <b>어디로 나가는지를 알 수 없어</b>(NpcStateMachine이
        // CurrentState를 갱신하기 전에 부른다) 그 하나만 골라 남길 수가 없기 때문이다.
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
