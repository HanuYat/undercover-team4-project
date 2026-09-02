using UnityEngine;

/// <summary>
/// 래그돌 골반 밑 지면 탐색 — NPC·플레이어 래그돌이 같은 식으로 쓰던 것을 모았다.
/// 정착 자격 판정과 정착 정렬이 <b>같은 것</b>을 써야 한다(다르면 그 차이가 얼리는 순간 낙차로 남는다).
/// </summary>
public static class RagdollGround
{
    private const float k_probeLift = 0.5f; // 골반이 바닥에 파묻혀 있어도 레이가 지면 위에서 출발하게

    public static bool TryGroundUnder(
        Vector3 hipsPosition, float probeDistance, LayerMask mask, out Vector3 point)
    {
        bool hitGround = Physics.Raycast(
            hipsPosition + Vector3.up * k_probeLift,
            Vector3.down,
            out RaycastHit hit,
            k_probeLift + probeDistance,
            mask,
            QueryTriggerInteraction.Ignore
        );

        point = hitGround ? hit.point : hipsPosition;
        return hitGround;
    }
}
