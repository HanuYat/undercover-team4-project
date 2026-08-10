using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 폭주 차량 본체 — 도로에 세워져 있다가 그 선에 사람이 들어오면 경고 후 급발진해 지나간다. (GDD 6-4, #304)
///
/// <b>국면 셋</b>(<see cref="VehiclePhase"/>) — 세워 둠 → 경고 → 주행. 세워 둔 동안은 도시 소품과
/// 구분되지 않고, 경고(시동음·경적)가 시작돼야 위험해진다. 경고 중에는 <b>움직이지 않는다</b> —
/// 맞으면 즉사 수준이라 비킬 시간이 곧 이 이벤트의 공정성이다.
///
/// 국면은 NetworkVariable로 전 피어에 알린다 — 엔진음·경적이 클라에서도 같은 순간에 나야 하고,
/// 소리가 곧 예고라 서버에서만 들리면 예고가 없는 것과 같다.
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
/// <summary>폭주 차량의 국면 — 전 피어가 이 값 하나로 같은 소리를 낸다. (#304)</summary>
public enum VehiclePhase
{
    /// <summary>도로에 세워져 있다 — 조용하다. 도시 소품과 구분되지 않는다.</summary>
    Parked,

    /// <summary>경고 중 — 시동음·경적. 아직 움직이지 않는다.</summary>
    Warning,

    /// <summary>급발진 — 직선을 달린다.</summary>
    Driving,

    /// <summary>제자리로 돌아가는 중 — 완주한 차가 <b>운전해서</b> 원래 세워져 있던 자리로 되돌아간다.
    /// 사람을 치지 않고 경적도 울리지 않는다. 순간이동으로 되돌리지 않는 이유는 그 편이
    /// 도시가 스스로 정리되는 것처럼 보이기 때문이다 (#304).</summary>
    Returning,
}

[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(AudioSource))] // 엔진음 루프 — 떼면 접근 예고가 사라진다
public class RunawayVehicle : NetworkBehaviour
{
    [Header("주행")]
    [Tooltip("주행 속도(m/s) — 플레이어 전력질주보다 충분히 빨라야 '피한다'가 성립한다")]
    [SerializeField] private float m_speed = 22f;

    [Tooltip("완주한 차가 제자리로 되돌아갈 때의 속도(m/s) — 급발진과 달리 평범한 주행이라 느리게 둔다")]
    [Min(0.1f)]
    [SerializeField] private float m_returnSpeed = 8f;

    [Tooltip("되돌아갈 때 방향을 트는 속도(도/초) — 제자리에서 홱 돌지 않게 한다")]
    [Min(1f)]
    [SerializeField] private float m_turnSpeed = 120f;

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

    [Tooltip("경고 중 경적을 다시 울리는 간격(초) — 서 있는 차가 울리는 쪽이라 주행 중 1회와 별개다")]
    [Min(0.1f)]
    [SerializeField] private float m_warningHornInterval = 0.7f;

    [Tooltip("경고·주행 중 켜지는 헤드라이트 — 소리를 못 듣는 상황(먹통·소음)에서 유일한 예고다")]
    [SerializeField] private Light[] m_headlights;

    [Tooltip("경고 중 헤드라이트 깜빡임 횟수(초당)")]
    [Min(0.1f)]
    [SerializeField] private float m_blinkFrequency = 4f;

    [Tooltip("경적 연출 — FxManager 인스펙터에서 소리를 배선한다")]
    [SerializeField] private EFx m_hornFx = EFx.None;

    [Tooltip("엔진음(루프) — 차체의 AudioSource가 직접 튼다. 카탈로그에 클립이 없으면 조용히 무음")]
    [SerializeField] private EAudioClip m_engineSound = EAudioClip.VehicleEngine;

    // 서버만 쓰는 주행 상태
    private Vector3 m_endPoint;
    private Vector3 m_direction;
    private bool m_driving;
    private bool m_hornPlayed;

