/// <summary>
/// 라운드 날씨 1종 (#700) — 준비 단계에 라운드당 <b>한 번</b> 뽑히고 스스로 끝나지 않는다(해제는 ServerReset뿐).
/// <see cref="SuddenEventManager"/>의 주기 추첨 후보에서 빠진다 — 한 라운드에 날씨가 둘일 수 없다.
/// 실효과는 InProgress부터다: 준비 단계에는 매니저가 <see cref="ISuddenEvent.ServerTick"/>을 안 돌려
/// 낙뢰 타격·빙판 누적이 저절로 멈춰 있다. 그래서 구현체에 페이즈 분기를 두지 않는다.
/// </summary>
public interface IRoundWeather : ISuddenEvent
{
    /// <summary>확률 표가 이 날씨를 가리키는 키.</summary>
    WeatherKind Kind { get; }
}
