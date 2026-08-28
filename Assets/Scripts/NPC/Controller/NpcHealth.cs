using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// NPC 체력 도메인 부품 (#366/#503/#916) — 0은 사망이 아니라 <b>쓰러짐</b>이다.
///
/// 0이 되면 쓰러지고(<see cref="NpcStun"/>), 기절이 끝나면 1로 일어나며, 쓰러진 몸이 또 맞으면
/// 죽는다(<see cref="NpcDeath"/>). 한 방의 초과 피해가 임계를 넘으면 곧장 죽는다.
///
/// 서버 권위 + 오프라인 폴백 — 서버(또는 오프라인)만 값을 바꾸고 클라는 동기화 값을 읽는다 (#56 패턴).
/// 폭발 피해(<see cref="BombDevice"/>)는 <see cref="IDamageable"/>을 GetComponent로 찾으므로 구현이
/// 코어에서 이 부품으로 옮겨와도 같은 GameObject에 있는 한 경로가 유지된다.
/// </summary>
public class NpcHealth : NetworkBehaviour, IDamageable
{
    private NpcController m_owner;

    // 서버 권위 HP — m_hp가 서버·오프라인의 진실값.
    private readonly NetworkVariable<int> m_syncedHp = new NetworkVariable<int>();
    private int m_hp;

    // 방치 회복 타이머 — 마지막 피해 이후 경과 시간. ApplyDamage가 리셋한다 (#707)
    private float m_secondsSinceDamage;

    // 초당 회복량의 소수 부분 누적 — 프레임마다 정수로 잘리는 손실을 막는다 (#707)
    private float m_regenAccumulator;

    /// <summary>최대 체력 — HUD가 비율 계산에 읽는다. (#366)</summary>
    public int MaxHp => m_owner.CommonConfig.MaxHp;

    /// <summary>현재 체력. 세션 중에는 동기화 값이라 클라에서도 안전하게 읽을 수 있다. (#366)</summary>
    public int CurrentHp => IsSpawned ? m_syncedHp.Value : m_hp;

    /// <summary>
    /// 피해 적용 순간 발행 — 서버 전용. 인자는 (맞은 NPC, 가해자). 납치(<see cref="AbductionEvent"/>)가
    /// 구독해 맞은 납치범을 호송에서 떼어낸다. (#371)
    ///
    /// <b>HP 반영 직전 발행이 계약이다</b> — 구독자가 상태를 바꾼 뒤에 기절 전이가 얹혀야 한다.
    /// 뒤로 옮기면 임무 해제가 넉백 기절을 덮어써 기절이 조용히 취소된다.
    /// </summary>
    public event Action<NpcController, GameObject> OnDamaged;

    /// <summary>
    /// 피격 순간 <b>전 피어</b>에서 발행되는 연출용 훅 — HP 폴링으로는 "지금 맞았다"를 잡을 수 없다. (#478)
    /// 몸에 붙는 연출은 본부 CCTV에서도 보여야 해서 오너가 아니라 전 피어다.
    ///
    /// <b>지금은 구독자가 없다</b>(타격 플래시를 걷어냈다). 다시 붙일 때 필요한 게 이 순간 알림
    /// 하나뿐이라 RPC와 함께 남겨 둔다 — 아깝다고 판단되면 둘을 같이 지울 것.
    /// <see cref="PlayerHealth.OnDamaged"/>와 이름이 어긋나는 이유는 이쪽엔 위 서버 훅이 그 이름을
    /// 이미 쓰고 있기 때문이다.
    /// </summary>
    public event Action<DamageHit> OnHit;

    private void Awake()
    {
        m_owner = GetComponent<NpcController>();
    }

    /// <summary>체력 초기화 — 코어의 InitBehavior에서 서버(또는 오프라인) 1회 호출된다.</summary>
    internal void InitHealth()
    {
        SetHp(MaxHp, null);
    }

