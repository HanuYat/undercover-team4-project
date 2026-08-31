using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 폭발 적용 — 반경 내 플레이어·NPC에 피해를 넣고 죽은 몸을 날린다.
/// 세기(감쇠식·노브)는 <see cref="BombBlastProfile"/>이 갖고, 폭발 시점·상태 전이는 <see cref="BombDevice"/>가 쥔다.
/// NetworkBehaviour인 이유는 <see cref="BlastDeathsClientRpc"/> 하나다.
/// </summary>
public class BombBlast : NetworkBehaviour
{
    [Header("폭발 세기 (인스펙터 조절)")]
    [SerializeField]
    private BombBlastProfile m_profile = new BombBlastProfile();

    [Header("가림 판정")]
    [SerializeField]
    private LayerMask m_blockMask = 1; // Default

    // 가림 검사 높이(m) — 피벗끼리 이으면 지면을 스치는 선이 되어 연석·경사·자기 콜라이더 바닥면에
    // 걸린다 (#947). 폭탄은 자기 박스 중심, 사람은 가슴 높이에서 잇는다.
    private const float k_occlusionOriginHeight = 0.4f;
    private const float k_occlusionTargetHeight = 1f;

    // 가림 검사 구 반지름(m) — 얇은 선은 창살·소품 틈으로 새어 '가려짐'을 오판한다
    // (SuddenEventUtil.k_visProbeRadius와 같은 이유).
    private const float k_occlusionProbeRadius = 0.2f;

    private readonly List<Transform> m_blastBuffer = new List<Transform>();

    // 이 폭발로 죽은 플레이어 — 래그돌 임펄스 대상(#506). 서버·오프라인에서만 채운다.
    private readonly List<NetworkObject> m_deathBuffer = new List<NetworkObject>();

    // 래그돌 뼈 콜라이더 다중 매칭 때문에 64칸은 대여섯 명이면 포화된다 (#768).
    private static readonly Collider[] s_blastColliders = new Collider[256];

    // 한 폭발에서 이미 처리한 NPC — 위 버퍼가 같은 사람을 여러 번 담기 때문이다 (#768).
    private static readonly HashSet<NpcController> s_blastNpcs = new HashSet<NpcController>();

    /// <summary>피해·넉백이 닿는 반경(m).</summary>
    public float ExplosionRadius => m_profile.Radius;

    /// <summary>넉백 세기(m/s).</summary>
    public float KnockbackForce => m_profile.KnockbackForce;

    /// <summary>
    /// 폭심에서 <paramref name="targetPosition"/>이 받는 넉백 속도(m/s) — 반경 밖이면 <see cref="Vector3.zero"/>.
    /// 세기의 단일 지점은 <see cref="BombBlastProfile.EvaluateKnockback"/>이고 이것은 폭심을 채워 주는 래퍼다.
    /// </summary>
    public Vector3 EvaluateKnockback(Vector3 targetPosition)
    {
        return m_profile.EvaluateKnockback(targetPosition - transform.position, transform.forward);
    }

    /// <summary>폭심에서 <paramref name="targetPosition"/>이 받는 피해량 — 반경 밖이면 0.</summary>
    public int EvaluateDamage(Vector3 targetPosition)
    {
        return m_profile.EvaluateDamage(targetPosition - transform.position);
    }

    private Vector3 EvaluateRagdollImpulse(Vector3 targetPosition)
    {
        return m_profile.EvaluateRagdollImpulse(targetPosition - transform.position, transform.forward);
    }

    /// <summary>
    /// 대상이 <b>환경에</b> 가려졌는가 — BombExplosionView도 이걸로 넉백 연출을 가려 서버 피해 판정과 기준을 맞춘다.
    /// 대상 자신도 사람이라 <see cref="AimOcclusion.IsEnvironmentBlocked"/>가 알아서 뺀다 — 루트를 넘길 필요가 없다.
    /// </summary>
    public bool IsOccluded(Vector3 targetPosition) =>
        AimOcclusion.IsEnvironmentBlocked(
            transform.position + Vector3.up * k_occlusionOriginHeight,
            targetPosition + Vector3.up * k_occlusionTargetHeight,
            m_blockMask,
            k_occlusionProbeRadius,
            transform
        );

