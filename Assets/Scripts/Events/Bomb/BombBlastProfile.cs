using System;
using UnityEngine;

/// <summary>
/// 폭발 세기 — 반경·피해·넉백·래그돌 임펄스의 <b>감쇠식과 노브를 함께 든 값 객체</b>. (#768 분할)
///
/// <b>Unity 타입이 아니다</b> — 세 감쇠식은 순수 계산이고, 여기 붙은 튜닝 근거가 폭탄 코드에서
/// 가장 두꺼운 덩어리다. 떼어 두면 감쇠식을 읽으려고 NGO RPC·수명 타이머를 지나칠 필요가 없다.
/// 폭심은 <b>인자로 받는다</b>(<c>transform</c>이 없다) — 거리 0일 때 쓸 대체 방향도 함께 받는다.
///
/// ScriptableObject로 만들지 않았다 — 라운드당 폭탄 1종이라 공유할 대상이 없고, 에셋이 늘면
/// 프리팹과 에셋 두 곳을 맞춰야 한다.
/// </summary>
[Serializable]
public class BombBlastProfile
{
    [Tooltip("이 반경(m) 안의 플레이어·NPC가 피해·넉백을 받는다")]
    [SerializeField]
    private float m_explosionRadius = 8f;

    [Tooltip("폭심에서의 피해량 — 반경 끝까지 m_damageEdgeFalloff 비율로 선형 감쇠한다")]
    [SerializeField]
    private int m_explosionDamage = 150;

    [Tooltip("반경 끝에서 남는 피해 비율 — 폭심(1.0)에서 반경 끝까지 선형 감쇠. " +
             "즉사 반경 = 반경 × (1 − 최대HP/피해) / (1 − 이 값). 기본값(150·0.2·8m)이면 약 3.3m")]
    [Range(0f, 1f)]
    [SerializeField]
    private float m_damageEdgeFalloff = 0.2f;

    [Tooltip("넉백 세기(m/s) — 폭심에서 밀려나는 초기 속도")]
    [SerializeField]
    private float m_knockbackForce = 12f;

    [Tooltip("수평 세기 대비 위로 띄우는 비율 — 0이면 순수 수평으로만 밀린다")]
    [Range(0f, 1f)]
    [SerializeField]
    private float m_knockbackUpwardRatio = 0.45f;

    [Tooltip("반경 끝에서 남는 세기 비율 — 폭심(1.0)에서 반경 끝까지 선형 감쇠한다")]
    [Range(0f, 1f)]
    [SerializeField]
    private float m_knockbackEdgeFalloff = 0.25f;

    [Tooltip("사망자 래그돌의 수평 세기 = 넉백 수평 세기 × 이 값. <b>연출 노브다</b> — 크게 잡으면 " +
             "시원하게 날아간다. 예전 주석이 말하던 '캡슐이 못 따라온다'는 제약은 이미 사라졌다 " +
             "(EvaluateRagdollImpulse 주석)")]
    [Range(0f, 1f)]
    [SerializeField]
    private float m_ragdollImpulseScale = 0.22f;

    [Tooltip("사망자 래그돌의 상승 세기 = 위 수평 세기 × 이 값. <b>곧 발사각이다</b> — 1.0이 45°로 " +
             "사거리 최대이고, 크면 높이 뜨는 대신 가까이 떨어진다. 예전 값 1.8은 61°라 속도를 " +
             "높이에 낭비했다. 정점(m) ≈ (수평세기 × 이 값)² / 19.6")]
    [Range(0f, 3f)]
    [SerializeField]
    private float m_ragdollLiftRatio = 1.2f;

    /// <summary>피해·넉백이 닿는 반경(m).</summary>
    public float Radius => m_explosionRadius;

    /// <summary>폭심에서의 피해량 — 로그·튜닝 표시용.</summary>
    public int PeakDamage => m_explosionDamage;

    /// <summary>반경 끝에서 남는 피해 비율 — 로그·튜닝 표시용.</summary>
    public float DamageEdgeFalloff => m_damageEdgeFalloff;

    /// <summary>넉백 세기(m/s).</summary>
    public float KnockbackForce => m_knockbackForce;

