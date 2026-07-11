using System;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

/// <summary>
/// 스폰 완료 후 모든 NPC에 랜덤 시민 프로필을 채우고, 그중 1명을 실제 범인으로 지정한다. (이슈 #38)
/// 범인 외에 거수자(도주/저항 성향의 무고 시민)를 N명 배정한다 — 잡아 인계하면 경범죄가 된다. (#78, 모델 B)
/// 몽타주 후보군(#74) 안 배치·디코이 수 정교화는 후속 과제 — 지금은 무작위 무고 시민에 배정한다.
/// 범인의 프로필(WantedProfile)이 곧 본부 수배 데이터(#58)의 원본이 된다.
/// </summary>
// TODO: 네트워크 전환(#52/#56) 시 서버에서만 배정하고 결과를 클라이언트에 동기화
public class CriminalAssigner : MonoBehaviour
{
    [Header("스포너 (비우면 씬에서 자동 탐색)")]
    [SerializeField]
    private NpcSpawner m_spawner;

    [Header("공식 기록 (세력 심볼 조회용)")]
    [SerializeField]
    private OfficialRecords m_officialRecords;

    [Header("범인 검거 반응 가중치 (#76)")]
    [Tooltip("합이 1일 필요 없음 — 비율로 추첨한다. 일반 시민은 항상 순응(GDD 6-3)")]
    [SerializeField]
    private float m_compliantWeight = 0.2f;

    [SerializeField]
    private float m_fleeWeight = 0.4f;

    [SerializeField]
    private float m_resistWeight = 0.4f;

    [Header("거수자 (#78)")]
    [Tooltip(
        "범인 외에 도주/저항 성향을 갖는 무고 시민 수. 잡아서 인계하면 경범죄(공무집행방해)가 된다 — 구성 비율 난이도 노브. 범인 제외 인원보다 크면 가능한 만큼만 배정"
    )]
    [SerializeField]
    private int m_suspiciousCitizenCount = 2;

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

    /// <summary>실제 범인으로 지정된 NPC. 배정 전에는 null.</summary>
    public NpcController CriminalNpc { get; private set; }

    /// <summary>범인의 프로필 = 본부 수배 데이터(#58)의 원본.</summary>
    public CitizenProfile WantedProfile { get; private set; }

    /// <summary>배정 완료 이벤트 — 수배 UI(#58)·진범 판정(#41) 등이 구독한다.</summary>
    public event Action<NpcController> OnCriminalAssigned;

    private void Awake()
    {
        if (m_spawner == null)
            m_spawner = FindFirstObjectByType<NpcSpawner>();
    }

    private void Start()
    {
        if (m_spawner == null)
        {
            Debug.LogWarning("CriminalAssigner: NpcSpawner를 찾지 못해 배정 불가", this);
            return;
        }

        // 스포너가 이미 스폰을 끝냈으면 바로 배정, 아니면 완료 이벤트를 기다린다
        if (m_spawner.IsSpawnCompleted)
            AssignAll();
        else
            m_spawner.OnSpawnCompleted += AssignAll;
    }

    private void OnDestroy()
    {
        if (m_spawner != null)
            m_spawner.OnSpawnCompleted -= AssignAll;
    }

