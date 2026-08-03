using TMPro;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 화면 상단 중앙 토스트 — 한 줄 알림을 정해진 시간 동안 띄웠다가 서서히 지운다. (#493)
/// HUD 프리팹에 부착되며 App.UI.Toast로 접근한다 — ChannelingGaugeUI와 동일 관례.
///
/// 표시는 로컬 전용이다: "내 화면에 띄우기"만 하고, 누구에게 보일지는 호출부가 정한다
/// (예: <see cref="PlayerPenaltyView"/>의 추격 경고는 [Rpc(SendTo.Owner)]로 대상 본인에게만 온다).
///
/// 문구는 <see cref="LocalizedString"/>으로 받아 StringChanged를 구독한다 — 떠 있는 동안 언어를
/// 바꿔도 즉시 갱신된다. 구독 해제 기준을 호출부가 아니라 이 참조로 잡는 이유는 InventorySlotView와
/// 같다(#251): 호출부가 파괴돼도 남은 구독이 나중에 발화해 엉뚱한 문구를 쓰는 것을 막는다.
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class ToastView : CommonManagerBase
{
    [Tooltip("토스트 루트 — 배경 + 문구를 함께 켜고 끄고, 알파로 페이드한다")]
    [SerializeField]
    private CanvasGroup m_group;

    [Tooltip("문구를 그릴 TMP 텍스트 (m_group 하위)")]
    [SerializeField]
    private TMP_Text m_label;

    [Tooltip("사라지기 직전 이 시간(초) 동안 서서히 투명해진다")]
    [SerializeField]
    private float m_fadeSeconds = 1f;

    // 지금 표시 중인 문구 — Hide(message)가 "내가 띄운 게 아직 떠 있는가"를 이걸로 판별한다
    private LocalizedString m_bound;

    private float m_hideTime;
    private bool m_isShowing;

    protected override void Awake()
    {
        base.Awake(); // App.UI.Toast 등록
        SetVisible(false);
    }

    protected override void OnDestroy()
    {
        Unbind();
        base.OnDestroy(); // App 등록 해제
    }

    /// <summary>
    /// 토스트 표시 — 이미 떠 있으면 문구·시간을 덮어쓴다(중첩 없이 최신 하나만).
    /// seconds는 카운트다운이 아니라 알림이 화면에 머무는 시간이다.
    /// </summary>
    public void Show(LocalizedString message, float seconds)
    {
        if (m_group == null || m_label == null)
            return;
        if (message == null || message.IsEmpty || seconds <= 0f)
            return;

        Unbind();

        m_bound = message;
        // 구독 즉시 현재 언어 값으로 1회 호출되고, 이후 언어 전환 시마다 다시 호출된다 (#251)
        m_bound.StringChanged += HandleStringChanged;

        m_hideTime = Time.time + seconds;
        m_isShowing = true;
        SetVisible(true);
    }

    /// <summary>
    /// 이 문구가 아직 떠 있을 때만 지운다 — 그 사이 다른 알림이 덮어썼으면 건드리지 않는다.
    /// 표시 시간이 끝나기 전에 조기 종료할 때 쓴다(포획 확정 등).
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
        m_isShowing = false;
        SetVisible(false);
    }

    private void Update()
    {
        if (!m_isShowing)
            return;

        float remaining = m_hideTime - Time.time;
        if (remaining <= 0f)
        {
            HideImmediate();
            return;
        }

        // 마지막 m_fadeSeconds 구간에서만 알파가 떨어진다 — 그 전까지는 1로 유지
        if (m_fadeSeconds > 0f)
            m_group.alpha = Mathf.Clamp01(remaining / m_fadeSeconds);
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
        // ToastView 자신은 HUD 루트에 붙어 있어 이 토글에 영향받지 않는다(Update가 계속 돈다).
        m_group.gameObject.SetActive(visible);
    }
}
