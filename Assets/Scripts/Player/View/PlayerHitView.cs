using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 플레이어 피격 표현 — <see cref="PlayerHealth.OnDamaged"/>를 구독해 화면·뷰모델 연출을 돌린다. (#476)
///
/// 지금까지 플레이어가 맞았을 때의 단서는 좌하단 HP 텍스트 숫자뿐이라, 교전 중에는 사실상 맞은 줄
/// 몰랐다. 특히 진압봉 아군 오사(#461)는 맞은 쪽이 "누가 나를 때리고 있다"를 알아야 무전으로
/// 항의하든 피하든 할 수 있는데 그 정보가 화면에 전혀 없었다.
///
/// <b>구독은 전 피어, 연출은 오너.</b> 이벤트 자체는 모든 피어에서 발행되지만(그쪽 판단은
/// PlayerHealth가 한다) 이 이슈에서 만드는 연출은 전부 내 화면 몫이라 오너에서만 돈다.
/// 남이 보는 내 몸의 피격 스파크는 후속 이슈에서 <see cref="HandleDamaged"/>의 오너 가드
/// <b>위</b>에 붙는다 — 그때 RPC 대상을 넓히는 재작업이 없도록 발행을 처음부터 전 피어로 뒀다.
///
/// 오프라인(비네트워크) Play 테스트에서도 동작한다 — 구독을 Awake에서 걸고, 스폰 전에는
/// <see cref="IsLocalOwner"/>가 자기를 오너로 취급한다 (PlayerHealth의 오프라인 폴백과 같은 방침).
/// </summary>
[RequireComponent(typeof(PlayerHealth))]
public class PlayerHitView : NetworkBehaviour
{
    private PlayerHealth m_health;
    private PlayerIncapacitation m_incapacitation; // 없는 구성(테스트 등)이면 null
    private PlayerInteractor m_interactor; // 조준 원점(카메라) — 피격 방향 환산 기준
    private PlayerHandView m_handView; // 1인칭 팔 — 오너에게만 존재하는 표현
    private PlayerLook m_look; // 카메라 흔들림 조립 지점 (#477)
    private ShockArcEmitter m_shockArcs; // 몸에서 튀는 전기 아크 — 전 피어 (#477)

    // 스폰 전(오프라인)에는 IsOwner가 늘 false다 — 그때는 자기 화면이 곧 내 화면이므로 오너로 본다.
    // PlayerHealth가 "IsSpawned && !IsServer"로 오프라인을 권위자 취급하는 것과 같은 형태.
    private bool IsLocalOwner => !IsSpawned || IsOwner;

    private void Awake()
    {
        m_health = GetComponent<PlayerHealth>();
        m_incapacitation = GetComponent<PlayerIncapacitation>();
        m_interactor = GetComponent<PlayerInteractor>();
        m_handView = GetComponent<PlayerHandView>();
        m_look = GetComponent<PlayerLook>();
        m_shockArcs = GetComponent<ShockArcEmitter>();

        // OnNetworkSpawn이 아니라 Awake에서 건다 — 스폰하지 않는 오프라인 테스트에서도 이벤트를 받아야 한다.
        m_health.OnDamaged += HandleDamaged;
    }

    public override void OnDestroy()
    {
        if (m_health != null)
            m_health.OnDamaged -= HandleDamaged;

        base.OnDestroy();
    }

    public override void OnNetworkDespawn()
    {
        // 씬 전환·리스폰으로 뷰가 사라질 때 화면에 연출이 눌어붙지 않게 (HUD는 이 오브젝트보다 오래 산다)
        if (IsLocalOwner)
        {
            App.UI.DamageVignette?.ClearAll();
            ApplyShock(0f); // 감전 중 디스폰되면 화면 지직·카메라 떨림이 그대로 남는다 (#477)
        }

        base.OnNetworkDespawn();
    }

    private void HandleDamaged(DamageHit hit)
    {
        // ── 후속 이슈: 전 피어에서 도는 몸 스파크가 이 가드 '위'에 붙는다 ──
        if (!IsLocalOwner)
            return;

        // 다운·기절 중에는 새 피격 연출을 띄우지 않는다 — 쓰러진 상태의 표현(Knockdown 모션·구조 UI)과
        // 화면에서 싸운다. HP 0 도달로 다운되는 그 타격 자체는 아직 무력화 전이라 정상적으로 나간다.
        if (m_incapacitation != null && m_incapacitation.IsIncapacitated)
            return;

        DamageVignetteUI vignette = App.UI.DamageVignette;
        if (vignette != null)
        {
            if (TryResolveDirection(hit, out float angle))
                vignette.PlayHit(hit.Amount, angle);
            else
                vignette.PlayHit(hit.Amount); // 가해자를 모르는 피해 — 방향 아크 없이 비네트만
        }

        m_handView?.PlayHitShake();
    }

    /// <summary>
    /// 가해자 월드 좌표를 <b>화면 정면 기준 각도</b>로 환산한다 — 0도가 정면, 오른쪽이 양수. (#476)
    /// 환산을 HUD가 아니라 여기서 하는 이유: 조준 원점(카메라)은 플레이어 오브젝트의 구조이고,
    /// HUD는 그걸 몰라야 한다.
    /// </summary>
    /// <remarks>
    /// 몸통(transform.forward)이 아니라 조준 원점의 전방을 쓴다 — 아크는 <b>화면</b> 좌표계의
    /// 표시라 시선과 어긋나면 안 된다. 수평 성분만 쓰므로 위아래를 봐도 방향이 흔들리지 않는다.
    /// </remarks>
    private bool TryResolveDirection(DamageHit hit, out float angleDegrees)
    {
        angleDegrees = 0f;
        if (!hit.HasAttacker)
            return false;

        Transform aim = m_interactor != null ? m_interactor.AimOrigin : transform;
        if (aim == null)
            return false;

        Vector3 forward = aim.forward;
        forward.y = 0f;

        Vector3 toAttacker = hit.AttackerPosition - transform.position;
        toAttacker.y = 0f;

        // 바로 위·아래에서 온 피해(폭발이 발밑에 있는 등)는 수평 방향이 정의되지 않는다 — 비네트만 띄운다
        if (forward.sqrMagnitude < 0.0001f || toAttacker.sqrMagnitude < 0.0001f)
            return false;

        angleDegrees = Vector3.SignedAngle(forward, toAttacker, Vector3.up);
        return true;
    }

    private void Update()
    {
        // 감전은 오너 화면(지직·카메라·팔)과 월드(몸 아크) 양쪽이라 오너 가드 <b>앞</b>에서 돈다 (#477).
        // 무력화 원인은 서버 권위로 동기화되므로(PlayerIncapacitation.Cause) 모든 피어가 같은 판단을 한다 —
        // 동료와 본부 CCTV가 "쟤는 테이저 맞은 거니까 구조 안 가도 된다"를 볼 수 있어야 한다.
        TickShock();

        if (!IsLocalOwner)
            return;

        // 저체력 글리치는 순간 이벤트가 아니라 지속 상태라 폴링한다 — HP가 이미 동기화 값이라
        // 이벤트에 실을 이유가 없다 (PlayerAnimationDriver가 Down/Crouch/Airborne을 폴링하는 것과 같은 방침).
        // 임계값 판단은 연출 튜닝이라 HUD가 갖는다.
        int maxHp = Mathf.Max(1, m_health.MaxHp);
        App.UI.DamageVignette?.UpdateHealthState(
            (float)m_health.CurrentHp / maxHp,
            m_incapacitation != null && m_incapacitation.IsIncapacitated
        );
    }

    // ---- 감전 (#477) ----

    /// <summary>
    /// 테이저 기절 연출을 매 프레임 갱신한다 — 원인이 <see cref="IncapacitationCause.Stun"/>일 때만 켜진다.
    /// </summary>
    /// <remarks>
    /// <b>이 연출이 다운과 기절을 화면에서 구분해 준다.</b> 모션은 원인을 가리지 않고 같은
    /// Knockdown이라(#252 — 카메라가 바닥 높이로 내려가는데 몸만 서 있으면 어긋나서 그렇게 뒀다),
    /// 지금까지 맞은 사람은 "5초 뒤 스스로 일어나는지, 동료 구조를 기다려야 하는지"를 알 수 없었다.
    ///
    /// 이벤트가 아니라 폴링인 이유: <c>OnIncapacitatedChanged</c>는 bool 변화에만 울리고 "원인만
    /// 바뀌면 울리지 않는다"고 명시돼 있어, 다운→기절 같은 원인 전환을 놓친다.
    /// </remarks>
    private void TickShock()
    {
        if (m_incapacitation == null || !m_incapacitation.IsStunned)
        {
            ApplyShock(0f);
            return;
        }

        // 남은 시간이 꼬리 구간에 들어서면 강도가 선형으로 줄어든다. 그 밖에서는 1로 포화된다.
        // 남은 시간은 서버 시각 기준이라(PlayerIncapacitation.RemainingStunSeconds) 모든 피어가 같이 잦아든다.
        // 꼬리 길이는 NPC 감전(NpcShockView)과 공유한다 — 각자 상수를 들면 같은 테이저에 맞았는데
        // 잦아드는 시점이 달라진다.
        float remaining = m_incapacitation.RemainingStunSeconds;
        ApplyShock(Mathf.Clamp01(remaining / ShockArcEmitter.k_calmTailSeconds));
    }

    // 강도 하나로 네 표현을 함께 몬다 — 화면·카메라·팔은 오너만, 몸 아크는 전 피어.
    // 0을 넣는 것이 곧 정리라, 해제 경로를 따로 두지 않는다(디스폰도 이 함수로 끈다).
    private void ApplyShock(float intensity)
    {
        // 아크는 강도로 세기를 조절하지 않는다 — 파티클 버스트는 켜짐/꺼짐이 분명해야 읽힌다.
        // 잦아드는 표현은 간격을 벌리는 쪽으로 낸다(NpcShockView와 같은 방침).
        // 강도가 1 미만이라는 건 이미 꼬리 구간에 들어섰다는 뜻이다.
        if (m_shockArcs != null)
        {
            m_shockArcs.SetEmitting(intensity > 0.001f);
            m_shockArcs.SetCalm(intensity < 0.999f);
        }

        if (!IsLocalOwner)
            return;

        App.UI.TaserShock?.SetShock(intensity);
        m_look?.SetShakeIntensity(intensity);
        m_handView?.SetConvulsion(intensity);
    }
}
