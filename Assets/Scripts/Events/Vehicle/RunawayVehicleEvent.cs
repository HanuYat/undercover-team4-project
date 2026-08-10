using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using Random = UnityEngine.Random;

/// <summary>
/// 폭주 차량 — 도로에 세워 둔 차가 그 선에 들어온 사람을 향해 경고 후 급발진하는 위협형 돌발 이벤트.
/// (GDD 6-4, #304) 제압 대상이 아니다. 예고를 듣고 비키면 그만이고, 못 비키면 치인다
/// (#220 "예고되고 피할 수 있다").
///
/// <b>추첨된 순간이 아니라 사람이 선에 들어온 순간 위험해진다</b> (2026-08-10 확정). 차는 먼저 도로에
/// 조용히 세워지고(<see cref="VehiclePhase.Parked"/>), 현장 인원이 그 직선 위에 서야 경고가 시작된다 —
/// 아무도 없는 길을 혼자 달리고 끝나면 이 이벤트는 누구도 위협하지 못한 채 소모된다(추격 폭탄이
/// "표적이 생긴 시점"에 시계를 켜는 것과 같은 이유). 세워 둔 차가 눈에 보이는 것 자체가 예고이기도 하다.
///
/// <b>차는 맵에 미리 놓여 있다</b> (2026-08-10 확정). 추첨될 때 만들어 내지 않고, 씬에 배치된 차
/// 중에서 현장 인원 근처의 한 대를 골라 <b>그 자리에서</b> 무장시킨다. 그래서 경로가 따로 필요 없다 —
/// <b>놓인 자리가 출발점이고 놓인 방향이 진행 방향</b>이며, 거기서 앞으로 곧게 달린다.
/// 도로 타일을 읽어 직선을 찾던 방식(RoadGrid)은 이 결정으로 쓰지 않는다: 맵 제작자가 차를 도로에
/// 놓고 방향만 맞추면 되고, 맵이 바뀌어도 타일 이름 규칙에 기대지 않는다.
///
/// 달리기가 끝난 차는 사라지지 않고 <b>운전해서 제자리로 돌아간다</b>(<see cref="VehiclePhase.Returning"/>) —
/// 도시가 스스로 정리되는 그림이고, 같은 차를 다음 추첨에 다시 쓸 수 있다.
///
/// 판정은 서버(또는 오프라인)에서만 — 차량은 씬에 배치된 NetworkObject라 위치가 그대로 복제된다. (#56)
/// </summary>
[RequireComponent(typeof(SuddenEventManager))]
public class RunawayVehicleEvent : MonoBehaviour, ISuddenEvent
{
    [Header("표시")]
    [SerializeField] private string m_displayName = "폭주 차량";

    [Header("경로 — 맵에 놓인 차가 놓인 방향으로 달린다")]
    [Tooltip("차가 앞으로 달리는 거리(m). 놓인 자리에서 전방으로 이만큼 간 뒤 멈추고 제자리로 돌아온다")]
    [Min(10f)]
    [SerializeField] private float m_runDistance = 120f;

    [Tooltip("현장 인원에서 이 거리(m) 안에 놓인 차만 고른다 — 멀리서 달려봐야 아무도 못 본다")]
    [Min(5f)]
    [SerializeField] private float m_pickRadius = 60f;

    [Header("발동 — 선에 사람이 들어오면")]
    [Tooltip("위험 구역의 반폭(m) — 차가 달릴 직선에서 이 거리 안에 서 있으면 경고가 시작된다. 곧 플레이어가 비켜야 하는 거리다")]
    [Min(0.5f)]
    [SerializeField] private float m_laneHalfWidth = 2.5f;

    [Tooltip(
        "경고 시간(초) — 이 동안 차는 움직이지 않는다.\n\n"
            + "치이면 사실상 즉사라 이 값이 곧 공정성이다. 짧으면 '피할 수 없었다'가 되고, "
            + "길면 경고를 보고도 걸어 나가면 되는 일이 된다"
    )]
    [Min(0.1f)]
    [SerializeField] private float m_warningSeconds = 2f;