    /// <summary>
    /// 터진다 — 서버(또는 오프라인) 전용. 호출 시점과 중복 방지는 <see cref="BombDevice"/>가 쥔다.
    /// </summary>
    public void ServerExplode()
    {
        Vector3 origin = transform.position;

        // 반경 내 행동 가능한 플레이어에게 거리 감쇠 피해.
        m_deathBuffer.Clear();
        SuddenEventUtil.CollectFieldPlayers(origin, m_profile.Radius, m_blastBuffer);
        for (int i = 0; i < m_blastBuffer.Count; i++)
        {
            Transform target = m_blastBuffer[i];
            if (IsOccluded(target.position))
                continue;

            // <b>유예를 주지 않는 피해</b>다 — 폭심에서 HP가 0이 되면 다운 60초를 거치지 않고 곧바로
            // 기능 정지다 (GDD 6-4 "폭심 즉사", PlayerHealth.TakeLethalDamage). 가장자리에서 HP가
            // 남으면 그대로 살아서 넉백만 받는다 — <b>거리 감쇠는 그대로다.</b>
            //
            // CollectFieldPlayers가 PlayerHealth로 대상을 모으므로 이 조회는 형식상 가드다. 아래
            // 사망자 수집이 같은 참조를 쓴다 — 예전에는 IDamageable로 때리고 두 줄 뒤에 PlayerHealth를
            // 다시 뽑았다.
            bool hasHealth = target.TryGetComponent(out PlayerHealth health);
            if (hasHealth)
                health.TakeLethalDamage(EvaluateDamage(target.position), gameObject);

            // CollectFieldPlayers는 행동 가능한(HP>0) 플레이어만 담으므로, 지금 0이면 이 폭발로 죽은 것이다.
            // TakeLethalDamage를 지난 뒤의 HP 0은 <b>곧 Die다</b> — 이 진입점에는 Down으로 갈 경로가 없다.
            if (hasHealth
                && health.CurrentHp == 0
                && target.TryGetComponent(out NetworkObject victim))
                m_deathBuffer.Add(victim);

            // 휘말린 사람은 밧줄에서 손을 뗀다(#559) — 두 번 불려도 무해하다.
            PlayerEscorter escorter = target.GetComponent<PlayerEscorter>();
            if (escorter != null)
                escorter.ReleaseAllDrags();
        }

        NotifyBlastDeaths();

        // NPC는 시체 자세를 RagdollPoseStreamer가 서버에서만 굴려 흘리므로 RPC가 필요 없다.
        ServerBlastNpcs();

        // 진압봉 즉발도 여기로 오므로 main의 "시간 초과" 문구는 쓰지 않는다 (#399 추격 폭탄).
        Debug.Log(
            $"[폭탄] 폭발 (반경 {m_profile.Radius}m, 폭심 피해 {m_profile.PeakDamage},"
                + $" 가장자리 비율 {m_profile.DamageEdgeFalloff}, 사망 {m_deathBuffer.Count}명)"
        );
    }

    // ---- 폭발 사망자 → 래그돌 임펄스 (#506) ----

