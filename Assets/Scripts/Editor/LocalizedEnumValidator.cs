using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.Localization;
using UnityEngine;
using UnityEngine.Localization.Tables;

/// <summary>
/// 규약 기반 키(<see cref="LocalizedEnumAttribute"/>)에 빠진 항목이 없는지 확인한다. (#497)
/// 설계 정본: docs/design/localization.md §2 결정 (h)
///
/// 규약 매핑은 매핑 에셋을 없애 주는 대신 <b>컴파일러가 막지 못하는 구멍</b>을 남긴다 — enum에 값을
/// 추가하고 테이블 키를 잊으면 그 값에서만 문구가 빈다. 그 구멍을 메우는 것이 이 검사다.
///
/// 검사 대상 목록은 따로 두지 않는다 — enum에 붙은 선언 자체가 목록이다. 새 규약을 만들면 enum에
/// 어트리뷰트를 붙이는 것으로 검사에 자동 편입된다.
/// </summary>
public static class LocalizedEnumValidator
{
    private const string k_menu = "Tools/Localization/규약 키 검증";

    [MenuItem(k_menu)]
    public static void Validate()
    {
        var problems = new List<string>();
        int checkedKeys = 0;
        int enums = 0;

        foreach (Type type in CollectAnnotatedEnums())
        {
            enums++;
            foreach (LocalizedEnumAttribute rule in type.GetCustomAttributes<LocalizedEnumAttribute>())
            {
                StringTableCollection collection = LocalizationEditorSettings.GetStringTableCollection(rule.Table);
                if (collection == null)
                {
                    problems.Add($"{type.Name}: 테이블 '{rule.Table}'을 찾을 수 없다 (접두 '{rule.KeyPrefix}')");
                    continue;
                }

                foreach (string valueName in Enum.GetNames(type))
                {
                    if (Array.IndexOf(rule.Except, valueName) >= 0)
                        continue;

                    checkedKeys++;
                    string key = rule.KeyPrefix + valueName;

                    if (collection.SharedData.GetEntry(key) == null)
                    {
                        problems.Add($"{type.Name}.{valueName}: '{rule.Table}'에 키 '{key}'가 없다");
                        continue;
                    }

                    // 키만 있고 값이 빈 로케일도 잡는다 — 폴백으로 반대 언어가 그대로 노출된다 (§6 3번)
                    foreach (StringTable table in collection.StringTables)
                    {
                        StringTableEntry entry = table.GetEntry(key);
                        if (entry == null || string.IsNullOrEmpty(entry.Value))
                            problems.Add($"{type.Name}.{valueName}: '{key}'의 {table.LocaleIdentifier.Code} 값이 비었다");
                    }
                }
            }
        }

        if (problems.Count == 0)
        {
            Debug.Log($"[규약 키 검증] 이상 없음 — enum {enums}개 · 키 {checkedKeys}개 확인");
            return;
        }

        var sb = new StringBuilder();
        sb.Append("[규약 키 검증] 문제 ").Append(problems.Count).Append("건 (enum ").Append(enums)
          .Append("개 · 키 ").Append(checkedKeys).Append("개 확인)");
        foreach (string p in problems)
            sb.Append('\n').Append("  · ").Append(p);

        Debug.LogError(sb.ToString());
    }

    // 어트리뷰트가 붙은 enum만 모은다. 게임 코드는 Assembly-CSharp에 있지만 어셈블리 정의가 늘어도
    // 견디게 로드된 어셈블리를 훑는다 — 검사는 에디터에서 사람이 돌리는 것이라 비용이 문제되지 않는다.
    private static IEnumerable<Type> CollectAnnotatedEnums()
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                types = e.Types; // 일부 타입만 못 읽는 어셈블리는 읽힌 것만 본다
            }

            foreach (Type type in types)
            {
                if (type != null && type.IsEnum && type.IsDefined(typeof(LocalizedEnumAttribute), false))
                    yield return type;
            }
        }
    }
}
