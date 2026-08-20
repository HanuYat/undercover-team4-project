using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 도로를 흐르는 차 한 대 (#634). 스폰된 자리에서 <b>직진만</b> 하고 정해진 거리를 다 쓰면 회수돼
/// 풀로 돌아간다 — 조향도, 국면도 없다(#304의 4국면은 차를 재사용하는 전제에서만 필요했다).
///
/// 예고는 헤드라이트·엔진음이 진다 — 둘 다 <see cref="OnEnable"/>(차가 화면에 나타나는 순간)부터
/// 켜져 있고, 배출 간격의 하한은 <see cref="TrafficLane"/>이 진다.
/// 이동은 서버가 풀고 NetworkTransform이 복제한다 — NavMesh가 아니라 좌표 직선이다. 명중은
/// 폭탄(<see cref="BombDevice"/>)의 3단 구조를 따른다: 사람 피해·밧줄은 서버, 사람 넉백은 오너 클라
/// (이동 권한이 오너다), NPC 피해·넉백은 서버. 한 번 친 대상은 그 주행 안에서 다시 치지 않는다.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(AudioSource))] // 엔진음 루프 — 떼면 접근 예고가 절반 사라진다
public class TrafficVehicle : NetworkBehaviour
{
    [Header("차체 판정")]
    [Tooltip("치임 판정 상자의 크기(m) — 실제 모델보다 조금 작게 두면 아슬아슬하게 피하는 맛이 산다")]
    [SerializeField] private Vector3 m_hitBoxSize = new Vector3(2.2f, 1.8f, 4.5f);

    [Header("명중 효과")]
    [Tooltip("치였을 때 사람·시민이 받는 피해 — 최대 HP(100)를 넘겨 확실히 죽인다. 좌우를 안 보고 건넌 대가라 어중간하게 깎지 않는다 (폭탄 폭심 150과 같은 결)")]
    [Min(0)]
    [SerializeField] private int m_damage = 120;

    [Tooltip("치였을 때 진행 방향으로 밀리는 세기")]
    [SerializeField] private float m_knockbackForward = 14f;

    [Tooltip("치였을 때 위로 뜨는 세기 — 0이면 바닥으로만 밀린다")]
    [SerializeField] private float m_knockbackUp = 6f;

    [Header("예고")]
    [Tooltip("스폰~회수 내내 켜져 있는 헤드라이트 — 소리를 못 듣는 상황(먹통·소음)에서 유일한 예고다")]
    [SerializeField] private Light[] m_headlights;

    [Tooltip("엔진음(루프) — 차체의 AudioSource가 직접 튼다. 카탈로그에 클립이 없으면 조용히 무음")]
    [SerializeField] private EAudioClip m_engineSound = EAudioClip.VehicleEngine;

    [Tooltip("경적 연출 — FxManager 조합표에서 소리를 배선한다. None이면 울리지 않는다")]
    [SerializeField] private EFx m_hornFx = EFx.VehicleHorn;

    [Tooltip("전방 이 거리(m) 안에 플레이어가 있으면 경적을 울린다 — 22m/s에서 30m가 충돌 1.4초 전이다")]
    [Min(0f)]
    [SerializeField] private float m_hornDistance = 30f;

    [Tooltip("경적을 울릴 좌우 폭(m) — 진행선에서 이만큼 벗어난 사람은 대상이 아니다. 도로 반폭 + 여유")]
    [Min(0.5f)]
    [SerializeField] private float m_hornHalfWidth = 3f;

    [Tooltip("경적을 다시 울리는 간격(초) — 앞에 사람이 계속 있으면 이 간격으로 되풀이한다")]
    [Min(0.1f)]
    [SerializeField] private float m_hornInterval = 1.2f;

    // 서버만 쓰는 주행 상태 — 레인이 정해 준다 (ServerBeginRun).
    // 위치는 <b>누적하지 않고</b> 시작점·시작시각에서 매번 새로 푼다 (#717 — ServerDriveStep 주석).
    private Vector3 m_startPoint;
    private float m_startTime;
    private float m_runDistance;
    private Vector3 m_endPoint;
    private Vector3 m_direction;
    private float m_speed;
    private bool m_driving;
    private float m_nextHornAt;

    private AudioSource m_engineSource;

    private bool m_tickHooked; // 틱 구독 여부 — 풀에서 재사용되므로 해제를 흘리면 구독이 겹친다

    // 이미 친 대상 — 차체가 지나가는 동안 매 틱 겹치므로 한 번만 친다
    private readonly HashSet<Transform> m_hitPeople = new HashSet<Transform>();
    private readonly HashSet<NpcController> m_hitNpcs = new HashSet<NpcController>();

    // 사람 쿼리라 <b>래그돌 본을 뺀다</b> — 한 사람이 본 11개라 마스크를 열면 64칸이 조용히 포화되고
    // (OverlapBox는 넘쳤다고 알려주지 않는다) 정면으로 치인 사람이 살아남는다.
    private static readonly List<Transform> s_hornScan = new List<Transform>();

    private static int s_hitLayers; // 0 = 아직 조회 전

    private static readonly Collider[] s_overlap = new Collider[64]; // 폭탄(BombBlast)은 256이다 — 여기는 차체 한 대분

    /// <summary>치임 판정 레이어 마스크 — 래그돌 본을 뺀 전 레이어.
    /// ⚠ 필드 초기화로 못 만든다: <see cref="LayerMask.NameToLayer"/>가 생성자·필드 초기화에서 금지돼 첫 사용 시점에 늦게 조회한다.</summary>
    private static int HitLayers
    {
        get
        {
            if (s_hitLayers == 0)
            {
                int ragdoll = LayerMask.NameToLayer("Ragdoll");
                s_hitLayers = ragdoll >= 0 ? ~(1 << ragdoll) : ~0; // 레이어가 없으면 전 레이어로 폴백
            }
            return s_hitLayers;
        }
    }

    // 세션에서는 주행을 <b>네트워크 틱에서</b> 굴린다 (#717) — NetworkTransform이 값을 찍는 바로 그 주기다.
    public override void OnNetworkSpawn()
    {
        if (!IsServer || NetworkManager == null)
            return;

        NetworkManager.NetworkTickSystem.Tick += OnServerTick;
        m_tickHooked = true;
    }

    public override void OnNetworkDespawn()
    {
        if (!m_tickHooked)
            return;

        if (NetworkManager != null)
            NetworkManager.NetworkTickSystem.Tick -= OnServerTick;
        m_tickHooked = false;
    }

    // 틱 1회 = 주행 1스텝. NetworkTransform이 값을 찍는 주기와 같아야 복제되는 이동량이 균일해진다.
    private void OnServerTick() => ServerDriveStep((float)NetworkManager.ServerTime.Time);


    /// <summary>주행 거리를 다 썼는가 — <see cref="TrafficManager"/>가 회수 판정에 쓴다. 서버 전용.</summary>
    public bool IsFinished { get; private set; }

    // 엔진음은 전 피어에서 건다 — 서버 주행 루프에 걸면 서버에서만 들린다.
    // 루프라 EffectManager 풀을 못 쓴다(오래된 소리를 뺏는 정책) — 발소리와 같은 이유로 자기 것을 직접 튼다.
    private void OnEnable()
    {
        SetHeadlights(true);
        PlayEngineLoop();
    }

    // 풀에 반납되는 지점 — 세션에서는 프리팹 핸들러의 Destroy가, 오프라인에서는 매니저가
    // SetActive(false)로 여기를 지난다. 두 경로를 한 자리로 모으려고 활성화에 묶었다.
    private void OnDisable()
    {
        SetHeadlights(false);
        StopEngineLoop();

        // 명중 장부는 여기서 비운다 — 중복 방지는 <b>한 주행 안에서만</b>이 목적이라, 비우지 않으면
        // 재사용된 차가 지난 주행에서 친 사람을 영영 다시 치지 않는다.
        m_hitPeople.Clear();
        m_hitNpcs.Clear();

        m_driving = false;
        m_nextHornAt = 0f;
        IsFinished = false;
    }

    /// <summary><paramref name="runDistance"/>m를 <paramref name="speed"/>로 달리게 한다 — 서버(또는 오프라인) 전용.
    /// 위치·회전은 레인이 스폰 전에 잡아 두므로, 여기서는 방향을 수평으로 눕히고 그 축으로만 달린다.</summary>
    public void ServerBeginRun(float runDistance, float speed)
    {
        m_direction = transform.forward;
        m_direction.y = 0f;

        // 레인 설정이 잘못된 경우 — 달리지 않고 곧바로 회수 대상이 된다(매니저가 다음 틱에 거둔다)
        if (m_direction.sqrMagnitude < 0.001f || runDistance <= 0f || speed <= 0f)
        {
            IsFinished = true;
            return;
        }

        m_direction.Normalize();
        transform.rotation = Quaternion.LookRotation(m_direction, Vector3.up);

        m_startPoint = transform.position;
        m_runDistance = runDistance;
        m_startTime = ServerNow();
        m_endPoint = m_startPoint + m_direction * runDistance;
        m_speed = speed;
        m_driving = true;
        m_nextHornAt = 0f; // 첫 경적은 앞에 사람이 보이는 즉시
        IsFinished = false;
    }

    // 세션에서는 주행이 틱에서 돈다(OnServerTick) — 여기는 <b>오프라인 폴백</b> 전용이다.
    // 클라는 m_driving이 꺼져 있어 어차피 들어오지 않는다.
    private void Update()
    {
        if (!m_driving)
            return;

        if (IsSpawned)
        {
            ServerRenderStep();
            return;
        }

        ServerDriveStep(Time.time);
    }

    // 호스트 화면만을 위한 프레임 보간 (#717) — 위치를 렌더 시각으로 당겨 그린다.
    // <b>판정·도착·경적은 건드리지 않는다</b> — 틱이 매번 자기 시각의 값으로 되돌려 놓는다.
    private void ServerRenderStep()
    {
        float travelled = Mathf.Min(m_speed * (ServerNow() - m_startTime), m_runDistance);
        transform.position = m_startPoint + m_direction * travelled;
    }

    // 서버(또는 오프라인)의 주행 1스텝 — 시작점·시작시각에서 그 시각의 위치를 <b>새로 푼다</b>.
    // ⚠ 누적하면 틱당 이동량이 프레임레이트에서 파생돼 클라가 속도 변동을 재생한다 (#717).
    private void ServerDriveStep(float now)
    {
        // Update()와 같은 가드 — 주행이 끝나도 회수 전까지 틱마다 불리므로(OnServerTick은
        // m_driving을 보지 않는다) 여기서 막아야 회수 타이밍이 바뀔 때 어긋나지 않는다.
        if (!m_driving)
            return;

        float travelled = m_speed * (now - m_startTime);
        bool arrived = travelled >= m_runDistance;

        transform.position = arrived ? m_endPoint : m_startPoint + m_direction * travelled;

        // 도착 스텝에도 판정을 돌린다 — 종점이 도로 끝이라 그 자리에 사람이 서 있을 수 있다
        ServerApplyHits();

        if (arrived)
        {
            m_driving = false;
            IsFinished = true; // 회수는 스폰한 쪽(TrafficManager)이 한다 — 풀의 주인이 하나여야 한다
            return;
        }

        ServerTickHorn();
    }

    // 주행 시계 — 세션은 서버 네트워크 시각, 오프라인은 로컬 시각. 시작시각과 스텝이 같은 시계를 봐야 한다.
    private float ServerNow() =>
        IsSpawned && NetworkManager != null ? (float)NetworkManager.ServerTime.Time : Time.time;

    private void SetHeadlights(bool on)
    {
        if (m_headlights == null)
            return;

        for (int i = 0; i < m_headlights.Length; i++)
        {
            if (m_headlights[i] != null)
                m_headlights[i].enabled = on;
        }
    }

    private void PlayEngineLoop()
    {
        if (!EnsureEngineSource())
            return;

        if (!m_engineSource.isPlaying)
            m_engineSource.Play();
    }

    private void StopEngineLoop()
    {
        if (m_engineSource != null && m_engineSource.isPlaying)
            m_engineSource.Stop();
    }

    // 카탈로그 배선은 한 번만 — App.Sound가 아직 없는 프레임에 걸리면 다음 활성화에서 다시 시도한다
    private bool EnsureEngineSource()
    {
        if (m_engineSource != null)
            return true;
        if (m_engineSound == EAudioClip.None)
            return false;

        AudioLibrary.Entry entry = App.Sound?.GetSfxEntry(m_engineSound);
        if (entry?.Clip == null)
            return false; // 카탈로그 미배정 — 조용히 무음

        AudioSource source = GetComponent<AudioSource>();
        if (source == null)
            return false;

        source.clip = entry.Clip;
        source.volume = entry.Volume;
        source.minDistance = entry.MinDistance;
        source.maxDistance = Mathf.Max(entry.MaxDistance, entry.MinDistance + 0.1f);
        source.spatialBlend = 1f; // 완전 3D — 어느 방향에서 오는지가 예고의 전부다
        source.rolloffMode = AudioRolloffMode.Linear;

        // 도플러를 끈다 (#673). 클라에서는 위치를 NetworkTransform 보간으로 받아 프레임별 속도가
        // 고르지 않아 피치가 떤다 — 예고용 소리는 다가오는 것을 음량이 이미 알려 준다.
        source.dopplerLevel = 0f;

        source.loop = true;
        m_engineSource = source;
        return true;
    }

    /// <summary>앞에 사람이 있으면 경적을 울린다 — 대상은 플레이어뿐. 서버 전용. 대낮엔 헤드라이트가 안 읽혀 예고의 실질은 소리다 (GDD 6-6).
    /// ⚠ 쿨다운을 먼저 본다 — 아래 훑기가 FindObjectsByType이라 동시 주행 대수만큼 곱해진다.</summary>
    private void ServerTickHorn()
    {
        if (m_hornFx == EFx.None || Time.time < m_nextHornAt)
            return;

        if (!IsPlayerAhead())
            return;

        m_nextHornAt = Time.time + m_hornInterval;
        App.Game.Fx?.PlayEverywhere(m_hornFx, transform.position);
    }

    // 진행선 앞쪽으로 m_hornDistance 안, 좌우 m_hornHalfWidth 안에 행동 가능한 플레이어가 있는가.
    // 반경으로 먼저 좁히고(유틸이 다운된 사람을 걸러 준다) 진행 축에 투영해 앞/옆을 가른다.
    private bool IsPlayerAhead()
    {
        SuddenEventUtil.CollectFieldPlayers(transform.position, m_hornDistance, s_hornScan);

        for (int i = 0; i < s_hornScan.Count; i++)
        {
            Vector3 offset = s_hornScan[i].position - transform.position;
            offset.y = 0f;

            float ahead = Vector3.Dot(offset, m_direction);
            if (ahead <= 0f) // 이미 지나친 사람에게 울릴 이유가 없다
                continue;

            Vector3 lateral = offset - m_direction * ahead;
            if (lateral.sqrMagnitude <= m_hornHalfWidth * m_hornHalfWidth)
                return true;
        }

        return false;
    }

    // 차체와 겹친 사람·NPC를 친다. 서버 전용.
    private void ServerApplyHits()
    {
        int count = Physics.OverlapBoxNonAlloc(
            transform.position + Vector3.up * (m_hitBoxSize.y * 0.5f),
            m_hitBoxSize * 0.5f,
            s_overlap,
            transform.rotation,
            HitLayers,
            QueryTriggerInteraction.Ignore
        );

        // 넘쳤으면 누군가는 잘렸다 — 조용히 안 맞는 것보다 로그가 남는 편이 낫다
        if (count == s_overlap.Length)
            Debug.LogWarning("TrafficVehicle: 치임 판정 버퍼가 찼다 — 뒤로 밀린 대상이 잘렸을 수 있다", this);

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

    // 시민도 같은 피해를 받고(#634) 진입점은 TakeEnvironmentalDamage다(#690 — 밧줄 신병도 치인다).
    // ⚠ 넉백이 피해보다 먼저다: 나중이면 Dead가 되어 씹힌다. 시체 임펄스는 안 건다(가능하지만 #634 결정, #768).
    private void ServerHitNpc(NpcController npc)
    {
        if (!m_hitNpcs.Add(npc))
            return;

        npc.Knockback.ServerApplyKnockback(BuildKnockback());
        npc.Health.TakeEnvironmentalDamage(m_damage, gameObject);
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
