using TMPro;
using UnityEngine;

/// <summary>
/// 수배 리스트의 한 줄 — 검거 대상의 이름 / 몽타주 포트레이트. 표시 전용.
///
/// 몽타주는 그림이 정본이다 (#607). 글(#497)은 표시에서 빠지고 비교용 토글로만 남는다 —
/// 본부가 그림을 보고 자기 말로 무전에 옮기는 것까지가 이 게임의 몫이라, 읽어 주면 되는 문장을 주면 그 과정이 사라진다.
/// 그림도 글과 같은 규칙이라 완성물이 아니라 재료(공개 축 + 값)만 항목에 실려 오고 각 피어가 조립한다 —
/// 그래서 AppearanceDatabase를 Bind 인자로 받는다(행마다 배선하지 않기 위해).
/// </summary>
public class WantedEntryView : MonoBehaviour
{
    [Header("몽타주")]
    [SerializeField]
    private MontagePortraitView m_portrait;

    [Header("텍스트 참조")]
    [SerializeField]
    private TMP_Text m_nameText;

    [Tooltip("현상금 표시 (#395). 비워 두면 표시하지 않는다")]
    [SerializeField]
    private TMP_Text m_bountyText;

    [Tooltip("옛 글 방식 몽타주(#497)를 함께 띄운다 — 그림 가독성을 글과 견줄 때만 켠다 (appearance-montage.md §7)")]
    [SerializeField]
    private bool m_showMontageText;

    [SerializeField]
    private TMP_Text m_montageText;

    [Tooltip("수배 조건 표시 (생사 불문/생포 필수, #766). 비워 두면 표시하지 않는다")]
    [SerializeField]
    private TMP_Text m_conditionText;

    // 금액 서식은 프로젝트 공용이고 행마다 같은 문구다 — SerializeField로 두면 행 프리팹이
    // 늘 때마다 같은 키를 다시 배선해야 하고 하나만 빠지면 그 행만 옛 표기로 남는다. (#497)
    // 언어 변경 갱신은 WantedListView가 로케일 변경에 걸고 통째로 다시 그리는 것으로 처리한다.
    private const string k_commonTable = "CommonTable";
    private const string k_moneyKey = "Common.Unit.Money";
    private const string k_hqTable = "HqTable";
    private const string k_conditionPrefix = "Hq.Wanted.Condition.";

    public void Bind(in WantedEntry entry, AppearanceDatabase appearanceDatabase)
    {
        if (m_nameText != null)
            m_nameText.text = entry.Name.ToString();

        if (m_portrait != null)
            m_portrait.Bind(entry.Appearance, entry.RevealedAxes, appearanceDatabase);

        if (m_montageText != null)
        {
            m_montageText.gameObject.SetActive(m_showMontageText);
            if (m_showMontageText)
                m_montageText.text = appearanceDatabase != null
                    ? appearanceDatabase.BuildMontageText(entry.Appearance, entry.RevealedAxes)
                    : string.Empty;
        }

        if (m_bountyText != null)
            m_bountyText.text = LocalizedStrings.Get(k_commonTable, k_moneyKey, entry.Bounty);

        if (m_conditionText != null)
            m_conditionText.text = LocalizedStrings.Get(k_hqTable, k_conditionPrefix + entry.Condition);
    }
}
