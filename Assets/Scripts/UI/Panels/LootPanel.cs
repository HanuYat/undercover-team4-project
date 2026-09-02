using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 약탈 창 — 기능 정지(Die)된 동료의 <b>소지품과 개인 자금을 확인하고</b> 골라 가져간다. (#487)
/// 약탈을 시작한 본인 클라이언트에서만 열린다(<see cref="PlayerLooter"/>가 서버 확인을 받은 뒤 호출).
///
/// <b>R은 확인용이고, 가져가는 것은 전부 칸 클릭이다</b> — 소지품은 <see cref="LootSlotView"/>,
/// 자금은 <see cref="LootFundsView"/>. 열어 보고 아무것도 안 가져간 채 떠날 수 있다.
/// <b>닫기는 ESC와 R 둘 다</b> — 연 손이 그대로 닫는다(<see cref="Update"/>).
///
/// <b>이 창은 권한이 아니다.</b> 열려 있다는 사실은 서버에서 아무것도 보장하지 않는다 — 칸을 누르면
/// 매번 서버가 처음부터 다시 검증한다(<see cref="PlayerLooter"/>). 아래의 자동 닫기는 순전히 UX이고,
/// 닫기가 늦거나 실패해도 규칙이 새지 않는다.
///
/// <b>목록은 로컬에서 읽는다.</b> 소지는 곧 부모 부착이고 부착은 NGO가 복제하므로 남의 소지품도
/// 이 클라에서 같은 답이 나온다(<see cref="HeldItems"/> 문서 주석) — 동기화 RPC가 따로 필요 없다.
/// 대신 <b>칸 배치</b>(피해자 화면의 1·2·3번 중 어디)는 오너 로컬이라 알 수 없어, 부착 순서대로 채운다.
///
/// <b>갱신 신호는 <see cref="PlayerLoadout.OnHeldItemsChangedAnyPeer"/>다</b> — 부착 지점의 자식 변화라
/// 전 피어에서 발생하고 원인을 가리지 않는다(내 약탈·남의 약탈·디스폰). <see cref="PlayerLoadout.OnSlotsChanged"/>와
/// 혼동하지 말 것: 그쪽은 서버 동기화 RPC(<c>SendTo.Owner</c>)에서 나오는 <b>오너 로컬</b> 이벤트라
/// 약탈자 화면에는 오지 않는다.
///
/// 입력 정지·커서 해제는 <see cref="PanelBase"/>가 다루지 않으므로 여기서 직접 한다
/// (<see cref="SignalInputPanel"/>과 같은 방침). <b>터는 동안은 움직이지 못한다</b> — 등 뒤가
/// 비는 것이 이 행동의 대가다.
/// </summary>
public class LootPanel : PanelBase
{
    public override bool CanCloseWithESC => true;

    public override bool IsStackable => true; // 창처럼 겹치는 모달

    [Header("슬롯")]
    [Tooltip("소지 5칸(GDD 10-1)에 맞춘 칸 뷰. 부착 순서대로 채운다")]
    [SerializeField]
    private LootSlotView[] m_slotViews;

    [Tooltip("개인 자금 칸 — 누르면 전액 가져간다. 없어도 소지품 약탈은 동작한다")]
    [SerializeField]
    private LootFundsView m_fundsView;

    [Header("문구")]
    [Tooltip("창 제목 — 예: HudTable/Hud.Loot.Title. 비우면 제목을 건드리지 않는다")]
    [SerializeField]
    private LocalizedString m_title;

    [SerializeField]
    private TMP_Text m_titleText;

    [Header("자동 닫기")]
    [Tooltip(
        "이 거리(m)를 넘게 벌어지면 창을 닫는다. 조준 사거리(3m)보다 넉넉하게 둘 것 — 경계에서 "
        + "창이 깜빡이지 않게 하려는 여유이고, 실제 거부는 서버가 한다"
    )]
    [SerializeField]
    private float m_keepOpenRange = 4.5f;

    private PlayerLooter m_looter;
    private PlayerLootable m_victim;
    private PlayerInputHandler m_input; // 창을 연 플레이어의 입력 — 닫을 때 되돌린다
    private PlayerIncapacitation m_looterIncapacitation; // 터던 사람이 쓰러지면 창을 닫는 판정
    private PlayerLoadout m_watched; // 부착 변화를 구독 중인 대상의 소지품 — 해제 기준

    // 창이 열린 프레임. R로 닫을 때 <b>열게 한 그 누름</b>을 닫기로 다시 세지 않기 위한 기준이다 —
    // 호스트·오프라인에서는 R 콜백에서 서버 검증까지 한 프레임에 끝나 Open이 같은 프레임에 돌고,
    // 그 프레임의 Update에서도 뒤지기 키는 아직 "이번 프레임에 눌림"이다.
    private int m_openedFrame = -1;

    // 커서 Push/Pop 짝을 지키는 래치. OpenPanel/ClosePanel엔 재진입 가드가 없어 같은 값으로 두 번
    // 불릴 수 있는데, Push만 두 번(또는 Pop만 두 번) 들어가면 전역 요청 수가 어긋나 커서가 영영
    // 풀리거나 영영 잠긴다. (SignalInputPanel·PausePanel과 같은 방침, #352)

    // 표시 목록 버퍼 — 매 프레임 대조하므로 새로 만들지 않는다.
    private readonly List<ItemBase> m_lootable = new List<ItemBase>();

    // 마지막으로 그린 내용. 바뀔 때만 다시 그린다 — 지역화 구독을 매 프레임 갈아 끼우지 않기 위해서다
    // (Bind가 StringChanged를 재구독한다). (InventoryBarView.m_lastSlots와 같은 방식)
    private ItemBase[] m_lastShown;

    protected override void Awake()
    {
        base.Awake();

        // 배선이 빠진 구성에서도 아래 순회들이 0칸으로 그냥 돌게 한다 — 칸 배열의 null 가드를
        // 여기 한 곳으로 모은다. RefreshSlots·HasChanged·ClearSlots가 매번 다시 확인하면
        // 기준이 갈라져, Awake만 살아남고 Open 순간에 터지는 구성이 생긴다.
        if (m_slotViews == null)
            m_slotViews = new LootSlotView[0];

        m_lastShown = new ItemBase[m_slotViews.Length];

        foreach (LootSlotView slot in m_slotViews)
        {
            if (slot != null)
                slot.Setup(this);
        }

        if (m_fundsView != null)
            m_fundsView.Setup(this);

        if (!m_title.IsEmpty)
            m_title.StringChanged += HandleTitleChanged;
    }

    protected override void OnDestroy()
    {
        if (!m_title.IsEmpty)
            m_title.StringChanged -= HandleTitleChanged;

        base.OnDestroy();
    }

    private void HandleTitleChanged(string localizedTitle)
    {
        if (m_titleText != null)
            m_titleText.text = localizedTitle;
    }

    /// <summary>
    /// 약탈 창을 연다 — 서버가 대상을 확인해 준 뒤 <see cref="PlayerLooter"/>가 오너 클라에서 부른다.
    ///
    /// <b>여는 것만으로는 아무것도 넘어오지 않는다.</b> 소지품도 자금도 칸을 눌러야 옮겨진다 —
    /// 열어 보고 그냥 떠날 수 있다.
    /// </summary>
    /// <param name="availableFunds">
    /// 대상의 잔액. 서버가 <b>이 약탈자에게만</b> 실어 보낸 값이다 — 잔액 NetworkVariable의 읽기 권한은
    /// 여전히 Owner라(#484) 클라가 스스로 읽은 것이 아니다. 연 순간의 <b>스냅숏</b>이므로 남이 먼저
    /// 털어 가면 어긋날 수 있고, 그때는 눌러도 서버가 실제 잔액(0)만 옮긴다.
    /// </param>
    public void Open(PlayerLooter looter, PlayerLootable victim, int availableFunds)
    {
        if (IsOpened || looter == null || victim == null)
            return;

        m_looter = looter;
        m_victim = victim;
        m_input = looter.GetComponent<PlayerInputHandler>();
        m_looterIncapacitation = looter.GetComponent<PlayerIncapacitation>();

        // 대상의 부착 목록이 바뀌면 다시 그린다 — 내가 가져갔든, 다른 동료가 같은 시체를 털었든,
        // 아이템이 디스폰됐든 원인을 가리지 않는다 (#487).
        m_watched = victim.Loadout;
        if (m_watched != null)
            m_watched.OnHeldItemsChangedAnyPeer += HandleVictimItemsChanged;

        m_openedFrame = Time.frameCount;

        RefreshSlots(force: true);

        if (m_fundsView != null)
            m_fundsView.Bind(availableFunds);

        SetBlocked(true);
        OpenPanel();
    }

    /// <summary>
    /// 자금 칸을 갱신한다 — 서버가 이전 결과를 알려 줄 때 <see cref="PlayerLooter"/>가 부른다.
    /// 창이 그 사이 다른 시체로 옮겨 갔을 수 있어 <b>대상을 대조한다</b>.
    /// </summary>
    internal void SetFunds(PlayerLootable victim, int amount)
    {
        if (!IsOpened || victim == null || victim != m_victim)
            return;

        if (m_fundsView != null)
            m_fundsView.Bind(amount);
    }

    /// <summary>
    /// 닫기 — ESC 스택·자동 닫기·씬 정리가 모두 여기로 모인다.
    /// 정지시킨 입력과 커서를 반드시 여기서 되돌린다.
    /// </summary>
    public override void ClosePanel()
    {
        if (!IsOpened)
            return; // 중복 호출로 CursorLock 참조 수가 어긋나지 않게

        base.ClosePanel();

        SetBlocked(false);

        // 구독 해제 기준을 m_victim이 아니라 구독한 그 대상으로 잡는다 — 창이 다른 시체로 옮겨 갔거나
        // 대상의 Loadout이 도중에 바뀌어도 "구독한 곳에서 뗀다"가 성립한다.
        //
        // 단, 이건 InventorySlotView.m_boundName과 같은 사정이 아니다: 그쪽 LocalizedString은 순수
        // C# 객체라 대상이 파괴돼도 참조가 살아 있지만, m_watched는 PlayerLoadout(UnityEngine.Object)라
        // 대상과 함께 파괴되면 여기도 똑같이 가짜 null이 되어 이 해제가 스킵된다. 그래도 새지 않는 이유는
        // 델리게이트를 들고 있는 HeldItemsWatcher가 같은 순간에 함께 파괴되기 때문이다 —
        // 참조 종류가 아니라 수명이 같다는 점이 근거다.
        if (m_watched != null)
            m_watched.OnHeldItemsChangedAnyPeer -= HandleVictimItemsChanged;
        m_watched = null;

        m_looter = null;
        m_victim = null;
        m_input = null;
        m_looterIncapacitation = null;

        // 칸이 죽은 아이템 참조와 지역화 구독을 들고 있지 않게 비운다
        ClearSlots();
    }

    /// <summary>
    /// 이 컴포넌트가 파괴·비활성되는 마지막 순간의 안전망 — 씬 전환이 대표적이다.
    /// 창이 열린 채 사라지면 정지시킨 입력과 커서 해제 요청을 되돌릴 주체가 없어진다.
    /// (SignalInputPanel과 같은 사정)
    /// </summary>
    private void OnDisable()
    {
        ClosePanel();
        SetBlocked(false); // 창이 이미 닫힌 뒤라도 래치가 켜져 있으면 짝이 안 맞은 것이다
    }

    /// <summary>입력을 멈출 대상 — 공용 SetBlocked(PanelBase)가 읽는다.</summary>
    protected override PlayerInputHandler BlockTarget => m_input;

    // 칸 내용은 이벤트로만 갱신한다 — Update가 남아 있는 것은 <b>거리</b> 때문이다.
    // 대상이 밧줄에 끌려 멀어지는 것(#365)에는 이벤트가 없어서 매 프레임 볼 수밖에 없다.
    private void Update()
    {
        if (!IsOpened)
            return;

        if (!CanKeepOpen())
        {
            ClosePanel();
            return;
        }

        // R로도 닫는다 — 연 키가 곧 닫는 키다. 여기서 폴링하는 이유는 이 창이 스스로 상호작용
        // 액션을 꺼 두기 때문이다(SetBlocked) — 콜백은 오지 않는다.
        // m_input이 null이면 위 CanKeepOpen에서 이미 닫혔다.
        if (Time.frameCount != m_openedFrame && m_input.WasLootPressedThisFrame())
            ClosePanel();
    }

    // 대상의 부착 목록이 바뀌었다 — 원인을 가리지 않는다(내 약탈·남의 약탈·디스폰).
    // 자식 재정렬처럼 내용이 그대로인 경우도 오므로, 대조해서 실제로 바뀌었을 때만 다시 그린다.
    private void HandleVictimItemsChanged() => RefreshSlots(force: false);

    // 창을 계속 띄워 둘 조건 — 어느 하나라도 깨지면 닫는다. 서버 판정과 별개인 UX 판정이다.
    private bool CanKeepOpen()
    {
        // 창을 연 플레이어가 디스폰(퇴장·라운드 종료)되면 정지된 입력을 되돌릴 대상이 사라진다
        if (m_looter == null || m_input == null)
            return false;

        // 터던 사람이 쓰러졌다 — 서버는 이미 거부하지만(PlayerLooter.CanLoot) 창은 그 사실을 모른다.
        // 이 검사가 없으면 동작하지 않는 창을 띄운 채 입력이 정지된 상태로 쓰러져 있게 된다:
        // 오브젝트는 파괴되지 않고 대상도 그대로이며 스스로 움직일 수 없어 거리 조건도 유지되기 때문이다.
        if (m_looterIncapacitation != null && m_looterIncapacitation.IsIncapacitated)
            return false;

        // 대상이 부활했거나(본부 이송 #365) 사라졌으면 털 것이 없다
        if (m_victim == null || !m_victim.CanBeLooted)
            return false;

        // 입력을 정지시켜 스스로는 못 움직이지만, 남이 시체를 밧줄로 끌고 갈 수 있다 (#365)
        Vector3 delta = m_victim.transform.position - m_looter.transform.position;
        return delta.sqrMagnitude <= m_keepOpenRange * m_keepOpenRange;
    }

    // 대상의 소지품을 다시 읽어 칸에 채운다. 내용이 그대로면 아무것도 하지 않는다.
    private void RefreshSlots(bool force)
    {
        m_lootable.Clear();
        if (m_victim != null && m_victim.Loadout != null)
            m_victim.Loadout.CollectDetachableItems(m_lootable);

        if (!force && !HasChanged())
            return;

        for (int i = 0; i < m_slotViews.Length; i++)
        {
            ItemBase item = i < m_lootable.Count ? m_lootable[i] : null;
            m_lastShown[i] = item;

            if (m_slotViews[i] != null)
                m_slotViews[i].Bind(item);
        }
    }

    // 그려 둔 내용과 지금 목록이 다른가 — 참조 비교. 아이템이 파괴됐으면 Unity 가짜 null이라
    // 새 목록에서는 빠지고, 이 대조에 걸려 칸이 비워진다.
    private bool HasChanged()
    {
        for (int i = 0; i < m_slotViews.Length; i++)
        {
            ItemBase item = i < m_lootable.Count ? m_lootable[i] : null;
            if (m_lastShown[i] != item)
                return true;
        }

        return false;
    }

    private void ClearSlots()
    {
        for (int i = 0; i < m_slotViews.Length; i++)
        {
            m_lastShown[i] = null;
            if (m_slotViews[i] != null)
                m_slotViews[i].Bind(null);
        }

        if (m_fundsView != null)
            m_fundsView.Bind(0);
    }

    /// <summary>
    /// 칸을 눌렀다 — 가져가기를 서버에 요청한다. <see cref="LootSlotView"/>가 호출.
    /// 성공하면 대상의 부착 목록이 바뀌고, 다음 <see cref="RefreshSlots"/>가 칸을 비운다.
    /// 실패(슬롯 꽉 참 등)는 서버가 오너 로그로 알린다 — 창은 그대로 둔다.
    /// </summary>
    internal void RequestTake(ItemBase item)
    {
        if (m_looter == null || m_victim == null || item == null)
            return;

        m_looter.RequestTakeItem(m_victim, item);
    }

    /// <summary>
    /// 자금 칸을 눌렀다 — 전액 가져가기를 서버에 요청한다. <see cref="LootFundsView"/>가 호출.
    /// 결과(옮긴 금액)는 서버가 오너에게 돌려주고, 그때 <see cref="SetFunds"/>로 칸이 비워진다.
    /// </summary>
    internal void RequestTakeFunds()
    {
        if (m_looter == null || m_victim == null)
            return;

        m_looter.RequestTakeFunds(m_victim);
    }
}
