using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 본부 모니터의 수배 리스트 표시 — WantedListManager의 동기화 리스트를 구독해 항목이 추가/제거될 때마다 행을 다시 그림.
/// MinimapViewer·CCTV와 함께 본부(HQ) 장소의 모니터 화면 컴포넌트다.
/// </summary>
public class WantedListView : MonoBehaviour
{
    [Header("수배 리스트 매니저 (비우면 씬에서 자동 탐색)")]
    [SerializeField] private WantedListManager m_manager;

    [Header("UI 참조")]
    [SerializeField] private RectTransform m_entryContainer; // 행 부모
    [SerializeField] private WantedEntryView m_entryPrefab;  // 행 프리팹

    private readonly List<WantedEntryView> m_rows = new List<WantedEntryView>();

    private void Awake()
    {
        if (m_manager == null)
            m_manager = FindFirstObjectByType<WantedListManager>();
    }

    private void OnEnable()
    {
        if (m_manager == null)
        {
            Debug.LogWarning("WantedListView: WantedListManager를 찾지 못해 표시할 수 없다", this);
            return;
        }

        // 이후 추가/제거는 OnListChanged로 갱신
        m_manager.Wanted.OnListChanged += HandleListChanged;
        // 접속 직후 초기 동기화 시점 — NetworkList는 late-join 클라에 초기 내용을 OnListChanged로 안 알리므로
        // 여기서 현재 상태를 처음 한 번 그린다 (뒤늦게 접속한 클라 빈 화면 방지)
        m_manager.OnListReady += Rebuild;

        // 매니저가 이미 스폰돼 있으면(뷰가 늦게 켜져 OnListReady를 놓친 경우) 즉시 그린다
        if (m_manager.IsSpawned)
            Rebuild();
    }

    private void OnDisable()
    {
        if (m_manager != null)
        {
            m_manager.Wanted.OnListChanged -= HandleListChanged;
            m_manager.OnListReady -= Rebuild;
        }
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

        NetworkList<WantedEntry> wanted = m_manager.Wanted;

        // 행 수를 리스트 수에 맞춘다 (부족하면 생성, 남으면 제거) — 매번 전부 파괴/생성하지 않고 재사용
        while (m_rows.Count < wanted.Count)
            m_rows.Add(Instantiate(m_entryPrefab, m_entryContainer));

        while (m_rows.Count > wanted.Count)
        {
            int last = m_rows.Count - 1;
            if (m_rows[last] != null)
                Destroy(m_rows[last].gameObject);
            m_rows.RemoveAt(last);
        }

        for (int i = 0; i < wanted.Count; i++)
            m_rows[i].Bind(wanted[i]);
    }
}
