using Unity.Netcode;
using UnityEngine;
using Random = UnityEngine.Random;

/// <summary>
/// 폭주 차량 — 현장 플레이어 곁을 스쳐 지나가는 위협형 돌발 이벤트. (GDD 6-4, #304)
/// 제압 대상이 아니다. 예고를 듣고 비키면 그만이고, 못 비키면 치인다(#220 "예고되고 피할 수 있다").
///
/// <b>경로는 런타임에 만든다</b> — 도로 웨이포인트를 씬에 찍지 않는다(2026-08-08 확정).
/// 맵이 교체될 예정이라 지금 찍은 마커는 버려지기 때문이다. 대신 무작위로 고른 현장 플레이어 옆을
/// 지나는 직선을 매번 새로 만든다: 시야 밖에서 출발해 플레이어 곁을 지나 반대편 시야 밖에서 사라진다.
/// 도로를 따르지 않으므로 건물 사이를 지날 수 있다 — 맵이 확정되면 마커 경로를 얹는 것이 후속이다.
///
/// 스폰·판정은 서버(또는 오프라인)에서만 — 차량은 NetworkObject로 복제된다. (#56)
/// </summary>
[RequireComponent(typeof(SuddenEventManager))]
public class RunawayVehicleEvent : MonoBehaviour, ISuddenEvent
{
    [Header("표시")]
    [SerializeField] private string m_displayName = "폭주 차량";

    [Header("차량")]
    [Tooltip("스폰할 차량 프리팹 — NetworkObject + RunawayVehicle")]
    [SerializeField] private RunawayVehicle m_vehiclePrefab;

    [Header("경로 — 씬에 깔린 도로에서 뽑는다")]
    [Tooltip("도로 타일을 식별할 이름 접두사. 맵 에셋이 바뀌면 이 값도 맞춰야 한다")]
    [SerializeField] private string m_roadNamePrefix = "SM_Env_Road";

    [Tooltip("표적에서 이 거리(m) 안의 도로를 고른다 — 멀리서 달려봐야 아무도 못 본다")]
    [SerializeField] private float m_roadSearchRadius = 45f;

    [Tooltip("이보다 짧은 직선 구간은 쓰지 않는다(m) — 짧으면 나타나자마자 사라진다. 현재 맵의 최장 직선은 55m")]
    [SerializeField] private float m_minRunLength = 40f;

    [Tooltip("도로 끝에서 이만큼(m) 더 밖에서 출발하고 반대편도 그만큼 더 가서 사라진다. 크게 두면 도로를 벗어나 건물에 걸린다")]
    [SerializeField] private float m_edgeMargin = 10f;

#if UNITY_EDITOR
    [Header("개발용 (에디터 전용)")]
    [Tooltip("실제 발생과 같은 경로로 한 번 일으킨다 — 무작위 표적·도로")]
    [SerializeField] private UnityEngine.InputSystem.Key m_devTriggerKey =
        UnityEngine.InputSystem.Key.F1;

    [Tooltip(
        "내 캐릭터를 정면으로 들이받게 보낸다 — 치임 판정 확인용이라 도로를 무시하고 반드시 맞는다.\n\n"
            + "호스트(또는 오프라인 단독 Play)에서만 동작한다 — 스폰 권한이 서버에 있다"
    )]
    [SerializeField] private UnityEngine.InputSystem.Key m_devHitMeKey =
        UnityEngine.InputSystem.Key.F2;

    [Tooltip("내 옆을 스치게 보낸다 — 회피 확인용이라 가만히 있으면 안 맞아야 한다")]
    [SerializeField] private UnityEngine.InputSystem.Key m_devGrazeKey =
        UnityEngine.InputSystem.Key.F3;

    [Min(0f)]
    [Tooltip("스침 키가 빗겨 가는 거리(m)")]
    [SerializeField] private float m_devGrazeOffset = 3f;
#endif

    /// <summary>알림을 띄우지 않는다 — 예고는 엔진음·경적으로만 한다 (#304 확정).
    /// HUD로 알리면 본부에도 그대로 보여 "소리를 듣고 피한다"가 무의미해진다.</summary>
    public bool AnnounceOnBegin => false;

