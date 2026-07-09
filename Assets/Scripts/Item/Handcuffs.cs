using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
// using Unity.Netcode; // TODO: 네트워크 테스트 시 주석 해제

/// <summary>
/// 수갑 아이템. 3초 채널링 후 대상을 구속하고 로그를 출력한다. (GDD 8-2)
/// 배터리 등 자원 소모는 없다.
/// </summary>
public class Handcuffs : ItemBase
{
    [Header("수갑 설정")]
    [SerializeField]
    private float m_channelSeconds = 3f;

    private bool m_isRestraining;
    private CancellationTokenSource m_cts;

    // ---- ItemBase ----

    // TODO: 네트워크 테스트 시 서버 권위로 재검증 (클라 CanUse 결과는 신뢰 불가)
    /// <summary>구속 중이 아닐 때만 사용 가능.</summary>
    public override bool CanUse() => !m_isRestraining;

    // TODO: 네트워크 테스트 시 서버 권위로 실행 (오너 입력 → ServerRpc 요청 → 서버가 채널링/구속 실행 후 결과 동기화)
    public override void Use()
    {
        if (!CanUse())
        {
            return;
        }

        RestrainAsync().Forget();
    }

    // ---- 구속 채널링 ----

    // TODO: 네트워크 테스트 시 채널링 타이밍을 서버 권위로 (클라 시간 조작 방지). 구속 결과는 ClientRpc/NetworkVariable로 전파
    private async UniTaskVoid RestrainAsync()
    {
        m_isRestraining = true;
        m_cts = new CancellationTokenSource();

        try
        {
            await UniTask.Delay(TimeSpan.FromSeconds(m_channelSeconds), cancellationToken: m_cts.Token);

            Debug.Log("NPC 구속됨");
        }
        catch (OperationCanceledException)
        {
            Debug.Log("구속 취소됨");
        }
        finally
        {
            m_isRestraining = false;
            m_cts?.Dispose();
            m_cts = null;
        }
    }

    /// <summary>진행 중인 구속 채널링을 취소한다. (이동·피격 등 방해 시 호출)</summary>
    public void CancelRestrain() => m_cts?.Cancel();

    // ---- 라이프사이클 ----

    // TODO: 네트워크 테스트 시 OnNetworkDespawn에서도 취소 처리 추가
    private void OnDisable()
    {
        CancelRestrain();
    }

    private void OnDestroy()
    {
        m_cts?.Cancel();
        m_cts?.Dispose();
    }
}
