using System.Collections.Generic;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;

/// <summary>
/// 주문창 구매 내역 (#840) — 세션 동안 팀이 무엇을 몇 개 샀는지를 진열 그리드 옆에 상시로 띄운다.
/// 라운드 구분은 하지 않는다: <see cref="ShopPurchases.Tallies"/>의 누적값을 그대로 읽는다.
///
/// 표시 전용이고 서버에 아무것도 요청하지 않는다 — 주문창이 권한이 아닌 것과 같다
/// (<see cref="ShopBrowserPanel"/>). 본부 재고 게시판(<see cref="HqStockBoard"/>)과 같은 목록을 읽고,
/// 이쪽은 Bought를 쓴다.
/// </summary>
public class ShopPurchaseHistoryView : MonoBehaviour
{
    private const string k_shopTable = "ShopTable";
    private const string k_emptyKey = "Shop.History.Empty";

    [Header("데이터")]
    [Tooltip("품목 id가 이 카탈로그의 인덱스다 — ShopLineup·ShopPurchases와 같은 에셋을 잡을 것")]
    [SerializeField]
    private ShopCatalog m_catalog;

    [Header("목록")]
    [SerializeField]
    private RectTransform m_rowContainer;

    [SerializeField]
    private ShopPurchaseRowView m_rowPrefab;

    [Header("문구")]
    [Tooltip("목록 제목 — 예: ShopTable/Shop.History.Title. 비우면 제목을 건드리지 않는다")]
    [SerializeField]
    private LocalizedString m_title;

    [SerializeField]
    private TMP_Text m_titleText;

    [Tooltip("아직 산 것이 없을 때 띄우는 줄. 비워 두면 표시하지 않는다")]
    [SerializeField]
    private TMP_Text m_emptyText;

    private readonly List<ShopPurchaseRowView> m_rows = new List<ShopPurchaseRowView>();

    // 그릴 줄만 걸러 담는 임시 목록 — 카탈로그에서 사라진 품목은 여기서 빠진다
    private readonly List<PurchaseTally> m_shown = new List<PurchaseTally>();

    // 구독해 둔 홀더 — 해제 기준을 지금 조회값이 아니라 실제로 구독한 그 참조로 잡는다
    private ShopPurchases m_bound;

    private void OnEnable()
    {
        if (!m_title.IsEmpty)
            m_title.StringChanged += HandleTitleChanged;

        // 이름이 테이블에서 오므로 언어가 바뀌면 통째로 다시 그린다 — 줄마다 구독하지 않는다 (#497)
        LocalizationSettings.SelectedLocaleChanged += HandleLocaleChanged;

        Bind();
        Rebuild(); // 홀더가 아직 없어도 빈 목록으로 한 번 그려 옛 줄이 남지 않게 한다
    }

    private void OnDisable()
    {
        if (!m_title.IsEmpty)
            m_title.StringChanged -= HandleTitleChanged;

        // 종료 중에는 설정 에셋을 되살리지 않는다 (ShopBrowserPanel 관례)
        if (LocalizationSettings.HasSettings)
            LocalizationSettings.SelectedLocaleChanged -= HandleLocaleChanged;

        Unbind();
    }

    private void Update()
    {
        // 상주 홀더는 보통 이미 스폰돼 있지만, 원격 클라는 늦게 도착할 수 있다
        // (ShopBrowserPanel이 팀 잔액 홀더를 잡는 것과 같은 사정)
        if (m_bound == null)
            Bind();
    }

    private void Bind()
    {
        ShopPurchases purchases = App.Game.ShopPurchases;
        if (purchases == null || !purchases.IsSpawned)
            return;

        m_bound = purchases;
        m_bound.Tallies.OnListChanged += HandleTalliesChanged;
        Rebuild();
    }

    private void Unbind()
    {
        if (m_bound != null)
            m_bound.Tallies.OnListChanged -= HandleTalliesChanged;

        m_bound = null;
    }

    private void HandleTalliesChanged(NetworkListEvent<PurchaseTally> _) => Rebuild();

    private void HandleLocaleChanged(Locale locale) => Rebuild();

    private void HandleTitleChanged(string localizedTitle)
    {
        if (m_titleText != null)
            m_titleText.text = localizedTitle;
    }

    // 줄을 다시 채운다. 줄 오브젝트는 재사용하고 남으면 지운다 (FactionSymbolBoardView와 같은 방식).
    private void Rebuild()
    {
        if (m_rowPrefab == null || m_rowContainer == null)
            return;

        CollectShown();

        while (m_rows.Count < m_shown.Count)
            m_rows.Add(Instantiate(m_rowPrefab, m_rowContainer));

        while (m_rows.Count > m_shown.Count)
        {
            int last = m_rows.Count - 1;
            if (m_rows[last] != null)
                Destroy(m_rows[last].gameObject);
            m_rows.RemoveAt(last);
        }

        for (int i = 0; i < m_shown.Count; i++)
        {
            PurchaseTally tally = m_shown[i];
            m_rows[i].Bind(m_catalog.Get(tally.CatalogIndex), tally.Bought, tally.Remaining);
        }

        if (m_emptyText != null)
        {
            m_emptyText.text = LocalizedStrings.Get(k_shopTable, k_emptyKey);
            m_emptyText.enabled = m_shown.Count == 0;
        }
    }

    private void CollectShown()
    {
        m_shown.Clear();

        if (m_catalog == null || m_bound == null || !m_bound.IsSpawned)
            return;

        NetworkList<PurchaseTally> tallies = m_bound.Tallies;
        for (int i = 0; i < tallies.Count; i++)
        {
            PurchaseTally tally = tallies[i];

            // 카탈로그에서 빠진 옛 품목은 이름·아이콘을 읽을 데가 없어 건너뛴다
            if (m_catalog.Get(tally.CatalogIndex) != null)
                m_shown.Add(tally);
        }
    }
}
