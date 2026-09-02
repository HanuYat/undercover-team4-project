using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 힐팩 — 사용자의 체력을 회복시키는 아이템.
/// </summary>
public class HealPack : ItemBase
{
    private const int k_healAmount = 50; // 회복량

    public override void Use(GameObject target)
    {
        if (HolderHealth == null)
        {
            Debug.LogWarning("힐팩 사용 실패: 체력 컴포넌트를 찾을 수 없음", this);
            return;
        }

        if (HolderHealth.CurrentHp >= HolderHealth.MaxHp)
        {
            Debug.Log("힐팩 사용 실패: 체력이 이미 최대임", this);
            return;
        }

        if (HasServerAuthority)
        {
            ServerTryHeal();
            return;
        }

        if (!IsOwner) return;   // 남의 아이템에서 온 호출 방지

        RequestHealRpc();
    }

    private PlayerHealth HolderHealth   // 이 아이템을 든 플레이어의 체력 컴포넌트.
    {
        get
        {
            PlayerInteractor holder = Holder;
            return holder != null ? holder.GetComponent<PlayerHealth>() : null;
        }
    }

    private void ServerTryHeal()
    {
        if (!HasServerAuthority) return;

        if (HolderHealth == null)
        {
            Debug.LogWarning("힐팩 사용 실패: 체력 컴포넌트를 찾을 수 없음", this);
            return;
        }

        if (HolderHealth.CurrentHp >= HolderHealth.MaxHp)
        {
            Debug.Log("힐팩 사용 실패: 체력이 이미 최대임", this);
            return;
        }

        HolderHealth?.ModifyHp(k_healAmount);
        ServerConsume();
    }

    [Rpc(SendTo.Server)]
    private void RequestHealRpc()
    {
        ServerTryHeal();
    }
}
