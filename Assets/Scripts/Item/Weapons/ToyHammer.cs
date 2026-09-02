using UnityEngine;

/// <summary>
/// 거대 뿅망치 아이템 
/// 낮은 확률(기본 1%)을 굴려, 성공하면 9999 데미지를 대신 넣는다.
/// </summary>
public class ToyHammer : Baton
{
    [Header("거대 뿅망치 (#816)")]
    [Tooltip("한 대당 대박이 터질 확률. 0.01 = 1%. 굴림은 서버에서만 돈다 (RollSwingPower)")]
    [Range(0f, 1f)]
    [SerializeField]
    private float m_criticalChance = 0.01f;

    [Tooltip(
        "대박이 터졌을 때의 데미지. NPC 최대 체력(기본 100)을 훌쩍 넘겨 어떤 체력 상태에서도 0으로 떨어뜨린다 — "
            + "동료(PlayerHealth)도 같은 규칙(건강하면 다운, 이미 다운 상태면 확인사살)으로 그대로 맞는다"
    )]
    [Min(1)]
    [SerializeField]
    private int m_criticalDamage = 9999;

    protected override string WeaponLogName => "뿅망치";

    /// <summary>
    /// 평타(부모 <see cref="Baton.RollSwingPower"/>가 정한 프리팹 데미지)에 낮은 확률로 대박을
    /// 얹는다. <see cref="Random.value"/>는 서버(또는 오프라인) 프로세스에서만 평가된다 —
    /// 이 메서드 자체가 서버 판정 경로(<c>ServerResolveHitAtImpactAsync</c>) 안에서만 불리기 때문이다.
    /// </summary>
    protected override SwingPower RollSwingPower() =>
        Random.value < m_criticalChance
            ? new SwingPower(m_criticalDamage, true)
            : base.RollSwingPower();

    /// <summary>
    /// 평타·대박 각각 하나의 소리뿐이다 — 로봇/사람을 가리지 않는다. 개그 아이템이라
    /// 진압봉의 재질별 구분(깡/퍽)을 굳이 물려받지 않고 뿅망치 전용 소리로 통일한다.
    /// </summary>
    protected override EFx ImpactFxFor(NpcController npc, PlayerHealth player, bool critical) =>
        critical ? EFx.HammerCrit : EFx.HammerHit;
}
