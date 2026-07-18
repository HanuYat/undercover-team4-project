using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

public enum RoundPhase
{
    Preparing,
    InProgress,
    Ended
}

public enum RoundResult
{
    None,       // 라운드 종료 전
    Success,    // 목표 달성
    Failure     // 목표 미달
}

public enum RoundEndReason
{
    None,           // 라운드 종료 전
    QuotaMet,       // 할당량 충족
    TimeOver,
    AllPlayersDown
}

/// <summary>
/// 라운드 흐름 관리 — 시작 시 NPC 스폰 트리거, 진행 중 상태 유지, 목표 달성/실패 시 종료 처리. (이슈 #42/#103, GDD 3-2)
/// 본 게임의 라운드 진입점.
///
/// 할당량·제한시간 수치는 전부 인스펙터 — 밸런싱 보류 항목(GDD 12장)이라 코드에 못 박지 않는다.
///
/// 범인 배정(CriminalAssigner)·검거 판정(ArrestJudge)이 서버 권위이므로 라운드 진행도 서버(또는 오프라인)에서만 한다.
/// 네트워크 세션에서는 서버만 스폰을 트리거하고 판정을 받는다 — 클라이언트는 관여하지 않는다. (#56 패턴)
/// (타이머도 마찬가지 — Phase가 InProgress가 되는 곳이 서버/오프라인뿐이라 클라에서는 돌지 않는다)
/// </summary>
// TODO: 라운드 페이즈·결과의 클라이언트 동기화는 본부 판정/결과 UI(#43) 연결 시 NetworkVariable/ClientRpc로 추가.
//       (ArrestJudge와 동일 방침 — 지금은 서버 로컬 상태 + 로컬 이벤트로만 둔다)
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class RoundManager : CommonManagerBase
{
    [Header("스포너 (비우면 씬에서 자동 탐색)")]
    [SerializeField] private NpcSpawner m_spawner;

    [Header("검거 판정 (비우면 씬에서 자동 탐색)")]
    [SerializeField] private ArrestJudge m_arrestJudge;

    [Header("라운드 목표")]
    [Tooltip("라운드당 검거 할당량")]
    [Min(1)]
    [SerializeField] private int m_arrestQuota = 1;
    [Tooltip("라운드 제한시간(초). 0 이하 = 무제한(타이머 없음)")]
    [SerializeField] private float m_timeLimitSeconds = 180f;

    private NetworkManager m_networkManager;
    private CriminalAssigner m_criminalAssigner;

    /// <summary>현재 라운드 단계. 서버(또는 오프라인)의 진실값 — 클라이언트 동기화는 #43에서.</summary>
    public RoundPhase Phase { get; private set; } = RoundPhase.Preparing;

    /// <summary>라운드 종료 결과. 종료 전에는 None.</summary>
    public RoundResult Result { get; private set; } = RoundResult.None;

    /// <summary>라운드 종료 사유 — 종료 피드백 UI(#210)가 읽는다. 종료 전에는 None.</summary>
    public RoundEndReason EndReason { get; private set; } = RoundEndReason.None;

    /// <summary>이번 라운드에 검거한 진범 수 — 할당량 진행도. 서버(또는 오프라인)의 진실값. (#103)</summary>
    public int CriminalArrestCount { get; private set; }

    /// <summary>라운드당 검거 할당량 — HUD(할당량 진행 표시) 등이 읽는다. (#103)</summary>
    public int ArrestQuota => m_arrestQuota;

    /// <summary>남은 제한시간(초). 무제한이면 양의 무한대. 서버(또는 오프라인)의 진실값. (#103)</summary>
    public float RemainingSeconds { get; private set; } = float.PositiveInfinity;

    /// <summary>
    /// 라운드 종료로 게임플레이가 정지(freeze)돼야 하는지 — 플레이어 이동(PlayerMovement) 등이 읽는다. (라운드 종료 freeze)
    /// 종료(Ended)이면서 이 피어가 권위(서버/오프라인)일 때만 true.
    /// 원격 클라이언트는 아직 라운드 종료를 동기화받지 못하므로(#43 전) 항상 false를 반환해 오판으로 멈추지 않게 한다.
    /// </summary>
    // TODO(#43): 페이즈 클라 동기화가 붙으면 클라이언트도 종료 시점에 정지하도록 확장한다.
    public bool GameplayFrozen
    {
        get
        {
            if (m_networkManager != null && m_networkManager.IsListening && !m_networkManager.IsServer)
                return false;
            return Phase == RoundPhase.Ended;
        }
    }

    /// <summary>라운드 시작 이벤트 — 스폰 트리거 직후 발행. UI·연출(#43 등)이 구독한다.</summary>
    public event Action OnRoundStarted;

    /// <summary>라운드 종료 이벤트 — 정산(#42 후속)·결과 UI(#43)·종료 피드백(#210)이 구독한다.</summary>
    public event Action<RoundResult, RoundEndReason> OnRoundEnded;

    protected override void Awake()
    {
        base.Awake(); // App.Game.Round 등록

        if (m_spawner == null)
            m_spawner = FindFirstObjectByType<NpcSpawner>();
        if (m_arrestJudge == null)
            m_arrestJudge = FindFirstObjectByType<ArrestJudge>();

        m_criminalAssigner = FindFirstObjectByType<CriminalAssigner>();
    }

    private void OnEnable()
    {
        if (m_arrestJudge != null)
            m_arrestJudge.OnArrestJudged += HandleArrestJudged;
            
        // 전원 다운(전멸) 감시 — 무력화 상태 변화는 서버·오프라인에서만 발행된다. (#105)
        PlayerIncapacitation.OnAnyIncapacitatedChanged += HandleAnyIncapacitatedChanged;
        
        // 할당량 > 실제 진범 수 검증을 위해 진범 배정 완료 이벤트를 구독. (#149)
        if (m_criminalAssigner != null)
            m_criminalAssigner.OnCriminalAssigned += HandleCriminalAssigned;
    }

    private void OnDisable()
    {
        if (m_arrestJudge != null)
            m_arrestJudge.OnArrestJudged -= HandleArrestJudged;
            
        PlayerIncapacitation.OnAnyIncapacitatedChanged -= HandleAnyIncapacitatedChanged;
        
        if (m_criminalAssigner != null)
            m_criminalAssigner.OnCriminalAssigned -= HandleCriminalAssigned;
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

        // 네트워크 세션 — 로비 대기. 라운드는 호스트의 '게임 시작'(LobbyManager)이 StartRound()로 연다.
        // (서버 시작 즉시 시작하던 기존 동작 제거 — #154 로비)
    }

    // 서버 재시작 시 이전 라운드 상태를 초기화한다 — Phase·결과·진행도와 스포너 래치를 되돌려 재스폰을 허용한다.
    private void ResetForRestart()
    {
        Phase = RoundPhase.Preparing;
        Result = RoundResult.None;
        EndReason = RoundEndReason.None;
        CriminalArrestCount = 0;
        RemainingSeconds = float.PositiveInfinity;
        m_spawner.ResetSpawnState(); // IsSpawnCompleted 래치 해제 + 이전 NPC 정리 → StartSpawn 재동작
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
        CriminalArrestCount = 0;
        // 0 이하 = 무제한 — 타이머를 아예 돌리지 않는다 (밸런싱 전 테스트·본부 단독 씬용)
        RemainingSeconds = m_timeLimitSeconds > 0f ? m_timeLimitSeconds : float.PositiveInfinity;
        m_spawner.StartSpawn(); // 서버/오프라인만 실제 스폰 — 클라이언트 호출은 NpcSpawner가 걸러낸다 (#56)
        Debug.Log($"[라운드] 시작 — NPC 스폰 트리거 (할당량 {m_arrestQuota}명, 제한시간 {(float.IsPositiveInfinity(RemainingSeconds) ? "무제한" : $"{RemainingSeconds:0}초")})");
        OnRoundStarted?.Invoke();
    }

    // [버그 수정] 배정 완료 시점에 실제 진범 수와 할당량을 대조합니다. (#149)
    // 달성 불가능한 할당량일 경우, 아무런 알림 없이 영구 실패하는 상황을 막기 위해 실제 진범 수로 clamp 처리합니다.
    private void HandleCriminalAssigned(IReadOnlyList<NpcController> criminals)
    {
        if (m_arrestQuota > criminals.Count)
        {
            Debug.LogWarning($"RoundManager: 총 할당량({m_arrestQuota})이 실제 배정된 진범 수({criminals.Count})보다 큽니다 — " +
                             $"달성 불가 - {criminals.Count}명으로 할당량을 강제로 깎아 적용합니다.", this);
            m_arrestQuota = criminals.Count;
        }
    }

    private void Update()
    {
        // 제한시간 진행 (#103). Phase가 InProgress가 되는 곳이 서버/오프라인뿐이라
        // 클라이언트에서는 이 타이머가 돌지 않는다 — 라운드 진행은 서버 권위.
        if (Phase != RoundPhase.InProgress || float.IsPositiveInfinity(RemainingSeconds))
            return;

        RemainingSeconds -= Time.deltaTime;
        if (RemainingSeconds > 0f)
            return;
        RemainingSeconds = 0f;

        // 시간 초과 = 할당량 미달 확정 (채웠다면 그 순간 이미 성공 종료됐다) → 게임오버. (GDD 9-3, 부록B #2)
        // (전원 다운(전멸)도 별도 게임오버 조건이다 — HandleAnyIncapacitatedChanged, #105)
        Debug.Log($"[라운드] 제한시간 초과 — 진범 검거 {CriminalArrestCount}/{m_arrestQuota}명, 할당량 미달");
        EndRound(RoundResult.Failure, RoundEndReason.TimeOver);
    }

    // 검거 판정 결과 수신 — 진범 검거를 할당량에 누적하고, 채우면 성공 종료. (서버/오프라인에서만 발행됨, ArrestJudge)
    private void HandleArrestJudged(ArrestResult result)
    {
        if (Phase != RoundPhase.InProgress)
            return;

        // 진범 검거만 할당량에 누적 — 오검거는 라운드를 끝내지도, 할당량을 채우지도 않는다 (GDD 9-3).
        // 오검거 정산·페널티는 팀 자금 정산 이슈(별도)가 이 이벤트를 따로 구독해 처리한다.
        if (result.Verdict != ArrestVerdict.WantedCriminal)
            return;

        CriminalArrestCount++;
        Debug.Log($"[라운드] 진범 검거 — 할당량 진행 {CriminalArrestCount}/{m_arrestQuota}");

        if (CriminalArrestCount >= m_arrestQuota)
            EndRound(RoundResult.Success, RoundEndReason.QuotaMet);
    }

    // 플레이어 무력화 상태 변화 수신 — 전원 다운(전멸)이면 게임오버로 종료한다. (#105, 서버/오프라인에서만 발행됨)
    // Phase가 InProgress가 되는 곳이 서버/오프라인뿐이라 클라이언트에서는 아래 가드에 걸려 아무 일도 하지 않는다.
    private void HandleAnyIncapacitatedChanged()
    {
        if (Phase != RoundPhase.InProgress)
            return;
        if (!AreAllPlayersIncapacitated())
            return;

        Debug.Log("[라운드] 플레이어 전원 다운 — 전멸(게임오버)");
        EndRound(RoundResult.Failure, RoundEndReason.AllPlayersDown);
    }

    // 현재 존재하는 모든 플레이어가 무력화 상태인지 — 한 명이라도 멀쩡하면 false. 플레이어가 없으면 전멸이 아니다.
    // (IsIncapacitated가 온라인=동기화값/서버=실참조, 오프라인=실참조를 알아서 처리하므로 세션 종류와 무관하게 동작한다)
    private static bool AreAllPlayersIncapacitated()
    {
        PlayerIncapacitation[] players = FindObjectsByType<PlayerIncapacitation>(FindObjectsSortMode.None);
        if (players.Length == 0)
            return false;

        foreach (PlayerIncapacitation player in players)
        {
            if (!player.IsIncapacitated)
                return false;
        }
        return true;
    }

    /// <summary>라운드를 종료한다 — 성공(할당량 달성)·실패(제한시간 초과/전멸) 공통 경로. (#42/#103)</summary>
    public void EndRound(RoundResult result, RoundEndReason reason)
    {
        if (Phase == RoundPhase.Ended)
            return;

        Phase = RoundPhase.Ended;
        Result = result;
        EndReason = reason;
        FreezeAllNpcs(); // NPC 정지 — 플레이어 정지는 PlayerMovement가 GameplayFrozen을 읽어 처리
        Debug.Log($"[라운드] 종료 — 결과: {result} (사유: {reason})");
        OnRoundEnded?.Invoke(result, reason);
    }

    // 스폰된 NPC를 전부 정지시킨다 — 서버(또는 오프라인)에서만 호출되며, 서버 정지가 전 클라이언트로 복제된다.
    private void FreezeAllNpcs()
    {
        if (m_spawner == null)
            return;

        foreach (NpcController npc in m_spawner.SpawnedNpcs)
        {
            if (npc != null)
                npc.SetFrozen(true);
        }
    }
}
