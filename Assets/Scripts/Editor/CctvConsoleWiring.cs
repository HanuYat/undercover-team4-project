using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.Localization;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Localization.Tables;
using UnityEngine.SceneManagement;

/// <summary>
/// 열린 맵 씬의 CCTV를 본부 콘솔에 배선한다 — 채널 배열과 설치 위치 문구를 한 번에 채운다.
///
/// 카메라가 늘어날 때마다 배열을 손으로 채우고 로케일 키를 하나씩 고르는 일을 없애려고 둔다.
/// 채널 번호는 <b>Hierarchy 순서</b>다 — 배치한 사람이 정한 순서를 툴이 바꾸지 않는다.
/// 번호를 바꾸려면 Hierarchy에서 카메라를 끌어 옮기고 다시 실행하면 된다.
///
/// 문구는 규약 키다 — <c>Cam_Market_Inside</c> → <c>World.Cctv.MarketInside</c>
/// (접두 <c>Cam_</c>를 떼고 <c>_</c>를 지운다). 테이블에 없는 키는 비워 두고 경고만 남긴다.
/// 라벨이 비면 모니터가 "CH 12"로 폴백하므로 배선 자체는 성립한다.
/// </summary>
public static class CctvConsoleWiring
{
    private const string k_menu = "Tools/CCTV/콘솔에 카메라 배선";
    private const string k_table = "WorldTable";
    private const string k_keyPrefix = "World.Cctv.";
    private const string k_namePrefix = "Cam_";

    [MenuItem(k_menu)]
    public static void Wire()
    {
        CCTVSwitcher switcher = Object.FindFirstObjectByType<CCTVSwitcher>();
        if (switcher == null)
        {
            Debug.LogError("[CCTV 배선] 씬에 CCTVSwitcher가 없다 — 맵 씬(HQ가 있는)을 열고 실행할 것");
            return;
        }

        List<CCTVNode> nodes = CollectInHierarchyOrder(switcher.gameObject.scene);
        if (nodes.Count == 0)
        {
            Debug.LogError("[CCTV 배선] 씬에 CCTVNode가 없다");
            return;
        }

        StringTableCollection collection = LocalizationEditorSettings.GetStringTableCollection(k_table);
        if (collection == null)
            Debug.LogWarning($"[CCTV 배선] 테이블 '{k_table}'을 찾을 수 없다 — 문구 없이 배열만 채운다");

        int labelled = 0;
        var report = new StringBuilder("[CCTV 배선] 채널 배치\n");

        for (int i = 0; i < nodes.Count; i++)
        {
            string key = KeyFor(nodes[i].name);
            SharedTableData.SharedTableEntry shared = collection != null
                ? collection.SharedData.GetEntry(key)
                : null;

            if (shared != null)
            {
                SerializedObject node = new SerializedObject(nodes[i]);
                node.FindProperty("m_locationLabel")
                    .FindPropertyRelative("m_TableEntryReference")
                    .FindPropertyRelative("m_KeyId")
                    .longValue = shared.Id;
                node.ApplyModifiedProperties();
                labelled++;
            }

            report.AppendLine(
                $"  CH{i + 1,-3} {nodes[i].name,-24} {(shared != null ? key : $"(키 없음: {key})")}");
        }

        SerializedObject console = new SerializedObject(switcher);
        SerializedProperty array = console.FindProperty("m_installations");
        array.arraySize = nodes.Count;
        for (int i = 0; i < nodes.Count; i++)
            array.GetArrayElementAtIndex(i).objectReferenceValue = nodes[i];
        console.ApplyModifiedProperties();

        EditorSceneManager.MarkSceneDirty(switcher.gameObject.scene);

        report.AppendLine($"  → 카메라 {nodes.Count}대, 문구 {labelled}건 배선. 씬을 저장할 것");
        Debug.Log(report.ToString(), switcher);
    }

    // Hierarchy에 보이는 순서 그대로 모은다 — 배치한 사람이 정한 순서가 채널 번호다.
    // FindObjectsByType은 순서를 보장하지 않으므로 직접 훑는다.
    private static List<CCTVNode> CollectInHierarchyOrder(Scene scene)
    {
        var found = new List<CCTVNode>();
        foreach (GameObject root in scene.GetRootGameObjects())
            Collect(root.transform, found);

        return found;
    }

    private static void Collect(Transform node, List<CCTVNode> into)
    {
        if (node.TryGetComponent(out CCTVNode cctv))
            into.Add(cctv);

        for (int i = 0; i < node.childCount; i++)
            Collect(node.GetChild(i), into);
    }

    // Cam_Market_Inside -> World.Cctv.MarketInside
    private static string KeyFor(string objectName)
    {
        string body = objectName.StartsWith(k_namePrefix)
            ? objectName.Substring(k_namePrefix.Length)
            : objectName;

        return k_keyPrefix + body.Replace("_", string.Empty);
    }
}
