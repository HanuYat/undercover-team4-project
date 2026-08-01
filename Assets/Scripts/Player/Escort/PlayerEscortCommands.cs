using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 검거·연행 <b>요청과 판정</b>의 서버 권위 허브. (#59, #56/#118 네트워크 전환, #269/#369)
/// 오너 클라의 아이템/상호작용(Rope·NpcSubdueInteractable·PlayerInteractor·HqDropoffTerminal)이
/// 여기 요청 API를 호출하면, 요청을 서버로 넘겨(ServerRpc) 서버가 채널링·사거리·가시선·자원을 검증하고
/// 그 결과 NpcController 상태 변경은 서버에서 일어난다.
///
/// 밧줄 연결 <b>상태</b>는 <see cref="PlayerEscorter"/>가 소유한다 — 이쪽은 입력이 올 때만 돌고
/// 저쪽은 매 프레임 도는, 구동 주체가 다른 관심사다. 의존은 <c>Commands → Escorter</c> 한 방향뿐이며
/// 목록을 직접 만지지 않고 <c>AddTether</c>/<c>RemoveTether</c>/<c>ReleaseDrag</c>로 위임한다.
///
/// 채널링 게이지 피드백(#184)은 공통 기반 <see cref="ChanneledInteractionBehaviour"/>가 제공한다.
/// </summary>
[RequireComponent(typeof(PlayerEscorter))]
public class PlayerEscortCommands : ChanneledInteractionBehaviour
{
    [Header("밧줄 채널링 (서버 권위)")]
    [Tooltip(
        "밧줄 채널링 시간(초) — 줄다리기 합류와 풀기에 쓴다. 새로 묶기는 무력화된 대상만 대상이 되면서 "
        + "채널링 없이 즉시 적용으로 바뀌어 이 값을 쓰지 않는다 (#446)"
    )]
    [SerializeField]
    private float m_channelSeconds = 3f;

    // 사거리는 조준·윤곽선과 같은 기준을 쓴다 — PlayerInteractor.Range 재사용 (#147 패턴, #184).
    // "윤곽선은 뜨는데 체포가 안 되는" 거리 불일치를 구조적으로 차단한다.
    private const float k_fallbackRange = 3f; // 테스트 구성 등 PlayerInteractor가 없을 때

    // 서버 채널링 생명주기(CTS 소유·재진입 가드)는 ServerChannel에 위임 (#109)
    private readonly ServerChannel m_channel = new();

    private PlayerEscorter m_escorter;
    private PlayerInteractor m_interactor;
    private PlayerLoadout m_loadout;

    private PlayerEscorter Escorter
    {
        get
        {
            if (m_escorter == null)
                m_escorter = GetComponent<PlayerEscorter>();
            return m_escorter;
        }
    }

    private PlayerInteractor Interactor
    {
        get
        {
            if (m_interactor == null)
                m_interactor = GetComponent<PlayerInteractor>();
            return m_interactor;
        }
    }

    // 밧줄 소지 확인용 로드아웃 (#229/#369). 테스트 구성 등 없을 수 있어 null 허용.
    private PlayerLoadout Loadout
    {
        get
        {
            if (m_loadout == null)
                m_loadout = GetComponent<PlayerLoadout>();
            return m_loadout;
        }
    }

    private float CaptureRange => Interactor != null ? Interactor.Range : k_fallbackRange;

    // 거리 기준점 — 조준 레이캐스트·윤곽선 게이트와 동일한 AimOrigin(카메라).
    // 루트(발밑) 기준이면 카메라 오프셋만큼 사거리 경계에서 판정이 어긋난다 (#147 관례, #184)
    private Vector3 AimOriginPosition =>
        Interactor != null ? Interactor.AimOrigin.position : transform.position;

    // ---- 오너 클라 진입점 (아이템/상호작용이 호출) ----

    /// <summary>채널링 취소 — 오너가 호출(이동·뗌 등).</summary>
    public void CancelCapture()
    {
        if (!IsSpawned)
        {
            ServerCancelCapture();
            return;
        }
        if (!IsOwner)
            return;
        CancelCaptureRpc();
    }

    /// <summary>밧줄 묶기 시도 — 오너가 호출(Rope 아이템 좌클릭). 서버/오프라인 즉시 실행, 원격은 서버로 요청. (#269)</summary>
    public void RequestRopeDrag(NpcController target)
    {
        if (target == null)
            return;
        if (!IsSpawned || IsServer)
        {
            ServerBeginRopeDrag(target);
            return;
        }
        if (!IsOwner)
            return;
        if (!IsTargetNetworkReady(target))
            return;
        RopeDragRequestRpc(new NetworkObjectReference(target.NetworkObject));
    }

    /// <summary>밧줄 끌기 재개 — 오너가 호출(E, NpcSubdueInteractable). 놓아뒀던 체포 대상을 다시 끈다. (#91 재연행의 자리, #369)</summary>
    public void RequestRopeResume(NpcController target)
    {
        if (target == null)
            return;
        if (!IsSpawned || IsServer)
        {
            ServerResumeRopeDrag(target);
            return;
        }
        if (!IsOwner)
            return;
        if (!IsTargetNetworkReady(target))
            return;
        RopeResumeRequestRpc(new NetworkObjectReference(target.NetworkObject));
    }

    /// <summary>밧줄 풀기 시도 — 오너가 호출(Rope 좌클릭, 대상이 체포 상태일 때). 서버/오프라인 즉시 실행, 원격은 서버로 요청. (#290 → #369)</summary>
    public void RequestUnrope(NpcController target)
    {
        if (target == null)
            return;
        if (!IsSpawned || IsServer)
        {
            ServerBeginUnrope(target);
            return;
        } // 서버/오프라인 즉시 실행
        if (!IsOwner)
            return;
        if (!IsTargetNetworkReady(target))
            return;
        UnropeRequestRpc(new NetworkObjectReference(target.NetworkObject));
    }

    /// <summary>끌기 놓기 — 오너가 호출(E). 조준한 대상 하나만 놓는다, 나머지는 계속 끌린다. (#390)</summary>
    public void RequestRelease(NpcController target)
    {
        if (target == null)
            return;
        if (!IsSpawned)
        {
            Escorter.ReleaseDrag(target);
            return;
        }
        if (!IsOwner)
            return;
        if (!IsTargetNetworkReady(target))
            return;
        ReleaseRpc(new NetworkObjectReference(target.NetworkObject));
    }

    /// <summary>
    /// 본부 인계 요청 — 오너가 호출(인계 단말 E). 서버가 대상·구역을 재검증해 판정한다. (#414)
    /// 예전엔 인계존 콜라이더가 자동으로 판정을 냈다 — 트리거가 상호작용키로 옮겨진 진입점이다.
    /// </summary>
    public void RequestDeliver()
    {
        if (!IsSpawned)
        {
            ServerDeliver();
            return;
        }
        if (!IsOwner)
            return;
        DeliverRpc();
    }

    // 원격 클라 → 서버로 대상을 넘기려면 스폰돼 있어야 한다(NetworkObjectReference 제약).
    // 스폰 안 된 NPC(씬 배치 후 미스폰 등)면 참조 생성이 예외를 던지므로 미리 걸러 경고만 남긴다.
    private bool IsTargetNetworkReady(NpcController target)
    {
        if (target.NetworkObject != null && target.NetworkObject.IsSpawned)
            return true;
        Debug.LogWarning(
            $"검거/제압 요청 무시 — 대상 NPC가 네트워크 스폰되지 않음: {target.name}",
            this
        );
        return false;
    }

    // ---- 서버 RPC (오너 → 서버) ----
    // NGO 2.x 유니버설 RPC: 오너가 자기 플레이어 오브젝트에서 서버로 보내므로 소유권 문제 없음

    [Rpc(SendTo.Server)]
    private void CancelCaptureRpc() => ServerCancelCapture();

    [Rpc(SendTo.Server)]
    private void RopeDragRequestRpc(NetworkObjectReference targetRef)
    {
        if (targetRef.TryGet(out NetworkObject targetObj) &&
            targetObj.TryGetComponent(out NpcController target))
        {
            ServerBeginRopeDrag(target);
        }
    }

    [Rpc(SendTo.Server)]
    private void RopeResumeRequestRpc(NetworkObjectReference targetRef)
    {
        if (targetRef.TryGet(out NetworkObject targetObj) &&
            targetObj.TryGetComponent(out NpcController target))
        {
            ServerResumeRopeDrag(target);
        }
    }

    [Rpc(SendTo.Server)]
    private void UnropeRequestRpc(NetworkObjectReference targetRef)
    {
        if (
            targetRef.TryGet(out NetworkObject targetObj)
            && targetObj.TryGetComponent(out NpcController target)
        )
        {
            ServerBeginUnrope(target);
        }
    }

    [Rpc(SendTo.Server)]
    private void ReleaseRpc(NetworkObjectReference targetRef)
    {
        if (
            targetRef.TryGet(out NetworkObject targetObj)
            && targetObj.TryGetComponent(out NpcController target)
        )
        {
            Escorter.ReleaseDrag(target);
        }
    }

    [Rpc(SendTo.Server)]
    private void DeliverRpc() => ServerDeliver();

    // ---- 서버 실행: 채널 제어 ----

    private void ServerCancelCapture() => m_channel.Cancel();

    /// <summary>
    /// 진행 중인 채널링을 서버 권위로 즉시 중단한다 — 밧줄을 채널링 중 버리는 등 아이템 소유권 이전
    /// 경로에서 서버가 직접 호출한다(Rope.ServerCancelActiveUse).
    /// 오너에 묶인 <see cref="CancelCapture"/>와 달리 소유권과 무관하므로 데디케이티드 서버에서도 동작한다.
    /// 서버(또는 오프라인) 전용.
    /// </summary>
    public void ServerCancelChannel()
    {
        if (IsSpawned && !IsServer)
            return;
        m_channel.Cancel();
    }

    // ---- 서버 실행: 밧줄 묶기 (#269/#369) ----

    /// <summary>밧줄 묶기 진입 — 검증 후 채널링을 시작한다. 서버(또는 오프라인) 실행. (#369)</summary>
    private void ServerBeginRopeDrag(NpcController target)
    {
        if (!CanBeginRopeDrag(target))
            return;

        // 남이 끌고 있는 대상에는 밧줄을 덧건다 — 줄다리기 합류. 기존 끌기는 끊지 않는다(탈취 차단).
        // 이미 커스터디라 반응 판정이 필요 없어 채널링만 태우고 바로 붙인다.
        // 합류는 제압이 아니라 이미 확보된 신병에 대한 조작이라 좌클릭 홀드가 그대로 남아 있다 (#446).
        if (NpcStateRules.CanJoinDrag(target.CurrentState))
        {
            ServerRopeJoinChannelAsync(target).Forget();
            return;
        }

        // 새로 묶기는 무력화된 대상만 — 깨어 있는 NPC를 좌클릭 3초 홀드로 묶던 경로는 제거됐다 (#446).
        // 홀드가 없어져 판정을 통과하면 그 자리에서 즉시 묶인다: 원래 기절 대상에만 있던 지름길이
        // (기절 지속이 채널보다 짧아 콤보가 깨지던 문제, #269) 이제 유일한 경로가 됐다.
        // 클라 조기검증·조준 피드백(Rope)과 단일 기준 (#184).
        if (!NpcStateRules.CanRopeBind(target))
            return;

        ServerApplyRopeDrag(target);
    }

    /// <summary>밧줄 끌기 재개 — 이미 체포되어 멈춘 대상을 채널링·반응 판정 없이 즉시 다시 끈다. (#369)
    /// 수갑 시절의 재연행(ServerEscort)이 그랬듯, 이미 확보된 신병에 반응 판정을 다시 굴리면
    /// 잡아 둔 대상이 그 자리에서 도망치게 된다.</summary>
    private void ServerResumeRopeDrag(NpcController target)
    {
        if (m_channel.IsActive)
            return;
        if (!IsInRange(target))
            return;

        // 이미 내 줄에 묶여 있는(E로 놓아둔) 대상은 용량 게이트를 타지 않는다 — 새 밧줄을 쓰지 않으므로.
        // 태우면 밧줄을 꽉 채워 놓아둔 순간 아무도 다시 못 끌게 된다.
        bool ownRope = Escorter.IsTetheredTo(target);
        if (!ownRope)
        {
            // 남이 묶어 둔 대상은 가져올 수 없다 — 탈취 차단.
            // 합류는 상대가 실제로 끌고 있을 때(Escorted) 밧줄 좌클릭으로만 열린다.
            if (PlayerEscorter.FindEscorterOf(target) != null)
                return;
            if (!CanBeginRopeDrag(target))
                return;
        }

        // 기본은 체포되어 멈춘 대상(Captured)이고, 내 줄이 걸려 있으면 남이 계속 끄는 중(Escorted)도
        // 재개할 수 있다 — 줄다리기에서 E로 빠졌다 다시 끼는 정상 플레이다 (#398).
        // 줄이 없으면 Captured만 — 그 차이가 탈취 차단이다.
        if (!NpcStateRules.CanRelease(target.CurrentState)
            && !(ownRope && NpcStateRules.CanJoinDrag(target.CurrentState)))
            return;

        ServerApplyRopeDrag(target);
    }

    // 새 대상을 묶을 수 있는가 — 자원(밧줄 개수)·중복·사거리. 상태 게이트는 호출부가 각자 건다.
    private bool CanBeginRopeDrag(NpcController target)
    {
        if (m_channel.IsActive)
            return false;
        if (Escorter.IsTetheredTo(target))
            return false; // 이미 내 줄에 묶여 있다 — 좌클릭은 풀기/재개로 갈린다
        if (Escorter.IsAtRopeCapacity)
            return false; // 소지한 밧줄 수만큼만 — 예전의 "한 번에 1명"을 대체한 자원 게이트. 운반 중인 동료도 한 칸을 차지한다 (#365)
        return IsInRange(target);
    }

    // 줄다리기 합류 채널링 — 새로 묶기가 즉시 적용으로 바뀌면서(#446) 이 채널은 합류 전용이 됐다.
    private async UniTaskVoid ServerRopeJoinChannelAsync(NpcController target)
    {
        NotifyOwner($"줄다리기 합류 채널링 시작: {target.name} ({m_channelSeconds}초)");
        NotifyChannelGaugeStart(m_channelSeconds);

        // 수갑 체포와 동일한 keepAlive — 도중 거리 이탈은 즉시 실패시킨다 (#91)
        ServerChannel.Result result;
        try
        {
            result = await m_channel.RunAsync(
                m_channelSeconds, () => target != null && IsInRange(target));
        }
        finally
        {
            NotifyChannelGaugeEnd(); // 어떤 경로로 끝나도 게이지 숨김 보장 (#184)
        }

        switch (result)
        {
            case ServerChannel.Result.OutOfRange:
                NotifyOwner("합류 실패 — 대상이 범위를 벗어남");
                return;

            case ServerChannel.Result.Canceled:
                NotifyOwner("합류 취소됨 (홀드 뗌)");
                return;
        }

        // 채널링 도중 상태·자원이 바뀌었을 수 있다 — 완료 시점에 재확인(그 사이 밧줄을 버려 용량이
        // 준 경우, 합류하려던 대상을 끌던 사람이 그새 놓아버린 경우 등).
        if (target == null || Escorter.IsAtRopeCapacity)
            return;
        if (!NpcStateRules.CanJoinDrag(target.CurrentState))
            return;

        // 반응 판정은 여기서 굴리지 않는다 (#400) — 밧줄은 순수 검거 수단이 됐고, 판정은
        // NpcController.ServerReactTo가 단독으로 갖는다.
        ServerApplyRopeDrag(target);
    }

    // 실제 끌기 진입 — 검증이 끝난 뒤의 상태 조작만 담당한다. 서버(또는 오프라인).
    // 합류(남이 이미 끌고 있음)에도 그대로 쓴다: 아래 셋은 같은 상태를 다시 쓰거나 앵커를 더할 뿐이라
    // 기존 참가자를 건드리지 않는다.
    //
    // 세 호출의 순서가 전부 강제다:
    //   StartEscort → StartRopeDrag : 에이전트를 끈 뒤에 전이하면 직전 상태 Exit이 꺼진 에이전트에
    //                                 isStopped를 써 에러가 난다 (넉백 ServerApplyKnockback과 같은 순서).
    //   StartEscort → ExitStun      : EnterStunned가 Escorted를 만나면 StopEscort로 연행을 끊으므로
    //                                 뒤집으면 방금 건 커스터디가 풀린다.
    private void ServerApplyRopeDrag(NpcController target)
    {
        Escorter.AddTether(target);

        // 커스터디 상태는 수갑 연행과 같은 Escorted를 재사용한다 — 인계존·이벤트 수명·가로채기 방지가
        // 이미 이 상태를 기준으로 판정하기 때문. 이동은 밧줄 장력이 하고 NpcEscortedState가 IsRoped를 보고
        // 추종을 건너뛴다. (#369)
        target.StartEscort(transform);
        target.StartRopeDrag(transform); // 끈 플레이어를 위협으로 기억 — 풀려나면 이쪽에서 도망친다

        // 기절한 채 묶였으면 오버레이를 걷는다 — 남겨두면 만료 해제 경로(resumeReaction: true)가
        // StartFlee를 걸어 묶자마자 도망친다. (#292)
        target.ExitStun(resumeReaction: false);

        NotifyOwner($"밧줄로 묶어 끌기 시작: {target.name} ({Escorter.TetheredCount}/{Escorter.RopeCapacity})");
    }

    // ---- 서버 실행: 밧줄 풀기 채널링 (#290 → #369) ----
    // 묶기 채널링의 역방향 — 밧줄을 든 좌클릭으로 체포되어 멈춘 NPC를 풀어 배회로 돌려보낸다.
    // 묶기와 같은 m_channel·게이지·사거리 판정을 재사용한다(대상 상태가 갈라 주므로 채널 하나면 충분).

    /// <summary>밧줄 풀기 진입 — 중복·사거리 검증 후 채널링 시작. 서버(또는 오프라인) 실행. (#369/#390)
    /// 두 갈래다: 놓아둔 체포(Captured)는 <b>누구나</b> 풀어 배회로 돌려보낼 수 있고(오검거 구제·방해 수단),
    /// 끌리는 중(Escorted)이면 <b>자기 줄만</b> 뺄 수 있다 — 줄다리기에서 손을 떼는 수단이다.
    /// 남이 끌고 있는 줄까지 풀 수 있게 하면 탈취 차단의 우회로가 된다 — 뺏을 필요도 없이 다 풀어버린다.
    /// 다른 대상을 끌고 있어도 풀기는 가능하다.</summary>
    private void ServerBeginUnrope(NpcController target)
    {
        if (m_channel.IsActive)
            return; // 묶기/풀기 채널링 중복 방지 (한 채널 공유)
        if (Loadout != null && !Loadout.HasRope)
            return; // 밧줄을 들고 있어야 풀 수 있다
        if (!CanUnrope(target))
            return; // 클라 조기검증(Rope.Use)과 단일 기준 (#184/#369)
        if (!IsInRange(target))
            return; // 사거리 밖이면 시작조차 안 함

        ServerUnropeChannelAsync(target).Forget();
    }

    /// <summary>이 대상에 밧줄 풀기를 걸 수 있는가 — 서버 가드와 클라 조기검증(Rope)이 함께 쓰는 단일 기준.</summary>
    public bool CanUnrope(NpcController target) =>
        target != null
        && (NpcStateRules.CanRelease(target.CurrentState) || Escorter.IsTetheredTo(target));

    private async UniTaskVoid ServerUnropeChannelAsync(NpcController target)
    {
        NotifyOwner($"밧줄 풀기 채널링 시작: {target.name} ({m_channelSeconds}초)");
        NotifyChannelGaugeStart(m_channelSeconds);

        // 체포 채널링과 동일한 keepAlive — 도중 거리 이탈은 즉시 실패시킨다.
        ServerChannel.Result result;
        try
        {
            result = await m_channel.RunAsync(
                m_channelSeconds,
                () => target != null && IsInRange(target)
            );
        }
        finally
        {
            NotifyChannelGaugeEnd(); // 어떤 경로로 끝나도 게이지 숨김 보장
        }

        if (result != ServerChannel.Result.Completed)
        {
            NotifyOwner("밧줄 풀기 중단 (홀드 뗌 / 거리 이탈)");
            return;
        }

        // 채널링 도중 상태가 바뀌었을 수 있다 — 완료 시점에 재확인(예: 그새 다른 플레이어가 끌기 재개).
        if (!CanUnrope(target))
            return;

        // 내 줄이 걸려 있으면 그것부터 뺀다 — 줄다리기 중이면 여기서 끝이다(남은 참가자가 계속 끈다).
        // 마지막 한 명이었으면 대상이 커스터디에서 풀려 아래 배회 복귀로 이어진다. (#390 규칙 8)
        if (Escorter.IsTetheredTo(target))
        {
            Escorter.ReleaseDrag(target);
            Escorter.RemoveTether(target);

            if (PlayerEscorter.FindEscorterOf(target) != null)
            {
                NotifyOwner($"내 밧줄만 풀었다 — 다른 참가자가 계속 확보 중: {target.name}");
                return;
            }
        }

        // 밧줄은 소모되지 않아 대상에 남은 게 없다 — 회수할 자원 없이 배회로 돌려보내기만 한다 (#369).
        NotifyOwner($"밧줄 풀기 완료 — 배회 복귀: {target.name}");
        target.ReleaseFromCustody();
    }

    // ---- 서버 실행: 본부 인계 (#414) ----

    // 인계 실행 — 대상은 클라가 지정하지 않는다. 서버가 자기 권위 상태(밧줄 목록)에서 읽으므로
    // "남이 데려온 NPC를 인계했다"는 위조가 성립할 수 없다. 상태·구역 검증과 판정은 ArrestJudge가 한다 —
    // 연행 허브가 인계존을 알 필요는 없고, 판정 기준이 한 곳(#414)에 모여 있어야 하기 때문이다.
    private void ServerDeliver()
    {
        if (IsSpawned && !IsServer)
            return;

        // 끌기(DraggingNpc)가 아니라 밧줄이 기준이다 — 인계존에 내려놓고 접수하는 경로에서는
        // 끌기가 풀려 있다. 묶여 있는 동안은 끌든 놓든 같은 대상이라 이 하나로 두 경로가 모두 덮인다.
        if (Escorter.TetheredCount == 0)
            return; // 묶어 둔 대상이 없으면 넘길 것이 없다

        ArrestJudge judge = App.Game.ArrestJudge;
        if (judge == null)
        {
            Debug.LogWarning("PlayerEscortCommands: ArrestJudge가 없어 인계 판정을 할 수 없다", this);
            return;
        }

        // 묶은 순서대로 판정한다 (#414 팀 확정 — 판정 기준이 NPC가 아니라 플레이어다).
        // 인계존 밖이거나 상태가 맞지 않는 대상은 ArrestJudge가 걸러 내고 목록에 그대로 남는다 —
        // 다시 데려와 E를 누르면 그때 판정된다(재판정 #358과 같은 취급).
        //
        // 복사해서 도는 이유: 판정에 성공한 대상은 Jailed로 넘어가고 그 순간 매 프레임 정리가
        // 목록에서 빼므로, 원본을 그대로 순회하면 도중에 컬렉션이 바뀐다.
        // 여러 명을 한 번에 끌고 왔으면(#390) 전부 순서대로 접수된다.
        List<NpcController> pending = new List<NpcController>(Escorter.ServerTethered);
        for (int i = 0; i < pending.Count; i++)
            judge.TryDeliver(pending[i]);
    }

    // ---- 공통 ----

    private bool IsInRange(NpcController target)
    {
        // 사거리 + 가시선 — 거리만 보면 위조 RPC로 벽 너머 제압·검거가 된다 (#360).
        // Interactor 없는 구성(테스트 등)은 종전대로 거리만 본다.
        return (target.transform.position - AimOriginPosition).sqrMagnitude
                <= CaptureRange * CaptureRange
            && (Interactor == null || Interactor.HasLineOfSightTo(target.transform));
    }

    // 판정 로그는 서버에서 찍히므로 원격 클라 오너는 결과를 볼 수 없다 — 오너 콘솔에도 같은 로그를 전달한다 (#91).
    // ⚠ PlayerEscorter에 같은 쌍이 하나 더 있다 — [Rpc]는 인스턴스 메서드여야 해 컴포넌트마다 필요하다.
    private void NotifyOwner(string message)
    {
        Debug.Log(message); // 서버(호스트)·오프라인 콘솔
        if (IsSpawned && IsServer && !IsOwner)
            OwnerLogRpc(message); // 원격 클라가 오너인 경우에만 전달 (호스트 오너는 위에서 이미 찍음)
    }

    [Rpc(SendTo.Owner)]
    private void OwnerLogRpc(string message) => Debug.Log($"[서버 판정] {message}");

    public override void OnNetworkDespawn()
    {
        ServerCancelCapture();
    }

    public override void OnDestroy()
    {
        m_channel.Dispose();
        base.OnDestroy(); // NetworkBehaviour의 파괴 시 네트워크 정리 — 생략하면 정리 로직이 통째로 건너뛰어진다
    }
}
