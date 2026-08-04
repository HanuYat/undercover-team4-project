using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using UnityEngine;

/// <summary>
/// WrongfulArrestPenalty의 호송 파트 (#279) — 포획 접수와 광장 도착 후의 결말을 든다.
/// 본체(WrongfulArrestPenalty.cs)는 정책(카운트·수용·출동·매달기)을, 이 파일은 그 정책과 호송을 잇는다 —
/// partial이므로 상태(m_activeNpcs·m_carryTarget·광장 참조)는 그대로 공유한다.
///
/// <b>수렴·대형·끌기 연출 자체는 <see cref="CarryEscortSequence"/>로 빠졌다</b> (#371) — 납치 이벤트가
/// 같은 세 안전망(수렴 상한·이동 상한·대상 소실)을 쓰기 때문이다. 여기 남은 것은 오검거만의 판단이다:
/// 누가 페널티 독박을 쓰는지, 기능 정지된 몸은 접수하지 않는다는 규칙, 도착 후 30초 매달기.
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

        PlayerIncapacitation incap = caught.GetComponent<PlayerIncapacitation>();

        // 기능 정지(Die)된 몸은 접수하지 않는다 (#365). 추격 포획은 거리만 보므로(NpcChaseState) 바닥에
        // 쓰러진 몸도 잡히는데, 아래 Incapacitate는 Die를 덮지 못하게 막혀 있어도(#364) 호송·매달기는
        // 그 가드 밖이라 그대로 진행된다 — 상태만 Die로 둔 채 광장으로 순간이동하고, 본부 부활 장치에
        // 안치해 둔 몸이면 동료 눈앞에서 사라진다. 여기서 끊으면 끌기 연출과 m_carryTarget 점유
        // (다른 사람 페널티까지 막는다), Incapacitate의 경고 로그가 함께 정리된다.
        //
        // 페널티가 취소되는 것은 아니다 — 추격대는 해산하지 않고 계속 노리므로 부활한 뒤에 잡힌다.
        // 방치·격퇴와 같은 '유예'다 (7-3: 페널티는 미뤄질 뿐 사라지지 않는다).
        if (incap != null && incap.IsDead)
            return;

        m_carryTarget = caught;

        // 즉시 행동불능(구조 불가) — 매달기와 같은 계열의 무력화 (#101/#105).
        // 끌려가는 동안 걸어 나가지 못하게 하는 것이기도 하다.
        if (incap != null)
            incap.Incapacitate(IncapacitationCause.Penalty);

        PruneDead(m_activeNpcs);
        var convergers = new List<NpcController>(m_activeNpcs); // 포획 시점 스냅샷 — 이 호송의 수렴·해산 대상
        foreach (NpcController npc in convergers)
            npc.StartPenaltyConverge(caught);

        Debug.Log($"[오검거] 포획 — {catcher.name} → {caught.name}, {convergers.Count}명 수렴 시작");
        CarryToPlazaAsync(caught, convergers).Forget();
    }

    // 공용 호송 시퀀스에 광장을 목적지로 넘기고, 결말(30초 매달기)을 집행한다. (서버 전용)
    // convergers = 포획 시점 스냅샷 — 수렴·대형·해산 전부 이 목록 기준. m_activeNpcs 전체가 아니다.
    private async UniTask CarryToPlazaAsync(Transform caught, List<NpcController> convergers)
    {
        var settings = new CarryEscortSequence.Settings(
            m_convergeArriveDistance,
            m_convergeTimeoutSeconds,
            k_carrierGap,
            k_plazaArriveDistance,
            k_carryTravelTimeoutSeconds);

        bool arrived = await CarryEscortSequence.RunAsync(
            caught, convergers, m_plazaPoint, settings, destroyCancellationToken);

        // 정리는 도착·중단 무관하게 같다 — 수렴분만 시민으로 복귀시키고 처리 중 표시를 지운다.
        // 호송 중 새로 출동한 추격대(m_activeNpcs에는 있지만 convergers에는 없음)는 계속 추격한다.
        ReleaseAll(convergers);
        m_carryTarget = null;

        if (!arrived)
        {
            Debug.Log("[오검거] 호송 중단 — 대상 소실");
            return;
        }

        // 광장 스냅 + 30초 매달기 (#101 로직 재사용) — 길이 막혀 도착하지 못한 경우의 보정도 여기가 한다.
        if (caught != null)
            await HangAsync(caught);
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
}