    /// <summary>
    /// <paramref name="delta"/>(폭심 → 대상)가 받는 넉백 속도(m/s) — 반경 밖이면 <see cref="Vector3.zero"/>.
    ///
    /// <b>넉백 세기의 단일 지점.</b> 플레이어는 각 피어가 자기 오너 캐릭터에 적용하고
    /// (<see cref="BombExplosionView"/>), NPC는 서버가 직접 민다(<see cref="BombBlast"/>) —
    /// 두 경로가 각자 감쇠식을 들면 같은 폭발인데 사람과 시민이 다르게 날아간다.
    /// </summary>
    /// <param name="delta">폭심에서 대상까지의 벡터. y는 이 안에서 지운다.</param>
    /// <param name="fallbackDirection">거리 0(폭탄을 정확히 밟고 선 경우)에 쓸 방향.</param>
    public Vector3 EvaluateKnockback(Vector3 delta, Vector3 fallbackDirection)
    {
        if (m_explosionRadius <= 0f || m_knockbackForce <= 0f)
            return Vector3.zero;

        delta.y = 0f; // 밀리는 방향은 수평 — 띄우는 성분은 아래에서 따로 더한다
        float distance = delta.magnitude;
        if (distance > m_explosionRadius)
            return Vector3.zero;

        // 폭탄을 정확히 밟고 선 경우(거리 0)엔 방향이 없다 — 대체 방향으로 밀어 예외 없이 날아가게 한다
        Vector3 direction = distance > 0.01f ? delta / distance : fallbackDirection;
        float scaled = m_knockbackForce * Mathf.Lerp(1f, m_knockbackEdgeFalloff, distance / m_explosionRadius);

        return direction * scaled + Vector3.up * (scaled * m_knockbackUpwardRatio);
    }

    /// <summary>
    /// <paramref name="delta"/>(폭심 → 대상)가 받는 피해량 — 반경 밖이면 0. <b>피해 세기의 단일 지점.</b>
    ///
    /// 감쇠가 있어야 사망 래그돌이 성립한다(#506 §4) — 균일 피해로는 "반경 안 전원 생존" 아니면
    /// "전원 즉사"뿐이라 폭심은 날아가고 가장자리는 밀리는 그림이 나오지 않는다.
    /// <b>거리는 3차원으로 잰다</b> — 넉백이 y를 지우는 것은 밀리는 방향이 수평이어야 하기 때문이고
    /// 피해에는 방향이 없다. 대상 수집도 3차원이라 수평 거리를 쓰면 수집됐는데 피해가 0인 대상이 생긴다.
    /// </summary>
    public int EvaluateDamage(Vector3 delta)
    {
        if (m_explosionRadius <= 0f || m_explosionDamage <= 0)
            return 0;

        float distance = delta.magnitude;
        if (distance > m_explosionRadius)
            return 0;

        float scaled = m_explosionDamage
            * Mathf.Lerp(1f, m_damageEdgeFalloff, distance / m_explosionRadius);
        return Mathf.RoundToInt(scaled);
    }

    /// <summary>
    /// 사망자 래그돌 임펄스 — <see cref="EvaluateKnockback"/>의 <b>수평 성분과 방향</b>만 가져와
    /// 래그돌용 세기·들어올림으로 다시 세운다.
    ///
    /// <b>비행 거리는 설계 제약이 아니라 연출 노브다</b> — 예전 근거(캡슐이 지형에 막힌다)는
    /// <c>PlayerRagdoll.TickCapsuleFollow</c>가 직접 대입으로 바뀌며 사라졌고, 남은 실질 상한은
    /// <c>PlayerRagdoll.m_flightAlignPullSpeed</c> 하나다(둘은 짝이다).
    /// <b>세기와 들어올림을 따로 잡는다</b> — 균일하게 곱하면 시체가 바닥을 훑는다. 비율이 곧 발사각이라
    /// 1.0(45°)이 사거리 최대다. 튜닝은 폭심~3.3m(즉사 구간)만 보면 된다 (2026-08-06 실측, k≈1.05).
    /// </summary>
    public Vector3 EvaluateRagdollImpulse(Vector3 delta, Vector3 fallbackDirection)
    {
        Vector3 knockback = EvaluateKnockback(delta, fallbackDirection);
        Vector3 horizontal = new Vector3(knockback.x, 0f, knockback.z) * m_ragdollImpulseScale;
        if (horizontal == Vector3.zero)
            return Vector3.zero;

        return horizontal + Vector3.up * (horizontal.magnitude * m_ragdollLiftRatio);
    }
}
