using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Localization;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Tables;

/// <summary>
/// 안경 어휘 배선 (#619) — 메뉴: Tools/안경 어휘 배선
///
/// 안경 축을 아래 표대로 통째로 다시 쓴다. <see cref="HairVocabularyPopulator"/>와 같은 이유로 코드에 표를 둔다.
///
/// 값 묶음은 비교 시트를 16px로 재서 정했다 — 기존 값들끼리 26~60칸 떨어져 있고, 후보는 가장 가까운 값과의
/// 거리로 붙였다(3~28칸). 눈으로 본 것과 결론이 갈린 곳이 있는데(<c>PS_Glasses_02</c>는 32px에서 검은
/// 선글라스처럼 보이지만 16px에서는 바이저와 8칸 차이) <b>묶는 기준은 몽타주 해상도</b>라 측정을 따랐다.
///
/// 배선 후에는 <c>Tools/몽타주 레이어 굽기</c>로 레이어를 다시 구워야 새 값의 그림이 생긴다.
/// </summary>
public static class EyewearVocabularyPopulator
{
    private const string k_generic = "Assets/Imported/Synty/PolygonGeneric/Prefabs/Characters/Attachments/SM_Gen_Chr_Attach_";
    private const string k_police = "Assets/Imported/Synty/PolygonPoliceStation/Prefabs/Characters/Chr_Attach/SM_Chr_Attach_";
    private const string k_apocalypse = "Assets/Imported/Synty/PolygonApocalypse/Prefabs/Characters/Attachments/SM_Chr_Attach_";

    private const string k_table = "NpcTable";

    /// <summary>값 하나 — 이름 키(+없으면 새로 팔 번역), 틴트 색, 그 값이 쓰는 메시들(0번이 몽타주 대표).</summary>
    private readonly struct Value
    {
        public readonly string Key;
        public readonly string Ko;
        public readonly string En;
        public readonly Color Color;
        public readonly bool SciFiOnly;
        public readonly string[] Meshes;

        public Value(string key, string ko, string en, Color color, bool sciFiOnly, params string[] meshes)
        {
            Key = key;
            Ko = ko;
            En = en;
            Color = color;
            SciFiOnly = sciFiOnly;
            Meshes = meshes;
        }
    }

    // 순서가 곧 인덱스다. 기존 네 키를 버리지 않고 자리만 옮겼다 — [2]는 메시를 그대로 두고 이름을
    // 바이저→안경으로, [3]은 고글→바이저로 고쳤다(눈 전체를 덮는 판이라 그게 맞다). '고글' 키는
    // 진짜 고글 메시가 들어온 [5]로 갔다. AppearanceModelCatalog에서 [3]을 쓰던 모델은 확인이 필요하다.
    //
    // 한 값에 묶은 메시는 같은 색으로 틴트한다 — 색이 그림에 들어가는 축이라(실루엣 축이 아니다)
    // 색을 통일해야 그림이 어느 메시든 맞다.
    private static readonly Value[] s_values =
    {
        new Value("Npc.Appearance.None", "없음", "None", Color.white, false),

        new Value(
            "Npc.Appearance.Eyewear.Sunglasses",
            "선글라스",
            "Sunglasses",
            new Color(0.08f, 0.08f, 0.08f, 1f),
            false,
            k_generic + "Sunglasses_01",
            k_police + "Glasses_02",
            k_police + "Glasses_01"
        ),
        new Value(
            "Npc.Appearance.Eyewear.Glasses",
            "안경",
            "Glasses",
            Color.white,
            false,
            k_police + "Glasses_03",
            k_apocalypse + "Soldier_Male_Glass_01",
            k_apocalypse + "Nerd_Female_Glasses_01"
        ),
        new Value(
            "Npc.Appearance.Eyewear.Visor",
            "바이저",
            "Visor",
            new Color(0.2f, 0.3f, 0.2f, 1f),
            false,
            k_generic + "Headset_01"
        ),
        new Value(
            "Npc.Appearance.Eyewear.GlowLens",
            "발광렌즈",
            "Glowing lens",
            new Color(0.2f, 0.8f, 0.8f, 1f),
            true,
            k_police + "Goggles_01"
        ),
        new Value(
            "Npc.Appearance.Eyewear.Goggles",
            "고글",
            "Goggles",
            Color.white,
            false,
            k_police + "Goggles_02"
        ),

        new Value(
            "Npc.Appearance.Eyewear.Eyepatch",
            "안대",
            "Eyepatch",
            Color.white, // 메시 자체가 검은 가죽이라 틴트하지 않는다
            false,
            k_apocalypse + "Press_Male_Eyepatch_01"
        ),
    };

