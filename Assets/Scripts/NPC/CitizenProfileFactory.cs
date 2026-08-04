using System;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

/// <summary>
/// 시민 한 명분의 신원(이름·타입·세력·문양)을 만든다 — 라운드 시작 배정의 재료 공급처. (#38 · #222 · #223)
///
/// <see cref="CitizenProfile.Initialize"/>는 <b>항상 정본 프로필</b>을 만들고, 위조는 그 뒤에 표시값만
/// 덮어쓰는 2단 구조다. 이 클래스가 그 두 단계를 <see cref="Create"/>와 <see cref="ApplyForgery"/>로
/// 그대로 나눠 갖는다 — 순서를 지키지 않으면(위조 후 Initialize) 오염값이 정본으로 되돌아간다.
///
/// 이름 풀은 생성자에서 한 번 섞어 인원수만큼 확정한다 — 라운드 안에서 중복이 없어야 하므로
/// 개별 호출로는 만들 수 없다. 그래서 정적 유틸이 아니라 인스턴스다.
/// </summary>
public sealed class CitizenProfileFactory
{
    // 임시 이름 풀 — 사이버펑크 톤. 추후 데이터 에셋으로 분리 가능.
    // 라운드 시작 인원(NpcSpawner.m_spawnCount)뿐 아니라 라운드 중에 스폰되는 돌발 이벤트
    // NPC(#106)까지 이 풀에서 이어 뽑는다 — 여유가 없으면 NextName의 번호 폴백이 화면에 나온다 (#505).
    private static readonly string[] s_namePool =
    {
        "Kai Vex",
        "Nova Lin",
        "Rex Halden",
        "Mira Sato",
        "Juno Ashe",
        "Silas Kwon",
        "Vera Molnar",
        "Dax Rivera",
        "Iris Chen",
        "Orin Blake",
        "Lena Voss",
        "Cyrus Nam",
        "Tessa Rho",
        "Egan Cole",
        "Yuna Park",
        "Marlo Finn",
        "Sana Idris",
        "Bront Keller",
        "Hana Ryu",
        "Odis Grant",
        "Piper Nyx",
        "Ravi Sol",
        "Wren Okada",
        "Zane Mercer",
        "Ada Krell",
        "Bex Otoro",
        "Coda Vane",
        "Doro Kesh",
        "Elin Marsh",
        "Fen Alarie",
        "Gil Vantor",
        "Hollis Bay",
        "Ivo Strand",
        "Jae Corbin",
        "Kira Lund",
        "Lux Ferrer",
        "Mox Danil",
        "Nadia Sork",
        "Oren Talis",
        "Pax Ludwin",
    };

    // None(무소속·문양 없음)은 위조 대조 축이 될 수 없어 배정에서 제외한다 (#222 (b)).
    // enum에 세력을 추가하면 자동으로 후보에 포함된다 — 여기를 고칠 필요 없음.
    private static readonly OfficialRecords.Faction[] s_assignableFactions = BuildAssignableFactions();

    private readonly OfficialRecords m_records;

    // 라운드 시작에 한 번 섞어 두는 이름 풀 — 뽑기 전에 섞어야 순서가 곧 무작위 배정이 된다.
    private readonly string[] m_shuffledNames;

    // 지금까지 내준 이름 수 = 다음에 내줄 자리. 라운드 중에 스폰되는 이벤트 NPC(#106)도 같은
    // 인스턴스에서 이어 뽑으므로 중복 회피가 이 커서 한 곳에 남는다 (#505).
    private int m_issuedCount;

    // 세션 중 세력별 '진짜' 문양 index — FactionSymbolManager가 없는 오프라인 테스트용 폴백 캐시.
    private readonly Dictionary<OfficialRecords.Faction, int> m_localRealIndices =
        new Dictionary<OfficialRecords.Faction, int>();

    /// <param name="records">세력 심볼 조회용 공식 기록. null이면 문양 위조가 불가능해 이름 위조로 폴백한다.</param>
    public CitizenProfileFactory(OfficialRecords records)
    {
        m_records = records;
        m_shuffledNames = ShuffledPool();
    }

    /// <summary>
    /// 다음 시민의 정본 프로필을 만든다 — 이름은 섞어 둔 풀에서 순서대로, 타입·세력은 추첨한다.
    /// 한 인스턴스에서 뽑는 동안 이름은 중복되지 않는다 — 호출 시점이 라운드 시작이든 도중이든 같다 (#505).
    /// 프로필은 에셋이 아닌 런타임 인스턴스다 — 라운드마다 새로 배정된다.
    /// </summary>
    public CitizenProfile Create()
    {
        OfficialRecords.Faction faction = RandomFaction();

        CitizenProfile profile = ScriptableObject.CreateInstance<CitizenProfile>();
        profile.Initialize(
            NextName(),
            RandomEnum<OfficialRecords.CitizenType>(),
            faction,
            RealSymbolIndex(faction),
            m_records
        );
        return profile;
    }

