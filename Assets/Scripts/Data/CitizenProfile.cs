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

    [Header("스캔으로 확인할 결과")]
    public string m_typeView;     // 표시 타입
    public string m_factionView;  // 표시 세력
    public Sprite m_symbolView;   // 표시 심볼

    // 생성 시: enum 랜덤 → 표시값을 OfficialRecords에서 복사 → 일정 확률로 한 글자만 오염
    // ex. typeText    = OfficialRecords.CitizenTypeNames[type];

    /// <summary>
    /// 런타임 생성용 초기화 — 실제 데이터를 채우고 표시값을 공식 기록에서 복사한다. (이슈 #38)
    /// 표시값 오염(디코이·난이도 튜닝)은 다음 빌드 과제라 지금은 실제값을 그대로 복사한다.
    /// </summary>
    public void Initialize(string citizenName, OfficialRecords.CitizenType citizenType,
        OfficialRecords.Faction faction, OfficialRecords officialRecords)
    {
        m_citizenName = citizenName;
        m_citizenType = citizenType;
        m_faction = faction;

        m_typeView = OfficialRecords.CitizenTypeNames[citizenType];
        m_factionView = OfficialRecords.FactionNames[faction];
        m_symbolView = officialRecords != null ? officialRecords.GetFactionSymbol(faction) : null;
    }
}
