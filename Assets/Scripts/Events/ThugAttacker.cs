using UnityEngine;
using UnityEngine.AI;
using Unity.Netcode;

/// <summary>
/// 괴한 — 괴한 습격(돌발 이벤트 · 현장)에서 스폰되는 위협 개체. (GDD 6-4/7-4, #106)
/// 저항형 NPC와 달리 <b>제압 대상이 아니다</b>(NpcSubdueInteractable가 없어 E로 반응하지 않는다) —
/// 가장 가까운 <b>행동 가능한</b> 현장 플레이어를 NavMesh로 추격하며 근접 시 주기적으로 HP를 깎는다
/// (<see cref="IDamageable.TakeDamage"/> 공통 경로). 표적이 다운되면 즉시 버리고 다른 플레이어로 옮겨간다 —
/// 다운은 이미 무력화이므로 계속 때릴 이유가 없고, 동료가 구조(#105)하러 올 여지를 남긴다.
/// 플레이어는 회피하거나, 다운되면 동료 구조(#105)에 의존한다.
/// 지속 시간 관리·디스폰은 <see cref="ThugAssaultEvent"/>가 담당하고, 이 컴포넌트는 추격·타격 행동만 맡는다.
///
/// 서버 권위 — 추격·타격 판단은 서버(또는 오프라인) 전용이고, 클라이언트는 NetworkTransform으로 동기화된
/// 위치만 표현한다. HP 감소는 PlayerData의 동기화 HP를 통해 전 클라에 반영된다. (#56 패턴, NpcController와 동일)
/// </summary>
[RequireComponent(typeof(NavMeshAgent))]
public class ThugAttacker : NetworkBehaviour
{
    [Header("추격")]
    [Tooltip("이 반경(m) 안에서 가장 가까운 현장 플레이어를 표적으로 삼는다 (다운된 플레이어 제외)")]
    [SerializeField] private float m_targetSearchRadius = 40f;
    [Tooltip("표적을 다시 고르는 주기(초) — 이동 목적지는 매 프레임 갱신되므로 추격 자체는 끊기지 않는다")]
    [SerializeField] private float m_retargetInterval = 0.25f;

    [Header("근접 타격")]
    [Tooltip("표적과 이 거리(m) 이내로 붙으면 타격한다")]
    [SerializeField] private float m_attackRange = 2f;
    [Tooltip("타격 주기(초)")]
    [SerializeField] private float m_attackInterval = 1.2f;
    [Tooltip("타격 1회당 플레이어 HP 감소량")]
    [SerializeField] private int m_attackDamage = 12;
    [Tooltip("스윙 시작→타격이 닿는 프레임까지의 시간(초). 이 만큼 뒤에 데미지가 들어가고, 그때 사거리를 재검증하므로 준비 동작이 곧 회피 창이 된다 (#220)")]
    [SerializeField] private float m_strikeOffsetSeconds = 0.45f;
    [Tooltip("타격이 닿는 정면 부채꼴의 전체 각도(도). 타격 순간 이 각도(정면 기준 ±절반) 안에 있어야 명중 — 준비 중 측면·뒤로 돌아가면 빗나간다 (#220)")]
    [SerializeField] private float m_attackConeAngle = 120f;

    [Header("소란 (#81 패닉 전파)")]
    [Tooltip("습격이 주변 시민을 패닉시키는 전파 반경(m)")]
    [SerializeField] private float m_disturbanceRadius = 8f;
    [Tooltip("소란 펄스 주기(초)")]
    [SerializeField] private float m_disturbancePulseInterval = 1f;

    private const float k_noPendingStrike = -1f;

    private NavMeshAgent m_agent;
    private PlayerData m_target;
    private float m_nextRetargetTime;
    private float m_nextAttackTime;
    private float m_nextPulseTime;
    // 스윙을 시작한 뒤 타격 프레임을 기다리는 예약. 데미지는 스윙 시작이 아니라 이 시점에 넣고
    // 그때 사거리·타깃 유효성을 재검증하므로 준비 중 벗어난 표적은 빗나간다. (#220)
    private float m_pendingStrikeTime = k_noPendingStrike;
    private PlayerData m_pendingStrikeTarget;

    /// <summary>타격을 한 번 휘두를 때 발행 — 전 피어에서 발생한다(서버는 로컬 발행 + ClientRpc 중계).
    /// 애니메이션 표현(<see cref="ThugAnimationDriver"/>)이 구독해 타격 모션을 재생한다. (#56 서버 권위 패턴)</summary>
    public event System.Action OnAttack;

    private void Awake()
    {
        m_agent = GetComponent<NavMeshAgent>();
    }

    public override void OnNetworkSpawn()
    {
        // 클라이언트의 위치는 NetworkTransform이 담당 — NavMeshAgent를 켜두면 동기화 위치와 싸운다 (NpcController와 동일)
        if (IsSpawned && !IsServer)
            m_agent.enabled = false;
    }

