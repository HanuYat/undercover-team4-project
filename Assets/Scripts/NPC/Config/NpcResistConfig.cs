using UnityEngine;

/// <summary>
/// 저항(Attack) 전투 튜닝 값. (#79, #220, #259 — NpcController에서 분리)
/// 체력·타격량은 NpcCommonConfig로 이관됐다 (#366) — 저항 전용 값이 아니게 됐기 때문이다.
/// ThreatSearchRadiusMultiplier는 AttackRange와 곱해져 위협 탐색 반경(#205/#213)을 정한다.
/// </summary>
[CreateAssetMenu(fileName = "NpcResistConfig", menuName = "Undercover/NPC/Resist Config")]
public class NpcResistConfig : ScriptableObject
{
    [Tooltip("저항 중 범위 타격을 휘두르는 주기(초)")]
    [SerializeField] private float m_attackInterval = 1.5f;
    [Tooltip("범위 타격이 닿는 반경(m)")]
    [SerializeField] private float m_attackRange = 2f;
    [Tooltip("범위 타격 1회당 플레이어 HP 감소량")]
    [SerializeField] private int m_attackDamage = 10;
    [Tooltip("표적이 사라진 채 이 시간(초)이 지나면 배회로 복귀한다 — 제한시간 도주를 없애면서 생긴 Attack 상태의 유일한 시간 기반 출구 (#366)")]
    [SerializeField] private float m_noTargetIdleSeconds = 5f;
    [Tooltip("유발자가 이 거리(m) 밖으로 달아나면 표적을 잃은 것으로 본다 — 위 복귀 타이머를 여는 조건. 도주의 이탈 거리(NpcFleeConfig.EscapeDistance)와 같은 값으로 둘 것 (#366)")]
    [SerializeField] private float m_giveUpDistance = 25f;
    [Tooltip("스윙 시작→타격 프레임까지의 시간(초) — 클립별 오프셋이 비었거나 범위 밖일 때만 쓰는 폴백값 (#220)")]
    [SerializeField] private float m_strikeOffsetSeconds = 0.45f;
    [Tooltip("스윙 변형별 타격 오프셋(초). 인덱스 = 클립 순서(attack02·03·04·05). 배열 길이가 곧 변형 개수 — 블렌드 트리 자식 수와 같아야 한다 (#220)")]
    [SerializeField] private float[] m_swingImpactOffsets = { 0.63f, 0.53f, 0.44f, 0.73f };
    [Tooltip("타격이 닿는 정면 부채꼴의 전체 각도(도). 이 각도 안(정면 기준 ±절반)에 있는 플레이어만 맞는다 (#220)")]
    [SerializeField] private float m_attackConeAngle = 120f;
    [Tooltip("저항 중 표적을 바라보도록 도는 회전 속도(도/초) — 부채꼴 기준 방향을 표적에 맞춘다 (#220)")]
    [SerializeField] private float m_attackTurnSpeed = 540f;
    [Tooltip("스윙 1회 동안 제자리에 멈춰 때리는 시간(초) — 추격을 멈춰 미끄러지며 때리는 그림을 막는다. 드라이버의 스윙 모션 유지시간(0.9)과 맞출 것")]
    [SerializeField] private float m_swingHoldSeconds = 0.9f;
    [Tooltip("위협 탐색 반경 배율 — AttackRange에 곱한다. 저항 패배 후 도주 대상 탐색(#205)·도주 방향 산출(#213)이 공유")]
    [SerializeField] private float m_threatSearchRadiusMultiplier = 5f;

    [Tooltip(
        "저항 추격 중 가속도(m/s²) — 진입 시 에이전트에 덮어쓰고 Exit에서 원복한다 (#568 후속).\n\n"
            + "선회 반경 = 속도² / 이 값이다. 저항 중에는 updateRotation을 꺼 두고 몸을 직접 돌리므로"
            + "(AttackTurnSpeed) 궤적을 제약하는 것은 각속도가 아니라 이 값이다.\n\n"
            + "시민 기본값(8)이면 저항 속도 6m/s에서 반경 4.5m — 정지 거리(AttackRange×0.8=1.6m)보다 커서"
            + "표적이 원을 그리면 안쪽으로 파고들지 못하고 같이 공전한다. 30이면 반경 1.2m로 그 안에 들어온다"
    )]
    [Min(0.1f)]
    [SerializeField] private float m_chaseAcceleration = 30f;

    public float AttackInterval => m_attackInterval;
    public float AttackRange => m_attackRange;
    public int AttackDamage => m_attackDamage;
    public float NoTargetIdleSeconds => m_noTargetIdleSeconds;

    /// <summary>유발자를 표적에서 놓는 거리(m). 도주의 이탈 거리와 같은 기준이다 — "쫓다가 포기하는
    /// 거리"와 "도망쳐서 벗어난 거리"가 어긋나면 같은 상황을 두 규칙이 다르게 판정한다. (#366)</summary>
    public float GiveUpDistance => m_giveUpDistance;
    public float StrikeOffsetSeconds => m_strikeOffsetSeconds;
    public float AttackConeAngle => m_attackConeAngle;
    public float AttackTurnSpeed => m_attackTurnSpeed;
    public float SwingHoldSeconds => m_swingHoldSeconds;
    public float ThreatSearchRadiusMultiplier => m_threatSearchRadiusMultiplier;

    /// <summary>저항 추격 중 에이전트 가속도 — 곧 <b>선회 반경</b>이다(반경 = 속도² / 이 값). (#568 후속)
    /// <see cref="AttackTurnSpeed"/>와 혼동하지 말 것: 저쪽은 부채꼴 기준 방향을 맞추는 <b>몸통 회전</b>이고,
    /// 이쪽은 <b>이동 궤적</b>을 정한다. 저항 중에는 updateRotation이 꺼져 있어 각속도가 궤적에 관여하지 않는다.</summary>
    public float ChaseAcceleration => m_chaseAcceleration;

    /// <summary>스윙 변형 개수 — 오프셋 배열 길이(=블렌드 트리 클립 수). 비어 있으면 단일 변형(0)으로 폴백. (#220)</summary>
    public int SwingVariantCount =>
        m_swingImpactOffsets != null && m_swingImpactOffsets.Length > 0 ? m_swingImpactOffsets.Length : 1;

    /// <summary>변형 index에 해당하는 타격 오프셋(초). 범위 밖이면 고정 폴백값. (#220)</summary>
    public float SwingImpactOffset(int variant) =>
        m_swingImpactOffsets != null && variant >= 0 && variant < m_swingImpactOffsets.Length
            ? m_swingImpactOffsets[variant]
            : m_strikeOffsetSeconds;
}
