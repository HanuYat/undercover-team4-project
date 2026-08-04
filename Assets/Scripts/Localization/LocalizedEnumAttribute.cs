using System;

/// <summary>
/// 이 enum의 값 이름이 문자열 테이블 키의 일부라는 선언. 설계 정본: docs/design/localization.md §2 결정 (h)
///
/// 규약 기반 매핑(<c>접두 + enum 이름</c>)은 매핑 에셋이라는 "두 번째 진실"을 없애는 대신,
/// <b>컴파일러가 막아 주지 못하는 구멍</b>을 남긴다 — 값을 추가하고 테이블 키를 잊으면 그 값에서만
/// 문구가 비고, 그 코드 경로를 실제로 밟기 전까지 아무 신호도 없다.
///
/// 그래서 규약을 <b>enum 선언부에</b> 붙인다. 값을 추가하는 사람이 가장 먼저 보는 자리이고,
/// <c>LocalizedEnumValidator</c>(에디터 메뉴 <i>Tools ▸ Localization ▸ 규약 키 검증</i>)가 이 선언을
/// 근거로 값 전부에 키가 있는지 확인한다. 선언이 곧 검증 대상 목록이므로 별도 등록표를 두지 않는다.
///
/// 접두가 둘 이상이면(이름·설명처럼) 여러 번 붙인다.
/// <example><code>
/// [LocalizedEnum("ItemTable", "Item.Name.", nameof(EInstallable.None))]
/// [LocalizedEnum("ItemTable", "Item.Description.", nameof(EInstallable.None))]
/// public enum EInstallable { None, SignalDecoder, JailSirenButton }
/// </code></example>
/// </summary>
[AttributeUsage(AttributeTargets.Enum, AllowMultiple = true)]
public sealed class LocalizedEnumAttribute : Attribute
{
    public LocalizedEnumAttribute(string table, string keyPrefix, params string[] except)
    {
        Table = table;
        KeyPrefix = keyPrefix;
        Except = except ?? Array.Empty<string>();
    }

    /// <summary>키가 들어 있는 테이블 컬렉션 이름.</summary>
    public string Table { get; }

    /// <summary>키 접두 — 여기에 enum 값 이름을 붙이면 키가 된다.</summary>
    public string KeyPrefix { get; }

    /// <summary>키를 두지 않는 값 이름 — 표시 대상이 아닌 값(<c>None</c> 같은 것).</summary>
    public string[] Except { get; }
}
