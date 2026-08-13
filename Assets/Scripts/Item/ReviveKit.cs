using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 부활 키트 — 기능 정지(<see cref="IncapacitationCause.Die"/>)된 동료를 그 자리에서 <b>즉시</b>
/// 일으키는 일회용 소지형 아이템. 상점에서 산다(<see cref="ItemBase.ShopPrice"/>). (#613, GDD 7-5)
///
/// 조준하고 좌클릭하면 서버가 대상·거리를 검증하고 곧바로 <see cref="PlayerHealth.ServerRevive"/>를
/// 부른 뒤 키트를 소모한다 — <b>채널링도 대기 시간도 없다.</b> 회복 HP는 부활 경로 공통값
/// (<c>PlayerHealth.m_reviveHp</c>)을 그대로 쓴다: 부활은 하나의 규칙이고 경로마다 갈릴 이유가 없다.
///
/// 이 키트가 생기기 전 복구 경로는 본부 이송 부활(<c>HqRevivalDevice</c>, #365) 하나였다 —
/// 시체를 본부까지 끌고 가 안치하고 30초를 기다리는 길이다. 그쪽 코드는 남아 있지만 씬에 배치하지
/// 않으므로(#613 결정), 실질적인 부활 수단은 이 키트다.
///
/// 배터리(<see cref="ItemBattery"/>)는 달지 않는다 — 한 번 쓰면 사라지므로 잔량 개념이 없다.
/// 판정·소모는 전부 서버 권위다(#55): 원격 클라가 RPC를 직접 던져도 서버가 대상 상태·거리·가시선을
/// 다시 본다. 채널링이 없어 취소 경로(<see cref="ItemBase.CancelUse"/>)도 기본 구현(무동작)을 그대로 쓴다.
/// </summary>
public class ReviveKit : ItemBase
{
    // 사거리는 조준·윤곽선과 같은 기준 — PlayerInteractor.Range를 재사용한다 (#147 패턴, #184).
    // 값을 따로 두면 "윤곽선은 떴는데 서버가 거부하는" 구간이 생긴다.
    private const float k_fallbackRange = 3f; // 테스트 구성 등 PlayerInteractor가 없을 때

    /// <summary>
    /// 이 키트로 일으킬 수 있는 대상을 조준 중인지 — 윤곽선·크로스헤어 게이트. (#184)
    /// 서버 검증(<see cref="ServerTryRevive"/>)과 같은 기준을 써야 어긋나지 않는다.
    /// </summary>
    public override bool CanTarget(GameObject aimTarget) => ResolveTarget(aimTarget) != null;

    /// <summary>
    /// 사용 진입점 — 오너의 의도를 서버로 넘긴다. 대상 해석은 조기 반환·진단 로그를 위해 클라에서도
    /// 하지만, 실제 부활과 소모는 서버가 한다 (Scanner.Use와 같은 구조, #55).
    /// </summary>
    public override void Use(GameObject target)
    {
        PlayerHealth revivable = ResolveTarget(target);
        if (revivable == null)
        {
            // 살아 있는 동료·NPC·자기 자신·빈 조준이 전부 여기로 떨어진다 — 사유를 나누지 않는 이유는
            // 조준 윤곽선(CanTarget)이 이미 "쓸 수 있는 대상"을 색으로 알려주고 있기 때문이다.
            Debug.Log("부활 실패: 기능 정지된 동료를 조준해야 한다");
            return;
        }

        // 서버(호스트 포함)·오프라인은 로컬 참조로 바로 실행 (PlayerReviver.RequestBeginRevive 관례)
        if (HasServerAuthority)
        {
            ServerTryRevive(revivable);
            return;
        }

        if (!IsOwner)
            return; // 남의 아이템에서 온 호출 방지 — 서버도 RPC 소유권으로 다시 막는다

        // 원격 클라 → 서버로 대상을 넘기려면 스폰돼 있어야 한다(NetworkObjectReference 제약)
        if (revivable.NetworkObject == null || !revivable.NetworkObject.IsSpawned)
        {
            Debug.LogWarning($"부활 요청 무시 — 대상이 네트워크 스폰되지 않음: {revivable.name}", this);
            return;
        }

        RequestReviveRpc(new NetworkObjectReference(revivable.NetworkObject));
    }

