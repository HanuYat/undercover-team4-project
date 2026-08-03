using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 테이저건 아이템 — 조준한 방향으로 전극을 쏴 맞은 NPC를 기절시킨다. (GDD 8-3, #108)
/// 도주형(Run)·저항형(Attack)을 그 자리에 멈춰 세우는 것이 주 용도이며,
/// 기절 중에는 스캔·피격 반응이 걸러져 그대로 검거된다 (NpcController.ServerReactTo, #400).
/// 소지형·영구형이라 배터리 같은 소모 자원이 없다 (GDD 8-4).
///
/// <b>조준 사격</b> — 수갑·스캐너처럼 PlayerInteractor가 잡아준 대상을 쓰지 않는다.
/// 그 경로는 사거리가 상호작용 레이(3m)에 묶여 원거리 무기가 될 수 없고, 겨냥만 하면 100% 명중이라
/// 빗나갈 여지가 없다. 대신 조준 방향으로 직접 레이캐스트해 <b>맞으면 명중, 빗나가면 실패</b>다.
/// 벽·다른 오브젝트가 대상보다 앞이면 그대로 빗나간다 (엄폐 성립).
/// "앞"의 정의는 <see cref="AimOcclusion"/>가 단독으로 갖는다 — 진압봉·상호작용 가시선과 같은 규칙이다.
///
/// 서버 권위 — 오너가 조준 원점·방향을 보내면 서버가 자기 물리로 레이캐스트해 판정한다 (#55).
/// 클라가 보낸 원점은 서버가 아는 플레이어 위치와 대조해 검증한다 (원점 위조 = 벽 너머 저격 방지).
/// 채널링이 없는 즉발 아이템이라 CancelUse는 기본 구현(무동작)을 그대로 쓴다.
/// 다만 <b>발사 후 충전(쿨다운)은 채널링과 같은 원형 게이지로 표시한다</b> (#455) —
/// 진행 방향이 같아서(0→100%로 차오르고 가득 차는 순간 재발사 가능) 같은 UI가 그대로 맞는다.
/// 게이지 인프라는 ItemBase가 물려주는 ChanneledInteractionBehaviour의 것을 쓴다.
/// </summary>
public class Taser : ItemBase, IAimedWeapon
{
    [Header("테이저 설정")]
    [Tooltip("전극이 날아가는 최대 사거리(m). 상호작용 레이(PlayerInteractor.Range)와 무관하게 이 값이 기준이다")]
    [SerializeField]
    private float m_range = 8f;

    // 실측 근거: Player.prefab의 카메라는 루트에서 (0, 1.60, 0) — 즉 정상 원점-루트 거리는
    // 1.60m(섬)에서 0.80m(앉음, PlayerCrouch.HeadDrop 최대 0.8) 사이다. 여기에 네트워크 지연분을 더한다:
    // 원점을 보낸 시점과 서버가 대조하는 시점의 위치가 다르므로, 스프린트 8m/s(PlayerMovement.m_sprintSpeed)
    // 기준 150ms 어긋나면 1.2m가 벌어진다. 1.60 + 1.2 ≈ 2.8 → 3.0으로 잡았다.
    // 더 줄이면 핑 높은 플레이어의 정상 사격이 조용히 거부된다(핵심 아이템이라 치명적).
    [Tooltip("클라가 보낸 조준 원점이 서버가 아는 플레이어 위치에서 이만큼(m) 넘게 떨어져 있으면 거부한다 — 카메라 높이(1.6m) + 이동 지연 여유")]
    [SerializeField]
    private float m_originTolerance = 3f;

    // 기절 지속(NpcController.m_stunSeconds = 2.67초)보다 길게 잡는다 — 짧으면 기절이 풀리기 전에
    // 다시 쏠 수 있어 NPC를 영구히 묶어두는 무한 락이 된다. 둘 중 하나를 조정하면 이 관계를 유지할 것.
    [Tooltip("발사 후 다음 발사까지 대기 시간(초). 명중·빗나감 모두 소모한다 — 빗나가도 대가가 있어야 조준이 의미를 갖는다")]
    [SerializeField]
    private float m_cooldownSeconds = 5f;

    // 아군 오사(#252) — 동료를 맞췄을 때의 기절 시간. NPC 기절(2.67초)과 따로 둔다: 플레이어는 구조 없이
    // 스스로 일어나므로 '쓰러져 있는 동안 아무것도 못 한다'가 곧 대가이고, 그 길이를 따로 잡아야 한다.
    [Tooltip("동료를 맞췄을 때 기절 시간(초). 쿨다운(m_cooldownSeconds)보다 짧게 둘 것 — 같거나 길면 일어나는 순간 다시 쏴서 한 명을 영구히 묶을 수 있다")]
    [SerializeField]
    private float m_playerStunSeconds = 5f;

    // 다음 발사가 가능해지는 시각. 판정자가 서버 하나뿐이라 동기화하지 않는다 (서버 전용 상태).
    // 아이템 인스턴스에 붙어 있으므로 버리고 다시 주워도 충전 상태가 따라간다.
    private float m_nextFireTime;

    // 조준 히트 버퍼 — 크로스헤어(HasValidAimTarget)가 매 프레임 도는 경로라
    // RaycastAll(호출마다 배열 할당) 대신 NonAlloc + 고정 버퍼를 쓴다. (Baton.s_hitBuffer와 동일 관례)
    private static readonly RaycastHit[] s_aimBuffer = new RaycastHit[16];

    // ---- ItemBase ----

    // CanTarget은 재정의하지 않는다(기본 false) — 조준 사격이라 조준 대상 윤곽선(#184)이 없는 게 맞다.
    // 윤곽선을 켜면 상호작용 레이(3m) 기준으로 떠서, 실제 사거리(8m)와 어긋난 표시가 된다.

    /// <summary>
    /// 아이템 사용 진입점. 겨냥 대상(aimTarget)은 쓰지 않는다 — 조준 방향으로 직접 쏘기 때문이다.
    /// 오너는 조준 원점·방향만 넘기고, 명중 판정은 전적으로 서버가 수행한다.
    /// </summary>
    public override void Use(GameObject aimTarget)
    {
        // 조준 기준은 든 플레이어의 AimOrigin(카메라) — 아이템은 줍기/버리기로 부모가 바뀌므로
        PlayerInteractor interactor = Holder;
        if (interactor == null)
        {
            Debug.LogWarning("Taser: PlayerInteractor를 찾지 못함 — 조준 기준 없음", this);
            return;
        }

        Transform aim = interactor.AimOrigin;
        Vector3 origin = aim.position;
        Vector3 direction = aim.forward;

        // 서버(호스트)·오프라인은 로컬 참조로 즉시 실행
        if (!IsSpawned || IsServer)
        {
            ServerFire(origin, direction);
            return;
        }

        if (!IsOwner)
        {
            return;
        }

        RequestFireRpc(origin, direction);
    }

    [Rpc(SendTo.Server)]
    private void RequestFireRpc(Vector3 origin, Vector3 direction)
    {
        ServerFire(origin, direction);
    }

    // ---- 서버 판정 ----

    /// <summary>
    /// 서버에서 조준 사격을 판정한다. 클라가 보낸 원점·방향은 신뢰할 수 없으므로,
    /// 원점이 서버가 아는 플레이어 위치 근처인지 확인한 뒤 서버 물리로 레이캐스트한다.
    /// (원점 검증이 없으면 위조 RPC로 맵 어디서든, 벽 너머로도 쏠 수 있다)
    /// </summary>
    private void ServerFire(Vector3 origin, Vector3 direction)
    {
        // 스폰 전(오프라인)엔 IsServer 캐시가 아직 갱신되지 않아 false일 수 있으므로,
        // "스폰된 상태에서 서버가 아닐 때"만 차단한다. (Scanner.ServerBeginScan 관례)
        if (IsSpawned && !IsServer)
        {
            return;
        }

        if (direction.sqrMagnitude < 0.0001f)
        {
            return; // 방향이 0벡터면 레이를 만들 수 없다 (위조·직렬화 사고 방어)
        }

        if (!IsOriginPlausible(origin))
        {
            Debug.LogWarning($"Taser: 조준 원점이 플레이어 위치와 너무 멀다 — 사격 거부 (origin={origin})", this);
            return;
        }

        // 쿨다운은 유효성 검사를 전부 통과한 뒤에 본다 — 위조·사고로 거부된 요청이 충전을 깎으면
        // 정상 사격이 엉뚱하게 막힌다. 여기부터는 "실제로 발사했다"로 취급한다.
        if (Time.time < m_nextFireTime)
        {
            NotifyOwner($"테이저 충전 중 — {m_nextFireTime - Time.time:F1}초 남음");
            return;
        }

        // 명중 여부와 무관하게 소모한다 — 빗나감에 대가가 없으면 조준할 이유가 사라진다.
        m_nextFireTime = Time.time + m_cooldownSeconds;

        // 충전 게이지 — 쏜 사람 화면에만 (#455). 유효성 검사를 모두 통과한 뒤라 여기가
        // "실제로 발사했다"가 확정되는 지점이고, 거부된 요청에는 충전도 게이지도 걸리지 않는다.
        // 명중 판정보다 앞에 두는 이유: 빗나가도 충전은 소모되므로 게이지도 같이 떠야 한다.
        NotifyChannelGaugeStart(m_cooldownSeconds);

        // 명중 판정은 EvaluateAim이 단일 규칙으로 수행한다 — 클라 크로스헤어(#328)와 공유해 색↔명중을 일치시킨다.
        switch (
            EvaluateAim(
                origin, direction, out NpcController target,
                out PlayerIncapacitation playerTarget, out RaycastHit hit))
        {
            case AimResult.NoHit:
                NotifyOwner("테이저 빗나감 — 허공");
                return;
            case AimResult.HitNonTarget:
                NotifyOwner($"테이저 빗나감 — {hit.collider.name}에 맞음");
                return;
            case AimResult.TargetInvalidState:
                // 소리는 낸다 — 전극은 실제로 몸에 닿았다. 침묵하면 입력이 씹힌 것처럼 보인다.
                // (진압봉의 같은 분기와 동일한 방침, #478)
                PlayImpact(hit.point);
                NotifyOwner(
                    playerTarget != null
                        ? $"테이저 무효 — 이미 무력화된 동료 ({playerTarget.name})"
                        : $"테이저 무효 — 이미 기절한 대상 ({target.name})");
                return;
        }

        // 명중 — 대상이 로봇이든 사람이든 같은 소리다. 전기는 몸체를 가리지 않는다.
        PlayImpact(hit.point);

        // 동료를 맞췄다 — 아군 오사 (#252). NPC와 달리 위협 개념이 없다(도주할 상대가 아니다).
        // 구조 없이 시간이 지나면 스스로 일어나고, 전멸 판정에도 잡히지 않는다 (IncapacitationCause.Stun).
        if (playerTarget != null)
        {
            playerTarget.ServerStun(m_playerStunSeconds);
            NotifyOwner($"테이저 명중 — 동료 오사! {playerTarget.name} ({m_playerStunSeconds}초 기절)");
            return;
        }

        // 쏜 사람을 위협으로 넘긴다 — 기절이 풀리면 이 사람에게서 도망친다 (#269)
        PlayerInteractor shooter = Holder;
        // 원인을 명시한다 — 감전 연출(#477)이 붙는 유일한 경로다. 체력 0 쓰러짐(#366)은 기본값
        // Knockdown으로 남아 전기 연출 없이 지나간다(색 언어상 시안은 테이저 전용).
        target.EnterStunned(
            shooter != null ? shooter.transform : null,
            null,
            NpcStunCause.Taser
        );
        NotifyOwner($"테이저 명중: {target.name} ({target.StunSeconds}초 기절)");
    }

    // ---- 피격 연출 (#477 일부) ----

    /// <summary>
    /// 명중 지점의 감전음을 전 피어에 전파한다 — 서버 판정 지점에서만 호출한다.
    /// <b>기절이 지속되는 동안 울리는 소리가 아니라 맞는 순간의 원샷이다.</b> 기절은 지속 상태라
    /// 아키텍처 규칙상 동기화 값으로 구동해야 하는데(docs/architecture.md 연출 전파 규칙),
    /// 그건 몸 전기 아크·화면 지직과 함께 #477 본체에서 다룬다. 여기서는 "맞았다"만 들려준다.
    /// </summary>
    private void PlayImpact(Vector3 point)
    {
        if (!IsSpawned)
        {
            ApplyImpactFeedback(point); // 오프라인 — RPC 경로가 없다
            return;
        }

        PlayImpactRpc(point);
    }

    [Rpc(SendTo.Everyone)]
    private void PlayImpactRpc(Vector3 point) => ApplyImpactFeedback(point);

    // 매니저가 없는 구성(부트스트랩 없는 씬 직접 Play)에서는 조용히 넘어간다 (R8 관례).
    // 먼지는 내지 않는다 — 전기는 충격이 아니라서 흙먼지가 일 이유가 없다.
    private static void ApplyImpactFeedback(Vector3 point) =>
        App.Sound?.PlaySfxAt(EAudioClip.TaserHit, point);

    // ---- 조준 판정 (서버 사격 · 클라 크로스헤어 공유, #328) ----

    private enum AimResult { NoHit, HitNonTarget, TargetInvalidState, ValidTarget }

    /// <summary>
    /// 조준 원점·방향으로 사거리(m_range) 레이캐스트해 명중 결과를 분류한다 (레이캐스트 1회).
    /// 서버 사격 판정(ServerFire)과 오너 크로스헤어 색(#328)이 이 한 규칙을 공유한다.
    /// 마스크 ~0 + 트리거 무시. 히트를 전부 받아 <see cref="AimOcclusion.FindNearestByPivot"/>로
    /// 하나를 고르며, 그 기준이 벽 엄폐의 정의다.
    /// </summary>
    private AimResult EvaluateAim(
        Vector3 origin,
        Vector3 direction,
        out NpcController target,
        out PlayerIncapacitation playerTarget,
        out RaycastHit hit)
    {
        target = null;
        playerTarget = null;
        hit = default;

        if (direction.sqrMagnitude < 0.0001f)
            return AimResult.NoHit; // 0벡터 방향은 레이를 만들 수 없다 (위조·직렬화 사고 방어)

        int count = Physics.RaycastNonAlloc(
            origin, direction.normalized, s_aimBuffer, m_range, ~0, QueryTriggerInteraction.Ignore);

        // 제외 계층은 넘기지 않는다 — 레이는 원점을 감싼 콜라이더를 애초에 감지하지 않고,
        // 자기 자신을 맞추는 예외는 EvaluatePlayerAim이 명시적으로 걸러낸다.
        int index = AimOcclusion.FindNearestByPivot(origin, s_aimBuffer, count, null);
        if (index < 0)
            return AimResult.NoHit;

        hit = s_aimBuffer[index];

        // 콜라이더가 루트의 자식일 수 있으므로 부모까지 탐색한다 (Rope.ResolveTarget과 동일 관례).
        // 벽·소품을 맞췄으면 그대로 빗나감이고, 동료를 맞췄으면 아군 오사다 (#252).
        NpcController npc = hit.collider.GetComponentInParent<NpcController>();
        if (npc == null)
            return EvaluatePlayerAim(hit, out playerTarget);

        // 상태 게이트는 사라졌다 — 스턴이 오버레이가 되면서 전 상태에 걸린다 (#292).
        // 확보·페널티 상태도 3초 얼었다가 원래 하던 일을 그대로 재개하므로 막을 이유가 없다.
        // (타격 피해는 여전히 막힌다 — NpcStateRules.CanBeDamaged)
        target = npc;

        // 남은 무효 케이스는 하나 — 이미 기절해 있는 대상이다. EnterStunned가 no-op이라
        // 그냥 통과시키면 탄만 쓰고 "명중"이 뜬다. 크로스헤어(#328)도 이 판정을 공유한다.
        if (npc.IsStunned)
            return AimResult.TargetInvalidState;

        return AimResult.ValidTarget;
    }

    // 동료 명중 판정 (#252). 소지자 자신은 대상이 아니다 — 카메라 원점이 자기 캡슐 안이라 보통 안 맞지만,
    // 앉기·넉백으로 원점이 몸 밖으로 나가는 순간 자기를 쏠 수 있어 명시적으로 막는다.
    private AimResult EvaluatePlayerAim(RaycastHit hit, out PlayerIncapacitation playerTarget)
    {
        playerTarget = hit.collider.GetComponentInParent<PlayerIncapacitation>();
        if (playerTarget == null)
            return AimResult.HitNonTarget;

        if (playerTarget == GetComponentInParent<PlayerIncapacitation>())
        {
            playerTarget = null;
            return AimResult.HitNonTarget; // 자기 자신
        }

        // 이미 무력화된 동료는 무효 — 다운을 기절로 덮어써 구조 대상에서 빼버리면 안 된다
        // (ServerStun도 같은 가드를 갖지만, 여기서 걸러야 탄만 쓰고 '명중'이 뜨지 않는다)
        return playerTarget.IsIncapacitated ? AimResult.TargetInvalidState : AimResult.ValidTarget;
    }

    /// <summary>
    /// 조준선이 스턴 가능한 대상(NPC·동료)에 닿는지 — 오너 크로스헤어 색 예측용(#328). 서버 판정과 동일 규칙이다.
    /// 동료를 겨눠도 켜진다 — 쏘면 실제로 맞으므로, 오사를 피하려면 그게 보여야 한다 (#252).
    /// 로컬 물리로 매 프레임 호출해도 되도록 순수 조회다(원점 검증·쿨다운과 무관).
    /// </summary>
    public bool HasValidAimTarget(Vector3 origin, Vector3 direction)
        => EvaluateAim(origin, direction, out _, out _, out _) == AimResult.ValidTarget;

    /// <summary>
    /// 클라가 보낸 조준 원점이 서버가 아는 이 아이템 소지자 위치 근처인지 — 원점 위조 방어.
    /// 아이템은 플레이어에 부착돼 있고(#88) 부모 변경은 서버가 수행하므로,
    /// 부모 플레이어의 위치는 서버 권위 값이다. 카메라는 오너 로컬이라 서버가 알 수 없어
    /// 방향은 클라를 믿되(FPS 통상), 원점만 거리로 묶는다.
    /// </summary>
    private bool IsOriginPlausible(Vector3 origin)
    {
        PlayerInteractor holder = Holder;
        if (holder == null)
        {
            return false; // 아무에게도 안 들린 아이템이 쏠 수는 없다
        }

        return (origin - holder.transform.position).sqrMagnitude
            <= m_originTolerance * m_originTolerance;
    }

    /// <summary>
    /// 버리기 등 소유권 이전 경로에서 서버가 직접 충전 게이지를 내린다 (ItemBase 훅, #455).
    /// 손에 없는 아이템의 충전이 화면에 남지 않게 하는 것뿐이다 — 충전 자체(m_nextFireTime)는
    /// 아이템 인스턴스에 남아 다시 주웠을 때 그대로 이어진다.
    /// 오너 라우팅은 기반(ChanneledInteractionBehaviour)이 처리하므로 여기서는 그냥 부르면 된다:
    /// 호스트 오너는 로컬로, 원격 오너에게는 SendTo.Owner RPC로 나간다.
    ///
    /// 슬롯을 바꿔 손에서 내리는 경우는 이 훅이 아니라 <c>PlayerLoadout.EquipSlot</c>이 게이지를 내린다 —
    /// 그쪽은 아이템 종류를 가리지 않는 오너 로컬 처리라 여기에 중복으로 둘 필요가 없다.
    /// <c>CancelUse</c>를 쓰지 않는 이유도 같은 맥락이다: 그건 <b>좌클릭 뗌</b>에도 불려서,
    /// 발사 직후 버튼을 떼는 순간 충전 게이지가 사라져 버린다.
    /// </summary>
    public override void ServerCancelActiveUse() => NotifyChannelGaugeEnd();

    /// <summary>
    /// 다시 장착됐다 — 아직 충전 중이면 남은 만큼 게이지를 이어 띄운다 (#455).
    /// 오너 클라에서만 불린다(PlayerLoadout.EquipSlot). 충전 시각(m_nextFireTime)은 서버 전용 상태라
    /// 원격 오너는 남은 시간을 모르므로 서버에 물어본다 — 게이지가 한 왕복만큼 늦게 뜨는 것은 감수한다.
    /// 동기화 변수로 바꾸지 않은 이유: 판정자는 서버 하나뿐이고, 이 값이 필요한 곳은 이 표시뿐이다.
    /// </summary>
    public override void OnEquipped()
    {
        // 호스트 오너·오프라인은 서버 상태를 직접 읽을 수 있다
        if (!IsSpawned || IsServer)
        {
            ServerReportCharge();
            return;
        }

        RequestChargeGaugeRpc();
    }

    // 아이템은 소지자 소유라 기본 권한(오너 전용)으로 충분하다 — RequestFireRpc와 동일.
    [Rpc(SendTo.Server)]
    private void RequestChargeGaugeRpc() => ServerReportCharge();

    // 남은 충전을 오너 화면 게이지로 되돌린다. 서버(또는 오프라인) 전용.
    private void ServerReportCharge()
    {
        float remaining = m_nextFireTime - Time.time;
        if (remaining <= 0f)
            return; // 충전 완료(또는 한 번도 안 쏨) — 띄울 것이 없다

        // 남은 시간을 duration으로 주면 게이지가 0%에서 다시 차오른다 —
        // 전체 쿨다운과 경과분을 함께 넘겨 중간부터 잇는다.
        NotifyChannelGaugeStart(m_cooldownSeconds, m_cooldownSeconds - remaining);
    }
}
