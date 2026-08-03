using UnityEngine;

public interface IInteractable
{
    void Interact(GameObject interactor);

    /// <summary>
    /// 지금 이 대상에 E 상호작용이 실제로 동작하는지 — 조준 피드백(윤곽선·크로스헤어) 판정용. (#184)
    /// 기본 구현은 항상 true. 상태에 따라 상호작용이 막히는 구현체만 재정의한다.
    /// Interact()가 내부에서 거르는 조건과 같은 기준을 유지해야
    /// "윤곽선이 떴는데 눌러도 반응 없음"이 안 생긴다.
    /// </summary>
    bool CanInteract(GameObject interactor) => true;

    // 끌기 중 E가 '놓기'보다 우선하는지를 여는 TakesPriorityOverRelease(#414)는 제거됐다 (#492) —
    // 유일한 재정의자였던 인계 단말이 사라져 아무도 true를 돌려주지 않는 죽은 확장점이 됐다.
    // 같은 취지가 다시 필요하면 운반 쪽 ICarriedBodyReceiver(PlayerInteractor)가 살아 있는 선례다.
}
