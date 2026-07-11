using System;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

/// <summary>
/// 범인 확정 후 전 NPC에 외형 특징 조합을 배정한다 — 디코이 보장 배치. (#74)
/// 범인 외형 확정 → 공개 축 선택 → 몽타주에 부합하는 NPC가 정확히 k명(범인 포함)이 되도록
/// 디코이 k−1명에게 공개 특징 일치 조합을 주고, 나머지는 공개 특징 중 최소 1개가 다르게 배정한다.
/// k값으로 난이도를 조절한다 (GDD 6-5 분포=난이도).
/// 배정은 서버 권위 — NpcAppearance.SetProfile이 인덱스만 전 클라이언트에 동기화한다 (#56).
/// 몽타주 텍스트(GDD 10-3 글 방식)는 범인 프로필에서 자동 생성 — 본부 수배 UI(#58)의 원본.
/// </summary>
public class AppearanceAssigner : MonoBehaviour
{
    [Header("범인 배정기 (비우면 씬에서 자동 탐색)")]
    [SerializeField] private CriminalAssigner m_criminalAssigner;

    [Header("스포너 (비우면 씬에서 자동 탐색)")]
    [SerializeField] private NpcSpawner m_spawner;

    [Header("외형 축별 옵션 정의")]
    [SerializeField] private AppearanceDatabase m_appearanceDatabase;

    [Header("몽타주 공개 특징 수")]
    [Tooltip("범인 외형 축 중 몽타주로 공개할 축 수 (기획 2~3개)")]
    [Range(1, AppearanceProfile.k_axisCount)]
    [SerializeField] private int m_revealedAxisCount = 2;

    [Header("몽타주 부합 인원 k (범인 포함)")]
    [Tooltip("몽타주 공개 특징에 부합하는 NPC 수 — 범인 1명 + 디코이 k−1명. 클수록 스캔 검증 부담이 커진다 (난이도)")]
    [SerializeField] private int m_montageMatchCount = 3;

    private readonly List<AppearanceAxis> m_revealedAxes = new List<AppearanceAxis>();

    /// <summary>몽타주로 공개된 특징 축들. 배정 전에는 비어 있다.</summary>
    public IReadOnlyList<AppearanceAxis> RevealedAxes => m_revealedAxes;

    /// <summary>범인의 외형 특징 조합. 배정 전에는 Unassigned.</summary>
    public AppearanceProfile CriminalProfile { get; private set; } = AppearanceProfile.Unassigned;

    /// <summary>글 방식 몽타주 텍스트 (GDD 10-3) — 본부 수배 UI(#58)가 그대로 표시한다.</summary>
    public string MontageText { get; private set; } = string.Empty;

    /// <summary>몽타주 생성 완료 이벤트 — 수배 UI(#58)가 구독한다.</summary>
    public event Action<string> OnMontageGenerated;

    private void Awake()
    {
        if (m_criminalAssigner == null)
            m_criminalAssigner = FindFirstObjectByType<CriminalAssigner>();
        if (m_spawner == null)
            m_spawner = FindFirstObjectByType<NpcSpawner>();

        // 범인 확정 이후에 배정해야 디코이 기준(범인)이 존재한다 — Start보다 먼저 구독해 이벤트를 놓치지 않는다
        if (m_criminalAssigner != null)
            m_criminalAssigner.OnCriminalAssigned += AssignAll;
    }

    private void Start()
    {
        if (m_criminalAssigner == null)
        {
            Debug.LogWarning("AppearanceAssigner: CriminalAssigner를 찾지 못해 외형 배정 불가", this);
            return;
        }

        // 이 컴포넌트가 늦게 활성화돼 배정 이벤트를 이미 놓친 경우 보정
        if (m_criminalAssigner.CriminalNpc != null && !CriminalProfile.IsAssigned)
            AssignAll(m_criminalAssigner.CriminalNpc);
    }

    private void OnDestroy()
    {
        if (m_criminalAssigner != null)
            m_criminalAssigner.OnCriminalAssigned -= AssignAll;
    }

