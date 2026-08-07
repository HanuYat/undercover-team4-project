using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 검거 판정 — 확보한 신병의 실제 신원을 대조해 진범/오검거를 판정한다. (GDD 7-2, #41)
/// <see cref="JailIntake"/>가 문 앞 E로 신병을 넣는 순간
/// <see cref="Judge"/>를 부르면 판정하고, 결과를 로그 + OnArrestJudged로 알린다.
/// 판정 장소 이력: 인계존 도달 자동 판정(#59) → 인계 단말 E(#414) →
/// <b>유치장 앞 보안 스캐너(#492)</b>. 중간에 '유치장 문턱(Jail 영역 진입)'을 거쳤는데, 유치장
/// <b>안</b>에서 확정된 오검거가 통행을 잃고 그 자리에 굳어 문 밖 게이트로 다시 물러났다.
/// 실제 자금 정산(#42)·오검거 페널티(GDD 7-3)·판정 UI(#43)는 이 이벤트를 구독해 후속 구현한다.
///
/// 범인 배정(CriminalAssigner)이 서버에서만 이뤄지고 아직 클라이언트에 동기화되지 않으므로(#52/#56 TODO),
/// 판정도 서버(또는 오프라인)에서만 수행한다 — 인계 NPC의 네트워크 권위로 게이트한다.
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class ArrestJudge : CommonManagerBase
{
    private const int k_wrongfulReward = 0;

    private RoundManager Round => App.Game.Round;

    // 진범·위조범 보상은 여기서 정하지 않는다 (#395) — NPC마다 다른 현상금을 CriminalAssigner가
    // 라운드 시작에 뽑아 CitizenIdentity.Bounty에 확정해 두고, 판정은 그 값을 읽기만 한다.
    // 판정 시점에 뽑으면 재판정(#358)·탈옥 후 재검거(#231)로 금액을 리롤할 수 있게 된다.

    public event Action<ArrestResult> OnArrestJudged;

    protected override void Awake()
    {
        base.Awake(); // App.Game.ArrestJudge 등록
    }

    // 판정 완료 표식은 NpcCustody.IsDelivered가 들고 있다 (#230) — NPC와 수명을 같이하므로
    // 씬 전환·라운드 재시작 시 수동으로 비울 static 상태가 없다.
    // (App 등록 해제는 베이스 OnDestroy가 처리 — 여기서 오버라이드할 것이 없다)

    /// <summary>
    /// 판정 — 대상의 신원을 대조해 진범/경범죄/오검거를 확정한다. 서버(또는 오프라인) 전용.
    ///
    /// <b>호출 시점은 <see cref="JailIntake"/>가 쥔다</b> (#492): 확보한 신병이 판정 게이트를 지나는
    /// 순간 한 번 부른다. 상태·구역 검증은 그쪽이 이미 끝냈으므로 여기서 다시 하지 않는다 —
    /// 예전 인계 단말 경로의 TryDeliver(상태·구역 재검증)는 단말과 함께 제거됐다.
    ///
    /// 재판정(#358)은 그대로 허용되지만 <b>그 판단은 여기가 아니다</b>: 같은 통과에 매 틱 다시
    /// 판정되지 않게 거르는 것은 JailIntake의 통과 기록이고, 게이트를 벗어나면 그 기록이 지워져
    /// 재판정이 열린다. 반출한 수감자를 다시 앉히려면 게이트를 한 번 더 지나야 한다.
    /// 반출은 <c>ClearDelivered</c>를 부르지 않는다 — 그건 탈옥 전용이다(JailIntake 주석).
    /// 재판정 후처리 중복은 <see cref="ArrestResult.IsFirstDelivery"/>가 건다.
    /// </summary>
    /// <param name="presser">
    /// 수감 버튼을 누른 플레이어 — 밧줄을 걸고 있지 않아도 <b>인계자에 포함</b>된다. (#537)
    ///
    /// 예전에는 인계자를 밧줄에서만 뽑았다. 신병을 끌고 유치장까지 들어가는 것이 곧 인계였기
    /// 때문이다. 문 앞 버튼으로 바뀌면서(#537) <b>줄을 풀어 문 앞에 놓고 누르는 것이 정상 경로</b>가
    /// 됐고, 그때 밧줄 목록이 비어 판정 배너(<see cref="ArrestVerdictFeedback"/>)가 보여줄 대상을
    /// 잃고 조용히 스킵됐다 — 누른 사람에게 결과가 안 뜬다.
    ///
    /// 오검거 페널티도 이 목록을 대상으로 삼으므로, 누른 사람이 함께 책임진다 —
    /// 무고한 시민을 넣은 것은 버튼을 누른 손이다. 인계 몫(#484)의 귀속 기준과도 같다.
    /// </param>
    public ArrestResult? Judge(NpcController npc, PlayerEscorter presser = null)
    {
        if (npc == null) return null;
        if (npc.IsSpawned && !npc.IsServer) return null;

        // 라운드 진행 중에만 판정한다. 준비 중(Preparing)에는 먼저 입장한 플레이어가 남들이 로딩하는
        // 사이에 검거해 진행도를 벌어둘 수 있고, 종료 후(Ended)에는 정산이 이미 스냅샷된 뒤다.
        // 판정을 막으면 MarkDelivered도 서지 않아 라운드가 시작된 뒤 정상적으로 다시 판정된다.
        // (RoundManager가 없는 단독 테스트 씬은 게이트하지 않는다 — MisdemeanorLoiterer와 같은 관례)
        if (Round != null && Round.Phase != RoundPhase.InProgress)
            return null;

        // 경범죄 이벤트 NPC(난동꾼)는 신원 대조 이전에 마커로 식별한다 (#106).
        MisdemeanorOffender misdemeanor = npc.GetComponent<MisdemeanorOffender>();
        CitizenIdentity identity = npc.GetComponent<CitizenIdentity>();

        // 경범죄 마커도 신원도 없으면 판정할 수 없다.
        if (misdemeanor == null && identity == null)
        {
            Debug.LogWarning($"ArrestJudge: 신원(CitizenIdentity) 없음 — 판정 불가: {npc.name}", npc);
            return null;
        }

        // 첫 인계 여부를 표식 세우기 전에 잡아 둔다 — 할당량·오검거 카운트가 재판정으로 부풀지 않게 (#358).
        bool firstDelivery = !npc.Custody.IsDelivered;

        // 판정 완료로 표시 — 방치 도주 타이머(#230)를 멈춘다. 재판정 자체는 허용하므로(#358)
        // 여기서 중복을 막지 않는다. 막는 것은 <b>부르는 쪽</b>이다: JailIntake가 게이트 통과당
        // 한 번만 부른다(m_judgedThisPass). 판정이 E 입력에서 폴링으로 바뀌었으므로(#492) "누른
        // 횟수만큼만 판정된다"는 옛 근거(#414)는 더 이상 성립하지 않는다.
        npc.Custody.MarkDelivered();

        ArrestVerdict verdict;
        int reward;
        if (misdemeanor != null)
        {
            // 난동꾼 즉결 처리 — 진범/오검거 대조를 타지 않고 경범죄로 확정, 이벤트가 정한 수익을 준다.
            // reward는 지급이 아니라 "유치장 수감 시 실릴 정산 bounty"다 — 실제 자금은 라운드 종료 시
            // 유치장 점유로 1회 정산된다(#340). 그래서 재검거 중복지급 방지용 Reward 비우기는 필요 없다
            // (점유를 한 번만 세므로) — 오히려 비우면 재수감된 난동꾼이 0으로 잡혀 정산에서 누락된다.
            verdict = ArrestVerdict.Misdemeanor;
            reward = misdemeanor.Reward;
        }
        else if (identity.IsCriminal)
        {
            // 진범 우선 — 진범이면서 위조범인 NPC도 현상수배범으로 판정한다 (위조 판정에 가려지지 않음, #320).
            verdict = ArrestVerdict.WantedCriminal;
            reward = ResolveBounty(identity, npc);
        }
        else if (identity.IsForger)
        {
            // 위조범 — 난동꾼과 동일한 즉결 경범죄로 확정하고 소액 위조 보상을 준다 (#320).
            verdict = ArrestVerdict.Misdemeanor;
            reward = ResolveBounty(identity, npc);
        }
        else
        {
            verdict = ArrestVerdict.WrongfulArrest;
            reward = k_wrongfulReward;
        }

        CitizenProfile profile = identity != null ? identity.Profile : null;

        // 줄다리기로 여러 명이 함께 끌고 왔을 수 있다 (#390) — 관여한 전원이 인계자다.
        // 오검거 페널티가 이 목록 전원에게 걸린다: 밧줄이 걸린 채 유치장까지 들어갔다는 것은
        // 막지 못했다는 뜻이고, 손을 떼는 수단(E 놓고 걸어가 줄 끊기 / 자기 줄 풀기)이 양쪽에 있다.
        // 끌고 있지 않아도 줄이 이어져 있으면 포함된다 — 유치장 안에 내려놓은 뒤 판정되는 경로(#492)에서도
        // 인계자가 '알 수 없음'이 되지 않는다.
        List<PlayerEscorter> deliverers = PlayerEscorter.FindEscortersOf(npc);

        // 버튼을 누른 사람도 인계자다 (#537) — 줄을 풀어 놓고 누르는 경로에서는 이 사람이 유일하다.
        // 중복은 거른다: 자기 줄에 걸어 끌고 온 사람이 그대로 누르는 것이 가장 흔한 경우다.
        if (presser != null && !deliverers.Contains(presser))
            deliverers.Add(presser);

        var result = new ArrestResult(npc, verdict, profile, reward, deliverers, firstDelivery);

        LogVerdict(result);

        // 밧줄 해제는 <b>오검거에만</b> 건다 (#492).
        //
        // 수감 판정(현상수배범·경범죄)은 판정 후에도 묶인 채 남아야 한다 — 플레이어가 좌석까지
        // 끌고 가 E로 놓을 때 앉기 때문이다. 여기서 풀면 유치장 문을 통과하는 순간 Captured가 되어
        // JailIntake가 즉시 착석시켜 버린다.
        //
        // 오검거는 반대로 <b>반드시 여기서 풀어야 한다</b>. 아래 OnArrestJudged의 구독자
        // WrongfulArrestPenalty가 그 자리에서 원한 구역으로 전이시키는데(SendToDetention),
        // 밧줄이 걸린 동안은 NavMeshAgent가 꺼져 있어(StartRopeDrag) 전이한 상태의 Enter가
        // 죽은 에이전트에 목적지를 걸고 조용히 실패한다 — 시민이 묶인 채 굳는다.
        // TickTetherCleanup은 목록에서만 빼고 앵커는 떼지 않으므로 뒤늦게 풀어도 이미 늦다.
        // 그래서 순서가 강제다: 해제 → 이벤트 발행.
        if (verdict == ArrestVerdict.WrongfulArrest)
        {
            // 서버 권위로 즉시 푼다 — RequestRelease는 클라 오너 권한이 필요해 비호스트 검거에서 동작하지 않는다.
            // 판정된 그 NPC의 줄만 전원에게서 푼다 — 같이 끌고 온 다른 대상은 계속 끌린다 (#390).
            if (deliverers.Count > 0)
            {
                foreach (PlayerEscorter deliverer in deliverers)
                    deliverer.ReleaseDrag(npc);
            }
            else
            {
                npc.Custody.StopEscort();
            }
        }

        // 수갑 회수(#307/#229)는 제거됐다 — 밧줄은 소모형이 아니라 NPC에 채워둔 자원이 없다. (#369)

        OnArrestJudged?.Invoke(result);

        return result;
    }

    // 배정된 현상금을 읽는다 (#395). 0이면 CriminalAssigner의 배정을 타지 않은 NPC라는 뜻이라 —
    // 라운드 목표(금액)가 조용히 미달로 흐르지 않게 경고를 남긴다. 값 자체는 그대로 쓴다.
    private static int ResolveBounty(CitizenIdentity identity, NpcController npc)
    {
        if (identity.Bounty <= 0)
            Debug.LogWarning($"ArrestJudge: {npc.name}에 현상금이 배정되지 않아 0원으로 판정한다 — CriminalAssigner 배정을 타지 않은 NPC인지 확인할 것", npc);

        return identity.Bounty;
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
        string deliverer = result.DeliveredBy.Count > 0
            ? string.Join(", ", result.DeliveredBy.ConvertAll(e => e.name))
            : "알 수 없음";
        Debug.Log($"[검거 판정] {tag}: {citizenName} (인계: {deliverer}) — 보상 {result.Reward}원");
    }
}

public readonly struct ArrestResult
{
    public readonly NpcController Npc;
    public readonly ArrestVerdict Verdict;
    public readonly CitizenProfile Profile;
    public readonly int Reward;

    /// <summary>이 대상에 밧줄을 걸고 유치장까지 들어온 플레이어 전원 — 아무도 없으면 빈 목록. (#390)
    /// 반출한 수감자가 밧줄 없이 따라 들어와 재판정되는 경로(#492)가 그 빈 목록의 실제 사례다.
    /// 줄다리기로 여러 명이 함께 끌 수 있어 단일 참조에서 목록이 됐다. 검거에 개인 보상은 없고
    /// (팀 자금은 라운드 종료에 유치장 점유로 1회 정산, #340) 이 목록은 <b>페널티 지정</b>에 쓰인다 —
    /// 오검거 개인 카운트와 추격대 대상이 여기서 나온다.</summary>
    public readonly List<PlayerEscorter> DeliveredBy;

    // 이 판정이 첫 인계인지 — 재판정(같은 대상을 유치장에 다시 넣음)이면 false. 할당량·오검거 카운트처럼
    // 1회만 세어야 하는 후처리가 이 값으로 재판정을 걸러 낸다. 탈옥(ClearDelivered) 후 재검거는 다시 true. (#358)
    public readonly bool IsFirstDelivery;

    public ArrestResult(NpcController npc, ArrestVerdict verdict, CitizenProfile profile,
        int reward, List<PlayerEscorter> deliveredBy, bool isFirstDelivery)
    {
        Npc = npc;
        Verdict = verdict;
        Profile = profile;
        Reward = reward;
        DeliveredBy = deliveredBy ?? new List<PlayerEscorter>();
        IsFirstDelivery = isFirstDelivery;
    }
}