using System;
using Cysharp.Threading.Tasks;
using UnityEngine;

/// <summary>
/// AbductionEvent의 <b>포획 이후</b> 파트 (#371) — 접수·호송·방치·구조를 든다.
/// 본체(AbductionEvent.cs)는 발동 조건·스폰·수명을 들고, 이 파일이 그것과 호송을 잇는다 —
/// partial이므로 상태(m_abductors·m_carryTarget·인스펙터 값)는 그대로 공유한다.
/// 오검거(WrongfulArrestPenalty + .Carry)와 같은 가름이며, 수렴·대형·끌기 연출 자체는
/// 두 이벤트가 <see cref="CarryEscortSequence"/>를 함께 쓴다.
///
/// 여기 남은 것은 납치만의 판단이다: 끌려가는 중에도 떼어낼 수 있다는 것(구조),
/// 목적지가 광장이 아니라 가장 가까운 외곽이라는 것, 도착하면 매다는 대신 잠시 방치한다는 것.
/// </summary>
public partial class AbductionEvent
{
    // 타격 통보 — <b>구조</b>다. 한 대라도 맞으면 그 납치범은 이 호송에서 떨어진다 (#371).
    // 체력을 다 깎을 필요는 없다: 여기서 요구하는 것은 "떼어냈다"이지 "제압했다"가 아니고,
    // 진압봉 3대(0.9초 쿨다운)를 요구하면 이미 외곽에 도착해 있다. 제압·검거는 그 다음 선택지다.
    //
    // 데미지 소스를 가리지 않는다 — 폭발(BombDevice)에 휘말려 놓치는 것도, 동료가 오사(#461)로 맞춘
    // 것도 같은 결말이면 맞는다. 그래서 attacker는 보지 않는다(가해자를 가리면 "누가 구했나"를 따지는
    // 규칙이 하나 더 생기는데, 떼어내진 사실은 누가 때렸든 같다).
    private void HandleAbductorDamaged(NpcController abductor, GameObject attacker)
    {
        ServerRepelAbductor(abductor); // 이 이벤트의 납치범인지·서버인지는 그쪽이 판정한다
    }

    // 포획 통보 — 행동불능을 걸고 수렴시킨 뒤 외곽까지 끌고 간다.
    private void HandleAbductionCaught(NpcController catcher, Transform caught)
    {
        if (m_carryTarget != null || caught == null)
            return;

        PlayerIncapacitation incap = caught.GetComponent<PlayerIncapacitation>();

        // 기능 정지된 몸은 접수하지 않는다 — 오검거와 같은 이유(#365): 본부 부활 장치에 안치해 둔 몸이
        // 동료 눈앞에서 사라진다. 납치범은 해산하지 않으니 부활한 뒤에 다시 노려진다.
        if (incap != null && incap.IsDead)
            return;

        m_carryTarget = caught;

        // 끌려가는 동안 걸어 나가지 못하게. 외곽에 도착하면 방치 시간만큼 더 이어지고, 구조되면 즉시 풀린다
        if (incap != null)
            incap.Incapacitate(IncapacitationCause.Abducted);

        PruneDead(m_abductors);
        foreach (NpcController abductor in m_abductors)
            abductor.StartPenaltyConverge(caught);

        // 이제부터 알린다 — 구조가 관심사가 되는 시점이다 (AnnounceOnBegin이 false인 이유)
        App.Game.SuddenEvent?.Announce($"{m_displayName} — 동료가 끌려가고 있다");
        Debug.Log($"[납치] 포획 — {catcher.name} → {caught.name}");

        CarryToOutskirtsAsync(caught).Forget();
    }