    private void AssignAll()
    {
        IReadOnlyList<NpcController> npcs = m_spawner.SpawnedNpcs;
        if (npcs.Count == 0)
        {
            Debug.LogWarning("CriminalAssigner: 스폰된 NPC가 없어 배정 불가", this);
            return;
        }

        int criminalIndex = Random.Range(0, npcs.Count);
        // 범인을 제외한 무고 시민 중 거수자로 만들 인덱스 집합 (도주/저항 성향 부여, #78)
        HashSet<int> suspiciousIndices = PickSuspiciousIndices(
            npcs.Count,
            criminalIndex,
            m_suspiciousCitizenCount
        );
        string[] names = BuildUniqueNames(npcs.Count);

        // 스캔 UI(#39) 전까지는 로그로 배정 결과를 확인한다
        var logBuilder = new System.Text.StringBuilder();
        logBuilder.AppendLine($"시민 프로필 배정 완료 ({npcs.Count}명):");

        for (int i = 0; i < npcs.Count; i++)
        {
            // 프리팹에 CitizenIdentity가 없어도 동작하도록 없으면 붙여준다
            CitizenIdentity identity = npcs[i].GetComponent<CitizenIdentity>();
            if (identity == null)
                identity = npcs[i].gameObject.AddComponent<CitizenIdentity>();

            // 프로필은 에셋이 아닌 런타임 인스턴스 — 라운드마다 새로 배정된다
            CitizenProfile profile = ScriptableObject.CreateInstance<CitizenProfile>();
            profile.Initialize(
                names[i],
                RandomEnum<OfficialRecords.CitizenType>(),
                RandomEnum<OfficialRecords.Faction>(),
                m_officialRecords
            );

            bool isCriminal = i == criminalIndex;
            identity.AssignProfile(profile, isCriminal);

            // 검거 반응 — 범인은 유형 추첨(#76), 거수자는 도주/저항만(#78), 그 외 시민은 순응 (GDD 6-1/6-2/6-3)
            ReactionType reaction;
            if (isCriminal)
                reaction = RollCriminalReaction();
            else if (suspiciousIndices.Contains(i))
                reaction = RollSuspiciousReaction();
            else
                reaction = ReactionType.Compliant;
            identity.AssignReaction(reaction);

            if (isCriminal)
            {
                CriminalNpc = npcs[i];
                WantedProfile = profile;
            }

            // 범인/거수자 표시는 정답이 노출되므로 데모 빌드 전에 제거할 것
            string roleTag = isCriminal ? $"  ← 범인 ({reaction})"
                : suspiciousIndices.Contains(i) ? $"  ← 거수자 ({reaction})"
                : "";
            logBuilder.AppendLine(
                $"  {profile.CitizenName} | {profile.m_typeView} | {profile.m_factionView}{roleTag}"
            );
        }

        OnCriminalAssigned?.Invoke(CriminalNpc);
        Debug.Log(logBuilder.ToString());
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

    /// <summary>범인의 검거 반응 유형을 가중치 비율로 추첨한다. (#76)</summary>
    private ReactionType RollCriminalReaction()
    {
        float total = m_compliantWeight + m_fleeWeight + m_resistWeight;
        if (total <= 0f)
            return ReactionType.Compliant; // 가중치가 전부 0이면 안전하게 순응

        float roll = Random.Range(0f, total);
        if (roll < m_compliantWeight)
            return ReactionType.Compliant;
        if (roll < m_compliantWeight + m_fleeWeight)
            return ReactionType.Flee;
        return ReactionType.Resist;
    }

    /// <summary>거수자의 반응을 추첨한다 — 순응은 제외하고 도주/저항만 (순응이면 거수자로서 의미가 없다). (#78)</summary>
    private ReactionType RollSuspiciousReaction()
    {
        float total = m_fleeWeight + m_resistWeight;
        if (total <= 0f)
            return ReactionType.Flee; // 가중치가 전부 0이면 안전하게 도주
        return Random.Range(0f, total) < m_fleeWeight ? ReactionType.Flee : ReactionType.Resist;
    }

    /// <summary>범인을 제외한 시민 중 거수자로 만들 인덱스를 count명 무작위로 고른다. (#78)</summary>
    private static HashSet<int> PickSuspiciousIndices(int total, int excludeIndex, int count)
    {
        var pool = new List<int>(total);
        for (int i = 0; i < total; i++)
            if (i != excludeIndex)
                pool.Add(i);

        int take = Mathf.Clamp(count, 0, pool.Count);
        // 피셔-예이츠로 앞에서 take개만 확정
        for (int i = 0; i < take; i++)
        {
            int j = Random.Range(i, pool.Count);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }

        var set = new HashSet<int>();
        for (int i = 0; i < take; i++)
            set.Add(pool[i]);
        return set;
    }

    private static TEnum RandomEnum<TEnum>()
        where TEnum : Enum
    {
        Array values = Enum.GetValues(typeof(TEnum));
        return (TEnum)values.GetValue(Random.Range(0, values.Length));
    }
}
