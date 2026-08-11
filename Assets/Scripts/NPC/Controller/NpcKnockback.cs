using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// 넉백 도메인 부품 (#232/#503) — 외력으로 날아가는 포물선 비행을 들고 있다.
/// 비행 중에는 FSM과 NavMeshAgent가 멈춘다. 동기화 값은 없다 — 위치는 NetworkTransform이,
/// 착지 후 상태는 코어의 m_networkState가 복제한다. 비행 틱은 코어 Update의 넉백 게이트가 돌린다.
/// </summary>
public class NpcKnockback : NetworkBehaviour
{
    private NpcController m_owner;

    // 넉백 비행 상태 — 서버(또는 오프라인)에서만 의미.
    private Vector3 m_knockbackVelocity;
    private Vector3 m_knockbackLaunch;
    private float m_knockbackElapsed;
    private bool m_knockbackActive;
    private NpcState m_knockbackLandingState; // 착지 후 돌아갈 상태 — 검거 중이었으면 Captured, 그 외엔 Stunned

    private void Awake()
    {
        m_owner = GetComponent<NpcController>();
    }

    /// <summary>폭발 등으로 날아가는 중인가 — 이 동안 FSM·NavMesh는 멈춘다. 서버(또는 오프라인)에서만 유효.</summary>
    public bool IsKnockedBack => m_knockbackActive;

    /// <summary>
    /// 외력으로 날려보낸다 — 폭발 넉백(<see cref="BombDevice"/>) 등. 세기는 m/s 단위 초기 속도로 준다.
    ///
    /// <b>서버(또는 오프라인) 전용</b> — 플레이어 넉백은 각 피어가 자기 오너 캐릭터에 적용하지만
    /// (<see cref="PlayerMovement.AddKnockback"/>), NPC는 이동 권한이 서버의 NavMeshAgent에 있다.
    ///
    /// 수감·침입은 제외한다 — 이벤트가 그 NPC의 진행(수용·자물쇠 해제)을 쥐고 있어서 중간에 날아가면
    /// 판정 경로가 끊긴다. 체포·연행 중인 NPC는 수갑을 찬 채 날아가 착지 후에도 체포 상태로 남는다.
    /// </summary>
    public void ServerApplyKnockback(Vector3 velocity)
    {
        if (IsSpawned && !IsServer)
            return;
        if (m_knockbackActive)
            return; // 같은 폭발이 콜라이더 여러 개로 잡힌 중복 호출 — 처음 것만 받는다
        if (velocity.sqrMagnitude < 0.01f)
            return;

        NpcState state = m_owner.StateMachine.CurrentState; // 서버 진실값 — 동기화 지연 없이 판정
        if (state == NpcState.Jailed || state == NpcState.Intruding)
            return;

        // 스턴 오버레이와 겹치면 넉백이 이긴다 (#292). 그냥 두면 Update의 스턴 게이트가 착지 후
        // NpcStunnedState.Tick을 가로채 깨어나지 못한다. 기절 시간은 넉백 기준으로 새로 흐른다.
        m_owner.Stun.ClearStunOverlay();

        // 상태 전이가 먼저다 — 에이전트를 끄기 전에 넣어야 상태 클래스가 에이전트를 정상적으로 정리한다.
        // 비행 중에는 FSM Tick을 건너뛰므로 상태별 타이머는 착지 후부터 흐른다.
        m_knockbackLandingState =
            state == NpcState.Captured || state == NpcState.Escorted
                ? NpcState.Captured // 검거 유지 — 폭발로 수갑이 풀리지는 않는다
                : NpcState.Stunned; // 그 외엔 축 늘어져 날아가 기절 상태로 착지한다

        if (state == NpcState.Escorted)
            m_owner.Custody.StopEscort(); // 연행만 해제 — 에이전트 정리는 Escorted.Exit이 맡는다
        else
            m_owner.StateMachine.ChangeState(m_knockbackLandingState);

        m_knockbackActive = true;
        m_knockbackVelocity = velocity;
        m_knockbackLaunch = transform.position;
        m_knockbackElapsed = 0f;

        // 에이전트가 켜져 있으면 매 프레임 NavMesh 위로 끌어내려 애초에 뜨지 못한다
        NavMeshAgent agent = m_owner.Agent;
        if (agent.enabled)
        {
            if (agent.isOnNavMesh)
                agent.ResetPath();
            agent.enabled = false;
        }
    }