    public string DisplayName => m_displayName;

    public bool IsActive => m_vehicle != null;

    private RunawayVehicle m_vehicle;

    public bool CanTrigger()
    {
        // 스쳐 지나갈 대상이 있어야 성립한다
        return m_vehiclePrefab != null && SuddenEventUtil.FindRandomFieldPlayer() != null;
    }

    public void ServerBegin()
    {
        if (m_vehiclePrefab == null)
        {
            Debug.LogWarning("RunawayVehicleEvent: 차량 프리팹이 지정되지 않음", this);
            return;
        }

        Transform target = SuddenEventUtil.FindRandomFieldPlayer();
        if (target == null)
            return; // 발생 직전에 대상이 사라짐 — 이번엔 건너뛴다

        if (!TryPickRoute(target.position, out Vector3 forward, out Vector3 start, out Vector3 end))
            return; // 어느 방향으로도 눈에 안 띄게 들여보낼 수 없다 — 이번엔 건너뛴다

        ServerLaunch(start, end, forward);
        Debug.Log($"[돌발이벤트] {m_displayName} — {target.name} 곁을 지나간다");
    }

    // 차량 한 대를 띄워 start→end로 달리게 한다. 완주 뒤 치우는 것은 ServerTick이 맡는다.
    private void ServerLaunch(Vector3 start, Vector3 end, Vector3 forward)
    {
        m_vehicle = Instantiate(m_vehiclePrefab, start, Quaternion.LookRotation(forward, Vector3.up));

        if (SuddenEventUtil.IsNetworkSessionActive)
            m_vehicle.GetComponent<NetworkObject>().Spawn();

        m_vehicle.ServerDrive(start, end);
    }

    // 표적 근처 도로의 직선 구간을 그대로 경로로 쓴다 — 도로 위만 달리므로 건물을 통과하지 않는다.
    // 양 끝을 도로 밖으로 조금 더 늘려, 도시 밖에서 들어와 반대편으로 빠져나가는 그림을 만든다.
    private bool TryPickRoute(Vector3 targetPosition, out Vector3 forward, out Vector3 start, out Vector3 end)
    {
        forward = Vector3.forward;
        start = Vector3.zero;
        end = Vector3.zero;

        if (!RoadGrid.Build(m_roadNamePrefix))
        {
            Debug.LogWarning(
                $"RunawayVehicleEvent: '{m_roadNamePrefix}'로 시작하는 도로 타일을 찾지 못했다 — 이 맵에서는 발생하지 않는다",
                this);
            return false;
        }

        if (!RoadGrid.TryFindStraightRun(
                targetPosition, m_roadSearchRadius, m_minRunLength,
                out Vector3 runFrom, out Vector3 runTo))
            return false;

        // 안 보이는 쪽 끝에서 들어온다 — 눈앞에서 튀어나오지 않게 (#332 A와 같은 이유).
        // 양쪽 다 보이거나 다 안 보이면 아무 쪽이나.
        bool fromHidden = SuddenEventUtil.IsHiddenFromFieldPlayers(runFrom);
        bool toHidden = SuddenEventUtil.IsHiddenFromFieldPlayers(runTo);
        bool swapEnds = (toHidden && !fromHidden)
            || (fromHidden == toHidden && Random.value < 0.5f);
        if (swapEnds)
        {
            Vector3 swap = runFrom;
            runFrom = runTo;
            runTo = swap;
        }

        forward = (runTo - runFrom).normalized;
        start = runFrom - forward * m_edgeMargin;
        end = runTo + forward * m_edgeMargin;
        return true;
    }

    public void ServerTick()
    {
        if (m_vehicle == null)
            return;

        // 완주했으면 치운다 — 제압 대상이 아니라 지나가는 위협이라 여기가 유일한 종료다
        if (m_vehicle.IsFinished)
            Despawn();
    }

