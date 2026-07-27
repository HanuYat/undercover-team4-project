using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(fileName = "OfficialRecord", menuName = "Scriptable Objects/OfficialRecord")]
public class OfficialRecords : ScriptableObject
{
    public enum CitizenType { Human, Android }
    public enum Faction { None, FactionA, FactionB }

    public static readonly Dictionary<CitizenType, string> CitizenTypeNames = new()
    {
        { CitizenType.Human, "Human" },
        { CitizenType.Android, "Android" }
    };

    public static readonly Dictionary<Faction, string> FactionNames = new()
    {
        { Faction.None, "None" },
        { Faction.FactionA, "Faction A" },
        { Faction.FactionB, "Faction B" }
    };

    [System.Serializable]
    public struct FactionSymbolSet
    {
        public Faction faction;
        public Sprite[] variants;   // 세력 하나가 갖는 "비슷한 문양" 세트.
    }

    [SerializeField]
    private FactionSymbolSet[] m_factionSymbolSets;

    public Sprite[] GetVariants(Faction faction)
    {
        foreach (var set in m_factionSymbolSets)
            if (set.faction == faction) return set.variants;
        
        return null;
    }

    public int GetVariantsCount(Faction faction)
    {
        Sprite[] variants = GetVariants(faction);
        return variants != null ? variants.Length : 0;
    }

    public Sprite GetFactionSymbol(Faction faction, int index)
    {
        Sprite[] variants = GetVariants(faction);
        if (variants == null || index < 0 || index >= variants.Length) return null;

        return variants[index];
    }
}
