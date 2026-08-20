using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 채널링(체포·구조·스캔) 진행을 표시하는 화면 중앙 원형 게이지. (#184)
/// HUD 프리팹에 부착되며 App.UI.Gauge로 접근한다 — 크로스헤어(CrosshairUI)와 동일 관례.
/// 서버가 시작(지속시간)/종료 이벤트만 알리고(각 채널링 호스트의 오너 RPC),
/// 진행률은 로컬 시간으로 채운다 — 진행도를 매 프레임 동기화하지 않기 위함.
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class ChannelingGaugeUI : CommonManagerBase
{
    [Tooltip("원형 필 이미지 — Image Type: Filled / Radial 360")]
    [SerializeField]
    private Image m_fillImage;

    private float m_duration;
    private float m_elapsed;
    private bool m_isRunning;

    // 지금 게이지를 띄운 쪽 — 아이템·구조·포박이 게이지 하나를 공유하므로, 소유자가 다른 Hide
    // 요청은 무시해야 남의 게이지가 꺼지지 않는다. (#725)
    private object m_owner;

    protected override void Awake()
    {
        base.Awake(); // App.UI.Gauge 등록
        SetVisible(false);
    }

    /// <summary>채널링 시작 — seconds 동안 0→100%로 차오른다. 서버 시작 통지 수신 시 호출.</summary>
    public void Show(float seconds, object owner) => Show(seconds, 0f, owner);

    /// <summary>
    /// 진행 중인 것을 <b>중간부터 이어</b> 표시한다 — 이미 elapsed초 지난 상태로 시작한다. (#455)
    /// 테이저 충전처럼 아이템을 다시 장착했을 때 남은 시간만 보여주려면, 남은 시간을 duration으로 주는 게
    /// 아니라 전체 시간과 경과 시간을 함께 줘야 한다: 남은 시간을 duration으로 주면 게이지가 0%에서
    /// 다시 차올라 "방금 시작한 것"처럼 보인다.
    /// </summary>
    /// <param name="owner">
    /// 이 게이지를 띄우는 채널 — 짝이 되는 <see cref="Hide"/> 호출이 같은 값을 넘겨야 실제로 꺼진다. (#725)
    /// </param>
    public void Show(float seconds, float elapsed, object owner)
    {
        if (seconds <= 0f)
            return;

        m_owner = owner;
        m_duration = seconds;
        m_elapsed = Mathf.Clamp(elapsed, 0f, seconds);
        m_isRunning = true;
        if (m_fillImage != null)
            m_fillImage.fillAmount = Mathf.Clamp01(m_elapsed / m_duration);
        SetVisible(true);
    }

    /// <summary>
    /// 채널링 종료(완료·취소·실패 공통) — <paramref name="owner"/>가 지금 게이지를 띄운 쪽과
    /// 같을 때만 숨긴다. 다른 소유자의 게이지면 무동작. (#725)
    /// </summary>
    public void Hide(object owner)
    {
        if (m_owner != null && owner != m_owner)
            return; // 다른 채널이 띄운 게이지 — 건드리지 않는다

        ForceHide();
    }

    private void ForceHide()
    {
        m_isRunning = false;
        m_owner = null;
        SetVisible(false);
    }

    private void Update()
    {
        if (!m_isRunning)
            return;

        m_elapsed += Time.deltaTime;
        if (m_fillImage != null)
            m_fillImage.fillAmount = Mathf.Clamp01(m_elapsed / m_duration);

        // 서버 종료 통지가 유실·지연돼도 게이지가 화면에 눌어붙지 않게 완료 후 여유를 두고 자동 숨김
        if (m_elapsed >= m_duration + 0.5f)
            ForceHide();
    }

    private void SetVisible(bool visible)
    {
        if (m_fillImage != null)
            m_fillImage.gameObject.SetActive(visible);
    }
}
