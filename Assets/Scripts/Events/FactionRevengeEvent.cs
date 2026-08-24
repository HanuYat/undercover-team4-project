using UnityEngine;
using Random = UnityEngine.Random;

/// <summary>
/// 세력 소탕 — <b>유치장에 갇힌 범인의 세력</b>이 복수대를 보내 현장을 덮친다. (GDD 6-4, #721)
/// 여럿이 붙어야 풀리는 합동 이벤트를 <b>수적 압박</b> 하나로 만든다: 조직원 N명이 한 지점에서 덩어리로
/// 나와 <b>전원이 같은 플레이어 한 명</b>을 문다. 표적이 혼자 못 버티므로 동료가 몰려와야 한다 —
/// 그것이 여기서 말하는 협동 강제의 실체다.
///
/// 상태를 새로 만들지 않는다 — 동네 깡패(<see cref="StreetThugEvent"/> #806)가 검증한
/// <see cref="NpcResistState"/>(표적 추격 + 텔레그래프 스윙)를 그대로 쓴다. 달라지는 것은 셋뿐이다:
///  · <b>인원</b> — <see cref="SpawnedNpcEventBase.SpawnCount"/>를 라운드 표에서 읽는다.
///  · <b>포기하지 않는다</b> — 기절시켜도 일어나 다시 저항으로 돌아온다
///    (<see cref="NpcReaction.IsRelentless"/>). 끝은 <b>사망 또는 밧줄 연행</b>뿐이다.
///  · <b>같은 얼굴</b> — 전원 한 모델로 고정해 한 방향에서 오는 "무리"로 즉시 읽히게 한다.
///
/// <b>오검거 위험은 없다.</b> 고정 외형(기본 Muscle_Male_01)은 범인·디코이로도 나올 수 있는 모델이지만,
/// <see cref="ArrestJudge"/>가 신원 대조보다 <see cref="MisdemeanorOffender"/> 마커를 <b>먼저</b> 보므로
/// 잡으면 경범죄 판정 + 수익이고 오검거로 집계되지 않는다.
///
/// <b>소란 지속 시간은 0(무제한)으로 둔다</b> — 방치하면 라운드 끝까지 따라온다. 그것이 의도다.
/// </summary>
public class FactionRevengeEvent : SpawnedNpcEventBase
{
    // 라운드 게이트 초기값 — 아직 한 번도 발동하지 않았다
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

    // 마지막으로 발동한 라운드 — 라운드당 1회 게이트. SuddenEventManager에는 횟수 제한 개념이 없고
    // (주기 추첨만 한다) 거기 넣으면 모든 이벤트에 영향을 주므로 이 이벤트 안에서 판정한다.
    private int m_lastTriggeredRound = k_neverTriggered;

    // 이번에 복수하러 온 세력 — 로그용. 외형은 세력별로 가르지 않는다(싸우는 데 알 필요가 없다).
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

        // 복수할 세력이 있어야 성립한다 — 유치장이 비었거나 수감자가 전원 무소속이면 발동하지 않는다
        if (!TryPickJailedFaction(out m_faction))
            return false;

        return base.CanTrigger(); // 표적이 될 현장 플레이어
    }

    /// <summary>
    /// 강제 발동 준비 (#775) — 테스트 때마다 범인을 잡아 수감시키지 않게 <b>조건 셋을 건너뛴다</b>:
    /// 발동 하한 라운드, 라운드당 1회, 세력 있는 수감자 존재. 세력은 수감자가 있으면 그 세력을 쓰고
    /// 없으면 임의로 정한다.
    ///
    /// <b>현장 플레이어만은 그대로 요구한다</b> — 표적이 없으면 때릴 상대가 없어 이벤트가 성립하지 않는다.
    /// </summary>
    public override bool ServerPrepareForceTrigger()
    {
        if (!base.CanTrigger())
        {
            Debug.LogWarning("FactionRevengeEvent: 강제 발동할 표적이 없다 — 행동 가능한 현장 플레이어가 없다", this);
            return false;
        }

        if (!TryPickJailedFaction(out m_faction))
            m_faction = PickAnyFaction();

        m_forced = true;
        Debug.Log($"[세력 소탕] 강제 발동 준비 — 세력 {m_faction} (라운드 게이트·수감자 조건을 건너뛴다)");
        return true;
    }

    public override void ServerBegin()
    {
        base.ServerBegin();

        if (!IsActive)
        {
            m_forced = false;
            return; // 스폰이 불발됐다 — 게이트를 소모하지 않는다(위에 이유가 로그로 남아 있다)
        }

        // 강제 발동은 게이트를 남기지 않는다 — 인원 스케일을 보려면 한 라운드에 여러 번 띄워야 한다
        if (!m_forced)
            m_lastTriggeredRound = CurrentRound;
        m_forced = false;

        Debug.Log($"[세력 소탕] {m_faction} 복수대가 현장으로 몰려온다 ({CurrentRound}라운드)");
    }

    /// <summary>
    /// 외형을 한 모델로 고정한다 — 같은 얼굴 N명이 한 방향에서 오면 "무리"로 즉시 읽힌다.
    /// 프리팹 기본은 무작위 바디(<see cref="NpcCatalogAppearance"/>)라 그대로 두면 잡다한 군중이 된다.
    /// </summary>
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
        // 전원에게 <b>같은</b> 표적을 넘긴다. 표적이 다운되면 NpcResistState.IsStillEngaged의 기존 규칙대로
        // 각자 근처 플레이어로 넘어간다 — 쓰러진 사람을 계속 때려 구조를 막지 않는다.
        // relentless — 기절시켜도 일어나 다시 저항으로 돌아온다. 끝은 사망 또는 밧줄 연행뿐이다.
        npc.Reaction.StartResist(m_threat, relentless: true);
    }

    /// <summary>
    /// 유치장 수감자 중 세력이 있는 자를 골라 그 세력을 돌려준다 — 전원 무소속이거나 유치장이 없으면 false.
    /// 후보를 모으지 않고 지나가며 뽑는다(reservoir sampling) — 수감자가 몇이든 할당이 없다.
    /// </summary>
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

    // 세력 있는 수감자가 없을 때(강제 발동 전용) 임의 세력 — None을 뺀 값 중 하나. 전부 None이면 None.
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

    // 카탈로그에서 모델 이름의 인덱스를 찾는다 — 못 찾으면 -1.
    // 정확히 일치하는 것을 먼저 보고, 없으면 접미사로 찾는다("Muscle_Male_01" → "Character_Muscle_Male_01").
    private static int IndexOfModel(NpcCatalogAppearance appearance, string modelName)
    {
        AppearanceModelCatalog catalog = appearance != null ? appearance.Catalog : null;
        if (catalog == null)
            return -1;

        int suffixMatch = -1;
        for (int i = 0; i < catalog.Count; i++)
        {
            string name = catalog.GetModelName(i);
            if (string.IsNullOrEmpty(name))
                continue;

            if (string.Equals(name, modelName, System.StringComparison.OrdinalIgnoreCase))
                return i;

            if (suffixMatch < 0 && name.EndsWith(modelName, System.StringComparison.OrdinalIgnoreCase))
                suffixMatch = i;
        }

        return suffixMatch;
    }
}
