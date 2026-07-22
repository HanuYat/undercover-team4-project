using UnityEngine;

/// <summary>
/// 패닉(Panic) 상태 튜닝 값. (#81, #259 — NpcController에서 분리)
/// 소란에 놀란 시민이 달아나는 속도·거리·진정 시간을 정한다.
/// </summary>
[CreateAssetMenu(fileName = "NpcPanicConfig", menuName = "Undercover/NPC/Panic Config")]
public class NpcPanicConfig : ScriptableObject
{
    [Tooltip("패닉 시 기본 이동 속도에 곱하는 배율")]
    [SerializeField] private float m_speedMultiplier = 1.8f;
    [Tooltip("패닉 도주 지점을 한 번에 이만큼(m) 앞으로 잡는다")]
    [SerializeField] private float m_stepDistance = 8f;
    [Tooltip("마지막 소란 감지 후 이 시간(초)이 지나면 진정하고 배회로 복귀")]
    [SerializeField] private float m_calmSeconds = 5f;

    public float SpeedMultiplier => m_speedMultiplier;
    public float StepDistance => m_stepDistance;
    public float CalmSeconds => m_calmSeconds;
}
