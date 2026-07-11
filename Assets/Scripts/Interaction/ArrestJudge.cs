using System;
using UnityEngine;

/// <summary>
/// 검거 판정 — 본부로 인계된 NPC의 실제 신원을 대조해 진범/오검거를 판정한다. (GDD 7-2, #41)
/// HqDropoffZone의 인계 이벤트를 구독해 자동 판정하고, 결과를 로그 + OnArrestJudged로 알린다.
/// 실제 자금 정산(#42)·오검거 페널티(GDD 7-3)·판정 UI(#43)는 이 이벤트를 구독해 후속 구현한다.
///
/// 범인 배정(CriminalAssigner)이 서버에서만 이뤄지고 아직 클라이언트에 동기화되지 않으므로(#52/#56 TODO),
/// 판정도 서버(또는 오프라인)에서만 수행한다 — 인계 NPC의 네트워크 권위로 게이트한다.
/// </summary>
// TODO: 본부 인계 상호작용(#40) 연결 시 자동 판정 대신 상호작용 시점에 Judge()를 호출하도록 전환.
//       네트워크 전환 시 판정 결과를 ClientRpc로 브로드캐스트해 본부 UI(#43)가 전 피어에서 표시.
public class ArrestJudge : MonoBehaviour
{
    /// <summary>현상수배범(진범) 검거 보상 (GDD 9-1).</summary>
    private const int k_wantedReward = 10000;

    /// <summary>오검거 보상 — 없음 (GDD 9-1: 보상 없는 대상이라 수익 0).</summary>
    private const int k_wrongfulReward = 0;

    /// <summary>경범죄(거수자 공무집행방해) 검거 보상 (GDD 9-1: 1,000원, #78).</summary>
    private const int k_misdemeanorReward = 1000;

    [Header("인계 구역 (비우면 씬에서 자동 탐색)")]
    [SerializeField] private HqDropoffZone m_dropoffZone;

    [Tooltip("인계 도달 시 자동 판정. 본부 인계 상호작용(#40) 도입 전까지 데모용으로 켜둔다")]
    [SerializeField] private bool m_autoJudgeOnDelivery = true;

    /// <summary>판정 완료 이벤트 — 자금 정산(#42)·오검거 페널티(GDD 7-3)·판정 UI(#43)가 구독한다.</summary>
    public event Action<ArrestResult> OnArrestJudged;

    private void Awake()
    {
        if (m_dropoffZone == null)
            m_dropoffZone = FindFirstObjectByType<HqDropoffZone>();
    }

    private void OnEnable()
    {
        if (m_dropoffZone != null)
            m_dropoffZone.OnNpcDelivered += HandleNpcDelivered;
    }

    private void OnDisable()
    {
        if (m_dropoffZone != null)
            m_dropoffZone.OnNpcDelivered -= HandleNpcDelivered;
    }

    private void HandleNpcDelivered(NpcController npc)
    {
        if (m_autoJudgeOnDelivery)
            Judge(npc);
    }

    /// <summary>
    /// 인계된 NPC를 판정한다. 판정에 성공하면 결과를 반환하고 OnArrestJudged를 발행한다.
    /// 클라이언트(서버 권위 밖)이거나 신원이 없어 판정할 수 없으면 null을 반환한다.
    /// </summary>
    public ArrestResult? Judge(NpcController npc)
    {
        if (npc == null)
            return null;

        // 서버 권위 — 범인 배정이 서버에서만 유효하므로 클라이언트는 판정하지 않는다 (#56 패턴)
        if (npc.IsSpawned && !npc.IsServer)
            return null;

        CitizenIdentity identity = npc.GetComponent<CitizenIdentity>();
        if (identity == null)
        {
            Debug.LogWarning($"ArrestJudge: 신원(CitizenIdentity) 없음 — 판정 불가: {npc.name}", npc);
            return null;
        }

        // 진범이면 수배 검거. 무고하더라도 도주·저항한 거수자면 경범죄(공무집행방해),
        // 순순히 따라온 무고자면 오검거다. (모델 B — 행위범, #78)
        ArrestVerdict verdict;
        if (identity.IsCriminal)
            verdict = ArrestVerdict.WantedCriminal;
        else if (identity.Reaction == ReactionType.Flee || identity.Reaction == ReactionType.Resist)
            verdict = ArrestVerdict.Misdemeanor;
        else
            verdict = ArrestVerdict.WrongfulArrest;

        int reward = verdict switch
        {
            ArrestVerdict.WantedCriminal => k_wantedReward,
            ArrestVerdict.Misdemeanor => k_misdemeanorReward,
            _ => k_wrongfulReward,
        };

        var result = new ArrestResult(npc, verdict, identity.Profile, reward, ResolveDeliverer(npc));

        LogVerdict(result);
        OnArrestJudged?.Invoke(result);
        return result;
    }

    /// <summary>이 NPC를 연행 중이던 플레이어를 찾는다 — 오검거 개인 기록(GDD 7-3)용. 못 찾으면 null.</summary>
    private static PlayerEscorter ResolveDeliverer(NpcController npc)
    {
        PlayerEscorter[] escorters = FindObjectsByType<PlayerEscorter>(FindObjectsSortMode.None);
        foreach (PlayerEscorter escorter in escorters)
            if (escorter.EscortingNpc == npc)
                return escorter;

        return null;
    }

    private static void LogVerdict(ArrestResult result)
    {
        string citizenName = result.Profile != null ? result.Profile.CitizenName : result.Npc.name;
        string tag = result.Verdict switch
        {
            ArrestVerdict.WantedCriminal => "현상수배범 검거",
            ArrestVerdict.Misdemeanor => "경범죄 검거",
            _ => "오검거",
        };
        string deliverer = result.DeliveredBy != null ? result.DeliveredBy.name : "알 수 없음";
        Debug.Log($"[검거 판정] {tag}: {citizenName} (인계: {deliverer}) — 보상 {result.Reward}원");
    }
}

/// <summary>검거 판정 결과 묶음. (#41)</summary>
public readonly struct ArrestResult
{
    /// <summary>판정된 NPC.</summary>
    public readonly NpcController Npc;

    /// <summary>판정 결과 — 진범/오검거.</summary>
    public readonly ArrestVerdict Verdict;

    /// <summary>인계 NPC의 실제 프로필. 신원 미배정이면 null.</summary>
    public readonly CitizenProfile Profile;

    /// <summary>이 검거로 발생하는 보상액(원) — 실제 정산은 #42가 담당.</summary>
    public readonly int Reward;

    /// <summary>인계한 플레이어 — 오검거 개인 기록(GDD 7-3)용. 못 찾으면 null.</summary>
    public readonly PlayerEscorter DeliveredBy;

    public ArrestResult(NpcController npc, ArrestVerdict verdict, CitizenProfile profile,
        int reward, PlayerEscorter deliveredBy)
    {
        Npc = npc;
        Verdict = verdict;
        Profile = profile;
        Reward = reward;
        DeliveredBy = deliveredBy;
    }
}
