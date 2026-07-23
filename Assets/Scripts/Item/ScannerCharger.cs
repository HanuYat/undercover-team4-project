using UnityEngine;

/// <summary>
/// 본부 스캐너 충전기 — 상호작용 시 상대의 장착 아이템이 IChargeable이면 충전한다. (이슈 #60)
/// GDD 5-2: 스캐너는 배터리 충전식이며 본부 충전기에서만 재충전 가능.
/// 네트워킹은 IChargeable.Charge()(예: Scanner.Charge())가 서버 권한으로 처리하므로
/// 이 컴포넌트 자체는 NetworkBehaviour일 필요가 없다.
/// </summary>
[RequireComponent(typeof(Collider))]
public class ScannerCharger : MonoBehaviour, IInteractable
{
    [Header("충전 방식")]
    [SerializeField] private bool m_chargeToFull = true;
    [Tooltip("m_chargeToFull이 false일 때 1회 상호작용당 충전량")]
    [SerializeField] private int m_chargeAmount = 1;

    /// <summary>장착 아이템이 충전 대상(IChargeable)이면 상호작용 의미가 있다 — 조준 피드백(윤곽선) 판정용. (#184)
    /// 완충이어도 윤곽선을 띄운다 — E로 "이미 가득 참" 토스트 피드백을 주기 위함 (#309).</summary>
    public bool CanInteract(GameObject interactor)
    {
        IChargeable chargeable = interactor.GetComponentInParent<PlayerItemUser>()?.EquippedItem as IChargeable;
        return chargeable != null;
    }

    public void Interact(GameObject interactor)
    {
        IChargeable chargeable = interactor.GetComponentInParent<PlayerItemUser>()?.EquippedItem as IChargeable;
        if (chargeable == null)
            return;

        // 완충이어도 Charge를 호출한다 — 서버가 완충을 감지해 "가득 참" 토스트를 오너에게 띄운다 (#309).
        // (값이 안 바뀌므로 실제 충전은 없고 피드백만 나간다.)
        int amount = m_chargeToFull ? chargeable.MaxBattery : m_chargeAmount;
        chargeable.Charge(amount);
        Debug.Log($"충전기 상호작용 — {amount} 충전 요청");
    }
}
