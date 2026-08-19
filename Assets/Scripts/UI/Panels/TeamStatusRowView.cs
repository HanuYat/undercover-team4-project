using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>팀 상황판의 파티원 상태 — 3단계로만 나눈다. 기절·매달림은 아직 서 있는 것으로 본다. (#720)</summary>
public enum ETeamMemberState
{
    Normal,
    Down,
    Dead,
}

/// <summary>
/// 팀 상황판의 파티원 한 줄 (#720) — 이름 · HP 게이지 · 상태.
///
/// 게이지는 좌하단 기름통(<see cref="HpOilGaugeView"/>)을 그대로 얹는다. 같은 값을 두 모양으로
/// 그리면 "내 HP"와 "동료 HP"가 다른 물건처럼 읽힌다.
/// </summary>
public class TeamStatusRowView : MonoBehaviour
{
    private const string k_table = "HudTable";

    // 첫 호출이 반드시 통과하도록 실제 상태가 될 수 없는 값으로 둔다.
    private const ETeamMemberState k_noState = (ETeamMemberState)(-1);

    [Tooltip("대원 얼굴 — LobbyPortraitStage가 구운 텍스처를 받는다 (#598과 같은 경로)")]
    [SerializeField] private RawImage m_portrait;

    [SerializeField] private TextMeshProUGUI m_nameText;

    [Tooltip("HP 게이지 — 좌하단 기름통 뷰를 그대로 쓴다. 숫자(72/100)도 이 뷰가 통 안에 그린다")]
    [SerializeField] private HpOilGaugeView m_gauge;

    [SerializeField] private TextMeshProUGUI m_stateText;

    [Header("상태 색")]
    [SerializeField] private Color m_normalTone = new Color(0.85f, 0.92f, 0.95f, 1f);
    [SerializeField] private Color m_downTone = new Color(0.95f, 0.72f, 0.25f, 1f);
    [SerializeField] private Color m_deadTone = new Color(0.85f, 0.30f, 0.30f, 1f);

    // 마지막으로 그린 상태 — 같은 값이면 다시 그리지 않는다.
    private ETeamMemberState m_shownState = k_noState;

    // 패널이 다시 열릴 때 캐시를 버린다 — 닫혀 있는 사이에 언어가 바뀌었을 수 있다.
    private void OnEnable() => m_shownState = k_noState;

    /// <summary>이름은 접속 중에 바뀌지 않으므로 행을 만들 때 한 번만 넣는다.</summary>
    /// <summary>
    /// 얼굴을 넣는다. <b>지금은 전원이 같은 한 장을 나눠 쓴다</b> — 플레이어별 외형 데이터가
    /// 아직 없기 때문이다(로봇 색 커스터마이징 #432). 개인별로 갈리면 무대를 사람 수만큼
    /// 세우고 각자의 텍스처를 넘기면 된다 — 여기 배선은 그대로다.
    /// </summary>
    public void SetPortrait(Texture portrait)
    {
        if (m_portrait != null)
            m_portrait.texture = portrait;
    }

    public void SetName(string displayName)
    {
        if (m_nameText != null)
            m_nameText.text = displayName;
    }

    /// <summary>매 프레임 값만 갈아 끼운다 — 상황판이 떠 있는 동안만 불린다.</summary>
    public void SetStatus(int hp, int maxHp, ETeamMemberState state)
    {
        if (m_gauge != null)
            m_gauge.SetHealth(hp, maxHp);

        // 상태 표기는 바뀔 때만 찾는다 — 매 프레임 부르는 경로라 그냥 대입하면 값이 같아도
        // 프레임마다 테이블을 조회하고 TMP 메시를 더티로 만든다 (HpOilGaugeView.SetHealth와 같은 방침).
        if (m_stateText == null || state == m_shownState)
            return;

        m_shownState = state;
        m_stateText.text = LocalizedStrings.Get(k_table, StateKey(state));
        m_stateText.color = StateTone(state);
    }

    private static string StateKey(ETeamMemberState state)
    {
        switch (state)
        {
            case ETeamMemberState.Dead:
                return "Hud.Team.State.Dead";
            case ETeamMemberState.Down:
                return "Hud.Team.State.Down";
            default:
                return "Hud.Team.State.Normal";
        }
    }

    private Color StateTone(ETeamMemberState state)
    {
        switch (state)
        {
            case ETeamMemberState.Dead:
                return m_deadTone;
            case ETeamMemberState.Down:
                return m_downTone;
            default:
                return m_normalTone;
        }
    }
}
