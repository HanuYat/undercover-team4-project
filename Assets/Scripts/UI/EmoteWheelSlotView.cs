using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// 감정표현 휠의 칸 하나 — 아이콘·이름·강조. (#219)
/// 배치 각도는 EmoteWheelView가 정한다(판정과 같은 값을 써야 하므로).
/// </summary>
public class EmoteWheelSlotView : MonoBehaviour
{
    [SerializeField]
    private RectTransform m_root;

    [SerializeField]
    private Image m_icon;

    [SerializeField]
    private TextMeshProUGUI m_label;

    [Tooltip("가리키는 동안 켜지는 강조 표시")]
    [SerializeField]
    private GameObject m_highlight;

    [Tooltip("빈 칸일 때의 아이콘 투명도")]
    [SerializeField]
    private float m_emptyAlpha = 0.25f;

    // 지금 구독 중인 표시 이름 — 갈아끼울 때 이전 구독을 끊기 위해 들고 있는다
    private UnityEngine.Localization.LocalizedString m_boundName;

    private void Awake()
    {
        if (m_root == null)
            m_root = transform as RectTransform;
    }

    public void SetAnchoredPosition(Vector2 position)
    {
        if (m_root == null)
            m_root = transform as RectTransform;

        if (m_root != null)
            m_root.anchoredPosition = position;
    }

    /// <summary>칸에 감정표현을 채운다 — null이면 빈 칸으로 그린다.</summary>
    public void Bind(EmoteDefinition definition)
    {
        bool filled = definition != null;

        if (m_icon != null)
        {
            m_icon.sprite = filled ? definition.Icon : null;
            m_icon.enabled = filled && definition.Icon != null;

            Color color = m_icon.color;
            color.a = filled ? 1f : m_emptyAlpha;
            m_icon.color = color;
        }

        // LocalizedString은 비동기 조회라 즉시 값이 없을 수 있어 준비되면 채우도록 구독한다.
        // 구독을 갈아끼울 때 이전 것을 반드시 끊는다 — 로비 편집에서는 같은 칸이 계속 다시
        // Bind되므로, 안 끊으면 구독이 쌓여 한 칸에 여러 문구가 번갈아 들어온다.
        UnsubscribeLabel();

        if (!filled)
        {
            m_boundName = null;
            SetLabelText(string.Empty);
            return;
        }

        // 표시 이름 키가 아직 연결되지 않았으면 id를 대신 보여 준다.
        // 빈 칸으로 두면 아이콘 색만으로 무엇인지 구분해야 하는데, 임시 아이콘 단계에서는
        // 그게 사실상 구분 불가다 — 로컬라이즈 배선이 끝나기 전에도 고를 수 있어야 한다.
        if (definition.DisplayName == null || definition.DisplayName.IsEmpty)
        {
            m_boundName = null;
            SetLabelText(definition.Id);
            return;
        }

        m_boundName = definition.DisplayName;
        m_boundName.StringChanged += SetLabelText;
    }

    private void OnDestroy() => UnsubscribeLabel();

    private void UnsubscribeLabel()
    {
        if (m_boundName == null)
            return;

        m_boundName.StringChanged -= SetLabelText;
        m_boundName = null;
    }

    public void SetHighlighted(bool highlighted)
    {
        if (m_highlight != null)
            m_highlight.SetActive(highlighted);
    }

    private void SetLabelText(string value)
    {
        if (m_label != null)
            m_label.text = value;
    }
}
