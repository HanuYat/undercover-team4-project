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

    [Tooltip("아직 못 얻은 칸에 켜지는 자물쇠. 없으면 흐려지기만 한다")]
    [SerializeField] private GameObject m_lockMark;

    [Tooltip("잠긴 칸의 아이콘 투명도")]
    [SerializeField] private float m_lockedAlpha = 0.3f;

    [Tooltip("다른 치장에 가려진 칸의 아이콘 투명도 — 잠김보다 옅게 흐리지 않는다 (#932)")]
    [SerializeField] private float m_hiddenAlpha = 0.55f;

    private Button m_button;

    private bool m_locked;
    private bool m_hidden;

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

    /// <summary>
    /// 잠긴 칸으로 만든다 (#818 D) — 자판기로 해금하기 전의 항목이다.
    /// <b>숨기지 않고 흐리게 남긴다</b>: 무엇이 더 있는지 보여야 뽑을 이유가 생긴다.
    /// 버튼도 함께 끄므로 눌러도 착용되지 않는다.
    /// </summary>
    public void SetLocked(bool locked)
    {
        m_locked = locked;
        Apply();
    }

    /// <summary>
    /// 다른 치장에 가려지는 칸으로 만든다 (#932) — 전면 헬멧 밑의 머리카락 같은 자리다.
    ///
    /// <b>잠김과 다르다.</b> 자물쇠를 켜지 않고 버튼도 끄지 않는다 — 헬멧 밑에 쓸 머리를 미리
    /// 골라 두는 것은 정상 조작이고, 벗으면 그대로 나온다(#818, AccessoryCatalog.HiddenSlots).
    /// 흐림은 "지금 화면에 안 보인다"는 신호일 뿐이다.
    /// </summary>
    public void SetHidden(bool hidden)
    {
        m_hidden = hidden;
        Apply();
    }

    // 잠김이 가려짐을 이긴다 — 버튼을 끄는 쪽이 강한 상태다. 둘 다면 자물쇠와 잠김 투명도가 남는다.
    private void Apply()
    {
        if (m_lockMark != null)
            m_lockMark.SetActive(m_locked);

        if (m_icon != null)
        {
            Color color = m_icon.color;
            color.a = m_locked ? m_lockedAlpha : (m_hidden ? m_hiddenAlpha : 1f);
            m_icon.color = color;
        }

        if (m_button == null)
            m_button = GetComponent<Button>();

        m_button.interactable = !m_locked;
    }
}
