using UnityEngine;

/// <summary>
/// 차저(구 괴한) 튜닝 값. (#291 — ThugAttacker에서 분리, #259 config SO 패턴)
/// 돌진 사이클(접근·윈드업·돌진·경직)과 명중 효과(데미지·넉백)를 담는다.
/// </summary>
[CreateAssetMenu(fileName = "ThugChargerConfig", menuName = "Undercover/Events/Thug Charger Config")]
public class ThugChargerConfig : ScriptableObject
{
    [Header("표적 탐색")]
    [Tooltip("이 반경(m) 안 가장 가까운 행동 가능 현장 플레이어를 표적으로 (다운 제외)")]
    [SerializeField] private float m_targetSearchRadius = 40f;
    [Tooltip("표적을 다시 고르는 주기(초)")]
    [SerializeField] private float m_retargetInterval = 0.25f;

    [Header("접근")]
    [Tooltip("표적과 이 거리(m) 이내로 붙으면 돌진 준비(윈드업)를 시작한다")]
    [SerializeField] private float m_chargeStartRange = 8f;
    [Tooltip("접근 이동 속도(m/s)")]
    [SerializeField] private float m_approachSpeed = 4f;

    [Header("윈드업 (회피 창)")]
    [Tooltip("돌진 직전 제자리에서 표적 방향을 겨누는 준비 시간(초) — 이 동안 피하면 빗나간다 (#220 철학)")]
    [SerializeField] private float m_windupSeconds = 0.6f;

    [Header("돌진")]
    [Tooltip("돌진 이동 속도(m/s)")]
    [SerializeField] private float m_chargeSpeed = 12f;
    [Tooltip("한 번의 돌진 최대 이동 거리(m)")]
    [SerializeField] private float m_chargeMaxDistance = 12f;
    [Tooltip("돌진 안전장치 — 이 시간(초)이 지나면 돌진을 끝낸다")]
    [SerializeField] private float m_chargeMaxSeconds = 1.2f;
    [Tooltip("돌진 경로가 플레이어에 이 거리(m) 이내로 스치면 명중")]
    [SerializeField] private float m_hitRadius = 1.5f;
    [Tooltip("돌진 중 벽으로 칠 콜라이더 — 차저 자신 레이어는 런타임에 자동 제외")]
    [SerializeField] private LayerMask m_obstacleMask = ~0;

    [Header("명중 효과")]
    [Tooltip("명중 1회당 플레이어 HP 감소량")]
    [SerializeField] private int m_hitDamage = 20;
    [Tooltip("명중 시 플레이어에 가하는 수평 넉백 초기 속도(m/s) — 돌진 충돌이라 강하게")]
    [SerializeField] private float m_knockbackSpeed = 20f;

    [Header("경직 / 쿨다운")]
    [Tooltip("빗맞거나 벽에 박은 뒤 제자리에서 움직이지 못하는 시간(초) — 반격 창")]
    [SerializeField] private float m_recoverSeconds = 1.5f;
    [Tooltip("명중 성공 후 다음 사이클까지 쉬는 시간(초)")]
    [SerializeField] private float m_hitCooldownSeconds = 0.8f;

    [Header("소란 (#81)")]
    [Tooltip("습격이 주변 시민을 패닉시키는 전파 반경(m)")]
    [SerializeField] private float m_disturbanceRadius = 8f;
    [Tooltip("소란 펄스 주기(초)")]
    [SerializeField] private float m_disturbancePulseInterval = 1f;

    public float TargetSearchRadius => m_targetSearchRadius;
    public float RetargetInterval => m_retargetInterval;
    public float ChargeStartRange => m_chargeStartRange;
    public float ApproachSpeed => m_approachSpeed;
    public float WindupSeconds => m_windupSeconds;
    public float ChargeSpeed => m_chargeSpeed;
    public float ChargeMaxDistance => m_chargeMaxDistance;
    public float ChargeMaxSeconds => m_chargeMaxSeconds;
    public float HitRadius => m_hitRadius;
    public LayerMask ObstacleMask => m_obstacleMask;
    public int HitDamage => m_hitDamage;
    public float KnockbackSpeed => m_knockbackSpeed;
    public float RecoverSeconds => m_recoverSeconds;
    public float HitCooldownSeconds => m_hitCooldownSeconds;
    public float DisturbanceRadius => m_disturbanceRadius;
    public float DisturbancePulseInterval => m_disturbancePulseInterval;
}
