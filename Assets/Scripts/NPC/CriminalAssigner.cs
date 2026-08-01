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

    private readonly List<NpcController> m_criminalNpcs = new List<NpcController>();
    private readonly List<CitizenProfile> m_wantedProfiles = new List<CitizenProfile>();

    // 제보 전화 승격 (#102) — 예비 용의자 명단을 빌려 보고 다음 공개 대상을 판단한다.
    // 구동 주체가 달라 분리했다: 배정은 스폰 완료로 1회, 승격은 전화마다 1명씩.
    private SuspectRevealer m_revealer;

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

    protected override void Awake()
    {
        base.Awake(); // App 등록

        // 명단은 같은 List 인스턴스를 넘긴다 — 배정이 채우면 승격 쪽에서도 그대로 보인다.
        m_revealer = new SuspectRevealer(
            m_criminalNpcs,
            m_compliantWeight,
            m_fleeWeight,
            m_resistWeight,
            m_criminalBountyMin,
            m_criminalBountyMax
        );
    }

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

        // 이름 풀은 인원수만큼 한 번에 확정된다 — 라운드 안에서 중복이 없어야 하므로 매 라운드 새로 만든다
        var factory = new CitizenProfileFactory(m_officialRecords, npcs.Count);

        m_criminalNpcs.Clear();
        m_wantedProfiles.Clear();
        m_totalAssignedBounty = 0;

        // 스캔 UI(#39) 전까지 배정 결과를 콘솔로 확인한다 — 정답이 노출되므로 데모 빌드 전 제거 대상.
        // 지우려면 이 줄과 아래 log.Add / log.Flush만 걷어내면 된다 (AssignmentLog 참고).
        var log = new AssignmentLog(npcs.Count, suspectCount, revealCount);

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

            CitizenProfile profile = factory.Create(i);

            // 위조범: 표시값을 정본/인명부와 어긋나게 한다 — 이름·문양 중 하나만 오염된다 (#222 (a)①).
            // 어느 축인지는 팩토리가 정하고(문양 variant가 모자라면 이름으로 폴백), 여기서는 결과만 받는다.
            bool isForger = forgerIndices.Contains(i);
            bool forgedSymbol = isForger && factory.ApplyForgery(profile, m_forgedCharCount);

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
                ? ReactionRoll.Roll(m_compliantWeight, m_fleeWeight, m_resistWeight)
                : ReactionRoll.Roll(m_citizenCompliantWeight, m_citizenFleeWeight, m_citizenResistWeight);
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

            log.Add(identity, isSuspect, isForger, forgedSymbol, factory.RealSymbolIndex(profile.Faction));
        }

        OnCriminalAssigned?.Invoke(m_criminalNpcs);
        log.Flush(m_totalAssignedBounty);
    }

    // ---- 제보 전화 승격 (#102) ----
    //
    // 배정 자체가 서버(또는 오프라인)에서만 일어나므로 승격도 같은 쪽에서만 의미가 있다.
    // 호출자(TipCallPhone)가 서버 권위를 게이트한다 — 여기서 다시 막지 않는다.

    /// <summary>
    /// 아직 공개되지 않은 예비 용의자가 남아 있는가 — 전화를 계속 걸지의 기준. (#102)
    /// 판단은 <see cref="SuspectRevealer"/>가 한다.
    /// </summary>
    public bool HasPendingSuspect => m_revealer != null && m_revealer.HasPending;

    /// <summary>
    /// 대기 중인 예비 용의자 1명을 수배로 공개한다 — 제보 전화(TipCallPhone)가 호출한다. (#102)
    /// 성공하면 true. 승격 자체는 <see cref="SuspectRevealer"/>가 하고, 여기서는 현상금 총합만
    /// 반영한다 — 배정과 승격이 함께 더하는 값이라 소유자를 하나로 둔다 (#395).
    /// </summary>
    public bool PromoteNext()
    {
        if (m_revealer == null || !m_revealer.TryPromoteNext(out int bountyDelta))
            return false;

        m_totalAssignedBounty += bountyDelta;
        return true;
    }

    /// <summary>0~total-1 인덱스를 셔플해 앞에서 count개를 뽑는다 — 중복 없는 진범 인덱스. (#127)</summary>
    private static HashSet<int> PickCriminalIndices(int total, int count)
    {
        // 인덱스 배열 피셔-예이츠 셔플 — 이름 풀(CitizenProfileFactory)과 같은 방식
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
}
