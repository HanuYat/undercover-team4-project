using TMPro;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.UI;

/// <summary>
/// 번역된 한 줄을 화면 한 자리에 띄우는 HUD 표시의 공통 뼈대 — 구독·표시·숨김만 담당한다. (#493)
/// HUD 프리팹에 부착되며 App.UI를 통해 접근한다 — ChannelingGaugeUI와 동일 관례.
///
/// <b>언제 사라지는가</b>는 파생 쪽이 정한다:
/// <see cref="TimedMessageView"/>는 정해진 시간이 지나면(경보·수신), <see cref="PromptView"/>는
/// 호출부가 지울 때까지(상태 안내). 그래서 표시를 시작하는 입구도 파생이 각자 공개한다 —
/// 시간제 표시를 시간 없이 띄우면 화면에 눌어붙기 때문이다.
///
/// 문구는 <see cref="LocalizedString"/>의 StringChanged를 구독한다 — 떠 있는 동안 언어를 바꿔도
/// 즉시 갱신된다. 구독 해제 기준을 호출부가 아니라 이 참조로 잡는 이유는 InventorySlotView와
/// 같다(#251): 호출부가 파괴돼도 남은 구독이 나중에 발화해 엉뚱한 문구를 쓰는 것을 막는다.
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public abstract class LocalizedMessageView : CommonManagerBase
{
    [Tooltip("표시 루트 — 배경 + 문구를 함께 켜고 끄고, 알파로 페이드한다")]
    [SerializeField]
    private CanvasGroup m_group;

    [Tooltip("문구를 그릴 TMP 텍스트 (m_group 하위)")]
    [SerializeField]
    private TMP_Text m_label;

    [Tooltip("톤 색을 입힐 배경 그래픽 — 비어 있으면 색을 건드리지 않는다")]
    [SerializeField]
    private Graphic m_toneTarget;

    // 프리팹에 설정해 둔 배경색 — 톤을 지정하지 않은 표시는 여기로 되돌린다. 되돌리지 않으면
    // 앞선 표시의 색이 남아 다음 문구의 성격을 잘못 알린다(검거 초록 뒤에 오는 경고 등).
    private Color m_defaultTone = Color.white;

    // 지금 표시 중인 문구 — Hide(message)가 "내가 띄운 게 아직 떠 있는가"를 이걸로 판별한다
    private LocalizedString m_bound;

    /// <summary>페이드 등 연출용 — 파생이 알파를 직접 만질 때 쓴다. 배선이 없으면 null.</summary>
    protected CanvasGroup Group => m_group;

    /// <summary>지금 무언가 떠 있는가.</summary>
    protected bool IsShowing { get; private set; }

    protected override void Awake()
    {
        base.Awake(); // App에 등록

        if (m_toneTarget != null)
            m_defaultTone = m_toneTarget.color;

        SetVisible(false);
    }

    protected override void OnDestroy()
    {
        Unbind();
        base.OnDestroy(); // App 등록 해제
    }

    /// <summary>
    /// 표시 시작 — 이미 떠 있으면 덮어쓴다(중첩 없이 최신 하나만).
    /// 파생이 자기 공개 입구에서 부른다.
    /// </summary>
    protected void ShowMessage(LocalizedString message)
    {
        if (m_group == null || m_label == null)
            return;
        if (message == null || message.IsEmpty)
            return;

        Unbind();

        m_bound = message;
        // 구독 즉시 현재 언어 값으로 1회 호출되고, 이후 언어 전환 시마다 다시 호출된다 (#251)
        m_bound.StringChanged += HandleStringChanged;

        IsShowing = true;
        SetVisible(true);
    }

    /// <summary>
    /// 배경 톤을 정한다 — null이면 프리팹 기본색으로 되돌린다. (#616)
    /// 판정 배너가 판정 종류마다 색을 바꾸는 것과 같은 취지로, 한 자리를 여러 기능이 공유할 때
    /// "무슨 성격의 알림인가"를 색으로 가른다.
    /// </summary>
    protected void ApplyTone(Color? tone)
    {
        if (m_toneTarget == null)
            return;

        m_toneTarget.color = tone ?? m_defaultTone;
    }

    /// <summary>
    /// 이 문구가 아직 떠 있을 때만 지운다 — 그 사이 다른 문구가 덮어썼으면 건드리지 않는다.
    /// </summary>
    public void Hide(LocalizedString message)
    {
        if (message == null || !ReferenceEquals(m_bound, message))
            return;

        HideImmediate();
    }

    /// <summary>누가 띄웠든 즉시 지운다 (라운드 종료·씬 전환 등 화면을 통째로 비울 때).</summary>
    public void HideImmediate()
    {
        Unbind();
        IsShowing = false;
        SetVisible(false);
    }

    private void HandleStringChanged(string localized)
    {
        if (m_label != null)
            m_label.text = localized;
    }

    private void Unbind()
    {
        if (m_bound == null)
            return;

        m_bound.StringChanged -= HandleStringChanged;
        m_bound = null;
    }

    private void SetVisible(bool visible)
    {
        if (m_group == null)
            return;

        if (visible)
            m_group.alpha = 1f;

        // 루트 오브젝트를 껐다 켠다 — 배경까지 같이 사라져야 한다.
        // 이 컴포넌트 자신은 HUD 루트에 붙어 있어 이 토글에 영향받지 않는다(Update가 계속 돈다).
        m_group.gameObject.SetActive(visible);
    }
}