    private void Update()
    {
        // 추격·타격은 서버 전용 (오프라인 폴백 포함) — 클라이언트는 동기화된 위치만 표현한다 (#56)
        if (IsSpawned && !IsServer)
            return;

        // 예약된 타격은 표적이 바뀌거나 사라졌더라도 먼저 처리한다 — 스윙을 시작한 이상 타격 프레임은 온다
        ProcessPendingStrike();

        PlayerData target = AcquireTarget();
        if (target == null)
        {
            if (m_agent.enabled && m_agent.isOnNavMesh)
                m_agent.isStopped = true;
            return;
        }

        ChaseAndAttack(target);
        EmitDisturbancePulse();
    }

    // 표적 유지 — 유효한 표적은 계속 쫓고, 다운·소멸한 표적은 즉시 버린다. 새 표적 선정은 주기적으로만 한다.
    private PlayerData AcquireTarget()
    {
        // 다운되거나 사라진 표적은 주기를 기다리지 않고 바로 놓아준다 — 쓰러진 플레이어를 계속 쫓지 않게
        if (m_target != null && !m_target.IsTargetable)
            m_target = null;

        // 재탐색은 주기적으로 — 매 프레임 씬을 훑지 않고, 표적이 프레임마다 흔들리지도 않는다
        if (Time.time >= m_nextRetargetTime)
        {
            m_nextRetargetTime = Time.time + m_retargetInterval;
            m_target = SuddenEventUtil.FindNearestFieldPlayer(transform.position, m_targetSearchRadius);
        }

        return m_target;
    }

    private void ChaseAndAttack(PlayerData target)
    {
        Vector3 targetPosition = target.transform.position;

        if (m_agent.enabled && m_agent.isOnNavMesh)
        {
            m_agent.isStopped = false;
            m_agent.SetDestination(targetPosition);
        }

        float sqrDistance = (targetPosition - transform.position).sqrMagnitude;
        if (sqrDistance > m_attackRange * m_attackRange)
            return; // 아직 사거리 밖 — 계속 추격

        if (Time.time < m_nextAttackTime)
            return;
        m_nextAttackTime = Time.time + m_attackInterval;

        NotifyAttack(); // 스윙 모션은 명중 여부와 무관하게 재생 (전 피어)

        // 데미지는 타격 프레임까지 미룬다 — 스윙 준비 동작과 실제 HP 감소 순간을 일치시킨다 (#220)
        m_pendingStrikeTime = Time.time + m_strikeOffsetSeconds;
        m_pendingStrikeTarget = target;
    }

    // 예약된 타격을 타격 프레임에 실행한다 — 그 순간 사거리·타깃 유효성을 다시 확인하므로
    // 준비 동작 중 벗어나거나 다운된 표적은 자연히 빗나간다(회피 창). (#220)
    private void ProcessPendingStrike()
    {
        if (m_pendingStrikeTime < 0f || Time.time < m_pendingStrikeTime)
            return;

        PlayerData target = m_pendingStrikeTarget;
        m_pendingStrikeTime = k_noPendingStrike;
        m_pendingStrikeTarget = null;

        if (target == null || !target.IsTargetable)
            return; // 준비 중 표적이 다운되거나 사라짐

        Vector3 to = target.transform.position - transform.position;
        to.y = 0f;
        if (to.sqrMagnitude > m_attackRange * m_attackRange)
            return; // 준비 중 사거리를 벗어남 — 빗나감

        // 정면 부채꼴 밖(측면·뒤)이면 빗나감 — 추격 중 표적을 바라보므로 보통은 정면이지만, 준비 중 옆으로 파고들면 빗맞는다 (#220)
        Vector3 forward = transform.forward;
        forward.y = 0f;
        if (to.sqrMagnitude > 0.0001f && Vector3.Angle(forward, to) > m_attackConeAngle * 0.5f)
            return;

        ((IDamageable)target).TakeDamage(m_attackDamage, gameObject);
        Debug.Log($"괴한 습격 타격: {name} → {target.name} (-{m_attackDamage})");
    }

    // 습격 자체가 소란의 원천 — 주기적으로 주변 시민을 패닉시킨다 (#81, 저항 NPC의 EmitDisturbancePulse와 동일 패턴)
    private void EmitDisturbancePulse()
    {
        if (Time.time < m_nextPulseTime)
            return;
        m_nextPulseTime = Time.time + m_disturbancePulseInterval;
        NpcController.BroadcastDisturbance(transform.position, m_disturbanceRadius);
    }

    // 타격 스윙을 전 피어에 알린다 — 애니메이션 표현용. (SuddenEventManager.AnnounceEvent와 동일 패턴)
    private void NotifyAttack()
    {
        OnAttack?.Invoke(); // 서버·오프라인 로컬 발행
        if (IsSpawned && IsServer)
            PlayAttackClientRpc();
    }

    [ClientRpc]
    private void PlayAttackClientRpc()
    {
        // 서버(호스트)는 위에서 이미 발행했으므로 원격 클라에서만 중계
        if (IsServer)
            return;
        OnAttack?.Invoke();
    }

}
