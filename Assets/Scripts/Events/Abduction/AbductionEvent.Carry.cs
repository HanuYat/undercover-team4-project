using System;
using Cysharp.Threading.Tasks;
using UnityEngine;

/// <summary>
/// AbductionEvent의 <b>포획 이후</b> 파트 (#371) — 접수·호송·결말·구조를 든다.
/// 본체(AbductionEvent.cs)는 발동 조건·스폰·수명을 들고, 이 파일이 그것과 호송을 잇는다 —
/// partial이므로 상태(m_abductors·m_carryTarget·인스펙터 값)는 그대로 공유한다.
/// 오검거(WrongfulArrestPenalty + .Carry)와 같은 가름이며, 수렴·대형·끌기 연출 자체는
/// 두 이벤트가 <see cref="CarryEscortSequence"/>를 함께 쓴다.
///
/// 여기 남은 것은 납치만의 판단이다: 끌려가는 중에도 떼어낼 수 있다는 것(구조),
/// 목적지가 광장이 아니라 가장 가까운 맨홀이라는 것, 그리고 <b>도착이 끝이 아니라</b>
/// 뚜껑 열림·하강으로 이어진다는 것 (#775).
/// </summary>
public partial class AbductionEvent
{
    // 타격 통보 — <b>구조</b>다. 한 대라도 맞으면 그 납치범은 이 호송에서 떨어진다 (#371).
    // 체력을 다 깎을 필요는 없다: 여기서 요구하는 것은 "떼어냈다"이지 "제압했다"가 아니고,
    // 진압봉 3대(0.9초 쿨다운)를 요구하면 이미 맨홀에 도착해 있다. 제압·검거는 그 다음 선택지다.
    //
    // 데미지 소스를 가리지 않는다 — 폭발(BombDevice)에 휘말려 놓치는 것도, 동료가 오사(#461)로 맞춘
    // 것도 같은 결말이면 맞는다. 그래서 attacker는 보지 않는다(가해자를 가리면 "누가 구했나"를 따지는
    // 규칙이 하나 더 생기는데, 떼어내진 사실은 누가 때렸든 같다).
    private void HandleAbductorDamaged(NpcController abductor, GameObject attacker)
    {
        ServerRepelAbductor(abductor); // 이 이벤트의 납치범인지·서버인지는 그쪽이 판정한다
    }

    // 무력화 통보 — 타격과 <b>같은 격퇴</b>다 (#554). 테이저 한 발이면 그 납치범은 이 호송에서 떨어진다.
    // 데미지에만 물려 있던 것을 넓힌 이유는 역할 분담이다: 체력 깎기는 진압봉, 즉시 무력화는 테이저인데
    // (#446) 격퇴가 타격 전용이면 "즉시 무력화" 담당이 정작 동료가 끌려가는 상황에서만 쓸모가 없다.
    // 테이저는 상태 게이트(CanBeDamaged)를 타지 않으므로 추격·호송 어느 단계에서도 통한다.
    //
    // 떨어져 나간 납치범은 <b>스턴이 유지된 채</b>다 — 그대로 밧줄로 묶어 검거할 수 있다(팀 확정 2026-08-07).
    // 구조와 검거가 한 동작으로 이어지는 것이 테이저의 값이고, 대가는 쿨다운 5초·사거리 8m다.
    // 진압봉도 결국 HP 0 → 기절 → 밧줄이라 종착점은 같고, 여기서만 그 앞이 짧아진다.
    //
    // attacker/threat를 보지 않는 것은 타격 쪽과 같은 이유다 — 떼어내진 사실은 누가 했든 같다.
    private void HandleAbductorStunned(NpcController abductor, Transform threat)
    {
        ServerRepelAbductor(abductor);
    }

