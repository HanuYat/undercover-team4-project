using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 폭발 적용 — 반경 내 플레이어·NPC에 피해를 넣고 죽은 몸을 날린다. (#768 분할)
///
/// <b>세기는 여기서 정하지 않는다</b> — 감쇠식과 노브는 <see cref="BombBlastProfile"/>이 들고,
/// 이 컴포넌트는 누가 반경에 들었나를 찾아 적용할 뿐이다. 폭발 시점과 <see cref="BombState"/>
/// 전이도 <see cref="BombDevice"/>가 쥔다.
///
/// <b><see cref="NetworkBehaviour"/>인 이유는 <see cref="BlastDeathsClientRpc"/> 하나다</b> —
/// 한 <c>NetworkObject</c>에 여러 개는 정상이고, NetworkVariable은 상태 주인 쪽에 남는다.
/// </summary>
public class BombBlast : NetworkBehaviour
{
    [Header("폭발 세기 (인스펙터 조절)")]
    [SerializeField]
    private BombBlastProfile m_profile = new BombBlastProfile();

    private readonly List<Transform> m_blastBuffer = new List<Transform>();

    // 이 폭발로 죽은 플레이어 — 래그돌 임펄스 대상(#506). 서버·오프라인에서만 채운다.
    private readonly List<NetworkObject> m_deathBuffer = new List<NetworkObject>();

    // 폭발 대상 수집용 공유 버퍼 — 서버(또는 오프라인)에서만 쓰므로 정적으로 공유해도 안전하다.
    // 사람 하나가 래그돌 뼈 콜라이더 여러 개로 잡혀 64칸은 대여섯 명이면 포화된다 (#768).
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
            IDamageable damageable = target.GetComponent<IDamageable>();
            if (damageable != null)
                damageable.TakeDamage(EvaluateDamage(target.position), gameObject);

            // CollectFieldPlayers는 행동 가능한(HP>0) 플레이어만 담으므로, 지금 0이면 이 폭발로 죽은 것이다.
            if (target.TryGetComponent(out PlayerHealth health)
                && health.CurrentHp == 0
                && target.TryGetComponent(out NetworkObject victim))
                m_deathBuffer.Add(victim);

            // 휘말린 사람은 밧줄에서 손을 뗀다 (#559) — 넉백이 오너 로컬이라 반경을 아는 것은 이 자리뿐이다.
            // 두 번 불려도 무해하다(끌고 있지 않으면 그대로 돌아간다).
            PlayerEscorter escorter = target.GetComponent<PlayerEscorter>();
            if (escorter != null)
                escorter.ReleaseAllDrags();
        }

        NotifyBlastDeaths();

        // 반경 내 NPC 피해·넉백 — 서버 권위. 플레이어와 달리 RPC가 없다: 시체 자세는
        // RagdollPoseStreamer가 서버에서만 굴려 원격에 흘린다(PoseAuthority.Server).
        ServerBlastNpcs();

        // 진압봉 즉발도 여기로 오므로 main의 "시간 초과" 문구는 쓰지 않는다 (#399 추격 폭탄).
        Debug.Log(
            $"[폭탄] 폭발 (반경 {m_profile.Radius}m, 폭심 피해 {m_profile.PeakDamage},"
                + $" 가장자리 비율 {m_profile.DamageEdgeFalloff}, 사망 {m_deathBuffer.Count}명)"
        );
    }

    // ---- 폭발 사망자 → 래그돌 임펄스 (#506) ----

    /// <summary>
    /// 이 폭발로 죽은 사람과 <b>그에게 줄 임펄스</b>를 전 피어에 알린다.
    ///
    /// <b>임펄스를 서버가 계산해 함께 보낸다.</b> 각 피어가 직접 계산하면 감쇠식이 쓰는
    /// <c>victim.transform.position</c>이 NetworkTransform 보간값이라 <b>세기와 방향이 갈려</b>
    /// 궤적이 처음부터 벌어진다 — 래그돌은 각 피어가 로컬로 굴리므로 입력이 같아야 한다 (#506 §10-3).
    /// <b>RPC가 필요한 이유는 임펄스 하나다</b> — 사망 자체는 <see cref="PlayerRagdoll"/>이 동기화값
    /// 폴링으로 잡지만, "누가 이 폭발로 죽었나"는 피해를 계산한 서버만 안다. 호스트 중복 발행은
    /// <see cref="WrongCutClientRpc"/> 관례로 막는다(ClientRpc 쪽이 <c>IsServer</c>면 물러난다).
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

    // 래그돌 진입은 <b>멱등이다</b> — 사망 폴링과 이 RPC는 다른 오브젝트에서 와 순서를 맞출 수 없어,
    // 어느 쪽이 먼저 와도 결과가 같게 만든다 (#506 §3-1).
    private void ApplyBlastRagdoll(NetworkObject victim, Vector3 impulse)
    {
        if (victim == null || !victim.TryGetComponent(out PlayerRagdoll ragdoll))
            return;

        ragdoll.EnterRagdoll(impulse);
    }

    // 반경 내 NPC에 피해를 주고, 죽으면 시체를 날리고 살아 있으면 넉백만 건다.
    // 한 사람이 콜라이더 여러 개로 잡히므로 집합으로 한 번만 처리한다.
    private void ServerBlastNpcs()
    {
        int hitCount = Physics.OverlapSphereNonAlloc(transform.position, m_profile.Radius, s_blastColliders);

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
                // 원래 있던 시체는 건드리지 않는다 — EnterRagdoll이 정착 시체를 거부하지 않아
                // (뼈가 키네마틱이라 경고만 뜨고 몸이 깨어난다) 이 wasAlive가 유일한 방어다
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
