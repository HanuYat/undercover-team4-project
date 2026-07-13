using UnityEngine;

/// <summary>
/// 수갑 아이템. 사용 시 겨냥한 NPC(레이캐스트 타겟)를 대상으로 잡아 체포를 요청한다. (GDD 8-2, 이슈 #36/#35)
/// 실제 채널링·사거리·반응 판정·연행은 서버 권위이며 PlayerEscorter가 수행한다 (#118).
/// 이 컴포넌트는 오너 클라의 "의도"만 담당한다 — 대상을 해석해 PlayerEscorter에 요청을 넘긴다.
/// (채널링을 여기서 직접 돌리면 서버 가드에 막혀 클라 검거가 조용히 실패한다 — #88 머지 회귀의 원인)
/// 배터리 등 자원 소모는 없다.
/// </summary>
public class Handcuffs : ItemBase
{
    /// <summary>
    /// 이 수갑을 든 플레이어의 검거·연행 관리자 — 체포/연행 요청을 서버로 넘긴다 (#59/#118).
    /// 아이템이 독립 NetworkObject가 되어(#88) 줍기/버리기로 부모가 바뀌므로 캐시하지 않고
    /// 사용 시점마다 부모 계층에서 해석한다 (버려진 상태에선 부모가 없어 null).
    /// </summary>
    private PlayerEscorter Escorter => GetComponentInParent<PlayerEscorter>();

    // ---- ItemBase ----

    /// <summary>수갑은 언제나 사용 시도 가능 — 실제 가부(채널링 중복 등)는 서버가 판정한다.</summary>
    public override bool CanUse() => true;

    public override void Use(GameObject aimTarget)
    {
        PlayerEscorter escorter = Escorter;
        if (escorter == null)
        {
            Debug.LogWarning("Handcuffs: PlayerEscorter를 찾지 못함 — 검거 불가", this);
            return;
        }

        // 연행 중이면 이번 입력은 "놓기" — NPC는 그 자리에서 체포 상태로 멈춘다 (#59)
        if (escorter.IsEscorting)
        {
            escorter.RequestRelease();
            return;
        }

        // 겨냥한 대상에서 NPC를 조회한다 (#35). 대상이 없거나 NPC가 아니면 요청 자체를 보내지 않는다.
        NpcController target = ResolveTarget(aimTarget);
        if (target == null)
        {
            Debug.Log("체포할 대상이 없음 (NPC를 겨냥하지 않음)");
            return;
        }

        // 대상 상태 판정(즉시 재연행/채널링/사거리)과 반응 판정은 서버가 수행한다 — 여기서는 요청만 넘긴다.
        escorter.RequestCapture(target);
    }

    /// <summary>진행 중인 구속 채널링을 취소한다. (이동·피격 등 방해 시 호출) — 서버 채널링에 취소를 요청한다.</summary>
    public void CancelRestrain() => Escorter?.CancelCapture();

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
        // 디스폰(버리기·파괴) 시 진행 중인 서버 채널링도 취소 요청 (#88)
        CancelRestrain();
    }

    private void OnDisable()
    {
        // 장착 해제·비활성 시 진행 중인 서버 채널링도 취소 요청
        CancelRestrain();
    }
}
