using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// NPC 머리 위에 붙는 월드공간 체력 바. (#76/#79/#366 → #493)
/// 위치 추종·빌보드·누운 자세 보정·표시 토글은 <see cref="NpcWorldCard"/>가 담당한다.
///
/// 조준한 NPC에만 뜬다 — 켜고 끄는 판단은 플레이어 쪽 <see cref="NpcHealthBarPresenter"/>가 한다.
/// 체력이 가득한 NPC도 보여준다: "다친 NPC만" 규칙을 두면 바가 떴다는 사실 자체가 "얘가 그때 놓친
/// 놈"이라는 표식이 되어, 플레이어가 추리 대신 바를 찾아다니게 된다.
///
/// 값은 <see cref="NpcController.CurrentHp"/>(동기화 값)를 읽으므로 서버·클라 구분 없이 각자
/// 화면에 그린다. 체력 변경 이벤트가 없어 보이는 동안만 폴링한다 — 한 번에 한 NPC만 켜져 있으니
/// 프레임당 비교 한 번이다.
/// </summary>
public class NpcHealthBarView : NpcWorldCard
{
    [Header("게이지")]
    [Tooltip("체력 바 — Image Type을 Filled로, Fill Method는 Horizontal로 설정한다")]
    [SerializeField]
    private Image m_fill;

    [Header("색")]
    [Tooltip("체력이 가득할 때 — 아직 위협적이다")]
    [SerializeField]
    private Color m_fullColor = new Color(0.9f, 0.25f, 0.2f);

    [Tooltip("제압에 가까울 때")]
    [SerializeField]
    private Color m_lowColor = new Color(0.25f, 0.85f, 0.3f);

    private NpcController m_controller;

    protected override void Awake()
    {
        base.Awake();
        m_controller = GetComponentInParent<NpcController>();

        if (m_controller == null)
            Debug.LogWarning("NpcHealthBarView: NpcController를 찾지 못해 체력을 읽을 수 없다", this);
    }

    /// <summary>바를 켠다 — 조준이 시작될 때 프레젠터가 부른다.</summary>
    public void Show()
    {
        Refresh(); // 켜기 전에 한 번 맞춘다 — 첫 프레임에 옛 값이 보이지 않게
        SetCardActive(true);
    }

    protected override void LateUpdate()
    {
        base.LateUpdate(); // 빌보드
        Refresh();
    }

    private void Refresh()
    {
        if (m_fill == null || m_controller == null)
            return;

        int max = m_controller.MaxHp;
        float fill = max > 0 ? Mathf.Clamp01((float)m_controller.CurrentHp / max) : 0f;

        m_fill.fillAmount = fill;
        m_fill.color = Color.Lerp(m_lowColor, m_fullColor, fill);
    }
}