    /// <summary>
    /// 위조범의 표시값을 정본/인명부와 어긋나게 한다 — 이름·문양 중 <b>하나만</b> 오염한다 (#222 (a)①).
    /// 문양 variant가 2개 미만이면 가짜를 만들 수 없어 이름 위조로 폴백한다 (#222 (c)).
    /// 반드시 AssignProfile(= CitizenData 동기화 스냅샷) 이전에 호출해야 오염값이 전 클라에 전파된다 (#223).
    /// </summary>
    /// <returns>문양을 위조했으면 true, 이름을 위조했으면 false — 진단 로그가 어느 축인지 구분하는 데 쓴다.</returns>
    public bool ApplyForgery(CitizenProfile profile, int forgedCharCount)
    {
        OfficialRecords.Faction faction = profile.Faction;
        bool canForgeSymbol = m_records != null && m_records.GetVariantsCount(faction) >= 2;

        if (canForgeSymbol && Random.value < 0.5f)
        {
            profile.SetSymbolIndexView(PickFakeSymbolIndex(faction, RealSymbolIndex(faction)), m_records);
            return true;
        }

        profile.m_nameView = NameForgery.Corrupt(profile.CitizenName, forgedCharCount);
        return false;
    }

    /// <summary>
    /// 이번 세션에 이 세력의 진짜 문양 index. 세션 중이면 동기화 값, 오프라인이면 로컬 폴백. (#222)
    /// 위조 진단 로그가 "진짜 → 가짜"를 찍을 때도 쓴다.
    /// </summary>
    public int RealSymbolIndex(OfficialRecords.Faction faction)
    {
        FactionSymbolManager manager = App.Game.FactionSymbol;
        if (manager != null)
            return manager.RealIndex(faction);

        if (!m_localRealIndices.TryGetValue(faction, out int index))
        {
            int count = m_records != null ? m_records.GetVariantsCount(faction) : 0;
            index = count > 0 ? Random.Range(0, count) : 0;
            m_localRealIndices[faction] = index;
        }
        return index;
    }

    /// <summary>진짜를 제외한 나머지 variant 중 하나 — 위조범의 가짜 문양. variant 2개 이상일 때만 호출. (#222)</summary>
    private int PickFakeSymbolIndex(OfficialRecords.Faction faction, int realIndex)
    {
        int count = m_records.GetVariantsCount(faction);
        int pick = Random.Range(0, count - 1); // 진짜 1개를 뺀 범위에서 뽑고
        return pick >= realIndex ? pick + 1 : pick; // 진짜 자리를 건너뛴다
    }

    private static OfficialRecords.Faction RandomFaction() =>
        s_assignableFactions.Length > 0
            ? s_assignableFactions[Random.Range(0, s_assignableFactions.Length)]
            : OfficialRecords.Faction.None;

    private static OfficialRecords.Faction[] BuildAssignableFactions()
    {
        var all = (OfficialRecords.Faction[])Enum.GetValues(typeof(OfficialRecords.Faction));
        var list = new List<OfficialRecords.Faction>(all.Length);
        foreach (OfficialRecords.Faction faction in all)
            if (faction != OfficialRecords.Faction.None)
                list.Add(faction);
        return list.ToArray();
    }

    /// <summary>이름 풀을 피셔-예이츠로 섞은 사본. 뽑기 전에 한 번만 돌린다.</summary>
    private static string[] ShuffledPool()
    {
        string[] shuffled = (string[])s_namePool.Clone();
        for (int i = shuffled.Length - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
        }
        return shuffled;
    }

    // 다음 이름 하나. 풀을 다 쓰면 번호를 붙여 재사용한다 — 보기 좋지 않지만 중복 자체는 없다.
    // 번호가 보이기 시작하면 s_namePool을 늘릴 신호다 (#505).
    private string NextName()
    {
        int index = m_issuedCount++;
        int length = m_shuffledNames.Length;
        return index < length
            ? m_shuffledNames[index]
            : $"{m_shuffledNames[index % length]} {index / length + 1}";
    }

    private static TEnum RandomEnum<TEnum>()
        where TEnum : Enum
    {
        Array values = Enum.GetValues(typeof(TEnum));
        return (TEnum)values.GetValue(Random.Range(0, values.Length));
    }
}
