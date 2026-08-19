using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;
using Random = UnityEngine.Random;

/// <summary>
/// 필드에 다수의 NPC를 스폰하는 스포너. (이슈 #37)
/// 여러 스폰 포인트를 돌아가며 지정된 수만큼 NPC를 생성하고,
/// 스폰 위치는 NavMesh 위 지점으로 보정해 배회가 항상 동작하게 한다.
/// 네트워크 세션에서는 서버만 스폰하고 NetworkObject.Spawn으로 전 클라이언트에 복제한다. (#56)
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class NpcSpawner : CommonManagerBase
{
    [Header("NPC 프리팹")]
    [SerializeField] private NpcController m_npcPrefab;

    [Header("혼합 스폰 (선택)")]
    [Tooltip("지정하면 이 프리팹을 m_altRatio 확률로 섞어 스폰한다 (예: Generic NPC). 비우면 m_npcPrefab만 스폰")]
    [SerializeField] private NpcController m_npcPrefabAlt;

    [Range(0f, 1f)]
    [Tooltip("전체 스폰 중 m_npcPrefabAlt(Generic) 비율")]
    [SerializeField] private float m_altRatio = 0.5f;

    [Header("총 스폰 수")]
    [SerializeField] private int m_spawnCount = 15;

    [Header("스폰 포인트")]
    [Tooltip("비워두면 이 오브젝트의 자식 Transform들을 스폰 포인트로 사용한다")]
    [SerializeField] private Transform[] m_spawnPoints;

    [Header("스폰 분산 반경")]
    [Tooltip("각 스폰 포인트를 중심으로 이 반경(m) 안에 랜덤하게 흩어 배치한다")]
    [SerializeField] private float m_spawnRadius = 5f;

    [Header("NavMesh 보정 최대 거리")]
    [Tooltip("랜덤 위치를 이 거리(m) 안의 가장 가까운 통행 가능 지점으로 끌어당긴다. 그 안에 아무것도 없을 때만 위치를 버리고 다시 뽑는다")]
    [SerializeField] private float m_sampleMaxDistance = 4f;

    [Header("스냅 허용 거리")]
    [Tooltip("보정으로 후보가 수평으로 이 거리(m)보다 멀리 끌려가면 그 위치를 버리고 다시 뽑는다. 0 이하면 검사하지 않는다 (#714)")]
    [SerializeField] private float m_maxSnapDistance = 1.5f;

    [Header("도로 여유 거리")]
    [Tooltip("스폰 자리에서 이 거리(m) 안에 도로가 있으면 버리고 다시 뽑는다. 연석에 발을 걸친 채 시작해 차에 치이는 것을 막는다. 0 이하면 검사하지 않는다 (#660)")]
    [SerializeField] private float m_roadClearance = 0.5f;

    [Header("최소 스폰 간격")]
    [Tooltip("이미 스폰된 NPC와 이 거리(m)보다 가까우면 그 위치를 버리고 다시 뽑는다. 0 이하면 검사하지 않는다 (#660)")]
    [SerializeField] private float m_minSpawnSeparation = 1.5f;

    [Header("고립 지점 검증")]
    [Tooltip("끊긴 NavMesh 조각(건물 안쪽 주머니·2층 문턱 선반)에 스폰되지 않도록, 후보에서 빠져나오는 경로가 있는지 확인하고 없으면 다시 뽑는다 (#660)")]
    [SerializeField] private bool m_validateConnectivity = true;

    [Tooltip("스폰 포인트마다 기준점을 고를 때 쓰는 탐침 수. 실측상 6 미만은 기준점 자체가 섬에 앉을 수 있다")]
    [SerializeField] private int m_anchorProbeCount = NpcSpawnAnchors.k_defaultProbeCount;

    [Header("프레임당 스폰 수")]
    [Tooltip("한 프레임에 이 수만큼만 생성하고 다음 프레임으로 넘긴다 — 대량 스폰 시 첫 프레임 끊김(히칭) 방지")]
    [SerializeField] private int m_spawnPerFrame = 3;

    [Header("시작 시 자동 스폰")]
    [Tooltip("끄면 라운드 매니저 등 외부에서 StartSpawn()을 호출해 원하는 시점(예: 페이드인 중)에 스폰한다")]
    [SerializeField] private bool m_spawnOnStart = true;

    private readonly List<NpcController> m_spawnedNpcs = new List<NpcController>();
    private bool m_isSpawning;

    // 최소 간격 판정용 스폰 위치 — 개체의 현재 위치가 아니라 "놓은 자리"를 들고 있어야 한다.
    // 스폰은 여러 프레임에 걸치므로, 먼저 나온 개체가 배회로 움직인 뒤 위치를 보면 판정이 흔들린다.
    private readonly List<Vector3> m_spawnedPositions = new List<Vector3>();

    // 스폰 포인트별 "본토" 기준점과 경로 계산 버퍼 — 스폰 시작 시 1회 준비한다 (#660)
    private NpcSpawnAnchors.Anchor[] m_anchors;
    private NavMeshPath m_pathBuffer;

    // StartSpawn이 받은 정지 여부 — 프레임 분산 스폰 루프가 개체마다 읽는다
    private bool m_spawnFrozen;

    /// <summary>스폰된 NPC 목록. (#38 범인 랜덤 배정 등 후속 시스템에서 사용)</summary>
    public IReadOnlyList<NpcController> SpawnedNpcs => m_spawnedNpcs;

    /// <summary>
    /// 스폰 포인트 목록 — 일반 NPC와 같은 지점에서 등장해야 하는 시스템이 빌려 쓴다 (#231 범인 탈출의 침입자).
    /// Awake에서 자식 자동 수집이 끝난 뒤부터 유효하다.
    /// </summary>
    public IReadOnlyList<Transform> SpawnPoints => m_spawnPoints;

    /// <summary>스폰이 모두 끝났는지 여부.</summary>
    public bool IsSpawnCompleted { get; private set; }

    /// <summary>스폰 완료 이벤트 — 범인 배정(#38) 등 "전원 스폰 이후"에 시작해야 하는 시스템이 구독한다.</summary>
    public event Action OnSpawnCompleted;

    protected override void Awake()
    {
        base.Awake(); // App.Game.NpcSpawner 등록

        // 인스펙터에 스폰 포인트를 따로 지정하지 않았으면 자식들을 그대로 사용한다
        if (m_spawnPoints == null || m_spawnPoints.Length == 0)
        {
            m_spawnPoints = new Transform[transform.childCount];
            for (int i = 0; i < transform.childCount; i++)
                m_spawnPoints[i] = transform.GetChild(i);
        }
    }

    private void Start()
    {
        if (!m_spawnOnStart)
            return;

        // 네트워크 씬(NetworkManager 존재)에서는 자동 스폰 금지 — Play 직후는 아직 Host 시작 전이라
        // IsListening이 false여서 전 피어가 각자 "네트워크에 실리지 않은 로컬 NPC"를 만들어버린다.
        // 그러면 클라이언트는 자기 화면의 유령 NPC를 조준하게 되고, 네트워크 오브젝트가 아니라
        // 검거 요청을 서버로 보낼 수 없어 체포가 영원히 실패한다 (#118 회귀 원인).
        // 네트워크 씬의 스폰은 RoundManager가 서버 시작(OnServerStarted) 후 StartSpawn()으로 트리거한다.
        if (NetworkManager.Singleton != null)
        {
            Debug.LogWarning("NpcSpawner: 네트워크 씬에서는 자동 스폰을 건너뛴다 — RoundManager가 서버 시작 후 스폰을 트리거함 (m_spawnOnStart를 꺼 두는 것을 권장)", this);
            return;
        }

        StartSpawn();
    }

    /// <summary>
    /// 스폰을 시작한다. 자동 스폰을 끈 경우 라운드 매니저 등 외부에서
    /// 원하는 시점(페이드인·라운드 준비 화면 중)에 호출한다.
    /// </summary>
    /// <param name="spawnFrozen">
    /// 스폰한 NPC를 곧바로 정지 상태로 둘지. 라운드 준비 중에는 켠다 —
    /// 스폰은 프레임당 <see cref="m_spawnPerFrame"/>마리씩 나뉘므로 스폰 완료 후 일괄로 얼리면
    /// 먼저 나온 개체가 이미 몇 초 배회한 뒤가 된다. 해제는 RoundManager가 라운드 시작에 건다.
    /// </param>
    public void StartSpawn(bool spawnFrozen = false)
    {
        // 네트워크 세션에서는 서버만 스폰한다 — 클라이언트는 NGO가 복제해주는 NPC를 받기만 함 (#56)
        if (IsNetworkSessionActive && !NetworkManager.Singleton.IsServer)
            return;

        if (m_isSpawning || IsSpawnCompleted)
            return;

        m_spawnFrozen = spawnFrozen;
        SpawnAllAsync().Forget();
    }

    /// <summary>
    /// 스폰 상태를 초기화한다 — 서버 재시작(Shutdown 후 재기동) 시 재스폰을 허용하기 위해 RoundManager가 호출한다.
    /// 이전 NPC를 정리하고 완료 래치(IsSpawnCompleted)를 풀어 StartSpawn()이 다시 동작하게 한다.
    /// (네트워크 재시작 시 NGO가 이미 despawn한 NPC는 null이라 파괴를 건너뛴다)
    /// </summary>
    public void ResetSpawnState()
    {
        foreach (NpcController npc in m_spawnedNpcs)
        {
            if (npc != null)
                Destroy(npc.gameObject);
        }

        m_spawnedNpcs.Clear();
        m_spawnedPositions.Clear();
        m_isSpawning = false;
        m_spawnFrozen = false;
        IsSpawnCompleted = false;
        m_anchors = null; // 씬·NavMesh가 바뀌었을 수 있으니 다음 스폰에서 다시 잡는다
    }

    // 네트워크 세션이 켜져 있는지 — 꺼져 있으면 기존처럼 로컬 단독 스폰으로 동작한다
    private static bool IsNetworkSessionActive =>
        NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening;

    // 대량 스폰 시 첫 프레임 히칭을 막기 위해 프레임당 m_spawnPerFrame마리씩 나눠 생성한다
    private async UniTaskVoid SpawnAllAsync()
    {
        if (m_npcPrefab == null || m_spawnPoints.Length == 0)
        {
            Debug.LogWarning("NpcSpawner: NPC 프리팹 또는 스폰 포인트가 설정되지 않음", this);
            return;
        }

        m_isSpawning = true;

        // 검증에 쓸 통행 마스크는 프리팹 에이전트에서 읽는다 — 두 프리팹은 같은 마스크를 쓴다 (#415)
        NavMeshAgent baseAgent = m_npcPrefab.GetComponent<NavMeshAgent>();
        int agentAreaMask = baseAgent != null ? baseAgent.areaMask : NavMesh.AllAreas;

        if (m_validateConnectivity)
            ResolveAnchors(agentAreaMask);

        int spawned = 0;
        int attempts = 0;
        // 스냅 거리·연결성 기각이 겹치면 시도가 늘어난다 — 실측 최악(아포칼립스, 100마리)이 173회라
        // 20배면 충분한 여유다. 상한에 걸리면 아래에서 미달 경고가 나간다.
        int maxAttempts = m_spawnCount * 20;
        int spawnedThisFrame = 0;

        while (spawned < m_spawnCount && attempts < maxAttempts)
        {
            attempts++;

            // 스폰 포인트를 순환하며 사용해 특정 지점에만 몰리는 것을 막는다
            int pointIndex = spawned % m_spawnPoints.Length;
            Transform point = m_spawnPoints[pointIndex];
            Vector2 offset = Random.insideUnitCircle * m_spawnRadius;
            Vector3 candidate = point.position + new Vector3(offset.x, 0f, offset.y);

            // 프리팹을 먼저 고른다 — 아래 NavMesh 보정에 그 에이전트의 통행 마스크를 써야 하기 때문 (#415)
            NpcController prefab = (m_npcPrefabAlt != null && Random.value < m_altRatio) ? m_npcPrefabAlt : m_npcPrefab;
            NavMeshAgent prefabAgent = prefab.GetComponent<NavMeshAgent>();
            // 도로는 뺀다 — 라운드 시작부터 도로 한복판에 서 있으면 첫 차에 그대로 치인다 (#634)
            int spawnAreaMask = NpcNavAreas.ExcludeRoad(
                prefabAgent != null ? prefabAgent.areaMask : NavMesh.AllAreas
            );

            // NavMesh 위 지점으로 보정 — NavMesh 밖에 스폰되면 NavMeshAgent가 동작하지 않아 배회가 멈춘다.
            // 못 가는 영역(Jail)에 붙여 놓으면 경로가 안 잡혀 그 자리에서 고착되므로 마스크를 건다 (#415)
            if (!NavMesh.SamplePosition(candidate, out NavMeshHit hit, m_sampleMaxDistance, spawnAreaMask))
                continue;

            // 보정은 후보를 버리지 않고 가장 가까운 통행 가능 지점으로 끌어당긴다 — 도로 위에 뽑힌 후보가
            // 전부 연석으로 투영돼 인도에 한 줄로 쌓인다. 너무 멀리 끌려간 것은 버려 분포를 되살린다 (#714).
            // 수평으로만 잰다: 스폰 포인트가 지면보다 높게 놓인 경우(실측 예: y=1.06) 수직 성분이 상시로
            // 끼어들어 그 포인트만 과도하게 기각된다. 위층 선반에 얹히는 문제는 아래 연결성 검증이 잡는다.
            if (m_maxSnapDistance > 0f)
            {
                Vector3 snapDelta = hit.position - candidate;
                snapDelta.y = 0f;
                if (snapDelta.sqrMagnitude > m_maxSnapDistance * m_maxSnapDistance)
                    continue;
            }

            // 마스크에서 도로를 빼도 도로 위 후보는 버려지지 않고 "가장 가까운 인도 지점" =
            // <b>연석 경계선</b>으로 끌려온다. 그 자리는 발밑 폴리곤이 도로라(실측: 100마리 중 7~9마리)
            // NpcNavAreas.IsOnRoad가 참이 되고, 그러면 배회 중에도 마스크에서 도로가 빠지지 않아
            // (#634의 경로 실패 가드) 차도로 걸어 들어간다. 도로에서 떨어진 자리만 받는다.
            if (NpcNavAreas.HasRoadWithin(hit.position, m_roadClearance))
                continue;

            // 스폰 포인트 하나에 수십 마리가 몰리면 반경 안이 포화돼 서로 겹쳐 선다 — 실측(아포칼립스,
            // 포인트당 12.5마리)으로 100마리 중 46마리가 1m 안에 이웃을 두고 나왔다. 이미 놓은 자리와
            // 너무 가까운 후보는 버려 간격을 확보한다.
            if (m_minSpawnSeparation > 0f && IsTooCloseToSpawned(hit.position))
                continue;

            // SamplePosition은 "NavMesh 위인가"만 답한다 — 끊긴 조각 위여도 참이라 그대로 두면
            // 건물 안쪽 주머니나 2층 문턱에 얹힌 채 영영 못 나온다. 기준점까지 길이 있는지 묻는다 (#660).
            // 통행은 에이전트의 전체 마스크로 본다 — 도로를 뺀 마스크로 물으면 블록 간이 전부 끊긴 것으로 나온다.
            if (m_validateConnectivity && m_anchors != null && m_anchors[pointIndex].IsValid)
            {
                int pathAreaMask = prefabAgent != null ? prefabAgent.areaMask : NavMesh.AllAreas;
                if (!NpcSpawnAnchors.IsConnected(hit.position, m_anchors[pointIndex].Position, pathAreaMask, m_pathBuffer))
                    continue;
            }

            Quaternion rotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
            // 부모를 지정하지 않고 씬 루트에 생성 — NetworkObject는 비NetworkObject 아래에
            // 부모로 붙인 채 스폰할 수 없다 (NGO가 경고 후 강제로 떼어낸다)
            NpcController npc = Instantiate(prefab, hit.position, rotation);
            // 외형 랜덤 교체는 프리팹의 NpcAppearance가 담당한다 — 서버가 뽑은 인덱스를 전 클라에 동기화 (#56)

            // 네트워크 세션이면 전 클라이언트에 복제 (서버 권위 스폰, #56)
            if (IsNetworkSessionActive)
                npc.GetComponent<NetworkObject>().Spawn(destroyWithScene: true);

            // 준비 중 스폰이면 곧바로 정지 — Spawn()이 OnNetworkSpawn(=FSM 시동)을 동기로 마친 뒤라
            // 여기서 얼리면 첫 Update 전에 걸려 한 걸음도 떼지 않는다
            if (m_spawnFrozen)
                npc.SetFrozen(true);

            m_spawnedNpcs.Add(npc);
            m_spawnedPositions.Add(hit.position);
            spawned++;
            spawnedThisFrame++;

            // 프레임당 할당량을 채웠으면 다음 프레임으로 양보 (스포너 파괴 시 자동 취소)
            if (spawnedThisFrame >= Mathf.Max(1, m_spawnPerFrame))
            {
                spawnedThisFrame = 0;
                await UniTask.Yield(destroyCancellationToken);
            }
        }

        if (spawned < m_spawnCount)
            Debug.LogWarning($"NpcSpawner: {m_spawnCount}마리 중 {spawned}마리만 스폰됨 — 스폰 포인트가 NavMesh 근처에 있는지 확인 필요", this);

        m_isSpawning = false;
        IsSpawnCompleted = true;
        OnSpawnCompleted?.Invoke();
    }

    // 이미 놓은 자리 중 최소 간격 안에 드는 것이 있는지
    private bool IsTooCloseToSpawned(Vector3 position)
    {
        float sqrMinSeparation = m_minSpawnSeparation * m_minSpawnSeparation;

        for (int i = 0; i < m_spawnedPositions.Count; i++)
        {
            if ((m_spawnedPositions[i] - position).sqrMagnitude < sqrMinSeparation)
                return true;
        }

        return false;
    }

    // 스폰 포인트마다 "본토" 기준점을 잡아둔다 — 후보가 여기 닿지 못하면 끊긴 조각 위라는 뜻이다 (#660)
    private void ResolveAnchors(int agentAreaMask)
    {
        m_pathBuffer = new NavMeshPath();
        m_anchors = NpcSpawnAnchors.Resolve(m_spawnPoints, m_spawnRadius, m_sampleMaxDistance,
            NpcNavAreas.ExcludeRoad(agentAreaMask), agentAreaMask, m_anchorProbeCount);

        // 기준점끼리 못 닿으면 그중 하나가 고립 구역에 앉은 것이다. 그 포인트는 정상 후보까지 전부
        // 기각돼 "N마리만 스폰됨"으로만 드러나므로, 원인이 보이게 미리 경고한다.
        int disconnected = NpcSpawnAnchors.CountDisconnectedPairs(m_anchors, agentAreaMask);
        if (disconnected > 0)
            Debug.LogWarning($"NpcSpawner: 기준점 {disconnected}쌍이 서로 닿지 않는다 — 스폰 포인트 하나가 고립 구역에 있을 수 있다 (#660)", this);
    }

    // 씬 뷰에서 스폰 포인트 위치와 분산 반경을 눈으로 확인할 수 있게 기즈모를 그린다
    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.cyan;

        if (m_spawnPoints != null && m_spawnPoints.Length > 0)
        {
            foreach (Transform point in m_spawnPoints)
            {
                if (point != null)
                    Gizmos.DrawWireSphere(point.position, m_spawnRadius);
            }
        }
        else
        {
            // 스폰 포인트 미지정 시 실제로 사용될 자식들을 미리 보여준다
            foreach (Transform child in transform)
                Gizmos.DrawWireSphere(child.position, m_spawnRadius);
        }
    }
}