    /// <summary>
    /// 비행을 중단한다 — <b>착지 처리를 하지 않는다.</b> 사망(<see cref="NpcDeath"/>) 전용. (#571)
    ///
    /// <see cref="EndKnockback"/>과 갈라 둔 이유가 전부다: 저쪽은 NavMesh 위 착지점을 찾아 에이전트를
    /// 되살리고 상태를 전이시키는데, 죽은 몸에는 셋 다 틀렸다. 여기서는 플래그만 내려 코어 Update의
    /// 넉백 게이트에서 빠져나오게 하고, 에이전트·상태는 부르는 쪽(사망)이 정한다.
    /// 에이전트는 이미 <see cref="ServerApplyKnockback"/>이 꺼 뒀으므로 그대로 두면 된다.
    /// </summary>
    internal void ServerAbortFlight()
    {
        m_knockbackActive = false;
        m_knockbackVelocity = Vector3.zero;
    }

    // 포물선 비행 1프레임. 착지하면 NavMesh 위로 되돌리고 발사 시점에 정한 상태로 넘긴다.
    internal void Tick()
    {
        NpcCommonConfig config = m_owner.CommonConfig;

        m_knockbackElapsed += Time.deltaTime;
        m_knockbackVelocity.y += config.KnockbackGravity * Time.deltaTime;

        Vector3 next = transform.position + m_knockbackVelocity * Time.deltaTime;

        // 벽을 뚫고 날아가지 않게 이번 프레임 수평 이동분을 코어 공용 스윕으로 훑는다
        Vector3 horizontalStep = new Vector3(
            next.x - transform.position.x,
            0f,
            next.z - transform.position.z
        );
        float stepDistance = horizontalStep.magnitude;
        if (
            stepDistance > 0.0001f
            && m_owner.SweepHitsObstacle(horizontalStep / stepDistance, stepDistance, out _)
        )
        {
            // 진짜 벽에 닿았다 — 수평 성분을 버리고 그 자리에서 떨어진다
            next.x = transform.position.x;
            next.z = transform.position.z;
            m_knockbackVelocity.x = 0f;
            m_knockbackVelocity.z = 0f;
        }

        transform.position = next;

        bool timedOut = m_knockbackElapsed >= config.KnockbackMaxFlightSeconds;
        if (!timedOut && m_knockbackVelocity.y > 0f)
            return; // 아직 상승 중 — 착지 판정은 내려올 때부터

        // 통행 마스크로 착지점을 찾는다 — 못 가는 영역(Jail)에 Warp되면 경로가 안 잡혀 고착된다 (#415)
        if (
            NavMesh.SamplePosition(
                transform.position,
                out NavMeshHit ground,
                config.KnockbackLandSampleDistance,
                m_owner.Agent.areaMask
            )
        )
        {
            if (!timedOut && transform.position.y > ground.position.y + 0.05f)
                return; // 아직 공중

            EndKnockback(ground.position);
            return;
        }

        // NavMesh를 아예 벗어난 곳까지 날아갔다 — 시간이 다 되면 출발점으로 회수한다(맵 밖 유실 방지)
        if (timedOut)
            EndKnockback(m_knockbackLaunch);
    }

    private void EndKnockback(Vector3 landing)
    {
        m_knockbackActive = false;
        m_knockbackVelocity = Vector3.zero;

        NavMeshAgent agent = m_owner.Agent;
        agent.enabled = true;
        agent.Warp(landing); // 에이전트를 NavMesh 위 착지점에 다시 붙인다

        // Warp가 실패했으면(착지점이 NavMesh 밖) 상태 전이를 시키지 않는다 — 상태 클래스들이 곧바로
        // 에이전트를 건드려 에러가 난다. 에이전트는 TickNavMeshRecovery가 1초 뒤 다시 붙이지만(#557)
        // 아래 '보정' 전이는 되살아나지 않는다.
        if (!agent.isOnNavMesh)
        {
            Debug.LogWarning(
                "NpcKnockback: 넉백 착지 지점을 NavMesh에 붙이지 못했다 — 회수 대기: "
                    + $"{name} @{transform.position.ToString("F1")}",
                this
            );
            return;
        }

        // 이미 발사 시점에 전이해 뒀다 — 여기 호출은 그 사이 상태가 바뀐 경우를 위한 보정이다.
        // (같은 상태면 StateMachine이 무시하므로 상태별 타이머가 착지 시점에 리셋되지도 않는다)
        m_owner.StateMachine.ChangeState(m_knockbackLandingState);
    }
}
