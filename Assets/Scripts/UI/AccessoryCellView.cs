using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 치장 한 칸 (#818) — <see cref="AccessoryPickerView"/>가 카탈로그 길이만큼 찍어 낸다.
/// <see cref="PlayerColorSwatchView"/>와 같은 구조다: 무엇을 고른 것인지는 목록을 아는 쪽이 알고,
/// 여기서는 눌렸다는 사실만 알린다.
///
/// 색 칸과 달리 <b>아이콘</b>을 보여 준다 — 모자·수염은 이름만으로 구별되지 않는다.
/// 아이콘은 <see cref="CosmeticIconBaker"/>가 구운 것이고, 없으면 이름만 보인다.
/// </summary>
[RequireComponent(typeof(Button))]
public class AccessoryCellView : MonoBehaviour
{
    [Tooltip("착용 모습 아이콘")]
    [SerializeField] private Image m_icon;

    [Tooltip("아이템 이름")]
    [SerializeField] private TMP_Text m_label;

    [Tooltip("지금 고른 칸에만 켜지는 표시. 없으면 선택 표시가 없다")]
    [SerializeField] private GameObject m_selectedMark;

    private Button m_button;

    private void Awake() => m_button = GetComponent<Button>();

    /// <summary>아이콘·이름·클릭 동작을 채운다 — 찍어 낼 때 한 번 부른다.</summary>
    public void Bind(Sprite icon, string label, Action onPicked)
    {
        // 에디터에서 미리 채워 볼 때는 Awake가 돌지 않는다 — 그 자리에서 잡는다
        if (m_button == null)
            m_button = GetComponent<Button>();

        if (m_icon != null)
        {
            m_icon.sprite = icon;
            m_icon.enabled = icon != null;
        }

        if (m_label != null)
            m_label.text = label;

        m_button.onClick.RemoveAllListeners();
        m_button.onClick.AddListener(() => onPicked?.Invoke());
    }

    /// <summary>이름만 갈아 끼운다 — 언어가 바뀌었을 때 (#818).</summary>
    public void SetLabel(string label)
    {
        if (m_label != null)
            m_label.text = label;
    }

    public void SetSelected(bool selected)
    {
        if (m_selectedMark != null)
            m_selectedMark.SetActive(selected);
    }
}
