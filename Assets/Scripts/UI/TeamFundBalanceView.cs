using TMPro;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;

/// <summary>
/// 팀 자금 잔액 표시 (#486) — 상점에서 "지금 얼마 있는지"를 보여준다.
///
/// 화면 상시 팀 자금 HUD를 걷어내면서(#486) 잔액을 볼 수 있는 곳이 정산 화면뿐이 됐다.
/// 상점에서는 잔액을 봐야 구매 판단이 되므로 상점 씬에는 이 표시를 남긴다.
/// 라운드 수금 진행도(RoundFundBoard)와는 다른 값이다 — 이쪽은 세션을 넘어 이월되는 잔액이고,
/// 그쪽은 이번 라운드에 유치장에 잡아둔 현상금 합이다.
///
/// 표시 전용. 자금은 서버 권위 NetworkVariable이라 호스트·원격 클라가 같은 값을 본다.
/// 값이 바뀔 때만 갱신한다(매 프레임 폴링 금지).
/// </summary>
public class TeamFundBalanceView : MonoBehaviour
{
    [Header("표시")]
    [Tooltip("잔액을 표시할 TextMeshPro (UGUI, 3D)")]
    [SerializeField] private TMP_Text m_balanceText;

    // 표시 서식은 CommonTable의 공용 금액 표기다 (#497). 인스펙터 문자열로 두면 그 필드가 영원히
    // 번역되지 않고(문서 §2 결정 (f)), 금액 표기를 쓰는 다른 화면과도 갈린다.
    private const string k_commonTable = "CommonTable";
    private const string k_moneyKey = "Common.Unit.Money";

    // 구독해 둔 자금 홀더. TeamFund는 씬을 넘어 사는 상주 홀더(#214)라 이 씬이 로드될 때 이미
    // 스폰돼 있는 게 보통이지만, 원격 클라는 스폰 동기화가 늦게 도착할 수 있다 — 잡힐 때까지 기다린다.
    private TeamFund m_teamFund;

    private void OnEnable()
    {
        if (m_balanceText == null)
        {
            Debug.LogWarning("TeamFundBalanceView: 잔액 텍스트가 연결되지 않아 표시할 수 없다", this);
            return;
        }

        // 금액 서식이 테이블에서 오므로 언어가 바뀌면 다시 그린다 — 자금 값은 그대로여도 표기가 바뀐다.
        // 항목이 하나뿐이라 StringChanged 대신 로케일 변경에 걸고 통째로 다시 채운다 (ShopStand와 같은 방식).
        LocalizationSettings.SelectedLocaleChanged += HandleLocaleChanged;

        TryBind();
    }

    private void OnDisable()
    {
        // ?. 금지 — 파괴된 Unity 오브젝트 fake null 우회 방지 (HqPanelView 관례)
        if (m_teamFund != null)
            m_teamFund.Fund.OnValueChanged -= HandleFundChanged;
        m_teamFund = null;

        // 종료 중에는 설정 에셋을 되살리지 않는다 — HasSettings로 먼저 확인한다 (ShopStand 관례)
        if (LocalizationSettings.HasSettings)
            LocalizationSettings.SelectedLocaleChanged -= HandleLocaleChanged;
    }

    private void HandleLocaleChanged(Locale locale)
    {
        if (m_teamFund != null)
            Refresh(m_teamFund.Balance);
    }

    // 아직 못 잡았을 때만 도는 폴링 — 잡는 즉시 이벤트 구동으로 넘어간다.
    private void Update()
    {
        if (m_teamFund == null && m_balanceText != null)
            TryBind();
    }

    private void TryBind()
    {
        TeamFund fund = App.Game.TeamFund;
        if (fund == null || !fund.IsSpawned)
        {
            SetVisible(false); // 자금 홀더가 붙기 전엔 "0원"을 띄우지 않는다
            return;
        }

        m_teamFund = fund;
        m_teamFund.Fund.OnValueChanged += HandleFundChanged;

        SetVisible(true);
        Refresh(m_teamFund.Balance);
    }

    private void HandleFundChanged(int previous, int current) => Refresh(current);

    private void Refresh(int balance) =>
        m_balanceText.text = LocalizedStrings.Get(k_commonTable, k_moneyKey, balance);

    // TMP 컴포넌트만 켜고 끈다 (자기 콜백을 죽이지 않도록)
    private void SetVisible(bool visible)
    {
        if (m_balanceText.enabled != visible)
            m_balanceText.enabled = visible;
    }
}
