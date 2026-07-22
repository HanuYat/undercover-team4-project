using UnityEngine;

/// <summary>
/// 밧줄 아이템 — 테이저로 기절(Stunned)시킨 NPC를 묶어 누운 채 질질 끌고 다닌다. (#269)
/// 좌클릭으로 겨냥한 기절 NPC에 끌기를 요청한다. 실제 사거리·대상·끌기 판정은 서버 권위이며
/// PlayerEscorter가 수행한다 (수갑과 동일한 허브 패턴, #59/#118).
/// 끌기 중엔 손이 묶여 다른 아이템을 쓸 수 없고, 수갑 연행과 동시에 진행되지 않는다(한 번에 1명).
/// </summary>
public class Rope : ItemBase
{
    /// <summary>이 밧줄을 든 플레이어의 연행 허브 — 끌기 요청을 서버로 넘긴다. (Handcuffs와 동일 관례, #88)</summary>
    private PlayerEscorter Escorter => GetComponentInParent<PlayerEscorter>();

    public override bool CanUse() => true;

    public override void Use(GameObject aimTarget)
    {
        PlayerEscorter escorter = Escorter;
        if (escorter == null)
        {
            Debug.LogWarning("Rope: PlayerEscorter를 찾지 못함 — 끌기 불가", this);
            return;
        }

        NpcController target = ResolveTarget(aimTarget);
        if (target == null)
        {
            Debug.Log("밧줄로 묶을 대상이 없음 (NPC를 겨냥하지 않음)");
            return;
        }

        // 대상 판정은 NpcStateRules 단일 기준 — 서버 가드·조준 피드백과 동일 (#184)
        if (!NpcStateRules.IsRopeable(target.CurrentState))
        {
            Debug.Log("기절한 대상만 밧줄로 묶을 수 있음");
            return;
        }

        escorter.RequestRopeDrag(target);
    }

    /// <summary>Use()의 조기 검증과 동일 기준 — 조준 피드백(윤곽선)용. (#184)</summary>
    public override bool CanTarget(GameObject aimTarget)
    {
        NpcController target = ResolveTarget(aimTarget);
        if (target == null || !NpcStateRules.IsRopeable(target.CurrentState))
            return false;

        PlayerEscorter escorter = Escorter;
        return escorter != null && !escorter.IsBusy; // 연행/끌기 중이면 불가 (한 번에 1명)
    }

    private static NpcController ResolveTarget(GameObject aimTarget)
    {
        if (aimTarget == null)
            return null;
        return aimTarget.GetComponentInParent<NpcController>();
    }
}
