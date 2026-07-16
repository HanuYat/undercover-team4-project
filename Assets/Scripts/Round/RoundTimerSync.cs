using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 라운드 제한시간의 클라이언트 동기화 — "남은 시간"이 아니라 <b>종료 시각(서버 시계 기준)</b> 하나만
/// NetworkVariable로 공유한다. 각 피어는 NGO의 동기화 시계(<see cref="NetworkManager.ServerTime"/>)로
/// 남은 시간을 스스로 계산하므로 매 프레임 트래픽 없이 전 클라이언트가 같은 시간을 보고,
/// 뒤늦게 접속한 플레이어도 NetworkVariable 초기 동기화만으로 즉시 정확한 값을 얻는다.
/// 표시 전용이다 — 시간 초과 판정은 지금처럼 RoundManager(서버 권위)가 한다.
///
/// 씬 구성: 씬에 NetworkObject와 함께 배치한다(in-scene placed — 서버 시작 시 자동 스폰, 프리팹 등록 불필요).
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class RoundTimerSync : NetworkBehaviour
{
    /// <summary>타이머가 돌고 있지 않음(미시작·무제한·종료)을 나타내는 종료 시각 값.</summary>
    private const double k_notRunning = 0d;

    [Header("라운드 관리 (비우면 씬에서 자동 탐색)")]
    [SerializeField]
    private RoundManager m_round;

    // 서버만 쓰기, 전 클라이언트 읽기. 값은 ServerTime 기준 라운드 종료 시각(초).
    private readonly NetworkVariable<double> m_endServerTime = new NetworkVariable<double>(
        k_notRunning
    );

    private void Awake()
    {
        if (m_round == null)
            m_round = FindFirstObjectByType<RoundManager>();
    }

    public override void OnNetworkSpawn()
    {
        if (!IsServer)
            return;

        // 서버 재시작(Shutdown 후 StartHost) 시 씬 NetworkObject에는 이전 세션 값이 남는다 —
        // 서버가 새로 뜨면 항상 정지 상태로 시작한다 (WantedListManager #209와 같은 방침)
        m_endServerTime.Value = k_notRunning;

        if (m_round == null)
        {
            Debug.LogWarning(
                "RoundTimerSync: RoundManager를 찾지 못해 타이머를 동기화할 수 없다",
                this
            );
            return;
        }

        m_round.OnRoundStarted += HandleRoundStarted;
        m_round.OnRoundEnded += HandleRoundEnded;

        // 스폰 순서 경합 대비 — 이 오브젝트가 스폰되기 전에 라운드가 이미 시작됐다면
        // 서버 진실값(RemainingSeconds)으로 종료 시각을 복원한다.
        if (m_round.Phase == RoundPhase.InProgress)
            HandleRoundStarted();
    }

    public override void OnNetworkDespawn()
    {
        if (m_round == null)
            return;

        m_round.OnRoundStarted -= HandleRoundStarted;
        m_round.OnRoundEnded -= HandleRoundEnded;
    }

    // 라운드 시작 — 서버 시계 기준 종료 시각을 한 번만 기록한다. 이후 갱신은 각 피어의 로컬 계산.
    private void HandleRoundStarted()
    {
        // 무제한(RemainingSeconds == 무한대)이면 타이머를 걸지 않는다 — UI도 표시하지 않음
        if (float.IsPositiveInfinity(m_round.RemainingSeconds))
        {
            m_endServerTime.Value = k_notRunning;
            return;
        }

        m_endServerTime.Value = NetworkManager.ServerTime.Time + m_round.RemainingSeconds;
    }

    // 라운드 종료 — 조기 종료(할당량 달성·전멸)에도 타이머 표시를 멈춘다. 종료 사유는 RoundEndFeedback(#210)가 보여준다.
    private void HandleRoundEnded(RoundResult result, RoundEndReason reason)
    {
        m_endServerTime.Value = k_notRunning;
    }

    /// <summary>
    /// 이 피어에서 계산한 남은 시간(초). 타이머가 돌고 있지 않으면(미시작·무제한·종료·미스폰) false.
    /// 서버·클라이언트 모두 동기화 시계(ServerTime)를 쓰므로 어느 피어에서든 같은 값이 나온다.
    /// </summary>
    public bool TryGetRemainingSeconds(out float seconds)
    {
        seconds = 0f;
        if (!IsSpawned || m_endServerTime.Value <= k_notRunning)
            return false;

        seconds = Mathf.Max(0f, (float)(m_endServerTime.Value - NetworkManager.ServerTime.Time));
        return true;
    }
}