    // 외곽까지 끌고 가 방치했다가 풀어 준다. 구조로 끝났으면 방치 없이 즉시 푼다. (서버 전용)
    private async UniTask CarryToOutskirtsAsync(Transform caught)
    {
        var settings = new CarryEscortSequence.Settings(
            m_convergeArriveDistance, m_convergeTimeoutSeconds,
            m_carrierGap, m_arriveDistance, m_travelTimeoutSeconds);

        Transform destination = PickOutskirtPoint(caught.position);

        bool arrived = await CarryEscortSequence.RunAsync(
            caught, m_abductors, destination, settings, destroyCancellationToken);

        // 끌기는 여기서 끝난다 — 납치범은 임무를 벗어 잔류 시민이 된다(방치 시간을 지키고 서 있지 않는다).
        ReleaseAllAbductors();

        if (arrived)
        {
            // 도착 — 그 자리에 버려둔다. m_carryTarget을 아직 비우지 않는 이유는 라운드가 이 사이에
            // 끝날 수 있기 때문이다: ServerReset이 그 참조로 행동불능을 풀어 준다.
            Debug.Log($"[납치] 외곽 도착 — {(caught != null ? caught.name : "대상")}을(를) {m_abandonedSeconds}초간 방치한다");
            await UniTask.Delay(
                TimeSpan.FromSeconds(m_abandonedSeconds), cancellationToken: destroyCancellationToken);
        }
        else
        {
            Debug.Log("[납치] 호송 해체 — 그 자리에서 즉시 풀려난다");
        }

        // 대기 중에 파괴·기능정지·라운드 정리가 끼어들 수 있어 여기서 다시 집는다.
        // Cause 확인이 그 가드다 — 다른 사유(다운·기능정지)로 바뀌었으면 건드리지 않는다.
        PlayerIncapacitation incap = caught != null ? caught.GetComponent<PlayerIncapacitation>() : null;
        if (incap != null && incap.Cause == IncapacitationCause.Abducted)
            incap.Recover();

        m_carryTarget = null;
        Finish();
    }

    // 표적에서 <b>가장 가까운</b> 외곽 지점. 처음에는 가장 먼 곳을 골랐는데, 그러면 끌려가는 구간이 늘
    // 최대치(맵 횡단 30~55초)가 된다 — 그 시간은 페널티가 아니라 아무것도 못 하고 보고만 있는 시간이다.
    // 실제 페널티는 방치된 뒤 걸어 돌아오는 거리이고, 그건 어느 외곽에 놓이든 남는다.
    private Transform PickOutskirtPoint(Vector3 from)
    {
        Transform nearest = null;
        float nearestSqr = float.MaxValue;

        for (int i = 0; i < m_outskirtPoints.Length; i++)
        {
            Transform point = m_outskirtPoints[i];
            if (point == null)
                continue;

            float sqr = (point.position - from).sqrMagnitude;
            if (sqr < nearestSqr)
            {
                nearestSqr = sqr;
                nearest = point;
            }
        }

        return nearest;
    }

    /// <summary>
    /// 납치범 1명을 이 호송에서 떼어낸다 — <b>구조 진입점</b>. 서버(또는 오프라인) 전용. (#371)
    ///
    /// 오검거의 <see cref="NpcController.ApplyChaseRepel"/>과 다르다: 그쪽은 포획 후에는 일부러 무시하지만
    /// (유예 창은 잡히기 전까지다, #278) 납치는 <b>끌려가는 중에 떼어내는 것이 협동의 핵심</b>이다.
    ///
    /// 2명이 끌고 있으면 하나만 떼어져도 남은 1명이 계속 끌고 간다 — 구조가 2단계다.
    /// 전원이 떨어지면 <see cref="CarryEscortSequence"/>가 호송 해체를 감지해 스스로 끝낸다.
    /// </summary>
    public void ServerRepelAbductor(NpcController abductor)
    {
        if (!HasServerAuthority || abductor == null)
            return;

        if (!m_abductors.Contains(abductor))
            return; // 이 이벤트의 납치범이 아니다

        ReleaseAbductor(abductor);
        Debug.Log($"[납치] 납치범 이탈 — {abductor.name}, 남은 {m_abductors.Count}명");
    }

    /// <summary>이 NPC가 지금 납치범인가 — 타격 경로가 격퇴 대상인지 묻는다. (#371)</summary>
    public bool IsAbductor(NpcController npc) => npc != null && m_abductors.Contains(npc);
}
