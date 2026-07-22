using UnityEngine;

/// <summary>
/// 기절(Stunned) 상태 튜닝 값. (#259 — NpcController에서 분리)
/// 테이저 등으로 무력화된 지속 시간과, 그 끝에 일어나는 모션 구간(#269)을 정한다.
/// </summary>
[CreateAssetMenu(fileName = "NpcStunConfig", menuName = "Undercover/NPC/Stun Config")]
public class NpcStunConfig : ScriptableObject
{
    [Tooltip("기절(테이저 등) 지속 시간(초)")]
    [SerializeField] private float m_stunSeconds = 3f;
    [Tooltip("기절이 풀릴 때 일어나는 모션의 길이(초) — 기절 시간의 마지막 이 구간에 일어난다(총 무력화 시간은 그대로). Knockdown01-StandUp 클립 길이(1.17초) ÷ 재생속도 배율(빌더 k_standUpSpeed, 2배)에 맞춘 값 (#269)")]
    [SerializeField] private float m_standUpSeconds = 0.585f;

    public float StunSeconds => m_stunSeconds;
    public float StandUpSeconds => m_standUpSeconds;
}
