using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 다운된 동료 구조(리바이브) — 서버 권위 채널링. (#105, GDD 7-5)
/// 오너가 다운된 아군을 조준한 채 상호작용 버튼을 누르고 있으면(홀드) 서버가 T초 채널링을 돌리고,
/// 완료 시 대상의 HP를 일부 회복시켜 무력화를 해제한다(PlayerHealth.ServerRevive).
/// 버튼을 떼거나 대상이 사거리를 벗어나면 실패. 서버 권위·RPC 구조는 PlayerEscorter를 본뜬다.
///
/// <b>⚠ #524로 현장 구조 채널링은 휴면 상태다.</b> HP 0이 곧 <c>Die</c>가 되어 <c>Down</c>이 발생하지
/// 않으므로, 구조 진입점(<see cref="HandleInteractStarted"/>)이 대상을 찾지 못해 항상 무동작으로 끝난다.
/// 지우지 않고 남긴 이유는 되살리는 값이 한 줄이기 때문이다 — <c>PlayerHealth.SetHp</c>의 무력화 원인을
/// <c>Down</c>으로 되돌리면 이 컴포넌트와 HUD의 다운 분기가 그대로 다시 동작한다.
///
/// <b>프리팹에서 떼지 말 것:</b> <c>PlayerReviveHud</c>가 <c>[RequireComponent]</c>로 걸고 있고,
/// 기능 정지된 아군 조준 안내(<see cref="CurrentDeadTarget"/>)는 지금도 살아 있는 경로다.
/// </summary>
[RequireComponent(typeof(PlayerInputHandler))]
public class PlayerReviver : ChanneledInteractionBehaviour
{
    [Header("구조 채널링 (서버 권위)")]
    [Tooltip("구조 채널링 시간(초)")]
    [SerializeField] private float m_reviveSeconds = 3f;

    // 사거리는 조준·윤곽선과 같은 기준 — PlayerInteractor.Range 재사용 (#147 패턴, #184)
    private const float k_fallbackRange = 3f; // 테스트 구성 등 PlayerInteractor가 없을 때

    private PlayerInputHandler m_inputHandler;
    private PlayerInteractor m_interactor;      // 조준 대상 조회용
    private PlayerIncapacitation m_incapacitation; // 내가 다운 중이면 구조 불가
    private PlayerHealth m_selfHealth;              // 자기 자신 제외 판정용

    // 서버 채널링 생명주기(CTS 소유·재진입 가드)는 ServerChannel에 위임 (#109)
    private readonly ServerChannel m_channel = new();

    /// <summary>지금 조준 중인 '다운된 아군'. 없으면 null. 임시 구조 HUD 프롬프트용(오너 전용). (#105)</summary>
    public PlayerHealth CurrentReviveTarget => IsOwner ? FindAllyTarget(IncapacitationCause.Down) : null;

    /// <summary>
    /// 지금 조준 중인 '기능 정지(Die)된 아군'. 없으면 null. 구조 불가 안내용(오너 전용). (#364)
    /// 히트박스가 Die에서도 켜져 윤곽선은 잡히는데(운반 조준용, #365) 구조는 거부되므로,
    /// 안내가 없으면 "조준은 되는데 홀드해도 아무 일이 없는" 상태가 된다.
    /// </summary>
    public PlayerHealth CurrentDeadTarget => IsOwner ? FindAllyTarget(IncapacitationCause.Die) : null;

    public override void OnNetworkSpawn()
    {
        m_inputHandler = GetComponent<PlayerInputHandler>();
        m_interactor = GetComponent<PlayerInteractor>();
        m_incapacitation = GetComponent<PlayerIncapacitation>();
        m_selfHealth = GetComponent<PlayerHealth>();

        // 입력 구독은 오너만 — 서버 RPC 수신·채널링은 enabled와 무관하게 동작하므로
        // (PlayerEscorter처럼) 컴포넌트를 비활성화하지 않는다.
        if (IsOwner)
        {
            m_inputHandler.OnInteractStarted += HandleInteractStarted;
            m_inputHandler.OnInteractCanceled += HandleInteractCanceled;
        }
    }

    public override void OnNetworkDespawn()
    {
        if (IsOwner)
        {
            m_inputHandler.OnInteractStarted -= HandleInteractStarted;
            m_inputHandler.OnInteractCanceled -= HandleInteractCanceled;
        }
        ServerCancelRevive();
    }

    // ---- 오너 입력 핸들러 ----

    private void HandleInteractStarted()
    {
        // 내가 다운 중이면 구조할 수 없다
        if (m_incapacitation != null && m_incapacitation.IsIncapacitated)
            return;

        PlayerHealth target = FindAllyTarget(IncapacitationCause.Down);
        if (target != null)
            RequestBeginRevive(target);
    }

    private void HandleInteractCanceled() => RequestCancelRevive();

    // 조준 중인 대상이 지정한 무력화 원인의 아군이면 그 PlayerHealth를, 아니면 null을 반환한다. (#105, #364)
    private PlayerHealth FindAllyTarget(IncapacitationCause cause)
    {
        GameObject targetObj = m_interactor != null ? m_interactor.CurrentTarget : null;
        if (targetObj == null)
            return null;

        PlayerHealth target = targetObj.GetComponentInParent<PlayerHealth>();
        if (target == null || target == m_selfHealth)
            return null; // 자기 자신 제외

        PlayerIncapacitation targetIncap = target.GetComponent<PlayerIncapacitation>();
        return targetIncap != null && targetIncap.Cause == cause ? target : null;
    }

    // ---- 오너 클라 진입점 (서버/오프라인은 즉시 실행, 원격 클라는 서버로 요청) ----

    /// <summary>구조 채널링 시작 요청 — 오너가 호출.</summary>
    public void RequestBeginRevive(PlayerHealth target)
    {
        if (target == null)
            return;

        // 서버(호스트 포함)·오프라인은 로컬 참조로 바로 실행 (PlayerEscorter.RequestCapture 관례)
        if (!IsSpawned || IsServer)
        {
            ServerBeginRevive(target);
            return;
        }
        if (!IsOwner)
            return; // 남의 플레이어 오브젝트에서 온 호출 방지
        if (!IsTargetNetworkReady(target))
            return;
        BeginReviveRpc(new NetworkObjectReference(target.NetworkObject));
    }

    /// <summary>구조 채널링 취소 요청 — 오너가 호출(버튼 뗌).</summary>
    public void RequestCancelRevive()
    {
        if (!IsSpawned) { ServerCancelRevive(); return; }
        if (!IsOwner) return;
        CancelReviveRpc();
    }

    // 원격 클라 → 서버로 대상을 넘기려면 스폰돼 있어야 한다(NetworkObjectReference 제약).
    private bool IsTargetNetworkReady(PlayerHealth target)
    {
        if (target.NetworkObject != null && target.NetworkObject.IsSpawned)
            return true;
        Debug.LogWarning($"구조 요청 무시 — 대상이 네트워크 스폰되지 않음: {target.name}", this);
        return false;
    }

    // ---- 서버 RPC (오너 → 서버) ----

    [Rpc(SendTo.Server)]
    private void BeginReviveRpc(NetworkObjectReference targetRef)
    {
        if (targetRef.TryGet(out NetworkObject targetObj) &&
            targetObj.TryGetComponent(out PlayerHealth target))
        {
            ServerBeginRevive(target);
        }
    }

    [Rpc(SendTo.Server)]
    private void CancelReviveRpc() => ServerCancelRevive();

    // ---- 서버 실행 (권위) ----

    private void ServerBeginRevive(PlayerHealth target)
    {
        if (m_channel.IsActive || target == null)
            return;

        // --- [Issue #148] 변조된 클라이언트의 비정상 RPC 호출 방어를 위한 서버 측 검증 ---
        
        // 1. 구조자가 다운된 상태인지 검증
        if (m_incapacitation != null && m_incapacitation.IsIncapacitated)
        {
            Debug.LogWarning($"[Server] 다운 상태인 플레이어({m_selfHealth.name})가 구조를 시도하여 거부됨.");
            return;
        }

        // 2. 구조 대상이 자기 자신인지 검증 (자가 구조 방지)
        if (target == m_selfHealth)
        {
            Debug.LogWarning($"[Server] 플레이어({m_selfHealth.name})가 자가 구조(Self-revive)를 시도하여 거부됨.");
            return;
        }
        // --------------------------------------------------------------------------------

        PlayerIncapacitation targetIncap = target.GetComponent<PlayerIncapacitation>();
        if (targetIncap == null || !targetIncap.IsDowned)
        {
            // HP0 다운만 구조 대상 — 매달기(#101)·기절(#252)은 스스로 풀린다
            // (둘 다 히트박스가 꺼져 조준도 안 되지만 위조 RPC 방어로 여기서도 본다)
            // Die는 히트박스가 켜져 있어 실제로 여기까지 온다 — 조준·홀드가 되는데 침묵하면 버그로 보인다 (#364)
            if (targetIncap != null && targetIncap.IsDead)
                NotifyOwner($"구조 불가 — {target.name}은 기능 정지 상태다. 부활 키트로 일으켜야 한다 (#613)");
            return;
        }
        if (!IsInRange(target))
            return; // 사거리 밖이면 시작조차 안 함

        ServerChannelAsync(target, targetIncap).Forget();
    }

    private async UniTaskVoid ServerChannelAsync(PlayerHealth target, PlayerIncapacitation targetIncap)
    {
        NotifyOwner($"구조 채널링 시작: {target.name} ({m_reviveSeconds}초)");
        NotifyChannelGaugeStart(m_reviveSeconds);

        // 대상이 구조 대상이 아니게 되는 순간 즉시 끊는다 (#364) — 구조 제한시간이 채널링 도중 끝나
        // Die로 떨어졌는데 게이지만 끝까지 차오르면, 다 채운 뒤에 실패를 통보받는 꼴이 된다.
        // 거리 검사는 종전대로 완료 시점에만 한다 — 여기 넣으면 채널링 중 한 발짝 어긋나도 즉시 실패다.
        ServerChannel.Result result;
        try
        {
            result = await m_channel.RunAsync(
                m_reviveSeconds, () => target != null && targetIncap.IsDowned);
        }
        finally
        {
            // 완료·뗌·예외 어떤 경로로 끝나도 게이지 숨김을 보장한다 (#184)
            NotifyChannelGaugeEnd();
        }

        switch (result)
        {
            case ServerChannel.Result.Canceled:
                NotifyOwner("구조 취소됨 (홀드 뗌)");
                return;

            case ServerChannel.Result.OutOfRange:
                // 여기서는 '거리 이탈'이 아니라 대상이 구조 대상에서 벗어난 것이다 (keepAlive, #364).
                NotifyOwner(
                    target != null && targetIncap.IsDead
                        ? $"구조 중단 — 제한시간 초과로 기능 정지됨: {target.name} (본부 이송 필요)"
                        : "구조 중단 — 대상이 구조 대상이 아니게 됨");
                return;

            case ServerChannel.Result.Completed:
                break;
        }

        // 채널링 동안 대상이 파괴됐거나 사거리를 벗어났으면 실패
        if (target == null || !IsInRange(target))
        {
            NotifyOwner("구조 실패 — 대상이 범위를 벗어남");
            return;
        }
        // 다른 동료가 먼저 살렸다면 중복 구조 방지.
        // 채널링(3초) 도중 구조 제한시간이 끝나 Die로 떨어졌을 수도 있다 — 한 발 늦은 구조는 실패다 (#364)
        if (!targetIncap.IsDowned)
        {
            NotifyOwner(
                targetIncap.IsDead
                    ? $"구조 실패 — 제한시간 초과로 기능 정지됨: {target.name} (본부 이송 필요)"
                    : "구조 취소 — 대상이 이미 복구됨"
            );
            return;
        }

        NotifyOwner($"구조 완료: {target.name}");
        target.ServerRevive();
    }

    private void ServerCancelRevive() => m_channel.Cancel();

    private bool IsInRange(PlayerHealth target) =>
        PlayerInteractor.IsWithinReach(
            m_interactor,
            target.transform,
            PlayerInteractor.RangeOf(m_interactor, k_fallbackRange),
            transform.position);

    // 채널링 게이지와 오너 피드백(NotifyOwner)은 기반 ChanneledInteractionBehaviour가 제공한다. (#184/#91)

    public override void OnDestroy()
    {
        m_channel.Dispose();
        base.OnDestroy(); // NetworkBehaviour 내부 정리 — 반드시 호출
    }
}