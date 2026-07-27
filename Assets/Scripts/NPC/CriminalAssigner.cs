using System;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

/// <summary>
/// 스폰 완료 후 모든 NPC에 랜덤 시민 프로필을 채우고, 인스펙터에서 지정한 수만큼 실제 범인으로 지정한다. (이슈 #38/#127)
/// 무고 시민도 확률적으로 도주/저항 반응을 보인다 — 진범을 헷갈리게 하는 미끼 행동으로,
/// 잡아도 보상 없는 오검거일 뿐이다(진범만 유효 검거). 행동은 단서가 아니라 노이즈다. (#78)
/// 범인의 프로필(WantedProfile)이 곧 본부 수배 데이터(#58)의 원본이 된다.
/// </summary>
// 네트워크 세션에서 배정은 자연히 서버 전용이다 — 스포너의 OnSpawnCompleted가 서버에서만 발생한다 (#56).
// 배정 결과 중 공개 가능한 신원은 CitizenIdentity의 NetworkVariable로 전 클라이언트에 동기화된다 (#52).
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class CriminalAssigner : CommonManagerBase
{
    private NpcSpawner Spawner => App.Game.NpcSpawner;

    [Header("공식 기록 (세력 심볼 조회용)")]
    [SerializeField]
    private OfficialRecords m_officialRecords;

    [Header("진범 수 (#127)")]
    [Tooltip("이번 라운드에 배정할 진범 수. NPC 수보다 크면 NPC 수로 잘라 배정한다(경고 로그)")]
    [Min(1)]
    [SerializeField]
    private int m_criminalCount = 1;

    [Header("위조범 (#223)")]
    [Tooltip("표시 이름을 오염시킬 위조범 수. 진범과 독립 배정된다")]
    [Min(0)]
    [SerializeField]
    private int m_forgerCount = 2;

    [Tooltip("위조범 1명의 표시 이름에서 오염시킬 글자 수(모음↔모음·자음↔자음 치환). 이름 글자 수보다 크면 잘린다")]
    [Min(1)]
    [SerializeField]
    private int m_forgedCharCount = 1;

    [Header("범인 검거 반응 가중치 (#76)")]
    [Tooltip("합이 1일 필요 없음 — 비율로 추첨한다. 범인은 도주/저항 성향이 높다")]
    [SerializeField]
    private float m_compliantWeight = 0.2f;

    [SerializeField]
    private float m_fleeWeight = 0.4f;

    [SerializeField]
    private float m_resistWeight = 0.4f;

    [Header("일반 시민 검거 반응 가중치 (#78)")]
    [Tooltip(
        "무고 시민의 반응 추첨 비율. 도주/저항은 진범을 헷갈리게 하는 미끼일 뿐 잡아도 오검거다 — 이 비율로 미끼 행동의 빈도(난이도)를 조절한다. 기본값은 대부분 순응 + 소수만 도주/저항"
    )]
    [SerializeField]
    private float m_citizenCompliantWeight = 0.8f;

    [SerializeField]
    private float m_citizenFleeWeight = 0.1f;

    [SerializeField]
    private float m_citizenResistWeight = 0.1f;

    // 임시 이름 풀 — 사이버펑크 톤. 추후 데이터 에셋으로 분리 가능
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
    };

    private readonly List<NpcController> m_criminalNpcs = new List<NpcController>();
    private readonly List<CitizenProfile> m_wantedProfiles = new List<CitizenProfile>();

    private readonly Dictionary<OfficialRecords.Faction, int> m_localRealIndices = new Dictionary<OfficialRecords.Faction, int>();

    /// <summary>실제 범인으로 지정된 NPC들. 배정 전에는 비어 있다. (#127)</summary>
    public IReadOnlyList<NpcController> CriminalNpcs => m_criminalNpcs;

    /// <summary>범인들의 프로필 = 본부 수배 데이터(#58)의 원본. CriminalNpcs와 같은 순서.</summary>
    public IReadOnlyList<CitizenProfile> WantedProfiles => m_wantedProfiles;

    /// <summary>배정 완료 이벤트 — 수배 UI(#58)·진범 판정(#41) 등이 구독한다. 배정된 전체 범인 목록을 넘긴다. (#127)</summary>
    public event Action<IReadOnlyList<NpcController>> OnCriminalAssigned;

    private void Start()
    {
        if (Spawner == null)
        {
            Debug.LogWarning("CriminalAssigner: NpcSpawner를 찾지 못해 배정 불가", this);
            return;
        }

        // 스포너가 이미 스폰을 끝냈으면 바로 배정, 아니면 완료 이벤트를 기다린다
        if (Spawner.IsSpawnCompleted)
            AssignAll();
        else
            Spawner.OnSpawnCompleted += AssignAll;
    }

    protected override void OnDestroy()
    {
        base.OnDestroy(); // App 등록 해제

        if (Spawner != null)
            Spawner.OnSpawnCompleted -= AssignAll;
    }

    private void AssignAll()
    {
        IReadOnlyList<NpcController> npcs = Spawner.SpawnedNpcs;
        if (npcs.Count == 0)
        {
            Debug.LogWarning("CriminalAssigner: 스폰된 NPC가 없어 배정 불가", this);
            return;
        }

        // 진범 수는 NPC 수를 넘을 수 없다 — 초과 지정 시 잘라서 배정한다 (#127)
        int criminalCount = Mathf.Clamp(m_criminalCount, 1, npcs.Count);
        if (m_criminalCount > npcs.Count)
            Debug.LogWarning(
                $"CriminalAssigner: 진범 수({m_criminalCount})가 NPC 수({npcs.Count})보다 많아 {criminalCount}명으로 잘라 배정한다",
                this
            );

        HashSet<int> criminalIndices = PickCriminalIndices(npcs.Count, criminalCount);
        // 위조범은 진범과 독립적으로 추첨한다 — 겹칠 수도 있다(범인이 위조 papers 소지) (#223)
        HashSet<int> forgerIndices = PickCriminalIndices(npcs.Count, Mathf.Clamp(m_forgerCount, 0, npcs.Count));
        string[] names = BuildUniqueNames(npcs.Count);

        m_criminalNpcs.Clear();
        m_wantedProfiles.Clear();

        // 스캔 UI(#39) 전까지는 로그로 배정 결과를 확인한다
        var logBuilder = new System.Text.StringBuilder();
        logBuilder.AppendLine($"시민 프로필 배정 완료 ({npcs.Count}명, 진범 {criminalCount}명):");

        for (int i = 0; i < npcs.Count; i++)
        {
            // CitizenIdentity는 NetworkBehaviour라 스폰 뒤 AddComponent로 붙일 수 없다 (#52)
            // — NPC 프리팹에 미리 부착돼 있어야 하며, 없으면 이 NPC는 신원 없이 배회만 한다
            CitizenIdentity identity = npcs[i].GetComponent<CitizenIdentity>();
            if (identity == null)
            {
                Debug.LogWarning(
                    $"CriminalAssigner: {npcs[i].name}에 CitizenIdentity가 없어 신원 배정을 건너뜀 — NPC 프리팹에 부착 필요",
                    npcs[i]
                );
                continue;
            }

            // 프로필은 에셋이 아닌 런타임 인스턴스 — 라운드마다 새로 배정된다
            OfficialRecords.Faction faction = RandomFaction();
            int realIndex = RealSymbolIndex(faction);

            CitizenProfile profile = ScriptableObject.CreateInstance<CitizenProfile>();
            profile.Initialize(
                names[i],
                RandomEnum<OfficialRecords.CitizenType>(),
                faction,
                realIndex,
                m_officialRecords
            );

            // 위조범: 표시값을 정본/인명부와 어긋나게 한다. 이름·문양 중 하나만 오염한다 (#222 (a)①) —
            // 본부가 "이름이 안 맞나 문양이 안 맞나"를 매번 새로 대조하게 만든다.
            // 문양 variant가 2개 미만이면 가짜를 만들 수 없어 이름 위조로 폴백한다 (#222 (c)).
            // 반드시 AssignProfile(= CitizenData 동기화 스냅샷) 이전에 적용해야 오염값이 전 클라에 전파된다 (#223)
            bool isForger = forgerIndices.Contains(i);
            bool forgedSymbol = false;
            if (isForger)
            {
                bool canForgeSymbol =
                    m_officialRecords != null && m_officialRecords.GetVariantsCount(faction) >= 2;
                forgedSymbol = canForgeSymbol && Random.value < 0.5f;

                if (forgedSymbol)
                    profile.SetSymbolIndexView(PickFakeSymbolIndex(faction, realIndex), m_officialRecords);
                else
                    profile.m_nameView = CorruptName(profile.CitizenName, m_forgedCharCount);
            }

            bool isCriminal = criminalIndices.Contains(i);
            identity.AssignProfile(profile, isCriminal);
            // 위조 여부를 신원에 기록 — 위조 검거 판정(#320)이 읽는다. 이름 오염(m_nameView)과 별개의 서버 전용 플래그.
            identity.AssignForgery(isForger);

            // 검거 반응 — 범인은 범인 가중치로, 무고 시민은 시민 가중치로 추첨한다.
            // 시민의 도주/저항은 진범을 헷갈리게 하는 미끼 행동일 뿐 판정엔 영향이 없다 (GDD 6-1/6-3, #76/#78)
            ReactionType reaction = isCriminal
                ? RollReaction(m_compliantWeight, m_fleeWeight, m_resistWeight)
                : RollReaction(m_citizenCompliantWeight, m_citizenFleeWeight, m_citizenResistWeight);
            identity.AssignReaction(reaction);

            if (isCriminal)
            {
                m_criminalNpcs.Add(npcs[i]);
                m_wantedProfiles.Add(profile);
            }

            // 범인 표시는 정답이 노출되므로 데모 빌드 전에 제거할 것. 반응은 미끼 행동 확인용으로 함께 로그
            string roleTag = isCriminal ? $"  ← 범인 ({reaction})"
                : reaction != ReactionType.Compliant ? $"  (미끼: {reaction})"
                : "";

            // 위조 시 어느 축이 오염됐는지 함께 남겨 대조 확인에 쓴다 (데모 빌드 전 제거 대상)
            string forgeryTag = !isForger ? ""
                : forgedSymbol ? $"  [위조: 문양 {realIndex}→{profile.m_symbolIndexView}]"
                : $"  [위조: {profile.CitizenName}→{profile.m_nameView}]";

            logBuilder.AppendLine(
                $"  {profile.CitizenName} | {profile.m_typeView} | {profile.m_factionView}{roleTag}{forgeryTag}"
            );
        }

        OnCriminalAssigned?.Invoke(m_criminalNpcs);
        Debug.Log(logBuilder.ToString());
    }

    /// <summary>0~total-1 인덱스를 셔플해 앞에서 count개를 뽑는다 — 중복 없는 진범 인덱스. (#127)</summary>
    private static HashSet<int> PickCriminalIndices(int total, int count)
    {
        // 인덱스 배열 피셔-예이츠 셔플 — 이름 풀(BuildUniqueNames)과 같은 방식
        int[] indices = new int[total];
        for (int i = 0; i < total; i++)
            indices[i] = i;

        for (int i = total - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (indices[i], indices[j]) = (indices[j], indices[i]);
        }

        var result = new HashSet<int>();
        for (int i = 0; i < count; i++)
            result.Add(indices[i]);
        return result;
    }

    /// <summary>이름 풀을 섞어 중복 없는 이름 배열을 만든다. NPC가 풀보다 많으면 번호를 붙인다.</summary>
    private static string[] BuildUniqueNames(int count)
    {
        // 풀 복사 후 피셔-예이츠 셔플
        string[] shuffled = (string[])s_namePool.Clone();
        for (int i = shuffled.Length - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
        }

        string[] result = new string[count];
        for (int i = 0; i < count; i++)
        {
            result[i] =
                i < shuffled.Length
                    ? shuffled[i]
                    : $"{shuffled[i % shuffled.Length]} {i / shuffled.Length + 1}"; // 풀 초과분은 번호로 구분
        }
        return result;
    }

    /// <summary>순응/도주/저항 가중치 비율로 검거 반응을 추첨한다. 범인·시민이 각자의 가중치로 호출한다. (#76/#78)</summary>
    private static ReactionType RollReaction(float compliantWeight, float fleeWeight, float resistWeight)
    {
        float total = compliantWeight + fleeWeight + resistWeight;
        if (total <= 0f)
            return ReactionType.Compliant; // 가중치가 전부 0이면 안전하게 순응

        float roll = Random.Range(0f, total);
        if (roll < compliantWeight)
            return ReactionType.Compliant;
        if (roll < compliantWeight + fleeWeight)
            return ReactionType.Flee;
        return ReactionType.Resist;
    }

    private static TEnum RandomEnum<TEnum>()
        where TEnum : Enum
    {
        Array values = Enum.GetValues(typeof(TEnum));
        return (TEnum)values.GetValue(Random.Range(0, values.Length));
    }

    // ---- 세력 · 문양 (#222) ----

    // None(무소속·문양 없음)은 위조 대조 축이 될 수 없어 배정에서 제외한다 (#222 (b)).
    // enum에 세력을 추가하면 자동으로 후보에 포함된다 — 여기를 고칠 필요 없음.
    private static readonly OfficialRecords.Faction[] s_assignableFactions = BuildAssignableFactions();

    private static OfficialRecords.Faction[] BuildAssignableFactions()
    {
        var all = (OfficialRecords.Faction[])Enum.GetValues(typeof(OfficialRecords.Faction));
        var list = new List<OfficialRecords.Faction>(all.Length);
        foreach (OfficialRecords.Faction faction in all)
            if (faction != OfficialRecords.Faction.None)
                list.Add(faction);
        return list.ToArray();
    }

    private static OfficialRecords.Faction RandomFaction() =>
        s_assignableFactions.Length > 0
            ? s_assignableFactions[Random.Range(0, s_assignableFactions.Length)]
            : OfficialRecords.Faction.None;

    /// <summary>이번 세션에 이 세력의 진짜 문양 index. 세션 중이면 동기화 값, 오프라인이면 로컬 폴백. (#222)</summary>
    private int RealSymbolIndex(OfficialRecords.Faction faction)
    {
        FactionSymbolManager manager = App.Game.FactionSymbol;
        if (manager != null)
            return manager.RealIndex(faction);

        if (!m_localRealIndices.TryGetValue(faction, out int index))
        {
            int count = m_officialRecords != null ? m_officialRecords.GetVariantsCount(faction) : 0;
            index = count > 0 ? Random.Range(0, count) : 0;
            m_localRealIndices[faction] = index;
        }
        return index;
    }

    /// <summary>진짜를 제외한 나머지 variant 중 하나 — 위조범의 가짜 문양. variant 2개 이상일 때만 호출. (#222)</summary>
    private int PickFakeSymbolIndex(OfficialRecords.Faction faction, int realIndex)
    {
        int count = m_officialRecords.GetVariantsCount(faction);
        int pick = Random.Range(0, count - 1); // 진짜 1개를 뺀 범위에서 뽑고
        return pick >= realIndex ? pick + 1 : pick; // 진짜 자리를 건너뛴다
    }

    // ---- 이름 위조 (#223) ----

    private static readonly char[] s_vowels = { 'a', 'e', 'i', 'o', 'u' };
    private static readonly char[] s_consonants =
        { 'b', 'c', 'd', 'f', 'g', 'h', 'j', 'k', 'l', 'm', 'n', 'p', 'q', 'r', 's', 't', 'v', 'w', 'x', 'y', 'z' };

    /// <summary>
    /// 이름의 알파벳 중 count글자를 같은 종류(모음↔모음, 자음↔자음)의 다른 글자로 치환한다. (#223)
    /// 발음 가능한 자연스러운 이름을 유지한 채 정본과 어긋나게 만든다 — 대소문자 보존, 공백/기호는 건너뛴다.
    /// count가 이름의 알파벳 수보다 크면 가능한 만큼만 오염한다.
    /// </summary>
    private static string CorruptName(string name, int count)
    {
        if (string.IsNullOrEmpty(name) || count <= 0)
            return name;

        // 알파벳 위치만 모아 셔플 → 앞에서 count개 = 중복 없는 오염 위치
        var positions = new List<int>();
        for (int i = 0; i < name.Length; i++)
            if (char.IsLetter(name[i]))
                positions.Add(i);
        if (positions.Count == 0)
            return name;

        for (int i = positions.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (positions[i], positions[j]) = (positions[j], positions[i]);
        }

        int corruptCount = Mathf.Min(count, positions.Count);
        char[] chars = name.ToCharArray();
        for (int k = 0; k < corruptCount; k++)
            chars[positions[k]] = SubstituteSameClass(chars[positions[k]]);

        return new string(chars);
    }

    /// <summary>알파벳 한 글자를 같은 종류(모음↔모음, 자음↔자음)의 다른 글자로 치환한다. 대소문자 보존. (#223)</summary>
    private static char SubstituteSameClass(char original)
    {
        char lower = char.ToLowerInvariant(original);
        char[] pool = Array.IndexOf(s_vowels, lower) >= 0 ? s_vowels : s_consonants;

        char replacement;
        do
        {
            replacement = pool[Random.Range(0, pool.Length)];
        }
        while (replacement == lower);

        return char.IsUpper(original) ? char.ToUpperInvariant(replacement) : replacement;
    }
}
