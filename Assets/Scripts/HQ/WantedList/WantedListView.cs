using System.Collections.Generic;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;
using UnityEngine.UI;

/// <summary>
/// 본부 모니터의 수배 리스트 표시 — WantedListManager의 동기화 리스트를 구독해 항목이 추가/제거될 때마다 행을 다시 그림.
/// MinimapViewer·CCTV와 함께 본부(HQ) 장소의 모니터 화면 컴포넌트다.
///
/// <b>한 페이지에 담기는 만큼만 그린다</b> (#917) — 판 크기는 고정인데 수배 정원(CriminalAssigner의
/// m_maxWantedCount)은 씬 값이라 언제든 늘어난다. 전부 쌓으면 판 밖으로 넘치므로, 시민 명부
/// (<see cref="CitizenDirectoryView"/>)와 같은 페이지 방식을 쓴다 — 정원이 몇이 되든 페이지 수만 늘어난다.
/// 한 페이지 정원은 그리드 배치가 판에 몇 개를 담는지에 맞춘 값이다.
/// </summary>
public class WantedListView : MonoBehaviour
{
    private WantedListManager Manager => App.Game.WantedList;

    [Header("UI 참조")]
    [SerializeField] private RectTransform m_entryContainer; // 행 부모 (GridLayoutGroup)
    [SerializeField] private WantedEntryView m_entryPrefab;  // 행 프리팹

    [Header("페이지")]
    [SerializeField] private Button m_prevButton;
    [SerializeField] private Button m_nextButton;
    [SerializeField] private TMP_Text m_pageLabel; // "1 / 2" (선택)

    [Tooltip("한 페이지에 그릴 수배 수 — 컨테이너 GridLayoutGroup이 판에 담는 칸 수와 맞출 것")]
    [Min(1)]
    [SerializeField] private int m_entriesPerPage = 6;

    [Tooltip("페이지가 둘 이상일 때 자동으로 넘기는 간격(초). 본부 모니터는 상시 표시 화면이라 누를 수가 없다")]
    [Min(1f)]
    [SerializeField] private float m_autoPageSeconds = 8f;

    private float m_autoPageElapsed;
    private int m_pageCount = 1;

    private readonly List<WantedEntryView> m_rows = new List<WantedEntryView>();
    private int m_page;

    private void Awake()
    {
        if (m_prevButton != null)
            m_prevButton.onClick.AddListener(() => ChangePage(-1));
        if (m_nextButton != null)
            m_nextButton.onClick.AddListener(() => ChangePage(1));
    }

    private void ChangePage(int delta)
    {
        m_page += delta;
        m_autoPageElapsed = 0f;
        Rebuild();
    }

    // 본부는 커서가 잠겨 있어 버튼을 누를 수 없다 — 페이지가 여럿이면 스스로 넘긴다
    private void Update()
    {
        if (m_pageCount <= 1)
            return;

        m_autoPageElapsed += Time.deltaTime;
        if (m_autoPageElapsed < m_autoPageSeconds)
            return;

        m_autoPageElapsed = 0f;
        m_page = (m_page + 1) % m_pageCount;
        Rebuild();
    }

    // 몽타주 문장을 조립할 옵션 정의 — 항목에는 인덱스만 실려 오므로 표시하는 쪽이 자기 언어로 만든다 (#497).
    // 배정기가 들고 있는 것을 그대로 쓴다 — 행 프리팹에 같은 에셋을 또 배선하면 두 곳이 어긋날 수 있다.
    private AppearanceDatabase Database => App.Game.Appearance != null ? App.Game.Appearance.Database : null;

