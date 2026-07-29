using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 진압봉 아이템 — 조준 방향을 근접 타격해 맞은 NPC의 체력을 깎는다. (GDD 7-4, #217)
/// 체력이 0에 도달하면 기절(Stunned)하는데, 그 판정은 <see cref="NpcController.TakeDamage"/>의
/// 엣지 트리거가 담당하므로 여기서는 데미지만 넣는다 (#366).
///
/// <b>조준 타격</b> — 테이저와 같은 방식이다. PlayerInteractor가 잡아준 대상을 쓰지 않고
/// 조준 방향으로 직접 캐스트해 <b>맞으면 명중, 빗나가면 실패</b>다. 다만 근접 사거리라
/// 점 레이캐스트는 조준이 과하게 빡빡해서 <see cref="Physics.SphereCastNonAlloc"/>로 두께를 준다.
/// 벽·소품이 먼저 맞으면 그대로 빗나간다(가장 가까운 것만 판정 — 엄폐가 성립).
/// 다른 플레이어를 맞추면 '빗나감'이다 — 아군 오사는 없다.
///
/// 서버 권위 — 오너가 조준 원점·방향을 보내면 서버가 자기 물리로 캐스트해 판정한다 (#55).
/// 클라가 보낸 원점은 서버가 아는 플레이어 위치와 대조해 검증한다 (원점 위조 = 벽 너머 타격 방지).
///
/// <b>판정은 즉발이 아니라 임팩트 프레임에 일어난다</b> — 좌클릭 순간에 데미지를 넣으면 봉이
/// 아직 뒤로 젖혀져 있는데 NPC가 먼저 맞는 그림이 된다. 그래서 스윙 애니메이션만 먼저 전 피어에
/// 재생시키고, <see cref="PlayerAnimationDriver.k_swingImpactSeconds"/>만큼 기다렸다가 캐스트한다.
/// 1인칭·3인칭 애니메이션이 모두 같은 상수로 임팩트를 맞추므로, 세 곳(내 화면·남의 화면·데미지)이
/// 한 순간에 일어난다.
///
/// 홀드 채널링은 아니다 — 좌클릭을 떼도 이미 시작된 스윙은 그대로 들어간다(CancelUse 기본 구현 유지).
/// 취소되는 경우는 스윙 도중 아이템이 손을 떠났을 때뿐이다.
/// </summary>
public class Baton : ItemBase, IAimedWeapon
{
    [Header("진압봉 설정")]
    [Tooltip("타격이 닿는 최대 사거리(m). 상호작용 레이(PlayerInteractor.Range)와 무관하게 이 값이 기준이다")]
    [SerializeField]
    private float m_range = 2f;

    [Tooltip("타격 판정 구체의 반경(m). 근접 조준을 관대하게 만드는 값 — 키울수록 빗맞아도 맞는다")]
    [SerializeField]
    private float m_hitRadius = 0.35f;

    // 기본값 34는 E 제압 홀드 1회(NpcCommonConfig.SubdueHitPower)와 같은 값 — MaxHp 100 기준 3대에 기절한다.
    // 같은 수치를 공유하지만 참조하지는 않는다: 저건 'NPC가 제압당할 때 깎이는 양'이고 이건 '이 무기의 위력'이라,
    // 무기 밸런스를 만지려고 NPC 공용 config를 건드리면 제압 홀드까지 함께 움직인다.
    [Tooltip("1회 타격이 깎는 NPC 체력. 서버가 자기 프리팹 값을 쓴다 — 클라이언트가 수치를 보내지 않는다")]
    [SerializeField]
    private int m_damage = 34;

    // 스윙 연출 길이(PlayerAnimationDriver.k_swingSeconds, 0.64초)보다 길게 유지할 것. 짧게 잡으면
    // 애니메이션이 끝나기 전에 다음 스윙이 들어와 계속 처음부터 잘린다 — 데미지는 들어가는데
    // 화면에서는 휘두르다 마는 그림이 된다. 남는 0.26초는 다음 타격 전의 뜸이다(연출 아님).
    [Tooltip("타격 후 다음 타격까지 대기 시간(초). 명중·빗나감 모두 소모한다 — 빗나가도 대가가 있어야 조준이 의미를 갖는다")]
    [SerializeField]
    private float m_cooldownSeconds = 0.9f;

    // 근거는 Taser.m_originTolerance와 동일: 카메라 높이(1.6m) + 스프린트 8m/s × 지연 150ms(1.2m) ≈ 2.8 → 3.0.
    // 줄이면 핑 높은 플레이어의 정상 타격이 조용히 거부된다.
    [Tooltip("클라가 보낸 조준 원점이 서버가 아는 플레이어 위치에서 이만큼(m) 넘게 떨어져 있으면 거부한다")]
    [SerializeField]
    private float m_originTolerance = 3f;

    // 캐스트 결과 버퍼 — 서버 판정과 오너 크로스헤어(HasValidAimTarget)가 함께 쓰지만 공유해도 안전하다.
    // 둘 다 메인 스레드에서 동기적으로 돌고, 결과를 호출 안에서 즉시 꺼내 쓴 뒤 버퍼를 붙들지 않는다.
    // (호스트에서는 두 경로가 같은 프레임에 돌 수 있지만 겹쳐 실행되지는 않는다)
    // 2m 반경 0.35m 구체가 훑는 범위에 16개를 넘는 콜라이더가 들어올 일은 없다(넘치면 초과분이 잘릴 뿐, 최근접은 대개 남는다).
    private static readonly RaycastHit[] s_hitBuffer = new RaycastHit[16];

    // 다음 타격이 가능해지는 시각. 판정자가 서버 하나뿐이라 동기화하지 않는다 (서버 전용 상태).
    // 아이템 인스턴스에 붙어 있으므로 버리고 다시 주워도 쿨다운이 따라간다.
    private float m_nextSwingTime;

    // 스윙 시작 → 임팩트 대기. 홀드 채널링은 아니지만 필요한 건 같다(서버 전용 타이머, CTS 소유,
    // 재진입 가드, 디스폰 시 정리) — 그 골격이 이미 ServerChannel에 있어 그대로 재사용한다 (#109).
    private readonly ServerChannel m_swingImpact = new();

    // ---- ItemBase ----

    // CanTarget은 재정의하지 않는다(기본 false) — 조준 대상 윤곽선(#184)의 기준인 상호작용 레이(3m)와
    // 실제 사거리(2m)가 어긋나 그대로 쓰면 2~3m에서 "윤곽선은 떴는데 안 맞는" 상태가 된다.
    // 대신 IAimedWeapon으로 크로스헤어 색만 구동한다 (테이저와 같은 방식, 아래 HasValidAimTarget).

    /// <summary>
    /// 조준선이 지금 휘두르면 맞을 NPC에 닿는지 — 오너 크로스헤어 색 예측용. (#184/#217)
    /// 서버 타격 판정과 <b>같은 함수</b>(<see cref="EvaluateSwing"/>)를 쓰므로 규칙이 어긋날 수 없다.
    /// </summary>
    /// <remarks>
    /// 거리 비교로 흉내 내지 않는 이유: 실제 판정은 두께 있는 구체 캐스트라 사거리 안이어도
    /// 벽 엄폐에 걸리거나 빗나간다. 쿨다운은 보지 않는다 — 서버 전용 상태(m_nextSwingTime)라
    /// 원격 오너에게는 늘 0이어서, 넣으면 호스트와 클라이언트의 크로스헤어가 서로 달라진다.
    /// '지금 칠 수 있나'가 아니라 '겨냥이 맞았나'만 답한다 (Taser.HasValidAimTarget과 같은 방침).
    /// </remarks>
    public bool HasValidAimTarget(Vector3 origin, Vector3 direction)
    {
        // 소지자 계층은 자기 몸을 캐스트에서 걸러내는 데 필요하다 — 원점이 카메라(캡슐 안)라
        // 걸러내지 않으면 자기 콜라이더가 distance 0으로 먼저 잡힌다. (EvaluateSwing 주석 참고)
        PlayerInteractor holder = GetComponentInParent<PlayerInteractor>();
        if (holder == null)
        {
            return false;
        }

        return EvaluateSwing(origin, direction, holder.transform, out _, out _)
            == SwingResult.ValidTarget;
    }

    /// <summary>
    /// 아이템 사용 진입점. 겨냥 대상(aimTarget)은 쓰지 않는다 — 조준 방향으로 직접 휘두르기 때문이다.
    /// 오너는 조준 원점·방향만 넘기고, 명중 판정은 전적으로 서버가 수행한다.
    /// </summary>
    public override void Use(GameObject aimTarget)
    {
        // 조준 기준은 든 플레이어의 AimOrigin(카메라) — 아이템은 줍기/버리기로 부모가 바뀌므로
        // 캐시하지 않고 사용 시점에 해석한다 (Taser.Use 관례).
        PlayerInteractor interactor = GetComponentInParent<PlayerInteractor>();
        if (interactor == null)
        {
            Debug.LogWarning("Baton: PlayerInteractor를 찾지 못함 — 조준 기준 없음", this);
            return;
        }

        Transform aim = interactor.AimOrigin;
        Vector3 origin = aim.position;
        Vector3 direction = aim.forward;

        // 서버(호스트)·오프라인은 로컬 참조로 즉시 실행
        if (!IsSpawned || IsServer)
        {
            ServerSwing(origin, direction);
            return;
        }

        if (!IsOwner)
        {
            return;
        }

        RequestSwingRpc(origin, direction);
    }

    [Rpc(SendTo.Server)]
    private void RequestSwingRpc(Vector3 origin, Vector3 direction)
    {
        ServerSwing(origin, direction);
    }

    // ---- 서버 판정 ----

    /// <summary>
    /// 서버에서 근접 타격을 판정한다. 클라가 보낸 원점·방향은 신뢰할 수 없으므로,
    /// 원점이 서버가 아는 플레이어 위치 근처인지 확인한 뒤 서버 물리로 캐스트한다.
    /// (원점 검증이 없으면 위조 RPC로 맵 어디서든, 벽 너머로도 때릴 수 있다)
    /// </summary>
    private void ServerSwing(Vector3 origin, Vector3 direction)
    {
        // 스폰 전(오프라인)엔 IsServer 캐시가 아직 갱신되지 않아 false일 수 있으므로,
        // "스폰된 상태에서 서버가 아닐 때"만 차단한다. (Taser.ServerFire 관례)
        if (IsSpawned && !IsServer)
        {
            return;
        }

        if (direction.sqrMagnitude < 0.0001f)
        {
            return; // 방향이 0벡터면 캐스트를 만들 수 없다 (위조·직렬화 사고 방어)
        }

        PlayerInteractor holder = GetComponentInParent<PlayerInteractor>();
        if (holder == null)
        {
            return; // 아무에게도 안 들린 아이템이 휘둘러질 수는 없다
        }

        if ((origin - holder.transform.position).sqrMagnitude > m_originTolerance * m_originTolerance)
        {
            Debug.LogWarning($"Baton: 조준 원점이 플레이어 위치와 너무 멀다 — 타격 거부 (origin={origin})", this);
            return;
        }

        // 쿨다운은 유효성 검사를 전부 통과한 뒤에 본다 — 위조·사고로 거부된 요청이 쿨다운을 소모하면
        // 정상 타격이 엉뚱하게 막힌다. 여기부터는 "실제로 휘둘렀다"로 취급한다.
        // 임팩트 대기 중인 스윙이 있으면 함께 막는다 — 쿨다운(0.9초)이 임팩트 지연(0.3초)보다 길어
        // 정상 경로에선 걸릴 일이 없지만, 쿨다운을 줄이면 대기가 겹쳐 앞 타격이 유실된다.
        if (Time.time < m_nextSwingTime || m_swingImpact.IsActive)
        {
            return; // 연타 입력이라 로그를 남기지 않는다 — 남기면 좌클릭 연타마다 콘솔이 도배된다
        }

        // 명중 여부와 무관하게 소모한다 — 빗나감에 대가가 없으면 조준할 이유가 사라진다.
        m_nextSwingTime = Time.time + m_cooldownSeconds;

        // 애니메이션 먼저, 판정은 임팩트 프레임에. 빗나가도 휘두르는 모습은 보여야 한다. (#217)
        PlaySwing();

        // 조준을 소지자 로컬로 환산해 들고 간다 — 대기하는 0.3초 동안 플레이어가 걷거나 돌면
        // 월드 좌표로 굳혀 둔 조준선은 몸에서 떨어져 나가, 화면에서는 정면을 후려치는데 판정은
        // 0.3초 전 허공에서 나가는 일이 생긴다. 몸에 붙여 두면 스윙이 캐릭터를 따라간다
        // (= 애니메이션이 보여주는 것과 같다). 방향도 몸 기준이라 스윙 도중 마우스로 다시
        // 겨누는 것은 여전히 안 된다 — 휘두르기 시작하면 되돌릴 수 없다.
        Transform holderTransform = holder.transform;
        ServerResolveHitAtImpactAsync(
            holderTransform.InverseTransformPoint(origin),
            holderTransform.InverseTransformDirection(direction),
            holder
        ).Forget();
    }

    /// <summary>
    /// 임팩트 프레임까지 기다렸다가 캐스트해 데미지를 넣는다 — 서버 전용. (#217)
    /// 대기 동안 상황이 바뀔 수 있으므로(디스폰·버리기·소지자 소멸) 판정 직전에 다시 확인한다.
    /// </summary>
    private async UniTaskVoid ServerResolveHitAtImpactAsync(
        Vector3 localOrigin,
        Vector3 localDirection,
        PlayerInteractor holder
    )
    {
        ServerChannel.Result result = await m_swingImpact.RunAsync(
            PlayerAnimationDriver.k_swingImpactSeconds
        );

        // 취소 = 스윙 도중 아이템이 손을 떠났거나 디스폰됐다. 없던 일이므로 조용히 끝낸다.
        if (result != ServerChannel.Result.Completed)
        {
            return;
        }

        // 대기 동안 파괴·디스폰됐을 수 있다 (Unity의 == null 오버로드가 파괴된 객체를 잡아준다).
        if (this == null || (IsSpawned && !IsServer))
        {
            return;
        }

        // 소지자가 사라졌거나, 스윙 도중 아이템을 버렸거나 남에게 넘어갔으면 이 타격은 무효다 —
        // 손을 떠난 봉이 때리는 그림은 없다.
        if (holder == null || GetComponentInParent<PlayerInteractor>() != holder)
        {
            return;
        }

        Transform holderTransform = holder.transform;
        Vector3 origin = holderTransform.TransformPoint(localOrigin);
        Vector3 direction = holderTransform.TransformDirection(localDirection);

        switch (EvaluateSwing(origin, direction, holderTransform, out NpcController target, out RaycastHit hit))
        {
            case SwingResult.NoHit:
                NotifyOwner("진압봉 빗나감 — 허공");
                return;
            case SwingResult.HitNonTarget:
                NotifyOwner($"진압봉 빗나감 — {hit.collider.name}에 맞음");
                return;
            case SwingResult.TargetInvalidState:
                NotifyOwner($"진압봉 무효 — 이미 제압됐거나 페널티 진행 중인 대상 ({target.CurrentState})");
                return;
        }

        // 때린 사람을 가해자로 넘긴다 — 맞은 즉시 이 사람에게 반격·도주하고(#400),
        // 이 타격으로 기절하면 깨어난 뒤에도 이 사람에게서 도망친다 (#269).
        target.TakeDamage(m_damage, holder.gameObject);
        target.ServerReactTo(ReactionTrigger.Damage, holderTransform); // 맞은 즉시 반응 (#400)
        NotifyOwner($"진압봉 명중: {target.name} (-{m_damage} → {target.CurrentHp}/{target.MaxHp})");
    }

    // ---- 정리 ----

    /// <summary>
    /// 서버가 소유권 회수를 동반해 사용을 끊는 경로(버리기) — 대기 중인 임팩트를 취소한다.
    /// 손을 떠난 봉이 뒤늦게 때리는 것을 막는다.
    /// (좌클릭 뗌은 CancelUse라 여기로 오지 않는다 — 시작된 스윙은 끝까지 간다. 의도된 것)
    /// </summary>
    public override void ServerCancelActiveUse() => m_swingImpact.Cancel();

    public override void OnNetworkDespawn()
    {
        m_swingImpact.Cancel(); // 디스폰된 뒤에 판정이 남아 돌지 않게
        base.OnNetworkDespawn();
    }

    public override void OnDestroy()
    {
        m_swingImpact.Dispose();
        base.OnDestroy();
    }

    // ---- 스윙 애니메이션 (전 피어) ----

    /// <summary>
    /// 스윙 모션을 전 피어에서 재생시킨다 — 서버 판정 지점에서 한 번만 호출된다. (#217)
    /// Down/Crouch/Airborne처럼 동기화값을 폴링할 수 없는 일회성 이벤트라 브로드캐스트로 처리한다.
    /// (드라이버 주석의 "트리거 대신 bool" 원칙은 지속 상태 얘기다 — 서버가 명시적으로 한 번 쏘는
    ///  RPC는 유실·중복이 없으므로 트리거가 맞다)
    /// </summary>
    private void PlaySwing()
    {
        if (!IsSpawned)
        {
            ApplySwingAnimation(GetComponentInParent<PlayerInteractor>()); // 오프라인 — RPC 경로가 없다
            return;
        }

        PlaySwingRpc();
    }

    [Rpc(SendTo.Everyone)]
    private void PlaySwingRpc()
    {
        // 소지자를 인자로 싣지 않는 이유: 아이템의 부착 부모는 NetworkObject 부모 동기화로 전 피어가
        // 동일하므로, 각 피어가 자기 계층에서 찾는 편이 참조 직렬화보다 싸고 어긋날 여지가 없다.
        ApplySwingAnimation(GetComponentInParent<PlayerInteractor>());
    }

    // 드라이버는 Animator가 붙은 모델 쪽에 있을 수도, 루트에 있을 수도 있다 — 소지자 루트에서 아래로 찾는다
    // (GetComponentInChildren은 자기 자신도 포함하므로 두 배치 모두 걸린다).
    private static void ApplySwingAnimation(PlayerInteractor holder)
    {
        if (holder == null)
        {
            return;
        }

        PlayerAnimationDriver driver = holder.GetComponentInChildren<PlayerAnimationDriver>();
        if (driver != null)
        {
            driver.TriggerAttack();
        }
    }

    // ---- 조준 판정 ----

    private enum SwingResult { NoHit, HitNonTarget, TargetInvalidState, ValidTarget }

    /// <summary>
    /// 조준 원점·방향으로 사거리(m_range)만큼 반경 m_hitRadius 구체를 날려 명중 결과를 분류한다.
    /// 마스크 ~0 + 트리거 무시. 후보 중 하나를 고르는 기준은 <see cref="AimOcclusion"/>가 단독으로
    /// 가지며, 그 기준이 벽 엄폐의 정의다 — 테이저·상호작용 가시선과 같은 규칙이다.
    ///
    /// SphereCast는 레이캐스트와 달리 <b>시작 지점에 이미 겹친 콜라이더를 distance 0으로 되돌려준다.</b>
    /// 원점이 카메라(= 소지자 캡슐 안)라서 자기 몸이 항상 걸리므로, 소지자 계층은 걸러내고 최근접을 고른다.
    /// 원점을 앞으로 밀어 피하는 방법도 있지만, 벽에 붙어 있을 때 시작점이 벽 너머로 넘어가 관통 타격이 된다.
    ///
    /// <b>부수효과 없는 순수 판정으로 유지할 것.</b> 서버 타격 판정(<see cref="ServerResolveHitAtImpactAsync"/>)과
    /// 오너 크로스헤어(<see cref="HasValidAimTarget"/>) 둘이 공유한다 — 후자는 매 프레임 도는 로컬
    /// 피드백이라, 여기에 상태 변경이나 로그를 넣으면 조준만 해도 그게 매 프레임 실행된다.
    /// 둘이 같은 함수를 보는 것이 "크로스헤어는 켜졌는데 안 맞음"을 막는 장치이므로 분기시키지 말 것.
    /// </summary>
    private SwingResult EvaluateSwing(
        Vector3 origin,
        Vector3 direction,
        Transform holderRoot,
        out NpcController target,
        out RaycastHit hit
    )
    {
        target = null;
        hit = default;

        int count = Physics.SphereCastNonAlloc(
            origin,
            m_hitRadius,
            direction.normalized,
            s_hitBuffer,
            m_range,
            ~0,
            QueryTriggerInteraction.Ignore
        );

        // 휘두른 본인(과 그가 들고 있는 것들)은 제외 계층으로 넘긴다 — 위 주석의 자기 겹침 문제.
        int index = AimOcclusion.FindNearestByPivot(origin, s_hitBuffer, count, holderRoot);
        if (index < 0)
        {
            return SwingResult.NoHit;
        }

        hit = s_hitBuffer[index];

        // 콜라이더가 NPC 루트의 자식일 수 있으므로 부모까지 탐색한다 (Taser.EvaluateAim과 동일 관례).
        // 벽·소품·다른 플레이어를 맞췄으면 그대로 빗나감이다.
        NpcController npc = hit.collider.GetComponentInParent<NpcController>();
        if (npc == null)
        {
            return SwingResult.HitNonTarget;
        }

        // 피해 게이트를 데미지 전에 본다 — TakeDamage도 같은 규칙으로 피해를 무시하지만(#366/#292),
        // 여기서 먼저 걸러야 오너에게 "무효" 사유를 알려줄 수 있다. 두 곳의 기준은 반드시 같아야 한다.
        //
        // 스턴 게이트가 아니라 <b>타격 게이트</b>다 — #292에서 스턴이 오버레이가 되며 전 상태에 걸리게
        // 되면서 둘이 갈라졌다(구 CanBeStunned → CanBeDamaged). 타격까지 함께 열면 연행 중인 NPC를
        // 때려 기절시켜 신병에서 빼내는 우회가 생기므로, 진압봉은 좁은 쪽(타격)을 따른다.
        target = npc;
        if (!NpcStateRules.CanBeDamaged(npc.CurrentState))
        {
            return SwingResult.TargetInvalidState;
        }

        return SwingResult.ValidTarget;
    }

    // ---- 오너 로그 피드백 ----

    // 판정 로그는 서버에서 찍히므로 원격 클라 오너는 결과를 볼 수 없다 — 오너 콘솔에도 같은 로그를 전달한다.
    // Taser.NotifyOwner / Scanner.NotifyOwner와 동일 패턴 (#91).
    private void NotifyOwner(string message)
    {
        Debug.Log(message); // 서버(호스트)·오프라인 콘솔
        if (IsSpawned && IsServer && !IsOwner)
            OwnerLogRpc(message); // 원격 클라가 오너인 경우에만 전달 (호스트 오너는 위에서 이미 찍음)
    }

    [Rpc(SendTo.Owner)]
    private void OwnerLogRpc(string message) => Debug.Log($"[서버 판정] {message}");
}
