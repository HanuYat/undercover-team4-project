using UnityEngine;

/// <summary>
/// 추격 계산에 쓰는 거리 셈 — 부품 셋과 상태가 <b>같은 자를 쓰게</b> 모아 둔다. (#568)
/// </summary>
public static class ChaseMath
{
    /// <summary>
    /// 수평(XZ) 거리 — Y를 뺀다. (#568)
    ///
    /// 포획·사거리 판정이 이것을 쓰는 이유: Y를 포함하면 계단·경사면이나 피벗 높이차만으로도
    /// 수평으로 밀착한 상태가 포획 거리(1.3m)를 넘겨, <b>다 따라잡고도 판정이 안 붙는다</b>.
    /// </summary>
    public static float FlatDistance(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }
}
