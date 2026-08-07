using UnityEngine;

/// <summary>
/// 감정표현 휠의 기하 계산 — 마우스 방향을 8칸 중 하나로 환산한다. (#219)
///
/// 순수 함수만 두는 이유는 이 계산이 <b>화면에서 검증하기 가장 어려운 부분</b>이기 때문이다.
/// 경계각이 한 칸씩 밀려 있어도 화면에서는 "가끔 엉뚱한 게 나온다"로만 보이고, 그 상태로
/// 휠 UI·입력·네트워크를 다 지나가면 원인을 좁히는 데 오래 걸린다.
///
/// 좌표계는 <b>12시가 0번, 시계방향</b>이다 — 휠 UI가 아이콘을 배치하는 순서와 같아야 하므로
/// <see cref="SlotCenterDegrees"/>를 같은 곳에서 제공한다. 두 곳에서 각자 각도를 계산하면
/// 한쪽만 고쳤을 때 그림과 판정이 조용히 어긋난다.
/// </summary>
public static class EmoteWheelGeometry
{
    /// <summary>휠 칸 수 — 8칸 고정. EmoteLoadout.k_slotCount와 같아야 한다.</summary>
    public const int k_slotCount = 8;

    /// <summary>
    /// 이 반경 안에서는 선택이 없다(정규화 기준). 휠을 열었다가 아무것도 고르지 않고
    /// 그냥 떼는 길을 남기기 위한 것 — 없으면 휠을 여는 순간 무조건 뭔가 발동된다.
    /// </summary>
    public const float k_deadZone = 0.35f;

    private const float k_degreesPerSlot = 360f / k_slotCount;

    /// <summary>
    /// 방향 벡터를 슬롯 인덱스로 환산한다. 데드존 안이면 -1.
    /// 벡터 크기는 정규화된 값(휠 반경 기준)으로 넘길 것 — 데드존 판정에 쓴다.
    /// </summary>
    public static int SlotFromDirection(Vector2 direction, float deadZone = k_deadZone)
    {
        if (direction.magnitude < deadZone)
            return -1;

        // Atan2(x, y)로 넣으면 12시가 0도, 시계방향이 +가 된다 (일반적인 Atan2(y, x)와 축이 바뀐 형태).
        float degrees = Mathf.Atan2(direction.x, direction.y) * Mathf.Rad2Deg;

        // 슬롯 중심을 기준으로 반 칸 밀어 내림하면 경계가 칸 사이에 놓인다.
        // Repeat을 쓰는 이유는 음수 각도(-180~0)와 360 넘김을 한 번에 접기 위해서다.
        float shifted = Mathf.Repeat(degrees + k_degreesPerSlot * 0.5f, 360f);
        return Mathf.FloorToInt(shifted / k_degreesPerSlot) % k_slotCount;
    }

    /// <summary>슬롯의 중심 각도(도) — 12시가 0, 시계방향. 휠 UI의 아이콘 배치가 쓴다.</summary>
    public static float SlotCenterDegrees(int slot) => slot * k_degreesPerSlot;
}
