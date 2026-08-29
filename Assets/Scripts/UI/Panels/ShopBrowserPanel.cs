using System;
using Cysharp.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;
using UnityEngine.UI;

/// <summary>
/// 장비 주문창 (#843) — 카탈로그 아이템(<see cref="ShopCatalogItem"/>)을 쓰면 열리는 브라우저 창.
/// 이번 라운드 진열 칸을 그리드로 그리고, 우상단에 팀 잔액을 띄운다.
///
/// <b>이 창은 권한이 아니다.</b> 열려 있다는 사실은 서버에서 아무것도 보장하지 않는다 — 주문 버튼을
/// 누르면 <see cref="ShopLineup"/>에 칸 번호를 보내고, 가격·품절·자금은 서버가 자기 NetworkList로
/// 처음부터 다시 판정한다 (<see cref="LootPanel"/>과 같은 방침).
///
/// 진열대가 없어진 뒤로 이름·가격·설명이 보이는 곳은 여기뿐이다.
///
/// 화면은 탭 둘로 갈린다 (#840) — 진열 그리드와 구매 내역(<see cref="ShopPurchaseHistoryView"/>).
/// 창을 열 때마다 진열 탭에서 시작한다.
/// </summary>
public class ShopBrowserPanel : PanelBase
{
    private const string k_commonTable = "CommonTable";
    private const string k_shopTable = "ShopTable";

    // 구매 응답 사유는 규약 키로 조회한다 — 서버가 보내는 것은 enum뿐이다 (#525)
    private const string k_replyPrefix = "Shop.Reply.";
    private const string k_moneyKey = "Common.Unit.Money";

    public override bool CanCloseWithESC => true;

    public override bool IsStackable => true; // 창처럼 겹치는 모달

    [Header("목록")]
    [Tooltip("이번 라운드 진열 칸을 들고 있는 홀더 — 같은 씬이라 인스펙터로 잡는다")]
    [SerializeField]
    private ShopLineup m_lineup;

    [Tooltip("그리드 칸. 부착 순서대로 진열 칸에 대응한다")]
    [SerializeField]
    private ShopOrderSlotView[] m_slotViews;

    [Header("탭")]
    [Tooltip("진열 탭이 켜는 것 — 그리드 루트")]
    [SerializeField]
    private GameObject m_catalogView;

    [Tooltip("구매 내역 탭이 켜는 것 — ShopPurchaseHistoryView 루트")]
    [SerializeField]
    private GameObject m_historyView;

    [SerializeField]
    private Button m_catalogTab;

    [SerializeField]
    private Button m_historyTab;

    [Tooltip("선택된 탭 배경 / 글자색")]
    [SerializeField]
    private Color m_tabOnColor = new Color(0.106f, 0.208f, 0.286f, 1f);

    [SerializeField]
    private Color m_tabOnTextColor = new Color(0.541f, 0.902f, 1f, 1f);

    [Tooltip("선택되지 않은 탭 배경 / 글자색")]
    [SerializeField]
    private Color m_tabOffColor = new Color(0.043f, 0.098f, 0.141f, 1f);

    [SerializeField]
    private Color m_tabOffTextColor = new Color(0.443f, 0.514f, 0.573f, 1f);

    [Header("문구")]
    [Tooltip("창 제목 — 예: ShopTable/Shop.Order.Title. 비우면 제목을 건드리지 않는다")]
    [SerializeField]
    private LocalizedString m_title;

    [SerializeField]
    private TMP_Text m_titleText;

    [Tooltip("우상단 팀 잔액")]
    [SerializeField]
    private TMP_Text m_balanceText;

    [Tooltip("주문 결과(성공·자금 부족 등) 알림줄")]
    [SerializeField]
    private TMP_Text m_noticeText;

    [Tooltip("알림 표시 유지 시간(초)")]
    [SerializeField]
    private float m_noticeSeconds = 2.5f;

    [Header("열기 연출")]
    [Tooltip("창이 뜨는 시간(초). 0이면 바로 뜬다")]
    [SerializeField]
    private float m_openSeconds = 0.12f;

    [Tooltip("시작 크기 배율 — 이 값에서 1로 커지며 뜬다")]
    [SerializeField]
    private float m_openFromScale = 0.94f;

