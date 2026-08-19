using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 팔레트의 색 한 칸 (#432) — <see cref="PlayerColorPickerView"/>가 팔레트 길이만큼 찍어 낸다.
/// 고르는 동작은 여기서 하지 않는다: 눌렸다는 사실만 알리고, 무엇을 고른 것인지는 목록을 아는 쪽이 안다.
/// </summary>
[RequireComponent(typeof(Button))]
public class PlayerColorSwatchView : MonoBehaviour
{
    [Tooltip("색이 칠해질 그림 — 비워 두면 이 오브젝트의 Image를 쓴다")]
    [SerializeField]
    private Image m_fill;

    [Tooltip("지금 고른 칸에만 켜지는 표시(테두리 등). 없으면 선택 표시가 없다")]
    [SerializeField]
    private GameObject m_selectedMark;

    private Button m_button;

    private void Awake()
    {
        m_button = GetComponent<Button>();

        if (m_fill == null)
            m_fill = GetComponent<Image>();
    }

    /// <summary>색과 클릭 동작을 채운다 — 찍어 낼 때 한 번 부른다.</summary>
    public void Bind(Color color, System.Action onPicked)
    {
        if (m_fill != null)
            m_fill.color = color;

        m_button.onClick.RemoveAllListeners();
        m_button.onClick.AddListener(() => onPicked?.Invoke());
    }

    public void SetSelected(bool selected)
    {
        if (m_selectedMark != null)
            m_selectedMark.SetActive(selected);
    }
}
