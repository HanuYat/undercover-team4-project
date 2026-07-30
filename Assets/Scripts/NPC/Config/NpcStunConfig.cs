using UnityEngine;

/// <summary>
/// 기절(Stunned) 상태 튜닝 값. (#259 — NpcController에서 분리)
/// 테이저 등으로 무력화된 지속 시간과, 그 끝에 일어나는 모션 구간(#269)을 정한다.
/// </summary>
[CreateAssetMenu(fileName = "NpcStunConfig", menuName = "Undercover/NPC/Stun Config")]
public class NpcStunConfig : ScriptableObject
{
    [Tooltip("기절 지속 시간(초) — 테이저 무력화와 넉백 착지 KO가 쓴다 (GDD 8-3의 기준값)")]
    [SerializeField] private float m_stunSeconds = 3f;
    [Tooltip("체력 0으로 쓰러진 기절의 지속 시간(초) — 테이저와 분리된 값이다. 밧줄 묶기가 무력화된 대상만 대상이 되면서(#446) 이 시간이 곧 검거할 수 있는 유일한 창이라 난이도의 핵심 손잡이. 마지막 StandUpSeconds는 일어나는 모션이라 실제로 누워 있는 시간은 그만큼 짧다(5초 → 약 4.4초)")]
    [SerializeField] private float m_knockdownStunSeconds = 5f;
    [Tooltip("기절이 풀릴 때 일어나는 모션의 길이(초) — 기절 시간의 마지막 이 구간에 일어난다(총 무력화 시간은 그대로). Knockdown01-StandUp 클립 길이(1.17초) ÷ 재생속도 배율(빌더 k_standUpSpeed, 2배)에 맞춘 값 (#269)")]
    [SerializeField] private float m_standUpSeconds = 0.585f;

    public float StunSeconds => m_stunSeconds;

    /// <summary>체력 0으로 쓰러진 기절의 지속 시간 — 타격 경로 전용. (#400)</summary>
    public float KnockdownStunSeconds => m_knockdownStunSeconds;
    public float StandUpSeconds => m_standUpSeconds;
}