    private PlayerInputHandler m_input; // 창을 연 플레이어의 입력 — 닫을 때 되돌린다
    private TeamFund m_teamFund;

    // 커서 Push/Pop 짝을 지키는 래치 (LootPanel과 같은 사정, #352)
    private bool m_blocked;

    // 구독해 둔 홀더 — 해제 기준을 지금 인스펙터 값이 아니라 실제로 구독한 그 참조로 잡는다
    private ShopLineup m_bound;

    // 알림 자동 숨김 예약의 세대 번호 — 새 알림·지우기가 번호를 올리면 먼저 걸린 예약은 스스로 물러난다
    private int m_noticeVersion;

    // 열기 연출용 — 패널 루트에서 잡는다 (없으면 붙인다)
    private RectTransform m_rootRect;
    private CanvasGroup m_rootGroup;

    // 열기 연출의 세대 번호 — 알림과 같은 방식이다
    private int m_openVersion;

    // 지금 구매 내역 탭인가 — 창을 열 때마다 진열로 되돌린다
    private bool m_historyShown;

    protected override void Awake()
    {
        base.Awake();

        // base.Awake가 m_panelRoot를 확정한 뒤라야 잡을 수 있다
        m_rootRect = m_panelRoot.transform as RectTransform;
        m_rootGroup = m_panelRoot.GetComponent<CanvasGroup>();
        if (m_rootGroup == null)
            m_rootGroup = m_panelRoot.AddComponent<CanvasGroup>();

        if (m_slotViews == null)
            m_slotViews = new ShopOrderSlotView[0];

        foreach (ShopOrderSlotView slot in m_slotViews)
        {
            if (slot != null)
                slot.Setup(this);
        }

        if (!m_title.IsEmpty)
            m_title.StringChanged += HandleTitleChanged;

        if (m_catalogTab != null)
            m_catalogTab.onClick.AddListener(HandleCatalogTabClicked);
        if (m_historyTab != null)
            m_historyTab.onClick.AddListener(HandleHistoryTabClicked);

        // 여기서는 어느 쪽을 켤지만 정한다 — 라벨은 로컬라이제이션이 준비된 뒤 OnOpen이 채운다
        SwapTabViews(false);
    }

    protected override void OnDestroy()
    {
        if (!m_title.IsEmpty)
            m_title.StringChanged -= HandleTitleChanged;

        if (m_catalogTab != null)
            m_catalogTab.onClick.RemoveListener(HandleCatalogTabClicked);
        if (m_historyTab != null)
            m_historyTab.onClick.RemoveListener(HandleHistoryTabClicked);

        base.OnDestroy();
    }

    private void HandleTitleChanged(string localizedTitle)
    {
        if (m_titleText != null)
            m_titleText.text = localizedTitle;
    }

    /// <summary>주문창을 연다 — 카탈로그 아이템이 오너 클라에서 부른다.</summary>
    public void Open(PlayerInteractor holder)
    {
        if (IsOpened || holder == null)
            return;

        m_input = holder.GetComponent<PlayerInputHandler>();

        BindLineup();
        BindFund();
        ClearNotice();
        ShowTab(false); // 열 때는 늘 진열부터

        // 언어가 바뀌면 칸을 통째로 다시 채운다 — 칸마다 StringChanged를 걸지 않는 이유가 이것이다
        LocalizationSettings.SelectedLocaleChanged += HandleLocaleChanged;

        SetBlocked(true);
        OpenPanel();
    }

    /// <summary>연출을 붙이려고 가로챈다 — 창이 뚝 나타나지 않게 아주 짧게 키우며 띄운다.</summary>
    public override void OpenPanel()
    {
        base.OpenPanel();

        PlayOpenAsync(++m_openVersion).Forget();
    }

    /// <summary>닫기 — ESC 스택·씬 정리가 모두 여기로 모인다. 정지시킨 입력과 커서를 반드시 되돌린다.</summary>
    public override void ClosePanel()
    {
        if (!IsOpened)
            return; // 중복 호출로 CursorLock 참조 수가 어긋나지 않게

        base.ClosePanel();

        ResetOpenVisual();
        SetBlocked(false);

        // 종료 중에는 설정 에셋을 되살리지 않는다 (ShopStand 관례)
        if (LocalizationSettings.HasSettings)
            LocalizationSettings.SelectedLocaleChanged -= HandleLocaleChanged;

        UnbindLineup();
        UnbindFund();

        m_input = null;
        ClearNotice();
    }

