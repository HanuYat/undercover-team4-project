using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 전원 준비 완료 판정 (#410) — 각 피어가 "내 화면이 실제로 준비됐다"를 서버에 보고하고,
/// 서버가 전원 보고를 확인하면 게이트를 연다. 게임 씬에 NetworkObject와 함께 배치한다.
/// 
/// 판정은 서버 권위 — 대기하는 쪽(RoundManager·로딩 화면)은 IsOpen만 본다.
///
/// 보고가 오지 않는 피어가 있어도 m_readyTimeoutSeconds가 지나면 경고 후 연다.
/// 라운드가 영영 시작되지 않는 상태를 만들지 않는다.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class SceneReadyGate : NetworkedManagerBase
{
    // 정상 대기 시간이 아니라 "안 오는 피어를 언제 포기할지"다 — 섣불리 줄이지 말 것.
    // MPPM 다인 테스트는 클론의 프레임이 굶어 정상 동작에도 10초 이상 걸린다 (#410).
    [Tooltip("전원 보고를 기다리는 상한(초) — 넘으면 경고 후 남은 인원으로 연다")]
    [SerializeField] private float m_readyTimeoutSeconds = 30f;

    // 서버 판정 결과. 클라는 스폰 시 false로 받으므로 초기 동기화 전에 앞질러 통과할 일이 없다.
    private readonly NetworkVariable<bool> m_openSynced = new NetworkVariable<bool>();

    // 표시용 진행도 — 로딩 화면의 "다른 플레이어 대기 중 (2/4)"
    private readonly NetworkVariable<int> m_readyCountSynced = new NetworkVariable<int>();
    private readonly NetworkVariable<int> m_expectedCountSynced = new NetworkVariable<int>();

    // 서버 전용 — 기다릴 대상과 보고를 마친 대상. 불변식: m_ready ⊆ m_expected
    private readonly HashSet<ulong> m_expected = new HashSet<ulong>();
    private readonly HashSet<ulong> m_ready = new HashSet<ulong>();

    private float m_deadline;

    /// <summary>서버가 "전원 준비 완료"로 판정했는가 — 라운드 시작·로딩 화면 내리기의 공통 신호.</summary>
    public bool IsOpen => m_openSynced.Value;

    /// <summary>준비를 보고한 인원 수 (표시용). 전 피어 읽기 가능.</summary>
    public int ReadyCount => m_readyCountSynced.Value;

    /// <summary>기다리는 총 인원 수 (표시용). 전 피어 읽기 가능.</summary>
    public int ExpectedCount => m_expectedCountSynced.Value;

    public override void OnNetworkSpawn()
    {
        if (!IsServer)
            return;

        // 같은 실행에서 세션을 다시 만들면 씬 오브젝트에 이전 세션 값이 남는다 (SessionRoster와 같은 방침)
        m_expected.Clear();
        m_ready.Clear();
        m_openSynced.Value = false;

        // 기다릴 대상은 이 시점의 접속자로 고정한다 — 뒤늦게 들어온 피어까지 기다리면 라운드가 늦어진다.
        foreach (ulong clientId in NetworkManager.ConnectedClientsIds)
            m_expected.Add(clientId);

        NetworkManager.OnClientDisconnectCallback += HandleClientDisconnected;

        m_deadline = Time.realtimeSinceStartup + m_readyTimeoutSeconds;
        PublishAndEvaluate();
    }

    public override void OnNetworkDespawn()
    {
        if (IsServer && NetworkManager != null)
            NetworkManager.OnClientDisconnectCallback -= HandleClientDisconnected;
    }

    /// <summary>
    /// 이 피어의 준비 완료를 서버에 보고한다 — 씬 매니저가 로딩 화면을 내리기 직전에 부른다.
    /// 호스트도 자기 몫이 있으므로 서버·클라 구분 없이 부른다.
    /// </summary>
    public void ReportSelfReady()
    {
        if (!IsSpawned)
            return; // 오프라인·씬 직접 Play — 보고할 서버가 없다

        ReportReadyRpc();
    }

    // InvokePermission = Everyone 명시 — 씬에 놓인 서버 소유 오브젝트라 어떤 플레이어도 오너가 아니다.
    // 기본값(오너 전용)이면 클라가 보고할 수 없다. (SessionRoster.ReportSelfRpc와 같은 이유)
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void ReportReadyRpc(RpcParams rpcParams = default)
    {
        // 보낸 값을 믿지 않고 서버가 본 발신자를 쓴다 — 남의 준비를 대신 보고하지 못하게
        ulong clientId = rpcParams.Receive.SenderClientId;

        // 스폰 뒤에 들어온 피어의 보고 — 기다리진 않았지만 표시 인원에는 넣는다 (3/4처럼 총원이 어긋나 보이지 않게)
        m_expected.Add(clientId);
        m_ready.Add(clientId);
        PublishAndEvaluate();
    }

    // 끊긴 피어는 기다릴 대상에서 뺀다 — 안 그러면 남은 인원이 타임아웃까지 갇힌다
    private void HandleClientDisconnected(ulong clientId)
    {
        m_expected.Remove(clientId);
        m_ready.Remove(clientId);
        PublishAndEvaluate();
    }

    private void Update()
    {
        if (!IsSpawned || !IsServer || m_openSynced.Value)
            return;

        // 실시간 기준 — timeScale이 건드려져도 흐르게 (RoundManager 시작 지연과 같은 방침)
        if (Time.realtimeSinceStartup < m_deadline)
            return;

        Debug.LogWarning(
            $"[준비] 전원 준비 완료를 {m_readyTimeoutSeconds:0.#}초 내에 받지 못했다 "
                + $"({m_ready.Count}/{m_expected.Count}명) — 남은 인원으로 진행한다",
            this
        );
        m_openSynced.Value = true;
    }

    // 표시용 수치를 복제하고, 전원이 모였으면 연다. (서버 전용 — 호출부가 전부 서버 경로다)
    private void PublishAndEvaluate()
    {
        m_readyCountSynced.Value = m_ready.Count;
        m_expectedCountSynced.Value = m_expected.Count;

        if (m_openSynced.Value || m_ready.Count < m_expected.Count)
            return;

        Debug.Log($"[준비] 전원 준비 완료 — {m_ready.Count}명");
        m_openSynced.Value = true;
    }
}
