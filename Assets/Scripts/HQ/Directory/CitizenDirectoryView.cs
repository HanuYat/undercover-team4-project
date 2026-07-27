using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Unity.Netcode;
using System.Collections.Generic;

/// <summary>
/// 본부 시민 인명부 패널 (#223) — E로 펼치는 열람 UI. DirectoryManager의 동기화 리스트를 읽어 행으로 그린다.
/// 정렬(이름순/세력순, 같은 버튼 재클릭 시 오름/내림 토글)과 페이지 넘김(첫 페이지=다음만, 마지막=이전만)을 제공한다.
/// 여는 동안 게임플레이 입력을 정지하고 커서를 푼다(BombManualHud와 동일 취지). Esc로 닫는다.
/// 로컬 UI — 상호작용한 본인 클라에서만 열린다. 데이터 갱신은 NetworkList.OnListChanged로 따라간다.
/// </summary>
public class CitizenDirectoryView : HqPanelView
{
    private DirectoryManager Manager => App.Game.Directory;

    [SerializeField]
    private RectTransform m_entryContainer; // ScrollRect Content

    [SerializeField]
    private DirectoryEntryView m_entryPrefab;

    [Header("정렬 버튼 (초록)")]
    [SerializeField]
    private Button m_sortNameButton;

    [SerializeField]
    private Button m_sortFactionButton;

    [Header("페이지 버튼 (빨강)")]
    [SerializeField]
    private Button m_prevButton;

    [SerializeField]
    private Button m_nextButton;

    [SerializeField]
    private TMP_Text m_pageLabel; // "1 / 3" (선택)

    [Header("설정")]
    [SerializeField]
    private int m_entriesPerPage = 8;

    private enum SortKey
    {
        Name,
        Faction,
    }

    private SortKey m_sortKey = SortKey.Name;
    private bool m_ascending = true;
    private int m_page;

    private readonly List<DirectoryEntryView> m_rows = new List<DirectoryEntryView>();
    private readonly List<DirectoryEntry> m_sorted = new List<DirectoryEntry>();

    protected override void Awake()
    {
        base.Awake();

        if (m_sortNameButton != null) m_sortNameButton.onClick.AddListener(() => SetSort(SortKey.Name));
        if (m_sortFactionButton != null) m_sortFactionButton.onClick.AddListener(() => SetSort(SortKey.Faction));
        if (m_prevButton != null) m_prevButton.onClick.AddListener(() => ChangePage(-1));
        if (m_nextButton != null) m_nextButton.onClick.AddListener(() => ChangePage(1));
    }

    private void SetSort(SortKey key)
    {
        if (m_sortKey == key)
            m_ascending = !m_ascending; // 같은 기준 재클릭 → 방향 토글
        else
        {
            m_sortKey = key;
            m_ascending = true;
        }
        m_page = 0; // 정렬 바뀌면 첫 페이지로
        Rebuild();
    }

    private void ChangePage(int delta)
    {
        m_page += delta;
        Rebuild();
    }

    private void HandleListChanged(NetworkListEvent<DirectoryEntry> _) => Rebuild();

    private void Rebuild()
    {
        if (m_entryPrefab == null || m_entryContainer == null || Manager == null)
            return;

        // 동기화 리스트 → 로컬 복사 후 정렬 (NetworkList는 정렬 불가)
        m_sorted.Clear();
        NetworkList<DirectoryEntry> directory = Manager.Directory;
        for (int i = 0; i < directory.Count; i++)
            m_sorted.Add(directory[i]);
        m_sorted.Sort(Compare);

        int perPage = Mathf.Max(1, m_entriesPerPage);
        int pageCount = Mathf.Max(1, Mathf.CeilToInt(m_sorted.Count / (float)perPage));
        m_page = Mathf.Clamp(m_page, 0, pageCount - 1);

        int start = m_page * perPage;
        int end = Mathf.Min(start + perPage, m_sorted.Count);
        int visible = Mathf.Max(0, end - start);

        // 행 수를 현재 페이지 항목 수에 맞춘다 (재사용)
        while (m_rows.Count < visible)
            m_rows.Add(Instantiate(m_entryPrefab, m_entryContainer));
        while (m_rows.Count > visible)
        {
            int last = m_rows.Count - 1;
            if (m_rows[last] != null)
                Destroy(m_rows[last].gameObject);
            m_rows.RemoveAt(last);
        }

        for (int i = 0; i < visible; i++)
            m_rows[i].Bind(m_sorted[start + i]);

        // 페이지 버튼: 첫 페이지=다음만, 마지막=이전만, 중간=둘 다
        if (m_prevButton != null)
            m_prevButton.gameObject.SetActive(m_page > 0);
        if (m_nextButton != null)
            m_nextButton.gameObject.SetActive(m_page < pageCount - 1);
        if (m_pageLabel != null)
            m_pageLabel.text = $"{m_page + 1} / {pageCount}";
    }

    private int Compare(DirectoryEntry a, DirectoryEntry b)
    {
        int c;
        if (m_sortKey == SortKey.Faction)
        {
            c = ((int)a.Faction).CompareTo((int)b.Faction);
            if (c == 0) // 같은 세력이면 이름순 보조 정렬
                c = string.Compare(
                    a.Name.ToString(),
                    b.Name.ToString(),
                    System.StringComparison.OrdinalIgnoreCase
                );
        }
        else
        {
            c = string.Compare(
                a.Name.ToString(),
                b.Name.ToString(),
                System.StringComparison.OrdinalIgnoreCase
            );
        }
        return m_ascending ? c : -c;
    }

    protected override void OnOpened()
    {
        m_page = 0;
        if (Manager != null) Manager.Directory.OnListChanged += HandleListChanged;
        Rebuild();
    }

    protected override void OnClosed()
    {
        if (Manager != null) Manager.Directory.OnListChanged -= HandleListChanged;
    }
}
