using System.Collections.Generic;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;

/// <summary>
/// 본부 소모품 재고 게시판 (#840) — 상점에서 <b>사서 아직 안 쓴</b> 소모품이 몇 개 남았는지를 띄운다.
/// 기본 지급품·주운 물건은 세지 않는다 — 원본이 구매 집계라 상점을 거치지 않은 물건은 표식이 없다.
/// 열고 닫는 패널이 아니라 씬에 붙은 게시판이다(RoundFundBoard와 같은 자리) — 본부 인원이 지나가며
/// 눈으로 확인해 무전으로 알려주는 그림이기 때문이다.
///
/// 원본은 <see cref="ShopPurchases.Tallies"/>의 Remaining이다. 주문창 구매 내역
/// (<see cref="ShopPurchaseHistoryView"/>)과 같은 목록을 읽으므로 두 화면이 어긋날 수 없다.
///
/// 무엇을 소모품으로 볼지는 <see cref="ShopCatalog.Entry.IsConsumable"/>이 정한다 — 진열 고정 그룹
/// (IsStaple)과는 다른 축이다. 산 적 없는 품목도 0개로 걸어 둔다: "없다"도 본부가 알아야 하는 정보다.
/// </summary>
public class HqStockBoard : MonoBehaviour
{
    [Header("데이터")]
    [Tooltip("소모품 목록과 품목 id의 출처 — ShopPurchases와 같은 에셋을 잡을 것")]
    [SerializeField]
    private ShopCatalog m_catalog;

    [Header("목록")]
    [SerializeField]
    private RectTransform m_rowContainer;

    [SerializeField]
    private HqStockRowView m_rowPrefab;

    [Header("문구")]
    [Tooltip("게시판 제목 — 예: HqTable/Hq.Stock.Title. 비우면 제목을 건드리지 않는다")]
    [SerializeField]
    private LocalizedString m_title;

    [SerializeField]
    private TMP_Text m_titleText;

    private readonly List<HqStockRowView> m_rows = new List<HqStockRowView>();

    // 게시판에 걸 소모품 항목의 카탈로그 인덱스 — 카탈로그는 세션 중 바뀌지 않아 한 번 모으면 된다
    private readonly List<int> m_consumables = new List<int>();

    // 구독해 둔 홀더 — 해제 기준을 지금 조회값이 아니라 실제로 구독한 그 참조로 잡는다
    private ShopPurchases m_bound;

    private void OnEnable()
    {
        if (!m_title.IsEmpty)
            m_title.StringChanged += HandleTitleChanged;

        // 품목 이름이 테이블에서 오므로 언어가 바뀌면 통째로 다시 그린다 — 줄마다 구독하지 않는다 (#497)
        LocalizationSettings.SelectedLocaleChanged += HandleLocaleChanged;

        CollectConsumables();

        // Bind가 성공하면 그쪽이 그린다 — 홀더가 아직 없을 때만 0개 줄로 한 번 그려 둔다
        if (!Bind())
            Rebuild();
    }

    private void OnDisable()
    {
        if (!m_title.IsEmpty)
            m_title.StringChanged -= HandleTitleChanged;

        // 종료 중에는 설정 에셋을 되살리지 않는다 (WantedListView 관례)
        if (LocalizationSettings.HasSettings)
            LocalizationSettings.SelectedLocaleChanged -= HandleLocaleChanged;

        Unbind();
    }

    private void Update()
    {
        // 상주 홀더는 세션과 함께 오지만, 원격 클라는 씬 로드보다 늦게 도착할 수 있다
        if (m_bound == null)
            Bind();
    }

    private bool Bind()
    {
        ShopPurchases purchases = App.Game.ShopPurchases;
        if (purchases == null || !purchases.IsSpawned)
            return false;

        m_bound = purchases;
        m_bound.Tallies.OnListChanged += HandleTalliesChanged;
        Rebuild();
        return true;
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

    private void CollectConsumables()
    {
        m_consumables.Clear();

        if (m_catalog == null)
        {
            Debug.LogWarning("HqStockBoard: 카탈로그가 배선되지 않아 재고를 띄울 수 없다", this);
            return;
        }

        for (int i = 0; i < m_catalog.Count; i++)
        {
            ShopCatalog.Entry entry = m_catalog.Get(i);
            if (entry != null && entry.IsValid && entry.IsConsumable)
                m_consumables.Add(i);
        }
    }

    // 줄 오브젝트는 재사용하고 남으면 지운다 (FactionSymbolBoardView와 같은 방식).
    private void Rebuild()
    {
        if (m_rowPrefab == null || m_rowContainer == null)
            return;

        while (m_rows.Count < m_consumables.Count)
            m_rows.Add(Instantiate(m_rowPrefab, m_rowContainer));

        while (m_rows.Count > m_consumables.Count)
        {
            int last = m_rows.Count - 1;
            if (m_rows[last] != null)
                Destroy(m_rows[last].gameObject);
            m_rows.RemoveAt(last);
        }

        for (int i = 0; i < m_consumables.Count; i++)
        {
            int catalogIndex = m_consumables[i];
            m_rows[i].Bind(m_catalog.Get(catalogIndex), Remaining(catalogIndex));
        }
    }

    // 집계에 줄이 없으면 산 적이 없다는 뜻이라 0개다.
    private int Remaining(int catalogIndex)
    {
        if (m_bound == null || !m_bound.IsSpawned)
            return 0;

        NetworkList<PurchaseTally> tallies = m_bound.Tallies;
        for (int i = 0; i < tallies.Count; i++)
        {
            PurchaseTally tally = tallies[i];
            if (tally.CatalogIndex == catalogIndex)
                return tally.Remaining;
        }

        return 0;
    }
}
