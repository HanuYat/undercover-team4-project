using UnityEngine;

[CreateAssetMenu(fileName = "CitizenProfile", menuName = "Scriptable Objects/CitizenProfile")]
public class CitizenProfile : ScriptableObject
{
    [Header("실제 데이터")]
    [SerializeField] private string m_citizenName;
    [SerializeField] private OfficialRecords.CitizenType m_citizenType;
    [SerializeField] private OfficialRecords.Faction m_faction;

    public string CitizenName => m_citizenName;
    public OfficialRecords.CitizenType CitizenType => m_citizenType;
    public OfficialRecords.Faction Faction => m_faction;

    // ---- 스캔 표시값(view) ----
    // 현장 스캐너가 읽는 값. 정본(위 실제 데이터)과 별개로, 위조범은 이 표시값만 어긋나게 오염된다 (#223).
    // 본부 인명부는 정본(CitizenName/Faction)을, 스캔 UI는 표시값(m_nameView/m_factionView/m_symbolView)을 쓴다.
    [Header("스캔으로 확인할 결과")]
    public string m_nameView;     // 표시 이름 — 정본과 다르면 이름 위조 (#223)
    public string m_typeView;     // 표시 타입
    public string m_factionView;  // 표시 세력
    public Sprite m_symbolView;   // 표시 심볼
    public int m_symbolIndexView;    // 표시 심볼의 variant index

    /// <summary>
    /// 런타임 생성용 초기화 — 실제 데이터를 채우고 표시값을 정본과 동일하게 세팅한다(정상 시민 기준). (이슈 #38)
    /// 위조(표시값 오염)는 이 뒤 배정 단계(#223)에서 표시값 필드만 덮어써 적용한다 — Initialize 자체는 항상 정상 프로필을 만든다.
    /// </summary>
    public void Initialize(string citizenName, OfficialRecords.CitizenType citizenType,
        OfficialRecords.Faction faction, int symbolIndex, OfficialRecords officialRecords)
    {
        m_citizenName = citizenName;
        m_citizenType = citizenType;
        m_faction = faction;

        m_nameView = citizenName;
        m_typeView = OfficialRecords.CitizenTypeNames[citizenType];
        m_factionView = OfficialRecords.FactionNames[faction];
        m_symbolIndexView = symbolIndex;
        m_symbolView = officialRecords != null ? officialRecords.GetFactionSymbol(faction, symbolIndex) : null;
    }

    /// <summary>표시 문양만 다른 variant로 덮어쓴다 — 위조범의 문양 위조 전용 (#222/#223).</summary>
    public void SetSymbolIndexView(int symbolIndex, OfficialRecords officialRecords)
    {
        m_symbolIndexView = symbolIndex;
        m_symbolView = officialRecords != null ? officialRecords.GetFactionSymbol(m_faction, symbolIndex) : null;
    }
}
