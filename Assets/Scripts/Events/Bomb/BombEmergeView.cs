using UnityEngine;

/// <summary>
/// 등장 연출(폭탄 쪽) — 상자가 밀려나 드러난 로봇이 한 번 들썩이고 기동한다. (#399 표현 계층)
///
/// <b>순수 표현이다.</b> 등장 시간 동안 폭탄은 무장 전(<see cref="BombState.Emerging"/>)이라 움직이지도
/// 터지지도 않는다. 그 사실은 <see cref="BombDevice"/>가 서버 권위로 정해 전 피어에 전파하고, 이 뷰는
/// 상태가 Emerging이 된 순간부터 자기 시계로 애니메이션을 돌린다 — 진행도는 동기화하지 않는다
/// (몇 프레임 어긋나도 보이는 것만 다르고, 무장 시각은 서버가 쥔 하나뿐이다).
/// <b>상자는 여기서 다루지 않는다</b> — 씬의 <see cref="BombCrate"/>가 자기 연출을 스스로 돌리고,
/// 이 뷰는 상자가 물러난 뒤의 로봇 기동만 맡는다.
/// </summary>
[RequireComponent(typeof(BombDevice))]
public class BombEmergeView : MonoBehaviour
{
    [Tooltip("폭탄 본체 모델 — 기동할 때 살짝 들썩인다")]
    [SerializeField]
    private Transform m_model;

    [Tooltip("상자가 물러날 때까지 기다리는 시간(초) — BombCrate의 들썩+밀려남 길이에 맞출 것. " +
             "짧으면 아직 상자에 덮인 채로 들썩이고, 길면 드러난 뒤 한참 가만히 있는다")]
    [SerializeField]
    private float m_startupDelaySeconds = 1.25f;

    [Tooltip("로봇이 기동하며 들썩이는 높이(m)")]
    [SerializeField]
    private float m_startupHop = 0.12f;

    private BombDevice m_device;
    private bool m_playing;
    private float m_elapsed;
    private Vector3 m_modelPosition;

    private void Awake()
    {
        m_device = GetComponent<BombDevice>();
        if (m_model != null)
            m_modelPosition = m_model.localPosition;
    }

    private void Update()
    {
        if (m_model == null)
            return;

        if (m_device.State != BombState.Emerging)
        {
            // 등장이 끝났거나 아직 시작 전 — 모델을 제자리에 두고 다음 등장을 기다린다
            if (m_playing)
            {
                m_playing = false;
                m_model.localPosition = m_modelPosition;
            }
            return;
        }

        if (!m_playing)
        {
            m_playing = true;
            m_elapsed = 0f;
        }

        m_elapsed += Time.deltaTime;
        if (m_elapsed < m_startupDelaySeconds)
            return; // 아직 상자 안 — 움직이면 상자를 뚫고 나온다

        TickStartup(m_elapsed - m_startupDelaySeconds);
    }

    // 로봇이 한 번 들썩이고 자세를 잡는다 — 감쇠하는 사인이라 마지막엔 제자리에 선다
    private void TickStartup(float t)
    {
        float decay = Mathf.Exp(-t * 5f);
        float hop = Mathf.Sin(t * 12f) * m_startupHop * decay;
        m_model.localPosition = m_modelPosition + Vector3.up * Mathf.Max(0f, hop);
    }
}
