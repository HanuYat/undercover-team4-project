using UnityEngine;

/// <summary>
/// 배달 아이템 흩뿌리기 계산 (#824) — 겹쳐 쌓이면 조준으로 골라 줍기 어려우니 지점 둘레에 흩뿌리고,
/// 앵커의 authored 높이가 실제 표면과 어긋나도 메시에 파묻히지 않게 바닥에 스냅한다.
/// ShopDelivery(실내 폴백)와 DeliveryPad(패드) 양쪽이 같은 계산을 쓴다.
/// </summary>
public static class DeliveryScatter
{
    private const float k_groundProbeHeight = 2f;
    private const float k_groundClearance = 0.02f;

    public static Vector3 Resolve(
        Vector3 anchor,
        int index,
        float spreadRadius,
        LayerMask groundMask
    )
    {
        Vector3 position = anchor;
        if (index > 0)
        {
            float angle = (index % 6) * 60f * Mathf.Deg2Rad;
            float radius = spreadRadius * (1f + index / 6);
            position += new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;
        }

        return SnapToGround(position, groundMask);
    }

    public static Vector3 SnapToGround(Vector3 candidate, LayerMask groundMask)
    {
        Vector3 probeOrigin = candidate + Vector3.up * k_groundProbeHeight;
        if (
            Physics.Raycast(
                probeOrigin,
                Vector3.down,
                out RaycastHit hit,
                k_groundProbeHeight * 2f,
                groundMask,
                QueryTriggerInteraction.Ignore
            )
        )
            return hit.point + Vector3.up * k_groundClearance;

        return candidate;
    }
}
