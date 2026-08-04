using TMPro;
using UnityEngine;

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

    [Tooltip("표시 형식 - {0} = 팀 자금 잔액")]
    [SerializeField] private string m_format = "{0:N0}원";

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

        TryBind();
    }

    private void OnDisable()
    {
        // ?. 금지 — 파괴된 Unity 오브젝트 fake null 우회 방지 (HqPanelView 관례)
        if (m_teamFund != null)
            m_teamFund.Fund.OnValueChanged -= HandleFundChanged;
        m_teamFund = null;
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

    private void Refresh(int balance) => m_balanceText.text = string.Format(m_format, balance);

    // TMP 컴포넌트만 켜고 끈다 (자기 콜백을 죽이지 않도록)
    private void SetVisible(bool visible)
    {
        if (m_balanceText.enabled != visible)
            m_balanceText.enabled = visible;
    }
}
