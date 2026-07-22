using UnityEngine;

/// <summary>
/// 컨트롤러 레벨 공용 튜닝 값 — 특정 FSM 상태에 속하지 않는다. (#259 — NpcController에서 분리)
/// 스폰 시 개체 편차, 소란 브로드캐스트(#81), 넉백 비행 물리(#232)를 담는다.
/// </summary>
[CreateAssetMenu(fileName = "NpcCommonConfig", menuName = "Undercover/NPC/Common Config")]
public class NpcCommonConfig : ScriptableObject
{
    [Header("개체별 이동 속도 편차 (배율)")]
    [Tooltip("스폰 시 NavMeshAgent 속도에 이 범위의 랜덤 배율을 곱한다 — 군중이 전부 같은 속도로 걷는 것 방지")]
    [SerializeField] private float m_spawnSpeedMultiplierMin = 0.8f;
    [SerializeField] private float m_spawnSpeedMultiplierMax = 1.2f;

    [Header("소란 전파 (#81)")]
    [Tooltip("소란(저항 전투·도주)이 주변 시민을 패닉시키는 전파 반경(m)")]
    [SerializeField] private float m_disturbanceRadius = 8f;
    [Tooltip("저항·도주 중 소란 펄스를 발산하는 주기(초) — 소란이 계속되면 지나가던 시민도 놀란다")]
    [SerializeField] private float m_disturbancePulseInterval = 1f;

    [Header("넉백 (폭발 등 외력) — #232")]
    [Tooltip("날아가는 동안 받는 중력. 음수 — 클수록 낮고 빠르게 떨어진다")]
    [SerializeField] private float m_knockbackGravity = -18f;
    [Tooltip("안전장치: 이 시간(초)이 지나도 착지 판정이 안 나면 강제로 내려놓는다")]
    [SerializeField] private float m_knockbackMaxFlightSeconds = 3f;
    [Tooltip("착지 지점을 NavMesh 위로 되돌릴 때 허용하는 최대 탐색 거리(m)")]
    [SerializeField] private float m_knockbackLandSampleDistance = 4f;
    [Tooltip("날아가는 도중 벽으로 칠 콜라이더 — 여기에 걸리면 수평 이동이 멈춘다. NPC 자신의 레이어는 런타임에 자동으로 빠진다")]
    [SerializeField] private LayerMask m_knockbackObstacleMask = ~0;

    public float SpawnSpeedMultiplierMin => m_spawnSpeedMultiplierMin;
    public float SpawnSpeedMultiplierMax => m_spawnSpeedMultiplierMax;
    public float DisturbanceRadius => m_disturbanceRadius;
    public float DisturbancePulseInterval => m_disturbancePulseInterval;
    public float KnockbackGravity => m_knockbackGravity;
    public float KnockbackMaxFlightSeconds => m_knockbackMaxFlightSeconds;
    public float KnockbackLandSampleDistance => m_knockbackLandSampleDistance;
    public LayerMask KnockbackObstacleMask => m_knockbackObstacleMask;
}
