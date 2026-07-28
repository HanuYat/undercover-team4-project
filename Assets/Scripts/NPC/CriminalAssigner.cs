using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;
using Random = UnityEngine.Random;

/// <summary>
/// 스폰 완료 후 모든 NPC에 랜덤 시민 프로필을 채우고, 인스펙터에서 지정한 수만큼 예비 용의자로 지정한다. (이슈 #38/#127)
/// 예비 용의자는 라운드 시작에 전원 확정되지만 앞 N명만 수배로 공개되고, 나머지는 제보 전화로 1명씩 공개된다 (#102).
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

    [Header("수배 용의자 (#127 · #102)")]
    [Tooltip("이번 라운드의 예비 용의자 풀 크기 = 최대 수배 수. 라운드 시작에 전원 확정되지만 공개는 나눠서 된다(제보 전화). NPC 수보다 크면 NPC 수로 잘라 배정한다(경고 로그)")]
    [Min(1)]
    [FormerlySerializedAs("m_criminalCount")] // 씬에 저장된 기존 값 보존 — 의미가 '진범 수'에서 '예비 풀 크기'로 바뀌었다 (#102)
    [SerializeField]
    private int m_maxWantedCount = 3;

    [Tooltip("라운드 시작에 이미 수배로 공개된 용의자 수. 나머지는 미공개로 대기하다가 제보 전화를 받을 때마다 1명씩 공개된다 (#102). 0이면 수배 없이 시작한다")]
    [Min(0)]
    [SerializeField]
    private int m_initialRevealCount = 1;

    [Header("위조범 (#223)")]
    [Tooltip("표시 이름을 오염시킬 위조범 수. 진범과 독립 배정된다")]
    [Min(0)]
    [SerializeField]
    private int m_forgerCount = 2;

    [Tooltip("위조범 1명의 표시 이름에서 오염시킬 글자 수(모음↔모음·자음↔자음 치환). 이름 글자 수보다 크면 잘린다")]
    [Min(1)]
    [SerializeField]
    private int m_forgedCharCount = 1;

    [Header("현상금 (#395)")]
    [Tooltip("진범 1명의 현상금 하한. 라운드 시작 배정 시점에 [하한, 상한]에서 100원 단위로 뽑아 확정한다 — 판정 시점에 뽑으면 재검거 리롤이 가능해진다")]
    [Min(0)]
    [SerializeField]
    private int m_criminalBountyMin = 8000;

    [Tooltip("진범 1명의 현상금 상한")]
    [Min(0)]
    [SerializeField]
    private int m_criminalBountyMax = 15000;

    [Tooltip("위조범 1명의 현상금 하한 (경범죄 취급 — GDD 9-1 기본 1,000 주변)")]
    [Min(0)]
    [SerializeField]
    private int m_forgeryBountyMin = 500;

    [Tooltip("위조범 1명의 현상금 상한")]
    [Min(0)]
    [SerializeField]
    private int m_forgeryBountyMax = 2000;

    [Header("예비 용의자 반응 가중치 (#76 · 트리거 변경 #400)")]
    [Tooltip("합이 1일 필요 없음 — 비율로 추첨한다. 스캔·피격당할 때 이 유형대로 반응한다 (#400). 범인은 도주/저항 성향이 높다")]
    [SerializeField]
    private float m_compliantWeight = 0.2f;

    [SerializeField]
    private float m_fleeWeight = 0.4f;

    [SerializeField]
    private float m_resistWeight = 0.4f;

    [Header("일반 시민 반응 가중치 (#78 · 재조정 #400)")]
    [Tooltip(
        "무고 시민의 반응 추첨 비율. 도주/저항은 진범을 헷갈리게 하는 미끼일 뿐 잡아도 오검거다 — 이 비율로 미끼 행동의 빈도(난이도)를 조절한다. 반응 트리거가 스캔으로 옮겨지면서(#400) 반응이 진범 tell이 될 위험이 커져 비순응 비율을 절반까지 올렸다 (GDD 6-2)"
    )]
    [SerializeField]
    private float m_citizenCompliantWeight = 0.5f;

    [SerializeField]
    private float m_citizenFleeWeight = 0.25f;

    [SerializeField]
    private float m_citizenResistWeight = 0.25f;

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

    private int m_totalAssignedBounty;

    /// <summary>
    /// 이번 라운드에 배정된 현상금 총합 (#395) — 진범 + 위조범. 배정 전에는 0.
    /// 라운드 목표 금액이 달성 가능한지 대조하는 기준이다(RoundManager). 돌발 이벤트로 나중에 스폰되는
    /// 난동꾼의 수익은 여기에 포함되지 않는다 — 배정 시점엔 존재하지 않기 때문이다.
    /// </summary>
    public int TotalAssignedBounty => m_totalAssignedBounty;

    /// <summary>
    /// 이번 라운드의 예비 용의자 전원 — 공개된 수배와 미공개 대기분을 모두 포함한다. 배정 전에는 비어 있다. (#127 · #102)
    /// 공개 여부는 각 NPC의 <see cref="CitizenIdentity.IsCriminal"/>이 가른다 — 이 목록에 있다고 진범인 것은 아니다.
    /// </summary>
    public IReadOnlyList<NpcController> CriminalNpcs => m_criminalNpcs;

    /// <summary>예비 용의자 전원의 프로필 = 본부 수배 데이터(#58)의 원본. CriminalNpcs와 같은 순서.</summary>
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

        // 예비 풀 크기는 NPC 수를 넘을 수 없다 — 초과 지정 시 잘라서 배정한다 (#127)
        int suspectCount = Mathf.Clamp(m_maxWantedCount, 1, npcs.Count);
        if (m_maxWantedCount > npcs.Count)
            Debug.LogWarning(
                $"CriminalAssigner: 최대 수배 수({m_maxWantedCount})가 NPC 수({npcs.Count})보다 많아 {suspectCount}명으로 잘라 배정한다",
                this
            );

        // 최초 공개 수는 풀 크기를 넘을 수 없다 — 넘으면 전원 공개(= 제보 전화가 아무것도 안 하는 구성)
        int revealCount = Mathf.Clamp(m_initialRevealCount, 0, suspectCount);

        HashSet<int> criminalIndices = PickCriminalIndices(npcs.Count, suspectCount);
        // 위조범은 진범과 독립적으로 추첨한다 — 겹칠 수도 있다(범인이 위조 papers 소지) (#223)
        HashSet<int> forgerIndices = PickCriminalIndices(npcs.Count, Mathf.Clamp(m_forgerCount, 0, npcs.Count));
        string[] names = BuildUniqueNames(npcs.Count);

        m_criminalNpcs.Clear();
        m_wantedProfiles.Clear();
        m_totalAssignedBounty = 0;

        // 스캔 UI(#39) 전까지는 로그로 배정 결과를 확인한다
        var logBuilder = new System.Text.StringBuilder();
        logBuilder.AppendLine($"시민 프로필 배정 완료 ({npcs.Count}명, 예비 용의자 {suspectCount}명 중 {revealCount}명 공개):");

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

            // 예비 풀은 라운드 시작에 전부 확정하고 공개만 나눈다 — 전화 시점에 몽타주를 역생성하면
            // 부합 인원 수를 통제할 수 없어 디코이 설계가 깨진다 (#102 설계 결정 1)
            bool isSuspect = criminalIndices.Contains(i);
            // m_criminalNpcs에 담기기 전에 세므로 이 비교가 곧 '앞 revealCount명'이다
            bool isCriminal = isSuspect && m_criminalNpcs.Count < revealCount;
            identity.AssignProfile(profile, isCriminal);
            // 위조 여부를 신원에 기록 — 위조 검거 판정(#320)이 읽는다. 이름 오염(m_nameView)과 별개의 서버 전용 플래그.
            identity.AssignForgery(isForger);

            // 검거 반응 — 범인은 범인 가중치로, 무고 시민은 시민 가중치로 추첨한다.
            // 시민의 도주/저항은 진범을 헷갈리게 하는 미끼 행동일 뿐 판정엔 영향이 없다 (GDD 6-1/6-3, #76/#78)
            ReactionType reaction = isCriminal
                ? RollReaction(m_compliantWeight, m_fleeWeight, m_resistWeight)
                : RollReaction(m_citizenCompliantWeight, m_citizenFleeWeight, m_citizenResistWeight);
            identity.AssignReaction(reaction);

            // 현상금 확정 (#395) — 판정 시점이 아니라 여기서 뽑는다. ArrestJudge의 판정 우선순위와 같은
            // 순서로 정한다(진범 > 위조범 > 무고). 무고 시민은 오검거라 0원이다.
            // 기준은 isSuspect가 아니라 isCriminal(지금 공개된 수배)이다 — 미공개 예비 용의자를 잡으면
            // 오검거이거나(0원) 위조 검거라, 그 시점의 판정과 금액이 맞아야 한다. 승격되어 진범이 되는
            // 순간의 현상금은 PromoteNext가 다시 배정한다 (#102 · #395).
            int bounty = isCriminal ? BountyRoll.Roll(m_criminalBountyMin, m_criminalBountyMax)
                : isForger ? BountyRoll.Roll(m_forgeryBountyMin, m_forgeryBountyMax)
                : 0;
            identity.AssignBounty(bounty);
            m_totalAssignedBounty += bounty;

            // 미공개 예비 용의자도 목록에 담는다 — AppearanceAssigner가 이 목록으로 디코이와 몽타주를
            // 만들고, 승격(PromoteNext)도 여기서 다음 대상을 찾는다. 공개 여부는 IsCriminal이 가른다 (#102)
            if (isSuspect)
            {
                m_criminalNpcs.Add(npcs[i]);
                m_wantedProfiles.Add(profile);
            }

            // 범인 표시는 정답이 노출되므로 데모 빌드 전에 제거할 것. 반응은 미끼 행동 확인용으로 함께 로그
            string roleTag = isCriminal ? $"  ← 수배 공개 ({reaction})"
                : isSuspect ? $"  ← 예비 용의자 · 미공개 ({reaction})"
                : reaction != ReactionType.Compliant ? $"  (미끼: {reaction})"
                : "";

            // 위조 시 어느 축이 오염됐는지 함께 남겨 대조 확인에 쓴다 (데모 빌드 전 제거 대상)
            string forgeryTag = !isForger ? ""
                : forgedSymbol ? $"  [위조: 문양 {realIndex}→{profile.m_symbolIndexView}]"
                : $"  [위조: {profile.CitizenName}→{profile.m_nameView}]";

            string bountyTag = bounty > 0 ? $"  [현상금 {bounty}원]" : "";

            logBuilder.AppendLine(
                $"  {profile.CitizenName} | {profile.m_typeView} | {profile.m_factionView}{roleTag}{forgeryTag}{bountyTag}"
            );
        }

        logBuilder.AppendLine($"  → 배정 현상금 총합 {m_totalAssignedBounty}원 (돌발 이벤트 수익 별도)");

        OnCriminalAssigned?.Invoke(m_criminalNpcs);
        Debug.Log(logBuilder.ToString());
    }

    // ---- 제보 전화 승격 (#102) ----
    //
    // 배정 자체가 서버(또는 오프라인)에서만 일어나므로 승격도 같은 쪽에서만 의미가 있다.
    // 호출자(TipCallPhone)가 서버 권위를 게이트한다 — 여기서 다시 막지 않는다.

    /// <summary>
    /// 아직 공개되지 않은 예비 용의자가 남아 있는가 — 전화를 계속 걸지의 기준 (#102 설계 결정 5).
    /// 연행·끌기 중인 대상도 '남아 있다'로 센다: 곧 판정되면 IsDelivered로 자동으로 빠지고,
    /// 석방되면 다시 승격 후보가 된다. 여기서 빼면 마지막 예비 용의자를 끌고 가는 동안 울린
    /// 전화 하나 때문에 그 라운드 전화가 영영 끊긴다.
    /// </summary>
    public bool HasPendingSuspect
    {
        get
        {
            for (int i = 0; i < m_criminalNpcs.Count; i++)
                if (IsPending(m_criminalNpcs[i]))
                    return true;
            return false;
        }
    }

    /// <summary>
    /// 지금 수배로 공개된 용의자 수 — 라운드 시작 직후엔 초기 공개 수이고, 제보 전화 승격마다 늘어난다. (#102)
    /// 라운드 시작 할당량이 달성 가능한지 대조하는 기준이다(RoundManager) — 미공개 예비 용의자는 수배
    /// 리스트에 없어 잡을 대상으로 인식되지 않으므로(잡으면 오검거) 시작 할당량에 셀 수 없다.
    /// </summary>
    public int RevealedCount
    {
        get
        {
            int count = 0;
            for (int i = 0; i < m_criminalNpcs.Count; i++)
            {
                NpcController npc = m_criminalNpcs[i];
                if (npc == null)
                    continue;

                CitizenIdentity identity = npc.GetComponent<CitizenIdentity>();
                if (identity != null && identity.IsCriminal)
                    count++;
            }
            return count;
        }
    }

    /// <summary>
    /// 대기 중인 예비 용의자 1명을 수배로 공개한다 — 제보 전화를 받았을 때 호출한다. (#102)
    /// 성공하면 true. 지금 승격 가능한 대상이 없으면 false — 그 전화 한 번을 놓친 것일 뿐이므로
    /// 호출자는 다음 수신을 그대로 예약하면 된다 (풀 소진 판정은 HasPendingSuspect로 따로 한다).
    /// </summary>
    public bool PromoteNext()
    {
        AppearanceAssigner appearance = App.Game.Appearance;
        if (appearance == null)
        {
            // 몽타주를 발행하지 못하면 수배 리스트에 뜨지 않는다 — IsCriminal만 켜면
            // 아무도 모르는 진범이 생기므로, 켜기 전에 막는다
            Debug.LogWarning("CriminalAssigner: AppearanceAssigner를 찾지 못해 승격할 수 없다", this);
            return false;
        }

        NpcController npc = FindNextPromotable();
        if (npc == null)
            return false;

        CitizenIdentity identity = npc.GetComponent<CitizenIdentity>();
        identity.SetCriminal(true);

        // 오검거로 이미 한 번 판정된 대상일 수 있다 — 표식을 지워야 다시 잡아 인계했을 때
        // '첫 인계'로 잡혀 검거 수가 정상 누적된다(IsFirstDelivery, #358). 탈옥 재검거(#231)가
        // ClearDelivered를 부르는 것과 같은 이유다.
        npc.ClearDelivered();

        // 예비 용의자는 시민 가중치로 뽑혀 있다 — 범인 가중치로 다시 뽑는다.
        // Reaction은 서버 전용이라 바꿔도 플레이어에게 티가 나지 않는다 (#102 설계 결정 6).
        // 이미 반응 중이면 유형만 바뀌고 진행 중인 반응은 유지된다 — CanStartReaction이 막는다 (#400)
        identity.AssignReaction(RollReaction(m_compliantWeight, m_fleeWeight, m_resistWeight));

        // 현상금도 진범 몫으로 다시 배정한다 (#395) — 대기 중에는 오검거/위조 기준 금액이 들어 있어,
        // 그대로 두면 승격된 진범이 0원이나 소액으로 잡힌다. 재검거 리롤은 생기지 않는다: 승격 대상은
        // IsCriminal이 꺼진 개체뿐이라(FindNextPromotable) 한 번 승격된 NPC는 다시 이 경로를 타지 않는다.
        int promotedBounty = BountyRoll.Roll(m_criminalBountyMin, m_criminalBountyMax);
        m_totalAssignedBounty += promotedBounty - identity.Bounty; // 대기 시 금액을 빼고 새 금액을 더한다
        identity.AssignBounty(promotedBounty);

        // 라운드 시작에 보관해 둔 몽타주를 그대로 발행한다 — WantedListManager가 이 이벤트로
        // NetworkList 추가와 TotalWanted++ 를 한다(기존 경로 재사용)
        appearance.RevealMontage(npc);

        CitizenProfile profile = identity.Profile;
        Debug.Log($"[제보 전화] 수배 공개: {(profile != null ? profile.CitizenName : npc.name)} ({identity.Reaction}, 현상금 {promotedBounty}원)");
        return true;
    }

    /// <summary>미공개 예비 용의자인가 — 살아 있고, 아직 공개 전이고, 지금 잡을 수 있다. (#102 · #392)</summary>
    private static bool IsPending(NpcController npc)
    {
        // 디스폰·파괴된 대상은 Unity null로 잡힌다 — IsSpawned는 오프라인에서 항상 false라 쓸 수 없다
        if (npc == null)
            return false;

        CitizenIdentity identity = npc.GetComponent<CitizenIdentity>();
        if (identity == null || identity.IsCriminal)
            return false;

        // 유치장에 수감된 대상만 뺀다 — 미공개 상태에서 위조범으로 판정돼 갇힌 개체다. 수배로 올려도
        // 본부 안에 있어 찾을 것이 없고, 이미 위조 현상금으로 정산에 계상돼 있다.
        //
        // 오검거로 판정된 대상은 뺐다가 되살렸다 (#392). 예전에는 IsDelivered를 영구 제외했는데
        // 그 근거("판정이 끝난 대상은 아무도 못 잡는 유령 항목이 된다", #230)가 더 이상 맞지 않는다:
        // 오검거당한 시민은 죽지 않고 원한 구역(Detained)에서 대기하다 추격대(Chasing)로 나가며,
        // 재판정도 허용된다(#358 — 다시 끌어와 인계하면 판정된다). 즉 잡을 수 있는 대상인데 승격만
        // 막고 있었고, 그 탓에 미공개 용의자를 오검거로 태울 때마다 풀이 영구히 줄어 제보 전화가
        // 조용히 죽었다.
        return npc.CurrentState != NpcState.Jailed;
    }

    /// <summary>지금 당장 승격시킬 수 있는 첫 후보. 없으면 null. (#102 설계 §3 가드)</summary>
    private NpcController FindNextPromotable()
    {
        for (int i = 0; i < m_criminalNpcs.Count; i++)
        {
            NpcController npc = m_criminalNpcs[i];
            if (!IsPending(npc))
                continue;

            // 연행·끌기 중 — 곧 판정될 대상이라 등록 직후 사라진다. 건너뛰되 풀 소진으로는 세지 않는다
            // (FindEscorterOf는 연행과 밧줄 끌기를 둘 다 본다, #269)
            if (PlayerEscorter.FindEscorterOf(npc) != null)
                continue;

            return npc;
        }
        return null;
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
