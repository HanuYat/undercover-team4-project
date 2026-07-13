using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 수갑 아이템. 사용 시 겨냥한 NPC(레이캐스트 타겟)를 대상으로 잡아 체포를 요청한다. (GDD 8-2, 이슈 #36/#35)
/// 실제 채널링·사거리·반응 판정·연행은 서버 권위이며 PlayerEscorter가 수행한다 (#118).
/// 이 컴포넌트는 오너 클라의 "의도"만 담당한다 — 대상을 해석해 PlayerEscorter에 요청을 넘긴다.
/// 배터리 등 자원 소모는 없다.
/// </summary>
public class Handcuffs : ItemBase
{
    [Header("수갑 설정")]
    [SerializeField]
    private float m_channelSeconds = 3f;

    [Header("체포 판정")]
    [Tooltip("채널링 도중 대상이 이 거리(m)를 벗어나면 체포 실패로 처리한다")]
    [SerializeField]
    private float m_captureRange = 2.5f;

    private bool m_isRestraining;
    private CancellationTokenSource m_cts;

    /// <summary>
    /// 이 수갑을 든 플레이어의 연행 관리자 — 체포 성공 시 연행을 시작한다 (#59).
    /// 아이템이 독립 NetworkObject가 되어(#88) 줍기/버리기로 부모가 바뀌므로 캐시하지 않고
    /// 사용 시점마다 부모 계층에서 해석한다 (버려진 상태에선 부모가 없어 null).
    /// </summary>
    private PlayerEscorter Escorter => GetComponentInParent<PlayerEscorter>();

    // ---- ItemBase ----

    /// <summary>수갑은 언제나 사용 시도 가능 — 실제 가부(채널링 중복 등)는 서버가 판정한다.</summary>
    public override bool CanUse() => true;

    public override void Use(GameObject aimTarget)
    {
        if (m_escorter == null)
        {
            Debug.LogWarning("Handcuffs: PlayerEscorter를 찾지 못함 — 검거 불가", this);
            return;
        }

        // 연행 중이면 이번 입력은 "놓기" — NPC는 그 자리에서 체포 상태로 멈춘다 (#59)
        PlayerEscorter escorter = Escorter;
        if (escorter != null && escorter.IsEscorting)
        {
            escorter.Release();
            return;
        }

        // 겨냥한 대상에서 NPC를 조회한다 (#35). 대상이 없거나 NPC가 아니면 요청 자체를 보내지 않는다.
        NpcController target = ResolveTarget(aimTarget);
        if (target == null)
        {
            Debug.Log("체포할 대상이 없음 (NPC를 겨냥하지 않음)");
            return;
        }

        // 연행 중인 NPC는 대상에서 제외 — 중복 연행·타인의 연행 가로채기 방지 (#59)
        if (target.CurrentState == NpcState.Escorted)
        {
            Debug.Log("이미 연행 중인 대상 — 체포 불가");
            return;
        }

        // 이미 체포되어 멈춰 있는 대상은 채널링 없이 즉시 재연행한다 (#59)
        // 상태는 반드시 동기화된 CurrentState로 읽는다 — StateMachine 값은 서버에서만 갱신됨 (#56)
        if (target.CurrentState == NpcState.Captured)
        {
            escorter?.StartEscort(target);
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

            // 채널링 성공 순간 대상의 반응이 갈린다 (GDD 6-1, #76).
            // 기절 중인 대상은 반응하지 못하고 그대로 연행된다 (테이저 연결고리).
            PlayerEscorter escorter = Escorter;
            ReactionType reaction = ResolveReaction(target);
            switch (reaction)
            {
                case ReactionType.Flee:
                    // 뿌리치고 도주 — 근접 제압 홀드 또는 테이저(후속)로만 잡힌다
                    Debug.Log($"체포 실패 — 뿌리치고 도주: {target.name}");
                    target.StartFlee(escorter != null ? escorter.transform : transform);
                    break;

                case ReactionType.Resist:
                    // 그 자리에서 저항 — 제압 게이지를 깎아야 체포된다
                    Debug.Log($"체포 실패 — 저항 시작: {target.name}");
                    target.StartResist();
                    break;

                default:
                    Debug.Log($"NPC 구속됨: {target.name}");
                    // 체포 성공 즉시 연행 시작 — 플레이어를 따라온다 (#59).
                    // 에스코터가 없는 구성(테스트 등)에서는 기존처럼 그 자리에서 체포 상태 유지
                    if (escorter != null)
                        escorter.StartEscort(target);
                    else
                        target.StateMachine.ChangeState(NpcState.Captured);
                    break;
            }
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

    /// <summary>
    /// 채널링 성공 순간의 반응을 정한다. (#76)
    /// 기절(Stunned) 중이거나 신원이 없으면 반응 없이 순응 취급 — 즉시 연행된다.
    /// </summary>
    private static ReactionType ResolveReaction(NpcController target)
    {
        if (target.CurrentState == NpcState.Stunned)
            return ReactionType.Compliant;

        CitizenIdentity identity = target.GetComponent<CitizenIdentity>();
        return identity != null ? identity.Reaction : ReactionType.Compliant;
    }

    // ---- 대상 탐색 ----

    /// <summary>
    /// 겨냥한 대상 GameObject에서 NPC를 조회한다. NPC(NpcController)가 아니면 null.
    /// 콜라이더가 NPC 루트의 자식일 수 있으므로 부모까지 탐색한다 (Scanner.ResolveProfile과 동일 관례, #34/#35).
    /// </summary>
    private static NpcController ResolveTarget(GameObject aimTarget)
    {
        if (aimTarget == null)
        {
            return null;
        }

        return aimTarget.GetComponentInParent<NpcController>();
    }

    // ---- 라이프사이클 ----

    public override void OnNetworkDespawn()
    {
        CancelRestrain();
    }

    private void OnDisable()
    {
        // 장착 해제·비활성 시 진행 중인 서버 채널링도 취소 요청
        CancelRestrain();
    }

    public override void OnDestroy()
    {
        m_cts?.Cancel();
        m_cts?.Dispose();
        base.OnDestroy();
    }
}
