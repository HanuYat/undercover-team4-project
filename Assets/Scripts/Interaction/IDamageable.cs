using UnityEngine;

/// <summary>
/// 피해를 받을 수 있는 대상 — 저항형 NPC의 범위 타격(#79)을 시작으로,
/// 거리 난동자·괴한 습격(GDD 6-4) 등 모든 데미지 소스가 이 경로로 대상 HP를 깎는다.
/// 구현체는 서버 권위로만 실제 값을 변경해야 한다 (클라이언트 호출은 무시).
/// </summary>
public interface IDamageable
{
    /// <summary>피해 적용. attacker는 데미지 출처 — 패배 판정·어그로 등에 쓰이며 null 허용.</summary>
    void TakeDamage(int amount, GameObject attacker);
}
