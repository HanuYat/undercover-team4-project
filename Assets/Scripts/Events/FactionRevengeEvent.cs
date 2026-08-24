using UnityEngine;
using Random = UnityEngine.Random;

/// <summary>
/// 세력 소탕 — 유치장에 갇힌 범인의 세력이 복수대 N명을 보내 현장을 덮친다. (GDD 6-4, #721)
/// 조직원 전원이 <b>같은 플레이어 한 명</b>을 물어 표적이 혼자 버티지 못하게 하는 것이 협동 강제의 실체다.
///
/// 상태를 새로 만들지 않는다 — 동네 깡패(<see cref="StreetThugEvent"/>)가 검증한
/// <see cref="NpcResistState"/>를 그대로 쓰고, 인원(<see cref="SpawnCount"/>)·포기하지 않음
/// (<see cref="NpcReaction.IsRelentless"/>)·고정 외형 셋만 다르다. 규칙과 근거는 GDD 6-4.
/// </summary>
public class FactionRevengeEvent : SpawnedNpcEventBase
{
    private const int k_neverTriggered = int.MinValue;

    [Header("세력 소탕 — 발동 조건")]
    [Tooltip("이 라운드부터 발동한다 — 유치장에 세력 있는 수감자가 쌓이는 라운드 후반용 이벤트 (수치 보류, GDD 12장)")]
    [Min(1)]
    [SerializeField] private int m_minRound = 3;

    [Header("복수대 인원")]
    [Tooltip("라운드별 복수대 인원 표. 비우면 아래 폴백 인원을 그대로 쓴다")]
    [SerializeField] private FactionRevengeTable m_memberTable;

    [Tooltip("표를 안 붙였을 때 쓸 복수대 인원")]
    [Min(1)]
    [SerializeField] private int m_fallbackMemberCount = 3;

    [Header("외형")]
    [Tooltip("복수대 전원이 쓸 모델 이름 — AppearanceModelCatalog의 ModelName과 대조한다. 비우면 프리팹 기본(무작위 바디)")]
    [SerializeField] private string m_modelName = "Character_Muscle_Male_01";

    // 라운드당 1회 게이트 — 매니저에는 횟수 제한 개념이 없어 이벤트가 스스로 판정한다
    private int m_lastTriggeredRound = k_neverTriggered;

    // 발동 <b>근거</b> 세력 — 로그용이다. 조직원 프로필에는 실리지 않는다 (GDD 6-4)
    private OfficialRecords.Faction m_faction;

    // 개발자 단축키가 게이트를 건너뛴 발동인가 — ServerBegin에서 한 번 쓰고 버린다 (#775)
    private bool m_forced;

    public override string NoticeKey => "Hud.Event.Notice.FactionRevenge";

    protected override ERiotBehavior RiotBehavior => ERiotBehavior.Resist;

    protected override int SpawnCount =>
        m_memberTable != null
            ? m_memberTable.GetMemberCount(CurrentRound, m_fallbackMemberCount)
            : m_fallbackMemberCount;

    // 상주 진행도가 없는 구성(오프라인 단독 Play)은 1라운드로 친다 — SuddenEventManager와 같은 규약
    private static int CurrentRound
    {
        get
        {
            RoundProgress progress = App.Game.RoundProgress;
            return progress != null ? progress.Current : RoundProgress.k_firstRound;
        }
    }

    public override bool CanTrigger()
    {
        int round = CurrentRound;
        if (round < m_minRound || round == m_lastTriggeredRound)
            return false;

        // 복수할 세력이 있는지만 본다 — 어느 세력인지는 ServerBegin에서 고른다(술어가 상태를 남기지 않게)
        if (!TryPickJailedFaction(out _))
            return false;

        return base.CanTrigger(); // 표적이 될 현장 플레이어
    }

    /// <summary>강제 발동 준비 — 라운드 하한·라운드당 1회·수감자 조건을 건너뛴다.
    /// 표적만은 그대로 요구한다 — 때릴 상대가 없으면 이벤트가 성립하지 않는다. (#775)</summary>
    public override bool ServerPrepareForceTrigger()
    {
        if (!base.CanTrigger())
        {
            Debug.LogWarning("FactionRevengeEvent: 강제 발동할 표적이 없다 — 행동 가능한 현장 플레이어가 없다", this);
            return false;
        }

        m_forced = true;
        Debug.Log("[세력 소탕] 강제 발동 준비 — 라운드 게이트·수감자 조건을 건너뛴다");
        return true;
    }

    public override void ServerBegin()
    {
        // 세력은 여기서 고른다 — 강제 발동이면 세력 있는 수감자가 없을 수 있어 임의 세력으로 떨어진다
        if (!TryPickJailedFaction(out m_faction))
            m_faction = PickAnyFaction();

        base.ServerBegin();

        if (!IsActive)
        {
            m_forced = false;
            return; // 스폰 불발 — 게이트를 소모하지 않는다
        }

        // 강제 발동은 게이트를 남기지 않는다 — 인원 스케일을 보려면 한 라운드에 여러 번 띄워야 한다
        if (!m_forced)
            m_lastTriggeredRound = CurrentRound;
        m_forced = false;

        Debug.Log($"[세력 소탕] 복수대가 현장으로 몰려온다 ({CurrentRound}라운드) — 발동 근거: 유치장의 {m_faction} 수감자");
    }