    private void AssignAll(NpcController criminalNpc)
    {
        if (m_appearanceDatabase == null)
        {
            Debug.LogWarning("AppearanceAssigner: AppearanceDatabase가 지정되지 않아 외형 배정 불가", this);
            return;
        }
        if (m_spawner == null)
        {
            Debug.LogWarning("AppearanceAssigner: NpcSpawner를 찾지 못해 외형 배정 불가", this);
            return;
        }

        IReadOnlyList<NpcController> npcs = m_spawner.SpawnedNpcs;
        if (npcs.Count == 0)
            return;

        // 1. 범인 외형 확정 + 몽타주로 공개할 축 선택
        CriminalProfile = m_appearanceDatabase.CreateRandomProfile();
        PickRevealedAxes();

        // 공개 축이 전부 옵션 1개 이하면 모든 NPC가 몽타주에 부합해 필터링이 성립하지 않는다
        if (!HasMutableRevealedAxis())
            Debug.LogWarning("AppearanceAssigner: 공개 축에 옵션이 2개 이상인 축이 없음 — 비부합 NPC를 만들 수 없다. AppearanceDatabase 옵션을 확인할 것", this);

        // 2. 범인을 제외한 NPC 중 디코이 k−1명을 랜덤 선정
        HashSet<NpcController> decoys = PickDecoys(npcs, criminalNpc);

        // 3. 배정 — 범인·디코이는 공개 특징 일치, 나머지는 최소 1개 공개 특징이 다르게
        var logBuilder = new System.Text.StringBuilder();
        foreach (NpcController npc in npcs)
        {
            AppearanceProfile profile;
            string role;
            if (npc == criminalNpc)
            {
                profile = CriminalProfile;
                role = "  ← 범인";
            }
            else if (decoys.Contains(npc))
            {
                profile = CreateDecoyProfile();
                role = "  ← 디코이";
            }
            else
            {
                profile = CreateNonMatchingProfile();
                role = string.Empty;
            }

            ApplyToNpc(npc, profile);
            logBuilder.AppendLine($"  {DescribeProfile(profile)}{role}");
        }

        // 4. 몽타주 텍스트 생성 — 본부 수배 UI(#58)의 원본
        MontageText = m_appearanceDatabase.BuildMontageText(CriminalProfile, m_revealedAxes);
        OnMontageGenerated?.Invoke(MontageText);

        // 수배 UI(#58) 전까지는 로그로 배치 결과를 확인한다 — 정답이 노출되므로 데모 빌드 전에 제거할 것
        Debug.Log($"외형 배정 완료 ({npcs.Count}명) | 몽타주: \"{MontageText}\" | 부합 목표 {Mathf.Min(m_montageMatchCount, npcs.Count)}명\n{logBuilder}");
    }

    /// <summary>축 전체를 셔플해 앞에서 공개 수만큼 고른다.</summary>
    private void PickRevealedAxes()
    {
        m_revealedAxes.Clear();
        for (int i = 0; i < AppearanceProfile.k_axisCount; i++)
            m_revealedAxes.Add((AppearanceAxis)i);

        for (int i = m_revealedAxes.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (m_revealedAxes[i], m_revealedAxes[j]) = (m_revealedAxes[j], m_revealedAxes[i]);
        }

        int count = Mathf.Clamp(m_revealedAxisCount, 1, m_revealedAxes.Count);
        m_revealedAxes.RemoveRange(count, m_revealedAxes.Count - count);
    }

    /// <summary>범인을 제외한 NPC를 셔플해 앞에서 k−1명을 디코이로 고른다.</summary>
    private HashSet<NpcController> PickDecoys(IReadOnlyList<NpcController> npcs, NpcController criminalNpc)
    {
        var candidates = new List<NpcController>(npcs.Count);
        foreach (NpcController npc in npcs)
        {
            if (npc != criminalNpc)
                candidates.Add(npc);
        }

        for (int i = candidates.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
        }

        int decoyCount = Mathf.Clamp(m_montageMatchCount - 1, 0, candidates.Count);
        var decoys = new HashSet<NpcController>();
        for (int i = 0; i < decoyCount; i++)
            decoys.Add(candidates[i]);
        return decoys;
    }

