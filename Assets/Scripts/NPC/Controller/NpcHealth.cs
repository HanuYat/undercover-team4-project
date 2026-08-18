using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// NPC 체력 도메인 부품 (#366/#503/#571) — <b>회복 지점이 없다.</b> 한 번 깎인 체력은 라운드가
/// 끝날 때까지 그대로다.
///
/// 체력이 만드는 결과는 둘이고 둘 다 되돌아오지 않는다: 임계 비율 아래로 내려가면 한 번 쓰러졌다
/// 일어나고(<see cref="NpcStun"/>), 0이 되면 죽는다(<see cref="NpcDeath"/>).
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
        SetHp(Mathf.Clamp(CurrentHp - amount, 0, MaxHp), attacker);

        // 피격 반응(#400)은 여기서 굴리지 않는다 — 환경 피해도 이 경로를 지나므로
        // 플레이어 타격 경로(Baton.ServerSwing)가 직접 부른다. 연출은 반대로 여기가 맞다 (#478).
        int applied = before - CurrentHp;
        if (applied > 0)
            BroadcastDamaged(applied, attacker);
    }

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

    // 체력 회복(구 ServerRestoreHp)은 #571에서 제거됐다 — 회복 지점 둘(NpcStun.ExitStun ·
    // NpcStunnedState.Exit)이 함께 사라져 부르는 곳이 없어졌다. 이유는 그 두 곳의 주석에 있다.

    /// <summary>
    /// 체력을 만으로 되돌린다 — <b>수감 지점 전용</b>. 서버(또는 오프라인)에서만 의미. (#571 후속)
    /// 부르는 곳은 <see cref="NpcCustody.SendToJail"/> 하나이며, 기절 해제와 같은 자리에서 불린다.
    ///
    /// <b>#571이 지운 회복과 다른 자리다.</b> 그쪽(기절에서 깨어날 때)은 임계 교차를 개체당 한 번으로
    /// 묶는 일방 톱니 — "쓰러진 몸이 다음에 맞으면 죽는다" — 를 무너뜨리므로 닫힌 채로 둔다.
    /// 수감은 그 톱니가 겨눈 자리가 아니다: 대상이 전투에서 빠져나가 판정까지 끝낸 뒤이고,
    /// 깎인 채로 두면 탈옥으로 풀려난 수감자가 진압봉 한 대에 죽는다.
    ///
    /// 위로 올리는 것은 어느 엣지도 건드리지 않는다 — 사망은 0 <b>도달</b>, 기절은 임계 아래로
    /// <b>내려가는</b> 순간만 보므로(<see cref="SetHp"/>) 회복은 통지 없이 값만 바뀐다.
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

    // 체력 변화가 만드는 결과는 둘이고, 둘 다 <b>엣지</b>다 — 값이 아니라 '넘어서는 순간'을 본다.
    // 덕분에 이미 그 아래인 대상에 추가 타격이 들어와도 기절 타이머가 리셋되지 않고, 죽은 대상이
    // 두 번 죽지도 않는다.
    //
    // ⚠ <b>사망이 먼저다.</b> 한 타격이 둘 다 만족하는 경우가 실제로 생긴다 — 최대 100·임계 40에서
    // 32→0 타격은 임계 교차이면서 동시에 0 도달이다. 순서를 뒤집으면 그 대상이 쓰러졌다가 죽는 것이
    // 아니라 쓰러지기만 하고 사망 처리가 <b>영영 걸리지 않는다</b>(EnterStunned가 IsStunned로 물러난 뒤
    // 다음 0 도달 엣지가 없다).
    private void SetHp(int value, GameObject attacker)
    {
        int previous = CurrentHp;
        m_hp = value;
        if (IsSpawned && IsServer)
            m_syncedHp.Value = value;

        if (value == 0 && previous > 0)
        {
            m_owner.Death.ServerEnterDead(attacker);
            return;
        }

        // 임계 아래로 <b>내려가는</b> 순간만 — 회복이 없어졌으므로(#571) 이 교차는 개체당 한 번뿐이다.
        // 그게 의도다: 임계 아래로 내려간 몸이 다음에 맞으면 다시 눕는 것이 아니라 죽는다.
        // 타격으로 쓰러진 기절은 테이저보다 길다 — 밧줄로 끌 창을 따로 튜닝한다 (#400)
        int knockdownHp = m_owner.CommonConfig.KnockdownHp;
        if (previous > knockdownHp && value <= knockdownHp)
            m_owner.Stun.EnterStunned(
                attacker != null ? attacker.transform : null,
                m_owner.StunConfig.KnockdownStunSeconds
            );
    }
}
