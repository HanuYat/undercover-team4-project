using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 플레이어의 검거·연행 서버 권위 허브. (#59, #56/#118 네트워크 전환)
/// 오너 클라의 아이템/상호작용(Handcuffs·NpcSubdueInteractable)이 이 컴포넌트의 요청 API를 호출하면,
/// 요청을 서버로 넘겨(ServerRpc) 서버가 채널링·사거리·반응 판정을 실행한다.
/// 그 결과 NpcController 상태 변경은 서버에서 일어나고 NetworkVariable로 전 피어에 동기화된다.
/// 한 번에 1명만 연행 가능 (동시 1명 제약).
/// </summary>
public class PlayerEscorter : NetworkBehaviour
{
    [Header("수갑 채널링 (서버 권위)")]
    [Tooltip("체포 채널링 시간(초)")]
    [SerializeField] private float m_channelSeconds = 3f;
    [Tooltip("채널링 시작/완료 시 대상이 이 거리(m)를 벗어나면 실패")]
    [SerializeField] private float m_captureRange = 2.5f;

    /// <summary>지금 연행 중인 NPC. 없으면 null. 서버(또는 오프라인)에서만 유효.</summary>
    public NpcController EscortingNpc { get; private set; }

    public bool IsEscorting => EscortingNpc != null;

    // 서버에서 진행 중인 채널링 취소 토큰 — 오너가 이동/뗌으로 취소하거나 대상이 사라지면 끊는다
    private CancellationTokenSource m_channelCts;
    private bool m_isChanneling; // 서버 기준 채널링 진행 여부(중복 시작 방지)

    // ---- 오너 클라 진입점 (아이템/상호작용이 호출) ----

    /// <summary>체포 시도 — 오너가 호출. 서버/오프라인은 즉시 실행, 원격 클라는 서버로 요청을 넘긴다.</summary>
    public void RequestCapture(NpcController target)
    {
        if (target == null)
            return;

        // 서버(호스트 포함)·오프라인은 로컬 NpcController 참조로 바로 실행 — 네트워크 직렬화 불필요.
        // (호스트는 이미 대상을 들고 있어 RPC가 불필요하고, NetworkObjectReference는 스폰된 대상에서만
        //  생성 가능해 스폰 안 된 NPC를 넘기면 예외가 난다 — 이 우회가 호스트 검거 회귀를 막는다, #118)
        if (!IsSpawned || IsServer)
        {
            ServerBeginCapture(target);
            return;
        }
        if (!IsOwner)
            return; // 남의 플레이어 오브젝트에서 온 호출 방지

        if (!IsTargetNetworkReady(target))
            return;
        CaptureRequestRpc(new NetworkObjectReference(target.NetworkObject));
    }

    /// <summary>채널링 취소 — 오너가 호출(이동·뗌 등).</summary>
    public void CancelCapture()
    {
        if (!IsSpawned) { ServerCancelCapture(); return; }
        if (!IsOwner) return;
        CancelCaptureRpc();
    }

    /// <summary>연행 놓기 — 오너가 호출.</summary>
    public void RequestRelease()
    {
        if (!IsSpawned) { Release(); return; }
        if (!IsOwner) return;
        ReleaseRpc();
    }

    /// <summary>도주 NPC 근접 제압 — 오너가 호출.</summary>
    public void RequestSubdueCapture(NpcController target)
    {
        if (target == null) return;
        if (!IsSpawned || IsServer) { ServerSubdueCapture(target); return; } // 서버/오프라인 즉시 실행
        if (!IsOwner) return;
        if (!IsTargetNetworkReady(target)) return;
        SubdueCaptureRpc(new NetworkObjectReference(target.NetworkObject));
    }

    // 원격 클라 → 서버로 대상을 넘기려면 스폰돼 있어야 한다(NetworkObjectReference 제약).
    // 스폰 안 된 NPC(씬 배치 후 미스폰 등)면 참조 생성이 예외를 던지므로 미리 걸러 경고만 남긴다.
    private bool IsTargetNetworkReady(NpcController target)
    {
        if (target.NetworkObject != null && target.NetworkObject.IsSpawned)
            return true;
        Debug.LogWarning($"검거/제압 요청 무시 — 대상 NPC가 네트워크 스폰되지 않음: {target.name}", this);
        return false;
    }

    // ---- 서버 RPC (오너 → 서버) ----
    // NGO 2.x 유니버설 RPC: 오너가 자기 플레이어 오브젝트에서 서버로 보내므로 소유권 문제 없음

    [Rpc(SendTo.Server)]
    private void CaptureRequestRpc(NetworkObjectReference targetRef)
    {
        if (targetRef.TryGet(out NetworkObject targetObj) &&
            targetObj.TryGetComponent(out NpcController target))
        {
            ServerBeginCapture(target);
        }
    }

    [Rpc(SendTo.Server)]
    private void CancelCaptureRpc() => ServerCancelCapture();

    [Rpc(SendTo.Server)]
    private void ReleaseRpc() => Release();

    [Rpc(SendTo.Server)]
    private void SubdueCaptureRpc(NetworkObjectReference targetRef)
    {
        if (targetRef.TryGet(out NetworkObject targetObj) &&
            targetObj.TryGetComponent(out NpcController target))
        {
            ServerSubdueCapture(target);
        }
    }

    // ---- 서버 실행 (권위) ----

    /// <summary>체포 진입 — 대상 상태에 따라 즉시 연행 또는 채널링 시작. 서버(또는 오프라인)에서만 실행.</summary>
    private void ServerBeginCapture(NpcController target)
    {
        if (m_isChanneling)
            return; // 중복 채널링 방지
        if (IsEscorting)
        {
            // 이미 연행 중이면 이번 입력은 "놓기"
            Release();
            return;
        }
        if (target.CurrentState == NpcState.Escorted)
            return; // 이미(타인이) 연행 중 — 가로채기 방지
        if (!IsInRange(target))
            return; // 사거리 밖이면 시작조차 안 함

        // 이미 체포되어 멈춰 있는 대상은 채널링 없이 즉시 재연행
        if (target.CurrentState == NpcState.Captured)
        {
            StartEscort(target);
            return;
        }

        ServerChannelAsync(target).Forget();
    }

    private async UniTaskVoid ServerChannelAsync(NpcController target)
    {
        m_isChanneling = true;
        m_channelCts = new CancellationTokenSource();
        try
        {
            await UniTask.Delay(TimeSpan.FromSeconds(m_channelSeconds), cancellationToken: m_channelCts.Token);

            // 채널링 동안 대상이 파괴됐거나 사거리를 벗어났으면 실패 — 도주형 NPC 대응 (GDD 6장)
            if (target == null || !IsInRange(target))
            {
                Debug.Log("구속 실패 — 대상이 범위를 벗어남");
                return;
            }

            // 채널링 성공 순간 반응 판정 (GDD 6-1, #76)
            ReactionType reaction = ResolveReaction(target);
            switch (reaction)
            {
                case ReactionType.Flee:
                    // 뿌리치고 도주 — 근접 제압 홀드 또는 테이저(후속)로만 잡힌다
                    Debug.Log($"체포 실패 — 뿌리치고 도주: {target.name}");
                    target.StartFlee(transform); // 이 플레이어(서버측 transform)로부터 도주
                    break;

                case ReactionType.Resist:
                    // 그 자리에서 저항 — 제압 게이지를 깎아야 체포된다
                    Debug.Log($"체포 실패 — 저항 시작: {target.name}");
                    target.StartResist();
                    break;

                default:
                    // 체포 성공 → 이 플레이어를 따라 연행 (#59)
                    Debug.Log($"NPC 구속됨: {target.name}");
                    StartEscort(target);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            Debug.Log("구속 취소됨");
        }
        finally
        {
            m_isChanneling = false;
            m_channelCts?.Dispose();
            m_channelCts = null;
        }
    }

    private void ServerCancelCapture() => m_channelCts?.Cancel();

    /// <summary>도주 NPC 근접 제압 — 서버 실행. 도주 중일 때만 그 자리에서 체포.</summary>
    private void ServerSubdueCapture(NpcController target)
    {
        if (target.CurrentState == NpcState.Run)
            target.CaptureBySubdue();
    }

    private bool IsInRange(NpcController target)
    {
        return (target.transform.position - transform.position).sqrMagnitude
            <= m_captureRange * m_captureRange;
    }

    /// <summary>채널링 성공 순간의 반응. 기절 중이거나 신원이 없으면 순응(즉시 연행) 취급. (#76)</summary>
    private static ReactionType ResolveReaction(NpcController target)
    {
        if (target.CurrentState == NpcState.Stunned)
            return ReactionType.Compliant;

        CitizenIdentity identity = target.GetComponent<CitizenIdentity>();
        return identity != null ? identity.Reaction : ReactionType.Compliant;
    }

    // ---- 서버 내부 연행 상태 조작 ----

    /// <summary>연행 시작. 이미 다른 NPC를 연행 중이면 무시된다 (동시 1명 제약). 서버(또는 오프라인) 실행.</summary>
    public void StartEscort(NpcController npc)
    {
        if (IsEscorting || npc == null)
            return;

        EscortingNpc = npc;
        npc.StartEscort(transform);
        Debug.Log($"연행 시작: {npc.name}");
    }

    /// <summary>연행 놓기 — NPC는 그 자리에서 체포 상태로 멈춘다. 다시 다가가 재연행 가능. 서버(또는 오프라인) 실행.</summary>
    public void Release()
    {
        if (!IsEscorting)
            return;

        Debug.Log($"연행 놓기: {EscortingNpc.name}");
        EscortingNpc.StopEscort();
        EscortingNpc = null;
    }

    private void Update()
    {
        // 참조 정리는 서버(또는 오프라인)에서만 — 연행 상태 자체가 서버 권위다 (#56/#118).
        // 클라이언트에서는 EscortingNpc가 서버 로직으로만 세팅되므로 여기서 건드리지 않는다.
        if (IsSpawned && !IsServer)
            return;

        // 거리 이탈 등으로 NPC 쪽에서 연행이 스스로 풀린 경우 참조를 정리한다
        if (EscortingNpc != null && EscortingNpc.CurrentState != NpcState.Escorted)
            EscortingNpc = null;
    }

    public override void OnNetworkDespawn()
    {
        ServerCancelCapture();
    }

    private void OnDestroy()
    {
        m_channelCts?.Cancel();
        m_channelCts?.Dispose();
    }
}
