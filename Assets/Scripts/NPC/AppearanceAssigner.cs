using System;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

/// <summary>
/// 범인 확정 후 전 NPC에 외형 특징 조합을 배정한다 — 디코이 보장 배치. (#74)
/// 범인별 외형 확정 → 공개 축 선택(전 범인 공통) → 각 몽타주에 부합하는 NPC가 정확히 k명(해당 범인 포함)이
/// 되도록 범인마다 디코이 k−1명에게 공개 특징 일치 조합을 주고, 나머지는 모든 범인과 공개 특징이
/// 최소 1개 다르게 배정한다. k값으로 난이도를 조절한다 (GDD 6-5 분포=난이도).
/// 진범이 여러 명이면(#127) 몽타주도 범인 수만큼 생성되고, OnMontageGenerated가 범인마다 발행된다.
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
    [Tooltip("범인 외형 축 중 몽타주로 공개할 축 수 (기획 2~3개). 진범이 여러 명이어도 공개 축은 공통이다")]
    [Range(1, AppearanceProfile.k_axisCount)]
    [SerializeField] private int m_revealedAxisCount = 2;

    [Header("몽타주 부합 인원 k (범인 포함)")]
    [Tooltip("몽타주 1건당 공개 특징에 부합하는 NPC 수 — 범인 1명 + 디코이 k−1명. 클수록 스캔 검증 부담이 커진다 (난이도)")]
    [SerializeField] private int m_montageMatchCount = 3;

    private readonly List<AppearanceAxis> m_revealedAxes = new List<AppearanceAxis>();
    private readonly List<AppearanceProfile> m_criminalProfiles = new List<AppearanceProfile>();
    private readonly List<string> m_montageTexts = new List<string>();

    /// <summary>몽타주로 공개된 특징 축들. 배정 전에는 비어 있다. 전 범인 공통.</summary>
    public IReadOnlyList<AppearanceAxis> RevealedAxes => m_revealedAxes;

    /// <summary>범인별 외형 특징 조합 — CriminalAssigner.CriminalNpcs와 같은 순서. 배정 전에는 비어 있다. (#127)</summary>
    public IReadOnlyList<AppearanceProfile> CriminalProfiles => m_criminalProfiles;

    /// <summary>범인별 글 방식 몽타주 텍스트 (GDD 10-3) — CriminalNpcs와 같은 순서. 본부 수배 UI(#58)가 그대로 표시한다.</summary>
    public IReadOnlyList<string> MontageTexts => m_montageTexts;

    /// <summary>몽타주 생성 완료 이벤트 — 수배 UI(#58)가 구독한다. 범인마다 한 번씩 (해당 범인, 몽타주 텍스트)로 발행된다. (#127)</summary>
    public event Action<NpcController, string> OnMontageGenerated;

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
        if (m_criminalAssigner.CriminalNpcs.Count > 0 && m_criminalProfiles.Count == 0)
            AssignAll(m_criminalAssigner.CriminalNpcs);
    }

    private void OnDestroy()
    {
        if (m_criminalAssigner != null)
            m_criminalAssigner.OnCriminalAssigned -= AssignAll;
    }

    private void AssignAll(IReadOnlyList<NpcController> criminals)
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
        if (npcs.Count == 0 || criminals.Count == 0)
            return;

        // 1. 범인별 외형 확정 + 몽타주로 공개할 축 선택 (공개 축은 전 범인 공통 — 몽타주 포맷 통일)
        m_criminalProfiles.Clear();
        m_montageTexts.Clear();
        for (int i = 0; i < criminals.Count; i++)
            m_criminalProfiles.Add(m_appearanceDatabase.CreateRandomProfile());
        PickRevealedAxes();

        // 공개 축이 전부 옵션 1개 이하면 모든 NPC가 몽타주에 부합해 필터링이 성립하지 않는다
        if (!HasMutableRevealedAxis())
            Debug.LogWarning("AppearanceAssigner: 공개 축에 옵션이 2개 이상인 축이 없음 — 비부합 NPC를 만들 수 없다. AppearanceDatabase 옵션을 확인할 것", this);

        // 2. 범인을 제외한 NPC 중 범인마다 디코이 k−1명을 겹치지 않게 랜덤 선정
        Dictionary<NpcController, int> decoyOwners = PickDecoys(npcs, criminals);

        // 범인 → 인덱스 역조회 — 배정 루프에서 자기 프로필을 찾기 위함
        var criminalIndexOf = new Dictionary<NpcController, int>();
        for (int i = 0; i < criminals.Count; i++)
            criminalIndexOf[criminals[i]] = i;

        // 3. 배정 — 범인·디코이는 담당 범인과 공개 특징 일치, 나머지는 모든 범인과 최소 1개 공개 특징이 다르게
        var logBuilder = new System.Text.StringBuilder();
        foreach (NpcController npc in npcs)
        {
            AppearanceProfile profile;
            string role;
            if (criminalIndexOf.TryGetValue(npc, out int criminalIndex))
            {
                profile = m_criminalProfiles[criminalIndex];
                role = $"  ← 범인 #{criminalIndex + 1}";
            }
            else if (decoyOwners.TryGetValue(npc, out int ownerIndex))
            {
                profile = CreateDecoyProfile(m_criminalProfiles[ownerIndex]);
                role = $"  ← 디코이 (범인 #{ownerIndex + 1})";
            }
            else
            {
                profile = CreateNonMatchingProfile();
                role = string.Empty;
            }

            ApplyToNpc(npc, profile);
            logBuilder.AppendLine($"  {DescribeProfile(profile)}{role}");
        }

        // 4. 범인별 몽타주 텍스트 생성 — 본부 수배 UI(#58)의 원본. 범인마다 이벤트를 한 번씩 발행한다 (#127)
        for (int i = 0; i < criminals.Count; i++)
        {
            string montageText = m_appearanceDatabase.BuildMontageText(m_criminalProfiles[i], m_revealedAxes);
            m_montageTexts.Add(montageText);
            OnMontageGenerated?.Invoke(criminals[i], montageText);
        }

        // 수배 UI(#58) 전까지는 로그로 배치 결과를 확인한다 — 정답이 노출되므로 데모 빌드 전에 제거할 것
        Debug.Log($"외형 배정 완료 ({npcs.Count}명, 범인 {criminals.Count}명) | 몽타주: {string.Join(" / ", m_montageTexts.ConvertAll(t => $"\"{t}\""))} | 몽타주당 부합 목표 {m_montageMatchCount}명\n{logBuilder}");
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

    /// <summary>
    /// 범인을 제외한 NPC를 셔플해 범인마다 k−1명씩 디코이로 겹치지 않게 배분한다.
    /// 후보가 모자라면 뒤쪽 범인의 디코이가 줄어든다. 반환: 디코이 NPC → 담당 범인 인덱스.
    /// </summary>
    private Dictionary<NpcController, int> PickDecoys(IReadOnlyList<NpcController> npcs, IReadOnlyList<NpcController> criminals)
    {
        var criminalSet = new HashSet<NpcController>(criminals);
        var candidates = new List<NpcController>(npcs.Count);
        foreach (NpcController npc in npcs)
        {
            if (!criminalSet.Contains(npc))
                candidates.Add(npc);
        }

        for (int i = candidates.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
        }

        int decoyPerCriminal = Mathf.Max(m_montageMatchCount - 1, 0);
        var decoyOwners = new Dictionary<NpcController, int>();
        int next = 0;
        for (int criminalIndex = 0; criminalIndex < criminals.Count; criminalIndex++)
        {
            for (int i = 0; i < decoyPerCriminal && next < candidates.Count; i++)
                decoyOwners[candidates[next++]] = criminalIndex;
        }

        if (next >= candidates.Count && decoyPerCriminal * criminals.Count > candidates.Count)
            Debug.LogWarning(
                $"AppearanceAssigner: 디코이 후보({candidates.Count}명)가 필요 수({decoyPerCriminal * criminals.Count}명)보다 적어 일부 몽타주의 부합 인원이 목표 k에 못 미친다",
                this
            );
        return decoyOwners;
    }

    /// <summary>공개 축은 담당 범인과 같게, 비공개 축은 랜덤인 디코이 프로필을 만든다.</summary>
    private AppearanceProfile CreateDecoyProfile(in AppearanceProfile criminalProfile)
    {
        AppearanceProfile profile = m_appearanceDatabase.CreateRandomProfile();
        foreach (AppearanceAxis axis in m_revealedAxes)
            profile.SetIndex(axis, criminalProfile.GetIndex(axis));
        return profile;
    }

    /// <summary>모든 범인과 공개 특징 중 최소 1개가 다른 프로필을 만든다 — 전 몽타주 비부합 보장.</summary>
    private AppearanceProfile CreateNonMatchingProfile()
    {
        AppearanceProfile profile = m_appearanceDatabase.CreateRandomProfile();
        if (!MatchesAnyCriminal(profile))
            return profile;

        // 우연히 어느 범인과 공개 축이 전부 일치하면, 공개 축 하나를 '모든 범인의 값과 다른' 값으로
        // 강제로 바꾼다 — 몽타주 부합은 공개 축 전부 일치일 때만 성립하므로 이 한 번으로 전 범인 비부합이 보장된다
        foreach (AppearanceAxis axis in m_revealedAxes)
        {
            int optionCount = m_appearanceDatabase.GetOptionCount(axis);
            var criminalValues = new HashSet<int>();
            foreach (AppearanceProfile criminalProfile in m_criminalProfiles)
                criminalValues.Add(criminalProfile.GetIndex(axis));

            // 이 축의 옵션이 전부 범인 값으로 차 있으면 다른 축에서 시도한다
            if (criminalValues.Count >= optionCount)
                continue;

            int value;
            do
            {
                value = Random.Range(0, optionCount);
            } while (criminalValues.Contains(value));
            profile.SetIndex(axis, value);
            return profile;
        }

        // 모든 공개 축이 범인 값들로 가득 참 — 비부합을 만들 수 없다 (옵션 수 대비 범인이 너무 많음)
        Debug.LogWarning("AppearanceAssigner: 공개 축 옵션이 범인 외형 값들로 가득 차 비부합 프로필을 만들 수 없다 — AppearanceDatabase 옵션 수나 진범 수를 조정할 것", this);
        return profile;
    }

    /// <summary>어느 한 범인이라도 공개 축이 전부 일치하는지 — 몽타주 부합 여부.</summary>
    private bool MatchesAnyCriminal(in AppearanceProfile profile)
    {
        foreach (AppearanceProfile criminalProfile in m_criminalProfiles)
        {
            if (profile.MatchesOn(criminalProfile, m_revealedAxes))
                return true;
        }
        return false;
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
