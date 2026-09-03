using TMPro;
using UnityEngine;

/// <summary>
/// 폭탄 카운트다운 표시 — 폭탄에 붙는 월드 텍스트로 남은 시간을 보여준다. (#232 표현 계층)
///
/// 순수 표현이다. 남은 시간은 <see cref="BombDevice.RemainingSeconds"/>에서 읽는데, 이 값은 전 피어에서
/// 동작한다(서버·오프라인은 로컬 시계, 원격은 동기화된 서버시간). 그래서 이 뷰는 매 프레임 값을 읽어
/// 그리기만 하고 상태를 바꾸지 않는다 — 별도 동기화가 필요 없다.
/// </summary>
public class BombTimerView : MonoBehaviour
{
    [Tooltip("카운트다운을 그릴 텍스트 — 비우면 이 오브젝트의 TMP_Text를 쓴다")]
    [SerializeField]
    private TMP_Text m_label;

    [Tooltip("이 시간(초) 이하로 남으면 경고색으로 바뀐다")]
    [SerializeField]
    private float m_warnThreshold = 10f;

    [SerializeField]
    private Color m_normalColor = new Color(0.2f, 1f, 0.35f); // 평상시(녹색 계열 디지털)

    [SerializeField]
    private Color m_warnColor = new Color(1f, 0.25f, 0.2f); // 임박(빨강)

    private BombDevice m_device;

    private void Awake()
    {
        if (m_label == null)
            m_label = GetComponent<TMP_Text>();
        m_device = GetComponentInParent<BombDevice>();
    }

    private void Update()
    {
        if (m_label == null || m_device == null)
            return;

        switch (m_device.State)
        {
            case BombState.Armed:
                float t = m_device.RemainingSeconds;
                int minutes = (int)(t / 60f);
                int seconds = (int)(t % 60f);
                m_label.text = string.Format("{0}:{1:00}", minutes, seconds);
                m_label.color = t <= m_warnThreshold ? m_warnColor : m_normalColor;
                break;

            case BombState.Exploded:
                m_label.text = "BOOM";
                m_label.color = m_warnColor;
                break;

            default: // Idle — 아직 무장 전
                m_label.text = "--:--";
                m_label.color = m_normalColor;
                break;
        }
    }
}
