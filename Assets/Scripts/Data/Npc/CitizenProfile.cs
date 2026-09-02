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
    // 현장 스캐너가 읽는 값. 본부 인명부는 정본(CitizenName/Faction)을, 스캔 UI는 표시값을 쓴다.
    // 위조(표시값 오염)를 걷어낸 뒤로 표시값은 항상 정본과 같다 — 두 경로는 남겨 둔다.
    [Header("스캔으로 확인할 결과")]
    public string m_nameView;     // 표시 이름

    // 타입·세력은 enum으로 둔다 — 표기 문구는 표시 시점에 번역한다 (#497).
    // 완성된 문자열을 담아 두면 그 문구만 번역에서 빠지고, 프로필이 만들어진 시점의 언어로 굳는다.
    public OfficialRecords.CitizenType m_typeView;     // 표시 타입
    public OfficialRecords.Faction m_factionView;      // 표시 세력
    public Sprite m_symbolView;   // 표시 심볼
    public int m_symbolIndexView;    // 표시 심볼의 variant index

    /// <summary>런타임 생성용 초기화 — 실제 데이터를 채우고 표시값을 정본과 동일하게 세팅한다. (이슈 #38)</summary>
    public void Initialize(string citizenName, OfficialRecords.CitizenType citizenType,
        OfficialRecords.Faction faction, int symbolIndex, OfficialRecords officialRecords)
    {
        m_citizenName = citizenName;
        m_citizenType = citizenType;
        m_faction = faction;

        m_nameView = citizenName;
        m_typeView = citizenType;
        m_factionView = faction;
        m_symbolIndexView = symbolIndex;
        m_symbolView = officialRecords != null ? officialRecords.GetFactionSymbol(faction, symbolIndex) : null;
    }
}
