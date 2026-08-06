using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

/// <summary>
/// NPC 여럿이 플레이어 한 명을 붙잡아 목적지까지 끌고 가는 <b>연출 시퀀스</b>. 서버(또는 오프라인) 전용. (#279 → #371)
///
/// 오검거 페널티(<see cref="WrongfulArrestPenalty"/>)의 광장 호송에서 뽑아낸 공용 부분이다. 뽑은 이유는
/// 납치 이벤트(#371)가 <b>같은 세 안전망</b>을 쓰기 때문이다 — 수렴 상한·이동 상한·대상 소실 처리를
/// 두 곳에서 각각 관리하면 한쪽만 고쳐지고 다른 쪽은 조용히 남는다.
///
/// <b>이 시퀀스가 책임지는 것은 "끌고 가서 도착했는가"까지다.</b> 무엇을 목적지로 삼고, 도착하면
/// 무엇을 할지(오검거는 30초 매달기 · 납치는 그 자리에서 방치), 임무를 어떻게 해제할지는 부르는 쪽이
/// 정한다. 그래서 이 클래스는 오검거도 납치도 모른다.
///
/// <b>행동불능은 여기서 걸지 않는다</b> — 포획을 접수하는 쪽이 이미 걸어 둔다(끌려가는 동안 걸어
/// 나가지 못하게). 해제 시점이 용도마다 다르므로(오검거는 매달기 뒤, 납치는 도착 즉시) 여기서 만지지 않는다.
/// </summary>
public static class CarryEscortSequence
{
    /// <summary>호송 대형·타임아웃 값 묶음 — 용도마다 다르므로 부르는 쪽이 넘긴다.</summary>
    public readonly struct Settings
    {
        /// <summary>이 거리(m) 안에 전원이 모이면 끌기를 시작한다.</summary>
        public readonly float ConvergeArriveDistance;

        /// <summary>수렴 대기 상한(초) — 길이 막힌 NPC가 있어도 이 시간이 지나면 모인 인원으로 시작한다.</summary>
        public readonly float ConvergeTimeoutSeconds;

        /// <summary>양옆 끌기 담당의 선두 기준 좌우 간격(m).</summary>
        public readonly float CarrierGap;

        /// <summary>선두가 목적지에 닿았다고 볼 거리(m).</summary>
        public readonly float ArriveDistance;

        /// <summary>이동 상한(초) — 넘으면 도착하지 못한 것으로 보고 끝낸다(부르는 쪽이 스냅으로 보정).</summary>
        public readonly float TravelTimeoutSeconds;

        public Settings(
            float convergeArriveDistance,
            float convergeTimeoutSeconds,
            float carrierGap,
            float arriveDistance,
            float travelTimeoutSeconds)
        {
            ConvergeArriveDistance = convergeArriveDistance;
            ConvergeTimeoutSeconds = convergeTimeoutSeconds;
            CarrierGap = carrierGap;
            ArriveDistance = arriveDistance;
            TravelTimeoutSeconds = travelTimeoutSeconds;
        }
    }

    // 수렴 여부를 볼 때 도착 거리에 더하는 여유(m) — 정확히 그 거리에 서지 않아도 "모였다"로 본다.
    private const float k_convergeSlack = 1f;

    // 뒤따름 대형: 좌우 지그재그 폭(m)과 첫 줄까지의 뒤 간격·줄 간격(m).
    private const float k_followSideOffset = 0.9f;
    private const float k_followFirstRowBack = 1.8f;
    private const float k_followRowGap = 1.2f;

