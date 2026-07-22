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

    public float MaxSpeed => m_maxSpeed;
    public float AccelSeconds => m_accelSeconds;
    public float Range => m_range;
    public float CatchDistance => m_catchDistance;
    public float RepelFleeSeconds => m_repelFleeSeconds;
    public float RetargetCooldown => m_retargetCooldown;
}
