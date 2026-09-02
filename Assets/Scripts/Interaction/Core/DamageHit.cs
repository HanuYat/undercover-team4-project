using UnityEngine;

/// <summary>
/// 피격 1회의 <b>표현용</b> 정보 — 연출 컴포넌트가 구독하는 OnDamaged 이벤트의 페이로드다. (#476)
///
/// <see cref="IDamageable.TakeDamage"/>의 인자를 그대로 나르지 않고 여기서 한 번 환산한다:
/// 가해자가 <c>GameObject</c>라 RPC에 실을 수 없으므로 <b>월드 좌표</b>로 바꿔 보내고,
/// 받는 쪽은 자기 카메라 기준으로 방향을 계산한다. 덕분에 <see cref="IDamageable"/> 시그니처를
/// 건드리지 않고도(= <c>BombDevice</c> 등 기존 데미지 소스를 그대로 두고) 연출이 붙는다.
///
/// 체력 비율은 일부러 담지 않는다 — HP는 이미 동기화 값이라(<c>PlayerHealth.CurrentHp</c>)
/// 연출 컴포넌트가 직접 읽으면 되고, 저체력 표시는 순간 이벤트가 아니라 지속 상태라 폴링이 맞다.
/// </summary>
public readonly struct DamageHit
{
    /// <summary>실제로 깎인 체력. 요청한 데미지가 아니라 <b>적용된 양</b>이다(HP 하한에서 잘린 뒤).</summary>
    public readonly int Amount;

    /// <summary>가해자의 월드 좌표. <see cref="HasAttacker"/>가 false면 의미 없는 값이다.</summary>
    public readonly Vector3 AttackerPosition;

    /// <summary>가해자를 아는 피해인가 — false면 방향을 표시할 수 없다(출처 불명 환경 피해).</summary>
    public readonly bool HasAttacker;

    public DamageHit(int amount, Vector3 attackerPosition, bool hasAttacker)
    {
        Amount = amount;
        AttackerPosition = attackerPosition;
        HasAttacker = hasAttacker;
    }
}
