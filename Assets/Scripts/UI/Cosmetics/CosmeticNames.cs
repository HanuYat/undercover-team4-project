using System;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;

/// <summary>
/// 치장 아이템 이름 (#818) — 표는 <c>CosmeticsTable</c>이고 열쇠는 <b>프리팹 이름</b>이다
/// ("Cosmetic.&lt;프리팹 이름&gt;"). 카탈로그에 이름 필드를 두지 않는 이유가 여기 있다: 아이템이
/// 늘 때 손댈 자리를 표 한 곳으로 모은다.
///
/// 표에 없는 열쇠는 <b>열쇠 그대로</b> 돌려준다 — 빈 칸보다 "무엇이 빠졌는지" 보이는 편이 낫다.
/// </summary>
public static class CosmeticNames
{
    private const string k_table = "CosmeticsTable";
    private const string k_noneKey = "Cosmetic.None";

    /// <summary>언어가 바뀌었다 — 이름을 띄워 둔 쪽이 다시 읽는다. (설정 창에서 언어를 갈 수 있다)</summary>
    public static event Action OnLanguageChanged;

    // static 이벤트도 함께 리셋한다 — 도메인 리로드를 끄면 이전 플레이의 죽은 구독자가 남는다
    // (GameSettings.Load와 같은 방침).
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Hook()
    {
        OnLanguageChanged = null;
        LocalizationSettings.SelectedLocaleChanged -= HandleLocaleChanged;
        LocalizationSettings.SelectedLocaleChanged += HandleLocaleChanged;
    }

    private static void HandleLocaleChanged(Locale _) => OnLanguageChanged?.Invoke();

    /// <summary>그 프리팹의 표시 이름 — null이면 "안 씀"이다.</summary>
    public static string Of(GameObject prefab)
    {
        string name = Lookup(prefab == null ? k_noneKey : "Cosmetic." + prefab.name);
        if (!string.IsNullOrEmpty(name))
            return name;

        // 표를 못 읽는 자리에서도 칸이 비어 보이지 않게 한다 — 언어가 아직 안 정해졌거나
        // (에디터) 열쇠가 표에 없는 경우다. 프리팹 이름에서 출처·성별 토막만 걷어낸다.
        return prefab == null ? "-" : Prettify(prefab.name);
    }

    private static string Lookup(string key)
    {
        if (LocalizationSettings.SelectedLocaleAsync.IsDone
            && LocalizationSettings.SelectedLocaleAsync.Result == null)
            return null; // 언어가 아직 안 정해졌다 — 표를 뒤져도 빈 값이 온다

        try
        {
            return LocalizationSettings.StringDatabase.GetLocalizedString(k_table, key);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[{nameof(CosmeticNames)}] '{key}'를 읽지 못했다: {ex.Message}");
            return null;
        }
    }

    private static string Prettify(string name)
    {
        string[] tokens = name.Split('_');
        var kept = new System.Collections.Generic.List<string>(tokens.Length);
        foreach (string token in tokens)
            if (token != "Apo" && token != "Pol" && token != "Male" && token != "Female")
                kept.Add(token);

        return kept.Count == 0 ? name : string.Join(" ", kept);
    }
}
