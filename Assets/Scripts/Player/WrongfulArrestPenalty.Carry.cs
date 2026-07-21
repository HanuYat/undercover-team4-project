using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using UnityEngine;

/// <summary>
/// WrongfulArrestPenalty의 호송 연출 파트 (#279) — 포획 접수부터 광장 도착까지의 수렴·대형·끌기 시퀀스.
/// 본체(WrongfulArrestPenalty.cs)는 정책(카운트·수용·출동·매달기)을, 이 파일은 연출 오케스트레이션을 든다 —
/// partial이므로 상태(m_activeNpcs·m_carryTarget·광장 참조)는 그대로 공유한다. 이동만 했고 동작 변화는 없다.
/// </summary>
public partial class WrongfulArrestPenalty
{
    // 추격 NPC의 포획 통보 — 잡힌 플레이어가 페널티 독박: 포획 시점의 미해소 NPC 스냅샷이 수렴한다.
    // 스냅샷 이후(호송 중) 새로 출동한 추격대는 건드리지 않는다 — 자기 사냥을 계속하다가
    // 이 호송이 끝난 뒤의 포획부터 다시 접수된다 (조기 해산 방지, 리뷰 반영).
    private void HandlePenaltyCaught(NpcController catcher, Transform caught)
    {
        if (m_carryTarget != null)
            return; // 이미 호송 처리 중 — 추격 상태가 간격을 두고 재통보하므로 다음 기회에 처리된다
        if (caught == null)
            return;

        m_carryTarget = caught;

        // 즉시 행동불능(구조 불가) — 매달기와 같은 계열의 무력화 (#101/#105)
        PlayerIncapacitation incap = caught.GetComponent<PlayerIncapacitation>();
        if (incap != null)
            incap.Incapacitate(revivable: false);

        PruneDead(m_activeNpcs);
        var convergers = new List<NpcController>(m_activeNpcs); // 포획 시점 스냅샷 — 이 호송의 수렴·해산 대상
        foreach (NpcController npc in convergers)
            npc.StartPenaltyConverge(caught);

        Debug.Log($"[오검거] 포획 — {catcher.name} → {caught.name}, {convergers.Count}명 수렴 시작");
        CarrySequenceAsync(caught, convergers).Forget();
    }

    // 수렴 대기 → 대형 편성(2명 양옆 끌기 + 뒤따름) → 광장 도착 → 30초 매달기. (서버 전용)
    // convergers = 포획 시점 스냅샷 — 수렴·대형·해산 전부 이 목록 기준. m_activeNpcs 전체가 아니다.
    private async UniTask CarrySequenceAsync(Transform caught, List<NpcController> convergers)
    {
        // ---- 수렴 대기: 전원이 모이거나 상한이 지날 때까지 — "다 모여야 끌기 시작" (#276 확정)
        float deadline = Time.time + m_convergeTimeoutSeconds;
        while (Time.time < deadline)
        {
            PruneDead(convergers);
            if (caught == null || convergers.Count == 0)
            {
                AbortCarry(convergers);
                return;
            }

            if (AllWithin(convergers, caught.position, m_convergeArriveDistance + 1f))
                break;

            await UniTask.Delay(TimeSpan.FromSeconds(0.25), cancellationToken: destroyCancellationToken);
        }

        PruneDead(convergers);
        if (caught == null || convergers.Count == 0)
        {
            AbortCarry(convergers);
            return;
        }

        // ---- 대형 편성: 가장 가까운 2명이 양옆 끌기, 나머지는 뒤따름 (#279)
        Vector3 caughtPos = caught.position;
        convergers.Sort(
            (a, b) => (a.transform.position - caughtPos).sqrMagnitude
                .CompareTo((b.transform.position - caughtPos).sqrMagnitude));

        NpcController carrierA = convergers[0];
        NpcController carrierB = convergers.Count > 1 ? convergers[1] : null;

        carrierA.StartPenaltyEscort(m_plazaPoint, null, Vector3.zero);
        if (carrierB != null)
            carrierB.StartPenaltyEscort(m_plazaPoint, carrierA, new Vector3(k_carrierGap, 0f, 0f));

        for (int i = 2; i < convergers.Count; i++)
        {
            // 뒤따름 대형 — 좌우 지그재그로 한 줄씩 뒤에 선다
            float x = i % 2 == 0 ? -0.9f : 0.9f;
            float z = -(1.8f + (i - 2) / 2 * 1.2f);
            convergers[i].StartPenaltyEscort(m_plazaPoint, carrierA, new Vector3(x, 0f, z));
        }

        // 플레이어 본인은 오너 클라가 끌기 담당 2명 사이를 추종한다 — NetworkTransform 오너 권한 (#279)
        PlayerPenaltyView view = caught.GetComponent<PlayerPenaltyView>();
        if (view != null)
            view.StartCarried(carrierA, carrierB != null ? carrierB : carrierA);

        Debug.Log($"[오검거] 호송 시작 — 끌기 {carrierA.name}{(carrierB != null ? "·" + carrierB.name : "")}, 총 {convergers.Count}명");

        // ---- 광장 도착 대기 — 선두 기준. 선두 소실·광장 미배선·상한 초과면 스냅 텔레포트(HangAsync)가 보정한다
        float travelDeadline = Time.time + k_carryTravelTimeoutSeconds;
        while (Time.time < travelDeadline)
        {
            if (caught == null)
            {
                AbortCarry(convergers);
                return;
            }
            if (carrierA == null || m_plazaPoint == null)
                break;
            if (Vector3.Distance(carrierA.transform.position, m_plazaPoint.position) <= k_plazaArriveDistance)
                break;

            await UniTask.Delay(TimeSpan.FromSeconds(0.25), cancellationToken: destroyCancellationToken);
        }

        // ---- 종료: 끌기 해제 → 수렴분만 시민 복귀 → 광장 스냅 + 30초 매달기 (#101 로직 재사용)
        // 호송 중 새로 출동한 추격대(m_activeNpcs에는 있지만 convergers에는 없음)는 계속 추격한다.
        if (view != null)
            view.StopCarried();
        ReleaseAll(convergers);
        m_carryTarget = null;

        if (caught != null)
            await HangAsync(caught);
    }

    // 호송 중단(대상 소실 등) — 수렴 중이던 NPC만 시민으로 복귀시키고 처리 중 표시를 지운다.
    private void AbortCarry(List<NpcController> convergers)
    {
        ReleaseAll(convergers);
        m_carryTarget = null;
        Debug.Log("[오검거] 호송 중단 — 대상 소실");
    }

    // ---- 호송 전용 정리 헬퍼 ----

    // 페널티 임무 해제 — 이벤트 구독을 풀고 시민(배회)으로 복귀시킨다. 미해소 목록에서도 뺀다.
    private void ReleaseNpc(NpcController npc)
    {
        if (npc != null)
        {
            npc.OnPenaltyCaught -= HandlePenaltyCaught;
            npc.EndPenaltyDuty();
        }

        m_activeNpcs.Remove(npc);
    }

    // 목록의 NPC 전원 임무 해제 — 뒤에서부터 지워 ReleaseNpc의 m_activeNpcs.Remove와 안전하게 공존한다.
    private void ReleaseAll(List<NpcController> npcs)
    {
        for (int i = npcs.Count - 1; i >= 0; i--)
        {
            ReleaseNpc(npcs[i]);
            npcs.RemoveAt(i);
        }
    }

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
