/// <summary>
/// 라운드 날씨 종류 (#700) — 확률 표(<see cref="RoundWeatherTable"/>)가 날씨를 가리키는 키.
/// 맑음은 여기 없다 — "아무것도 안 뽑음"이라 대응 이벤트가 없고, 표의 맑음 확률이 그 갈림을 맡는다.
/// ⚠ 순서 변경·중간 삽입 금지 — 정수로 직렬화돼 기존 표의 가중치가 다른 날씨로 옮겨 간다. 새 날씨는 끝에.
/// </summary>
public enum WeatherKind
{
    Lightning,
    Fog,
    Snow,
}
