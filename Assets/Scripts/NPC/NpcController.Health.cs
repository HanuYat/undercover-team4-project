using Unity.Netcode;
using UnityEngine;

/// <summary>
/// NPC 체력 (#366) — 저항 제압 게이지(#76/#79)를 대체한다. 0이 되면 기절(Stunned)한다.
///
/// 서버 권위 + 오프라인 폴백: 서버(또는 오프라인)만 값을 바꾸고 클라이언트는 동기화 값을 읽는다.
/// <see cref="PlayerData"/>의 HP와 같은 규칙이며, m_networkState와 같은 이중 구조를 쓴다 (#56 패턴).
///
/// <b>지속형이다</b> — 교전이 끝나도 깎인 체력은 남는다(구 게이지는 저항 진입마다 리셋됐다).
/// 회복 지점은 기절에서 깨어나는 순간 하나뿐이다(<see cref="ServerRestoreHp"/>, NpcStunnedState).
/// </summary>
public partial class NpcController : IDamageable
{
    // 서버 권위 HP — 서버만 쓰고 모든 클라이언트가 읽는다. m_hp가 서버·오프라인의 진실값.
    private readonly NetworkVariable<int> m_syncedHp = new NetworkVariable<int>();
    private int m_hp;

    /// <summary>최대 체력 — HUD가 비율 계산에 읽는다. (#366)</summary>
    public int MaxHp => m_commonConfig.MaxHp;

    /// <summary>현재 체력. 세션 중에는 동기화 값이라 클라이언트에서도 안전하게 읽을 수 있다. (#366)</summary>
    public int CurrentHp => IsSpawned ? m_syncedHp.Value : m_hp;

    /// <summary>체력 초기화 — InitBehavior에서 서버(또는 오프라인) 1회 호출된다.</summary>
    private void InitHealth()
    {
        SetHp(MaxHp, null);
    }

    /// <summary>
    /// 피해 적용 (<see cref="IDamageable"/>) — 진압봉 타격 등 모든 데미지 소스의 공통 경로. (#366)
    ///
    /// 신병을 확보했거나 오검거 페널티가 진행 중인 상태(<see cref="NpcStateRules.CanBeDamaged"/>가 false)
    /// 에서는 <b>피해 자체를 무시</b>한다. 이 게이트가 없으면 호송 중인 NPC를 때려 기절시켜 신병에서
    /// 빼내는 우회가 생긴다. 테이저는 #292로 그 상태들에도 걸리게 됐지만(스턴은 링크를 끊지 않는다)
    /// 타격은 계속 막는다 — 팀 결정이다.
    /// 무시하는 쪽을 택한 이유: HP만 깎고 기절은 막으면 "HP 0인데 기절 아님" 상태가 생겨,
    /// 엣지 트리거 특성상 그 NPC가 영영 기절하지 않게 된다.
    /// </summary>
    /// <param name="amount">깎을 체력. 0 이하는 무시한다.</param>
    /// <param name="attacker">가해자 — 기절 시 위협 대상으로 넘긴다. null 허용.</param>
    public void TakeDamage(int amount, GameObject attacker)
    {
        if (IsSpawned && !IsServer)
            return;
        if (amount <= 0)
            return;
        if (!NpcStateRules.CanBeDamaged(CurrentState))
            return;

        SetHp(Mathf.Clamp(CurrentHp - amount, 0, MaxHp), attacker);
    }

    /// <summary>
    /// 체력 완전 회복 — 기절에서 깨어나는 순간 <see cref="NpcStunnedState"/>가 호출한다. (#366)
    /// 이 회복이 빠지면 HP 0인 채로 깨어나고, 아래 엣지 트리거 때문에 두 번 다시 기절하지 않는다.
    /// </summary>
    public void ServerRestoreHp()
    {
        if (IsSpawned && !IsServer)
            return;

        SetHp(MaxHp, null);
    }

    // 0에 '도달하는 순간'에만 기절시킨다 — PlayerData.SetHp의 무력화 진입과 같은 엣지 트리거.
    // 덕분에 이미 0인 대상에 대한 추가 타격이 기절 타이머를 리셋하지 못한다.
    private void SetHp(int value, GameObject attacker)
    {
        int previous = CurrentHp; // 변경 전 값 — 0 도달 '순간'을 잡기 위함
        m_hp = value;
        if (IsSpawned && IsServer)
            m_syncedHp.Value = value;

        if (value == 0 && previous > 0)
            EnterStunned(attacker != null ? attacker.transform : null);
    }
}
