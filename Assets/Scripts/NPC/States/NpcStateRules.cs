/// <summary>
/// NPC 상태 → 상호작용 가능성 해석 규칙. (#184)
/// 클라 조기검증(Rope)·서버 가드(PlayerEscorter)·조준 피드백(InteractionFeedback)이
/// 모두 여기를 읽는다 — 새 상태 추가 시 이 파일만 고치면 셋이 함께 움직인다.
/// 상태별 '행동'은 NpcXxxState 클래스(FSM, 서버 전용), 상태별 '가능 여부'는 여기 — 역할 분리.
/// 대부분은 동기화된 enum(NpcController.CurrentState)만 보는 순수 함수다 — 클라도 그것만 알기 때문.
/// 상태 enum에 없는 것을 함께 봐야 하는 몇몇은 NpcController를 받는다: 무력화(<see cref="CanRopeBind"/>, #446),
/// 밧줄 유무(<see cref="IsFollowingUnroped"/>), 반출 표식(<see cref="CanResumeUnropedEscort"/>, #517).
/// 그중 <see cref="StaysPutWhenFreed"/>만 위치(NavMesh 영역)까지 보므로 <b>서버(또는 오프라인) 전용</b>이다 (#526).
/// </summary>
public static class NpcStateRules
{
    /// <summary>수갑 체포 채널링의 대상이 될 수 있는 상태인가.
    /// 제외 목록 방식 — 새 상태는 기본 '체포 가능'이므로 막아야 하면 여기 추가할 것.
    /// 도주(Run)·저항(Attack)은 수갑이 아니라 진압봉·테이저로 기절시킨 뒤 밧줄로 잡는다
    /// (GDD 6-1/7-4, #254 · E 제압은 #436·#438에서 전부 제거) —
    /// 반응이 시작된 뒤에는 수갑 채널링이 걸리지 않아야 한다.</summary>
    public static bool IsCapturable(NpcState state) =>
        state != NpcState.Dead // 시체는 검거 대상이 아니다 (#571)
        && state != NpcState.Escorted
        && state != NpcState.Captured
        && state != NpcState.Jailed
        && state != NpcState.Run
        && state != NpcState.Attack
        // 오검거 페널티에 얽힌 시민(수용·추격·호송)은 다시 수갑을 채울 수 없다 (#277~#279) —
        // 추격대를 체포해 페널티 집행을 무산시키는 우회를 막는다 (회피 수단은 격퇴(호루라기 #250)뿐)
        && state != NpcState.Detained
        && state != NpcState.Chasing
        && state != NpcState.PenaltyEscorting
        // 반출돼 인도 지점으로 걸어가는 대상도 수갑으로는 못 잡는다 (#548) — 저지 수단은
        // 도주·저항과 같다: 진압봉·테이저로 기절시킨 뒤 밧줄. 여기를 열면 걸어가는 대상을
        // 채널링 한 번으로 세울 수 있어 '들키면 저지당한다'가 '보이면 끝난다'가 된다.
        && state != NpcState.Releasing;

    /// <summary>타격 피해가 들어가는 상태인가 — <b>스턴 게이트가 아니다.</b> (#292)
    /// 스턴은 오버레이가 되면서 전 상태에 걸리게 됐지만(#292), 타격까지 함께 열면 연행 중인
    /// NPC를 때려 기절시켜 신병에서 빼내는 우회가 생긴다. 그래서 게이트를 둘로 쪼개고
    /// 이쪽은 구 CanBeStunned(#289)의 제외 목록을 그대로 물려받았다 — 팀 결정은
    /// "스턴은 허용, 타격은 차단"이다.
    ///
    /// 제외하는 건 이미 신병을 확보(Escorted/Captured/Jailed)했거나 오검거 페널티가 진행(Detained/
    /// Chasing/PenaltyEscorting) 중인 상태 — 도주(Run)·저항(Attack)은 주 타격 대상이라 제외하지 않는다.</summary>
    public static bool CanBeDamaged(NpcState state) =>
        // 시체는 더 때릴 수 없다 (#571). 막지 않아도 HP는 이미 0이라 SetHp의 엣지가 안 걸리지만,
        // 열어 두면 OnDamaged·피격 연출·납치 격퇴 훅이 시체에서 계속 발행된다.
        state != NpcState.Dead
        && state != NpcState.Escorted
        && state != NpcState.Captured
        && state != NpcState.Jailed
        && state != NpcState.Detained
        && state != NpcState.Chasing
        && state != NpcState.PenaltyEscorting;

