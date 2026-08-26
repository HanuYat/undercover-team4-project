using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 팀 상황판의 대원 상태 — 동료가 움직여야 하는가로만 갈린다. 기절·오검거 매달기는 스스로 풀려
/// 생존으로 묶는다. Down은 넣지 않는다 — GDD 10-2대로 #524 이후 발생하지 않는다. (#720)
///
/// Dead는 GDD 7-5의 <b>기능 정지</b>다 — 로봇이라 죽는 것이 아니다. 표시 문구도 그 용어를 쓴다.
/// </summary>
public enum ETeamMemberState
{
    Alive,
    Abducted,
    Dead,
}

/// <summary>
/// 팀 상황판의 대원 카드 (#720) — 얼굴 · 이름 · 체력 게이지 · 상태.
/// 게이지는 좌하단 기름통(<see cref="HpOilGaugeView"/>)을 그대로 얹는다 — 같은 값을 두 모양으로
/// 그리면 내 체력과 동료 체력이 다른 물건처럼 읽힌다.
/// </summary>
public class TeamStatusRowView : MonoBehaviour
{
    private const string k_table = "HudTable";

    // 첫 호출이 반드시 통과하도록 실제 상태가 될 수 없는 값으로 둔다.
    private const ETeamMemberState k_noState = (ETeamMemberState)(-1);

    [Tooltip("대원 얼굴 — 상점에서 구운 것을 그대로 쓴다 (#598 · #863)")]
    [SerializeField] private RawImage m_portrait;

    [SerializeField] private TextMeshProUGUI m_nameText;

    [Tooltip("HP 게이지 — 좌하단 기름통 뷰를 그대로 쓴다. 숫자(72/100)도 이 뷰가 통 안에 그린다")]
    [SerializeField] private HpOilGaugeView m_gauge;

    [SerializeField] private TextMeshProUGUI m_stateText;

    [Header("상태 색")]
    [SerializeField] private Color m_aliveTone = new Color(0.85f, 0.92f, 0.95f, 1f);
    [SerializeField] private Color m_abductedTone = new Color(0.95f, 0.72f, 0.25f, 1f);
    [SerializeField] private Color m_deadTone = new Color(0.85f, 0.30f, 0.30f, 1f);

    // 마지막으로 그린 상태 — 같은 값이면 다시 그리지 않는다.
    private ETeamMemberState m_shownState = k_noState;

    // 마지막으로 그린 이름 — null은 "아직 한 번도 안 넣었다"는 뜻이다.
    private string m_shownName;

    /// <summary>이름이 들어와 있는가 — 상황판이 빈 이름만 다시 물어보게 하는 표시다. (#720)</summary>
    public bool HasName => !string.IsNullOrEmpty(m_shownName);

    // 패널이 다시 열릴 때 캐시를 버린다 — 닫혀 있는 사이에 언어가 바뀌었을 수 있다.
    private void OnEnable()
    {
        m_shownState = k_noState;
        m_shownName = null;
    }

    /// <summary>얼굴이 들어와 있는가 — 색이 늦게 도착하면 상황판이 다시 넘긴다. (#432)</summary>
    public bool HasPortrait => m_portrait != null && m_portrait.texture != null;

    /// <summary>얼굴을 넣는다 — 사람마다 고른 색·치장으로 상점에서 구운 그림이다. (#432 · #863)</summary>
    public void SetPortrait(Texture portrait)
    {
        if (m_portrait == null)
            return;

        m_portrait.texture = portrait;
        m_portrait.enabled = portrait != null; // 텍스처 없는 RawImage는 흰 사각형으로 그려진다 (#863)
    }

    /// <summary>
    /// 이름을 넣는다 — 접속 중에 바뀌지는 않지만 스폰과 같은 프레임에는 아직 비어 있을 수 있어,
    /// 상황판이 빌 때마다 다시 넘긴다. 같은 값이면 TMP 메시를 건드리지 않는다. (#720)
    /// </summary>
    public void SetName(string displayName)
    {
        if (m_nameText == null || displayName == m_shownName)
            return;

        m_shownName = displayName;
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
            case ETeamMemberState.Abducted:
                return "Hud.Team.State.Abducted";
            default:
                return "Hud.Team.State.Alive";
        }
    }

    private Color StateTone(ETeamMemberState state)
    {
        switch (state)
        {
            case ETeamMemberState.Dead:
                return m_deadTone;
            case ETeamMemberState.Abducted:
                return m_abductedTone;
            default:
                return m_aliveTone;
        }
    }
}