    // 씬에 놓였을 때의 자리 — 완주 후 여기로 운전해 돌아간다 (#304).
    // 런타임 스폰이 아니라 맵에 미리 배치된 차라, 이 값이 곧 '원래 있어야 할 곳'이다.
    private Vector3 m_homePosition;
    private Quaternion m_homeRotation;

    // 국면 — 서버가 쓰고 전 피어가 읽는다. 소리·라이트가 이 값만 보고 돈다
    private readonly NetworkVariable<VehiclePhase> m_phaseSynced =
        new NetworkVariable<VehiclePhase>(VehiclePhase.Parked);
    private VehiclePhase m_phase = VehiclePhase.Parked; // 서버·오프라인 진실값 (비네트워크 Play 폴백)

    private AudioSource m_engineSource;
    private float m_nextWarningHornAt;

    // 이미 친 대상 — 차체가 지나가는 동안 매 틱 겹치므로 한 번만 친다
    private readonly HashSet<Transform> m_hitPeople = new HashSet<Transform>();
    private readonly HashSet<NpcController> m_hitNpcs = new HashSet<NpcController>();

    private static readonly Collider[] s_overlap = new Collider[32];

    /// <summary>완주했거나 정리돼 더 이상 달리지 않는가 — 이벤트가 종료 판정에 쓴다.</summary>
    public bool IsFinished { get; private set; }

    /// <summary>지금 국면 — 서버·오프라인은 진실값, 원격 피어는 동기화값. (DeviceBlackoutEvent와 같은 판정)</summary>
    public VehiclePhase Phase => IsSpawned && !IsServer ? m_phaseSynced.Value : m_phase;

    // 엔진음은 전 피어에서 건다 — 주행(ServerDrive)에 걸면 서버에서만 들린다.
    // 루프라 풀을 못 쓴다(오래된 소리를 뺏는 정책) — 발소리와 같은 이유로 자기 AudioSource로 직접 튼다.
    //
    // <b>여기서 틀지는 않는다</b> — 세워 둔 차가 엔진을 돌리고 있으면 도시 소품으로 보이지 않고,
    // 시동이 걸리는 순간이 곧 경고여야 한다. 재생은 국면이 경고로 넘어갈 때 시작한다.
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
        m_engineSource = source;