    /// <summary>공개 축은 범인과 같게, 비공개 축은 랜덤인 디코이 프로필을 만든다.</summary>
    private AppearanceProfile CreateDecoyProfile()
    {
        AppearanceProfile profile = m_appearanceDatabase.CreateRandomProfile();
        foreach (AppearanceAxis axis in m_revealedAxes)
            profile.SetIndex(axis, CriminalProfile.GetIndex(axis));
        return profile;
    }

    /// <summary>공개 특징 중 최소 1개가 범인과 다른 프로필을 만든다 — 몽타주 비부합 보장.</summary>
    private AppearanceProfile CreateNonMatchingProfile()
    {
        AppearanceProfile profile = m_appearanceDatabase.CreateRandomProfile();
        if (!profile.MatchesOn(CriminalProfile, m_revealedAxes))
            return profile;

        // 우연히 공개 축이 전부 일치하면 옵션이 2개 이상인 공개 축 하나를 강제로 바꾼다
        var mutableAxes = new List<AppearanceAxis>();
        foreach (AppearanceAxis axis in m_revealedAxes)
        {
            if (m_appearanceDatabase.GetOptionCount(axis) >= 2)
                mutableAxes.Add(axis);
        }
        if (mutableAxes.Count == 0)
            return profile; // 바꿀 수 있는 축이 없음 — AssignAll에서 이미 경고함

        AppearanceAxis target = mutableAxes[Random.Range(0, mutableAxes.Count)];
        int optionCount = m_appearanceDatabase.GetOptionCount(target);
        // 현재 값에 1 이상을 더해 모듈러 — 범인과 같은 값은 절대 나오지 않는다
        int changed = (CriminalProfile.GetIndex(target) + Random.Range(1, optionCount)) % optionCount;
        profile.SetIndex(target, changed);
        return profile;
    }

    /// <summary>신원(판정 기준)과 외형(시각·동기화) 양쪽에 프로필을 배정한다.</summary>
    private void ApplyToNpc(NpcController npc, AppearanceProfile profile)
    {
        // 몽타주 부합 판정(#39 스캔 검증 등)은 CitizenIdentity.Appearance를 기준으로 한다
        CitizenIdentity identity = npc.GetComponent<CitizenIdentity>();
        if (identity != null)
            identity.AssignAppearance(profile);

        NpcAppearance appearance = npc.GetComponent<NpcAppearance>();
        if (appearance != null)
            appearance.SetProfile(profile); // 서버 권위 — 인덱스만 전 클라이언트에 동기화 (#56)
        else
            Debug.LogWarning($"AppearanceAssigner: {npc.name}에 NpcAppearance가 없어 시각 적용 생략", npc);
    }

    /// <summary>공개 축에 옵션이 2개 이상인 축이 하나라도 있는지 — 비부합 프로필 생성 가능 여부.</summary>
    private bool HasMutableRevealedAxis()
    {
        foreach (AppearanceAxis axis in m_revealedAxes)
        {
            if (m_appearanceDatabase.GetOptionCount(axis) >= 2)
                return true;
        }
        return false;
    }

    // 배정 결과 로그용 — 전 축의 표시 이름을 나열한다
    private string DescribeProfile(in AppearanceProfile profile)
    {
        var builder = new System.Text.StringBuilder();
        for (int i = 0; i < AppearanceProfile.k_axisCount; i++)
        {
            var axis = (AppearanceAxis)i;
            AppearanceDatabase.AppearanceOption option = m_appearanceDatabase.GetOption(axis, profile.GetIndex(axis));
            if (builder.Length > 0)
                builder.Append(" / ");
            builder.Append(option != null ? option.DisplayName : "?");
        }
        return builder.ToString();
    }
}