    /// <summary>라운드 종료 정리 — 스폰물과 함께 라운드당 1회 게이트도 푼다. 매니저가 InProgress를
    /// 벗어날 때만 부르므로(날씨 교체 경로는 IRoundWeather만 탄다) 라운드 경계와 일치한다.
    /// 세션을 새로 시작해 라운드가 1로 되돌아와도 지난 게이트가 첫 라운드를 막지 않는다.</summary>
    public override void ServerReset()
    {
        base.ServerReset();
        m_lastTriggeredRound = k_neverTriggered;
    }

    /// <summary>외형을 한 모델로 고정한다 — 프리팹 기본은 무작위 바디라 그대로 두면 잡다한 군중이 된다.</summary>
    protected override void OnSpawned(NpcController npc)
    {
        if (string.IsNullOrEmpty(m_modelName))
            return;

        NpcCatalogAppearance appearance = npc.GetComponent<NpcCatalogAppearance>();
        int index = IndexOfModel(appearance, m_modelName);
        if (index >= 0)
        {
            appearance.SetModelIndex(index);
            return;
        }

        Debug.LogWarning(
            $"FactionRevengeEvent: 외형 모델 '{m_modelName}'을 카탈로그에서 찾지 못해 프리팹 기본 외형으로 둔다", this);
    }

    protected override void ApplyBehavior(NpcController npc)
    {
        // 전원에게 같은 표적을 넘긴다. 표적이 다운되면 저항 상태의 기존 규칙대로 각자 근처 플레이어로 넘어간다.
        npc.Reaction.StartResist(m_threat, relentless: true);
    }

    // 세력이 있는 수감자 중 하나를 뽑아 그 세력을 돌려준다 — 없으면 false.
    // 후보를 모으지 않고 지나가며 뽑는다(reservoir) — 수감자가 몇이든 할당이 없다.
    private static bool TryPickJailedFaction(out OfficialRecords.Faction faction)
    {
        faction = OfficialRecords.Faction.None;

        JailZone jail = App.Game.Jail;
        if (jail == null)
            return false;

        int candidates = 0;
        foreach (NpcController inmate in jail.Inmates)
        {
            if (inmate == null)
                continue;

            CitizenIdentity identity = inmate.GetComponent<CitizenIdentity>();
            if (identity == null || identity.Profile == null)
                continue;

            if (identity.Profile.Faction == OfficialRecords.Faction.None)
                continue;

            candidates++;
            if (Random.Range(0, candidates) == 0)
                faction = identity.Profile.Faction;
        }

        return candidates > 0;
    }

    // 강제 발동 전용 — 세력 있는 수감자가 없을 때 쓸 임의 세력. 전부 None이면 None.
    private static OfficialRecords.Faction PickAnyFaction()
    {
        var all = (OfficialRecords.Faction[])System.Enum.GetValues(typeof(OfficialRecords.Faction));

        int candidates = 0;
        OfficialRecords.Faction picked = OfficialRecords.Faction.None;
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i] == OfficialRecords.Faction.None)
                continue;

            candidates++;
            if (Random.Range(0, candidates) == 0)
                picked = all[i];
        }

        return picked;
    }

    // 정확히 일치를 먼저 보고, 없으면 접미사로 찾는다("Muscle_Male_01" → "Character_Muscle_Male_01"). 없으면 -1.
    private static int IndexOfModel(NpcCatalogAppearance appearance, string modelName)
    {
        AppearanceModelCatalog catalog = appearance != null ? appearance.Catalog : null;
        if (catalog == null)
            return -1;

        int suffixMatch = -1;
        int suffixCount = 0;
        for (int i = 0; i < catalog.Count; i++)
        {
            string name = catalog.GetModelName(i);
            if (string.IsNullOrEmpty(name))
                continue;

            if (string.Equals(name, modelName, System.StringComparison.OrdinalIgnoreCase))
                return i;

            if (!name.EndsWith(modelName, System.StringComparison.OrdinalIgnoreCase))
                continue;

            suffixCount++;
            if (suffixMatch < 0)
                suffixMatch = i;
        }

        // 접미사가 여럿 걸리면 어느 것을 골랐는지 알린다 — 조용히 엉뚱한 모델을 입지 않게
        if (suffixCount > 1)
            Debug.LogWarning($"FactionRevengeEvent: '{modelName}'이 접미사로 {suffixCount}개 모델에 걸려 "
                + $"'{catalog.GetModelName(suffixMatch)}'을 골랐다 — 전체 이름으로 적을 것");

        return suffixMatch;
    }
}
