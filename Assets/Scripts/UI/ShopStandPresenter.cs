using UnityEngine;

/// <summary>
/// 진열대 조준 카드 프레젠터 (#182) — 플레이어에 붙어, 겨냥한 진열대의 카드를 켠다.
/// 카드는 진열대의 자식이라 위치 추종 로직이 필요 없고, 조준이 바뀌는 순간에만
/// (PlayerInteractor.OnTargetChanged) 이전 카드를 끄고 새 카드를 켠다 — 매 프레임 폴링 없음.
/// (스캔 정보 카드 ScanResultPresenter ↔ ScanInfoView와 같은 구조)
///
/// NetworkBehaviour가 아닌 이유: PlayerInteractor가 오너 외에는 비활성이라 OnTargetChanged 자체가
/// 원격 플레이어에서는 발화하지 않는다 — 오너 판정을 따로 둘 필요가 없다.
/// </summary>
public class ShopStandPresenter : MonoBehaviour
{
    private PlayerInteractor m_interactor;
    private ShopStand m_current;

    private void Start()
    {
        // 인터랙터는 플레이어 루트에 있다 — 이 컴포넌트가 자식 HUD에 붙어도 찾도록 부모까지 탐색.
        m_interactor = GetComponentInParent<PlayerInteractor>();
        if (m_interactor == null)
        {
            Debug.LogWarning("ShopStandPresenter: PlayerInteractor를 찾지 못함 — 진열대 카드 표시 불가", this);
            return;
        }

        m_interactor.OnTargetChanged += HandleTargetChanged;
    }

    private void OnDestroy()
    {
        if (m_interactor != null)
            m_interactor.OnTargetChanged -= HandleTargetChanged;
    }

    private void HandleTargetChanged(GameObject target)
    {
        // 콜라이더가 진열대 루트의 자식일 수 있어 부모까지 탐색한다.
        ShopStand stand = target != null ? target.GetComponentInParent<ShopStand>() : null;
        if (stand == m_current)
            return;

        if (m_current != null)
            m_current.SetCardVisible(false);

        m_current = stand;

        if (m_current != null)
            m_current.SetCardVisible(true);
    }
}