    [Tooltip(
        "아무도 선에 들어오지 않을 때 차를 치우는 시간(초).\n\n"
            + "이게 없으면 이벤트가 영원히 진행 중으로 남아 다음 돌발 이벤트가 아예 걸리지 않는다 "
            + "(프레임워크는 IsActive가 false가 되어야 재추첨한다)"
    )]
    [Min(1f)]
    [SerializeField] private float m_parkedTimeoutSeconds = 60f;

#if UNITY_EDITOR
    [Header("개발용 (에디터 전용)")]
    [Tooltip("실제 발생과 같은 경로로 한 번 일으킨다 — 무작위 표적·도로")]
    [SerializeField] private UnityEngine.InputSystem.Key m_devTriggerKey =
        UnityEngine.InputSystem.Key.F1;
#endif

    /// <summary>알림을 띄우지 않는다 — 예고는 엔진음·경적으로만 한다 (#304 확정).
    /// HUD로 알리면 본부에도 그대로 보여 "소리를 듣고 피한다"가 무의미해진다.</summary>
    public bool AnnounceOnBegin => false;

    public string DisplayName => m_displayName;

    public bool IsActive => m_vehicle != null;

    private RunawayVehicle m_vehicle;

    // 지금 국면이 시작된 시각(서버 기준) — 대기 상한과 경고 시간을 여기서 잰다
    private float m_phaseStartTime;

    // 현장 플레이어 수집 버퍼 — 매 틱 할당을 피한다 (서버에서만 쓰므로 공유 안전)
    private readonly List<Transform> m_playerBuffer = new List<Transform>();

    // 후보 차량 버퍼 — 같은 이유로 매 발생마다 새로 만들지 않는다
    private readonly List<RunawayVehicle> m_candidates = new List<RunawayVehicle>();

    public bool CanTrigger()
    {
        // 스쳐 지나갈 대상이 있어야 성립한다 — 놓인 차가 있는지는 ServerBegin이 반경까지 보고 판단한다
        return SuddenEventUtil.FindRandomFieldPlayer() != null;
    }

    public void ServerBegin()
    {
        Transform target = SuddenEventUtil.FindRandomFieldPlayer();
        if (target == null)
            return; // 발생 직전에 대상이 사라짐 — 이번엔 건너뛴다

        RunawayVehicle vehicle = PickParkedVehicle(target.position);
        if (vehicle == null)
        {
            Debug.Log($"[돌발이벤트] {m_displayName} — {target.name} 근처에 세워 둔 차가 없어 건너뛴다");
            return;
        }

        // 놓인 자리에서 놓인 방향 그대로 — 여기서 차를 옮기지도, 돌리지도 않는다
        m_vehicle = vehicle;
        m_vehicle.ServerArm(vehicle.transform.position + vehicle.transform.forward * m_runDistance);
        m_phaseStartTime = Time.time;
        Debug.Log($"[돌발이벤트] {m_displayName} — {target.name} 근처의 {vehicle.name}에 시동이 걸렸다");
    }

    // 현장 인원 근처에 <b>제자리에 서 있는</b> 차 하나 — 달리는 중이거나 돌아가는 중인 차는 고르지 않는다.
    // 씬에 놓인 차가 곧 후보다: 맵에 몇 대를 놓아 두면 그만큼 발생 지점이 늘어난다.
    private RunawayVehicle PickParkedVehicle(Vector3 near)
    {
        RunawayVehicle[] all = FindObjectsByType<RunawayVehicle>(FindObjectsSortMode.None);
        m_candidates.Clear();

        float radiusSqr = m_pickRadius * m_pickRadius;
        for (int i = 0; i < all.Length; i++)
        {
            RunawayVehicle v = all[i];
            if (v == null || v.Phase != VehiclePhase.Parked || !v.IsHome)
                continue;

            if ((v.transform.position - near).sqrMagnitude <= radiusSqr)
                m_candidates.Add(v);
        }

        return m_candidates.Count == 0 ? null : m_candidates[Random.Range(0, m_candidates.Count)];
    }

