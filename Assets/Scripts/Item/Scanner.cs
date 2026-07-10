using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
// using Unity.Netcode; // TODO: 네트워크 테스트 시 주석 해제

/// <summary>
/// 스캐너 아이템. 3초 채널링 후 대상 시민의 스캔 정보를 로그로 출력한다.
/// 배터리 충전식(IChargeable)이며, 스캔 1회당 배터리를 1 소모한다. (GDD 5-1/5-2)
/// </summary>
public class Scanner : ItemBase, IChargeable
{
    [Header("스캐너 설정")]
    [SerializeField]
    private float m_channelSeconds = 3f;

    [SerializeField]
    private int m_maxBattery = 5;

    [Header("스캔 대상 (프로토타입 테스트용)")]
    [SerializeField]
    private CitizenProfile m_targetProfile;

    // TODO: 네트워크 테스트 시 m_currentBattery를 NetworkVariable<int>로 교체 (지금은 로컬 값이라 다른 클라에 동기화 안 됨)
    private int m_currentBattery;
    private bool m_isScanning;
    private CancellationTokenSource m_cts;

    // ---- IChargeable ----

    public int CurrentBattery => m_currentBattery;
    public int MaxBattery => m_maxBattery;
    public bool IsFullyCharged => m_currentBattery >= m_maxBattery;
    public bool IsDepleted => m_currentBattery <= 0;

    // TODO: 네트워크 테스트 시 OnCharged를 NetworkVariable.OnValueChanged로 구동 (전 클라 UI 갱신)
    public event Action<int> OnCharged;

    // TODO: 네트워크 테스트 시 서버 권위로만 호출 (본부 충전기 → ServerRpc 요청 → 서버가 배터리 변경)
    public void Charge(int amount)
    {
        if (amount <= 0)
        {
            return;
        }

        m_currentBattery = Mathf.Min(m_currentBattery + amount, m_maxBattery);
        OnCharged?.Invoke(m_currentBattery);
    }

    // ---- ItemBase ----

    // TODO: 네트워크 테스트 시 서버 권위로 재검증 (클라 CanUse 결과는 신뢰 불가)
    /// <summary>스캔 중이 아니고 배터리가 남아 있을 때만 사용 가능.</summary>
    public override bool CanUse() => !m_isScanning && !IsDepleted;

    // TODO: 네트워크 테스트 시 서버 권위로 실행 (오너 입력 → ServerRpc 요청 → 서버가 스캔 실행/검증 후 결과 동기화)
    public override void Use()
    {
        if (!CanUse())
        {
            return;
        }

        ScanAsync().Forget();
    }

    // ---- 스캔 채널링 ----

    // TODO: 네트워크 테스트 시 채널링 타이밍/배터리 소모를 서버 권위로 (클라 시간 조작 방지). 스캔 결과는 ClientRpc/NetworkVariable로 전파
    private async UniTaskVoid ScanAsync()
    {
        m_isScanning = true;
        m_cts = new CancellationTokenSource();

        try
        {
            await UniTask.Delay(TimeSpan.FromSeconds(m_channelSeconds), cancellationToken: m_cts.Token);

            m_currentBattery = Mathf.Max(m_currentBattery - 1, 0);
            Debug.Log($"NPC 스캔됨: {GetScanInfo()}");
        }
        catch (OperationCanceledException)
        {
            Debug.Log("스캔 취소됨");
        }
        finally
        {
            m_isScanning = false;
            m_cts?.Dispose();
            m_cts = null;
        }
    }

    /// <summary>진행 중인 스캔 채널링을 취소한다. (이동·피격 등 방해 시 호출)</summary>
    public void CancelScan() => m_cts?.Cancel();

    private string GetScanInfo()
    {
        if (m_targetProfile == null)
        {
            return "대상 정보 없음";
        }

        return $"이름={m_targetProfile.CitizenName}, 타입={m_targetProfile.m_typeView}, 세력={m_targetProfile.m_factionView}";
    }

    // ---- 라이프사이클 ----

    // TODO: 네트워크 테스트 시 배터리 초기화를 서버의 OnNetworkSpawn으로 이동 (NetworkVariable은 서버가 초기화)
    private void Awake()
    {
        m_currentBattery = m_maxBattery;
    }

    // TODO: 네트워크 테스트 시 OnNetworkDespawn에서도 취소 처리 추가
    private void OnDisable()
    {
        CancelScan();
    }

    private void OnDestroy()
    {
        m_cts?.Cancel();
        m_cts?.Dispose();
    }
}
