using TMPro;
using UnityEngine;

/// <summary>
/// 라운드 목표 금액 진행도 HUD (#395) — "지금 벌어둔 금액 / 목표 금액"을 표시한다.
///
/// 진행도는 지금 유치장에 잡아둔 대상들의 현상금 합(JailZone.BountyTotal)이다. 팀 자금(TeamFund) 잔액이 아니다.
/// 그쪽은 세션 이월분이라 이번 라운드 성과가 아니고, 상점 구매로 줄기도 한다.
/// 탈옥(#231)으로 수감자가 빠져나가면 이 값도 함께 줄어든다.
/// 
/// 표시 전용. 목표 금액은 씬에 저장된 값이라 모든 피어에서 같고, 진행도는 서버 권위로 동기화되므로
/// 클라이언트에서도 그대로 읽어 그린다. (RemainingCriminalsHud와 같은 구조)
/// </summary>
public class RoundFundBoard : MonoBehaviour
{
    private RoundManager Round => App.Game.Round;

    [Header("표시")]
    [Tooltip("목표 진행도를 표시할 TextMeshPro (UGUI, 3D)")]
    [SerializeField] private TMP_Text m_fundText;

    [Tooltip("표시 형식 — {0}=지금 벌어둔 금액, {1}=목표 금액")]
    [SerializeField] private string m_format = "{0:N0} / {1:N0}원";

    [Tooltip("목표를 채웠을 때 입힐 색 — 본부 종료 버튼이 켜졌다는 신호와 같은 의미다")]
    [SerializeField] private Color m_metColor = new Color(0.36f, 0.85f, 0.44f);

    private Color m_defaultColor;
    private bool m_defaultColorCached;

    // 마지막으로 표시한 값 — 바뀔 때만 문자열을 다시 만들어 불필요한 GC 할당을 피한다
    private int m_lastCurrent = int.MinValue;
    private int m_lastTarget = int.MinValue;

    // 구독해 둔 유치장 — 라운드 도중 교체될 일이 없으므로 OnEnable에서 한 번 잡아 둔다
    private JailZone m_jail;

    private void OnEnable()
    {
        if (m_fundText == null)
        {
            Debug.LogWarning("RoundFundBoard: 금액 텍스트가 연결되지 않아 표시할 수 없다", this);
            return;
        }

        if (!m_defaultColorCached)
        {
            m_defaultColor = m_fundText.color;
            m_defaultColorCached = true;
        }

        // 진행도 원본은 유치장이다 — 값이 바뀔 때만 다시 그리도록 직접 구독한다(매 프레임 폴링 대신).
        // JailZone은 App에 등록된 매니저가 아니라 씬 배치 오브젝트라 App 파사드 경로가 없다.
        m_jail = FindFirstObjectByType<JailZone>();
        if (m_jail != null)
            m_jail.OnBountyTotalChanged += HandleBountyChanged;

        Refresh();
    }

    private void OnDisable()
    {
        if (m_jail != null)
            m_jail.OnBountyTotalChanged -= HandleBountyChanged;
        m_jail = null;
    }

    private void HandleBountyChanged(int _) => Refresh();

    private void Refresh()
    {
        if (m_fundText == null)
            return;

        RoundManager round = Round;
        if (round == null)
        {
            SetVisible(false);
            return;
        }

        int target = round.TargetFund;
        // 목표가 잡히기 전(0 이하)에는 "0/0"을 띄우지 않고 숨긴다 — RemainingCriminalsHud와 같은 관례
        if (target <= 0)
        {
            SetVisible(false);
            m_lastCurrent = int.MinValue;
            m_lastTarget = int.MinValue;
            return;
        }

        SetVisible(true);

        int current = round.CurrentFund;
        if (current == m_lastCurrent && target == m_lastTarget)
            return;

        m_lastCurrent = current;
        m_lastTarget = target;
        m_fundText.text = string.Format(m_format, current, target);
        m_fundText.color = current >= target ? m_metColor : m_defaultColor;
    }

    // TMP 컴포넌트만 켜고 끈다 (RemainingCriminalsHud와 동일한 이유 — 자기 콜백을 죽이지 않도록)
    private void SetVisible(bool visible)
    {
        if (m_fundText != null && m_fundText.enabled != visible)
            m_fundText.enabled = visible;
    }
}