    /// <summary>이 NPC를 지금 때릴 수 있는가 — 위 상태 규칙에 <b>납치범 예외</b>를 얹은 정본. (#371)
    /// 납치범은 Chasing/PenaltyEscorting이라 상태만 보면 타격이 막히는데, 납치는 끌려가는 동료를
    /// <b>때려서 떼어내는 것</b>이 유일한 구조 수단이라(6-4) 그 예외를 연다. 위 게이트가 막으려던
    /// "호송 중인 NPC를 때려 신병에서 빼내기"와는 방향이 반대다 — 납치범은 신병이 아니라 가해자다.
    /// 오검거 추격대는 그대로 막힌다(회피 수단은 격퇴 하나). 둘을 가르는 것이
    /// <see cref="NpcDutyAgent.IsUndercoverDuty"/>이고, 동기화 값이라 클라 조준 피드백에서도 읽힌다.
    /// 소매치기(#303)도 같은 예외를 탄다 — 시민인 척 걸어오는 대상이라 시민과 똑같이 다룰 수 있어야 한다.
    /// ⚠ <b>사망은 위 예외보다 위다</b> (#571) — 임무 표식은 죽어도 즉시 내려가지 않으므로,
    /// 상태 검사에만 맡기면 죽은 납치범·소매치기가 계속 맞는다.</summary>
    public static bool CanBeDamaged(NpcController npc) =>
        npc != null
        && npc.CurrentState != NpcState.Dead
        && (npc.Penalty.IsUndercoverDuty || CanBeDamaged(npc.CurrentState));

    /// <summary>환경 피해(차량·폭발 등)가 들어가는 상태인가 — <see cref="CanBeDamaged"/>와 다른 게이트다 (#690).
    /// 그쪽은 신병 빼내기 우회를 막으려 연행 중을 제외하지만, 차·폭발은 신병 상태를 가리지 않는다.
    /// 여기는 죽은 대상만 막는다.</summary>
    public static bool CanTakeEnvironmentalDamage(NpcController npc) =>
        npc != null && npc.CurrentState != NpcState.Dead;

    /// <summary>이 NPC를 지금 밧줄로 묶을 수 있는가 — 상태 규칙에 <b>소매치기 예외</b>를 얹은 정본. (#303)
    /// Chasing이라 상태만 보면 막히지만, 접근 중에 무력화했으면 잡을 수 있어야 한다. 오검거·납치는 그대로 막힌다.</summary>
    public static bool CanArrest(NpcController npc) =>
        npc != null && (npc.Penalty.IsPickpocketDuty || CanArrest(npc.CurrentState));

    /// <summary>반응·배회군인가 — 스턴이 풀릴 때 도주로 전환되는 쪽. (#292)
    /// 여집합(확보·페널티군)은 스턴이 풀려도 아무 전이 없이 하던 일을 재개한다 —
    /// 상태 enum이 애초에 안 바뀌므로 호송·수감·페널티 링크가 그대로 살아 있다.
    ///
    /// 포함 목록 방식이라 <b>새 상태는 기본이 '재개'</b>다. 도주로 깨어나야 하면 여기 추가할 것.
    /// 의도적으로 뺀 것: <see cref="NpcState.Stunned"/>(넉백 KO — 자기 상태 클래스가 스스로
    /// 빠져나간다)와 <see cref="NpcState.Dead"/>(#571 — 시체는 도주하지 않는다. 사망 진입이 스턴
    /// 오버레이를 걷으므로 ExitStun 자체가 도달하지 않지만, 포함 목록이라 가만히 둬도 닫혀 있다).</summary>
    public static bool IsReactive(NpcState state) =>
        state is NpcState.Idle
            or NpcState.Walk
            or NpcState.Run
            or NpcState.Attack
            or NpcState.Intruding;

    /// <summary>지금 새로 반응(도주·저항)을 시작할 수 있는 상태인가. (#400)
    /// <see cref="IsReactive"/>에서 이미 반응 중인 둘(Run·Attack)을 뺀 집합 — 스캔·타격이 연달아
    /// 들어와도 진행 중인 반응을 갈아엎지 않는다. 확보·페널티군은 IsReactive가 이미 걸러 준다.</summary>
    public static bool CanStartReaction(NpcState state) =>
        IsReactive(state) && state != NpcState.Run && state != NpcState.Attack;

