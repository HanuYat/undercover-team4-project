using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Localization;
using UnityEngine.UI;

/// <summary>
/// 약탈 창의 개인 자금 칸 (#487). 털 대상의 잔액을 보여 주고, 누르면 전액 가져가기를 요청한다.
/// 로직은 <see cref="LootPanel"/>이 소유한다 — 이 칸은 표시와 클릭 전달만 한다
/// (<see cref="LootSlotView"/>와 같은 축).
///
/// <b>소지품 칸과 나눠 둔 이유</b>는 다루는 것이 다르기 때문이다: 소지품 칸은 <see cref="ItemBase"/>
/// 하나를 쥐고 아이콘·이름을 그리지만, 여기는 숫자 하나뿐이고 <b>칸이 하나로 고정</b>이다.
/// 한 컴포넌트에 얹으면 "아이템이면 이쪽, 아니면 저쪽" 분기가 표시 코드 전체에 퍼진다.
///
/// <b>표시하는 금액은 스냅숏이다.</b> 창이 떠 있는 동안 남이 먼저 털어 갈 수 있고, 그때 눌러도
/// 서버가 실제 잔액(0)만 옮긴다 — 서버 잔액이 언제나 진실이다 (<see cref="PlayerLooter"/>).
/// </summary>
public class LootFundsView : MonoBehaviour, IPointerClickHandler
{
    [Header("표시")]
    [SerializeField]
    private Image m_background;

    [Tooltip("칸 문구 — 예: HudTable/Hud.Loot.Funds. 비우면 금액만 그린다")]
    [SerializeField]
    private LocalizedString m_label;

    [SerializeField]
    private TextMeshProUGUI m_amountText;

    [Header("색")]
    [SerializeField]
    private Color m_filledColor = new Color(0f, 0f, 0f, 0.5f);

    [SerializeField]
    private Color m_emptyColor = new Color(0f, 0f, 0f, 0.2f);

    private LootPanel m_owner;
    private int m_amount;

    // 마지막으로 받아 둔 지역화 문구. 금액이 바뀔 때마다 지역화를 다시 부르지 않으려고 캐시한다
    // — 문구는 언어가 바뀔 때만 달라지고, 금액은 그보다 훨씬 자주 바뀐다.
    private string m_localizedLabel = string.Empty;

    /// <summary>이 칸이 표시 중인 금액. 빈 지갑이면 0.</summary>
    public int Amount => m_amount;

    /// <summary>창이 1회 호출 — 소유 창을 연결한다.</summary>
    public void Setup(LootPanel owner) => m_owner = owner;

    private void Awake()
    {
        // 구독 즉시 현재 언어 값으로 1회 호출되고, 이후 언어 전환 시마다 다시 호출된다 (#251)
        if (!m_label.IsEmpty)
            m_label.StringChanged += HandleLabelChanged;
    }

    private void OnDestroy()
    {
        if (!m_label.IsEmpty)
            m_label.StringChanged -= HandleLabelChanged;
    }

    private void HandleLabelChanged(string localizedLabel)
    {
        m_localizedLabel = localizedLabel;
        Redraw();
    }

    /// <summary>칸 내용 갱신. 0 = 빈 지갑(눌러도 아무 일도 일어나지 않는다).</summary>
    public void Bind(int amount)
    {
        m_amount = amount;
        Redraw();
    }

    private void Redraw()
    {
        if (m_background != null)
            m_background.color = m_amount > 0 ? m_filledColor : m_emptyColor;

        if (m_amountText == null)
            return;

        m_amountText.text = string.IsNullOrEmpty(m_localizedLabel)
            ? m_amount.ToString()
            : $"{m_localizedLabel} {m_amount}";
    }

    public void OnPointerClick(PointerEventData eventData)
    {
        // 빈 지갑이면 요청 자체를 보내지 않는다 — 서버도 0을 옮기고 끝이라 결과는 같지만,
        // 눌릴 때마다 RPC가 나가는 것을 막는다.
        if (m_amount <= 0 || m_owner == null)
            return;

        m_owner.RequestTakeFunds();
    }
}