    /// <summary>
    /// 피해 적용 (<see cref="IDamageable"/>) — 플레이어 타격 전용. (#366)
    /// <see cref="NpcStateRules.CanBeDamaged"/>가 신병 빼내기 우회를 막는다. 환경 피해는
    /// <see cref="TakeEnvironmentalDamage"/>로 간다 (#690).
    /// </summary>
    /// <param name="amount">깎을 체력. 0 이하는 무시한다.</param>
    /// <param name="attacker">가해자 — 기절 시 위협 대상으로 넘긴다. null 허용.</param>
    public void TakeDamage(int amount, GameObject attacker)
    {
        if (IsSpawned && !IsServer)
            return;
        if (amount <= 0)
            return;
        if (!NpcStateRules.CanBeDamaged(m_owner))
            return;

        ApplyDamage(amount, attacker);
    }

    /// <summary>
    /// 환경 피해 적용(차량·폭발 등) — 연행 중인 신병도 그대로 맞는다 (#690). 차·폭발은 신병
    /// 상태를 가리지 않는데 <see cref="TakeDamage"/>의 우회 방지 게이트에 함께 막히고 있었다.
    /// </summary>
    /// <param name="amount">깎을 체력. 0 이하는 무시한다.</param>
    /// <param name="attacker">가해자 — 기절 시 위협 대상으로 넘긴다. null 허용.</param>
    public void TakeEnvironmentalDamage(int amount, GameObject attacker)
    {
        if (IsSpawned && !IsServer)
            return;
        if (amount <= 0)
            return;
        if (!NpcStateRules.CanTakeEnvironmentalDamage(m_owner))
            return;

        ApplyDamage(amount, attacker);
    }

    // 두 진입점(TakeDamage · TakeEnvironmentalDamage)이 게이트만 다르고 이후는 같다 — 여기서 합친다.
    private void ApplyDamage(int amount, GameObject attacker)
    {
        // 피해를 얹기 전에 알린다 — 위 OnDamaged 주석의 순서 근거 참고
        OnDamaged?.Invoke(m_owner, attacker);

        // 실제로 깎인 양을 연출에 실어야 한다 — 아래 Clamp에 걸려 요청량보다 적을 수 있다 (#478)
        int before = CurrentHp;
        SetHp(Mathf.Clamp(before - amount, 0, MaxHp), attacker, IsLethal(before, amount));

        // 피격 반응(#400)은 여기서 굴리지 않는다 — 환경 피해도 이 경로를 지나므로
        // 플레이어 타격 경로(Baton.ServerSwing)가 직접 부른다. 연출은 반대로 여기가 맞다 (#478).
        int applied = before - CurrentHp;
        if (applied > 0)
            BroadcastDamaged(applied, attacker);

        // 방치 타이머 리셋 — 회복 중 다시 맞으면 처음부터 다시 잰다 (#707)
        m_secondsSinceDamage = 0f;
        m_regenAccumulator = 0f;
    }

    /// <summary>쓰러뜨리는 대신 <b>죽이는</b> 타격인가 — 확인사살(이미 0)이거나 오버킬. 피해를 얹기 전 값으로 판정한다. (#916)</summary>
    private bool IsLethal(int before, int amount) =>
        before == 0 || amount - before >= m_owner.CommonConfig.LethalOverkillHp;

    // 연출 알림을 전 피어에 돌린다 — 가해자를 GameObject로 실을 수 없어 월드 좌표로 환산해 보낸다.
    private void BroadcastDamaged(int amount, GameObject attacker)
    {
        bool hasAttacker = attacker != null;
        Vector3 attackerPosition = hasAttacker ? attacker.transform.position : Vector3.zero;

        if (!IsSpawned)
        {
            RaiseDamaged(amount, attackerPosition, hasAttacker); // 오프라인 폴백
            return;
        }

        PlayDamagedRpc(amount, attackerPosition, hasAttacker);
    }

    [Rpc(SendTo.Everyone)]
    private void PlayDamagedRpc(int amount, Vector3 attackerPosition, bool hasAttacker) =>
        RaiseDamaged(amount, attackerPosition, hasAttacker);

    private void RaiseDamaged(int amount, Vector3 attackerPosition, bool hasAttacker) =>
        OnHit?.Invoke(new DamageHit(amount, attackerPosition, hasAttacker));

