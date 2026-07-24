using System;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// 차저 — 괴한 습격에서 스폰되는 돌진형 위협 개체. (#291, 구 ThugAttacker 재작성)
/// 제압 대상이 아니며(NpcSubdueInteractable 없음), 표적에게 접근 → 윈드업(회피 창) → 직선 돌진 →
/// 명중 시 데미지+넉백 / 빗맞음·벽 충돌 시 경직(반격 창) 사이클을 돈다. 포획은 없다.
/// 지속 시간·디스폰은 <see cref="ThugAssaultEvent"/>가 담당한다.
///
/// 서버 권위 — 사이클·타격은 서버(또는 오프라인) 전용, 클라이언트는 NetworkTransform 위치만 표현한다. (#56)
/// </summary>
[RequireComponent(typeof(NavMeshAgent))]
public class ThugAttacker : NetworkBehaviour
{
    private enum Phase
    {
        Approach,
        Windup,
        Charge,
        Recover,
    }

    [Header("튜닝 (#291)")]
    [SerializeField]
    private ThugChargerConfig m_config;

    private NavMeshAgent m_agent;
    private PlayerData m_target;
    private Phase m_phase;
    private float m_phaseEndTime; // Windup/Recover 종료 시각
    private float m_nextRetargetTime;
    private float m_nextPulseTime;

    // 돌진 상태 — 서버(또는 오프라인)에서만 의미
    private Vector3 m_chargeDir;
    private Vector3 m_chargeStart;
    private float m_chargeElapsed;

    private bool IsAuthority => !IsSpawned || IsServer;

    // m_phase는 서버에서만 갱신되므로, 클라이언트가 윈드업(숨고르기) 모션을 내려면 표현용 동기화가 필요하다. (#291)
    private readonly NetworkVariable<bool> m_netIsWindingUp = new NetworkVariable<bool>();

    /// <summary>돌진 준비(윈드업) 중인가 — 표현 계층(ThugAnimationDriver)이 숨고르는 모션을 낼지 판단에 읽는다. (#291)</summary>
    public bool IsWindingUp =>
        IsSpawned && !IsServer ? m_netIsWindingUp.Value : m_phase == Phase.Windup;

    private void Awake()
    {
        m_agent = GetComponent<NavMeshAgent>();
        m_phase = Phase.Approach;
    }

    public override void OnNetworkSpawn()
    {
        // 클라이언트의 위치는 NetworkTransform이 담당 — NavMeshAgent를 켜두면 동기화 위치와 싸운다
        if (IsSpawned && !IsServer)
            m_agent.enabled = false;
    }

    private void Update()
    {
        if (!IsAuthority)
            return;
        if (m_config == null)
            return;

        switch (m_phase)
        {
            case Phase.Approach:
                TickApproach();
                break;
            case Phase.Windup:
                TickWindup();
                break;
            case Phase.Charge:
                TickCharge();
                break;
            case Phase.Recover:
                TickRecover();
                break;
        }

        EmitDisturbancePulse();
    }

    // 접근 — 표적에 붙는다. 돌진 사거리 안에 들면 윈드업으로.
    private void TickApproach()
    {
        PlayerData target = AcquireTarget();
        if (target == null)
        {
            if (m_agent.enabled && m_agent.isOnNavMesh)
                m_agent.isStopped = true;
            return;
        }

        if (m_agent.enabled && m_agent.isOnNavMesh)
        {
            m_agent.isStopped = false;
            m_agent.speed = m_config.ApproachSpeed;
            m_agent.SetDestination(target.transform.position);
        }

        float sqr = (target.transform.position - transform.position).sqrMagnitude;
        if (sqr <= m_config.ChargeStartRange * m_config.ChargeStartRange)
            EnterWindup(target);
    }

    private void EnterWindup(PlayerData target)
    {
        m_target = target;
        m_phase = Phase.Windup;
        m_phaseEndTime = Time.time + m_config.WindupSeconds;
        if (IsSpawned && IsServer)
            m_netIsWindingUp.Value = true;
        if (m_agent.enabled && m_agent.isOnNavMesh)
        {
            m_agent.isStopped = true;
            m_agent.ResetPath();
        }
    }

