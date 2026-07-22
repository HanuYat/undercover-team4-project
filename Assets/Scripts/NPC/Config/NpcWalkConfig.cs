using UnityEngine;

/// <summary>
/// Walk(배회 이동) 상태 튜닝 값. (#259 — NpcController에서 분리)
/// 다음 배회 지점을 뽑는 반경·최소거리. 추격(Chase)의 사냥 모드 배회도 이 값을 공유한다.
/// </summary>
[CreateAssetMenu(fileName = "NpcWalkConfig", menuName = "Undercover/NPC/Walk Config")]
public class NpcWalkConfig : ScriptableObject
{
    [Header("배회 반경")]
    [SerializeField] private float m_wanderRadius = 10f;

    [Header("배회 지점 최소 거리")]
    [Tooltip("다음 배회 지점이 이 거리보다 가까우면 다시 뽑는다 — 한두 걸음 걷고 마는 어색한 이동 방지")]
    [SerializeField] private float m_minWanderDistance = 3f;

    public float WanderRadius => m_wanderRadius;
    public float MinWanderDistance => m_minWanderDistance;
}
