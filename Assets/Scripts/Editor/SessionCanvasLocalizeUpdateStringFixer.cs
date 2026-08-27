using System.Collections.Generic;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.Localization.Components;

/// <summary>
/// 버튼 배선 재작업으로 새로 생긴 라벨(Text (TMP))은 LocalizeStringEvent 자체가
/// 새로 붙은 것이라 UpdateString 이벤트에 아무 퍼시스턴트 콜도 없었다. 키를 복원해도
/// (SessionCanvasLocalizationKeyFixer) 문자열을 TMP_Text로 넘겨줄 리스너가 없어 텍스트가
/// 갱신되지 않았던 것 — 이 스크립트가 UpdateString -> TMP_Text.set_text 배선을 되살린다.
/// 값 구성은 이미 정상 동작하는 다른 버튼 라벨(예: AuthGate/Window/Title)의 직렬화 형태를
/// 그대로 따른다 (m_Mode=0 EventDefined, m_CallState=1 EditorAndRuntime).
/// </summary>
public static class SessionCanvasLocalizeUpdateStringFixer
{
    private const string k_menu = "Tools/Localization/SessionCanvas UpdateString 배선 복원";
    private const string k_prefabPath = "Assets/Prefabs/UI/SessionCanvas.prefab";

    private static readonly string[] k_targets =
    {
        "AuthGate/Window/Contents/Buttons/SignUpBtn/Text (TMP)",
        "AuthGate/Window/Contents/Buttons/SignInBtn/Text (TMP)",
        "AuthGate/Window/Contents/Buttons/GuestLoginBtn/Text (TMP)",
        "SessionPanel/Buttons/QuitBtn/Text (TMP)",
        "SessionPanel/Buttons/SettingsBtn/Text (TMP)",
        "SessionPanel/Window/Contents/Buttons/JoinBtn/Text (TMP)",
        "SessionPanel/Window/Contents/Buttons/CreateBtn/Text (TMP)",
        "SessionPanel/Buttons/TutorialBtn/Text (TMP)",
        "SessionPanel/Buttons/SignOutBtn/Text (TMP)",
        "SessionPanel/Window/Contents/Nickname/NicknameRow/ApplyNicknameBtn/Text (TMP)",
        "SessionPanel/Window/Contents/Buttons/ContinueBtn/Text (TMP)",
    };

    [MenuItem(k_menu)]
    public static void Fix()
    {
        GameObject root = PrefabUtility.LoadPrefabContents(k_prefabPath);
        try
        {
            int fixedCount = 0;
            var missing = new List<string>();

            foreach (string path in k_targets)
            {
                Transform target = root.transform.Find(path);
                if (target == null)
                {
                    missing.Add($"경로를 찾을 수 없음: {path}");
                    continue;
                }

                var evt = target.GetComponent<LocalizeStringEvent>();
                var tmp = target.GetComponent<TMP_Text>();
                if (evt == null || tmp == null)
                {
                    missing.Add($"컴포넌트 없음(LocalizeStringEvent={evt != null}, TMP_Text={tmp != null}): {path}");
                    continue;
                }

                var so = new SerializedObject(evt);
                SerializedProperty calls = so.FindProperty("m_UpdateString.m_PersistentCalls.m_Calls");
                calls.arraySize = 1;
                SerializedProperty call = calls.GetArrayElementAtIndex(0);
                call.FindPropertyRelative("m_Target").objectReferenceValue = tmp;
                call.FindPropertyRelative("m_TargetAssemblyTypeName").stringValue = "TMPro.TMP_Text, Unity.TextMeshPro";
                call.FindPropertyRelative("m_MethodName").stringValue = "set_text";
                call.FindPropertyRelative("m_Mode").intValue = 0;
                call.FindPropertyRelative("m_CallState").intValue = 1;
                call.FindPropertyRelative("m_Arguments.m_ObjectArgument").objectReferenceValue = null;
                call.FindPropertyRelative("m_Arguments.m_ObjectArgumentAssemblyTypeName").stringValue = "";
                call.FindPropertyRelative("m_Arguments.m_IntArgument").intValue = 0;
                call.FindPropertyRelative("m_Arguments.m_FloatArgument").floatValue = 0f;
                call.FindPropertyRelative("m_Arguments.m_StringArgument").stringValue = "";
                call.FindPropertyRelative("m_Arguments.m_BoolArgument").boolValue = false;
                so.ApplyModifiedPropertiesWithoutUndo();

                fixedCount++;
            }

            if (fixedCount > 0)
                PrefabUtility.SaveAsPrefabAsset(root, k_prefabPath);

            Debug.Log($"[SessionCanvas UpdateString 복원] {fixedCount}/{k_targets.Length}개 복원됨");
            foreach (string m in missing)
                Debug.LogWarning($"[SessionCanvas UpdateString 복원] {m}");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }
}
