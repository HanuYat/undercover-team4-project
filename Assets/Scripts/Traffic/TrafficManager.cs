using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using Random = UnityEngine.Random;

/// <summary>
/// 도로에 차를 흘려보내는 스포너 (#634). 추첨이 없다 — 라운드가 도는 동안 각
/// <see cref="TrafficLane"/>이 자기 간격으로 계속 차를 낸다.
///
/// 풀은 NGO의 프리팹 핸들러에 물린다(#634 판단 1-a) — 서버의 Spawn/Despawn이 그대로 대여/반납이 되고
/// 클라이언트도 같은 핸들러를 지나 자기 풀에서 꺼낸다. 그래서 이 컴포넌트는 전 피어에 있어야 하고,
/// 배출 스케줄만 서버에서 돈다. 네트워크가 없는 로컬 Play에서는 스폰 없이 풀에서 직접 꺼내 쓴다.
///
/// App에 올리지 않는다 — 코드로 참조하는 곳이 없다(R3 ②). 배선은 전부 인스펙터다.
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class TrafficManager : MonoBehaviour
{
    [Header("차량 프리팹")]
    [Tooltip("레인이 차종을 지정하지 않으면(-1) 이 목록에서 매번 랜덤으로 고른다. 전부 DefaultNetworkPrefabs에 등록돼 있어야 한다")]
    [SerializeField] private TrafficVehicle[] m_vehiclePrefabs;

    [Header("레인")]
    [Tooltip("비워 두면 이 오브젝트의 자식에서 TrafficLane을 전부 모아 쓴다")]
    [SerializeField] private TrafficLane[] m_lanes;

    [Header("배출 간격 하한의 근거")]
    [Tooltip("건너는 사람의 이동 속도(m/s) — 전력질주(8)가 아니라 걷기 기준이어야 걸어서 건너는 사람도 산다")]
    [Min(0.1f)]
    [SerializeField] private float m_crossSpeed = 5f;

    [Tooltip("건너는 시간 위에 더하는 여유(초) — 차를 보고 건널지 말지 판단하는 시간이다")]
    [Min(0f)]
    [SerializeField] private float m_gapMarginSeconds = 2f;

    [Header("풀")]
    [Tooltip("(차종마다 미리 만들어 둘 인스턴스 수 — 첫 배출의 Instantiate 히칭을 없앤다.\n" +
            "⚠ 0으로 두는 것이 맞다 (#634, 2026-08-13 확정). 0보다 크면 Awake가 맵 씬 안에 비활성 " +
            "NetworkObject를 만들어 두는데, NGO의 씬 동기화가 그걸 'in-scene placed'로 입양해 버린다. " +
            "그러면 클라의 despawn이 프리팹 핸들러를 건너뛰어 차가 풀로 안 돌아오고, 같은 인스턴스가 " +
            "여러 NetworkObjectId로 스폰돼 \\\"Object-N is already spawned!\\\"가 쏟아진다. " +
            "되살리려면 VehiclePool.Prewarm 주석을 먼저 읽을 것")]
    [Min(0)]
    [SerializeField] private int m_prewarmPerPrefab;

    [Header("교통 on/off")]
    [Tooltip("끄면 새 차가 나오지 않는다 (디버그·튜토리얼용). 이미 달리는 차는 끝까지 간다")]
    [SerializeField] private bool m_enabled = true;

    private RoundManager Round => App.Game.Round;

    private readonly List<VehiclePool> m_pools = new List<VehiclePool>(); // 차종마다 하나
    private readonly List<ActiveVehicle> m_active = new List<ActiveVehicle>();

    private float[] m_nextSpawnAt;
    private bool m_flowing;
    private bool m_handlersRegistered;
    private RoundPhase m_lastPhase = RoundPhase.Preparing;

    // 배출 스케줄은 서버(또는 오프라인)만 — 클라는 복제된 차를 받기만 한다 (#56 패턴)
    private static bool IsAuthority
    {
        get
        {
            NetworkManager net = NetworkManager.Singleton;
            return net == null || !net.IsListening || net.IsServer;
        }
    }

    private static bool IsNetworkSessionActive =>
        NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening;

    private void Awake()
    {
        if (m_lanes == null || m_lanes.Length == 0)
            m_lanes = GetComponentsInChildren<TrafficLane>(true);

        m_nextSpawnAt = new float[m_lanes.Length];

        BuildPools();
    }

    private void Start() => EnsureHandlersRegistered();

    private void OnDestroy()
    {
        NetworkManager net = NetworkManager.Singleton;
        if (m_handlersRegistered && net != null && net.PrefabHandler != null)
        {
            for (int i = 0; i < m_pools.Count; i++)
                net.PrefabHandler.RemoveHandler(m_pools[i].Prefab.gameObject);
        }
        m_handlersRegistered = false;
    }

    private void BuildPools()
    {
        if (m_vehiclePrefabs == null)
            return;

        for (int i = 0; i < m_vehiclePrefabs.Length; i++)
        {
            TrafficVehicle prefab = m_vehiclePrefabs[i];
            if (prefab == null)
                continue;

            var pool = new VehiclePool(prefab);
            pool.Prewarm(m_prewarmPerPrefab);
            m_pools.Add(pool);
        }

        if (m_pools.Count == 0)
            Debug.LogWarning("TrafficManager: 차량 프리팹이 하나도 없다 — 도로가 비어 있게 된다", this);
    }

    // 클라가 스폰 메시지를 받을 때 자기 풀을 지나려면 그 메시지보다 먼저 등록돼 있어야 한다
    private void EnsureHandlersRegistered()
    {
        if (m_handlersRegistered)
            return;

        NetworkManager net = NetworkManager.Singleton;
        if (net == null || net.PrefabHandler == null)
            return; // 네트워크가 없는 로컬 Play — 풀을 직접 쓴다

        for (int i = 0; i < m_pools.Count; i++)
            net.PrefabHandler.AddHandler(m_pools[i].Prefab.gameObject, m_pools[i]);

        m_handlersRegistered = true;
    }

    private void Update()
    {
        EnsureHandlersRegistered(); // 세션이 씬보다 늦게 서는 구성 대비 — 등록됐으면 무동작

        if (!IsAuthority)
            return;

        // 라운드가 없는 구성(맵 씬 단독 Play)에서는 그냥 흐른다 — 배치를 눈으로 보려고 여는 경우다
        RoundManager round = Round;
        RoundPhase phase = round != null ? round.Phase : RoundPhase.InProgress;
        if (phase != m_lastPhase)
        {
            HandlePhaseChanged(phase);
            m_lastPhase = phase;
        }

        if (!m_flowing)
            return;

        RecycleFinished();
        TrySpawn();
    }

    private void HandlePhaseChanged(RoundPhase phase)
    {
        if (phase == RoundPhase.InProgress)
        {
            m_flowing = true;

            // 첫 배출 시각을 레인마다 흩는다 — 안 흩으면 시작 프레임에 전 레인이 동시에 낸다
            for (int i = 0; i < m_lanes.Length; i++)
                m_nextSpawnAt[i] = Time.time + Random.Range(0f, IntervalOf(m_lanes[i]));
            return;
        }

        m_flowing = false;
        RecycleAll();
    }

    private float IntervalOf(TrafficLane lane) =>
        lane != null ? lane.NextIntervalSeconds(m_crossSpeed, m_gapMarginSeconds) : 1f;

    private void TrySpawn()
    {
        if (!m_enabled || m_pools.Count == 0)
            return;

        for (int i = 0; i < m_lanes.Length; i++)
        {
            TrafficLane lane = m_lanes[i];
            if (lane == null || Time.time < m_nextSpawnAt[i])
                continue;

            SpawnOn(lane);
            m_nextSpawnAt[i] = Time.time + IntervalOf(lane);
        }
    }

    private void SpawnOn(TrafficLane lane)
    {
        Vector3 direction = lane.Direction;
        if (direction == Vector3.zero)
        {
            Debug.LogWarning($"TrafficLane '{lane.name}': 진행 방향이 수평이 아니다 — 배출을 건너뛴다", lane);
            return;
        }

        VehiclePool pool = PickPool(lane.VehicleIndex);
        if (pool == null)
            return;

        Quaternion rotation = Quaternion.LookRotation(direction, Vector3.up);
        TrafficVehicle vehicle = pool.Rent(lane.StartPoint, rotation);

        // 클라 쪽 인스턴스는 같은 핸들러가 자기 풀에서 꺼낸다. 맵 씬의 소품이라 씬과 함께 정리한다
        if (IsNetworkSessionActive)
        {
            NetworkObject netObj = vehicle.GetComponent<NetworkObject>();

            // 이미 스폰된 차를 만나면 <b>조용히 넘기지 않는다</b>. 예전에는 여기서 그냥 건너뛰었는데,
            // 그러면 서버는 아무 일 없다는 듯 그 차를 계속 굴리고 깨진 사실은 클라 콘솔의
            // "Object-N is already spawned!"로만 남았다 — 원인이 서버 쪽에서 안 보인 이유다.
            // 풀 가드(Rent/Return)가 먼저 걸러 주므로, 여기 걸리면 풀 밖의 다른 경로가 있다는 뜻이다.
            if (netObj != null && netObj.IsSpawned)
                Debug.LogError($"TrafficManager: 이미 스폰된 차를 다시 배출하려 했다 ({vehicle.name})", vehicle);
            else if (netObj != null)
                netObj.Spawn(destroyWithScene: true);
        }

        // 주행은 스폰 뒤에 건다 — 클라가 첫 위치를 받기 전에 움직이지 않게
        vehicle.ServerBeginRun(lane.RunDistance, lane.Speed);
        m_active.Add(new ActiveVehicle(vehicle, pool));
    }

    private VehiclePool PickPool(int index)
    {
        if (m_pools.Count == 0)
            return null;
        if (index < 0)
            return m_pools[Random.Range(0, m_pools.Count)];

        return index < m_pools.Count ? m_pools[index] : m_pools[m_pools.Count - 1];
    }

    private void RecycleFinished()
    {
        for (int i = m_active.Count - 1; i >= 0; i--)
        {
            TrafficVehicle vehicle = m_active[i].Vehicle;
            if (vehicle != null && !vehicle.IsFinished)
                continue;

            Recycle(m_active[i]);
            m_active.RemoveAt(i);
        }
    }

    private void RecycleAll()
    {
        for (int i = m_active.Count - 1; i >= 0; i--)
            Recycle(m_active[i]);

        m_active.Clear();
    }

    // 세션이면 Despawn이 핸들러의 Destroy를 태워 풀로 돌리고, 오프라인이면 풀에 직접 넣는다.
    // 둘 다 결국 오브젝트를 끄므로 뒷정리는 차의 OnDisable이 한다.
    private void Recycle(ActiveVehicle entry)
    {
        TrafficVehicle vehicle = entry.Vehicle;
        if (vehicle == null)
            return; // 씬 언로드로 이미 파괴됨

        NetworkObject netObj = vehicle.GetComponent<NetworkObject>();
        if (netObj != null && netObj.IsSpawned)
        {
            netObj.Despawn(destroy: true);
            return;
        }

        entry.Pool.Return(vehicle);
    }

    private readonly struct ActiveVehicle
    {
        public readonly TrafficVehicle Vehicle;
        public readonly VehiclePool Pool;

        public ActiveVehicle(TrafficVehicle vehicle, VehiclePool pool)
        {
            Vehicle = vehicle;
            Pool = pool;
        }
    }

    /// <summary>
    /// 차종 하나의 풀 — NGO의 프리팹 핸들러이기도 하다. 서버·클라가 같은 큐를 쓴다.
    ///
    /// ⚠ <b>대기 중인 차를 어디에도 붙이지 않는다.</b> 하이어라키 정리 삼아 꺼진 부모 밑에 넣었더니
    /// NGO가 <i>"NetworkObject can only be re-parented after being spawned!"</i>로 막고
    /// <b>부모를 원래대로 되돌려 버렸다</b> — 반납할 때마다 에러가 찍히고, 미리 만들어 둔 차는 꺼진
    /// 부모 밑에 갇혀 영영 도로에 나오지 못했다. 대기석은 부모가 아니라 <see cref="k_parkPosition"/>이다.
    /// </summary>
    private sealed class VehiclePool : INetworkPrefabInstanceHandler
    {
        // 미리 만들어 둔 차를 세워 두는 자리 — 맵 한참 아래다. Instantiate는 그 프레임에 OnEnable을
        // 태우므로(헤드라이트·엔진음) 어디든 화면 밖이어야 한다. 곧바로 꺼지므로 한 프레임짜리다.
        private static readonly Vector3 k_parkPosition = new Vector3(0f, -1000f, 0f);

        private readonly TrafficVehicle m_prefab;
        private readonly Queue<TrafficVehicle> m_idle = new Queue<TrafficVehicle>();

        public VehiclePool(TrafficVehicle prefab)
        {
            m_prefab = prefab;
        }

        public TrafficVehicle Prefab => m_prefab;

        /// <summary>
        /// 미리 만들어 둘 차를 찍어낸다 — <b>지금은 쓰지 않는다</b> (m_prewarmPerPrefab = 0).
        ///
        /// ⚠ <b>여기서 만든 차는 맵 씬에 속한다.</b> Awake에서 이걸 돌리면 NGO의 씬 동기화 스캔이
        /// (비활성 오브젝트까지 훑는다) 이 클론들을 in-scene placed NetworkObject로 입양한다.
        /// 그러면 <see cref="NetworkObject"/>.InScenePlaced가 true가 되고, 서버는 DestroyObjectMessage에
        /// DestroyGameObject=false를 실어 보낸다(NetworkObject.cs:1639) — 클라의 despawn이 프리팹 핸들러를
        /// 건너뛰어(NetworkSpawnManager.cs:1913) 차가 풀로 돌아오지 않는다. 결과가 같은 인스턴스를
        /// 여러 NetworkObjectId로 스폰하는 "already spawned" 폭탄이었다. (#634)
        ///
        /// 되살리려면 <b>씬 스캔이 닿지 않는 자리</b>에서 만들어야 한다(DontDestroyOnLoad 등).
        /// 다만 실측으로는 한 판에 Instantiate 18건이 띄엄띄엄 날 뿐 한 번에 몰리지 않았다 —
        /// 히칭을 줄이려다 그 버그를 다시 들이지 말 것. 프리웜은 이 고장을 만든 최적화다.
        /// </summary>
        public void Prewarm(int count)
        {
            for (int i = 0; i < count; i++)
            {
                TrafficVehicle vehicle = Object.Instantiate(m_prefab, k_parkPosition, Quaternion.identity);
                vehicle.gameObject.SetActive(false);
                m_idle.Enqueue(vehicle);
            }
        }

        public TrafficVehicle Rent(Vector3 position, Quaternion rotation)
        {
            TrafficVehicle vehicle = null;
            while (m_idle.Count > 0 && vehicle == null)
            {
                vehicle = m_idle.Dequeue(); // 씬 언로드로 파괴된 것은 건너뛴다

                // <b>아직 스폰돼 있는 차가 큐에 있다 = 반납이 어긋나 같은 인스턴스가 두 번 들어왔다.</b>
                // 그대로 내주면 물리 인스턴스 하나가 서로 다른 NetworkObjectId 둘로 스폰되고,
                // 클라가 "Object-N is already spawned!"를 뱉는다. 한 번 깨지면 자기 회복이 안 돼
                // 그 뒤로 계속 찍힌다 — 그래서 여기서 끊는다.
                // 대여하지 않고 버린다. 이미 큐에서 빠졌으므로 다음 Rent부터는 정상 인스턴스가 나온다.
                if (vehicle != null && IsStillSpawned(vehicle))
                {
                    Debug.LogError(
                        $"TrafficManager: 스폰 상태인 차가 풀에 있다 — 반납 경로가 어긋났다 ({vehicle.name})",
                        vehicle);
                    vehicle = null;
                }
            }

            if (vehicle == null)
                return Object.Instantiate(m_prefab, position, rotation);

            // 자리를 먼저 잡고 켠다 — 순서가 뒤집히면 지난 주행이 끝난 자리에서 한 프레임 보인다
            vehicle.transform.SetPositionAndRotation(position, rotation);
            vehicle.gameObject.SetActive(true);
            return vehicle;
        }

        public void Return(TrafficVehicle vehicle)
        {
            if (vehicle == null)
                return;

            // 두 번 반납되면 큐에 같은 인스턴스가 둘 들어가고, 그때부터 Rent가 그것을 동시에 두 번
            // 내준다 — 위 Rent 주석의 그 고장이다. 원인을 여기서 이름 붙여 남긴다.
            // 큐 길이가 차종당 몇 대라 Contains 비용은 문제되지 않는다.
            if (m_idle.Contains(vehicle))
            {
                Debug.LogError($"TrafficManager: 이미 반납된 차를 또 반납했다 ({vehicle.name})", vehicle);
                return;
            }

            vehicle.gameObject.SetActive(false);
            m_idle.Enqueue(vehicle);
        }

        // 이 차의 NetworkObject가 아직 스폰 상태인가 — 풀 불변식 검사용.
        private static bool IsStillSpawned(TrafficVehicle vehicle)
        {
            NetworkObject netObj = vehicle.GetComponent<NetworkObject>();
            return netObj != null && netObj.IsSpawned;
        }

        NetworkObject INetworkPrefabInstanceHandler.Instantiate(
            ulong ownerClientId,
            Vector3 position,
            Quaternion rotation
        ) => Rent(position, rotation).GetComponent<NetworkObject>();

        void INetworkPrefabInstanceHandler.Destroy(NetworkObject networkObject)
        {
            if (networkObject != null)
                Return(networkObject.GetComponent<TrafficVehicle>());
        }
    }
}
