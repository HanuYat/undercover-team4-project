using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 정해진 시간이 지나면 저절로 사라지는 표시 — 경보·수신처럼 "사건이 있었다"를 알리는 쪽. (#493)
/// 구독·표시는 <see cref="LocalizedMessageView"/>가 담당하고, 여기서는 수명과 페이드만 얹는다.
///
/// 표시 입구가 <see cref="Show(LocalizedString, float)"/> 하나뿐인 이유: 시간 없이 띄울 수 있으면
/// 지울 사람이 없어 화면에 눌어붙는다. 상태가 유지되는 동안 띄워야 하면 <see cref="PromptView"/> 쪽이다.
/// </summary>
public abstract class TimedMessageView : LocalizedMessageView
{
    [Tooltip("사라지기 직전 이 시간(초) 동안 서서히 투명해진다")]
    [SerializeField]
    private float m_fadeSeconds = 1f;

    private float m_hideTime;

    /// <summary>
    /// 표시 — 이미 떠 있으면 문구·시간을 덮어쓴다(중첩 없이 최신 하나만).
    /// seconds는 카운트다운이 아니라 문구가 화면에 머무는 시간이다.
    /// </summary>
    public void Show(LocalizedString message, float seconds)
    {
        if (seconds <= 0f)
            return;

        ShowMessage(message);
        m_hideTime = Time.time + seconds;
    }

    private void Update()
    {
        if (!IsShowing)
            return;

        float remaining = m_hideTime - Time.time;
        if (remaining <= 0f)
        {
            HideImmediate();
            return;
        }

        // 마지막 m_fadeSeconds 구간에서만 알파가 떨어진다 — 그 전까지는 1로 유지
        if (m_fadeSeconds > 0f && Group != null)
            Group.alpha = Mathf.Clamp01(remaining / m_fadeSeconds);
    }
}