    public void ServerTick()
    {
        if (m_vehicle == null)
            return;

        float elapsed = Time.time - m_phaseStartTime;

        switch (m_vehicle.Phase)
        {
            case VehiclePhase.Parked:
                if (IsAnyFieldPlayerOnPath())
                {
                    m_vehicle.ServerBeginWarning();
                    m_phaseStartTime = Time.time;
                }
                else if (elapsed >= m_parkedTimeoutSeconds)
                {
                    // 아무도 오지 않았다 — 시동을 끄고 손을 뗀다. 차는 놓인 자리에 그대로 서 있고,
                    // 붙잡고 있으면 다음 돌발 이벤트가 아예 걸리지 않는다
                    Debug.Log($"[돌발이벤트] {m_displayName} — 아무도 선에 들어오지 않아 시동을 껐다");
                    m_vehicle = null;
                }
                break;

            case VehiclePhase.Warning:
                // 경고가 시작되면 되돌리지 않는다 — 비켰다고 얌전해지면 다음부터 아무도 안 비킨다
                if (elapsed >= m_warningSeconds)
                    m_vehicle.ServerStartDrive();
                break;

            case VehiclePhase.Driving:
                // 완주했으면 제자리로 운전해 돌아간다 — 씬에 놓인 차라 치우지 않는다
                if (m_vehicle.IsFinished)
                    m_vehicle.ServerReturnHome();
                break;

            case VehiclePhase.Returning:
                // 제자리에 서서 원래 회전까지 맞췄으면 이 이벤트는 끝이다 — 차는 다음 추첨의 후보로 돌아간다
                if (m_vehicle.IsHome)
                {
                    Debug.Log($"[돌발이벤트] {m_displayName} — {m_vehicle.name}가 제자리로 돌아왔다");
                    m_vehicle = null;
                }
                break;
        }
    }

    // 차가 달릴 직선 위에 행동 가능한 현장 인원이 있는가 — 있으면 그때부터 위험해진다
    private bool IsAnyFieldPlayerOnPath()
    {
        // 반경은 넉넉히 — 정확한 판정은 아래 IsOnPath가 선분 기준으로 한다
        SuddenEventUtil.CollectFieldPlayers(
            m_vehicle.transform.position, m_runDistance, m_playerBuffer);

        for (int i = 0; i < m_playerBuffer.Count; i++)
        {
            if (m_playerBuffer[i] != null
                && m_vehicle.IsOnPath(m_playerBuffer[i].position, m_laneHalfWidth))
                return true;
        }

        return false;
    }

    public void ServerReset()
    {
        // 라운드 종료 일괄 정리 — 여기서는 운전해 돌아갈 여유가 없다(곧 씬이 내려간다). 즉시 제자리로.
        if (m_vehicle != null)
            m_vehicle.ServerSnapHome();
        m_vehicle = null;
    }
#if UNITY_EDITOR

    // ---- 개발용 단축키 (에디터 전용) ----
    //
    // 정상 경로는 발생 간격 추첨을 기다려야 해서, 한 번 보려고 매번 그걸 통과하는 것이 성가시다.
    // 그래서 <b>발생만 앞당기는</b> 키 하나를 둔다 — 그 뒤는 전부 정상 경로라 표적·도로 선정
    // (TryPickRoute)까지 이 키로 함께 확인된다.
    //
    // 내 쪽으로 곧장 보내는 키(치임·스침)도 있었지만 걷어냈다 — 도로를 무시하고 직선을 깔아
    // 보내는 길이라 실제로 굴러갈 경로와 다른 것을 보게 된다. 치임·회피는 이 키로 뜬 차량 앞에
    // 서 보면 그대로 확인된다.
    //
    // <b>빌드에는 없다</b> — 필드까지 통째로 #if UNITY_EDITOR 안이라 컴파일되지 않는다.

    // 이 키로 뜬 차량도 m_vehicle에 담긴다 — 완주 정리(ServerTick)·라운드 종료 정리(ServerReset)를
    // 정상 발생분과 똑같이 타므로 따로 치울 것이 없다.
    private void Update()
    {
        UnityEngine.InputSystem.Keyboard keyboard = UnityEngine.InputSystem.Keyboard.current;
        if (keyboard == null)
            return;

        if (keyboard[m_devTriggerKey].wasPressedThisFrame)
            DevTrigger();
    }

    // 스폰 권한이 서버에 있다 — MPPM 클론에서 눌러도 아무 일도 일어나지 않는다
    private static bool DevIsAuthority =>
        !SuddenEventUtil.IsNetworkSessionActive || NetworkManager.Singleton.IsServer;

    private void DevTrigger()
    {
        if (!DevIsAuthority || !DevCanLaunch())
            return;

        ServerBegin(); // 정상 경로 그대로 — 차량 선정까지 함께 확인된다
    }

    private bool DevCanLaunch()
    {
        if (IsActive)
        {
            Debug.Log("[돌발이벤트] 개발 단축키 — 이미 차량이 달리는 중이다");
            return false;
        }

        return true;
    }

#endif
}