    // 윈드업 — 제자리에서 표적을 바라본다(회피 창). 끝나면 방향을 고정해 돌진 개시.
    private void TickWindup()
    {
        if (m_target != null && m_target.IsTargetable)
            FaceTowards(m_target.transform.position);

        if (Time.time < m_phaseEndTime)
            return;

        // 돌진 방향 고정 — 이 시점의 표적 방향으로 커밋(이후 표적이 움직여도 방향은 안 바뀐다 = 회피 가능)
        Vector3 aim =
            (
                m_target != null
                    ? m_target.transform.position
                    : transform.position + transform.forward
            ) - transform.position;
        aim.y = 0f;
        m_chargeDir = aim.sqrMagnitude > 0.0001f ? aim.normalized : transform.forward;
        m_chargeStart = transform.position;
        m_chargeElapsed = 0f;
        m_phase = Phase.Charge;
        if (IsSpawned && IsServer)
            m_netIsWindingUp.Value = false;

        // 돌진 중에는 에이전트를 끄고 transform으로 직접 민다(넉백 비행과 동일 사고) — 켜두면 NavMesh가 직선을 꺾는다
        if (m_agent.enabled)
        {
            if (m_agent.isOnNavMesh)
                m_agent.ResetPath();
            m_agent.enabled = false;
        }
    }

    // 돌진 — 고정 방향으로 직선 이동. 플레이어에 스치면 명중, 벽/최대거리/시간이면 경직.
    private void TickCharge()
    {
        m_chargeElapsed += Time.deltaTime;
        float step = m_config.ChargeSpeed * Time.deltaTime;

        // 벽 스윕 — 넉백(#232)과 동일하게 몸통 굵기로 훑어 벽을 지나치지 않는다
        if (SweepHitsObstacle(m_chargeDir, step))
        {
            EnterRecover(m_config.RecoverSeconds);
            return;
        }

        transform.position += m_chargeDir * step;
        FaceTowards(transform.position + m_chargeDir);

        // 명중 판정 — 사거리(HitRadius) 안 행동 가능 플레이어
        PlayerData hit = SuddenEventUtil.FindNearestFieldPlayer(
            transform.position,
            m_config.HitRadius
        );
        if (hit != null)
        {
            HitPlayer(hit);
            EnterRecover(m_config.HitCooldownSeconds);
            return;
        }

        // 최대 거리/시간 소진 → 경직
        float traveled = (transform.position - m_chargeStart).magnitude;
        if (traveled >= m_config.ChargeMaxDistance || m_chargeElapsed >= m_config.ChargeMaxSeconds)
            EnterRecover(m_config.RecoverSeconds);
    }

    private void EnterRecover(float seconds)
    {
        m_phase = Phase.Recover;
        m_phaseEndTime = Time.time + seconds;

        // 에이전트를 되살려 NavMesh 위로 복귀(Warp) — 안 하면 이후 이동이 깨진다 (넉백 착지와 동일)
        if (!m_agent.enabled)
            m_agent.enabled = true;
        if (NavMesh.SamplePosition(transform.position, out NavMeshHit navHit, 2f, NavMesh.AllAreas))
            m_agent.Warp(navHit.position);
        if (m_agent.isOnNavMesh)
        {
            m_agent.isStopped = true;
            m_agent.ResetPath();
        }
    }

    // 경직 — 반격 창. 끝나면 접근으로 복귀.
    private void TickRecover()
    {
        if (Time.time >= m_phaseEndTime)
            m_phase = Phase.Approach;
    }

    // 표적 유지 — 유효 표적은 계속, 다운·소멸 표적은 버린다. 재선정은 주기적으로.
    private PlayerData AcquireTarget()
    {
        if (m_target != null && !m_target.IsTargetable)
            m_target = null;

        if (Time.time >= m_nextRetargetTime)
        {
            m_nextRetargetTime = Time.time + m_config.RetargetInterval;
            m_target = SuddenEventUtil.FindNearestFieldPlayer(
                transform.position,
                m_config.TargetSearchRadius
            );
        }
        return m_target;
    }

    // 명중 — 데미지(서버 권위 HP)는 직접, 넉백은 오너 피어가 적용하도록 ClientRpc로(비오너 self-무시). (BombExplosionView 패턴)
    private void HitPlayer(PlayerData player)
    {
        // 공격 스윙 모션 없음 — 달려가 부딪히는 돌진 그 자체가 타격이다 (#291 피드백).
        // 달리는 모션은 ThugAnimationDriver가 이동 속도로 이미 내준다.
        ((IDamageable)player).TakeDamage(m_config.HitDamage, gameObject);

        Vector3 kb = player.transform.position - transform.position;
        kb.y = 0f;
        kb = (kb.sqrMagnitude > 0.0001f ? kb.normalized : m_chargeDir) * m_config.KnockbackSpeed;

        if (IsSpawned && IsServer)
        {
            NetworkObject netObj = player.GetComponent<NetworkObject>();
            if (netObj != null)
                ApplyPlayerKnockbackClientRpc(netObj.NetworkObjectId, kb);
        }
        else if (!IsSpawned && player.TryGetComponent(out PlayerMovement pm))
        {
            pm.AddKnockback(kb); // 오프라인 — 오너 개념이 없으므로 직접 적용
        }

        Debug.Log($"차저 돌진 명중: {name} → {player.name} (-{m_config.HitDamage})");
    }

