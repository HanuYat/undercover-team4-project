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
/// 값은 <see cref="NpcHealth.CurrentHp"/>(동기화 값)를 읽으므로 서버·클라 구분 없이 각자
/// 화면에 그린다. 체력 변경 이벤트가 없어 보이는 동안만 폴링한다 — 한 번에 한 NPC만 켜져 있으니
/// 프레임당 비교 한 번이다.
/// </summary>
public class NpcHealthBarView : NpcWorldCard
{
    [Header("게이지")]
    [Tooltip(
        "체력 바 — Image Type은 Filled, Fill Method는 Horizontal. "
            + "[주의] Sprite가 비어 있으면 Unity가 Filled 타입을 무시하고 단순 사각형을 그려 "
            + "채움이 전혀 동작하지 않는다"
    )]
    [SerializeField]
    private Image m_fill;

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

        int max = m_controller.Health.MaxHp;

        // 채운 양만 줄인다 — 색은 프리팹에 정해둔 값을 그대로 쓴다.
        // 색까지 같이 바꾸면 "얼마나 남았나"를 두 가지 신호가 동시에 말하게 되는데, 정작 길이 쪽이
        // 안 보이면(sprite 미지정 등) 색만 변해 원인을 찾기 어렵다. 신호는 길이 하나로 둔다.
        m_fill.fillAmount = max > 0 ? Mathf.Clamp01((float)m_controller.Health.CurrentHp / max) : 0f;
    }
}