    private void OnEnable()
    {
        // 행의 몽타주·현상금 표기가 테이블에서 오므로 언어가 바뀌면 다시 그린다 — 값은 그대로여도 표기가 바뀐다.
        // 행마다 StringChanged를 걸지 않고 로케일 변경 한 곳에 걸어 통째로 다시 채운다 (ShopStand와 같은 방식). (#497)
        LocalizationSettings.SelectedLocaleChanged += HandleLocaleChanged;

        if (Manager == null)
        {
            Debug.LogWarning("WantedListView: WantedListManager를 찾지 못해 표시할 수 없다", this);
            return;
        }

        // 이후 추가/제거는 OnListChanged로 갱신
        Manager.Wanted.OnListChanged += HandleListChanged;
        // 접속 직후 초기 동기화 시점 — NetworkList는 late-join 클라에 초기 내용을 OnListChanged로 안 알리므로
        // 여기서 현재 상태를 처음 한 번 그린다 (뒤늦게 접속한 클라 빈 화면 방지)
        Manager.OnListReady += Rebuild;

        // 매니저가 이미 스폰돼 있으면(뷰가 늦게 켜져 OnListReady를 놓친 경우) 즉시 그린다
        if (Manager.IsSpawned)
            Rebuild();
    }

    private void OnDisable()
    {
        if (Manager != null)
        {
            Manager.Wanted.OnListChanged -= HandleListChanged;
            Manager.OnListReady -= Rebuild;
        }

        // 종료 중에는 설정 에셋을 되살리지 않는다 — HasSettings로 먼저 확인한다 (ShopStand 관례)
        if (LocalizationSettings.HasSettings)
            LocalizationSettings.SelectedLocaleChanged -= HandleLocaleChanged;
    }

    private void HandleLocaleChanged(Locale locale)
    {
        if (Manager != null && Manager.IsSpawned)
            Rebuild();
    }

    // 항목 추가/제거 시 전체를 다시 그린다.
    private void HandleListChanged(NetworkListEvent<WantedEntry> _) => Rebuild();

    private void Rebuild()
    {
        if (m_entryPrefab == null || m_entryContainer == null)
        {
            Debug.LogWarning("WantedListView: 행 프리팹/컨테이너가 지정되지 않았다", this);
            return;
        }

        NetworkList<WantedEntry> wanted = Manager.Wanted;

        int perPage = Mathf.Max(1, m_entriesPerPage);
        int pageCount = Mathf.Max(1, Mathf.CeilToInt(wanted.Count / (float)perPage));
        m_pageCount = pageCount;
        m_page = Mathf.Clamp(m_page, 0, pageCount - 1);

        int start = m_page * perPage;
        int visible = Mathf.Clamp(wanted.Count - start, 0, perPage);

        // 행 수를 이번 페이지 항목 수에 맞춘다 (부족하면 생성, 남으면 제거) — 매번 전부 파괴/생성하지 않고 재사용
        while (m_rows.Count < visible)
            m_rows.Add(Instantiate(m_entryPrefab, m_entryContainer));

        while (m_rows.Count > visible)
        {
            int last = m_rows.Count - 1;
            if (m_rows[last] != null)
                Destroy(m_rows[last].gameObject);
            m_rows.RemoveAt(last);
        }

        AppearanceDatabase database = Database;
        if (database == null && wanted.Count > 0)
            Debug.LogWarning("WantedListView: AppearanceDatabase를 찾지 못해 몽타주를 조립할 수 없다", this);

        for (int i = 0; i < visible; i++)
            m_rows[i].Bind(wanted[start + i], database);

        // 첫 페이지=다음만, 마지막=이전만 (명부와 같은 규칙)
        if (m_prevButton != null)
            m_prevButton.gameObject.SetActive(m_page > 0);
        if (m_nextButton != null)
            m_nextButton.gameObject.SetActive(m_page < pageCount - 1);
        // 한 페이지면 숫자를 보일 이유가 없다
        if (m_pageLabel != null)
        {
            m_pageLabel.gameObject.SetActive(pageCount > 1);
            m_pageLabel.text = $"{m_page + 1} / {pageCount}";
        }
    }
}