    /// <summary>맞았을 때 반응(도주·저항)으로 돌아설 수 있는가 — <see cref="CanStartReaction"/>에
    /// <b>반출 보행 예외</b>를 얹은 것. (#548) 반출 대상은 스캔에는 꿈쩍하지 않지만(IsReactive에 없다)
    /// 때리면 배정된 유형대로 돌아선다 — 쳐다봤다고 그만두면 저지가 너무 싸진다.
    ///
    /// <b>돌아서도 반출은 살아 있다</b> (2026-08-12 확정) — 목적지는 반응군에서도 유지되고
    /// (<see cref="NpcController"/>의 상태 훅) 쓰러뜨려 재우면 깨어나 다시 인도 지점으로 뛴다.
    /// 타격은 시간을 버는 수단이고, 무산시키려면 밧줄로 묶어야 한다.</summary>
    public static bool CanReactToDamage(NpcState state) =>
        CanStartReaction(state) || state == NpcState.Releasing;

    /// <summary>밧줄 대상에서 <b>신병·소유권 때문에</b> 빠지는 상태인가. (#269 → #369 기본 검거로 승격)
    /// 제외 목록 방식 — 이미 신병 확보(Escorted/Captured/Jailed)·타 시스템 소유(페널티)는 제외.
    /// Captured 제외 주의: 그 상태에선 밧줄 좌클릭이 '끌기 재개'로 갈리고, 푸는 건 E다 (#513).
    ///
    /// <b>이것만으로 묶기를 판정하지 말 것</b> — 새로 묶기는 무력화까지 요구하므로
    /// <see cref="CanRopeBind"/>가 정본이고 이 함수는 그 한 조각이다 (#446).</summary>
    public static bool CanArrest(NpcState state) =>
        // 시체는 <b>검거</b> 대상이 아니다 (#571) — 신병이 아니라 짐이라 커스터디로 들어갈 일이 없다.
        // 유치장까지 끌고 가면 계상되지만(ArrestJudge.JudgeCorpse) 그 경로도 커스터디를 쓰지 않는다.
        // ⚠ 그렇다고 시체에 줄을 못 거는 것은 아니다 — 시체 끌기는 이 함수를 거치지 않고
        // <see cref="CanRopeBind"/>가 사망을 무력화와 같은 급으로 따로 연다. 여기를 열면 커스터디
        // 전이(StartEscort)까지 딸려 오는데 시체는 Dead에서 나갈 수 없다.
        state != NpcState.Dead
        && state != NpcState.Escorted
        && state != NpcState.Captured
        && state != NpcState.Jailed
        && state != NpcState.Detained
        && state != NpcState.Chasing
        && state != NpcState.PenaltyEscorting;

