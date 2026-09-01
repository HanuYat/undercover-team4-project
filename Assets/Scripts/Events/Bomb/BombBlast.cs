using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 폭발 적용 — 반경 내 플레이어·NPC에 피해를 넣고 <b>죽었든 살았든 래그돌로 날린다.</b>
/// 세기(감쇠식·노브)는 <see cref="BombBlastProfile"/>이 갖고, 폭발 시점·상태 전이는 <see cref="BombDevice"/>가 쥔다.
///
/// <b>산 몸을 날리는 문은 홈런 진압봉(#815)이 낸 것을 그대로 쓴다</b> — 플레이어는
/// <see cref="IncapacitationCause.Launched"/>, NPC는 <see cref="NpcKnockback.ServerLaunchRagdoll"/>.
/// 예전의 캡슐 밀림(<c>PlayerMovement.AddKnockback</c>)·뻣뻣한 포물선(<c>ServerApplyKnockback</c>)은
/// 폭발 경로에서 사라졌다 — 같은 폭발인데 죽은 몸과 산 몸이 다르게 날아가는 그림을 없애기 위해서다.
/// NetworkBehaviour인 이유는 RPC 둘(<see cref="BlastDeathsClientRpc"/>·<see cref="LaunchSurvivorRpc"/>)이다.
/// </summary>
public class BombBlast : NetworkBehaviour
{
    [Header("폭발 세기 (인스펙터 조절)")]
    [SerializeField]
    private BombBlastProfile m_profile = new BombBlastProfile();

    [Header("가림 판정")]
    [SerializeField]
    private LayerMask m_blockMask = 1; // Default

    [Header("생존자 발사 (#815 문 재사용)")]
    [Tooltip("살아남은 NPC가 착지 후까지 누워 있는 시간(초). <b>비행 시간보다 넉넉히 길게</b> — " +
             "짧으면 아직 공중인 몸에 기상 모션이 나간다 (HomeRunBaton.m_stunSeconds와 같은 노브)")]
    [SerializeField]
    private float m_launchStunSeconds = 6f;

    [Tooltip("살아남은 플레이어의 비행 상태 서버 최대시간(초) — 오너의 정착 통보가 안 오는 경우 " +
             "(연결 끊김 등)의 안전장치. 정상 정착(1~3초)보다 넉넉히 잡을 것")]
    [SerializeField]
    private float m_launchMaxSeconds = 6f;

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

    /// <summary>피해·발사가 닿는 반경(m).</summary>
    public float ExplosionRadius => m_profile.Radius;

    /// <summary>폭심에서 <paramref name="targetPosition"/>이 받는 피해량 — 반경 밖이면 0.</summary>
    public int EvaluateDamage(Vector3 targetPosition)
    {
        return m_profile.EvaluateDamage(targetPosition - transform.position);
    }

    // 래그돌 뼈에 줄 초기 속도 — 사망자·생존자·시민이 전부 이 하나를 지난다.
    private Vector3 EvaluateRagdollImpulse(Vector3 targetPosition)
    {
        return m_profile.EvaluateRagdollImpulse(targetPosition - transform.position, transform.forward);
    }

    /// <summary>
    /// 대상이 <b>환경에</b> 가려졌는가 — 가려졌으면 피해도 임펄스도 통째로 없다.
    /// 대상 자신도 사람이라 <see cref="AimOcclusion.IsEnvironmentBlocked"/>가 알아서 뺀다 — 루트를 넘길 필요가 없다.
    /// </summary>
    private bool IsOccluded(Vector3 targetPosition) =>
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

