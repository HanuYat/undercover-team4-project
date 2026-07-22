using UnityEngine;

/// <summary>
/// 기절(Stunned) 상태 튜닝 값. (#259 — NpcController에서 분리)
/// 테이저 등으로 무력화된 지속 시간을 정한다.
/// </summary>
[CreateAssetMenu(fileName = "NpcStunConfig", menuName = "Undercover/NPC/Stun Config")]
public class NpcStunConfig : ScriptableObject
{
    [Tooltip("기절(테이저 등) 지속 시간(초)")]
    [SerializeField] private float m_stunSeconds = 3f;

    public float StunSeconds => m_stunSeconds;
}
