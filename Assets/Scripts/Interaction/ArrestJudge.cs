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
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class ArrestJudge : CommonManagerBase
{
    private const int k_wantedReward = 10000;
    private const int k_wrongfulReward = 0;

    [Header("인계 구역 (비우면 씬에서 자동 탐색)")]
    [SerializeField] private HqDropoffZone m_dropoffZone;

    [Tooltip("인계 도달 시 자동 판정. 본부 인계 상호작용(#40) 도입 전까지 데모용으로 켜둔다")]
    [SerializeField] private bool m_autoJudgeOnDelivery = true;

    public event Action<ArrestResult> OnArrestJudged;

    protected override void Awake()
    {
        base.Awake(); // App.Game.ArrestJudge 등록

        // HqDropoffZone은 장소 오브젝트라 App 대상이 아님 — 씬 탐색 유지 (같은 도메인 부품)
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

    // 판정 완료 표식은 NpcController.IsDelivered가 들고 있다 (#230) — NPC와 수명을 같이하므로
    // 씬 전환·라운드 재시작 시 수동으로 비울 static 상태가 없다.
    // (App 등록 해제는 베이스 OnDestroy가 처리 — 여기서 오버라이드할 것이 없다)

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
        if (npc.IsDelivered) return null;

        // 경범죄 이벤트 NPC(난동꾼)는 신원 대조 이전에 마커로 식별한다 (#106).
        MisdemeanorOffender misdemeanor = npc.GetComponent<MisdemeanorOffender>();
        CitizenIdentity identity = npc.GetComponent<CitizenIdentity>();

        // 경범죄 마커도 신원도 없으면 판정할 수 없다.
        if (misdemeanor == null && identity == null)
        {
            Debug.LogWarning($"ArrestJudge: 신원(CitizenIdentity) 없음 — 판정 불가: {npc.name}", npc);
            return null;
        }

        // [핵심] 판정이 시작되면 즉시 판정 완료로 표시 — 인계존 재진입 중복 판정과
        // 인계 방치 타이머(#230)를 함께 막는다
        npc.MarkDelivered();

        ArrestVerdict verdict;
        int reward;
        if (misdemeanor != null)
        {
            // 난동꾼 즉결 처리 — 진범/오검거 대조를 타지 않고 경범죄로 확정, 이벤트가 정한 수익을 준다.
            verdict = ArrestVerdict.Misdemeanor;
            reward = misdemeanor.Reward;
            // 보상은 첫 판정에만 — 경범죄자도 수감되면서(2026-07-23) 탈옥으로 풀려난 놈을 재검거하는
            // 경로가 생겼다. 마커를 지우지 않고 보상만 비우는 이유: 재검거가 오검거(페널티)로 판정되면
            // 다시 잡은 쪽이 손해를 보므로, 판정은 경범죄로 유지하되 수익만 반복되지 않게 한다.
            misdemeanor.Reward = 0;
        }
        else
        {
            verdict = identity.IsCriminal
                ? ArrestVerdict.WantedCriminal
                : ArrestVerdict.WrongfulArrest;
            reward = verdict == ArrestVerdict.WantedCriminal ? k_wantedReward : k_wrongfulReward;
        }

        CitizenProfile profile = identity != null ? identity.Profile : null;
        PlayerEscorter deliverer = PlayerEscorter.FindEscorterOf(npc);
        var result = new ArrestResult(npc, verdict, profile, reward, deliverer);

        LogVerdict(result);

        // 연행 상태 물리적 해제 (플레이어에게서 분리) — NPC는 Captured로 그 자리에 선다.
        // 반드시 OnArrestJudged보다 **먼저** 해야 한다: 구독자(CustodyRouter, #228)가 판정 결과에 따라
        // 다음 상태(유치장 이송·석방)로 전이시키는데, 해제를 뒤에 하면 StopEscort의 Captured 전이가
        // 그 행선지를 덮어써 NPC가 그 자리에 멈춰버린다.
        if (deliverer != null)
        {
            // [리뷰 반영] RequestRelease()는 클라이언트 오너 권한이 필요하므로,
            // 비호스트 유저 검거 시 동작하지 않습니다. 따라서 서버 권위로 즉시 풀어버리는 Release()를 호출합니다.
            deliverer.Release();
        }
        else
        {
            npc.StopEscort();
        }

        // 판정 완료 — 채워졌던 수갑을 연행자 인벤토리로 회수한다. 꽉 차 있으면 기존처럼 발밑 바닥에 떨군다 (#307).
        // 수감·석방 어느 쪽이든 공통 (#229). 연행 물리 해제와 같은 "판정 후 정리"라 여기(판정 funnel)에서 한다
        // — CustodyRouter는 유치장 씬에만 있을 수 있어(신병 라우팅 전용) 반환을 그쪽에 걸면 인계존만 있는 씬에서 누락된다.
        PlayerLoadout loadout = deliverer != null ? deliverer.GetComponent<PlayerLoadout>() : null;
        if (loadout == null || !loadout.TryRecoverHandcuffs(npc))
            npc.DropHandcuffs();

        OnArrestJudged?.Invoke(result);

        return result;
    }

    private static void LogVerdict(ArrestResult result)
    {
        string citizenName = result.Profile != null ? result.Profile.CitizenName : result.Npc.name;
        string tag = result.Verdict switch
        {
            ArrestVerdict.WantedCriminal => "현상수배범 검거",
            ArrestVerdict.Misdemeanor => "경범죄 처리",
            _ => "오검거"
        };
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