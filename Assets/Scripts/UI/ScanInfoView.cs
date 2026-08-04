using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// NPC 머리 위에 붙는 월드공간 스캔 정보 카드. (#233)
/// 위치 추종·빌보드·누운 자세 보정·표시 토글은 <see cref="NpcWorldCard"/>가 담당하고,
/// 이 클래스는 카드에 무엇을 쓸지만 맡는다.
///
/// 표시 내용(실제/??)은 각 클라이언트의 ScanResultPresenter가 로컬로 세팅한다 —
/// 네트워크 동기화 없음(개인별 관리, GDD 5-4). NPC는 각 클라마다 로컬 인스턴스가 있으므로
/// 클라 A는 실제값, 클라 B는 ??를 각자 자기 화면의 같은 NPC에 독립적으로 찍을 수 있다.
/// </summary>
public class ScanInfoView : NpcWorldCard
{
    [Header("텍스트")]
    [SerializeField]
    private TMP_Text m_nameText;

    [SerializeField]
    private TMP_Text m_typeText;

    [SerializeField]
    private TMP_Text m_factionText;

    [Tooltip("스캔된 NPC의 세력 문양")]
    [SerializeField]
    private Image m_symbolImage;

    // 미스캔 NPC의 미확인 필드 표기 (#233)
    private const string k_masked = "??";

    // 필드 라벨은 카드마다 같은 문구이고 카드는 NPC 수만큼 있다 — SerializeField로 두면
    // NPC 프리팹마다 같은 키를 다시 배선해야 하고 하나만 빠지면 그 NPC만 옛 표기로 남는다. (#497)
    // 언어 변경 갱신은 ScanResultPresenter가 로케일 변경에 걸고 다시 채우는 것으로 처리한다.
    private const string k_hudTable = "HudTable";
    private const string k_nameKey = "Hud.Scan.FieldName";
    private const string k_typeKey = "Hud.Scan.FieldType";
    private const string k_factionKey = "Hud.Scan.FieldFaction";

    /// <summary>
    /// 스캔 완료 NPC — 실제 프로필 값을 표시하고 카드를 켠다.
    /// 타입·세력은 완성된 문자열이 아니라 enum으로 받는다 — 표기를 여기서 지금 언어로 번역한다 (#497).
    /// </summary>
    public void ShowReal(
        string citizenName,
        OfficialRecords.CitizenType typeView,
        OfficialRecords.Faction factionView,
        Sprite symbolView
    )
    {
        SetFields(citizenName, OfficialRecords.TypeName(typeView), OfficialRecords.FactionName(factionView));
        SetSymbol(symbolView);
        SetCardActive(true);
    }

    /// <summary>미스캔 NPC — 모든 필드를 ??로 마스킹하고 카드를 켠다.</summary>
    public void ShowMasked()
    {
        SetFields(k_masked, k_masked, k_masked);
        SetSymbol(null);
        SetCardActive(true);
    }

    // 실제값이든 ??든 라벨 서식은 같다 — 마스킹은 값만 바꾼다.
    private void SetFields(string citizenName, string typeView, string factionView)
    {
        m_nameText.text = LocalizedStrings.Get(k_hudTable, k_nameKey, citizenName);
        m_typeText.text = LocalizedStrings.Get(k_hudTable, k_typeKey, typeView);
        m_factionText.text = LocalizedStrings.Get(k_hudTable, k_factionKey, factionView);
    }

    // 문양 스프라이트 세팅
    private void SetSymbol(Sprite symbol)
    {
        if (m_symbolImage == null) return;

        m_symbolImage.sprite = symbol;
        m_symbolImage.enabled = symbol != null;
    }
}