    /// <summary>
    /// 이 폭발로 죽은 사람과 임펄스를 전 피어에 알린다.
    /// 임펄스는 서버가 계산해 함께 보낸다 — 각 피어가 <c>victim.transform.position</c>(보간값)으로
    /// 직접 계산하면 세기·방향이 갈려 궤적이 벌어진다. 호스트 중복 발행은 <c>IsServer</c> 가드로 막는다.
    /// </summary>
    private void NotifyBlastDeaths()
    {
        if (m_deathBuffer.Count == 0)
            return;

        // 서버(또는 오프라인)가 임펄스를 확정한다 — 이 값이 전 피어의 공통 입력이 된다.
        Vector3[] impulses = new Vector3[m_deathBuffer.Count];
        for (int i = 0; i < impulses.Length; i++)
            impulses[i] = EvaluateRagdollImpulse(m_deathBuffer[i].transform.position);

        for (int i = 0; i < m_deathBuffer.Count; i++)
            ApplyBlastRagdoll(m_deathBuffer[i], impulses[i]); // 서버·오프라인 로컬 발행

        if (!IsSpawned || !IsServer)
            return;

        NetworkObjectReference[] victims = new NetworkObjectReference[m_deathBuffer.Count];
        for (int i = 0; i < victims.Length; i++)
            victims[i] = m_deathBuffer[i];

        BlastDeathsClientRpc(victims, impulses);
    }

    [ClientRpc]
    private void BlastDeathsClientRpc(NetworkObjectReference[] victims, Vector3[] impulses)
    {
        if (IsServer)
            return; // 호스트는 위에서 이미 발행

        // 길이는 서버가 맞춰 보내지만, 직렬화 경계를 믿지 않고 짧은 쪽까지만 돈다.
        int count = Mathf.Min(victims.Length, impulses.Length);
        for (int i = 0; i < count; i++)
        {
            if (victims[i].TryGet(out NetworkObject victim))
                ApplyBlastRagdoll(victim, impulses[i]);
        }
    }

    // 래그돌 진입은 멱등이다 — 사망 폴링과 이 RPC 중 어느 쪽이 먼저 와도 결과가 같다.
    private void ApplyBlastRagdoll(NetworkObject victim, Vector3 impulse)
    {
        if (victim == null || !victim.TryGetComponent(out PlayerRagdoll ragdoll))
            return;

        ragdoll.EnterRagdoll(impulse);
    }

    // 콜라이더 여러 개로 잡히는 NPC를 집합으로 한 번만 처리해 피해·넉백을 건다.
    private void ServerBlastNpcs()
    {
        Vector3 origin = transform.position;
        int hitCount = Physics.OverlapSphereNonAlloc(origin, m_profile.Radius, s_blastColliders);

        // 포화는 조용히 틀린다 — 넘친 대상은 아무 일도 겪지 않는다
        if (hitCount == s_blastColliders.Length)
            Debug.LogWarning($"[폭탄] 대상 버퍼({s_blastColliders.Length}) 포화 — 일부 NPC가 누락됐을 수 있다", this);

        s_blastNpcs.Clear();
        for (int i = 0; i < hitCount; i++)
        {
            NpcController npc = s_blastColliders[i].GetComponentInParent<NpcController>();
            if (npc == null || !s_blastNpcs.Add(npc))
                continue;

            Vector3 position = npc.transform.position;
            if (IsOccluded(position))
                continue;

            int damage = EvaluateDamage(position);
            if (damage <= 0)
                continue;

            // 피해 전에 재 둔다 — 아래에서 "이 폭발로 죽었나"를 가리는 근거다
            bool wasAlive = !npc.Death.IsDead;

            // 환경 피해로 넣는다 — TakeDamage의 게이트는 연행 중을 막는다 (차량과 같은 이유, #690)
            npc.Health.TakeEnvironmentalDamage(damage, gameObject);

            // 시체에는 넉백이 막혀 있어(#634) 임펄스로만 움직인다
            if (npc.Death.IsDead)
            {
                // wasAlive 가드로 원래 있던 시체는 건드리지 않는다 (EnterRagdoll은 정착 시체를 거부하지 않는다)
                if (wasAlive && npc.Ragdoll != null)
                    npc.Ragdoll.EnterRagdoll(EvaluateRagdollImpulse(position));
            }
            else
            {
                npc.Knockback.ServerApplyKnockback(EvaluateKnockback(position));
            }
        }
    }
}
