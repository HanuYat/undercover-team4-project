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
    public struct FactionSymbol
    {
        public Faction faction;
        public Sprite symbol;
    }

    [SerializeField]
    private FactionSymbol[] m_factionSymbols;

    public Sprite GetFactionSymbol(Faction faction)
    {
        foreach (var pair in m_factionSymbols)
            if (pair.faction == faction) return pair.symbol;
        
        return null;
    }
}