    /// <summary>
    /// 표적이 <b>납치 밖의 사유로</b> 쓰러졌다 — 그러면 납치는 손을 뗀다. (#554/#679)
    ///
    /// 포획 전: 추격 중 죽음은 납치범이 아직 손대지 못한 시점이라 항상 외부 사인이다. 표적 오브젝트는
    /// 파괴되지 않으니 <see cref="NpcChaseState"/>가 스스로 못 챙기므로, 60초 상한 전에 여기서 끊는다.
    ///
    /// 포획 후: 실제 경로는 폭탄이다 — 호송 중 폭발로 HP가 0이 되면 접수 단계 가드만으로는 못 잡는다.
    /// 납치범은 때리지 않으므로(#775) 여기 걸리는 사인은 언제나 외부 사유다 — 가려낼 것이 없다.
    ///
    /// 정적 이벤트라 모든 플레이어의 변경이 들어온다 — 지금 쫓거나 끌고 가는 대상만 본다.
    /// </summary>
    private void HandleVictimCauseChanged()
    {
        if (!HasServerAuthority)
            return;

        if (m_carryTarget == null)
        {
            if (m_chaseTarget == null)
                return;

            PlayerIncapacitation chaseIncap = m_chaseTarget.GetComponent<PlayerIncapacitation>();
            if (chaseIncap != null && chaseIncap.Cause == IncapacitationCause.Die)
            {
                Debug.Log($"[납치] 추격 무산 — 표적이 납치 밖의 사유로 사망: {m_chaseTarget.name}");
                ReleaseAllAbductors();
                Finish();
            }
            return;
        }

        if (m_descending || m_finishing)
            return;

        PlayerIncapacitation incap = m_carryTarget.GetComponent<PlayerIncapacitation>();
        if (incap == null || incap.Cause == IncapacitationCause.Abducted)
            return; // 우리가 건 무력화 그대로다

        Debug.Log($"[납치] 호송 중단 — {m_carryTarget.name}이 납치 밖의 사유로 쓰러졌다 ({incap.Cause})");

        // 임무만 해제한다 — 몸은 그 자리에 그대로 둔다(폭탄 사망이면 운반해 부활시킬 몸이다).
        // 남은 절차가 다음 틱에 "납치범이 남지 않았다"를 보고 스스로 끝내고, 그 뒤는 FinishRescued가 받는다.
        ReleaseAllAbductors();
    }

    // 포획 통보 — 행동불능을 걸고 수렴시킨 뒤 맨홀까지 끌고 간다.
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

        // 끌려가는 동안 걸어 나가지 못하게. 맨홀에 도착하면 뚜껑이 열릴 동안 더 이어지고, 구조되면 즉시 풀린다
        if (incap != null)
            incap.Incapacitate(IncapacitationCause.Abducted);

        PruneDead(m_abductors);
        foreach (NpcController abductor in m_abductors)
            abductor.Penalty.StartPenaltyConverge(caught);

        // 여기서는 알리지 않는다 — 토스트는 스폰 시점 한 번뿐이다(AnnounceOnBegin, 팀 확정 2026-08-13).
        // 이미 "납치가 시작됐다"를 띄운 뒤라 포획 토스트는 같은 사실을 두 번 말하는 셈이고,
        // 끌려가는 것 자체는 화면에서 보인다. 서버 로그는 남긴다 — 디버깅에는 이 순간이 필요하다.
        Debug.Log($"[납치] 포획 — {catcher.name} → {caught.name}");

