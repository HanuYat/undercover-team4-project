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
        if (m_factionText != null)
            m_factionText.text = OfficialRecords.FactionNames[faction];

        if (m_symbolImage != null)
        {
            m_symbolImage.sprite = symbol;
            m_symbolImage.enabled = symbol != null;
        }
    }
}
