using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>라운드 진행 단계. (GDD 3-2)</summary>
public enum RoundPhase
{
    /// <summary>시작 전 준비 상태.</summary>
    Preparing,
    /// <summary>수사·검거가 진행 중.</summary>
    InProgress,
    /// <summary>종료됨 (성공/실패는 Result 참조).</summary>
    Ended
}

/// <summary>라운드 종료 결과. (GDD 3-2 / 7장)</summary>
public enum RoundResult
{
    /// <summary>아직 종료되지 않음.</summary>
    None,
    /// <summary>목표 달성 — 진범 검거.</summary>
    Success,
    /// <summary>목표 미달 — 제한시간 초과 등. (1차 빌드에서는 미사용, #42 후속)</summary>
    Failure
}

/// <summary>
/// 라운드 흐름 관리 — 시작 시 NPC 스폰 트리거, 진행 중 상태 유지, 검거 성공 시 종료 처리. (이슈 #42, GDD 3-2)
/// 기존 테스트용 NpcRoundStarter(#37/#56)의 스폰 트리거 역할을 흡수해 본 게임의 라운드 진입점이 된다.
///
/// 1차 빌드 종료 조건은 <b>진범 1명 검거 = 성공</b> 단일 조건이다. 원래 설계(GDD 3-2)의
/// 제한시간 초과 실패는 팀 결정에 따라 후속으로 미룬다 — RoundResult.Failure/EndRound는 그 확장 지점.
///
/// 범인 배정(CriminalAssigner)·검거 판정(ArrestJudge)이 서버 권위이므로 라운드 진행도 서버(또는 오프라인)에서만 한다.
/// 네트워크 세션에서는 서버만 스폰을 트리거하고 판정을 받는다 — 클라이언트는 관여하지 않는다. (#56 패턴)
/// </summary>
// TODO: 라운드 페이즈·결과의 클라이언트 동기화는 본부 판정/결과 UI(#43) 연결 시 NetworkVariable/ClientRpc로 추가.
//       (ArrestJudge와 동일 방침 — 지금은 서버 로컬 상태 + 로컬 이벤트로만 둔다)
public class RoundManager : MonoBehaviour
{
    [Header("스포너 (비우면 씬에서 자동 탐색)")]
    [SerializeField] private NpcSpawner m_spawner;

    [Header("검거 판정 (비우면 씬에서 자동 탐색)")]
    [SerializeField] private ArrestJudge m_arrestJudge;

    private NetworkManager m_networkManager;

    /// <summary>현재 라운드 단계. 서버(또는 오프라인)의 진실값 — 클라이언트 동기화는 #43에서.</summary>
    public RoundPhase Phase { get; private set; } = RoundPhase.Preparing;

    /// <summary>라운드 종료 결과. 종료 전에는 None.</summary>
    public RoundResult Result { get; private set; } = RoundResult.None;

    /// <summary>라운드 시작 이벤트 — 스폰 트리거 직후 발행. UI·연출(#43 등)이 구독한다.</summary>
    public event Action OnRoundStarted;

    /// <summary>라운드 종료 이벤트 — 정산(#42 후속)·결과 UI(#43)가 구독한다.</summary>
    public event Action<RoundResult> OnRoundEnded;

    private void Awake()
    {
        if (m_spawner == null)
            m_spawner = FindFirstObjectByType<NpcSpawner>();
        if (m_arrestJudge == null)
            m_arrestJudge = FindFirstObjectByType<ArrestJudge>();
    }

    private void OnEnable()
    {
        if (m_arrestJudge != null)
            m_arrestJudge.OnArrestJudged += HandleArrestJudged;
    }

    private void OnDisable()
    {
        if (m_arrestJudge != null)
            m_arrestJudge.OnArrestJudged -= HandleArrestJudged;
        if (m_networkManager != null)
            m_networkManager.OnServerStarted -= HandleServerStarted;
    }

    private void Start()
    {
        if (m_spawner == null)
        {
            Debug.LogWarning("RoundManager: NpcSpawner를 찾지 못해 라운드를 시작할 수 없다", this);
            return;
        }

        m_networkManager = NetworkManager.Singleton;

        // 오프라인 실행 — 네트워크 없이 바로 라운드 시작 (기존 단독 테스트 유지)
        if (m_networkManager == null)
        {
            StartRound();
            return;
        }

        // 네트워크 세션 — 서버가 떠 있으면 즉시, 아니면 서버 시작 콜백에서 시작한다.
        // (씬에 NetworkManager가 있으면 Host 시작 전까지 라운드를 열지 않는다 — 서버 권위 스폰이 NGO에 실려야 하므로, #56)
        // 클라이언트에서는 OnServerStarted가 발생하지 않으므로 라운드를 스스로 시작하지 않는다.
        if (m_networkManager.IsServer)
            StartRound();
        else
            m_networkManager.OnServerStarted += HandleServerStarted;
    }

    private void HandleServerStarted()
    {
        StartRound();
    }

    /// <summary>
    /// 라운드를 시작한다 — NPC 스폰을 트리거하고 진행 상태로 전환한다.
    /// (스폰 → 범인 배정 → 판정 체인은 NpcSpawner.OnSpawnCompleted로 자동 이어진다)
    /// </summary>
    public void StartRound()
    {
        if (Phase != RoundPhase.Preparing)
            return;

        Phase = RoundPhase.InProgress;
        m_spawner.StartSpawn(); // 서버/오프라인만 실제 스폰 — 클라이언트 호출은 NpcSpawner가 걸러낸다 (#56)
        Debug.Log("[라운드] 시작 — NPC 스폰 트리거");
        OnRoundStarted?.Invoke();
    }

    // 검거 판정 결과 수신 — 진범을 검거하면 라운드 성공 종료. (서버/오프라인에서만 발행됨, ArrestJudge)
    private void HandleArrestJudged(ArrestResult result)
    {
        if (Phase != RoundPhase.InProgress)
            return;

        // 1차 빌드: 진범 1명 검거 = 성공. 오검거는 라운드를 끝내지 않는다 (정산·페널티는 후속 이슈).
        if (result.Verdict == ArrestVerdict.WantedCriminal)
            EndRound(RoundResult.Success);
    }

    /// <summary>라운드를 종료한다. 제한시간 실패 등 후속 종료 조건도 이 메서드로 연결한다. (#42 후속)</summary>
    public void EndRound(RoundResult result)
    {
        if (Phase == RoundPhase.Ended)
            return;

        Phase = RoundPhase.Ended;
        Result = result;
        Debug.Log($"[라운드] 종료 — 결과: {result}");
        OnRoundEnded?.Invoke(result);
    }
}
