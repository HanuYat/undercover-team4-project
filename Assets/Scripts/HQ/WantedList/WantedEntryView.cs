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

    // 금액 서식은 프로젝트 공용이고 행마다 같은 문구다 — SerializeField로 두면 행 프리팹이
    // 늘 때마다 같은 키를 다시 배선해야 하고 하나만 빠지면 그 행만 옛 표기로 남는다. (#497)
    // 언어 변경 갱신은 WantedListView가 로케일 변경에 걸고 통째로 다시 그리는 것으로 처리한다.
    private const string k_commonTable = "CommonTable";
    private const string k_moneyKey = "Common.Unit.Money";

    public void Bind(in WantedEntry entry)
    {
        if (m_nameText != null)
            m_nameText.text = entry.Name.ToString();

        if (m_montageText != null)
            m_montageText.text = entry.Montage.ToString();

        if (m_bountyText != null)
            m_bountyText.text = LocalizedStrings.Get(k_commonTable, k_moneyKey, entry.Bounty);
    }
}
