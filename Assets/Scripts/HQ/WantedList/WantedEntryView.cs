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

    [Tooltip("현상금 표시 (#395). 비워 두면 표시하지 않는다")]
    [SerializeField]
    private TMP_Text m_bountyText;

    [Tooltip("현상금 표시 형식 — {0}=금액")]
    [SerializeField]
    private string m_bountyFormat = "{0:N0}원";

    public void Bind(in WantedEntry entry)
    {
        if (m_nameText != null)
            m_nameText.text = entry.Name.ToString();

        if (m_montageText != null)
            m_montageText.text = entry.Montage.ToString();

        if (m_bountyText != null)
            m_bountyText.text = string.Format(m_bountyFormat, entry.Bounty);
    }
}
