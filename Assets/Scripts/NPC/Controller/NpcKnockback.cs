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
    ///
    /// <b>시체도 제외한다</b> (#634). 시체는 사라지지 않고 그 자리에 남으므로 <b>같은 자리를 지나는
    /// 외력이 계속 들어온다</b> — 상시 교통에서는 도로에 쓰러진 시민을 다음 차가 매번 다시 친다.
    /// 막지 않으면 셋이 어긋난다: ① 아래 상태 전이가 시체를 Stunned로 되돌리려다 FSM에 거부당해
    /// 매번 에러가 찍히고, ② 시체가 날아가고, ③ 착지 처리가 <b>에이전트를 다시 켜</b>
    /// <see cref="NpcDeath.ServerEnterDead"/> ⑥("시체는 NavMesh 위로 돌아가지 않는다")을 정면으로
    /// 뒤집는다 — 켜진 에이전트가 굳은 몸 정리(<c>TickStuckOffNavMesh</c>)의 감시 대상이 된다.
    /// 피해 쪽은 이미 <see cref="NpcStateRules.CanBeDamaged"/>가 같은 이유로 시체를 막고 있다.
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
        if (state == NpcState.Dead || state == NpcState.Jailed || state == NpcState.Intruding)
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

    /// <summary>
    /// 래그돌인 채로 날려보낸다 — 홈런 진압봉(#815). 서버(또는 오프라인) 전용.
    ///
    /// <b>위 <see cref="ServerApplyKnockback"/>과는 다른 문이다.</b> 그쪽은 스턴 오버레이를
    /// <see cref="NpcStun.ClearStunOverlay"/>로 걷어내고 <see cref="NpcState.Stunned"/> 전이로
    /// 뻣뻣한 포물선 비행을 만드는데, <see cref="NpcRagdoll.WantsRagdoll"/>이 그 오버레이를 보고
    /// 래그돌 진입을 판단하므로 오버레이를 걷으면 몸이 물리로 넘어가지 않는다. 그래서 여기서는
    /// 반대로 오버레이를 <b>켠 채</b> <see cref="NpcRagdoll.EnterRagdoll"/>을 직접 불러 뼈에 임펄스를
    /// 준다 — 폭발이 시체를 날릴 때(<see cref="BombBlast"/>)와 같은 문이고, 대상이 산 사람일 뿐이다.
    ///
    /// <b>순서가 사양이다.</b> 오버레이를 먼저 켜야 한다 — 뒤집으면 다음 <c>NpcRagdoll.Update</c>의
    /// 폴링이 <c>WantsRagdoll() == false</c>를 보고 몸을 곧바로 애니메이터에 되돌린다.
    /// </summary>
    /// <param name="impulse">뼈에 줄 초기 속도(m/s) — 수평 성분이 타격 방향, 수직 성분이 발사각이다.</param>
    /// <param name="stunSeconds">착지 후까지 누워 있는 시간(초). 비행 시간보다 짧으면 공중에서
    /// 일어나는 기상 사고가 난다 — 호출부(<c>HomeRunBaton</c>)가 발사 세기에 맞춰 넉넉히 잡는다.</param>
    /// <param name="threat">깨어나면 이 대상에게서 도주한다. null 허용.</param>
    public void ServerLaunchRagdoll(Vector3 impulse, float stunSeconds, Transform threat)
    {
        if (IsSpawned && !IsServer)
            return;
        if (impulse.sqrMagnitude < 0.01f)
            return;

        NpcState state = m_owner.StateMachine.CurrentState;
        if (state == NpcState.Dead || state == NpcState.Jailed || state == NpcState.Intruding)
            return;

        // 포물선 비행 중이었다면 끊는다 — 두 넉백이 같은 프레임에 transform.position을 다투면 안 된다.
        if (m_knockbackActive)
            ServerAbortFlight();

        // 이미 누워 있는 대상을 다시 후려치는 것이 이 아이템의 주 용도다 — 재무장해 타이머를 리셋한다.
        if (m_owner.Stun.HasStunOverlay)
            m_owner.Stun.ClearStunOverlay();
        m_owner.Stun.EnterStunned(threat, stunSeconds);

        // 정착해 잠들어 있었을 수 있다 — WakeCorpse는 래그돌 상태가 아니면 무동작이라 먼저 불러도 안전하다.
        m_owner.Ragdoll?.WakeCorpse();
        m_owner.Ragdoll?.EnterRagdoll(impulse);
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

        // 기준 마스크로 착지점을 찾는다 — 못 가는 영역(Jail)에 Warp되면 경로가 안 잡혀 고착된다 (#415).
        // 현재 통행 마스크가 아니라 기준값인 이유: 배회 중이면 도로가 빠져 있어(#634 후속) 차도 위로
        // 날아간 몸이 착지점을 못 찾고, 찾더라도 수 미터 옆 인도로 튕겨 착지 판정('아직 공중')이 어긋난다.
        if (
            NavMesh.SamplePosition(
                transform.position,
                out NavMeshHit ground,
                config.KnockbackLandSampleDistance,
                m_owner.BaseAreaMask
            )
        )
        {
            if (!timedOut && transform.position.y > ground.position.y + 0.05f)
                return; // 아직 공중

            EndKnockback(ground.position);
            return;
        }

        // NavMesh를 아예 벗어난 곳까지 날아갔다 — 시간이 다 되면 <b>그 자리에</b> 떨군다 (#913).
        // 예전에는 출발점으로 회수했는데, 홈런으로 밖에 나간 몸이 제자리로 순간이동하는 쪽이 더
        // 이상했다. NavMesh 밖에 남은 몸은 NpcController.TickStuckOffNavMesh가 행방불명으로 끝낸다.
        if (timedOut)
            EndKnockback(transform.position);
    }

    // 착지점이 옆으로 이만큼(m) 넘게 떨어져 있으면 붙이지 않는다 (#913) — 그건 착지가 아니라
    // 순간이동으로 보인다. 탐색 반경(KnockbackLandSampleDistance)은 "발밑에 NavMesh가 있는가"를
    // 묻는 값이라 넓어도 되지만, 그 결과를 그대로 워프에 쓰면 몸이 최대 그 거리만큼 옆으로 튄다.
    private const float k_landSnapMaxHorizontal = 1.5f;

    private void EndKnockback(Vector3 landing)
    {
        m_knockbackActive = false;
        m_knockbackVelocity = Vector3.zero;

        // 옆으로 멀면 그 자리에 떨군다 — NavMesh 밖에 남은 몸은 굳은 몸 정리가 끝낸다 (#913)
        Vector3 offset = landing - transform.position;
        offset.y = 0f;
        if (offset.sqrMagnitude > k_landSnapMaxHorizontal * k_landSnapMaxHorizontal)
            landing = transform.position;

        NavMeshAgent agent = m_owner.Agent;
        agent.enabled = true;
        agent.Warp(landing); // 에이전트를 NavMesh 위 착지점에 다시 붙인다

        // Warp가 실패했으면(착지점이 NavMesh 밖) 상태 전이를 시키지 않는다 — 상태 클래스들이 곧바로
        // 에이전트를 건드려 에러가 난다. 그 몸은 TickStuckOffNavMesh가 곧 행방불명 처리한다 (#913) —
        // 홈런으로 맵 밖까지 날아간 몸을 되돌리지 않는 것이 팀 결정이다.
        if (!agent.isOnNavMesh)
        {
            Debug.LogWarning(
                "NpcKnockback: 넉백 착지 지점을 NavMesh에 붙이지 못했다 — 행방불명 처리 대기: "
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
