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
/// <b>조작은 드래그앤드롭이다</b> — 카탈로그 항목을 칸으로 끌면 들어가고(찬 칸이면 덮어쓴다),
/// 칸을 다른 칸으로 끌면 자리를 바꾸고, 칸을 허공으로 끌면 비운다. 골라 두고 누르던 2단 조작
/// (고르기 → 칸 누르기)을 대체한다: 무엇이 골라져 있는지 화면에 드러나지 않아, 칸을 눌렀을 때
/// 들어갈지 지워질지 알 수 없었다.
///
/// 손잡이(<see cref="EmoteDragHandle"/>)는 <b>런타임에</b> 붙인다 — 칸이 Lobby 씬에 있어서다(그쪽 주석).
/// </summary>
public class EmoteLoadoutPanel : PanelBase
{
    [Tooltip("감정표현 목록")]
    [SerializeField]
    private EmoteCatalog m_catalog;

    [Tooltip("카탈로그 항목 버튼을 담을 부모")]
    [SerializeField]
    private RectTransform m_catalogContent;

    [Tooltip("카탈로그 항목 프리팹 — EmoteWheelSlotView")]
    [SerializeField]
    private GameObject m_catalogEntryPrefab;

    [Tooltip("휠 8칸 미리보기 — 인덱스 = 슬롯 번호")]
    [SerializeField]
    private EmoteWheelSlotView[] m_slotViews = new EmoteWheelSlotView[EmoteLoadout.k_slotCount];

    [SerializeField]
    private Button m_closeButton;

    // 저장 칸이 계정별로 갈리므로 PlayerId를 알 수 있는 Awake에서 만든다 (#640)
    private EmoteLoadout m_loadout;

    public override bool CanCloseWithESC => true;
    public override bool IsStackable => true;

    protected override void Awake()
    {
        base.Awake();

        m_loadout = new EmoteLoadout(App.Net.Auth != null ? App.Net.Auth.PlayerId : null);
        m_loadout.Load();
        BuildCatalogList();
        RefreshSlots();

        for (int slot = 0; slot < m_slotViews.Length; slot++)
        {
            if (m_slotViews[slot] != null)
                EmoteDragHandle.Attach(this, m_slotViews[slot].gameObject, EmoteDragHandle.EKind.Slot, slot);
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

            EmoteDragHandle.Attach(this, entry, EmoteDragHandle.EKind.Catalog, index);
        }
    }

    // ---- 드래그앤드롭 ----

    /// <summary>끌기 시작할 때 커서에 붙일 그림 — 빈 칸이면 null이라 드래그가 시작되지 않는다.</summary>
    internal Sprite ResolveIcon(EmoteDragHandle handle)
    {
        if (handle == null || m_catalog == null)
            return null;

        EmoteDefinition definition =
            handle.Kind == EmoteDragHandle.EKind.Catalog
                ? m_catalog.Get(handle.Index)
                : m_catalog.Get(m_catalog.IndexOf(m_loadout.GetSlot(handle.Index)));

        return definition != null ? definition.Icon : null;
    }

    /// <summary>손잡이 위에 떨어뜨렸다 — 카탈로그에서 왔으면 넣고, 칸에서 왔으면 자리를 바꾼다.</summary>
    internal void HandleDrop(EmoteDragHandle from, EmoteDragHandle to)
    {
        if (from == null || to == null || m_catalog == null)
            return;

        // 카탈로그로 되돌려 놓는 것은 '가져다 버린' 것으로 본다 — 목록은 원본이라 받을 칸이 없다
        if (to.Kind == EmoteDragHandle.EKind.Catalog)
        {
            HandleDropOutside(from);
            return;
        }

        if (from.Kind == EmoteDragHandle.EKind.Catalog)
        {
            EmoteDefinition definition = m_catalog.Get(from.Index);
            SetSlot(to.Index, definition != null ? definition.Id : null);
            return;
        }

        // 칸 → 칸: 맞바꾼다. 밀어내기(뒤 칸을 한 칸씩 미는 것)가 아니라 교환인 이유는 8칸이 고정
        // 배치라서다 — 휠에서 손가락이 기억하는 것은 순서가 아니라 방향이다.
        string moved = m_loadout.GetSlot(from.Index);
        SetSlot(from.Index, m_loadout.GetSlot(to.Index));
        SetSlot(to.Index, moved);
    }

    /// <summary>허공에 떨어뜨렸다 — 칸에서 끌어낸 것이면 비운다. 카탈로그에서 끌어낸 것은 무시.</summary>
    internal void HandleDropOutside(EmoteDragHandle from)
    {
        if (from == null || from.Kind != EmoteDragHandle.EKind.Slot)
            return;

        SetSlot(from.Index, null);
    }

    private void SetSlot(int slot, string emoteId)
    {
        m_loadout.SetSlot(slot, emoteId);

        // 칸을 만질 때마다 저장한다 — 닫을 때만 저장하면 패널을 열어 둔 채로 호스트가 게임을
        // 시작하거나 앱이 종료될 때 편집이 통째로 사라진다. 디스크 쓰기는 닫을 때 한 번으로
        // 미루므로 여기 드는 비용은 메모리 쓰기뿐이다 (#640).
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
