using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 테이저건 아이템 — 조준한 방향으로 전극을 쏴 맞은 NPC를 기절시킨다. (GDD 8-3, #108)
/// 도주형(Run)·저항형(Attack)을 그 자리에 멈춰 세우는 것이 주 용도이며,
/// 기절 중에는 수갑 채널링이 반응 없이 즉시 연행으로 이어진다 (PlayerEscorter.ResolveReaction).
/// 소지형·영구형이라 배터리 같은 소모 자원이 없다 (GDD 8-4).
///
/// <b>조준 사격</b> — 수갑·스캐너처럼 PlayerInteractor가 잡아준 대상을 쓰지 않는다.
/// 그 경로는 사거리가 상호작용 레이(3m)에 묶여 원거리 무기가 될 수 없고, 겨냥만 하면 100% 명중이라
/// 빗나갈 여지가 없다. 대신 조준 방향으로 직접 레이캐스트해 <b>맞으면 명중, 빗나가면 실패</b>다.
/// 벽·다른 오브젝트가 먼저 맞으면 그대로 빗나간다 (레이가 첫 충돌에서 멈추므로 엄폐가 성립).
///
/// 서버 권위 — 오너가 조준 원점·방향을 보내면 서버가 자기 물리로 레이캐스트해 판정한다 (#55).
/// 클라가 보낸 원점은 서버가 아는 플레이어 위치와 대조해 검증한다 (원점 위조 = 벽 너머 저격 방지).
/// 채널링이 없는 즉발 아이템이라 CancelUse는 기본 구현(무동작)을 그대로 쓴다.
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

    // 다음 발사가 가능해지는 시각. 판정자가 서버 하나뿐이라 동기화하지 않는다 (서버 전용 상태).
    // 아이템 인스턴스에 붙어 있으므로 버리고 다시 주워도 충전 상태가 따라간다.
    private float m_nextFireTime;

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
        // 캐시하지 않고 사용 시점에 해석한다 (Scanner.IsInRange 관례).
        PlayerInteractor interactor = GetComponentInParent<PlayerInteractor>();
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

        // 명중 판정은 EvaluateAim이 단일 규칙으로 수행한다 — 클라 크로스헤어(#328)와 공유해 색↔명중을 일치시킨다.
        switch (EvaluateAim(origin, direction, out NpcController target, out RaycastHit hit))
        {
            case AimResult.NoHit:
                NotifyOwner("테이저 빗나감 — 허공");
                return;
            case AimResult.HitNonTarget:
                NotifyOwner($"테이저 빗나감 — {hit.collider.name}에 맞음");
                return;
            case AimResult.TargetInvalidState:
                NotifyOwner($"테이저 무효 — 이미 기절한 대상 ({target.name})");
                return;
        }

        // 쏜 사람을 위협으로 넘긴다 — 기절이 풀리면 이 사람에게서 도망친다 (#269)
        PlayerInteractor shooter = GetComponentInParent<PlayerInteractor>();
        target.EnterStunned(shooter != null ? shooter.transform : null);
        NotifyOwner($"테이저 명중: {target.name} ({target.StunSeconds}초 기절)");
    }

    // ---- 조준 판정 (서버 사격 · 클라 크로스헤어 공유, #328) ----

    private enum AimResult { NoHit, HitNonTarget, TargetInvalidState, ValidTarget }

    /// <summary>
    /// 조준 원점·방향으로 사거리(m_range) 레이캐스트해 명중 결과를 분류한다 (레이캐스트 1회).
    /// 서버 사격 판정(ServerFire)과 오너 크로스헤어 색(#328)이 이 한 규칙을 공유한다.
    /// 마스크 ~0 + 트리거 무시 — "먼저 맞은 것"이 결과라 벽 엄폐가 성립한다.
    /// </summary>
    private AimResult EvaluateAim(Vector3 origin, Vector3 direction, out NpcController target, out RaycastHit hit)
    {
        target = null;
        hit = default;

        if (direction.sqrMagnitude < 0.0001f)
            return AimResult.NoHit; // 0벡터 방향은 레이를 만들 수 없다 (위조·직렬화 사고 방어)

        if (!Physics.Raycast(origin, direction.normalized, out hit, m_range, ~0, QueryTriggerInteraction.Ignore))
            return AimResult.NoHit;

        // 콜라이더가 NPC 루트의 자식일 수 있으므로 부모까지 탐색한다 (Handcuffs.ResolveTarget과 동일 관례).
        // 벽·소품·플레이어를 맞췄으면 그대로 빗나감이다.
        NpcController npc = hit.collider.GetComponentInParent<NpcController>();
        if (npc == null)
            return AimResult.HitNonTarget;

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

    /// <summary>
    /// 조준선이 스턴 가능한 NPC에 닿는지 — 오너 크로스헤어 색 예측용(#328). 서버 판정과 동일 규칙이다.
    /// 로컬 물리로 매 프레임 호출해도 되도록 순수 조회다(원점 검증·쿨다운과 무관).
    /// </summary>
    public bool HasValidAimTarget(Vector3 origin, Vector3 direction)
        => EvaluateAim(origin, direction, out _, out _) == AimResult.ValidTarget;

    /// <summary>
    /// 클라가 보낸 조준 원점이 서버가 아는 이 아이템 소지자 위치 근처인지 — 원점 위조 방어.
    /// 아이템은 플레이어에 부착돼 있고(#88) 부모 변경은 서버가 수행하므로,
    /// 부모 플레이어의 위치는 서버 권위 값이다. 카메라는 오너 로컬이라 서버가 알 수 없어
    /// 방향은 클라를 믿되(FPS 통상), 원점만 거리로 묶는다.
    /// </summary>
    private bool IsOriginPlausible(Vector3 origin)
    {
        PlayerInteractor holder = GetComponentInParent<PlayerInteractor>();
        if (holder == null)
        {
            return false; // 아무에게도 안 들린 아이템이 쏠 수는 없다
        }

        return (origin - holder.transform.position).sqrMagnitude
            <= m_originTolerance * m_originTolerance;
    }

    // ---- 오너 로그 피드백 ----

    // 판정 로그는 서버에서 찍히므로 원격 클라 오너는 결과를 볼 수 없다 — 오너 콘솔에도 같은 로그를 전달한다.
    // Scanner.NotifyOwner / PlayerEscorter.NotifyOwner와 동일 패턴 (#91).
    private void NotifyOwner(string message)
    {
        Debug.Log(message); // 서버(호스트)·오프라인 콘솔
        if (IsSpawned && IsServer && !IsOwner)
            OwnerLogRpc(message); // 원격 클라가 오너인 경우에만 전달 (호스트 오너는 위에서 이미 찍음)
    }

    [Rpc(SendTo.Owner)]
    private void OwnerLogRpc(string message) => Debug.Log($"[서버 판정] {message}");
}
