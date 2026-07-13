using TMPro;
using UnityEngine;

/// <summary>
/// 수배 리스트의 한 줄 — 검거 대상의 이름 / 글 방식 몽타주.
/// 행 프리팹에 붙여 두고, WantedListView가 항목 데이터를 Bind로 채운다. 표시 전용.
/// </summary>
public class WantedEntryView : MonoBehaviour
{
    [Header("텍스트 참조")]
    [SerializeField]
    private TMP_Text m_nameText;

    [SerializeField]
    private TMP_Text m_montageText;

    public void Bind(in WantedEntry entry)
    {
        if (m_nameText != null)
            m_nameText.text = entry.Name.ToString();

        if (m_montageText != null)
            m_montageText.text = entry.Montage.ToString();
    }
}
