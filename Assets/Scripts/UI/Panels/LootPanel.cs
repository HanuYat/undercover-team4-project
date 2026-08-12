using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 약탈 창 — 기능 정지(Die)된 동료의 소지품을 보고 골라 가져간다. (#487)
/// 약탈을 시작한 본인 클라이언트에서만 열린다(<see cref="PlayerLooter"/>가 서버 확인을 받은 뒤 호출).
///
/// <b>이 창은 권한이 아니다.</b> 열려 있다는 사실은 서버에서 아무것도 보장하지 않는다 — 칸을 누르면
/// 매번 서버가 처음부터 다시 검증한다(<see cref="PlayerLooter"/>). 아래의 자동 닫기는 순전히 UX이고,
/// 닫기가 늦거나 실패해도 규칙이 새지 않는다.
///
/// <b>목록은 로컬에서 읽는다.</b> 소지는 곧 부모 부착이고 부착은 NGO가 복제하므로 남의 소지품도
/// 이 클라에서 같은 답이 나온다(<see cref="HeldItems"/> 문서 주석) — 동기화 RPC가 따로 필요 없다.
/// 대신 <b>칸 배치</b>(피해자 화면의 1·2·3번 중 어디)는 오너 로컬이라 알 수 없어, 부착 순서대로 채운다.
/// 그래서 변화 감지도 이벤트가 아니라 폴링이다 — <see cref="PlayerLoadout.OnSlotsChanged"/>는
/// 그 소지품 주인의 클라에서만 발행되어 약탈자 화면에는 오지 않는다.
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
    [Tooltip("소지 3칸(GDD 10-1)에 맞춘 칸 뷰. 부착 순서대로 채운다")]
    [SerializeField]
    private LootSlotView[] m_slotViews;

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
    private PlayerLoadout m_watched; // 부착 변화를 구독 중인 대상의 소지품 — 해제 기준

    // 커서 Push/Pop 짝을 지키는 래치. OpenPanel/ClosePanel엔 재진입 가드가 없어 같은 값으로 두 번
    // 불릴 수 있는데, Push만 두 번(또는 Pop만 두 번) 들어가면 전역 요청 수가 어긋나 커서가 영영
    // 풀리거나 영영 잠긴다. (SignalInputPanel·PausePanel과 같은 방침, #352)
    private bool m_blocked;

    // 표시 목록 버퍼 — 매 프레임 대조하므로 새로 만들지 않는다.
    private readonly List<ItemBase> m_lootable = new List<ItemBase>();

    // 마지막으로 그린 내용. 바뀔 때만 다시 그린다 — 지역화 구독을 매 프레임 갈아 끼우지 않기 위해서다
    // (Bind가 StringChanged를 재구독한다). (InventoryBarView.m_lastSlots와 같은 방식)
    private ItemBase[] m_lastShown;

    protected override void Awake()
    {
        base.Awake();

        m_lastShown = new ItemBase[m_slotViews != null ? m_slotViews.Length : 0];

        if (m_slotViews != null)
        {
            foreach (LootSlotView slot in m_slotViews)
            {
                if (slot != null)
                    slot.Setup(this);
            }
        }

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
    /// 개인 자금은 이 시점에 이미 넘어와 있다(열기의 일부다).
    /// </summary>
    public void Open(PlayerLooter looter, PlayerLootable victim)
    {
        if (IsOpened || looter == null || victim == null)
            return;

        m_looter = looter;
        m_victim = victim;
        m_input = looter.GetComponent<PlayerInputHandler>();

        // 대상의 부착 목록이 바뀌면 다시 그린다 — 내가 가져갔든, 다른 동료가 같은 시체를 털었든,
        // 아이템이 디스폰됐든 원인을 가리지 않는다 (#487).
        m_watched = victim.Loadout;
        if (m_watched != null)
            m_watched.OnHeldItemsChangedAnyPeer += HandleVictimItemsChanged;

        RefreshSlots(force: true);

        SetBlocked(true);
        OpenPanel();
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

        // 구독 해제 기준을 m_victim이 아니라 이 참조로 잡는다 — 대상이 파괴되면 Unity 가짜 null이라
        // 대상 기준 해제가 통째로 스킵되고 구독이 남는다. (InventorySlotView.m_boundName과 같은 사정)
        if (m_watched != null)
            m_watched.OnHeldItemsChangedAnyPeer -= HandleVictimItemsChanged;
        m_watched = null;

        m_looter = null;
        m_victim = null;
        m_input = null;

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

    // 커서 해제·입력 정지를 한 쌍으로 묶는다. 래치 덕에 몇 번 불려도 Push/Pop은 1:1로 유지된다.
    private void SetBlocked(bool blocked)
    {
        if (m_blocked == blocked)
            return;

        m_blocked = blocked;

        // 플레이어 조회보다 먼저 — 플레이어가 도중에 사라져도 Push/Pop 짝은 유지돼야 한다 (#352)
        if (blocked)
            CursorLock.PushUnlock();
        else
            CursorLock.PopUnlock();

        // 터는 동안은 움직이지 못한다 — 커서를 푼 채 WASD가 이동으로 새는 것도 함께 막힌다.
        // ?. 금지 — Unity 오브젝트의 ?.는 C# 참조 null만 보고 파괴 판정(fake null)을 우회한다.
        if (m_input != null)
            m_input.SetSuspended(blocked);
    }

    // 칸 내용은 이벤트로만 갱신한다 — Update가 남아 있는 것은 <b>거리</b> 때문이다.
    // 대상이 밧줄에 끌려 멀어지는 것(#365)에는 이벤트가 없어서 매 프레임 볼 수밖에 없다.
    private void Update()
    {
        if (!IsOpened)
            return;

        if (!CanKeepOpen())
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
}
