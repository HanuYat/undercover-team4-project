using UnityEngine.Localization.Settings;

/// <summary>
/// 테이블 이름 + 키로 문자열을 즉시 조회하는 헬퍼. 설계 정본: docs/design/localization.md (#497)
///
/// <b>인스펙터에서 고를 것이 없는 자리에만 쓴다.</b> 표시 문구는 원칙적으로 `LocalizedString`
/// SerializeField로 두지만(그래야 어떤 키를 쓸지 화면마다 고를 수 있다), 아래 둘은 고를 것이 없다.
///  · <b>규약 기반 키</b> — <c>접두 + enum 이름</c>으로 코드가 만든다 (문서 §2 결정 (h)).
///    매핑 에셋도 인스펙터 배선도 두지 않는 것이 그 결정의 요지다.
///  · <b>전역 단위·서식</b> — 금액 표기처럼 프로젝트 전체가 한 문구를 쓰는 자리.
///
/// 같은 문구를 여러 인스턴스가 쓰는 경우 SerializeField로 두면 인스턴스마다 같은 키를 다시 배선해야
/// 하고, 하나만 빠지면 조용히 그 인스턴스만 옛 문구로 남는다 — 진열대 3개가 그런 자리다.
///
/// <b>언어 변경 갱신은 호출부 책임이다.</b> 이 헬퍼는 지금 언어로 한 번 읽어 주기만 한다.
/// 호출부가 <c>LocalizationSettings.SelectedLocaleChanged</c>를 구독해 다시 그려야 한다
/// (<see cref="ShopStand"/>가 그 방식이다 — 항목별 StringChanged를 여럿 구독하는 대신
/// 로케일 변경 한 곳에 걸고 표시를 통째로 다시 채운다).
/// </summary>
public static class LocalizedStrings
{
    /// <summary>
    /// 키를 지금 언어로 해석한다. 인자를 넘기면 Smart String으로 채운다.
    /// 로컬라이제이션 설정이 아직 없으면(에디터 초기화 전·테스트) 키를 그대로 돌려준다 —
    /// 빈 문자열을 돌려주면 화면에서 사라져 원인을 찾기 어렵다.
    /// </summary>
    public static string Get(string table, string key, params object[] args)
    {
        if (string.IsNullOrEmpty(table) || string.IsNullOrEmpty(key))
            return key;

        // HasSettings로 먼저 확인한다 — 종료 중에 설정 에셋을 되살리지 않기 위한 관례 (ShopStand와 동일)
        if (!LocalizationSettings.HasSettings)
            return key;

#if UNITY_EDITOR
        WarnIfMissing(table, key);
#endif

        string value =
            args != null && args.Length > 0
                ? LocalizationSettings.StringDatabase.GetLocalizedString(table, key, args)
                : LocalizationSettings.StringDatabase.GetLocalizedString(table, key);

        return string.IsNullOrEmpty(value) ? key : value;
    }

#if UNITY_EDITOR
    // 실제로 밟은 경로에서 빠진 키를 알려 준다 — 규약 키는 컴파일러가 막아 주지 않으므로 이것이
    // 두 번째 그물이다(첫 번째는 에디터 메뉴 Tools ▸ Localization ▸ 규약 키 검증).
    // 값 조회 자체는 건드리지 않는다 — 폴백 처리는 Localization 쪽에 맡기고 진단만 얹는다.
    private static readonly System.Collections.Generic.HashSet<string> s_warned =
        new System.Collections.Generic.HashSet<string>();

    private static void WarnIfMissing(string table, string key)
    {
        // 같은 키를 매 프레임 조회하는 자리도 있다(진열대 갱신) — 한 번만 짖는다
        string id = table + "/" + key;
        if (s_warned.Contains(id))
            return;

        var stringTable = LocalizationSettings.StringDatabase.GetTable(table);
        if (stringTable != null && stringTable.GetEntry(key) != null)
            return;

        s_warned.Add(id);
        UnityEngine.Debug.LogWarning(
            $"[LocalizedStrings] '{table}'에 키 '{key}'가 없다. "
                + "규약 기반 키라면 enum 값만 늘고 테이블 키가 빠진 것이다 — "
                + "Tools ▸ Localization ▸ 규약 키 검증으로 전체를 확인할 것."
        );
    }
#endif
}
