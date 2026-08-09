using UnityEngine;

/// <summary>
/// 오검거 추격(Chasing) 상태 튜닝 값. (#278, #259 — NpcController에서 분리)
/// </summary>
[CreateAssetMenu(fileName = "NpcChaseConfig", menuName = "Undercover/NPC/Chase Config")]
public class NpcChaseConfig : ScriptableObject
{
    [Tooltip("추격 최고 속도(m/s) — 플레이어 전력질주(8)보다 1 낮게: 직선에서는 벗어날 수 있되 코너·군중에서 따라잡힌다")]
    [SerializeField] private float m_maxSpeed = 7f;
    [Tooltip("타겟 확보 후 최고 속도까지 걸리는 가속 시간(초)")]
    [SerializeField] private float m_accelSeconds = 8f;
    [Tooltip("추격 가능 범위(m) — 타겟이 벗어나면 범위 안의 다른 플레이어로 갈아탄다. 아무도 없으면 배회하며 사냥 모드")]
    [SerializeField] private float m_range = 30f;
    [Tooltip("이 거리(m) 안으로 붙으면 포획 — 잡힌 플레이어가 오검거 페널티를 받는다")]
    [SerializeField] private float m_catchDistance = 1.3f;
    [Tooltip("격퇴(호루라기 예정 #250) 시 도주하는 시간(초)")]
    [SerializeField] private float m_repelFleeSeconds = 3f;
    [Tooltip("격퇴당한 뒤 이 시간(초) 동안은 격퇴한 플레이어를 다시 노리지 않는다")]
    [SerializeField] private float m_retargetCooldown = 5f;

    [Header("조향 — 추격 중에만 적용하고 벗어날 때 원복한다 (#568)")]
    [Tooltip("추격 중 선회 속도(도/초). 선회 반경 = 최고속/이 값(라디안)이라 포획 거리보다 작아야 파고들 수 있다 — 시민 기본값(240)이면 반경 1.67m로 포획 거리(1.3m) 안쪽을 못 판다")]
    [SerializeField] private float m_turnSpeed = 900f;
    [Tooltip("추격 중 가속도(m/s²) — 방향을 튼 뒤 속도를 되찾는 빠르기. 낮으면 꺾을 때마다 뒤처진다")]
    [SerializeField] private float m_acceleration = 24f;
    [Tooltip("표적이 이 시간(초) 뒤에 있을 위치를 조준한다. 크면 코너를 질러가고, 너무 크면 급반전에 속아 엉뚱한 데로 간다")]
    [SerializeField] private float m_maxLeadSeconds = 0.6f;

    public float MaxSpeed => m_maxSpeed;
    public float AccelSeconds => m_accelSeconds;
    public float Range => m_range;
    public float CatchDistance => m_catchDistance;
    public float RepelFleeSeconds => m_repelFleeSeconds;
    public float RetargetCooldown => m_retargetCooldown;
    public float TurnSpeed => m_turnSpeed;
    public float Acceleration => m_acceleration;
    public float MaxLeadSeconds => m_maxLeadSeconds;
}