    /// <summary>밧줄 좌클릭으로 <b>새로 묶을</b> 수 있는 대상인가 — 무력화된 대상만. (#446)
    /// 깨어 있는 NPC를 좌클릭 3초 홀드로 묶던 경로가 제거되면서 묶기의 전제가 무력화가 됐다.
    /// 역할이 완전히 갈린다: 체력 깎기는 진압봉, 즉시 무력화는 테이저, 신병 확보는 밧줄.
    /// 홀드가 없어졌으므로 이 판정을 통과한 대상은 좌클릭 한 번에 즉시 묶인다 —
    /// 원래 기절 대상에만 있던 지름길이 유일한 경로가 된 것이다 (PlayerEscorter.ServerBeginRopeDrag).
    ///
    /// 상태 enum이 아니라 <see cref="NpcStun.IsStunned"/>를 보는 이유: 스턴은 오버레이라
    /// 테이저·체력 0 기절이 CurrentState를 바꾸지 않는다(넉백 KO만 <see cref="NpcState.Stunned"/>).
    /// 상태로만 보면 두 기절 경로 중 하나가 조용히 빠진다 (#292). IsStunned는 동기화 값이라
    /// 클라 조기검증·조준 피드백(Rope)에서도 읽을 수 있다.
    ///
    /// <b>시체도 대상이다</b> (#571) — 무력화와 같은 자리에서 연다. 다만 끄는 <b>방식</b>이 갈린다:
    /// 산 대상은 서버가 위치를 대입하고(<see cref="NpcRopeDrag.Tick"/>) 시체는 관절 밧줄이 물리로
    /// 끈다(<see cref="RagdollRope"/>). 이 함수는 "줄을 걸 수 있나"만 답하고 그 분기는 알지 않는다.</summary>
    public static bool CanRopeBind(NpcController npc)
    {
        if (npc == null)
            return false;

        // 시체 — <see cref="CanArrest"/>를 함께 보지 않는다. Dead가 종착 상태라서다: 사망 진입이
        // 커스터디·페널티 링크를 전부 끊으므로 그 상태들과 겹칠 수 없고, Dead에서 나가지도 않는다.
        //
        // <b>시체도 여럿이 함께 끈다</b> (#638). 예전에는 1:1로 못박혀 있었다 — 관절 밧줄이
        // 대상당 하나뿐이라 두 번째 줄이 앞의 줄을 끊고 가로챘기 때문이고("합류는 허용, 탈취는
        // 차단"이 깨진다), 그래서 여기서 <c>!IsRoped</c>로 두 번째 사람을 막았다.
        // 이제 <see cref="RagdollRope"/>가 쥔 사람마다 가닥을 따로 걸어 그 이유가 사라졌다:
        // 덧거는 것이 탈취가 아니라 <b>합류</b>이고, 무게 분담·목줄(<c>RopeDragLoad</c>)도
        // 앵커 목록 기준이라 산 대상과 똑같이 적용된다.
        //
        // 중복·자원·사거리는 여기서 보지 않는다 — 호출부가 각자 본다(<c>CanBeginRopeDrag</c>:
        // 내 줄이 이미 걸렸는지, 밧줄이 남았는지). 산 대상의 합류가 <see cref="CanJoinDrag"/>로
        // 갈리는 것과 달리 시체는 상태가 <see cref="NpcState.Dead"/>로 고정이라 그 경로를 타지
        // 않고, 첫 줄이든 두 번째 줄이든 이 함수 하나로 들어온다.
        if (npc.Death.IsDead)
            return true;

        // ⚠ <b>일어나는 중에는 못 묶는다</b> — 아래 <see cref="IsPlayingStandUp"/>. (#572 후속)
        // 기절 기상 구간에도 오버레이는 켜져 있어(FSM을 계속 막아야 몸이 걸어 나가지 않는다)
        // <see cref="NpcStun.IsStunned"/>만 보면 <b>일어나던 몸을 묶어 도로 눕히게 된다.</b>
        //
        // 그래서 검거 창은 <b>정확히 누워 있는 시간</b>과 같다 — 연출(클립 길이)이 난이도를
        // 건드리지 않는다는 것이 이 구조의 요점이다.
        return npc.Stun.IsStunned && !IsPlayingStandUp(npc) && CanArrest(npc); // 오버로드 쪽이라 소매치기 예외를 함께 탄다 (#303)
    }

    /// <summary>
    /// 일어나는 <b>모션이 실제로 도는</b> 중인가 — <b>재포획 창의 끝</b>이다. (#572 후속)
    ///
    /// 기상 경로가 둘이라 여기서 합친다: 기절이 끝나 일어나는 것(<see cref="NpcStun.IsRising"/>)과
    /// 줄이 풀려 일어나는 것(<see cref="NpcStandUp.IsPlayingStandUp"/>). 밖에서는 "지금 일어나는
    /// 중인가" 하나만 물으면 된다 — <see cref="NpcStun.IsStunned"/>가 두 기절 경로를 합치는 것과 같다.
    ///
    /// ⚠ <b>쓰러져 기다리는 구간은 포함하지 않는다.</b> 줄을 풀고 일어나기까지 누워 있는 동안은
    /// 다시 묶을 수 있어야 한다 — #513이 열어 둔 재포획 창이고, <c>NpcRopeDrag.StartRopeDrag</c>가
    /// <c>CancelStandUp</c>을 부르는 것이 그 경로다. 닫는 것은 <b>몸이 실제로 일어나기 시작한
    /// 뒤</b>뿐이다: 그때 묶으면 일어나던 몸이 도로 눕는 그림이 나온다.
    /// </summary>
    public static bool IsPlayingStandUp(NpcController npc) =>
        npc != null && (npc.Stun.IsRising || npc.StandUp.IsPlayingStandUp);

