using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

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

    private int m_currentBattery;
    private bool m_isScanning;
    private CancellationTokenSource m_cts;

    // ---- IChargeable ----

    public int CurrentBattery => m_currentBattery;
    public int MaxBattery => m_maxBattery;
    public bool IsFullyCharged => m_currentBattery >= m_maxBattery;
    public bool IsDepleted => m_currentBattery <= 0;

    public event Action<int> OnCharged;

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

    /// <summary>스캔 중이 아니고 배터리가 남아 있을 때만 사용 가능.</summary>
    public override bool CanUse() => !m_isScanning && !IsDepleted;

    public override void Use()
    {
        if (!CanUse())
        {
            return;
        }

        ScanAsync().Forget();
    }

    // ---- 스캔 채널링 ----

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

        return $"이름={m_targetProfile.m_citizenName}, 타입={m_targetProfile.m_typeView}, 세력={m_targetProfile.m_factionView}";
    }

    // ---- 라이프사이클 ----

    private void Awake()
    {
        m_currentBattery = m_maxBattery;
    }

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
