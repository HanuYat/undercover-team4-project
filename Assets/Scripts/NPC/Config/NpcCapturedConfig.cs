using UnityEngine;

/// <summary>
/// 체포(Captured) 상태 튜닝 값. (#230, #259 — NpcController에서 분리)
/// 인계되지 않고 방치될 때 수갑을 풀고 도주하기까지의 타이밍을 정한다.
/// </summary>
[CreateAssetMenu(fileName = "NpcCapturedConfig", menuName = "Undercover/NPC/Captured Config")]
public class NpcCapturedConfig : ScriptableObject
{
    [Tooltip("체포된 채 이 시간(초) 동안 인계되지 않으면 수갑을 풀고 도주한다 — 방치 전략 차단")]
    [SerializeField]
    private float m_escapeSeconds = 30f;

    public float EscapeSeconds => m_escapeSeconds;
}
