using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Tables;

/// <summary>
/// 머리 어휘 배선 (#619) — 메뉴: Tools/머리 어휘 배선
///
/// 머리 스타일 축을 아래 표대로 통째로 다시 쓴다. 손으로 34칸을 채우는 대신 표를 코드에 두는 이유는
/// 둘이다: PolygonGeneric 부착물이 FBX의 <b>프리팹 변형</b>이라 참조를 텍스트로 쓸 수 없고(루트 fileID가
/// 파일에 없다), 값·이름·메시의 대응이 문서(appearance-montage.md §13-6)와 어긋나면 안 되기 때문이다.
///
/// <b>한 값에 메시를 여럿 넣은 곳은 그것들이 몽타주에서 서로 구분되지 않기 때문이다</b>(16px 실루엣 5% 미만).
/// 그림은 0번으로 한 장만 굽으므로, 구분되는 메시를 한 값에 섞으면 그림이 실물과 어긋난다 (§1).
///
/// 배선 후에는 <c>Tools/몽타주 레이어 굽기</c>로 레이어를 다시 구워야 새 값의 그림이 생긴다.
/// </summary>
public static class HairVocabularyPopulator
{
    private const string k_generic = "Assets/Imported/Synty/PolygonGeneric/Prefabs/Characters/Attachments/SM_Gen_Chr_Attach_";
    private const string k_police = "Assets/Imported/Synty/PolygonPoliceStation/Prefabs/Characters/Chr_Attach/SM_Chr_Attach_";
    private const string k_apocalypse = "Assets/Imported/Synty/PolygonApocalypse/Prefabs/Characters/Attachments/SM_Chr_Attach_";

    private const string k_table = "NpcTable";

    /// <summary>값 하나 — 이름 키 + 그 값이 쓰는 메시들(0번이 몽타주 대표).</summary>
    private readonly struct Value
    {
        public readonly string Key;
        public readonly string[] Meshes;

        public Value(string key, params string[] meshes)
        {
            Key = key;
            Meshes = meshes;
        }
    }

    private const string k_bald = "Npc.Appearance.HairStyle.Bald";
    private const string k_covered = "Npc.Appearance.HairStyle.Covered";
    private const string k_short = "Npc.Appearance.HairStyle.Short";
    private const string k_veryShort = "Npc.Appearance.HairStyle.VeryShort";
    private const string k_tied = "Npc.Appearance.HairStyle.Tied";
    private const string k_bob = "Npc.Appearance.HairStyle.Bob";

    // 순서가 곧 인덱스다. 0(대머리)과 마지막(가림)은 프롭이 없는 값이라 자리를 고정해 둔다 —
    // AppearanceModelCatalog가 인덱스로 이 축을 가리키므로, 표를 고치면 카탈로그도 함께 봐야 한다.
    private static readonly Value[] s_values =
    {
        new Value(k_bald), // [0] 대머리 — 프롭 없음

        new Value(k_bob, k_generic + "Hair_04", k_generic + "Hair_05"),
        new Value(k_bob, k_apocalypse + "Nerd_Female_Hair_01"),
        new Value(k_bob, k_apocalypse + "Waitress_Female_Hair_01"),
        new Value("Npc.Appearance.HairStyle.MidBob", k_apocalypse + "Teen_Female_Hair_01"),
        new Value("Npc.Appearance.HairStyle.Dreads", k_generic + "Hair_07"),
        new Value("Npc.Appearance.HairStyle.Shaggy", k_generic + "Hair_08"),
        new Value("Npc.Appearance.HairStyle.Asymmetric", k_apocalypse + "Emo_Female_Hair_01"),
        new Value("Npc.Appearance.HairStyle.Mohawk", k_apocalypse + "Criminal_Male_Hair_01"),
        new Value("Npc.Appearance.HairStyle.Mohawk", k_police + "Hair_08"),

        new Value(k_tied, k_generic + "Ponytail_01"),
        new Value(k_tied, k_apocalypse + "Cool_Female_Hair_01"),
        new Value(k_tied, k_apocalypse + "Islander_Male_Hair_01"),
        new Value(k_tied, k_apocalypse + "Punk_Female_Hair_01", k_apocalypse + "Zombie_Female_Hair_02"),
        new Value(k_tied, k_apocalypse + "Zombie_Female_Hair_01"),
        new Value(k_tied, k_apocalypse + "Wanderer_Male_Hair_01"),
        new Value(k_tied, k_apocalypse + "Soldier_Female_Hair_01"),
        new Value(k_tied, k_police + "Hair_02"),
        new Value(k_tied, k_police + "Hair_05"),

        new Value(k_short, k_apocalypse + "Business_Male_Hair_01"),
        new Value(k_short, k_apocalypse + "Cool_Male_Hair_01"),
        new Value(k_short, k_apocalypse + "Zombie_Male_Hair_01"),
        new Value(k_short, k_apocalypse + "Zombie_Male_Hair_02"),
        new Value(k_short, k_apocalypse + "Biker_Male_Hair_01", k_generic + "Hair_11"),
        new Value(k_short, k_generic + "Hair_06"),
        new Value(k_short, k_police + "Hair_02_Alt"),
        new Value(k_short, k_police + "Hair_06"),

        new Value(k_veryShort, k_apocalypse + "Press_Male_Hair_01"),
        new Value(k_veryShort, k_apocalypse + "RiotCop_Male_Hair_01"),
        new Value(k_veryShort, k_apocalypse + "Sheriff_Male_Hair_01", k_generic + "Hair_10"),
        new Value(k_veryShort, k_generic + "Hair_09"),
        new Value(k_veryShort, k_generic + "Hair_09_alt"),
        new Value(k_veryShort, k_police + "Hair_03"),
        new Value(k_veryShort, k_police + "Hair_07"),
        new Value(k_veryShort, k_police + "Hair_07_Alt"),

        new Value(k_covered), // 마지막 — 프롭 없는 SciFi 전용 값
    };

