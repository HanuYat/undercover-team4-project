using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 사이렌 버튼 쿨다운 표시 (#488) — 버튼 위 월드 스페이스 필. 누구든 버튼을 보면 지금 쓸 수 있는지 안다.
/// 남은 시간을 받아 그만큼만 채운다 — 쿨다운 도중 들어온 피어도 중간부터 이어 그린다
/// (ChannelingGaugeUI.Show(seconds, elapsed)와 같은 방식, #455).
/// </summary>
public class JailSirenButtonView : MonoBehaviour
{
    [Tooltip("비우면 같은 오브젝트에서 자동 탐색")]
    [SerializeField]
    private JailSirenButton m_button;

    [Tooltip("Image Type: Filled — 쿨다운 동안 0→100%로 차오른다")]
    [SerializeField]
    private Image m_fillImage;

    private float m_duration;
    private float m_elapsed;
    private bool m_isRunning;

    private void Awake()
    {
        if (m_button == null)
            m_button = GetComponent<JailSirenButton>();

        SetVisible(false);
    }

    private void OnEnable()
    {
        if (m_button != null)
            m_button.OnCooldownStarted += HandleCooldownStarted;
    }

    private void OnDisable()
    {
        if (m_button != null)
            m_button.OnCooldownStarted -= HandleCooldownStarted;
    }

    private void HandleCooldownStarted(float remaining)
    {
        m_duration = m_button.CooldownSeconds;
        if (m_duration <= 0f || remaining <= 0f)
            return;

        // 남은 시간만큼만 남기고 시작한다 — 갓 걸린 쿨다운은 0%, 도중에 들어왔으면 그만큼 차 있다.
        m_elapsed = Mathf.Clamp(m_duration - remaining, 0f, m_duration);
        m_isRunning = true;
        Apply();
        SetVisible(true);
    }

    // 필을 채우는 동안만 돈다 — 대기 중에는 첫 줄에서 빠져나간다.
    private void Update()
    {
        if (!m_isRunning)
            return;

        m_elapsed += Time.deltaTime;
        Apply();

        // 다 찼으면 스스로 접는다 — 해제 통지가 따로 오지 않는다(쿨다운은 시각 비교로 저절로 풀린다).
        if (m_elapsed >= m_duration)
            Stop();
    }

    private void Stop()
    {
        m_isRunning = false;
        SetVisible(false);
    }

    private void Apply()
    {
        if (m_fillImage != null)
            m_fillImage.fillAmount = Mathf.Clamp01(m_elapsed / m_duration);
    }

    private void SetVisible(bool visible)
    {
        if (m_fillImage != null)
            m_fillImage.enabled = visible;
    }
}
