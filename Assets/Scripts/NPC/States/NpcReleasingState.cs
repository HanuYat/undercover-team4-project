using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// 반출 보행(Releasing) 상태 — 감옥에서 꺼내진 대상이 인도 지점까지 <b>스스로</b> 걸어간다. (#548)
/// 목적지는 <see cref="NpcCustody.ReleaseDestination"/>이고, 그 값을 정하는 것은 비밀 청탁
/// (<see cref="SecretFavorBroker"/>)이다 — 이 상태는 "어디로"만 받아 걷는다.
///
/// <b>도착 판정은 이 상태가 하지 않는다.</b> 여기 도착하면 그 자리에 설 뿐이고, 완수(대상 소멸 + 보수)는
/// 브로커가 굴린다 — <b>대상이 인도 범위에 들어온 것만으로</b> 완수다(의뢰인은 없어도 된다).
/// 도착 통보를 따로 보내지 않는 이유가 그것이다: 브로커는 이미 매 프레임 대상 좌표를 보고 있어 알릴 것이 없다.
/// 여기서 멈추는 기준(k_arriveDistance)과 완수 기준(인도 범위 박스)은 서로 다른 자다 — 이 상태는 목적지
/// 앞에 서는 것까지만 책임지고, 완수 여부는 범위가 정한다.
///
/// <b>저지 구간이 이 상태의 존재 이유다.</b> 달리는 동안은 도주형 NPC와 같은 취급이라
/// 진압봉·테이저로 때리고(<see cref="NpcStateRules.CanBeDamaged"/>) 기절시킨 뒤 밧줄로 묶을 수 있다
/// (<see cref="NpcStateRules.CanArrest"/>). 수갑만 막힌다 — 도주(Run)·저항(Attack)과 같은 이유다.
///
/// <b>한 대라도 맞으면 반출은 무산된다</b> (2026-08-11 확정). 배정된 반응 유형대로 달아나거나
/// 맞서고(<see cref="NpcStateRules.CanReactToDamage"/>), 순응형이면 그 자리에서 둘 중 하나로 굳는다 —
/// 시민이 맞았을 때와 같은 규칙이다. 스캔으로는 꿈쩍하지 않는다: 쳐다봤다고 그만두면 저지가 너무 싸진다.
///
/// 이전에는 <b>쓰러뜨려야만</b> 무산됐고 그 사이 대상은 맞으면서도 목적지로 계속 걸었다 —
/// 맞고도 아무 일 없이 걸어가는 그림이 "저지당했다"로 읽히지 않았다. 지금 쓰러뜨리기는 무산 조건이
/// 아니라 <b>붙잡기 위한 수단</b>이다.
///
/// 피해 없이 무력화하는 경로(테이저)는 여전히 기절을 거친다 — 깨어나면 인도 지점으로 돌아가는 대신
/// 도주로 전환된다(<see cref="NpcStun.ExitStun"/>). 넉백 착지 KO도 결과가 같다 — 그쪽은
/// <see cref="NpcState.Stunned"/>로 전이했다가 자기 상태 클래스가 도주로 내보낸다.
/// 어느 경로든 완수를 되살리려면 <b>붙잡아 인도 지점까지 끌고 가는 수밖에 없다</b>.
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
        // <b>이 상태를 벗어나면 반출은 그것으로 끝난다</b> (#548) — 목적지를 여기서 지운다.
        // 밧줄에 묶이든(Escorted) 넉백 착지 KO로 쓰러지든(Stunned) 기절에서 깨어나 달아나든(Run),
        // 어느 경로로 나가도 인도 지점으로 다시 걷지 않는다. 나가는 자리가 여럿이라 각자 지우게 두면
        // 하나를 빠뜨렸을 때 목적지만 살아남아 나중에 엉뚱하게 되살아난다.
        m_owner.Custody.ClearRelease();

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