    // 조준 대상에서 '기능 정지된 아군'을 찾는다. 아니면 null. (PlayerReviver.FindAllyTarget과 같은 규칙)
    // 콜라이더가 몸의 자식일 수 있으므로 부모까지 훑는다 — 쓰러진 동안 켜지는 조준 히트박스가
    // 루트의 자식이다(PlayerIncapacitation.m_reviveHitbox).
    private PlayerHealth ResolveTarget(GameObject aimTarget)
    {
        if (aimTarget == null)
            return null;

        PlayerHealth target = aimTarget.GetComponentInParent<PlayerHealth>();
        if (target == null || target == HolderHealth)
            return null; // 자기 자신에게는 쓸 수 없다

        PlayerIncapacitation targetIncapacitation = target.GetComponent<PlayerIncapacitation>();
        return targetIncapacitation != null && targetIncapacitation.IsDead ? target : null;
    }

    // 이 키트를 든 사람의 체력 컴포넌트 — 자기 자신 판정용. 바닥에 놓여 있으면 null.
    private PlayerHealth HolderHealth
    {
        get
        {
            PlayerInteractor holder = Holder;
            return holder != null ? holder.GetComponent<PlayerHealth>() : null;
        }
    }

    [Rpc(SendTo.Server)]
    private void RequestReviveRpc(NetworkObjectReference targetRef)
    {
        if (targetRef.TryGet(out NetworkObject targetObject)
            && targetObject.TryGetComponent(out PlayerHealth target))
        {
            ServerTryRevive(target);
        }
    }

    // ---- 서버 실행 (권위) ----

    /// <summary>
    /// 부활 판정과 소모 — 서버(또는 오프라인) 전용.
    /// 클라 조기검증을 그대로 다시 본다: 변조된 클라가 RPC를 직접 던지는 경로가 있기 때문이다 (#148 관례).
    /// </summary>
    private void ServerTryRevive(PlayerHealth target)
    {
        if (!HasServerAuthority || target == null)
            return;

        PlayerInteractor holder = Holder;
        if (holder == null)
        {
            // 바닥에 놓인 키트로 온 요청 — 든 사람이 없으면 사거리 기준점 자체가 없다
            Debug.LogWarning("[부활 키트] 든 사람이 없는 키트로 부활 요청이 들어왔다 — 거부", this);
            return;
        }

        if (target == holder.GetComponent<PlayerHealth>())
        {
            Debug.LogWarning($"[부활 키트] 자가 부활 시도 거부 — {holder.name}", this);
            return;
        }

        // 쓰러진 사람은 아이템을 쓸 수 없다 — 오너 입력(PlayerItemUser)이 이미 막지만 위조 RPC로 뚫린다
        PlayerIncapacitation userIncapacitation = holder.GetComponent<PlayerIncapacitation>();
        if (userIncapacitation != null && userIncapacitation.IsIncapacitated)
        {
            NotifyOwner("부활 실패 — 무력화 상태에서는 키트를 쓸 수 없다");
            return;
        }

        PlayerIncapacitation targetIncapacitation = target.GetComponent<PlayerIncapacitation>();
        if (targetIncapacitation == null || !targetIncapacitation.IsDead)
        {
            // 살아 있는 동료, 또는 그 사이 다른 키트로 먼저 일어난 대상 — 키트는 소모하지 않는다
            NotifyOwner($"부활 실패 — {target.name}은 기능 정지 상태가 아니다");
            return;
        }

        // 사거리 + 가시선 — 거리만 보면 위조 RPC로 벽 너머 부활이 뚫린다 (#360)
        if (!PlayerInteractor.IsWithinReach(
                holder,
                target.transform,
                PlayerInteractor.RangeOf(holder, k_fallbackRange),
                transform.position))
        {
            NotifyOwner($"부활 실패 — {target.name}이 사거리를 벗어났다");
            return;
        }

        // 부활은 본부 장치와 같은 경로 — HP 부분 회복 + 무력화 해제 (#365와 동일)
        target.ServerRevive();
        NotifyOwner($"부활 완료: {target.name} (부활 키트 소모)");

        // 성공했을 때만 소모한다 — 거부된 사용으로 키트가 사라지면 산 값을 그냥 잃는다
        ServerConsume();
    }
}