        CarryToManholeAsync(caught).Forget();
    }

    // 맨홀까지 끌고 가 뚜껑을 열고, 그 아래로 데려간다. (서버 전용)
    // m_carryTarget을 끝에서야 비우는 이유는 그대로다 — 이 사이에 라운드가 끝나면 ServerReset이
    // 그 참조로 무력화를 풀어 준다.
    private async UniTask CarryToManholeAsync(Transform caught)
    {
        Transform manholePoint = PickNearest(m_outskirtPoints, caught.position);
        AbductionManhole manhole = manholePoint != null
            ? manholePoint.GetComponentInChildren<AbductionManhole>()
            : null;

        var settings = new CarryEscortSequence.Settings(
            m_convergeArriveDistance, m_convergeTimeoutSeconds,
            m_carrierGap, m_arriveDistance, m_travelTimeoutSeconds);

        bool arrived = await CarryEscortSequence.RunAsync(
            caught, m_abductors, manholePoint, settings, destroyCancellationToken);

        // 호송이 해체됐다 — 전원 격퇴(구조 성공)이거나 대상 소실. 그 자리에서 즉시 풀려난다.
        if (!arrived)
        {
            Debug.Log("[납치] 호송 해체 — 그 자리에서 즉시 풀려난다");
            FinishRescued(caught);
            return;
        }

        // 도착 — RunAsync가 StopCarried로 끌기를 끊었으므로 피해자는 그 자리에 멈춘다(자세는
        // 무력화가 정한다 — Abducted도 다른 무력화처럼 쓰러진 자세다, #901).
        // 무력화(Abducted)는 풀지 않는다: 뚜껑이 열리는 동안 아무것도 못 한다.
        if (!await OpenManholeAsync(caught, manhole))
        {
            FinishRescued(caught);
            return;
        }

        // 하강도 실패를 낸다 — 데려갈 사람이 남지 않은 채 여기까지 온 경우다(아래 참고).
        // 그때는 결말이 아니라 구조로 끝내야 한다: 몸을 풀어 주지 않으면 라운드가 끝날 때까지 굳는다.
        if (!await DescendAsync(caught, manholePoint, manhole))
        {
            FinishRescued(caught);
            return;
        }

        m_carryTarget = null;
        Finish();
    }

    /// <summary>
    /// 뚜껑을 열고 기다린다 — <b>이 구간이 마지막 구조 창</b>이다. (#775)
    /// 반환값: 끝까지 버텼으면 true. 구조(납치범 전멸)·대상 소실로 중단됐으면 false.
    ///
    /// 뚜껑은 선택 배선이다 — 없으면 연출만 빠지고 대기는 그대로 돈다. 씬 배선 상태에 결말이 걸리면
    /// 배선을 빠뜨린 지점으로 끌려간 판이 결말 없이 멈춘다.
    /// </summary>
    private async UniTask<bool> OpenManholeAsync(Transform caught, AbductionManhole manhole)
    {
        if (manhole != null)
            manhole.ServerOpen();

        Debug.Log($"[납치] 맨홀 도착 — 뚜껑 열림 ({m_manholeOpenSeconds:F1}초, 납치범 {m_abductors.Count}명)");

        // 마감을 <b>기다린 뒤에</b> 본다 — 조건을 while에 두면 마지막 폴링 이후 마감까지의 틈에서
        // 떼어낸 것을 못 보고 도착을 성공으로 반환한다. 구조 창은 내려가기 전까지이므로 그 틈도 창이다.
        float deadline = Time.time + m_manholeOpenSeconds;

        while (true)
        {
            await UniTask.Delay(
                TimeSpan.FromSeconds(0.25), cancellationToken: destroyCancellationToken);

            if (caught == null)
                return false; // 접속 종료 등 — 데려갈 대상이 없다

            PruneDead(m_abductors);
            if (m_abductors.Count == 0)
            {
                Debug.Log("[납치] 맨홀 앞에서 구조 성공 — 납치범이 남지 않았다");
                if (manhole != null)
                    manhole.ServerClose();
                return false;
            }

            if (Time.time >= deadline)
                return true;
        }
    }

    /// <summary>
    /// 맨홀 하강 — 피해자와 납치범이 함께 지하로 내려가 사라진다. <b>결말이 확정되는 구간</b>이다. (#775)
    ///
    /// <b>왜 에이전트를 끄고 직접 미는가.</b> 목적지가 지하라 NavMesh가 없다. 밧줄 끌기가 지고 있는
    /// 위험(재부착 실패로 그 자리에 굳는 것)은 여기 없다 — <b>다시 켜지 않기 때문이다.</b>
    /// 이 이동의 끝은 언제나 소멸이다.
    ///
    /// 몸이 따라 내려오는 것은 PlayerTowedMotion이 두 앵커의 중점만 보고 NavMesh를 보지 않기 때문이다.
    /// 서버가 플레이어 좌표를 직접 밀 수 없으므로(이동 권한은 오너에게 있다) 이 경로가 유일하다.
    /// CharacterController는 그쪽이 꺼 주므로 지면을 통과한다.
    ///
    /// <b>사망 확정은 다 내려간 뒤다.</b> 먼저 걸면 그 순간 래그돌이 켜지는데, 래그돌이 된 몸은
    /// 추종을 따라올 수단이 없어(동적 리지드바디는 부모 트랜스폼을 따르지 않는다) 캡슐만 내려가고
    /// 시체는 인도 위에 남는다.
    ///
    /// 반환값: 결말을 냈으면 true. <b>데려갈 사람이 남지 않아 못 냈으면 false</b> — 부르는 쪽이 구조로 끝낸다.
    /// </summary>
    private async UniTask<bool> DescendAsync(
        Transform caught, Transform manholePoint, AbductionManhole manhole)
    {
        // 이 지점부터 격퇴는 통하지 않는다 — 구조 창은 내려가기 전까지다.
        m_descending = true;

        // 뚜껑 대기의 마지막 순간에 떼어냈을 수 있다 — 여기서 끝내면 결말도 구조도 아닌 상태로
        // 무력화가 남는다. false로 물러나 부르는 쪽이 몸을 풀게 한다.
        PruneDead(m_abductors);
        if (caught == null || m_abductors.Count == 0)
        {
            if (manhole != null)
                manhole.ServerClose(); // 내려갈 사람이 없다 — 열어 둔 뚜껑만 되돌린다
            DisposeAbductors();
            return false;
        }

        // 몸을 다시 붙잡는다 — 호송 시퀀스가 도착과 함께 풀어 뒀다(그래서 뚜껑이 열리는 동안 서 있었다)
        PlayerPenaltyView view = caught.GetComponent<PlayerPenaltyView>();
        if (view != null)
        {
            NpcController lead = m_abductors[0];
            NpcController mate = m_abductors.Count > 1 ? m_abductors[1] : lead;
            view.StartCarried(lead, mate);
        }

        // FSM을 먼저 멈춘다 — 에이전트를 끄기 <b>전에</b> 불러야 isStopped가 정상 경로로 걸린다.
        for (int i = 0; i < m_abductors.Count; i++)
            m_abductors[i].SetFrozen(true);

        for (int i = 0; i < m_abductors.Count; i++)
            if (m_abductors[i].Agent != null && m_abductors[i].Agent.enabled)
                m_abductors[i].Agent.enabled = false;

        // 입구 위로 모아 세운 뒤 수직으로 내린다 — 서 있던 자리에서 그냥 가라앉으면 땅속으로 꺼지는 그림이 된다
        if (manholePoint != null)
        {
            for (int i = 0; i < m_abductors.Count; i++)
            {
                Transform body = m_abductors[i].transform;
                body.position = new Vector3(
                    manholePoint.position.x, body.position.y, manholePoint.position.z);
            }
        }

        // 피해자 시점을 먼저 지상으로 뺀다 — 1인칭으로 지면을 통과하면 땅속이 화면을 덮는다 (#775).
        // 사망 확정 전이라 관전 진입 신호는 이 피벗 고정 자체다(PlayerLook이 그것을 본다).
        if (view != null && manholePoint != null)
        {
            view.SetSpectatePivot(manholePoint.position);

            if (m_descendViewLeadSeconds > 0f)
            {
                await UniTask.Delay(
                    TimeSpan.FromSeconds(m_descendViewLeadSeconds),
                    cancellationToken: destroyCancellationToken);
            }
        }

        Debug.Log($"[납치] 맨홀 하강 — {m_descendDepth:F0}m 아래로 내려간다");

        float descended = 0f;
        while (descended < m_descendDepth)
        {
            await UniTask.Yield(destroyCancellationToken);

            PruneDead(m_abductors);
            if (m_abductors.Count == 0)
                break; // 데려갈 사람이 남지 않았다 — 이미 확정된 결말이라 되돌리지 않는다

            float step = m_descendSpeed * Time.deltaTime;
            descended += step;

            for (int i = 0; i < m_abductors.Count; i++)
                m_abductors[i].transform.position += Vector3.down * step;
        }

        // 다 내려갔으면 뚜껑을 덮는다 — 열린 채로 두면 지나가던 사람이 계속 들여다보는 그림이 된다
        if (manhole != null)
            manhole.ServerClose();

        // 다 내려왔다 — 여기서 라운드 아웃을 확정한다(래그돌은 지하에서 켜진다).
        // 우리가 낸 결말이라고 표시해 무력화 감시가 외부 사인으로 오인하지 않게 한다.
        // 관전 오빗 중심은 하강 전에 이미 맨홀로 넘겨 뒀다 — 시점이 지상에 남아 있다.
        PlayerIncapacitation incap = caught != null ? caught.GetComponent<PlayerIncapacitation>() : null;
        if (incap != null)
        {
            m_finishing = true;
            incap.ServerKillByBodyLost();

            // 체력도 0으로 내린다 — 때린 적이 없어 HP가 가득한 채였고, 그러면 화면에 "기능 정지"인데
            // 체력바는 100인 어긋남이 남는다. Die를 <b>먼저</b> 걸어야 이 0이 다운을 다시 걸지 않는다.
            PlayerHealth health = caught.GetComponent<PlayerHealth>();
            if (health != null && health.CurrentHp > 0)
                health.ModifyHp(-health.CurrentHp);

            m_finishing = false;
        }

        // 래그돌이 몸을 넘겨받을 틈을 준다 — 추종을 먼저 끊으면 CharacterController가 되살아나
        // 지면 밖으로 밀려 올라온다(오너 클라에서 사망이 전파되는 데 몇 프레임 걸린다).
        await UniTask.Delay(
            TimeSpan.FromSeconds(0.5), cancellationToken: destroyCancellationToken);

        // 추종은 앵커가 파괴되기 전에 끊는다 — 뒤에 끊으면 오너 클라가 사라진 참조를 한 프레임 따라간다
        if (view != null)
            view.StopCarried();

        Debug.Log("[납치] 하강 완료 — 맨홀 아래로 사라졌다");
        DisposeAbductors();
        return true;
    }

    // 구조·소실로 끝났다 — 납치범을 잔류 시민으로 놓아주고 피해자의 납치 무력화를 푼다.
    // 때린 적이 없으므로 피해자는 <b>멀쩡한 몸으로</b> 풀려난다 (#775).
    // Cause 확인이 가드다: 기능 정지(Die)까지 갔거나 다른 사유로 바뀌었으면 건드리지 않는다.
    private void FinishRescued(Transform caught)
    {
        ReleaseAllAbductors();

        // 아래 Recover가 무력화 감시(HandleVictimCauseChanged)를 울리므로 참조를 먼저 비운다 (#554) —
        // 남겨 두면 스스로 푼 것을 외부 사유로 오인해 중단 로그가 한 번 더 뜬다.
        m_carryTarget = null;

        PlayerIncapacitation incap = caught != null ? caught.GetComponent<PlayerIncapacitation>() : null;
        if (incap != null && incap.Cause == IncapacitationCause.Abducted)
        {
            incap.Recover();

            // 구조 성공도 토스트로 알리지 않는다 (팀 확정 2026-08-13) — 이 이벤트의 토스트는
            // 스폰 시점 하나뿐이다. 구해 낸 쪽은 현장에 있었으므로 결과가 보이고,
            // 나머지에게는 "끝났다"만 남는 알림이라 판단에 쓰이지 않는다.
            Debug.Log($"[납치] 구조 성공 — {caught.name} 풀려남");
        }

        Finish();
    }

    // 기준점에서 <b>가장 가까운</b> 맨홀 지점 — 끌고 갈 곳을 고른다.
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
    /// 납치범 1명을 이 호송에서 떼어낸다 — <b>구조 진입점</b>. 서버(또는 오프라인) 전용. (#371/#554)
    /// 타격(<see cref="HandleAbductorDamaged"/>)과 무력화(<see cref="HandleAbductorStunned"/>)가 함께 들어온다.
    ///
    /// 오검거의 <see cref="NpcDutyAgent.ApplyChaseRepel"/>과 다르다: 그쪽은 포획 후에는 일부러 무시하지만
    /// (유예 창은 잡히기 전까지다, #278) 납치는 <b>끌려가는 중에 떼어내는 것이 협동의 핵심</b>이다.
    ///
    /// 2명이 끌고 있으면 하나만 떼어져도 남은 1명이 계속 끌고 간다 — 구조가 2단계다.
    /// 전원이 떨어지면 <see cref="CarryEscortSequence"/>가 호송 해체를 감지해 스스로 끝낸다.
    /// </summary>
    public void ServerRepelAbductor(NpcController abductor)
    {
        if (!HasServerAuthority || abductor == null)
            return;

        if (m_descending)
            return; // 하강 구간 — 결말이 확정된 뒤라 떼어낼 것이 없다 (#775)

        if (!m_abductors.Contains(abductor))
            return; // 이 이벤트의 납치범이 아니다

        ReleaseAbductor(abductor);
        Debug.Log($"[납치] 납치범 이탈 — {abductor.name}, 남은 {m_abductors.Count}명");
    }

}
