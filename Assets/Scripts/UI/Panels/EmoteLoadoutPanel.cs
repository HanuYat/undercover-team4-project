using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 로비에서 감정표현 휠 8칸을 구성하는 패널. (#219)
///
/// 로비 전용인 이유: 로비는 세션당 한 번만 거치는 씬이고(이후 Shop↔Game 루프), 인게임 편집을
/// 열면 라운드 중에 휠 구성이 바뀌는 경우를 재생 상태와 함께 다뤄야 한다. 그 값어치가 아직 없다.
///
/// <b>구성은 네트워크로 올리지 않는다.</b> 남이 알아야 하는 것은 "지금 무엇을 재생 중인가"뿐이고,
/// 내가 몇 번 칸에 뭘 넣었는지는 아무도 볼 일이 없다. PlayerPrefs에 <b>계정별로</b> 로컬 저장하므로
/// 다음 실행에도 유지되고, 한 PC를 여러 계정이(MPPM 가상 플레이어 포함) 써도 서로 덮지 않는다 (#640).
///
/// 조작: 왼쪽 카탈로그 목록에서 하나 고르고 → 오른쪽 8칸 중 하나를 누르면 그 칸에 들어간다.
/// 선택 없이 칸을 누르면 그 칸을 비운다.
/// </summary>
public class EmoteLoadoutPanel : PanelBase
{
    [Tooltip("감정표현 목록")]
    [SerializeField]
    private EmoteCatalog m_catalog;

    [Tooltip("카탈로그 항목 버튼을 담을 부모")]
    [SerializeField]
    private RectTransform m_catalogContent;

    [Tooltip("카탈로그 항목 버튼 프리팹 — EmoteWheelSlotView + Button")]
    [SerializeField]
    private GameObject m_catalogEntryPrefab;

    [Tooltip("휠 8칸 미리보기 — 인덱스 = 슬롯 번호")]
    [SerializeField]
    private EmoteWheelSlotView[] m_slotViews = new EmoteWheelSlotView[EmoteLoadout.k_slotCount];

    [Tooltip("칸을 누를 버튼 8개 — m_slotViews와 같은 순서")]
    [SerializeField]
    private Button[] m_slotButtons = new Button[EmoteLoadout.k_slotCount];

    [SerializeField]
    private Button m_closeButton;

    // 저장 칸이 계정별로 갈리므로 PlayerId를 알 수 있는 Awake에서 만든다 (#640)
    private EmoteLoadout m_loadout;
    private int m_selectedCatalogIndex = -1;

    public override bool CanCloseWithESC => true;
    public override bool IsStackable => true;

    protected override void Awake()
    {
        base.Awake();

        m_loadout = new EmoteLoadout(App.Net.Auth != null ? App.Net.Auth.PlayerId : null);
        m_loadout.Load();
        BuildCatalogList();
        RefreshSlots();

        for (int slot = 0; slot < m_slotButtons.Length; slot++)
        {
            int captured = slot; // 클로저가 루프 변수를 잡지 않게 복사
            if (m_slotButtons[slot] != null)
                m_slotButtons[slot].onClick.AddListener(() => AssignToSlot(captured));
        }

        if (m_closeButton != null)
            m_closeButton.onClick.AddListener(ClosePanel);
    }

    public override void ClosePanel()
    {
        // 마무리로 한 번 더 — 칸을 누를 때 이미 PlayerPrefs에 넣었으므로 여기 남은 일은
        // 디스크로 밀어내는 것뿐이다 (#640).
        m_loadout.Save();
        base.ClosePanel();
    }

    private void BuildCatalogList()
    {
        if (m_catalog == null || m_catalogContent == null || m_catalogEntryPrefab == null)
            return;

        for (int index = 0; index < m_catalog.Count; index++)
        {
            EmoteDefinition definition = m_catalog.Get(index);
            if (definition == null)
                continue;

            GameObject entry = Instantiate(m_catalogEntryPrefab, m_catalogContent);

            EmoteWheelSlotView view = entry.GetComponent<EmoteWheelSlotView>();
            if (view != null)
                view.Bind(definition);

            int captured = index;
            Button button = entry.GetComponent<Button>();
            if (button != null)
                button.onClick.AddListener(() => SelectCatalogEntry(captured));
        }
    }

    private void SelectCatalogEntry(int catalogIndex)
    {
        m_selectedCatalogIndex = catalogIndex;
    }

    private void AssignToSlot(int slot)
    {
        EmoteDefinition definition = m_catalog != null ? m_catalog.Get(m_selectedCatalogIndex) : null;

        // 고른 것이 없으면 그 칸을 비운다 — 별도의 '지우기' 조작을 만들지 않기 위한 규칙이다.
        m_loadout.SetSlot(slot, definition != null ? definition.Id : null);

        // 칸을 만질 때마다 저장한다 — 닫을 때만 저장하면 패널을 열어 둔 채로 호스트가 게임을
        // 시작하거나 앱이 종료될 때 편집이 통째로 사라진다. 백드롭이 화면을 막고 있어 본인은
        // 뒤로 누를 뿐이지만, 호스트가 시작 버튼을 누르는 타이밍은 막을 수 없다. 디스크 쓰기는
        // 닫을 때 한 번으로 미루므로 칸을 누를 때의 비용은 메모리 쓰기뿐이다 (#640).
        m_loadout.Save(flush: false);

        RefreshSlots();
    }

    private void RefreshSlots()
    {
        for (int slot = 0; slot < m_slotViews.Length; slot++)
        {
            if (m_slotViews[slot] == null)
                continue;

            EmoteDefinition definition = null;
            if (m_catalog != null)
                definition = m_catalog.Get(m_catalog.IndexOf(m_loadout.GetSlot(slot)));

            m_slotViews[slot].Bind(definition);
        }
    }
}
