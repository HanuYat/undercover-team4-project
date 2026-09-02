using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class FactionSymbolRowView : MonoBehaviour
{
    [SerializeField]
    private TMP_Text m_factionText;

    [SerializeField]
    private Image m_symbolImage;

    public void Bind(OfficialRecords.Faction faction, Sprite symbol)
    {
        // 표기 문구는 지금 언어로 조회한다 — 언어가 바뀌면 FactionSymbolBoardView가 목록을 다시 그린다. (#497)
        if (m_factionText != null)
            m_factionText.text = OfficialRecords.FactionName(faction);

        if (m_symbolImage != null)
        {
            m_symbolImage.sprite = symbol;
            m_symbolImage.enabled = symbol != null;
        }
    }
}