    /// <summary>
    /// 쓰러졌던 몸을 체력 1로 일으킨다 — 기절이 끝나는 자리 전용. 서버(또는 오프라인). (#916)
    /// 만이 아니라 1인 것은 확인사살 창을 남기기 위해서다. 0이 아니면 무동작(테이저 기절).
    /// </summary>
    internal void ServerRestoreToOne()
    {
        if (IsSpawned && !IsServer)
            return;
        if (m_owner.Death.IsDead || CurrentHp > 0)
            return;

        SetHp(1, null);
    }

    /// <summary>
    /// NavMesh 밖에서 굳은 몸을 죽인다 — 서버(또는 오프라인). (#913)
    /// 부르는 곳은 <see cref="NpcController"/>의 굳은 몸 정리 하나다.
    ///
    /// <see cref="NpcDeath.ServerEnterDead"/>를 직접 부르지 않는 이유는 HP다 — 그쪽만 부르면 체력이
    /// 남은 시체가 되어 HUD·회복 틱이 산 몸처럼 읽는다. 사망은 0을 지나서 들어와야 한다.
    /// <see cref="NpcStateRules.CanBeDamaged"/> 게이트도 지나지 않는다 — 이건 피해가 아니라 굳은 몸의 끝이다.
    /// </summary>
    internal void ServerKillStuck()
    {
        if (IsSpawned && !IsServer)
            return;
        if (m_owner.Death.IsDead)
            return;

        SetHp(0, null, lethal: true);
    }

    /// <summary>방치 회복 틱 — NpcController.Update가 사망 게이트 통과 직후 매 프레임 부른다. 서버(또는 오프라인) 전용. (#707)</summary>
    internal void Tick()
    {
        if (IsSpawned && !IsServer)
            return;

        m_secondsSinceDamage += Time.deltaTime;

        if (CurrentHp >= MaxHp)
            return;
        if (m_secondsSinceDamage < m_owner.CommonConfig.RegenDelaySeconds)
            return;
        if (!NpcStateRules.CanRegenerate(m_owner))
            return;

        // 소수 누적 — 낮은 회복 속도에서 매 프레임 FloorToInt로 잘리는 손실 방지
        m_regenAccumulator += m_owner.CommonConfig.RegenHpPerSecond * Time.deltaTime;
        int wholeHp = Mathf.FloorToInt(m_regenAccumulator);
        if (wholeHp <= 0)
            return;

        m_regenAccumulator -= wholeHp;
        SetHp(Mathf.Min(CurrentHp + wholeHp, MaxHp), null);
    }

    /// <summary>
    /// 체력을 만으로 되돌린다 — <b>수감 지점 전용</b>. 서버(또는 오프라인)에서만 의미. (#571 후속)
    /// 부르는 곳은 <see cref="NpcCustody.SendToJail"/> 하나이며, 기절 해제와 같은 자리에서 불린다.
    ///
    /// 기상 회복(<see cref="ServerRestoreToOne"/>)이 1인 것과 달리 여기는 만이다 — 깎인 채로 두면
    /// 탈옥으로 풀려난 수감자가 진압봉 한 대에 눕는다.
    /// 이미 죽은 대상은 되살리지 않는다 — 사망은 되돌아오지 않는 끝이다.
    /// </summary>
    internal void ServerRestoreFull()
    {
        if (IsSpawned && !IsServer)
            return;
        if (m_owner.Death.IsDead)
            return;

        SetHp(MaxHp, null);
    }

    // 쓰러짐은 0 <b>도달</b> 엣지다 — 값이 아니라 '닿는 순간'을 본다.
    // lethal은 여기서 재지 않는다: 이미 0인 몸을 때리면 0→0이라 어떤 엣지도 안 걸린다 (IsLethal).
    private void SetHp(int value, GameObject attacker, bool lethal = false)
    {
        int previous = CurrentHp;
        m_hp = value;
        if (IsSpawned && IsServer)
            m_syncedHp.Value = value;

        if (lethal)
        {
            m_owner.Death.ServerEnterDead(attacker);
            return;
        }

        // 타격으로 쓰러진 기절은 테이저보다 길다 — 밧줄로 끌 창을 따로 튜닝한다 (#400)
        if (value == 0 && previous > 0)
            m_owner.Stun.EnterStunned(
                attacker != null ? attacker.transform : null,
                m_owner.StunConfig.KnockdownStunSeconds
            );
    }
}
