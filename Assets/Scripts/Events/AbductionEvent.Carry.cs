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
/// 목적지가 광장이 아니라 가장 가까운 외곽이라는 것, 그리고 <b>도착이 끝이 아니라</b>
/// 린치·처형·반출로 이어진다는 것(3단계의 근거는 본체 문서에 있다).
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

        // 이미 무력화된 몸은 접수하지 않는다 — 원인을 가리지 않는다.
        // 기능 정지(Die)를 막는 이유는 오검거와 같고(#365, 본부 부활 장치에 안치해 둔 몸이 동료 눈앞에서
        // 사라진다), 나머지(매달기·다운·기절)까지 함께 막는 이유는 <b>무력화 원인이 하나뿐</b>이기 때문이다:
        // Incapacitate는 Die만 덮어쓰기를 막으므로(#364) 여기서 걸러내지 않으면 오검거 호송 중인 플레이어를
        // 접수해 Cause를 Abducted로 덮고, 두 호송이 같은 몸을 광장과 외곽 양쪽으로 끌게 된다.
        // 실제로 열려 있는 경로다 — 추격 NPC는 '혼자' 판정을 깨지 않으므로(LonePlayerWatch는 플레이어만 센다)
        // 혼자 있는 상태에서 오검거를 저지르면 양쪽 표적이 동시에 된다.
        //
        // 납치가 취소되는 것은 아니다 — 표적은 고정이고 납치범은 해산하지 않으며 추격 상태가 간격을 두고
        // 포획을 재통보하므로, 상대 무력화가 풀린 뒤에 다시 접수된다. 오검거와 같은 '유예'다.
        if (incap != null && incap.IsIncapacitated)
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

    // 외곽까지 끌고 가 린치하고, 숨이 끊기면 시체를 맵 밖으로 반출한다. (서버 전용)
    // m_carryTarget을 끝에서야 비우는 이유는 그대로다 — 이 사이에 라운드가 끝나면 ServerReset이
    // 그 참조로 무력화를 풀어 준다.
    private async UniTask CarryToOutskirtsAsync(Transform caught)
    {
        var settings = new CarryEscortSequence.Settings(
            m_convergeArriveDistance, m_convergeTimeoutSeconds,
            m_carrierGap, m_arriveDistance, m_travelTimeoutSeconds);

        bool arrived = await CarryEscortSequence.RunAsync(
            caught, m_abductors, PickNearest(m_outskirtPoints, caught.position),
            settings, destroyCancellationToken);

        // 호송이 해체됐다 — 전원 격퇴(구조 성공)이거나 대상 소실. 그 자리에서 즉시 풀려난다.
        if (!arrived)
        {
            Debug.Log("[납치] 호송 해체 — 그 자리에서 즉시 풀려난다");
            FinishRescued(caught);
            return;
        }

        // 도착 — RunAsync가 StopCarried로 끌기를 끊었으므로 피해자는 그 자리에 선다.
        // 무력화(Abducted)는 풀지 않는다: 서 있되 아무것도 못 하는 채로 맞는 것이 이 구간이다.
        if (!await LynchAsync(caught))
        {
            FinishRescued(caught);
            return;
        }

        await DisposeBodyAsync(caught);

        m_carryTarget = null;
        Finish();
    }

    /// <summary>
    /// 외곽 린치 — 납치범을 저항형으로 돌려 피해자를 구타하고, HP가 0이 되면 기능 정지로 확정한다.
    /// 반환값: 숨이 끊겼으면 true. 구조(납치범 전멸)·대상 소실로 중단됐으면 false.
    ///
    /// 구타 자체는 <see cref="NpcResistState"/>가 한다 — 부채꼴 판정·사거리·모션 타이밍이 이미 거기 있고,
    /// 무력화된 표적을 놓지 않게 하는 예외도 그쪽에 있다. 여기서 보는 것은 "언제 끝나는가"뿐이다.
    /// </summary>
    private async UniTask<bool> LynchAsync(Transform caught)
    {
        // 무력화 원인을 린치로 바꾼다 — 행동 차단은 그대로 두고 <b>자세만</b> 세운다(PlayerIncapacitation.IsProne).
        // 이걸 빼면 끌려오던 자세 그대로 바닥에 누운 채 맞는다.
        PlayerIncapacitation victim = caught != null ? caught.GetComponent<PlayerIncapacitation>() : null;
        if (victim != null)
            victim.Incapacitate(IncapacitationCause.Lynched);

        PruneDead(m_abductors);
        foreach (NpcController abductor in m_abductors)
            abductor.StartResist(caught);

        Debug.Log($"[납치] 외곽 도착 — 린치 시작 (납치범 {m_abductors.Count}명)");

        PlayerHealth health = caught != null ? caught.GetComponent<PlayerHealth>() : null;
        float deadline = Time.time + m_lynchTimeoutSeconds;

        while (Time.time < deadline)
        {
            await UniTask.Delay(
                TimeSpan.FromSeconds(0.25), cancellationToken: destroyCancellationToken);

            if (caught == null || health == null)
                return false; // 접속 종료 등 — 처형할 대상이 없다

            PruneDead(m_abductors);
            if (m_abductors.Count == 0)
            {
                Debug.Log("[납치] 린치 중단 — 납치범이 남지 않았다 (구조 성공)");
                return false;
            }

            if (health.CurrentHp <= 0)
                break;
        }

        if (caught == null || health == null)
            return false;

        // 상한까지 못 죽였다 = 때리지 못하고 있다는 뜻이다(지형에 낀 경우 등). 서 있는 채로 영원히
        // 맞고 있게 두지 않고 결말을 집행하되, 원인을 놓치지 않게 경고를 남긴다.
        if (health.CurrentHp > 0)
            Debug.LogWarning($"[납치] 린치 상한({m_lynchTimeoutSeconds}초) 초과 — 강제로 끝낸다", this);

        // HP 0에는 PlayerHealth가 이미 기능 정지를 걸어 뒀으므로(#524) 보통은 여기서 할 일이 없다.
        // 그래도 부르는 것은 위의 상한 초과 폴백 때문이다 — HP가 남은 채 끝난 경우엔 여기서만 확정된다.
        // 구조 창은 HP 0 이전에 닫혔으므로 어느 쪽이든 되돌아갈 길은 없다.
        PlayerIncapacitation incap = caught.GetComponent<PlayerIncapacitation>();
        if (incap != null)
            incap.ServerKillByAbduction();

        return true;
    }

    /// <summary>
    /// 시체 반출 — 도시 바깥으로 <b>NavMesh를 벗어나</b> 걸어 나간 뒤, 시체와 납치범을 함께 치운다.
    ///
    /// <b>왜 에이전트를 끄고 직접 미는가.</b> 목적지가 맵 밖이라 애초에 NavMesh가 없다. 처음에는 맵
    /// 가장자리에 반출 지점을 배선해 <see cref="CarryEscortSequence"/>를 한 번 더 돌리려 했는데,
    /// 그건 "맵 밖으로 나간다"를 NavMesh 안에서 흉내 내는 것이라 후보 지점마다 경로가 끊겼다
    /// (바깥쪽 조각이 도로 건너 섬이라 걸어갈 수 없다 — 실측으로 5곳 중 2곳이 그랬다).
    /// 나가는 것이 목적이면 나가면 된다.
    ///
    /// 밧줄 끌기(<see cref="NpcController.StartRopeDrag"/>)가 지고 있는 위험 — 에이전트를 껐다 켤 때
    /// NavMesh 재부착에 실패해 그 자리에 굳는 것 — 은 여기 없다. <b>다시 켜지 않기 때문이다.</b>
    /// 이 이동의 끝은 언제나 소멸이다.
    ///
    /// 두 가지가 저절로 따라온다:
    ///  · <b>걷는 모션</b> — <see cref="NpcAnimationDriver"/>가 agent.velocity가 아니라 실제 트랜스폼
    ///    변위로 속도를 재므로, 에이전트를 꺼도 이동만 하면 로코모션이 맞는다.
    ///  · <b>시체 추종</b> — PlayerTowedMotion의 호송 추종은 두 앵커의 중점만 보지 NavMesh를 보지 않는다.
    ///
    /// 직선으로 나가므로 가장자리 지형을 스칠 수는 있다. 도시 바깥 방향이라 대개 열려 있고,
    /// 몇 초 뒤 사라지는 구간이라 벽 회피를 붙이지 않았다.
    /// </summary>
    private async UniTask DisposeBodyAsync(Transform caught)
    {
        PruneDead(m_abductors);
        if (caught == null || m_abductors.Count == 0)
        {
            DisposeAbductors();
            return;
        }

        // 도시 중심에서 멀어지는 방향 — 외곽에 서 있는 상태이므로 이게 곧 맵 바깥이다.
        // 중심은 외곽 지점들의 평균으로 잡는다(도시를 둘러싸게 배치된 데이터라 그 평균이 중심이다).
        Vector3 away = caught.position - OutskirtCentroid();
        away.y = 0f;
        away = away.sqrMagnitude > 0.01f ? away.normalized : Vector3.forward;
        Quaternion facing = Quaternion.LookRotation(away, Vector3.up);

        // 시체 추종을 다시 건다 — 호송 시퀀스가 도착과 함께 풀어 뒀다(그래서 린치 동안 서 있었다)
        PlayerPenaltyView view = caught.GetComponent<PlayerPenaltyView>();
        if (view != null)
        {
            NpcController lead = m_abductors[0];
            NpcController mate = m_abductors.Count > 1 ? m_abductors[1] : lead;
            view.StartCarried(lead, mate);
        }

        // FSM을 먼저 멈춘다 — 납치범은 아직 저항(Attack) 상태라, 안 멈추면 걸어 나가면서 계속 스윙하고
        // 꺼진 에이전트까지 만진다. 프리즈는 Update 최상단에서 틱을 끊고 에이전트 접근도 가드한다.
        // 에이전트를 끄기 <b>전에</b> 불러야 isStopped가 정상 경로로 걸린다.
        for (int i = 0; i < m_abductors.Count; i++)
            m_abductors[i].SetFrozen(true);

        for (int i = 0; i < m_abductors.Count; i++)
            if (m_abductors[i].Agent != null && m_abductors[i].Agent.enabled)
                m_abductors[i].Agent.enabled = false;

        Debug.Log($"[납치] 시체 반출 — 맵 밖으로 {m_disposalDistance:F0}m 끌고 나간다");

        float traveled = 0f;
        while (traveled < m_disposalDistance)
        {
            await UniTask.Yield(destroyCancellationToken);

            PruneDead(m_abductors);
            if (m_abductors.Count == 0)
                break; // 끌 사람이 남지 않았다 — 시체는 그 자리에 남는다(이미 확정된 결말이라 되돌리지 않는다)

            float step = m_disposalSpeed * Time.deltaTime;
            traveled += step;

            for (int i = 0; i < m_abductors.Count; i++)
            {
                Transform body = m_abductors[i].transform;
                body.SetPositionAndRotation(body.position + away * step, facing);
            }
        }

        // 추종을 먼저 끊는다 — 앵커가 파괴된 뒤에 끊으면 오너 클라가 사라진 참조를 한 프레임 따라간다
        if (view != null)
            view.StopCarried();

        Debug.Log("[납치] 반출 완료 — 시체와 납치범이 맵 밖으로 사라졌다");
        DisposeAbductors();
    }

    // 외곽 지점들의 평균 = 도시 중심. 반출 방향(중심 → 린치 지점)의 기준이다.
    // 별도 필드를 두지 않는 이유는 이미 배선된 데이터로 충분하기 때문이다 — 외곽 지점은 도시를
    // 둘러싸게 놓으므로 그 평균이 곧 중심이고, 지점을 옮기면 중심도 따라 움직인다.
    private Vector3 OutskirtCentroid()
    {
        Vector3 sum = Vector3.zero;
        int count = 0;
        for (int i = 0; i < m_outskirtPoints.Length; i++)
        {
            if (m_outskirtPoints[i] == null)
                continue;

            sum += m_outskirtPoints[i].position;
            count++;
        }

        return count > 0 ? sum / count : transform.position;
    }

    // 구조·소실로 끝났다 — 납치범을 잔류 시민으로 놓아주고 피해자의 납치 무력화를 푼다.
    // Cause 확인이 가드다: 기능 정지(Die)까지 갔거나 다른 사유로 바뀌었으면 건드리지 않는다.
    private void FinishRescued(Transform caught)
    {
        ReleaseAllAbductors();

        PlayerIncapacitation incap = caught != null ? caught.GetComponent<PlayerIncapacitation>() : null;
        if (incap != null
            && (incap.Cause == IncapacitationCause.Abducted || incap.Cause == IncapacitationCause.Lynched))
            incap.Recover();

        m_carryTarget = null;
        Finish();
    }

    // 기준점에서 <b>가장 가까운</b> 외곽 지점 — 끌고 갈 린치 장소를 고른다.
    // 가장 먼 곳을 고르던 초기 방식은 끌려가는 구간이 늘 최대치(맵 횡단 30~55초)가 됐다 —
    // 그 시간은 페널티가 아니라 아무것도 못 하고 보고만 있는 시간이다. 페널티는 결말이 내지 거리가 내지 않는다.
    // 목록이 비었거나 전부 미배선이면 null — 부르는 쪽이 그 경우를 처리한다.
    private static Transform PickNearest(Transform[] points, Vector3 from)
    {
        if (points == null)
            return null;

        Transform nearest = null;
        float nearestSqr = float.MaxValue;

        for (int i = 0; i < points.Length; i++)
        {
            Transform point = points[i];
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
