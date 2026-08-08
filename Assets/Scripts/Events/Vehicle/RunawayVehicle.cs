using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 폭주 차량 본체 — 정해진 직선을 고속으로 달려 지나가고, 스치는 것을 날려 버린다. (GDD 6-4, #304)
///
/// 이동은 서버가 계산하고 NetworkTransform이 결과를 복제한다. NavMesh를 쓰지 않는다 — 도로가 아니라
/// 좌표 직선이다(경로 마커가 없어도 어느 맵에서든 성립하게).
///
/// 명중 처리는 폭탄(<see cref="BombDevice"/>)의 3단 구조를 그대로 따른다:
///  · 사람 피해·밧줄 해제는 서버
///  · 사람 넉백은 <b>오너 클라</b> — 이동 권한이 오너에게 있어 서버가 밀어도 되돌아간다
///  · NPC 넉백은 서버 — NPC 이동 권한은 서버에 있다
/// 한 번 친 대상은 다시 치지 않는다(차체가 지나가는 동안 매 틱 겹치므로).
/// </summary>
[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(AudioSource))] // 엔진음 루프 — 떼면 접근 예고가 사라진다
public class RunawayVehicle : NetworkBehaviour
{
    [Header("주행")]
    [Tooltip("주행 속도(m/s) — 플레이어 전력질주보다 충분히 빨라야 '피한다'가 성립한다")]
    [SerializeField] private float m_speed = 22f;

    [Header("차체 판정")]
    [Tooltip("치임 판정 상자의 크기(m) — 실제 모델보다 조금 작게 두면 아슬아슬하게 피하는 맛이 산다")]
    [SerializeField] private Vector3 m_hitBoxSize = new Vector3(2.2f, 1.8f, 4.5f);

    [Header("명중 효과")]
    [Tooltip("치였을 때 사람이 받는 피해 — 최대 HP(100)를 넘겨 확실히 다운시킨다. 예고를 듣고 비켰어야 하는 위협이라 어중간하게 깎지 않는다 (폭탄 폭심 150과 같은 결)")]
    [Min(0)]
    [SerializeField] private int m_damage = 120;

    [Tooltip("치였을 때 진행 방향으로 밀리는 세기")]
    [SerializeField] private float m_knockbackForward = 14f;

    [Tooltip("치였을 때 위로 뜨는 세기 — 0이면 바닥으로만 밀린다")]
    [SerializeField] private float m_knockbackUp = 6f;

    [Header("예고")]
    [Tooltip("경적을 울릴 지점 — 목적지까지 남은 거리(m). 이 지점을 지날 때 한 번 울린다")]
    [SerializeField] private float m_hornDistanceBeforePass = 30f;

    [Tooltip("경적 연출 — FxManager 인스펙터에서 소리를 배선한다")]
    [SerializeField] private EFx m_hornFx = EFx.None;

    [Tooltip("엔진음(루프) — 차체의 AudioSource가 직접 튼다. 카탈로그에 클립이 없으면 조용히 무음")]
    [SerializeField] private EAudioClip m_engineSound = EAudioClip.VehicleEngine;

    // 서버만 쓰는 주행 상태
    private Vector3 m_endPoint;
    private Vector3 m_direction;
    private bool m_driving;
    private bool m_hornPlayed;

    // 이미 친 대상 — 차체가 지나가는 동안 매 틱 겹치므로 한 번만 친다
    private readonly HashSet<Transform> m_hitPeople = new HashSet<Transform>();
    private readonly HashSet<NpcController> m_hitNpcs = new HashSet<NpcController>();

    private static readonly Collider[] s_overlap = new Collider[32];

    /// <summary>완주했거나 정리돼 더 이상 달리지 않는가 — 이벤트가 종료 판정에 쓴다.</summary>
    public bool IsFinished { get; private set; }

    // 엔진음은 전 피어에서 건다 — 주행(ServerDrive)에 걸면 서버에서만 들린다.
    // 루프라 풀을 못 쓴다(오래된 소리를 뺏는 정책) — 발소리와 같은 이유로 자기 AudioSource로 직접 튼다.
    private void Start()
    {
        AudioSource source = GetComponent<AudioSource>();
        if (source == null || m_engineSound == EAudioClip.None)
            return;

        AudioLibrary.Entry entry = App.Sound?.GetSfxEntry(m_engineSound);
        if (entry?.Clip == null)
            return; // 카탈로그 미배정 — 조용히 무음

        source.clip = entry.Clip;
        source.volume = entry.Volume;
        source.minDistance = entry.MinDistance;
        source.maxDistance = Mathf.Max(entry.MaxDistance, entry.MinDistance + 0.1f);
        source.spatialBlend = 1f; // 완전 3D — 어느 방향에서 오는지가 예고의 전부다
        source.rolloffMode = AudioRolloffMode.Linear;
        source.loop = true;
        source.Play();
    }

