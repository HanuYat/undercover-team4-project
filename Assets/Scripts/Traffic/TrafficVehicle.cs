using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 도로를 흐르는 차 한 대 (#634). 스폰된 자리에서 <b>직진만</b> 하고, 정해진 거리를 다 쓰면
/// 회수돼 풀로 돌아간다 — 조향도, 제자리 복귀도 없다.
///
/// <b>국면이 없다.</b> 예전(#304)에는 세워 둠 → 경고 → 주행 → 복귀 넷을 오갔는데, 그 넷은
/// "이벤트로 뽑힌 한 대를 다시 쓴다"는 전제에서만 필요한 장치였다. 상시 교통에서 차는 스폰되는
/// 순간부터 이미 달리는 중이고, 다 달리면 <b>다른 자리에서 다시 태어난다</b>.
///
/// <b>예고는 빛과 소리가 진다.</b> 경고 2초가 사라진 자리를 헤드라이트·엔진음이 대신하며, 둘 다
/// 스폰 순간부터 켜져 있다. 그 시점을 오브젝트 활성화(<see cref="OnEnable"/>)에 묶어 둔 이유는
/// 세션이든 오프라인이든, 서버든 클라든 <b>차가 화면에 나타나는 바로 그 순간</b>이기 때문이다.
/// 공정성의 나머지 절반인 배출 간격의 하한은 <see cref="TrafficLane"/>이 진다.
///
/// 이동은 서버가 계산하고 NetworkTransform이 결과를 복제한다. NavMesh를 쓰지 않는다 — 도로가
/// 아니라 좌표 직선이다.
///
/// 명중 처리는 폭탄(<see cref="BombDevice"/>)의 3단 구조를 그대로 따른다:
///  · 사람 피해·밧줄 해제는 서버
///  · 사람 넉백은 <b>오너 클라</b> — 이동 권한이 오너에게 있어 서버가 밀어도 되돌아간다
///  · NPC 피해·넉백은 서버 — NPC 이동 권한은 서버에 있다
/// 한 번 친 대상은 다시 치지 않는다(차체가 지나가는 동안 매 틱 겹치므로).
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

    // 서버만 쓰는 주행 상태 — 레인이 정해 준다 (ServerBeginRun)
    private Vector3 m_endPoint;
    private Vector3 m_direction;
    private float m_speed;
    private bool m_driving;
    private float m_nextHornAt;

    private AudioSource m_engineSource;

    // 이미 친 대상 — 차체가 지나가는 동안 매 틱 겹치므로 한 번만 친다
    private readonly HashSet<Transform> m_hitPeople = new HashSet<Transform>();
    private readonly HashSet<NpcController> m_hitNpcs = new HashSet<NpcController>();

    // 사람을 찾는 쿼리라 <b>래그돌 본을 뺀다.</b> 한 사람이 본만 11개를 들고 있어(플레이어·시민 동일)
    // 마스크를 열어 두면 차체 5 + 도로 2 + 사람 둘이면 벌써 31개다 — OverlapBox는 버퍼가 넘치면
    // <b>잘린 개수만 돌려주고 넘쳤다고 알려주지 않으므로</b> 정면으로 치인 사람이 조용히 살아남는다.
    // 시체가 도로에 남는 설계라 그 포화는 라운드가 갈수록 상시가 된다.
    // 어느 본에 맞아도 GetComponentInParent가 같은 대상으로 올라가므로 판정력은 그대로다.
    private static readonly List<Transform> s_hornScan = new List<Transform>();

    private static int s_hitLayers; // 0 = 아직 조회 전

    private static readonly Collider[] s_overlap = new Collider[64]; // 폭탄(BombDevice)과 같은 크기

    /// <summary>
    /// 치임 판정에 쓰는 레이어 마스크 — 래그돌 본을 뺀 전 레이어.
    ///
    /// ⚠ <b>필드 초기화로 못 만든다.</b> <see cref="LayerMask.NameToLayer"/>는 MonoBehaviour의
    /// 생성자·필드 초기화(= static 생성자가 그 시점에 걸리는 경우 포함)에서 호출이 <b>금지</b>돼 있어
    /// 예외가 난다 — 풀이 차를 미리 만드는 순간 정확히 그 시점이다. 그래서 첫 사용 시점에 늦게
    /// 조회한다(<see cref="NpcNavAreas.RoadMask"/>와 같은 패턴).
    /// </summary>
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

        // 명중 장부는 여기서 비운다 — 중복을 막는 것은 <b>한 번의 주행 안에서만</b>이 목적이다
        // (차체가 지나가는 동안 매 틱 겹친다). 비우지 않으면 재사용된 차가 지난 주행에서 한 번
        // 친 사람을 영영 다시 치지 않는다.
        m_hitPeople.Clear();
        m_hitNpcs.Clear();

        m_driving = false;
        m_nextHornAt = 0f;
        IsFinished = false;
    }

    /// <summary>
    /// 이 자리에서 <paramref name="runDistance"/>m를 <paramref name="speed"/>로 달리게 한다 —
    /// 서버(또는 오프라인) 전용. 위치·회전은 이미 레인이 잡아 둔 상태로 들어온다(풀 핸들러가 스폰
    /// 전에 세운다) — 여기서는 방향을 수평으로 눕히고 그 축으로만 달린다.
    /// </summary>
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

        m_endPoint = transform.position + m_direction * runDistance;
        m_speed = speed;
        m_driving = true;
        m_nextHornAt = 0f; // 첫 경적은 앞에 사람이 보이는 즉시
        IsFinished = false;
    }

    private void Update()
    {
        // 클라는 NetworkTransform으로 결과만 받는다 — m_driving은 서버에서만 켜지므로 이 가드는
        // 사실상 중복이지만, 오프라인 폴백과 서버를 같은 조건으로 읽게 남겨 둔다
        if (!m_driving || (IsSpawned && !IsServer))
            return;

        float step = m_speed * Time.deltaTime;
        Vector3 remaining = m_endPoint - transform.position;

        bool arrived = remaining.sqrMagnitude <= step * step;

        transform.position = arrived ? m_endPoint : transform.position + m_direction * step;

        // 도착 프레임에도 판정을 돌린다 — 종점이 도로 끝이라 그 자리에 사람이 서 있을 수 있다
        ServerApplyHits();

        if (arrived)
        {
            m_driving = false;
            IsFinished = true; // 회수는 스폰한 쪽(TrafficManager)이 한다 — 풀의 주인이 하나여야 한다
            return;
        }

        ServerTickHorn();
    }

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
        source.loop = true;
        m_engineSource = source;
        return true;
    }

    /// <summary>
    /// 앞에 사람이 있으면 경적을 울린다. 서버 전용.
    ///
    /// <b>대낮에는 헤드라이트가 거의 읽히지 않는다</b> — 그래서 즉사의 예고를 지는 가시성 축
    /// (GDD 6-6)의 실질은 소리가 진다. 한 번만 울리면 눈치채기 전에 지나가므로 앞에 사람이 있는
    /// 동안 간격을 두고 되풀이한다.
    ///
    /// 대상은 <b>플레이어뿐이다</b> — 시민은 경적에 반응하지 않고, 예고는 피할 수 있는 쪽에만 뜻이 있다.
    ///
    /// ⚠ <b>쿨다운을 먼저 본다.</b> 아래 훑기가 FindObjectsByType이라 매 프레임 돌리면 동시 주행
    /// 대수만큼 곱해진다 — 이 순서면 차 한 대가 <see cref="m_hornInterval"/>에 한 번만 훑는다.
    /// </summary>
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

    // 시민도 플레이어와 같은 피해를 받는다 (#634 확정) — 예전에는 넘어지기만 했다.
    //
    // ⚠ <b>넉백이 피해보다 먼저다.</b> 피해가 먼저 가면 대상이 죽어 상태가 Dead가 되는데
    // ServerApplyKnockback은 그 상태를 거르지 않아 시체를 Stunned로 되살린다. 이 순서면
    // 사망 처리(NpcDeath.ServerEnterDead ①)가 비행을 스스로 끊는다 — 즉 <b>죽는 시민은 그 자리에
    // 무너지고</b>, 피해를 못 받는 대상(NpcStateRules.CanBeDamaged가 막는 수감자 등)만 날아간다.
    // 시체를 날리는 임펄스는 폭발(#506)과 같은 전 피어 통로가 필요해 여기서는 걸지 않는다.
    //
    // <b>장부는 시체를 못 막는다.</b> m_hitNpcs는 이 차의 한 번의 주행 안에서만 유효한데, 시체는
    // 도로에 남으므로 <b>다음 차가 같은 시체를 다시 친다</b>. 그래서 시체 거르기는 여기가 아니라
    // 맞는 쪽에 있다 — 피해는 NpcStateRules.CanBeDamaged가, 넉백은 ServerApplyKnockback이 막는다.
    private void ServerHitNpc(NpcController npc)
    {
        if (!m_hitNpcs.Add(npc))
            return;

        npc.Knockback.ServerApplyKnockback(BuildKnockback());
        npc.Health.TakeDamage(m_damage, gameObject);
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
