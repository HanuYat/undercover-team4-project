using TMPro;
using UnityEngine;

/// <summary>
/// 스캔 결과 텍스트 출력 전용 뷰. (#39)
/// 이름·타입·세력을 분할된 텍스트에 각각 표시한다.
/// 데이터 조회·이벤트 구독 없이 프레젠터(ScanResultPresenter)가 넘겨준 문자열만 표시한다.
/// </summary>
public class ScanResultView : MonoBehaviour
{
    [Header("UI 참조")]
    [SerializeField]
    private TMP_Text m_nameText;

    [SerializeField]
    private TMP_Text m_typeText;

    [SerializeField]
    private TMP_Text m_factionText;

    /// <summary>스캔 결과를 항목별 텍스트에 표시한다.</summary>
    public void Show(string citizenName, string typeView, string factionView)
    {
        m_nameText.text = $"Name: {citizenName}";
        m_typeText.text = $"Type: {typeView}";
        m_factionText.text = $"Faction: {factionView}";
    }

    /// <summary>표시 중인 결과를 모두 지운다.</summary>
    public void Clear()
    {
        m_nameText.text = string.Empty;
        m_typeText.text = string.Empty;
        m_factionText.text = string.Empty;
    }
}
