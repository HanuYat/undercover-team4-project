using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using Random = UnityEngine.Random;

/// <summary>
/// 범인 확정 후 전 NPC에 외형 특징 조합을 배정한다 — 디코이 보장 배치. (#74)
/// 범인별 외형 확정 → 공개 축 선택(전 범인 공통) → 각 몽타주에 부합하는 NPC가 정확히 k명(해당 범인 포함)이
/// 되도록 범인마다 디코이 k−1명에게 공개 특징 일치 조합을 주고, 나머지는 모든 범인과 공개 특징이
/// 최소 1개 다르게 배정한다. k값으로 난이도를 조절한다 (GDD 6-5 분포=난이도).
/// 진범이 여러 명이면(#127) 몽타주도 범인 수만큼 생성된다. 다만 발행(OnMontageGenerated)은
/// 이미 공개된 수배만 — 대기 중인 예비 용의자(#102)는 RevealMontage로 나중에 발행된다.
/// 배정은 서버 권위 — NpcAppearance.SetProfile이 인덱스만 전 클라이언트에 동기화한다 (#56).
/// 몽타주(GDD 10-3 글 방식)의 원본은 범인 프로필 + 공개 축이고, 문장 조립은 표시하는 쪽
/// (본부 수배 UI #58)이 자기 언어로 한다 — 여기서 문장을 만들어 보관하지 않는다 (#497).
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class AppearanceAssigner : CommonManagerBase
{
    private CriminalAssigner Assigner => App.Game.CriminalAssigner;
    private NpcSpawner Spawner => App.Game.NpcSpawner;

    [Header("외형 축별 옵션 정의")]
    [SerializeField] private AppearanceDatabase m_appearanceDatabase;

    [Header("몽타주 공개 특징 수")]
    [Tooltip("범인 외형 축 중 몽타주로 공개할 축 수 (기획 2~3개). 진범이 여러 명이어도 공개 축은 공통이다")]
    [Range(1, AppearanceProfile.k_axisCount)]
    [SerializeField] private int m_revealedAxisCount = 2;

    [Header("몽타주 부합 인원 k (범인 포함)")]
    [Tooltip("몽타주 1건당 공개 특징에 부합하는 NPC 수 — 범인 1명 + 디코이 k−1명. 클수록 스캔 검증 부담이 커진다 (난이도)")]
    [SerializeField] private int m_montageMatchCount = 3;

    private RevealedAxisSet m_revealedAxes;
    private readonly List<AppearanceProfile> m_criminalProfiles = new List<AppearanceProfile>();

    /// <summary>몽타주로 공개된 특징 축들. 배정 전에는 비어 있다. 전 범인 공통.</summary>
    public RevealedAxisSet RevealedAxes => m_revealedAxes;

    /// <summary>범인별 외형 특징 조합 — CriminalAssigner.CriminalNpcs와 같은 순서. 배정 전에는 비어 있다. (#127)</summary>
    public IReadOnlyList<AppearanceProfile> CriminalProfiles => m_criminalProfiles;

    /// <summary>
    /// 외형 축별 옵션 정의 — 인덱스를 표시 이름으로 옮길 때 쓴다. 읽기 전용.
    /// 수배 UI(#58)가 몽타주 문장을 조립하는 출처이기도 하다 — 클라이언트에도 씬에 배선돼 있다 (#497).
    /// </summary>
    public AppearanceDatabase Database => m_appearanceDatabase;

    /// <summary>
    /// 몽타주 공개 이벤트 — 수배 UI(#58)가 구독한다. 범인마다 한 번씩 (해당 범인, 외형 프로필)로 발행된다. (#127)
    /// 문장이 아니라 프로필을 넘긴다 — 표시하는 피어가 자기 언어로 조립하기 때문이다 (#497).
    /// 공개 축은 <see cref="RevealedAxes"/>로 함께 읽는다(라운드 내내 고정, 전 범인 공통).
    /// </summary>
    public event Action<NpcController, AppearanceProfile> OnMontageGenerated;

    private void Start()
    {
        if (Assigner == null)
        {
            Debug.LogWarning("AppearanceAssigner: CriminalAssigner를 찾지 못해 외형 배정 불가", this);
            return;
        }

        // 범인 확정 이벤트 구독 — 매니저 간 구독은 모든 매니저의 Awake(App 등록)가 끝난 Start에서 한다.
        // 실제 배정은 스폰 완료(수 프레임 뒤) 이후에 발화하므로 Start 구독으로 놓치지 않는다.
        Assigner.OnCriminalAssigned += AssignAll;

        // 구독 전에 배정이 이미 끝난 경우 보정
        if (Assigner.CriminalNpcs.Count > 0 && m_criminalProfiles.Count == 0)
            AssignAll(Assigner.CriminalNpcs);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy(); // App 등록 해제

        if (Assigner != null)
            Assigner.OnCriminalAssigned -= AssignAll;
    }

    private void AssignAll(IReadOnlyList<NpcController> criminals)
    {
        if (m_appearanceDatabase == null)
        {
            Debug.LogWarning("[AppearanceAssigner] AppearanceDatabase가 지정되지 않아 외형 배정 불가", this);
            return;
        }
        if (Spawner == null)
        {
            Debug.LogWarning("[AppearanceAssigner] NpcSpawner를 찾지 못해 외형 배정 불가", this);
            return;
        }
        IReadOnlyList<NpcController> npcs = Spawner.SpawnedNpcs;
        if (npcs.Count == 0 || criminals.Count == 0) return;

        AppearanceModelCatalog catalog = FindCatalog(npcs);

        // 1. 범인 배정
        m_criminalProfiles.Clear();
        foreach (NpcController c in criminals)
        {
            NpcCatalogAppearance cat = c.GetComponent<NpcCatalogAppearance>();
            if (cat != null && catalog != null) 
                m_criminalProfiles.Add(catalog.GetProfile(cat.ModelIndex)); // Sci-fi
            else
                m_criminalProfiles.Add(m_appearanceDatabase.CreateRandomProfile()); // Generic
        }
        PickRevealedAxes();

        // 2. 디코이 선정
        Dictionary<NpcController, int> decoyOwners = PickDecoys(npcs, criminals);
        var criminalIndexOf = new Dictionary<NpcController, int>();
        for (int i = 0; i < criminals.Count; i++) criminalIndexOf[criminals[i]] = i;

        // 3. 역할별 실현
        var logBuilder = new StringBuilder();
        foreach (var npc in npcs)
        {
            AppearanceProfile applied;
            string role;
            if (criminalIndexOf.TryGetValue(npc, out int ci))
            {
                applied = RealizeCriminal(npc, ci, catalog);
                role = $"  ← 범인 #{ci + 1}";
            }
            else if (decoyOwners.TryGetValue(npc, out int oi))
            {
                applied = RealizeDecoy(npc, oi, catalog);
                role = $"  ← 디코이 (범인 #{oi + 1})";
            }
            else
            {
                applied = RealizeNonMatching(npc, catalog);
                role = string.Empty;
            }
            logBuilder.AppendLine($"  {DescribeProfile(applied)}{role}");
        }

        // 4. 몽타주 공개 — 이미 공개된 수배만 발행한다. 예비 용의자(#102)는 IsCriminal = false로
        //    대기하다가 승격 시 RevealMontage가 m_criminalProfiles의 같은 프로필을 발행한다.
        //    공개 축(m_revealedAxes)은 라운드 내내 고정이라 그 시점에 다시 뽑지 않는다 — 다시 뽑으면
        //    본부에 이미 떠 있던 몽타주가 무효가 된다. 문장을 보관하지 않는 이유도 같다:
        //    보관할 원본은 프로필이고, 문장은 표시하는 쪽이 자기 언어로 조립한다 (#497).
        int revealedCount = 0;
        var montageLog = new StringBuilder();
        for (int i = 0; i < criminals.Count; i++)
        {
            AppearanceProfile p = m_criminalProfiles[i];

            if (montageLog.Length > 0)
                montageLog.Append(" / ");
            montageLog.Append('"').Append(m_appearanceDatabase.BuildMontageText(p, m_revealedAxes)).Append('"');

            CitizenIdentity identity = criminals[i].GetComponent<CitizenIdentity>();
            if (identity == null || !identity.IsCriminal)
                continue;

            revealedCount++;
            OnMontageGenerated?.Invoke(criminals[i], p);
        }
        Debug.Log($"외형 배정 완료 ({npcs.Count}명, 용의자 {criminals.Count}명 중 공개 {revealedCount}명) | 몽타주: {montageLog}\n{logBuilder}");
    }

    /// <summary>
    /// 대기 중이던 용의자의 몽타주를 지금 발행한다 — 제보 전화 승격(#102) 전용. 서버(또는 오프라인) 전용.
    /// 라운드 시작에 확정해 둔 프로필을 그대로 쓰므로 공개 축은 바뀌지 않는다.
    /// 발행 자체는 라운드 시작 때와 같은 경로(OnMontageGenerated)라, WantedListManager의
    /// NetworkList 추가와 TotalWanted++ 가 그대로 따라온다 — 별도 등록 경로를 만들지 않는 이유다.
    /// </summary>
    public void RevealMontage(NpcController npc)
    {
        if (npc == null)
            return;

        IReadOnlyList<NpcController> criminals = Assigner != null ? Assigner.CriminalNpcs : null;
        if (criminals == null)
        {
            Debug.LogWarning("AppearanceAssigner: CriminalAssigner를 찾지 못해 몽타주를 공개할 수 없다", this);
            return;
        }

        // CriminalNpcs와 m_criminalProfiles는 같은 순서다 — 인덱스로 짝을 찾는다
        for (int i = 0; i < criminals.Count; i++)
        {
            if (criminals[i] != npc)
                continue;

            if (i >= m_criminalProfiles.Count)
            {
                Debug.LogWarning($"AppearanceAssigner: {npc.name}의 외형이 아직 배정되지 않아 몽타주를 공개할 수 없다", npc);
                return;
            }

            OnMontageGenerated?.Invoke(npc, m_criminalProfiles[i]);
            return;
        }

        Debug.LogWarning($"AppearanceAssigner: {npc.name}은(는) 용의자 목록에 없어 공개 대상이 아니다", npc);
    }

    /// <summary>범인: SciFi는 스폰 모델 유지, Generic은 확정 프로필 배정. 신원엔 실제 프로필 반영.</summary>
    private AppearanceProfile RealizeCriminal(NpcController npc, int criminalIndex, AppearanceModelCatalog catalog)
    {
        NpcCatalogAppearance cat = npc.GetComponent<NpcCatalogAppearance>();
        if (cat != null && catalog != null)
        {
            AppearanceProfile p = catalog.GetProfile(cat.ModelIndex);
            AssignIdentity(npc, p);
            return p;
        }
        return RealizeGeneric(npc, m_criminalProfiles[criminalIndex]);
    }

    /// <summary>디코이: 담당 범인과 공개 축 일치. SciFi는 일치 모델 선택, Generic은 프로필 배정.</summary>
    private AppearanceProfile RealizeDecoy(NpcController npc, int ownerIndex, AppearanceModelCatalog catalog)
    {
        AppearanceProfile owner = m_criminalProfiles[ownerIndex];
        NpcCatalogAppearance cat = npc.GetComponent<NpcCatalogAppearance>();
        if (cat != null && catalog != null)
        {
            int idx = FindModelMatchingRevealed(catalog, owner);
            if (idx >= 0)
            {
                cat.SetModelIndex(idx);
                AppearanceProfile p = catalog.GetProfile(idx);
                AssignIdentity(npc, p);
                return p;
            }
            Debug.LogWarning($"[AppearanceAsigner] SciFi 디코이 {npc.name}에 공개축 일치 모델이 없어 비부합 처리 - 몽타주 부합 인원 부족 가능", npc);
            return RealizeSciFiNonMatching(npc, cat, catalog);
        }
        return RealizeGeneric(npc, CreateDecoyProfile(owner));
    }

    /// <summary>비부합: 전 범인과 공개 축이 최소 1개 다르게.</summary>
    private AppearanceProfile RealizeNonMatching(NpcController npc, AppearanceModelCatalog catalog)
    {
        NpcCatalogAppearance cat = npc.GetComponent<NpcCatalogAppearance>();
        if (cat != null && catalog != null)
            return RealizeSciFiNonMatching(npc, cat, catalog);
        return RealizeGeneric(npc, CreateNonMatchingProfile());
    }

    private AppearanceProfile RealizeSciFiNonMatching(NpcController npc, NpcCatalogAppearance cat, AppearanceModelCatalog catalog)
    {
        int idx = PickNonMatchingModel(catalog);
        cat.SetModelIndex(idx);
        AppearanceProfile p = catalog.GetProfile(idx);
        AssignIdentity(npc, p);
        return p;
    }

    /// <summary>Generic: 프롭 배정 + 신원. NpcAppearance.SetProfile이 인덱스만 전 클라에 동기화.</summary>
    private AppearanceProfile RealizeGeneric(NpcController npc, AppearanceProfile profile)
    {
        NpcAppearance app = npc.GetComponent<NpcAppearance>();
        if (app != null) app.SetProfile(profile);
        else Debug.LogWarning($"AppearanceAssigner: {npc.name}에 외형 컴포넌트가 없어 시각 적용 생략", npc);
        AssignIdentity(npc, profile);
        return profile;
    }

    private void AssignIdentity(NpcController npc, AppearanceProfile profile)
    {
        CitizenIdentity identity = npc.GetComponent<CitizenIdentity>();
        if (identity != null) identity.AssignAppearance(profile);
    }

    /// <summary>공개 축이 criminalProfile과 모두 같은 모델 인덱스(셔플 후 첫). 없으면 -1.</summary>
    private int FindModelMatchingRevealed(AppearanceModelCatalog catalog, AppearanceProfile criminalProfile)
    {
        var order = new List<int>(catalog.Count);
        for (int i = 0; i < catalog.Count; i++) order.Add(i);
        for (int i = order.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }
        foreach (int m in order)
        {
            AppearanceProfile p = catalog.GetProfile(m);
            if (p.MatchesOn(criminalProfile, m_revealedAxes)) return m;
        }
        return -1;
    }

    private AppearanceModelCatalog FindCatalog(IReadOnlyList<NpcController> npcs)
    {
        foreach (var n in npcs)
        {
            var cat = n.GetComponent<NpcCatalogAppearance>();
            if (cat != null && cat.Catalog != null) return cat.Catalog;
        }
        return null;
    }

    private int PickNonMatchingModel(AppearanceModelCatalog catalog)
    {
        var order = new List<int>(catalog.Count);
        for (int i = 0; i < catalog.Count; i++) order.Add(i);
        for (int i = order.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }
        foreach (int m in order)
        {
            AppearanceProfile p = catalog.GetProfile(m);
            if (!MatchesAnyCriminal(p)) return m;
        }
        Debug.LogWarning("[AppearanceAssigner] 전 범인과 비부합인 모델이 없음 — 공개 축 수나 모델 다양성 확인", this);
        return Random.Range(0, catalog.Count);
    }

    /// <summary>축 전체를 셔플해 앞에서 공개 수만큼 고른다. 담기는 순서는 뜻이 없다 — 집합이다.
    /// 머리가 보이지 않는 범인이 하나라도 있으면 머리색은 후보에서 빠진다 (#556).</summary>
    private void PickRevealedAxes()
    {
        bool skipHairColor = !AllCriminalsHaveVisibleHair();

        var order = new List<AppearanceAxis>(AppearanceProfile.k_axisCount);
        for (int i = 0; i < AppearanceProfile.k_axisCount; i++)
        {
            var axis = (AppearanceAxis)i;
            if (skipHairColor && axis == AppearanceAxis.HairColor)
                continue;
            order.Add(axis);
        }

        for (int i = order.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }

        int count = Mathf.Clamp(m_revealedAxisCount, 1, order.Count);
        m_revealedAxes.Clear();
        for (int i = 0; i < count; i++)
            m_revealedAxes.Add(order[i]);
    }

    /// <summary>
    /// 전 범인의 머리가 화면에 보이는가 — 머리색을 몽타주 힌트로 쓸 수 있는지의 기준. (#556)
    ///
    /// 머리 스타일이 '없음(대머리)'·'가림'이면 머리색은 화면에 나타나지 않고(NpcAppearance.TintPropAxis는
    /// 대상 프롭이 없으면 건너뛴다) 몽타주 텍스트에만 남는다 — 현장에서 눈으로 대조할 수 없는 특징이
    /// 무전에 실리면 대조가 성립하지 않고 그대로 오검거로 이어진다.
    /// 공개 축은 전 범인 공통이라, 한 명이라도 어긋나면 축 전체를 쓰지 않는다.
    /// </summary>
    private bool AllCriminalsHaveVisibleHair()
    {
        foreach (AppearanceProfile profile in m_criminalProfiles)
        {
            if (!m_appearanceDatabase.HasVisibleHair(profile))
                return false;
        }
        return true;
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
            var criminalValues = new HashSet<int>();
            foreach (AppearanceProfile criminalProfile in m_criminalProfiles)
                criminalValues.Add(criminalProfile.GetIndex(axis));

            // Generic 경로이므로 SciFiOnly가 아닌 값 중에서만 대체값을 찾는다 (가림 등 재유입 방지)
            List<int> selectable = m_appearanceDatabase.GetGenericSelectableIndices(axis);
            selectable.RemoveAll(v => criminalValues.Contains(v));

            // 이 축의 선택 가능한 값이 전부 범인 값으로 차 있으면 다른 축에서 시도한다
            if (selectable.Count == 0)
                continue;

            profile.SetIndex(axis, selectable[Random.Range(0, selectable.Count)]);
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
            builder.Append(AppearanceDatabase.GetOptionName(option));
        }
        return builder.ToString();
    }
}