    [ClientRpc]
    private void ApplyPlayerKnockbackClientRpc(ulong targetPlayerObjectId, Vector3 velocity)
    {
        // 각 피어가 자기 오너 플레이어에만 적용 — AddKnockback이 비오너를 스스로 무시한다 (BombExplosionView와 동일)
        if (NetworkManager.Singleton == null)
            return;
        if (
            !NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(
                targetPlayerObjectId,
                out NetworkObject obj
            )
        )
            return;
        if (obj.TryGetComponent(out PlayerMovement pm))
            pm.AddKnockback(velocity);
    }

    private void FaceTowards(Vector3 worldPoint)
    {
        Vector3 dir = worldPoint - transform.position;
        dir.y = 0f;
        if (dir.sqrMagnitude > 0.0001f)
            transform.rotation = Quaternion.LookRotation(dir);
    }

    // 벽 스윕 히트 버퍼 — 서버 전용 틱이라 공유해도 안전하다 (프레임마다의 할당 방지).
    // 밀집 군중 사이 돌진에서 캐릭터 히트가 버퍼를 채우면 그 뒤의 진짜 벽이 조용히 누락될 수 있어
    // 넉넉히 잡는다 — 포화 시 경고를 남겨 조용한 손실을 드러낸다 (PR #338 리뷰).
    private static readonly RaycastHit[] s_sweepBuffer = new RaycastHit[16];

    // 이번 프레임 이동 구간에 벽이 있는지 — 넉백(#232)의 SweepHitsObstacle와 동일 기법.
    // 캐릭터(플레이어·NPC)는 벽으로 치지 않는다(#313): 플레이어 몸통(CharacterController)이 환경과
    // 같은 Default 레이어라 마스크만으로는 걸러지지 않는데, 표적을 벽으로 보면 스윕이 명중 판정보다
    // 먼저 도는 구조상 돌진이 명중 직전에 끊긴다. 군중 NPC도 같은 이유로 뚫고 지나간다.
    private bool SweepHitsObstacle(Vector3 direction, float distance)
    {
        float radius = m_agent.radius;
        Vector3 origin = transform.position + Vector3.up * Mathf.Max(radius, m_agent.height * 0.5f);
        int mask = m_config.ObstacleMask & ~(1 << gameObject.layer);
        int count = Physics.SphereCastNonAlloc(
            origin,
            radius,
            direction,
            s_sweepBuffer,
            distance,
            mask,
            QueryTriggerInteraction.Ignore
        );

        // 버퍼 포화 = 반환되지 못한 히트가 있을 수 있다 — 그중 벽이 누락되면 돌진이 벽을 뚫는다.
        // 짧은 스텝 거리 특성상 정상 플레이에선 나오기 어려운 밀도라, 나오면 버퍼를 더 키울 신호다.
        if (count == s_sweepBuffer.Length)
            Debug.LogWarning($"ThugAttacker: 벽 스윕 버퍼 포화({count}) — 히트 누락 가능, 버퍼 확대 검토", this);

        for (int i = 0; i < count; i++)
        {
            Collider hit = s_sweepBuffer[i].collider;
            if (hit == null)
                continue;
            if (hit.GetComponentInParent<PlayerData>() != null)
                continue; // 표적/다른 플레이어 — 벽이 아니라 명중 후보다 (명중은 HitRadius 판정이 처리)
            if (hit.GetComponentInParent<NpcController>() != null)
                continue; // 군중 NPC — 사이를 뚫고 돌진한다

            return true; // 캐릭터가 아닌 무언가 = 벽/환경
        }

        return false;
    }

    private void EmitDisturbancePulse()
    {
        if (Time.time < m_nextPulseTime)
            return;
        m_nextPulseTime = Time.time + m_config.DisturbancePulseInterval;
        NpcController.BroadcastDisturbance(transform.position, m_config.DisturbanceRadius);
    }
}