    public void ServerReset()
    {
        Despawn(playVfx: false); // 라운드 종료 일괄 정리 — 이펙트는 끈다 (다른 이벤트와 같은 관례)
        RoadGrid.Invalidate(); // 다음 라운드는 다른 맵일 수 있다
    }

    private void Despawn(bool playVfx = true)
    {
        if (m_vehicle == null)
            return;

        SuddenEventUtil.DespawnOrDestroy(m_vehicle.gameObject, playVfx);
        m_vehicle = null;
    }

#if UNITY_EDITOR

    // ---- 개발용 단축키 (에디터 전용) ----
    //
    // 치임·회피를 손으로 확인하려면 차량이 나를 향해 와야 하는데, 정상 경로는 무작위 표적에
    // 무작위 도로다. 그래서 내 앞뒤로 직선을 깔아 곧장 보내는 지름길을 둔다 — 도로를 무시하므로
    // 경로 선정(TryPickRoute)은 발생 키(F1)로만 확인할 수 있다.
    //
    // <b>빌드에는 없다</b> — 필드까지 통째로 #if UNITY_EDITOR 안이라 컴파일되지 않는다.

    // 직접 보낸 차량도 m_vehicle에 담는다 — 완주 정리(ServerTick)·라운드 종료 정리(ServerReset)를
    // 정상 발생분과 똑같이 타므로 따로 치울 것이 없다.
    private void Update()
    {
        UnityEngine.InputSystem.Keyboard keyboard = UnityEngine.InputSystem.Keyboard.current;
        if (keyboard == null)
            return;

        if (keyboard[m_devTriggerKey].wasPressedThisFrame)
            DevTrigger();
        else if (keyboard[m_devHitMeKey].wasPressedThisFrame)
            DevSendAtMe(0f);
        else if (keyboard[m_devGrazeKey].wasPressedThisFrame)
            DevSendAtMe(m_devGrazeOffset);
    }

    // 스폰 권한이 서버에 있다 — MPPM 클론에서 눌러도 아무 일도 일어나지 않는다
    private static bool DevIsAuthority =>
        !SuddenEventUtil.IsNetworkSessionActive || NetworkManager.Singleton.IsServer;

    private void DevTrigger()
    {
        if (!DevIsAuthority || !DevCanLaunch())
            return;

        ServerBegin(); // 정상 경로 그대로 — 표적·도로 선정까지 함께 확인된다
    }

    // 내 위치 기준으로 직선을 깔아 한 대 보낸다. offset이 0이면 정면, 크면 그만큼 빗겨 간다.
    private void DevSendAtMe(float offset)
    {
        if (!DevIsAuthority || !DevCanLaunch())
            return;

        Transform me = DevLocalPlayer();
        if (me == null)
        {
            Debug.LogWarning("RunawayVehicleEvent: 개발 단축키 — 현장 플레이어를 찾지 못했다", this);
            return;
        }

        Vector3 forward = me.forward; // 내가 보는 방향에서 정면으로 온다
        Vector3 pass = me.position + Vector3.Cross(Vector3.up, forward) * offset;

        ServerLaunch(pass + forward * 60f, pass - forward * 60f, -forward);
        Debug.Log($"[돌발이벤트] 개발 단축키 — 차량 발사 (빗겨감 {offset}m)");
    }

    private bool DevCanLaunch()
    {
        if (m_vehiclePrefab == null)
        {
            Debug.LogWarning("RunawayVehicleEvent: 개발 단축키 — 차량 프리팹이 지정되지 않음", this);
            return false;
        }

        if (IsActive)
        {
            Debug.Log("[돌발이벤트] 개발 단축키 — 이미 차량이 달리는 중이다");
            return false;
        }

        return true;
    }

    // 오프라인 단독 Play에는 LocalClient가 없다 — 그때는 현장 플레이어가 나 하나다
    private static Transform DevLocalPlayer()
    {
        NetworkManager nm = NetworkManager.Singleton;
        return nm != null && nm.LocalClient != null && nm.LocalClient.PlayerObject != null
            ? nm.LocalClient.PlayerObject.transform
            : SuddenEventUtil.FindRandomFieldPlayer();
    }

#endif
}
