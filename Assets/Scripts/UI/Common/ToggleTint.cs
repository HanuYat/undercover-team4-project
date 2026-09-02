using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 체크박스 배경을 켜짐/꺼짐에 따라 갈아 칠한다 (#894) — 켜지면 배경이 골드로 차고 체크 표시는 흰색으로 남는다.
/// Toggle이 켜고 끄는 그림(graphic)은 하나뿐이라, 배경색까지 뒤집으려면 이렇게 따로 칠해야 한다.
///
/// <b>SetIsOnWithoutNotify로 값을 넣는 쪽은 <see cref="Refresh"/>를 불러야 한다</b> —
/// 알림 없이 넣으면 onValueChanged가 깨어나지 않아 색만 이전 상태로 남는다.
/// </summary>
[RequireComponent(typeof(Toggle))]
public class ToggleTint : MonoBehaviour
{
    [Tooltip("칠할 배경 — 비워 두면 Toggle의 targetGraphic을 쓴다")]
    [SerializeField] private Image m_background;

    [Tooltip("켜졌을 때 배경색")]
    [SerializeField] private Color m_onColor = new Color(0.878f, 0.663f, 0.290f);

    [Tooltip("꺼졌을 때 배경색")]
    [SerializeField] private Color m_offColor = new Color(0.122f, 0.153f, 0.200f);

    private Toggle m_toggle;

    private void Awake()
    {
        m_toggle = GetComponent<Toggle>();

        if (m_background == null)
            m_background = m_toggle.targetGraphic as Image;

        m_toggle.onValueChanged.AddListener(HandleValueChanged);
    }

    private void OnDestroy()
    {
        if (m_toggle != null)
            m_toggle.onValueChanged.RemoveListener(HandleValueChanged);
    }

    // 켜진 채로 창이 열릴 수 있다 — 켜지는 시점에 한 번 맞춘다
    private void OnEnable() => Refresh();

    /// <summary>지금 Toggle 상태로 배경색을 맞춘다.</summary>
    public void Refresh()
    {
        if (m_toggle == null || m_background == null)
            return;

        m_background.color = m_toggle.isOn ? m_onColor : m_offColor;
    }

    private void HandleValueChanged(bool on) => Refresh();
}
