using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 검거 판정 — 본부로 인계된 NPC의 실제 신원을 대조해 진범/오검거를 판정한다. (GDD 7-2, #41)
/// HqDropoffZone의 인계 이벤트를 구독해 자동 판정하고, 결과를 로그 + OnArrestJudged로 알린다.
/// 실제 자금 정산(#42)·오검거 페널티(GDD 7-3)·판정 UI(#43)는 이 이벤트를 구독해 후속 구현한다.
///
/// 범인 배정(CriminalAssigner)이 서버에서만 이뤄지고 아직 클라이언트에 동기화되지 않으므로(#52/#56 TODO),
/// 판정도 서버(또는 오프라인)에서만 수행한다 — 인계 NPC의 네트워크 권위로 게이트한다.
/// </summary>
public class ArrestJudge : MonoBehaviour
{
    private const int k_wantedReward = 10000;
    private const int k_wrongfulReward = 0;

    [Header("인계 구역 (비우면 씬에서 자동 탐색)")]
    [SerializeField] private HqDropoffZone m_dropoffZone;

    [Tooltip("인계 도달 시 자동 판정. 본부 인계 상호작용(#40) 도입 전까지 데모용으로 켜둔다")]
    [SerializeField] private bool m_autoJudgeOnDelivery = true;

    public event Action<ArrestResult> OnArrestJudged;

    // [수정] static으로 선언하여 HqDropoffZone 등 외부에서 쉽게 검사할 수 있도록 공개 (블랙리스트)
    public static HashSet<NpcController> JudgedNpcs { get; } = new HashSet<NpcController>();

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

    public ArrestResult? Judge(NpcController npc)
    {
        if (npc == null) return null;
        if (npc.IsSpawned && !npc.IsServer) return null;

        // 혹시 모를 중복 진입 방어
        if (JudgedNpcs.Contains(npc)) return null;

        CitizenIdentity identity = npc.GetComponent<CitizenIdentity>();
        if (identity == null)
        {
            Debug.LogWarning($"ArrestJudge: 신원(CitizenIdentity) 없음 — 판정 불가: {npc.name}", npc);
            return null;
        }

        // [핵심] 판정이 시작되면 즉시 판정 완료 명단에 추가
        JudgedNpcs.Add(npc);

        ArrestVerdict verdict = identity.IsCriminal
            ? ArrestVerdict.WantedCriminal
            : ArrestVerdict.WrongfulArrest;
        int reward = verdict == ArrestVerdict.WantedCriminal ? k_wantedReward : k_wrongfulReward;

        PlayerEscorter deliverer = ResolveDeliverer(npc);
        var result = new ArrestResult(npc, verdict, identity.Profile, reward, deliverer);

        LogVerdict(result);
        OnArrestJudged?.Invoke(result);

        // 연행 상태 물리적 해제 (플레이어에게서 분리)
        if (deliverer != null)
            deliverer.RequestRelease();
        else
            npc.StopEscort();

        return result;
    }

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
        string tag = result.Verdict == ArrestVerdict.WantedCriminal ? "현상수배범 검거" : "오검거";
        string deliverer = result.DeliveredBy != null ? result.DeliveredBy.name : "알 수 없음";
        Debug.Log($"[검거 판정] {tag}: {citizenName} (인계: {deliverer}) — 보상 {result.Reward}원");
    }
}

public readonly struct ArrestResult
{
    public readonly NpcController Npc;
    public readonly ArrestVerdict Verdict;
    public readonly CitizenProfile Profile;
    public readonly int Reward;
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