    [MenuItem("Tools/안경 어휘 배선")]
    private static void Populate()
    {
        AppearanceDatabase database = LoadDatabase();
        if (database == null)
            return;

        StringTableCollection collection = LocalizationEditorSettings.GetStringTableCollection(k_table);
        if (collection == null)
        {
            Debug.LogError($"[안경 어휘] {k_table} 문자열 테이블을 찾지 못했다");
            return;
        }

        var options = new List<AppearanceDatabase.AppearanceOption>(s_values.Length);
        var missing = new List<string>();
        var created = new List<string>();

        foreach (Value value in s_values)
        {
            if (EnsureKey(collection, value))
                created.Add($"{value.Key} = {value.Ko} / {value.En}");

            options.Add(
                new AppearanceDatabase.AppearanceOption
                {
                    DisplayName = new LocalizedString(k_table, value.Key),
                    Color = value.Color,
                    PropPrefabs = LoadMeshes(value.Meshes, missing),
                    SciFiOnly = value.SciFiOnly,
                }
            );
        }

        if (missing.Count > 0)
        {
            Debug.LogError($"[안경 어휘] 찾지 못한 프리팹 {missing.Count}개 — 배선 중단\n  {string.Join("\n  ", missing)}");
            return;
        }

        database.GetAxis(AppearanceAxis.Eyewear).Options = options.ToArray();
        EditorUtility.SetDirty(database);
        AssetDatabase.SaveAssets();

        var log = new System.Text.StringBuilder();
        for (int i = 0; i < options.Count; i++)
        {
            GameObject[] meshes = options[i].PropPrefabs;
            string names = meshes == null || meshes.Length == 0
                ? "(프롭 없음)"
                : string.Join(" + ", System.Array.ConvertAll(meshes, m => m.name));
            log.Append("\n  [").Append(i).Append("] ").Append(s_values[i].Ko).Append("  ").Append(names);
        }
        if (created.Count > 0)
            log.Append("\n  새로 판 이름 키: ").Append(string.Join(", ", created));

        Debug.Log($"[안경 어휘] 값 {options.Count}개 배선 완료 — 이제 Tools/몽타주 레이어 굽기로 레이어를 다시 구울 것{log}");
    }

    /// <summary>이름 키가 없으면 판다 — id는 테이블의 생성기가 발급해야 나중에 겹치지 않는다.</summary>
    private static bool EnsureKey(StringTableCollection collection, in Value value)
    {
        SharedTableData shared = collection.SharedData;
        if (shared.GetId(value.Key) != SharedTableData.EmptyId)
            return false;

        shared.AddKey(value.Key);
        foreach (StringTable table in collection.StringTables)
        {
            table.AddEntry(value.Key, table.LocaleIdentifier.Code.StartsWith("ko") ? value.Ko : value.En);
            EditorUtility.SetDirty(table);
        }
        EditorUtility.SetDirty(shared);
        return true;
    }

    private static GameObject[] LoadMeshes(string[] paths, List<string> missing)
    {
        if (paths.Length == 0)
            return System.Array.Empty<GameObject>();

        var loaded = new GameObject[paths.Length];
        for (int i = 0; i < paths.Length; i++)
        {
            loaded[i] = AssetDatabase.LoadAssetAtPath<GameObject>(paths[i] + ".prefab");
            if (loaded[i] == null)
                missing.Add(paths[i] + ".prefab");
        }
        return loaded;
    }

    private static AppearanceDatabase LoadDatabase()
    {
        string[] guids = AssetDatabase.FindAssets("t:AppearanceDatabase");
        if (guids.Length == 0)
        {
            Debug.LogError("[안경 어휘] AppearanceDatabase 에셋을 찾지 못했다");
            return null;
        }
        return AssetDatabase.LoadAssetAtPath<AppearanceDatabase>(AssetDatabase.GUIDToAssetPath(guids[0]));
    }
}
