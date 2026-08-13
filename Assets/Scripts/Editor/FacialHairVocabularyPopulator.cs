using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Localization;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Tables;

/// <summary>
/// 수염 어휘 배선 (#619) — 메뉴: Tools/수염 어휘 배선
///
/// 수염 축을 아래 표대로 통째로 다시 쓴다. <see cref="HairVocabularyPopulator"/>와 같은 이유로 코드에 표를 둔다:
/// Synty 부착물이 FBX의 <b>프리팹 변형</b>이라 참조를 텍스트로 쓸 수 없고, 값·이름·메시의 대응이
/// 문서(appearance-montage.md §13-10)와 어긋나면 안 되기 때문이다.
///
/// 값 묶음은 <c>Tools/몽타주 레이어 굽기</c>의 비교 시트를 16px로 보고 정했다 — 몽타주에서 갈리는 것은
/// <b>덮는 범위</b> 세 단계(얇게 깔림 / 턱 덩어리 / 뺨까지)와 자리(입술 위만 / 양옆만)다.
/// 한 값에 함께 넣은 메시는 그 해상도에서 서로 구분되지 않는 것들이고, 그림은 0번으로 한 장만 굽는다.
///
/// 배선 후에는 <c>Tools/몽타주 레이어 굽기</c>로 레이어를 다시 구워야 새 값의 그림이 생긴다.
/// </summary>
public static class FacialHairVocabularyPopulator
{
    private const string k_generic = "Assets/Imported/Synty/PolygonGeneric/Prefabs/Characters/Attachments/SM_Gen_Chr_Attach_";
    private const string k_police = "Assets/Imported/Synty/PolygonPoliceStation/Prefabs/Characters/Chr_Attach/SM_Chr_Attach_";
    private const string k_apocalypse = "Assets/Imported/Synty/PolygonApocalypse/Prefabs/Characters/Attachments/SM_Chr_Attach_";

    private const string k_table = "NpcTable";

    // 머리와 달리 실루엣으로 굽지 않는 축이라(MontageLayerBaker.IsSilhouetteAxis) 값이 색을 직접 들고 있다.
    // 기존 세 값이 쓰던 색을 그대로 쓴다 — 수염은 머리색 축의 틴트를 받지 않으므로 이 색이 곧 화면 색이다.
    private static readonly Color k_beardColor = new Color(0.15f, 0.12f, 0.1f, 1f);

    /// <summary>값 하나 — 이름 키(+없으면 새로 팔 번역) + 그 값이 쓰는 메시들(0번이 몽타주 대표).</summary>
    private readonly struct Value
    {
        public readonly string Key;
        public readonly string Ko;
        public readonly string En;
        public readonly string[] Meshes;

        public Value(string key, string ko, string en, params string[] meshes)
        {
            Key = key;
            Ko = ko;
            En = en;
            Meshes = meshes;
        }
    }

    // 순서가 곧 인덱스다. 기존 네 값(없음·콧수염·턱수염·구레나룻)의 자리를 그대로 두고 뒤에 셋만 붙인다 —
    // AppearanceModelCatalog가 인덱스로 이 축을 가리키므로, 자리를 유지하면 카탈로그를 고치지 않아도 된다.
    private static readonly Value[] s_values =
    {
        new Value("Npc.Appearance.None", "없음", "None"), // [0] 프롭 없음

        new Value(
            "Npc.Appearance.FacialHair.Mustache",
            "콧수염",
            "Moustache",
            k_generic + "Moustache_01",
            k_police + "Moustache_01",
            k_police + "Moustache_02"
        ),
        new Value(
            "Npc.Appearance.FacialHair.Beard",
            "턱수염",
            "Beard",
            k_apocalypse + "Homeless_Male_Beard_01",
            k_apocalypse + "Wanderer_Male_Beard_01"
        ),
        new Value("Npc.Appearance.FacialHair.Sideburns", "구레나룻", "Sideburns", k_generic + "Chops_01"),

        new Value(
            "Npc.Appearance.FacialHair.Goatee",
            "염소수염",
            "Goatee",
            k_apocalypse + "Criminal_Male_Beard_01",
            k_apocalypse + "Biker_Male_Beard_01"
        ),
        new Value(
            "Npc.Appearance.FacialHair.Stubble",
            "짧은 수염",
            "Stubble",
            k_apocalypse + "Hunter_Male_Beard_01",
            k_apocalypse + "Zombie_Male_Beard_01"
        ),
        new Value(
            "Npc.Appearance.FacialHair.Bushy",
            "덥수룩한 수염",
            "Bushy beard",
            k_generic + "Beard_01",
            k_generic + "Beard_02"
        ),
    };

    // 표에 없는 후보 하나 — RiotCop_Male_Beard_01은 수염이 아니라 하관을 덮는 판(입 자리만 뚫린 마스크)이다.
    // 수염 축에 넣으면 몽타주가 "수염"이라고 말하는데 화면에는 마스크가 보여 §1이 깨진다.
    // 마스크 축이 생기면 그리로 간다 (#586 프로토콜 +1).

    [MenuItem("Tools/수염 어휘 배선")]
    private static void Populate()
    {
        AppearanceDatabase database = LoadDatabase();
        if (database == null)
            return;

        StringTableCollection collection = LocalizationEditorSettings.GetStringTableCollection(k_table);
        if (collection == null)
        {
            Debug.LogError($"[수염 어휘] {k_table} 문자열 테이블을 찾지 못했다");
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
                    Color = value.Meshes.Length == 0 ? Color.white : k_beardColor,
                    PropPrefabs = LoadMeshes(value.Meshes, missing),
                }
            );
        }

        if (missing.Count > 0)
        {
            Debug.LogError($"[수염 어휘] 찾지 못한 프리팹 {missing.Count}개 — 배선 중단\n  {string.Join("\n  ", missing)}");
            return;
        }

        // Options는 직렬화되는 public 필드라 그대로 갈아끼우면 된다. 새 값은 MontageLayer가 비어 있으니
        // 굽기 전까지 CanDepict가 그 값을 공개 축 후보에서 빼 준다 — 그림 없는 값이 공개되는 일은 없다.
        database.GetAxis(AppearanceAxis.FacialHair).Options = options.ToArray();
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

        Debug.Log($"[수염 어휘] 값 {options.Count}개 배선 완료 — 이제 Tools/몽타주 레이어 굽기로 레이어를 다시 구울 것{log}");
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
            Debug.LogError("[수염 어휘] AppearanceDatabase 에셋을 찾지 못했다");
            return null;
        }
        return AssetDatabase.LoadAssetAtPath<AppearanceDatabase>(AssetDatabase.GUIDToAssetPath(guids[0]));
    }
}
