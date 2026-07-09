using UnityEngine;

[CreateAssetMenu(fileName = "CitizenProfile", menuName = "Scriptable Objects/CitizenProfile")]
public class CitizenProfile : ScriptableObject
{
    [Header("실제 데이터")]
    public string m_citizenName;
    public OfficialRecords.CitizenType m_citizenType;    // 실제 타입
    public OfficialRecords.Faction m_faction;            // 실제 세력

    [Header("스캔으로 확인할 결과")]
    public string m_typeView;     // 표시 타입
    public string m_factionView;  // 표시 세력
    public Sprite m_symbolView;   // 표시 심볼

    // 생성 시: enum 랜덤 → 표시값을 OfficialRecords에서 복사 → 일정 확률로 한 글자만 오염
    // ex. typeText    = OfficialRecords.CitizenTypeNames[type];
}