    /// <summary>밧줄 없이 따라오는 수감자인가 — 유치장에서 반출돼 추종 중인 대상. (#492)
    /// E를 누르면 그 자리에 세운다(Captured) — 유치장 안이면 JailIntake가 좌석에 다시 앉히고,
    /// 밖이면 그냥 선다(팀 확정 2026-08-03 "위치로 갈린다").
    ///
    /// 상태 enum만으로는 못 가른다 — 밧줄 끌기도 같은 <see cref="NpcState.Escorted"/>다.
    /// 그래서 <see cref="NpcRopeDrag.IsRoped"/>를 함께 본다(<see cref="CanRopeBind"/>와 같은 이유로
    /// NpcController를 받는다). IsRoped는 동기화 값이라 클라 조준 피드백에서도 읽을 수 있다.</summary>
    public static bool IsFollowingUnroped(NpcController npc) =>
        npc != null && npc.CurrentState == NpcState.Escorted && !npc.Rope.IsRoped;

    /// <summary>멈춰 선 반출 수감자인가 — E로 <b>밧줄 없는 추종</b>을 재개할 수 있는 대상. (#517)
    /// 반출된 대상은 거리가 벌어지면 <see cref="NpcEscortedState"/>가 Captured로 되돌려 세우는데,
    /// 상태만 보면 방금 제압한 신병과 구분되지 않아 E가 밧줄 끌기로 샜다 — 반출 흐름으로 되돌릴 입력이
    /// 없어지는 것이 #517의 증상이다. 그래서 상태 대신 <see cref="NpcCustody.IsJailExtracted"/>를
    /// 함께 본다(<see cref="CanRopeBind"/>·<see cref="IsFollowingUnroped"/>와 같은 이유로 NpcController를 받는다).
    ///
    /// 밧줄이 걸린 대상은 여기 오지 않는다 — 묶이는 순간 표식이 꺼져(NpcRopeDrag.StartRopeDrag)
    /// E가 다시 밧줄 재개로 간다. 두 분기가 겹치지 않는 근거가 그것이다.</summary>
    public static bool CanResumeUnropedEscort(NpcController npc) =>
        npc != null && npc.CurrentState == NpcState.Captured && npc.Custody.IsJailExtracted;

    /// <summary>이미 남이 끌고 있는 대상에 밧줄을 <b>덧걸</b> 수 있는가 — 줄다리기 합류. (#390)
    /// 팀 결정은 "합류는 허용, 탈취는 차단"이다. 합류는 기존 끌기를 끊지 않고 참가자만 하나 늘린다.
    /// 그래서 <see cref="CanArrest"/>의 <see cref="NpcState.Escorted"/> 제외를 <b>건드리지 않고</b>
    /// 규칙을 따로 판다 — 그쪽을 열면 "새로 묶기" 경로가 통째로 열려 탈취가 딸려온다.
    /// 같은 이유로 놓아둔 체포(Captured)는 뺀다: 남의 소유로 서 있는 대상이라 그게 곧 탈취다.</summary>
    public static bool CanJoinDrag(NpcState state) => state == NpcState.Escorted;

    /// <summary>E로 풀어 석방할 수 있는 상태인가 — 체포되어 멈춘 대상(Captured)만. (#290 → #369 → #513)
    /// 밧줄은 소모형이 아니라 상태만으로 가른다(수갑 시절의 자원 유무 조건 없음). 제압만으로 잡힌 Captured도 대상.</summary>
    public static bool CanRelease(NpcState state) => state == NpcState.Captured;

