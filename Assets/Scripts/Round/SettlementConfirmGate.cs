using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 정산 확인 게이트 (#509) — 각 피어가 정산 화면을 확인했음을 서버에 보고하고, 서버가 접속 중인
/// 전원의 보고를 모으면 <see cref="AllConfirmed"/>를 세운다.
/// 게임 씬의 <c>=== GameManagers === ▸ Gates</c>에 SceneReadyGate와 같은 NetworkObject를 쓰며 얹혀 있다.
///
/// 구조는 SceneReadyGate(#410)와 같다. 다른 점은 <b>대기 상한을 여기서 재지 않는다</b>는 것 —
/// 상한은 RoundEndResetter의 복귀 딜레이가 그대로 쥐고 있고, 이 게이트는 그 상한을 앞당기기만 한다.
/// 복귀(App.LoadScene) 자체도 서버의 RoundEndResetter가 한다 — 클라는 보고만 한다.
///
/// 세션 없이 씬을 직접 Play하면(테스트 씬·솔로) 보고할 서버가 없다 — 그때는 로컬 확인 하나를
/// 그대로 1/1로 센다. 오프라인에서도 확인하면 바로 넘어가고, 표시도 어긋나지 않는다.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class SettlementConfirmGate : NetworkedManagerBase
{
    // 표시용 진행도 — 정산 카운트다운의 "(2/4 확인)"
    private readonly NetworkVariable<int> m_confirmedCountSynced = new NetworkVariable<int>();
    private readonly NetworkVariable<int> m_expectedCountSynced = new NetworkVariable<int>();

    // 서버 전용 — 기다릴 대상과 확인을 마친 대상. 불변식: m_confirmed ⊆ m_expected
    private readonly HashSet<ulong> m_expected = new HashSet<ulong>();
    private readonly HashSet<ulong> m_confirmed = new HashSet<ulong>();

    // 오프라인 폴백 — 세션이 없을 때의 로컬 확인 여부
    private bool m_offlineConfirmed;

    /// <summary>표시 인원이 바뀌었다 — 카운트다운 문구를 다시 그리는 신호. (전 피어)</summary>
    public event Action OnCountsChanged;

    /// <summary>확인을 보고한 인원 수 (표시용). 전 피어 읽기 가능.</summary>
    public int ConfirmedCount =>
        IsSpawned ? m_confirmedCountSynced.Value
        : m_offlineConfirmed ? 1
        : 0;

    /// <summary>기다리는 총 인원 수 (표시용). 전 피어 읽기 가능.</summary>
    public int ExpectedCount => IsSpawned ? m_expectedCountSynced.Value : 1;

    /// <summary>전원이 확인했는가 — 서버가 이 값을 보고 남은 복귀 대기를 건너뛴다.</summary>
    public bool AllConfirmed => ExpectedCount > 0 && ConfirmedCount >= ExpectedCount;

    public override void OnNetworkSpawn()
    {
        // 표시용 수치는 전 피어가 본다 — 서버가 값을 바꾸면 각자 문구를 다시 그린다
        m_confirmedCountSynced.OnValueChanged += HandleCountSynced;
        m_expectedCountSynced.OnValueChanged += HandleCountSynced;

        if (!IsServer)
            return;

        // 같은 실행에서 세션을 다시 만들면 씬 오브젝트에 이전 세션 값이 남는다 (SceneReadyGate와 같은 방침)
        m_expected.Clear();
        m_confirmed.Clear();

        foreach (ulong clientId in NetworkManager.ConnectedClientsIds)
            m_expected.Add(clientId);

        NetworkManager.OnClientDisconnectCallback += HandleClientDisconnected;

        PublishCounts();
    }

    public override void OnNetworkDespawn()
    {
        m_confirmedCountSynced.OnValueChanged -= HandleCountSynced;
        m_expectedCountSynced.OnValueChanged -= HandleCountSynced;

        if (IsServer && NetworkManager != null)
            NetworkManager.OnClientDisconnectCallback -= HandleClientDisconnected;
    }

    /// <summary>
    /// 이 피어의 정산 확인을 서버에 보고한다 — 정산 패널을 확인·ESC로 닫을 때 부른다.
    /// 호스트도 자기 몫이 있으므로 서버·클라 구분 없이 부른다.
    /// </summary>
    public void ReportSelfConfirmed()
    {
        if (!IsSpawned)
        {
            // 오프라인 — 보고할 서버가 없다. 로컬 확인만 기억하고 표시를 갱신한다
            if (m_offlineConfirmed)
                return;
            m_offlineConfirmed = true;
            OnCountsChanged?.Invoke();
            return;
        }

        ReportConfirmedRpc();
    }

    // InvokePermission = Everyone 명시 — 씬에 놓인 서버 소유 오브젝트라 어떤 플레이어도 오너가 아니다.
    // 기본값(오너 전용)이면 클라가 보고할 수 없다. (SceneReadyGate.ReportReadyRpc와 같은 이유)
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void ReportConfirmedRpc(RpcParams rpcParams = default)
    {
        // 보낸 값을 믿지 않고 서버가 본 발신자를 쓴다 — 남의 확인을 대신 보고하지 못하게
        ulong clientId = rpcParams.Receive.SenderClientId;

        // 스폰 뒤에 들어온 피어의 보고 — 기다리진 않았지만 표시 인원에는 넣는다 (SceneReadyGate와 동일)
        m_expected.Add(clientId);
        m_confirmed.Add(clientId);
        PublishCounts();
    }

    // 끊긴 피어는 기다릴 대상에서 뺀다 — 안 그러면 남은 인원이 상한까지 갇힌다
    private void HandleClientDisconnected(ulong clientId)
    {
        m_expected.Remove(clientId);
        m_confirmed.Remove(clientId);
        PublishCounts();
    }

    // 서버 전용 — 호출부가 전부 서버 경로다. 판정(AllConfirmed)은 이 수치를 그대로 읽는다.
    private void PublishCounts()
    {
        m_confirmedCountSynced.Value = m_confirmed.Count;
        m_expectedCountSynced.Value = m_expected.Count;
    }

    private void HandleCountSynced(int previous, int current) => OnCountsChanged?.Invoke();
}