    [MenuItem("Tools/머리 어휘 배선")]
    private static void Populate()
    {
        AppearanceDatabase database = LoadDatabase();
        if (database == null)
            return;

        SharedTableData shared = LoadSharedTable();
        if (shared == null)
            return;

        var options = new List<AppearanceDatabase.AppearanceOption>(s_values.Length);
        var missing = new List<string>();

        foreach (Value value in s_values)
        {
            long id = shared.GetId(value.Key);
            if (id == SharedTableData.EmptyId)
                missing.Add("키 " + value.Key);

            var option = new AppearanceDatabase.AppearanceOption
            {
                DisplayName = new LocalizedString(k_table, value.Key),
                Color = Color.white, // 머리는 실루엣으로 굽고 머리색 축이 칠한다
                PropPrefabs = LoadMeshes(value.Meshes, missing),
                SciFiOnly = value.Meshes.Length == 0 && value.Key == k_covered,
            };
            options.Add(option);
        }

        if (missing.Count > 0)
        {
            Debug.LogError($"[머리 어휘] 찾지 못한 것 {missing.Count}개 — 배선 중단\n  {string.Join("\n  ", missing)}");
            return;
        }

        var serialized = new SerializedObject(database);
        SerializedProperty axis = serialized.FindProperty("m_hairStyle").FindPropertyRelative("Options");
        axis.arraySize = options.Count;
        serialized.ApplyModifiedProperties();

        // SerializedProperty로 배열 크기만 맞추고 내용은 직접 넣는다 — LocalizedString은 중첩이 깊어
        // SerializedProperty로 채우면 필드 경로를 일일이 알아야 한다
        AppearanceDatabase.AxisDefinition definition = database.GetAxis(AppearanceAxis.HairStyle);
        for (int i = 0; i < options.Count; i++)
            definition.Options[i] = options[i];

        EditorUtility.SetDirty(database);
        AssetDatabase.SaveAssets();

        var log = new System.Text.StringBuilder();
        for (int i = 0; i < options.Count; i++)
        {
            GameObject[] meshes = options[i].PropPrefabs;
            string names = meshes == null || meshes.Length == 0
                ? "(프롭 없음)"
                : string.Join(" + ", System.Array.ConvertAll(meshes, m => m.name));
            log.Append("\n  [").Append(i).Append("] ").Append(s_values[i].Key).Append("  ").Append(names);
        }
        Debug.Log($"[머리 어휘] 값 {options.Count}개 배선 완료 — 이제 Tools/몽타주 레이어 굽기로 레이어를 다시 구울 것{log}");
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
                missing.Add("프리팹 " + paths[i] + ".prefab");
        }
        return loaded;
    }

    private static AppearanceDatabase LoadDatabase()
    {
        string[] guids = AssetDatabase.FindAssets("t:AppearanceDatabase");
        if (guids.Length == 0)
        {
            Debug.LogError("[머리 어휘] AppearanceDatabase 에셋을 찾지 못했다");
            return null;
        }
        return AssetDatabase.LoadAssetAtPath<AppearanceDatabase>(AssetDatabase.GUIDToAssetPath(guids[0]));
    }

    private static SharedTableData LoadSharedTable()
    {
        foreach (string guid in AssetDatabase.FindAssets("t:SharedTableData"))
        {
            var shared = AssetDatabase.LoadAssetAtPath<SharedTableData>(AssetDatabase.GUIDToAssetPath(guid));
            if (shared != null && shared.TableCollectionName == k_table)
                return shared;
        }

        Debug.LogError($"[머리 어휘] {k_table}의 SharedTableData를 찾지 못했다");
        return null;
    }
}
