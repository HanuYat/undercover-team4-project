using System;

/// <summary>
/// 충전 가능한 아이템이 구현하는 인터페이스.
/// 배터리 상태를 읽기 전용으로 노출하고, 본부 충전기 등이 Charge()로 충전한다.
/// (GDD 5-2: 스캐너는 배터리 충전식이며 본부 충전기에서만 재충전)
/// </summary>
public interface IChargeable
{
    /// <summary>현재 배터리 잔량.</summary>
    int CurrentBattery { get; }

    /// <summary>배터리 최대치.</summary>
    int MaxBattery { get; }

    /// <summary>배터리가 최대치까지 충전되었는지 여부.</summary>
    bool IsFullyCharged { get; }

    /// <summary>배터리가 모두 소진되었는지 여부. (스캐너 등 사용 가능 판정에 사용)</summary>
    bool IsDepleted { get; }

    /// <summary>배터리가 충전되었을 때 발생하는 이벤트. 충전 후 현재 잔량을 전달한다. (UI 갱신 등)</summary>
    event Action<int> OnCharged;

    /// <summary>배터리를 amount만큼 충전한다. 최대치를 넘지 않도록 구현체가 클램프한다. (본부 충전기가 호출)</summary>
    void Charge(int amount);
}
