using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
// using Unity.Netcode; // TODO: 네트워크 테스트 시 주석 해제

/// <summary>
/// 수갑 아이템. 사용 시 주변에서 가장 가까운 NPC를 대상으로 잡아
/// 3초 채널링 후 대상 FSM을 Captured로 전이시킨다. (GDD 8-2, 이슈 #36)
/// 배터리 등 자원 소모는 없다.
/// </summary>
public class Handcuffs : ItemBase
{
    [Header("수갑 설정")]
    [SerializeField]
    private float m_channelSeconds = 3f;

    [Header("체포 판정")]
    [Tooltip("이 반경(m) 안에 있는 NPC만 체포 대상으로 잡는다")]
    [SerializeField]
    private float m_captureRange = 2.5f;

    private bool m_isRestraining;
    private CancellationTokenSource m_cts;
    private PlayerEscorter m_escorter;

    private void Awake()
    {
        // 이 수갑을 든 플레이어의 연행 관리자 — 체포 성공 시 연행을 시작한다 (#59)
        m_escorter = GetComponentInParent<PlayerEscorter>();
    }

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

        // 연행 중이면 이번 입력은 "놓기" — NPC는 그 자리에서 체포 상태로 멈춘다 (#59)
        if (m_escorter != null && m_escorter.IsEscorting)
        {
            m_escorter.Release();
            return;
        }

        // 대상이 없으면 채널링 자체를 시작하지 않는다
        NpcController target = FindTarget();
        if (target == null)
        {
            Debug.Log("체포할 대상이 근처에 없음");
            return;
        }

        // 이미 체포되어 멈춰 있는 대상은 채널링 없이 즉시 재연행한다 (#59)
        // 상태는 반드시 동기화된 CurrentState로 읽는다 — StateMachine 값은 서버에서만 갱신됨 (#56)
        if (target.CurrentState == NpcState.Captured)
        {
            m_escorter?.StartEscort(target);
            return;
        }

        RestrainAsync(target).Forget();
    }

    // ---- 구속 채널링 ----

    // TODO: 네트워크 테스트 시 채널링 타이밍을 서버 권위로 (클라 시간 조작 방지). 구속 결과는 ClientRpc/NetworkVariable로 전파
    private async UniTaskVoid RestrainAsync(NpcController target)
    {
        m_isRestraining = true;
        m_cts = new CancellationTokenSource();

        try
        {
            await UniTask.Delay(TimeSpan.FromSeconds(m_channelSeconds), cancellationToken: m_cts.Token);

            // 채널링 동안 대상이 파괴됐거나 사거리를 벗어났으면 실패 — 도주형 NPC 대응 (GDD 6장)
            if (target == null || !IsInRange(target))
            {
                Debug.Log("구속 실패 — 대상이 범위를 벗어남");
                return;
            }

            Debug.Log($"NPC 구속됨: {target.name}");

            // 체포 성공 즉시 연행 시작 — 플레이어를 따라온다 (#59).
            // 에스코터가 없는 구성(테스트 등)에서는 기존처럼 그 자리에서 체포 상태 유지
            if (m_escorter != null)
                m_escorter.StartEscort(target);
            else
                target.StateMachine.ChangeState(NpcState.Captured);
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

    // ---- 대상 탐색 ----

    /// <summary>사거리 안에서 가장 가까운, 아직 체포되지 않은 NPC를 찾는다.</summary>
    private NpcController FindTarget()
    {
        Collider[] hits = Physics.OverlapSphere(transform.position, m_captureRange);
        NpcController nearest = null;
        float nearestSqr = float.MaxValue;

        foreach (Collider hit in hits)
        {
            NpcController npc = hit.GetComponentInParent<NpcController>();
            if (npc == null)
                continue;

            // 연행 중인 NPC는 대상에서 제외 — 중복 연행·타인의 연행 가로채기 방지 (#59)
            // (Captured는 재연행 대상이므로 포함한다)
            if (npc.CurrentState == NpcState.Escorted)
                continue;

            float sqr = (npc.transform.position - transform.position).sqrMagnitude;
            if (sqr < nearestSqr)
            {
                nearestSqr = sqr;
                nearest = npc;
            }
        }

        return nearest;
    }

    private bool IsInRange(NpcController target)
    {
        return (target.transform.position - transform.position).sqrMagnitude
            <= m_captureRange * m_captureRange;
    }

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