        // 반경 내 <b>피해가 닿는</b> 플레이어에게 거리 감쇠 피해 — 표적 선정이 아니라 피해 판정이라
        // 비행 중인 사람도 담는다(CollectDamageablePlayers). 연쇄 폭발에서 첫 폭발에 날아간 사람이
        // 두 번째를 안 맞던 것이 이 줄이었다.
        m_deathBuffer.Clear();
        SuddenEventUtil.CollectDamageablePlayers(origin, m_profile.Radius, m_blastBuffer);
        for (int i = 0; i < m_blastBuffer.Count; i++)
        {
            Transform target = m_blastBuffer[i];
            if (IsOccluded(target.position))
                continue;

            // <b>유예를 주지 않는 피해</b>다 — 폭심에서 HP가 0이 되면 다운 60초를 거치지 않고 곧바로
            // 기능 정지다 (GDD 6-4 "폭심 즉사", PlayerHealth.TakeLethalDamage). 가장자리에서 HP가
            // 남으면 살아서 날아갔다 일어난다 — <b>거리 감쇠는 그대로다.</b>
            //
            // CollectFieldPlayers가 PlayerHealth로 대상을 모으므로 이 조회는 형식상 가드다. 아래
            // 사망자 수집이 같은 참조를 쓴다 — 예전에는 IDamageable로 때리고 두 줄 뒤에 PlayerHealth를
            // 다시 뽑았다.
            bool hasHealth = target.TryGetComponent(out PlayerHealth health);
            if (hasHealth)
                health.TakeLethalDamage(EvaluateDamage(target.position), gameObject);

            // 휘말린 사람은 밧줄에서 손을 뗀다(#559) — 두 번 불려도 무해하다.
            // <b>날리기 전이다</b> — 몸이 물리로 넘어간 뒤에 줄을 끊을 이유가 없다.
            PlayerEscorter escorter = target.GetComponent<PlayerEscorter>();
            if (escorter != null)
                escorter.ReleaseAllDrags();

            // 수집기가 HP>0인 사람만 담으므로, 지금 0이면 이 폭발로 죽은 것이다.
            // TakeLethalDamage를 지난 뒤의 HP 0은 <b>곧 Die다</b> — 이 진입점에는 Down으로 갈 경로가 없다.
            // 살아남았으면 <b>같은 임펄스로 산 채로</b> 날린다 — 아래 ServerLaunchSurvivor 주석 참고.
            if (!hasHealth)
                continue;

            if (health.CurrentHp == 0)
            {
                if (target.TryGetComponent(out NetworkObject victim))
                    m_deathBuffer.Add(victim);
            }
            else
            {
                ServerLaunchSurvivor(health, EvaluateRagdollImpulse(target.position));
            }
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
    ///
    /// ⚠ <b>죽기 직전의 오너에게는 임펄스를 주지 않는다.</b> 그 피어만 뼈가 아직 동적이라 임펄스가
    /// 실제로 들어가 자기 궤적을 만들고, 곧 소유권을 잃어 얼어붙었다가 서버 자세로 튄다. 서 있다
    /// 죽으면 양쪽이 정지에서 같은 임펄스를 받아 궤적이 겹쳐 안 보이지만, <b>비행 중에 맞으면</b>
    /// 클라에만 남은 비행 속도 때문에 갈라진다. 같은 처리가
    /// <c>TrafficVehicle.ServerNotifyDeathRagdoll</c>에도 있다 — 근거는 docs/506-explosion-ragdoll.md §16.
    /// </summary>
    private void NotifyBlastDeaths()
    {
        if (m_deathBuffer.Count == 0)
            return;

        // 서버(또는 오프라인)가 임펄스를 확정한다 — 이 값이 전 피어의 공통 입력이 된다.
        Vector3[] impulses = new Vector3[m_deathBuffer.Count];
        for (int i = 0; i < impulses.Length; i++)
            impulses[i] = EvaluateRagdollImpulse(m_deathBuffer[i].transform.position);

        if (!IsSpawned || !IsServer)
        {
            for (int i = 0; i < m_deathBuffer.Count; i++)
                ApplyBlastRagdoll(m_deathBuffer[i], impulses[i]); // 오프라인 — 피어가 하나뿐이다
            return;
        }

        // <b>임펄스는 물리 권위 피어에게만 간다</b> (#957) — 이관을 정착까지 미룬 구간에는 그것이
        // 서버가 아니라 <b>아직 옛 오너</b>다. 근거는 TrafficVehicle.ResolveImpulseAuthority.
        NetworkObjectReference[] victims = new NetworkObjectReference[m_deathBuffer.Count];
        ulong[] authorities = new ulong[m_deathBuffer.Count];
        ulong serverId = NetworkManager.ServerClientId;

        for (int i = 0; i < victims.Length; i++)
        {
            victims[i] = m_deathBuffer[i];

            PlayerIncapacitation incap = m_deathBuffer[i].GetComponent<PlayerIncapacitation>();
            authorities[i] =
                incap != null && incap.IsOwnershipHandoverPending
                    ? incap.BodyOwnerClientId
                    : m_deathBuffer[i].OwnerClientId;

            // 서버가 권위일 때만 힘을 싣는다 — 아니면 상태 진입만 시킨다.
            ApplyBlastRagdoll(
                m_deathBuffer[i],
                authorities[i] == serverId ? impulses[i] : Vector3.zero
            );
        }

        BlastDeathsClientRpc(victims, impulses, authorities);
    }

    [ClientRpc]
    private void BlastDeathsClientRpc(
        NetworkObjectReference[] victims,
        Vector3[] impulses,
        ulong[] authorities
    )
    {
        if (IsServer)
            return; // 호스트는 위에서 이미 발행

        NetworkManager nm = NetworkManager.Singleton;

        // 길이는 서버가 맞춰 보내지만, 직렬화 경계를 믿지 않고 짧은 쪽까지만 돈다.
        int count = Mathf.Min(victims.Length, impulses.Length);
        count = Mathf.Min(count, authorities.Length);
        for (int i = 0; i < count; i++)
        {
            if (!victims[i].TryGet(out NetworkObject victim))
                continue;

            // 권위 피어만 힘을 싣는다. 나머지는 상태 진입만 — 뼈가 키네마틱이라 어차피 무시된다.
            bool mine = nm != null && nm.LocalClientId == authorities[i];
            ApplyBlastRagdoll(victim, mine ? impulses[i] : Vector3.zero);
        }
    }

    // 래그돌 진입은 멱등이다 — 사망 폴링과 이 RPC 중 어느 쪽이 먼저 와도 결과가 같다.
    private void ApplyBlastRagdoll(NetworkObject victim, Vector3 impulse)
    {
        if (victim == null || !victim.TryGetComponent(out PlayerRagdoll ragdoll))
            return;

        ragdoll.EnterRagdoll(impulse);
    }

    // ---- 폭발 생존자 → 산 채로 래그돌 발사 ----

    /// <summary>
    /// 살아남은 동료를 래그돌인 채로 날린다 — 홈런 진압봉(<c>HomeRunBaton.ServerLaunchPlayer</c>)과
    /// 같은 문이다. 상태(<see cref="IncapacitationCause.Launched"/>)는 서버 권위 동기화값이라
    /// 전 피어가 <c>PlayerRagdoll.PollRagdollCause</c> 폴링으로 알아서 진입하고, RPC가 필요한 것은
    /// 임펄스 하나뿐이다.
    ///
    /// ⚠ <b>여기서만 <see cref="RpcTarget.Single"/>을 쓸 수 있다 — 사망자 경로에 복사하지 말 것.</b>
    /// 비행(Launched)은 소유권을 옮기지 않아 물리를 맞은 본인(오너)이 돌리지만, 사망은
    /// <c>ApplyDeathOwnership</c>이 같은 호출 스택에서 소유권을 서버로 옮겨 버려 이 시점의
    /// <c>OwnerClientId</c>가 이미 서버다 — 그래서 사망자는 <see cref="BlastDeathsClientRpc"/>로
    /// 전 피어에 뿌린다. 같은 함정이 <c>TrafficVehicle.ServerNotifyDeathRagdoll</c>에도 적혀 있다.
    /// </summary>
    private void ServerLaunchSurvivor(PlayerHealth player, Vector3 impulse)
    {
        // 반경 밖이라 세기가 남지 않았다 — 날릴 것이 없으면 눕히지도 않는다.
        if (impulse == Vector3.zero)
            return;

        // ⚠ <b>이미 날아가는 중이면 다시 안 날아간다</b> — ServerLaunch가 IsIncapacitated에서 물러난다.
        // 연쇄 폭발에서 두 번째 폭발은 <b>피해는 넣지만 임펄스는 못 얹는다</b>(회차가 살아 있어야
        // 정착 통보가 짝이 맞는다). 눈에 띄면 별도 이슈 — 근거는 docs/506-explosion-ragdoll.md §13.
        PlayerIncapacitation incap = player.GetComponent<PlayerIncapacitation>();
        incap?.ServerLaunch(m_launchMaxSeconds); // 이미 무력화된 대상이면 스스로 무동작이다

        if (!IsSpawned)
        {
            player.GetComponent<PlayerRagdoll>()?.EnterRagdoll(impulse); // 오프라인 Play 테스트
            return;
        }

        LaunchSurvivorRpc(impulse, RpcTarget.Single(player.OwnerClientId, RpcTargetUse.Temp));
    }

    [Rpc(SendTo.SpecifiedInParams)]
    private void LaunchSurvivorRpc(Vector3 impulse, RpcParams rpcParams)
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || nm.LocalClient.PlayerObject == null)
            return;

        nm.LocalClient.PlayerObject.GetComponent<PlayerRagdoll>()?.EnterRagdoll(impulse);
    }

    // 콜라이더 여러 개로 잡히는 NPC를 집합으로 한 번만 처리해 피해·발사를 건다.
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
                // 살아남은 시민도 <b>산 채로 래그돌</b>이다 — 홈런 진압봉과 같은 문(#815)이고
                // 대상이 시체가 아닐 뿐이다. 스턴 오버레이를 켠 채 뼈에 임펄스를 주는 순서까지
                // 저쪽이 쥐고 있으므로 여기서 재구현하지 않는다.
                // 연행·체포 중인 시민도 날아간다 — 그 함수가 Dead/Jailed/Intruding만 빼므로
                // 홈런봉과 규칙이 하나로 유지된다(수갑은 폭발로 풀리지 않는다).
                npc.Knockback.ServerLaunchRagdoll(
                    EvaluateRagdollImpulse(position),
                    m_launchStunSeconds,
                    threat: null // 폭탄은 도망칠 대상이 아니다 — 깨어나면 평소 FSM으로 돌아간다
                );
            }
        }
    }
}