        ApplyPhase(Phase); // 늦게 접속한 클라: 이미 달리는 중이면 그 소리부터 이어 낸다
    }

    // 씬에 놓인 자리를 기억해 둔다 — 이벤트가 고르기 전에 잡아야 급발진으로 옮겨진 뒤에도 남는다
    private void Awake()
    {
        m_homePosition = transform.position;
        m_homeRotation = transform.rotation;
    }

    public override void OnNetworkSpawn()
    {
        // 원격 피어는 서버의 Set 경로를 타지 않으므로 동기화값 변화로 소리·라이트를 건다
        m_phaseSynced.OnValueChanged += HandlePhaseSyncedChanged;
        ApplyPhase(Phase);
    }

    public override void OnNetworkDespawn() =>
        m_phaseSynced.OnValueChanged -= HandlePhaseSyncedChanged;

    private void HandlePhaseSyncedChanged(VehiclePhase previous, VehiclePhase next) =>
        ApplyPhase(next);

    /// <summary>
    /// <b>지금 서 있는 자리에서</b> 달릴 경로를 확정한다 — 서버(또는 오프라인) 전용. 이벤트가 한 번 부른다.
    /// 차를 옮기지 않는다: 맵에 미리 놓인 차가 곧 출발점이고, 놓인 방향이 곧 진행 방향이다 (#304).
    /// 여기서는 아직 달리지 않는다 — 급발진 시점은 이벤트(선 위에 사람이 들어옴)가 정한다.
    /// </summary>
    public void ServerArm(Vector3 endPoint)
    {
        Vector3 startPoint = transform.position;
        m_endPoint = endPoint;
        m_direction = (endPoint - startPoint).normalized;
        if (m_direction.sqrMagnitude < 0.001f)
        {
            IsFinished = true;
            return;
        }

        m_driving = false;
        m_hornPlayed = false;
        IsFinished = false;
        SetPhase(VehiclePhase.Parked);
    }

    /// <summary>
    /// 완주한 차를 제자리로 <b>운전해서</b> 돌려보낸다 — 서버(또는 오프라인) 전용.
    /// 도착하면 놓여 있던 회전까지 맞추고 <see cref="VehiclePhase.Parked"/>로 돌아가, 다음 추첨 때 다시 쓰인다.
    /// </summary>
    public void ServerReturnHome()
    {
        m_driving = false;
        m_hornPlayed = false;
        IsFinished = false;
        SetPhase(VehiclePhase.Returning);
    }

    /// <summary>제자리로 즉시 되돌린다 — 라운드 종료 정리처럼 연출이 필요 없는 경로 전용. 서버 전용.</summary>
    public void ServerSnapHome()
    {
        transform.SetPositionAndRotation(m_homePosition, m_homeRotation);
        m_driving = false;
        m_hornPlayed = false;
        IsFinished = false;
        SetPhase(VehiclePhase.Parked);
    }

    /// <summary>제자리로 돌아와 멈췄는가 — 이벤트가 종료 판정에 쓴다.</summary>
    public bool IsHome =>
        (transform.position - m_homePosition).sqrMagnitude < 0.04f
        && Quaternion.Angle(transform.rotation, m_homeRotation) < 1f;

    /// <summary>경고를 시작한다 — 시동음·경적·헤드라이트. 아직 움직이지 않는다. 서버 전용.</summary>
    public void ServerBeginWarning() => SetPhase(VehiclePhase.Warning);

    /// <summary>
    /// 이 점이 차가 달릴 선 위에 있는가 — <paramref name="halfWidth"/>(m)가 위험 구역의 반폭이다.
    /// 높이를 빼고 <b>수평</b>으로만 잰다: 연석 한 칸 차이로 판정이 갈리면 "비켰는데 맞았다"가 나온다.
    /// 서버 전용 — 경로(<see cref="m_endPoint"/>)를 서버만 알고 있다.
    /// </summary>
    public bool IsOnPath(Vector3 point, float halfWidth)
    {
        Vector3 from = transform.position;
        Vector3 segment = m_endPoint - from;
        float lengthSqr = segment.sqrMagnitude;
        if (lengthSqr < 0.001f)
            return false;

        float t = Mathf.Clamp01(Vector3.Dot(point - from, segment) / lengthSqr);
        Vector3 closest = from + segment * t;

        Vector3 flat = point - closest;
        flat.y = 0f;
        return flat.sqrMagnitude <= halfWidth * halfWidth;
    }

    /// <summary>세워 둔 차를 급발진시킨다 — 경로는 <see cref="ServerPark"/>에서 이미 정해져 있다. 서버 전용.</summary>
    public void ServerStartDrive()
    {
        if (IsFinished || m_direction.sqrMagnitude < 0.001f)
            return;

        // 놓인 방향 그대로 달리므로 보통은 이미 맞아 있지만, 배치가 살짝 틀어져 있어도
        // 급발진 순간에 진행 방향으로 맞춘다 — 옆으로 미끄러지는 그림을 막는다
        transform.rotation = Quaternion.LookRotation(m_direction, Vector3.up);
        m_driving = true;
        m_hornPlayed = false;
        SetPhase(VehiclePhase.Driving);
    }

    private void Update()
    {
        TickHeadlights(); // 연출은 전 피어에서 — 소리를 못 듣는 상황에서 유일한 예고다

        // 아래는 서버(또는 오프라인)만 — 클라는 NetworkTransform으로 결과만 받는다
        if (IsSpawned && !IsServer)
            return;

        if (m_phase == VehiclePhase.Warning)
        {
            TickWarningHorn(); // 경고 중에는 움직이지 않는다 — 비킬 시간이 이 이벤트의 공정성이다
            return;
        }

        if (m_phase == VehiclePhase.Returning)
        {
            TickReturn();
            return;
        }

        if (!m_driving)
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

    // 제자리로 운전해 돌아간다 — 먼저 방향을 틀고, 향한 뒤에 굴러간다.
    // 치임 판정(ServerApplyHits)을 부르지 않는다: 사고를 내고 돌아가는 차가 가는 길에 또 사람을
    // 치면 "치웠다"가 아니라 "두 번째 사고"가 된다. 속도도 급발진과 달리 평범한 주행이다.
    private void TickReturn()
    {
        Vector3 toHome = m_homePosition - transform.position;
        toHome.y = 0f;

        // 다 왔으면 놓여 있던 회전으로 마저 맞춘다 — 그것까지 끝나야 원래 소품으로 되돌아간 것이다
        if (toHome.sqrMagnitude <= 0.04f)
        {
            transform.position = m_homePosition;
            transform.rotation = Quaternion.RotateTowards(
                transform.rotation, m_homeRotation, m_turnSpeed * Time.deltaTime);

            if (Quaternion.Angle(transform.rotation, m_homeRotation) < 1f)
            {
                transform.rotation = m_homeRotation;
                SetPhase(VehiclePhase.Parked);
            }
            return;
        }

        Quaternion want = Quaternion.LookRotation(toHome.normalized, Vector3.up);
        transform.rotation = Quaternion.RotateTowards(
            transform.rotation, want, m_turnSpeed * Time.deltaTime);

        // 아직 많이 틀어져 있으면 제자리에서 방향부터 잡는다 — 옆으로 미끄러지지 않게
        if (Quaternion.Angle(transform.rotation, want) > 20f)
            return;

        float step = Mathf.Min(m_returnSpeed * Time.deltaTime, toHome.magnitude);
        transform.position += transform.forward * step;
    }

    // 경고 중에는 경적을 되풀이한다 — 한 번으로는 서 있는 차가 왜 우는지 읽히지 않는다.
    // FxManager 경로는 서버가 전 피어에 돌리므로(PlayEverywhere) 여기가 서버 전용인 것이 맞다.
    private void TickWarningHorn()
    {
        if (m_hornFx == EFx.None || Time.time < m_nextWarningHornAt)
            return;

        m_nextWarningHornAt = Time.time + m_warningHornInterval;
        App.Game.Fx?.PlayEverywhere(m_hornFx, transform.position);
    }

    // 경고 중에는 깜빡이고, 달리는 동안에는 켜져 있다. 세워 둔 차는 꺼져 있어야 소품으로 보인다.
    private void TickHeadlights()
    {
        if (m_headlights == null || m_headlights.Length == 0)
            return;

        VehiclePhase phase = Phase;
        bool on = phase == VehiclePhase.Driving
            || (phase == VehiclePhase.Warning && Mathf.Repeat(Time.time * m_blinkFrequency, 1f) < 0.5f);

        for (int i = 0; i < m_headlights.Length; i++)
        {
            if (m_headlights[i] != null)
                m_headlights[i].enabled = on;
        }
    }

    private void SetPhase(VehiclePhase next)
    {
        m_phase = next;

        if (IsSpawned && IsServer)
            m_phaseSynced.Value = next;

        ApplyPhase(next); // 서버·오프라인은 동기화 콜백을 타지 않는다
    }

    // 국면에 딸린 연출만 — 판정은 어디서도 이 함수를 보지 않는다
    private void ApplyPhase(VehiclePhase phase)
    {
        if (phase == VehiclePhase.Parked)
        {
            if (m_engineSource != null && m_engineSource.isPlaying)
                m_engineSource.Stop();
            return;
        }

        // 시동이 걸리는 순간이 곧 경고다 — 경고·주행 내내 엔진음을 물고 간다
        if (m_engineSource != null && !m_engineSource.isPlaying)
            m_engineSource.Play();

        if (phase == VehiclePhase.Warning)
            m_nextWarningHornAt = 0f; // 첫 경적은 즉시
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
