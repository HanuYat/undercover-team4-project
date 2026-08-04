using System;
using Cysharp.Threading.Tasks;
using TMPro;
using UnityEngine;

/// <summary>
/// 진열대 표시 (#182) — ShopStand의 자식으로 배치되는 월드공간 UI. 두 갈래를 함께 관리한다:
///  · <b>상시 가격표</b> — 이름·가격·구매 여부. 늘 켜져 있어 멀리서도 뭘 파는지 보인다.
///  · <b>조준 카드</b> — 이름·분류·설명 + 구매 결과 알림. 조준했을 때만 켜진다(ShopStandPresenter).
///
/// 카드가 진열대의 자식이라 위치 추종 로직이 필요 없다(ScanInfoView와 같은 구조). 진열대는 통로를
/// 향해 고정 배치되므로 빌보드 회전도 두지 않는다 — 프리팹의 고정 방향을 그대로 쓴다.
///
/// 구매 결과 알림은 요청자에게만 오는 RPC로 채워진다 — 카드는 클라마다 로컬 오브젝트라
/// 남의 화면에는 뜨지 않는다.
/// </summary>
public class ShopStandView : MonoBehaviour
{
    [Header("상시 가격표")]
    [SerializeField]
    private TMP_Text m_tagNameText;

    [SerializeField]
    private TMP_Text m_tagPriceText;

    [Tooltip("한 번이라도 구매한 품목에 켜지는 표시. 소지형은 중복 구매가 가능하므로 '구매 불가'가 아니라 '산 적 있음'이다")]
    [SerializeField]
    private GameObject m_purchasedMark;

    [Header("조준 카드 (기본 비활성)")]
    [Tooltip("카드 루트. 조준 중에만 켜진다")]
    [SerializeField]
    private GameObject m_cardRoot;

    [SerializeField]
    private TMP_Text m_cardNameText;

    [SerializeField]
    private TMP_Text m_cardTypeText;

    [SerializeField]
    private TMP_Text m_cardDescriptionText;

    [Tooltip("구매 결과(성공·자금 부족 등) 알림줄. 요청자 화면에만 뜬다")]
    [SerializeField]
    private TMP_Text m_cardNoticeText;

    [Tooltip("알림 표시 유지 시간(초)")]
    [SerializeField]
    private float m_noticeSeconds = 2.5f;

    // 알림 자동 숨김 예약의 세대 번호 — 새 알림·지우기가 번호를 올리면 먼저 걸린 예약은 스스로 물러난다.
    // (InventoryBarView의 아이템 이름 팝업과 같은 방식)
    private int m_noticeVersion;

    private const string k_shopTable = "ShopTable";
    private const string k_commonTable = "CommonTable";
    private const string k_moneyKey = "Common.Unit.Money"; // 금액 표기는 프로젝트 공용 서식

    private void Awake()
    {
        SetActive(m_cardRoot, false); // 조준 전엔 카드 숨김
        ClearNotice();
    }

    /// <summary>
    /// 진열대가 파는 품목을 표시에 채운다 — 스폰 시 1회, 이후 언어가 바뀔 때마다 다시
    /// (<see cref="ShopStand"/>가 로케일 변경을 구독해 통째로 다시 부른다).
    /// 이름·설명은 호출부가 해석해 넘기고, 가격 서식과 종류 표기는 진열대마다 같은 문구라 여기서 조회한다 —
    /// SerializeField로 두면 진열대 인스턴스마다 같은 키를 다시 배선해야 하고 하나만 빠지면 조용히 남는다. (#497)
    /// </summary>
    public void SetContent(string itemName, string description, bool installable, int price)
    {
        if (m_tagNameText != null)
            m_tagNameText.text = itemName;
        if (m_tagPriceText != null)
            m_tagPriceText.text = LocalizedStrings.Get(k_commonTable, k_moneyKey, price);

        if (m_cardNameText != null)
            m_cardNameText.text = itemName;
        if (m_cardTypeText != null)
            m_cardTypeText.text = LocalizedStrings.Get(
                k_shopTable,
                installable ? "Shop.Stand.TypeInstallable" : "Shop.Stand.TypeCarryable"
            );
        if (m_cardDescriptionText != null)
            m_cardDescriptionText.text = description;
    }

    /// <summary>구매 이력 표시를 갱신한다 — 진열대의 NetworkVariable이 바뀔 때마다 호출된다.</summary>
    public void SetPurchased(bool purchased)
    {
        SetActive(m_purchasedMark, purchased);
    }

    /// <summary>조준 카드를 켜고 끈다. 알림은 조준을 뗄 때도, 다시 겨냥할 때도 지운다.</summary>
    public void ShowCard(bool visible)
    {
        // 켤 때도 지우는 이유: 구매 응답(요청자 전용 RPC)이 도착하기 직전에 시선을 돌리면 카드가 꺼진 채
        // 알림 문구만 남는다 — 그대로 두면 나중에 다시 겨냥했을 때 지난 알림이 되살아난다.
        ClearNotice();
        SetActive(m_cardRoot, visible);
    }

    /// <summary>구매 결과를 카드 알림줄에 잠깐 띄운다 — 요청자 로컬에서만 호출된다.</summary>
    public void ShowNotice(string message)
    {
        if (m_cardNoticeText == null || string.IsNullOrEmpty(message))
            return;

        m_cardNoticeText.text = message;
        HideNoticeAfterAsync(++m_noticeVersion).Forget();
    }

    private async UniTaskVoid HideNoticeAfterAsync(int version)
    {
        await UniTask.Delay(TimeSpan.FromSeconds(m_noticeSeconds));

        // 파괴됐거나 그 사이 새 알림·지우기가 번호를 올렸으면 낡은 예약이므로 무시.
        if (this == null || version != m_noticeVersion)
            return;

        ClearNotice();
    }

    private void ClearNotice()
    {
        m_noticeVersion++; // 걸려 있던 예약 무효화
        if (m_cardNoticeText != null)
            m_cardNoticeText.text = string.Empty;
    }

    private static void SetActive(GameObject go, bool active)
    {
        if (go != null && go.activeSelf != active)
            go.SetActive(active);
    }
}
