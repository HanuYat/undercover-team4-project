using UnityEngine;

[CreateAssetMenu(fileName = "OfficialRecord", menuName = "Scriptable Objects/OfficialRecord")]
public class OfficialRecords : ScriptableObject
{
    // 값 이름이 곧 표기 문구의 키다 (접두 + 이름). 값을 추가하면 NpcTable에 같은 이름의 키를 함께 넣을 것. (#497)
    [LocalizedEnum(k_table, "Npc.CitizenType.")]
    public enum CitizenType
    {
        Human,
        Android,
    }

    [LocalizedEnum(k_table, "Npc.Faction.")]
    public enum Faction
    {
        None,
        FactionA,
        FactionB,
    }

    private const string k_table = "NpcTable";

    /// <summary>
    /// 시민 타입의 표시 문구 — 지금 언어로 한 번 읽어 준다.
    /// 예전에는 <c>Dictionary&lt;CitizenType, string&gt;</c>에 영문이 박혀 있어 한국어 화면에서도 영문이 나왔다. (#497)
    /// <b>언어 변경 갱신은 호출부 책임</b>이다 — 표시하는 쪽이 <c>SelectedLocaleChanged</c>에 걸어 다시 그려야 한다.
    /// </summary>
    public static string TypeName(CitizenType type) =>
        LocalizedStrings.Get(k_table, "Npc.CitizenType." + type);

    /// <summary>세력의 표시 문구. 갱신 책임은 <see cref="TypeName"/>과 같다.</summary>
    public static string FactionName(Faction faction) =>
        LocalizedStrings.Get(k_table, "Npc.Faction." + faction);

    [System.Serializable]
    public struct FactionSymbolSet
    {
        public Faction faction;
        public Sprite[] variants; // 세력 하나가 갖는 "비슷한 문양" 세트.
    }

    [SerializeField]
    private FactionSymbolSet[] m_factionSymbolSets;

    public Sprite[] GetVariants(Faction faction)
    {
        foreach (var set in m_factionSymbolSets)
            if (set.faction == faction)
                return set.variants;

        return null;
    }

    public int GetVariantsCount(Faction faction)
    {
        Sprite[] variants = GetVariants(faction);
        return variants != null ? variants.Length : 0;
    }

    // 이름 풀을 여기 두는 이유: 팩토리(CitizenProfileFactory)가 신원 4축(이름·타입·세력·문양)을 한 번에
    // 만드는데 이 에셋이 이미 나머지 셋을 들고 있다. CriminalAssigner에 SerializeField를 새로 달면
    // 그 컴포넌트가 있는 씬 3개를 전부 고쳐야 한다 — 팩토리가 이미 받는 참조로 온다. (#752)
    [Tooltip("시민 이름 풀 — 비워 두면 이름이 배정되지 않는다 (#752)")]
    [SerializeField] private CitizenNameCatalog m_citizenNames;

    /// <summary>시민 이름 풀 — 팩토리가 라운드 시작에 읽는다. 배선이 빠지면 null. (#752)</summary>
    public CitizenNameCatalog CitizenNames => m_citizenNames;

    public Sprite GetFactionSymbol(Faction faction, int index)
    {
        Sprite[] variants = GetVariants(faction);
        if (variants == null || index < 0 || index >= variants.Length)
            return null;

        return variants[index];
    }
}
