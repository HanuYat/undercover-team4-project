using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 스캔 결과 프레젠터. (#39)
/// 내 스캐너의 스캔 완료 이벤트를 구독해 표시 문자열로 가공한 뒤 뷰(ScanResultView)에 넘긴다.
/// 오너 로컬 전용 — 스캔 결과는 스캔한 본인 화면에만 뜨고, 공유는 무전 구두 전달로만 한다. (GDD 5-4)
/// (PlayerHandView와 동일한 오너 게이트 패턴 — 남의 플레이어 오브젝트에서는 UI를 통째로 끈다)
/// </summary>
public class ScanResultPresenter : NetworkBehaviour
{
    [Header("모델 (내 플레이어의 스캐너)")]
    [SerializeField]
    private Scanner m_scanner;

    [Header("뷰")]
    [SerializeField]
    private ScanResultView m_view;

    [Tooltip("HUD 루트(캔버스). 남의 플레이어 것이 내 화면에 겹쳐 그려지지 않게 오너가 아니면 끈다")]
    [SerializeField]
    private GameObject m_uiRoot;

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
        m_scanner.OnScanCompleted += HandleScanCompleted;
    }

    public override void OnNetworkDespawn()
    {
        if (m_scanner != null)
            m_scanner.OnScanCompleted -= HandleScanCompleted;
    }

    private void HandleScanCompleted(CitizenProfile profile)
    {
        m_view.Show(profile.CitizenName, profile.m_typeView, profile.m_factionView);
    }
}