    /// <summary>
    /// 줄이 풀려도 <b>그 자리에 남아야</b> 하는 신병인가 — 밧줄 끊김(<see cref="PlayerEscorter"/>)과
    /// 인계 방치 만료(<see cref="NpcCapturedState"/>)가 함께 보는 단일 기준. 서버(또는 오프라인) 전용. (#526)
    ///
    /// 두 경로가 같은 질문에 다르게 답하던 것이 #526이다. 방치 쪽에는 판정 완료 가드가 있는데
    /// (GDD 7-6 "감옥에서 탈출하면 안 된다") 밧줄 끊김 쪽에는 없어서, 유치장 안에 묶어 둔 수감자가
    /// 줄이 끊기는 순간 도주로 전환돼 <b>잠긴 창살을 통과해</b> 나갔다.
    ///
    /// <b>감옥이 격리 공간이 된 뒤로(#537) 그 탈출 자체는 불가능해졌다</b> — 도시로 나가는 NavMesh
    /// 경로가 없어 도주 상태가 돼도 방 안을 뛰어다닐 뿐이다. 그래도 가드는 남긴다: 감옥 안에서
    /// 수감자가 도주 상태로 돌아다니는 그림은 "가둬 뒀다"가 아니다.
    ///
    /// 둘 중 하나면 남는다:
    ///  · <b>감옥 방 안</b>(<see cref="JailRoom"/>) — 반출해 놓고 감옥 안에 방치한 대상이 여기 걸린다.
    ///  · <b>판정이 끝난 대상</b>(<see cref="NpcCustody.IsDelivered"/>) — GDD 7-6의 방치 타이머 제외와
    ///    같은 이유다. 예외는 반출해 놓고 방치한 대상(<see cref="NpcCustody.IsJailExtracted"/>, #517):
    ///    정산·진행도에서 이미 빠져 있어 그냥 두면 팀 손실만 남긴 채 영원히 서 있으므로 달아나게 한다.
    ///    그 대상도 감옥 안이면 위 조건에 걸려 남는다.
    ///
    /// <b>#548 이후 그 예외는 감옥 안에서만 걸린다</b> — 반출 표식이 문을 나서는 순간 꺼지기 때문이다
    /// (<see cref="JailIntake"/>). 그래서 문 밖에서 저지돼 풀려난 대상은 달아나지 않고 그 자리에 선다:
    /// 저지한 사람이 밧줄로 다시 끌어 재수감하라고 세워 두는 것이다.
    /// </summary>
    public static bool StaysPutWhenFreed(NpcController npc) =>
        npc != null
        && (JailRoom.Contains(npc.transform.position)
            || (npc.Custody.IsDelivered && !npc.Custody.IsJailExtracted));

    /// <summary>E 상호작용이 반응하는 상태인가 — 이제 <b>신병 조작 전용</b>이다. (#438/#492)
    /// 포함 목록 방식 — 새 상태는 기본 'E 불가'이므로 열어야 하면 여기 추가할 것.
    /// NpcSubdueInteractable.Interact의 분기 집합과 반드시 일치해야 한다.
    ///
    /// 두 단계로 좁혀졌다: 도주형 3초 제압 홀드 제거(#436)로 <c>Run</c>이 타격 분기에 합쳐졌고,
    /// 제압 타격 자체가 제거(#438)되면서 배회(Idle/Walk)·도주(Run)·저항(Attack)이 전부 빠졌다.
    /// 때리는 것은 진압봉, 즉시 무력화는 테이저, 신병 확보는 밧줄이 맡는다.
    ///
    /// 남은 둘: <c>Captured</c>는 재연행(밧줄 끌기 재개), <c>Jailed</c>는 <b>유치장 반출</b>이다 —
    /// 앉은 수감자를 일으켜 밧줄 없이 따라오게 한다 (#492). 이미 확보가 끝난 대상이라
    /// 무력화도 채널링도 요구하지 않는다.
    /// 끌리는 중(<c>Escorted</c>)의 줄다리기 복귀는 상태가 아니라 "누구의 줄인가"로 갈리므로
    /// 순수 함수인 여기가 아니라 호출부가 판단한다 (#398).
    ///
    /// 개명 이력: <c>HasSubdueInteraction</c> → 제압(subdue) 동작이 E에서 전부 빠져 이름이
    /// 실제 역할과 어긋나게 되어 #438에서 바꿨다.</summary>
    public static bool HasInteractKeyAction(NpcState state) =>
        state is NpcState.Captured or NpcState.Jailed;

    /// <summary>방치 회복(<see cref="NpcHealth"/>)이 지금 적용될 수 있는 상태인가 — 사망·수감·호송 중 제외. (#707)</summary>
    public static bool CanRegenerate(NpcController npc) =>
        npc != null
        && !npc.Death.IsDead
        && npc.CurrentState != NpcState.Jailed
        && npc.CurrentState != NpcState.Escorted;
}
