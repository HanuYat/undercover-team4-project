using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// CCTV 설치물의 조준을 루트에서 <c>Head</c>로 옮긴다 — 루트를 돌리면 몸통까지 돌아 마운트가
/// 벽에서 뜬다. 루트 회전 R을 두 자식에 나눠 넣으므로 보이는 결과는 그대로다.
/// 루트가 이미 identity면 건너뛴다.
/// </summary>
public static class CctvRigFixup
{
    private const string k_menu = "Tools/CCTV/루트 회전을 Head로 옮기기";
    private const float k_identityDot = 0.99999f;

    [MenuItem(k_menu)]
    public static void MoveRootRotationToHead()
    {
        CCTVNode[] nodes = Object.FindObjectsByType<CCTVNode>(FindObjectsSortMode.None);
        if (nodes.Length == 0)
        {
            Debug.LogError("[CCTV 리그] 씬에 CCTVNode가 없다");
            return;
        }

        int moved = 0;
        int skipped = 0;
        var problems = new List<string>();
        var report = new StringBuilder("[CCTV 리그] 루트 회전 이관\n");

        Undo.SetCurrentGroupName("CCTV 루트 회전을 Head로");
        int group = Undo.GetCurrentGroup();

        foreach (CCTVNode node in nodes)
        {
            Transform root = node.transform;
            Quaternion rotation = root.localRotation;

            if (Mathf.Abs(Quaternion.Dot(rotation, Quaternion.identity)) > k_identityDot)
            {
                skipped++;
                continue;
            }

            if (!TryFindParts(root, out Transform head, out Transform body))
            {
                problems.Add(node.name);
                continue;
            }

            Undo.RecordObject(root, k_menu);
            Undo.RecordObject(head, k_menu);
            if (body != null)
                Undo.RecordObject(body, k_menu);

            head.localRotation = rotation * head.localRotation;

            if (body != null)
            {
                // Body는 자식이라 위치까지 돌려야 제자리에 남는다
                body.localPosition = rotation * body.localPosition;
                body.localRotation = rotation * body.localRotation;
            }

            root.localRotation = Quaternion.identity;

            moved++;
            report.AppendLine($"  {node.name,-24} Head ← {rotation.eulerAngles}");
        }

        Undo.CollapseUndoOperations(group);

        report.AppendLine($"  → 이관 {moved}건, 건너뜀 {skipped}건. 씬을 저장할 것");
        Debug.Log(report.ToString());

        if (problems.Count > 0)
            Debug.LogWarning("[CCTV 리그] 카메라를 품은 자식을 못 찾아 건너뜀: " + string.Join(", ", problems));
    }

    // 이름이 아니라 구조로 찾는다 — 카메라를 품은 자식이 Head, 나머지가 Body다
    private static bool TryFindParts(Transform root, out Transform head, out Transform body)
    {
        head = null;
        body = null;

        for (int i = 0; i < root.childCount; i++)
        {
            Transform child = root.GetChild(i);
            if (head == null && child.GetComponentInChildren<Camera>(true) != null)
                head = child;
            else if (body == null)
                body = child;
        }

        return head != null;
    }
}