    /// <summary>
    /// 파괴·비활성되는 마지막 순간의 안전망 — 씬 전환이 대표적이다.
    /// 창이 열린 채 사라지면 정지시킨 입력과 커서 해제 요청을 되돌릴 주체가 없어진다.
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

        // ?. 금지 — 파괴된 Unity 오브젝트의 fake null 우회 방지
        if (m_input != null)
            m_input.SetSuspended(blocked);
    }

    private void Update()
    {
        if (!IsOpened)
            return;

        // 창을 연 플레이어가 디스폰(퇴장·라운드 종료)되면 정지된 입력을 되돌릴 대상이 사라진다
        if (m_input == null)
        {
            ClosePanel();
            return;
        }

        // 자금 홀더는 세션 상주물이라 보통 이미 스폰돼 있지만, 원격 클라는 늦게 도착할 수 있다
        if (m_teamFund == null)
            BindFund();
    }

    // ---- 슬롯 ----

    private void BindLineup()
    {
        UnbindLineup();

        if (m_lineup == null)
        {
            Debug.LogWarning("주문창: ShopLineup이 배선되지 않아 목록을 그릴 수 없다", this);
            RefreshSlots();
            return;
        }

        m_bound = m_lineup;
        m_bound.OnChanged += RefreshSlots;
        m_bound.OnPurchaseReply += ShowNotice;

        RefreshSlots();
    }

    private void UnbindLineup()
    {
        if (m_bound != null)
        {
            m_bound.OnChanged -= RefreshSlots;
            m_bound.OnPurchaseReply -= ShowNotice;
        }

        m_bound = null;
    }

    // 칸을 다시 채운다. 진열 칸이 그리드 칸보다 많으면 남는 칸은 그리지 않는다 — 배선 실수는
    // 칸이 비는 것으로 드러나고, 적으면 남는 칸이 빈 칸으로 표시된다.
    private void RefreshSlots()
    {
        int slotCount = m_lineup != null ? m_lineup.SlotCount : 0;

        for (int i = 0; i < m_slotViews.Length; i++)
        {
            if (m_slotViews[i] == null)
                continue;

            bool filled = i < slotCount;
            m_slotViews[i].Bind(
                i,
                filled ? m_lineup.GetEntry(i) : null,
                filled ? m_lineup.GetStatus(i) : EShopSlotStatus.Available
            );
        }
    }

    private void HandleLocaleChanged(Locale locale)
    {
        RefreshSlots();
        RefreshTabLabels();

        if (m_teamFund != null)
            RefreshBalance(m_teamFund.Balance);
    }

    // ---- 탭 ----

    private void HandleCatalogTabClicked() => ShowTab(false);

    private void HandleHistoryTabClicked() => ShowTab(true);

    // 탭 하나만 켜고 나머지를 끈다. 구매 내역 뷰는 꺼져 있는 동안 구독도 함께 풀린다(OnDisable).
    private void ShowTab(bool history)
    {
        SwapTabViews(history);
        RefreshTabLabels();
    }

    // 라벨을 건드리지 않는 절반 — Awake처럼 로컬라이제이션이 아직 준비되지 않은 시점에서 쓴다
    private void SwapTabViews(bool history)
    {
        m_historyShown = history;

        if (m_catalogView != null)
            m_catalogView.SetActive(!history);
        if (m_historyView != null)
            m_historyView.SetActive(history);
    }

    private void RefreshTabLabels()
    {
        ApplyTabLook(m_catalogTab, !m_historyShown, "Shop.Tab.Catalog");
        ApplyTabLook(m_historyTab, m_historyShown, "Shop.History.Title");
    }

    private void ApplyTabLook(Button tab, bool selected, string labelKey)
    {
        if (tab == null)
            return;

        Image background = tab.GetComponent<Image>();
        if (background != null)
            background.color = selected ? m_tabOnColor : m_tabOffColor;

        TMP_Text label = tab.GetComponentInChildren<TMP_Text>();
        if (label != null)
        {
            label.text = LocalizedStrings.Get(k_shopTable, labelKey);
            label.color = selected ? m_tabOnTextColor : m_tabOffTextColor;
        }
    }

    // ---- 잔액 ----

    private void BindFund()
    {
        TeamFund fund = App.Game.TeamFund;
        if (fund == null || !fund.IsSpawned)
            return;

        m_teamFund = fund;
        m_teamFund.Fund.OnValueChanged += HandleFundChanged;
        RefreshBalance(m_teamFund.Balance);
    }

    private void UnbindFund()
    {
        if (m_teamFund != null)
            m_teamFund.Fund.OnValueChanged -= HandleFundChanged;

        m_teamFund = null;
    }

    private void HandleFundChanged(int previous, int current) => RefreshBalance(current);

    private void RefreshBalance(int balance)
    {
        if (m_balanceText != null)
            m_balanceText.text = LocalizedStrings.Get(k_commonTable, k_moneyKey, balance);
    }

    // ---- 주문 ----

    /// <summary>
    /// 주문 버튼을 눌렀다 — 서버에 요청한다. <see cref="ShopOrderSlotView"/>가 호출.
    /// 성공하면 칸 상태가 바뀌고 <see cref="RefreshSlots"/>가 칸을 품절로 다시 그린다.
    /// 실패 사유는 서버가 요청자에게만 보내 알림줄에 뜬다.
    /// </summary>
    internal void RequestOrder(int slot)
    {
        if (m_lineup == null)
            return;

        m_lineup.RequestPurchase(slot);
    }

    // 서버는 사유만 보내고 문구는 여기서 자기 로케일로 조회한다 — 규약 키 Shop.Reply.<enum 이름> (#525)
    private void ShowNotice(EShopReply reply)
    {
        if (m_noticeText == null)
            return;

        m_noticeText.text = LocalizedStrings.Get(k_shopTable, k_replyPrefix + reply);
        HideNoticeAfterAsync(++m_noticeVersion).Forget();
    }

    private async UniTaskVoid HideNoticeAfterAsync(int version)
    {
        await UniTask.Delay(
            TimeSpan.FromSeconds(m_noticeSeconds),
            cancellationToken: this.GetCancellationTokenOnDestroy()
        );

        // 그 사이 새 알림·지우기가 번호를 올렸으면 낡은 예약이므로 물러난다
        if (version != m_noticeVersion)
            return;

        ClearNotice();
    }

    private void ClearNotice()
    {
        m_noticeVersion++; // 걸려 있던 예약 무효화

        if (m_noticeText != null)
            m_noticeText.text = string.Empty;
    }

    // ---- 열기 연출 ----

    // 시간 배율에 걸리지 않게 unscaled로 돈다 — 창이 열려 있는 동안 게임이 멎을 수 있다.
    private async UniTaskVoid PlayOpenAsync(int version)
    {
        if (m_openSeconds <= 0f)
        {
            ResetOpenVisual();
            return;
        }

        float elapsed = 0f;
        while (elapsed < m_openSeconds)
        {
            // 그 사이 닫히거나 다시 열렸으면 낡은 연출이므로 물러난다 — 마무리는 새 쪽이 한다
            if (version != m_openVersion)
                return;

            ApplyOpenProgress(elapsed / m_openSeconds);

            elapsed += Time.unscaledDeltaTime;
            await UniTask.Yield(PlayerLoopTiming.Update, this.GetCancellationTokenOnDestroy());
        }

        if (version == m_openVersion)
            ApplyOpenProgress(1f);
    }

    private void ApplyOpenProgress(float progress)
    {
        float eased = 1f - (1f - progress) * (1f - progress); // 처음이 빠르고 끝이 잦아든다

        if (m_rootGroup != null)
            m_rootGroup.alpha = eased;

        if (m_rootRect != null)
            m_rootRect.localScale = Vector3.one * Mathf.LerpUnclamped(m_openFromScale, 1f, eased);
    }

    // 연출 중에 닫혀도 다음에 열 때 찌그러진 채로 남지 않게 되돌린다
    private void ResetOpenVisual()
    {
        m_openVersion++;
        ApplyOpenProgress(1f);
    }
}