    /// <summary>주행을 시작한다 — 서버(또는 오프라인) 전용. 스폰 직후 이벤트가 한 번 부른다.</summary>
    public void ServerDrive(Vector3 startPoint, Vector3 endPoint)
    {
        transform.position = startPoint;
        m_endPoint = endPoint;
        m_direction = (endPoint - startPoint).normalized;
        if (m_direction.sqrMagnitude < 0.001f)
        {
            IsFinished = true;
            return;
        }

        transform.rotation = Quaternion.LookRotation(m_direction, Vector3.up);
        m_driving = true;
        m_hornPlayed = false;
        IsFinished = false;
    }

    private void Update()
    {
        if (!m_driving)
            return;

        // 서버(또는 오프라인)만 움직인다 — 클라는 NetworkTransform으로 결과만 받는다
        if (IsSpawned && !IsServer)
            return;

        float step = m_speed * Time.deltaTime;
        Vector3 remaining = m_endPoint - transform.position;

        PlayHornIfNear(remaining.magnitude);

        if (remaining.sqrMagnitude <= step * step)
        {
            transform.position = m_endPoint;
            m_driving = false;
            IsFinished = true;
            return;
        }

        transform.position += m_direction * step;
        ServerApplyHits();
    }

    // 통과 직전에 경적을 한 번 울린다 — 보이지 않는 방향에서 와도 알 수 있게 (#304 예고)
    private void PlayHornIfNear(float remainingDistance)
    {
        if (m_hornPlayed || m_hornFx == EFx.None)
            return;

        // 목적지까지 남은 거리가 아니라 '지나갈 지점까지'로 재고 싶지만, 직선이라 둘이 같은 축이다
        if (remainingDistance > m_hornDistanceBeforePass)
            return;

        m_hornPlayed = true;
        App.Game.Fx?.PlayEverywhere(m_hornFx, transform.position);
    }

    // 차체와 겹친 사람·NPC를 친다. 서버 전용.
    private void ServerApplyHits()
    {
        int count = Physics.OverlapBoxNonAlloc(
            transform.position + Vector3.up * (m_hitBoxSize.y * 0.5f),
            m_hitBoxSize * 0.5f,
            s_overlap,
            transform.rotation,
            ~0,
            QueryTriggerInteraction.Ignore
        );

        for (int i = 0; i < count; i++)
        {
            Collider hit = s_overlap[i];
            if (hit == null)
                continue;

            NpcController npc = hit.GetComponentInParent<NpcController>();
            if (npc != null)
            {
                ServerHitNpc(npc);
                continue;
            }

            PlayerHealth player = hit.GetComponentInParent<PlayerHealth>();
            if (player != null)
                ServerHitPlayer(player);
        }
    }

    // NPC는 넘어지기만 한다 (팀 확정) — 넉백 착지가 알아서 기절로 보낸다. 패닉 전파는 걸지 않는다.
    private void ServerHitNpc(NpcController npc)
    {
        if (!m_hitNpcs.Add(npc))
            return;

        npc.Knockback.ServerApplyKnockback(BuildKnockback());
    }

    private void ServerHitPlayer(PlayerHealth player)
    {
        if (!m_hitPeople.Add(player.transform))
            return;

        // 이미 쓰러진 사람은 다시 치지 않는다 — 폭발이 행동 가능한 대상만 담는 것과 같은 기준
        if (player.CurrentHp <= 0)
            return;

        player.GetComponent<IDamageable>()?.TakeDamage(m_damage, gameObject);

        // 휘말리면 밧줄에서 손을 뗀다 — 폭발과 같은 규칙 (#559)
        player.GetComponent<PlayerEscorter>()?.ReleaseAllDrags();

        // 넉백은 오너 클라에서 — 이동 권한이 오너라 서버가 밀면 다음 위치 전파에 덮인다 (폭발과 같은 이유)
        if (!IsSpawned)
        {
            player.GetComponent<PlayerMovement>()?.AddKnockback(BuildKnockback());
            return;
        }

        NetworkObject victim = player.GetComponent<NetworkObject>();
        if (victim != null)
        {
            HitClientRpc(
                BuildKnockback(),
                RpcTarget.Single(victim.OwnerClientId, RpcTargetUse.Temp)
            );
        }
    }

    private Vector3 BuildKnockback() => m_direction * m_knockbackForward + Vector3.up * m_knockbackUp;

    // 맞은 사람의 오너에게만 간다 — 남의 화면에서 밀어봤자 오너가 되돌린다
    [Rpc(SendTo.SpecifiedInParams)]
    private void HitClientRpc(Vector3 knockback, RpcParams rpcParams)
    {
        var nm = NetworkManager.Singleton;
        if (nm == null || nm.LocalClient.PlayerObject == null)
            return;

        nm.LocalClient.PlayerObject.GetComponent<PlayerMovement>()?.AddKnockback(knockback);
    }
}