    /// <summary>
    /// 수렴 대기 → 대형 편성 → 목적지 도착 대기까지 진행한다.
    ///
    /// <paramref name="carriers"/>는 <b>이 시퀀스가 직접 정리한다</b>(파괴된 대상 제거) — 부르는 쪽은
    /// 끝난 뒤 이 목록을 그대로 임무 해제에 쓰면 된다. 목록 자체를 비우지는 않는다.
    /// </summary>
    /// <returns>목적지에 도착했으면 true. 대상·NPC 소실로 중단됐으면 false.
    /// 이동 상한을 넘긴 경우도 true다 — 끌고 가긴 했으니 부르는 쪽이 결말을 집행해야 한다.</returns>
    public static async UniTask<bool> RunAsync(
        Transform target,
        List<NpcController> carriers,
        Transform destination,
        Settings settings,
        CancellationToken token)
    {
        if (target == null || carriers == null || carriers.Count == 0)
            return false;

        // ---- 수렴 대기: 전원이 모이거나 상한이 지날 때까지 — "다 모여야 끌기 시작"
        float convergeDeadline = Time.time + settings.ConvergeTimeoutSeconds;
        while (Time.time < convergeDeadline)
        {
            PruneDead(carriers);
            if (target == null || carriers.Count == 0)
                return false;

            if (AllWithin(carriers, target.position, settings.ConvergeArriveDistance + k_convergeSlack))
                break;

            await UniTask.Delay(TimeSpan.FromSeconds(0.25), cancellationToken: token);
        }

        PruneDead(carriers);
        if (target == null || carriers.Count == 0)
            return false;

        // ---- 대형 편성: 가장 가까운 2명이 양옆 끌기, 나머지는 뒤따름
        Vector3 targetPosition = target.position;
        carriers.Sort(
            (a, b) => (a.transform.position - targetPosition).sqrMagnitude
                .CompareTo((b.transform.position - targetPosition).sqrMagnitude));

        NpcController carrierA = carriers[0];
        NpcController carrierB = carriers.Count > 1 ? carriers[1] : null;

        carrierA.Penalty.StartPenaltyEscort(destination, null, Vector3.zero);
        if (carrierB != null)
            carrierB.Penalty.StartPenaltyEscort(destination, carrierA, new Vector3(settings.CarrierGap, 0f, 0f));

        for (int i = 2; i < carriers.Count; i++)
        {
            float x = i % 2 == 0 ? -k_followSideOffset : k_followSideOffset;
            float z = -(k_followFirstRowBack + (i - 2) / 2 * k_followRowGap);
            carriers[i].Penalty.StartPenaltyEscort(destination, carrierA, new Vector3(x, 0f, z));
        }

        // 플레이어 본인은 오너 클라가 끌기 담당 2명 사이를 추종한다 — NetworkTransform 오너 권한
        PlayerPenaltyView view = target.GetComponent<PlayerPenaltyView>();
        if (view != null)
            view.StartCarried(carrierA, carrierB != null ? carrierB : carrierA);

        // ---- 도착 대기 — <b>아직 임무 중인</b> 끌기 담당 기준. 담당 소실·목적지 미배선·상한 초과는
        // '도착'으로 보고 넘긴다: 그래야 부르는 쪽이 결말(스냅 보정 포함)을 집행해 플레이어가
        // 끌려가는 상태로 남지 않는다.
        //
        // 선두 고정이 아니라 매 틱 다시 고르는 이유는 납치 구조(#371)의 2단계 때문이다 — 둘 중 하나만
        // 떼어내면 남은 하나가 계속 끌고 가야 하는데, 선두만 보면 그가 떨어진 순간 도착 판정이
        // 배회하는 시민의 위치를 재게 되어 이동 상한까지 흘러간다.
        bool arrived = true;
        float travelDeadline = Time.time + settings.TravelTimeoutSeconds;
        NpcController boundLead = carrierA;
        NpcController boundMate = carrierB != null ? carrierB : carrierA;

        while (Time.time < travelDeadline)
        {
            if (target == null)
            {
                arrived = false; // 대상 소실 — 결말을 집행할 상대가 없다
                break;
            }
            if (destination == null)
                break;

            // 끌던 NPC 전원이 임무를 벗었다 — 호송이 해체됐다(납치 구조 #371, 라운드 정리 등).
            // EndPenaltyDuty가 목적지 참조를 지우므로 그것으로 판별한다. 여기서 끊지 않으면
            // 아무도 끌지 않는데 플레이어가 이동 상한(수십 초)까지 끌려가는 자세로 남는다.
            NpcController lead = OnDuty(carrierA) ? carrierA : (OnDuty(carrierB) ? carrierB : null);
            if (lead == null)
            {
                arrived = false;
                break;
            }

            // 담당이 하나로 줄었으면 끌기 표현도 다시 물린다 — 플레이어는 두 담당의 중점을 따라가므로,
            // 그대로 두면 떨어져 나간 쪽으로 몸이 계속 딸려간다. 1명일 때 같은 NPC를 두 번 넘기는 것은
            // StartCarried의 관례다(추종 중점이 그 NPC 위치가 된다).
            NpcController mate = OnDuty(carrierB) && lead != carrierB ? carrierB : lead;
            if (view != null && (boundLead != lead || boundMate != mate))
            {
                boundLead = lead;
                boundMate = mate;
                view.StartCarried(lead, mate);
            }

            if (Vector3.Distance(lead.transform.position, destination.position) <= settings.ArriveDistance)
                break;

            await UniTask.Delay(TimeSpan.FromSeconds(0.25), cancellationToken: token);
        }

        // 끌기 표현은 시작한 쪽에서 끝낸다 — 짝을 나누면 호출자가 잊었을 때 플레이어가 계속 딸려간다.
        if (view != null)
            view.StopCarried();

        return arrived;
    }

    // 아직 이 호송에 묶여 있는가 — EndPenaltyDuty가 PenaltyEscortGoal을 지운다.
    private static bool OnDuty(NpcController npc) =>
        npc != null && npc.Penalty.PenaltyEscortGoal != null;

    private static void PruneDead(List<NpcController> list) => list.RemoveAll(npc => npc == null);

    private static bool AllWithin(List<NpcController> npcs, Vector3 center, float radius)
    {
        float sqr = radius * radius;
        foreach (NpcController npc in npcs)
        {
            if (npc != null && (npc.transform.position - center).sqrMagnitude > sqr)
                return false;
        }

        return true;
    }
}
