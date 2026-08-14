using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Localization;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Tables;

/// <summary>
/// 모자 어휘 배선 (#619) — 메뉴: Tools/모자 어휘 배선
///
/// 모자 축을 아래 표대로 통째로 다시 쓴다. <see cref="HairVocabularyPopulator"/>와 같은 이유로 코드에 표를 둔다.
///
/// <b>값 묶음을 계산하지 않고 후보를 전부 별개 값으로 올렸다 (값 7 → 19).</b> 모자는 실루엣이 아니라
/// 실제 색으로 굽는 축이라(문서 §13-2) 형태가 닮아도 색이 갈린다 — 굽기 로그의 쌍별 측정에서
/// 24장 중 구분 문턱(12%) 미만인 쌍이 <b>0개</b>였고 최근접이 34칸(13.3%)이다. 수염(최근접 15칸)·
/// 안경(36칸)에서 받아들인 거리보다 넉넉하다.
/// 나중에 두 값이 너무 닮았다는 것이 눈에 띄면 <c>ExcludeFromMontage</c>만 켜면 되므로(묶음머리와 같은 수법)
/// 배열을 건드리지 않고 수습된다.
///
/// 값 [0]~[6]의 자리는 고정이다 — <c>AppearanceModelCatalog</c>의 <c>HeadwearIndex</c>가 이 번호를 가리킨다.
/// 새 값은 뒤에만 붙였다.
///
/// 이름은 128px 비교 시트로 메시를 눈으로 보고 붙였다(팩 이름이 번호뿐이라 그 길밖에 없다).
/// 이름은 이제 디버그 로그용이라 <b>이름 키를 바꿔도 인덱스는 안 움직인다.</b>
///
/// 배선 후에는 <c>Tools/몽타주 레이어 굽기</c>로 레이어를 다시 구워야 새 값의 그림이 생긴다.
/// </summary>
public static class HeadwearVocabularyPopulator
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

    // 순서가 곧 인덱스다. [0]~[6]은 기존 자리·기존 틴트 색 그대로다 — 카탈로그가 가리키는 뜻이 바뀌면 안 된다.
    //
    // 새 값은 틴트를 안 건다(흰색 = 메시 원색). 08-12 시트를 '실물 색으로' 구워 34칸을 확인한 것이 원색 기준이라,
    // 색을 새로 지어내면 그 측정이 무효가 된다.
    private static readonly Value[] s_values =
    {
        new Value("Npc.Appearance.None", "없음", "None", Color.white, false),

        new Value(
            "Npc.Appearance.Headwear.Beanie",
            "비니",
            "Beanie",
            new Color(0.2f, 0.2f, 0.25f, 1f),
            false,
            k_generic + "Beanie_01"
        ),
        new Value(
            "Npc.Appearance.Headwear.Cap",
            "캡",
            "Cap",
            new Color(0.7f, 0.15f, 0.15f, 1f),
            false,
            k_generic + "Hat_01"
        ),
        new Value(
            "Npc.Appearance.Headwear.Hat",
            "모자",
            "Hat",
            new Color(0.3f, 0.3f, 0.35f, 1f),
            false,
            k_generic + "Hat_02"
        ),
        // 후드는 어느 팩에도 부착물이 없다 — SciFi 13번 모델이 메시에 구워 쓰는 값이라 자리만 지킨다
        new Value(
            "Npc.Appearance.Headwear.Hood",
            "후드",
            "Hood",
            new Color(0.4f, 0.35f, 0.3f, 1f),
            true
        ),
        // PS 헤드셋 둘을 여기 묶었다 — 헤드셋이 안경 축(바이저)과 모자 축으로 갈려 있던 것을 이쪽으로 모은다.
        // 안경 축 '바이저'에 간 G_Headset_01은 눈을 덮는 판이라 그대로 둔다
        new Value(
            "Npc.Appearance.Headwear.Headphones",
            "헤드폰",
            "Headphones",
            new Color(0.15f, 0.15f, 0.15f, 1f),
            false,
            k_generic + "Headset_02",
            k_police + "Headset_01",
            k_police + "Headset_02"
        ),
        // SciFi 전용을 껐다 — 검은 헬멧에 투명 바이저뿐이라 경찰 표식이 없어 시민이 써도 아군과 안 섞인다.
        // SciFi 모델이 이 값을 쓰는 것은 그대로고, Generic 시민도 이제 뽑는다
        new Value(
            "Npc.Appearance.Headwear.Helmet",
            "헬멧",
            "Helmet",
            Color.white,
            false,
            k_apocalypse + "RiotCop_Male_Helmet_01"
        ),

        // ── 여기부터 새 값 (PoliceStation 5 + Apocalypse 7) ──
        //
        // 이름은 128px 비교 시트를 보고 붙였다. 머리 축과 같은 방식이다 — 값이 곧 이름은 아니고,
        // 그림으로 갈리는 값이 같은 이름을 나눠 쓴다([9]·[15] 챙 넓은 모자, [2]·[16] 캡).
        // 말은 갈래를 가리키고 후보를 좁히는 것은 그림이 한다.
        //
        // 경찰 표식이 있는 여섯(PS Hat_01·02 정모, Hat_03 순경 헬멧, Hat_04 POLICE 캡,
        // Helmet_01·04 POLICE 진압 헬멧)은 뺐다 — 플레이어가 로봇 경찰이라 시민이 그 표식을 달면
        // 화면에서 아군과 섞인다. 경찰로 읽히더라도 표식이 없는 것(베레모·전술 헬멧·챙 넓은 모자)은 남겼다.

        new Value("Npc.Appearance.Headwear.BrimHat", "챙모자", "Brim hat", Color.white, false, k_police + "Hat_05"),
        new Value("Npc.Appearance.Headwear.Beret", "베레모", "Beret", Color.white, false, k_police + "Hat_06"),
        new Value(
            "Npc.Appearance.Headwear.WideBrimHat",
            "챙 넓은 모자",
            "Wide-brim hat",
            Color.white,
            false,
            k_police + "Hat_07"
        ),

        new Value(
            "Npc.Appearance.Headwear.MotorcycleHelmet",
            "모터사이클 헬멧",
            "Motorcycle helmet",
            Color.white,
            false,
            k_police + "Helmet_02"
        ),
        new Value(
            "Npc.Appearance.Headwear.TacticalHelmet",
            "전술 헬멧",
            "Tactical helmet",
            Color.white,
            false,
            k_police + "Helmet_03"
        ),

        new Value(
            "Npc.Appearance.Headwear.CamoCap",
            "카모 캡",
            "Camo cap",
            Color.white,
            false,
            k_apocalypse + "Hunter_Male_Hat_01"
        ),
        new Value(
            "Npc.Appearance.Headwear.HuntingHat",
            "사냥 모자",
            "Hunting hat",
            Color.white,
            false,
            k_apocalypse + "Hunter_Male_Hat_02"
        ),
        new Value(
            "Npc.Appearance.Headwear.AviatorCap",
            "비행모",
            "Aviator cap",
            Color.white,
            false,
            k_apocalypse + "Scout_Female_Hat_01"
        ),
        new Value(
            "Npc.Appearance.Headwear.WideBrimHat",
            "챙 넓은 모자",
            "Wide-brim hat",
            Color.white,
            false,
            k_apocalypse + "Sheriff_Male_Hat_01"
        ),
        // 기존 [2]와 같은 이름을 쓴다 — 형태는 캡이고 색만 다르다
        new Value(
            "Npc.Appearance.Headwear.Cap",
            "캡",
            "Cap",
            Color.white,
            false,
            k_apocalypse + "Teen_Male_Hat_01"
        ),
        new Value(
            "Npc.Appearance.Headwear.FootballHelmet",
            "풋볼 헬멧",
            "Football helmet",
            Color.white,
            false,
            k_apocalypse + "FootballHelmet_01"
        ),
        new Value(
            "Npc.Appearance.Headwear.SoldierHelmet",
            "군용 헬멧",
            "Soldier helmet",
            Color.white,
            false,
            k_apocalypse + "Soldier_Male_Helmet_01"
        ),
    };

    // 한 번 팠다가 버린 이름 키 — 첫 배선에서 메시 번호를 그대로 옮겼던 자리표시(PS 모자 1 …)와,
    // 경찰 표식 때문에 뺀 값들의 이름이다. 지우지 않으면 안 쓰는 키가 NpcTable에 남는다.
    private static readonly string[] s_retiredKeys =
    {
        "Npc.Appearance.Headwear.PsHat1",
        "Npc.Appearance.Headwear.PsHat2",
        "Npc.Appearance.Headwear.PsHat3",
        "Npc.Appearance.Headwear.PsHat4",
        "Npc.Appearance.Headwear.PsHat5",
        "Npc.Appearance.Headwear.PsHat6",
        "Npc.Appearance.Headwear.PsHat7",
        "Npc.Appearance.Headwear.PsHelmet1",
        "Npc.Appearance.Headwear.PsHelmet2",
        "Npc.Appearance.Headwear.PsHelmet3",
        "Npc.Appearance.Headwear.PsHelmet4",
        "Npc.Appearance.Headwear.HunterHat1",
        "Npc.Appearance.Headwear.HunterHat2",
        "Npc.Appearance.Headwear.ScoutHat",
        "Npc.Appearance.Headwear.SheriffHat",
        "Npc.Appearance.Headwear.TeenHat",
        "Npc.Appearance.Headwear.PoliceCap",
        "Npc.Appearance.Headwear.CustodianHelmet",
        "Npc.Appearance.Headwear.PoliceLetterCap",
        "Npc.Appearance.Headwear.RiotHelmet",
    };

    [MenuItem("Tools/모자 어휘 배선")]
    private static void Populate()
    {
        AppearanceDatabase database = LoadDatabase();
        if (database == null)
            return;

        StringTableCollection collection = LocalizationEditorSettings.GetStringTableCollection(k_table);
        if (collection == null)
        {
            Debug.LogError($"[모자 어휘] {k_table} 문자열 테이블을 찾지 못했다");
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
            Debug.LogError($"[모자 어휘] 찾지 못한 프리팹 {missing.Count}개 — 배선 중단\n  {string.Join("\n  ", missing)}");
            return;
        }

        List<string> retired = RemoveRetiredKeys(collection);

        database.GetAxis(AppearanceAxis.Headwear).Options = options.ToArray();
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
        if (retired.Count > 0)
            log.Append("\n  걷어낸 이름 키: ").Append(string.Join(", ", retired));

        Debug.Log($"[모자 어휘] 값 {options.Count}개 배선 완료 — 이제 Tools/몽타주 레이어 굽기로 레이어를 다시 구울 것{log}");
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

    /// <summary>버린 이름 키를 테이블에서 지운다 — 이 표가 쓰는 키는 건드리지 않는다.</summary>
    private static List<string> RemoveRetiredKeys(StringTableCollection collection)
    {
        var used = new HashSet<string>();
        foreach (Value value in s_values)
            used.Add(value.Key);

        SharedTableData shared = collection.SharedData;
        var removed = new List<string>();

        foreach (string key in s_retiredKeys)
        {
            if (used.Contains(key) || shared.GetId(key) == SharedTableData.EmptyId)
                continue;

            foreach (StringTable table in collection.StringTables)
            {
                table.RemoveEntry(key);
                EditorUtility.SetDirty(table);
            }
            shared.RemoveKey(key);
            EditorUtility.SetDirty(shared);
            removed.Add(key);
        }
        return removed;
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
            Debug.LogError("[모자 어휘] AppearanceDatabase 에셋을 찾지 못했다");
            return null;
        }
        return AssetDatabase.LoadAssetAtPath<AppearanceDatabase>(AssetDatabase.GUIDToAssetPath(guids[0]));
    }
}
