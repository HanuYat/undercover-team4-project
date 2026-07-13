using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 스캔 결과 프레젠터. (#39)
/// 내 스캐너의 스캔 완료 이벤트를 구독해 표시 문자열로 가공한 뒤 뷰(ScanResultView)에 넘긴다.
/// 오너 로컬 전용 — 스캔 결과는 스캔한 본인 화면에만 뜨고, 공유는 무전 구두 전달로만 한다. (GDD 5-4)
/// (PlayerHandView와 동일한 오너 게이트 패턴 — 남의 플레이어 오브젝트에서는 UI를 통째로 끈다)
///
/// 스캐너는 PlayerLoadout이 런타임에 지급·장착하므로(#47) 직렬화 참조로 잡을 수 없다.
/// 대신 PlayerItemUser의 장착 변경 이벤트를 따라간다 — 스캐너가 장착되면 그 인스턴스의
/// OnScanCompleted를 구독하고, 다른 아이템으로 바뀌면 구독을 해제한다. (스캔은 스캐너 장착 상태에서만 가능)
/// </summary>
public class ScanResultPresenter : NetworkBehaviour
{
    [Header("뷰")]
    [SerializeField]
    private ScanResultView m_view;

    [Tooltip("HUD 루트(캔버스). 남의 플레이어 것이 내 화면에 겹쳐 그려지지 않게 오너가 아니면 끈다")]
    [SerializeField]
    private GameObject m_uiRoot;

    private PlayerItemUser m_itemUser;
    private Scanner m_scanner; // 현재 장착된 스캐너 인스턴스에 바인딩. 스캐너 미장착이면 null.

    public override void OnNetworkSpawn()
    {
        if (!IsOwner)
        {
            if (m_uiRoot != null)
                m_uiRoot.SetActive(false);
            enabled = false;
            return;
        }

        if (m_uiRoot != null)
            m_uiRoot.SetActive(true);

        m_view.Clear();

        // 이 프레젠터는 플레이어 하위 캔버스에 있고 PlayerItemUser는 플레이어 루트에 있다 — 부모까지 탐색.
        m_itemUser = GetComponentInParent<PlayerItemUser>();
        if (m_itemUser == null)
        {
            Debug.LogWarning("ScanResultPresenter: PlayerItemUser를 찾지 못함 — 스캔 결과 표시 불가", this);
            return;
        }

        // 지금 장착된 아이템을 즉시 반영하고, 이후 장착 변경도 따라간다.
        // (스폰 순서와 무관하게 동작: 현재값 확인 + 이벤트 구독 둘 다 수행)
        m_itemUser.OnEquippedItemChanged += HandleEquippedItemChanged;
        HandleEquippedItemChanged(m_itemUser.EquippedItem);
    }

    public override void OnNetworkDespawn()
    {
        if (m_itemUser != null)
            m_itemUser.OnEquippedItemChanged -= HandleEquippedItemChanged;

        BindScanner(null);
    }

    // 장착 아이템이 스캐너면 그 인스턴스에 바인딩, 아니면 해제한다.
    private void HandleEquippedItemChanged(ItemBase item)
    {
        BindScanner(item as Scanner);
    }

    // 구독 대상 스캐너를 교체한다 — 이전 스캐너는 구독 해제하고 새 스캐너(있으면)를 구독한다.
    private void BindScanner(Scanner scanner)
    {
        if (m_scanner == scanner)
            return;

        if (m_scanner != null)
            m_scanner.OnScanCompleted -= HandleScanCompleted;

        m_scanner = scanner;

        if (m_scanner != null)
            m_scanner.OnScanCompleted += HandleScanCompleted;
    }

    private void HandleScanCompleted(CitizenProfile profile)
    {
        m_view.Show(profile.CitizenName, profile.m_typeView, profile.m_factionView);
    }
}
