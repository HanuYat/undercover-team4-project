using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// [일회성 헬퍼] 시민 인명부(#223) 열람 UI를 현재 열린 씬에 구성한다.
/// 메뉴 "Tools/#223/Build Citizen Directory UI" 실행 → CitizenDirectoryCanvas(정렬 2 + 페이지 2 + 스크롤 목록)와
/// 책 오브젝트(CitizenDirectory)를 만들고 배선한다. 확인 후 씬을 저장하고, 이 스크립트는 삭제해도 된다.
/// (Unity MCP가 끊긴 상태에서 UI를 구성하기 위한 임시 빌더 — 정식 UI 확정 후 불필요)
/// </summary>
public static class DirectoryUiBuilder
{
    private const string k_rowPrefabPath = "Assets/Prefabs/HQ/DirectoryRow.prefab";

    [MenuItem("Tools/#223/Build Citizen Directory UI")]
    public static void Build()
    {
        if (GameObject.Find("CitizenDirectoryCanvas") != null)
        {
            Debug.LogWarning("DirectoryUiBuilder: CitizenDirectoryCanvas가 이미 있습니다 — 중복 생성 방지로 중단. 다시 만들려면 기존 것을 지우세요.");
            return;
        }

        GameObject rowAsset = AssetDatabase.LoadAssetAtPath<GameObject>(k_rowPrefabPath);
        DirectoryEntryView rowEv = rowAsset != null ? rowAsset.GetComponent<DirectoryEntryView>() : null;
        if (rowEv == null)
        {
            Debug.LogError($"DirectoryUiBuilder: 행 프리팹을 찾지 못했습니다 ({k_rowPrefabPath}). 먼저 DirectoryRow.prefab이 있어야 합니다.");
            return;
        }

        // ===== Canvas =====
        var canvasGo = new GameObject("CitizenDirectoryCanvas", typeof(RectTransform));
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 50;
        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        canvasGo.AddComponent<GraphicRaycaster>();
        var view = canvasGo.AddComponent<CitizenDirectoryView>();

        // ===== Panel (m_root) =====
        var panel = new GameObject("Panel", typeof(RectTransform), typeof(Image));
        panel.transform.SetParent(canvasGo.transform, false);
        var panelRt = panel.GetComponent<RectTransform>();
        panelRt.anchorMin = panelRt.anchorMax = panelRt.pivot = new Vector2(0.5f, 0.5f);
        panelRt.sizeDelta = new Vector2(760, 500);
        panelRt.anchoredPosition = Vector2.zero;
        panel.GetComponent<Image>().color = new Color(0.03f, 0.05f, 0.08f, 0.92f);

        // Title
        TMP_Text title = MakeText(panel.transform, "시민 인명부", 26, TextAlignmentOptions.TopLeft);
        var titleRt = title.rectTransform;
        titleRt.anchorMin = titleRt.anchorMax = titleRt.pivot = new Vector2(0, 1);
        titleRt.sizeDelta = new Vector2(400, 40);
        titleRt.anchoredPosition = new Vector2(28, -18);

        // Sort buttons (green, top-right)
        Color green = new Color(0.15f, 0.6f, 0.3f, 1f);
        Button factionBtn = MakeButton(panel.transform, "FactionSortButton", "세력순", green, new Vector2(120, 44), new Vector2(1, 1), new Vector2(-24, -16));
        Button nameBtn = MakeButton(panel.transform, "NameSortButton", "이름순", green, new Vector2(120, 44), new Vector2(1, 1), new Vector2(-156, -16));

        // Page buttons (red, bottom-right)
        Color red = new Color(0.75f, 0.2f, 0.2f, 1f);
        Button nextBtn = MakeButton(panel.transform, "NextButton", "다음", red, new Vector2(100, 44), new Vector2(1, 0), new Vector2(-24, 20));
        Button prevBtn = MakeButton(panel.transform, "PrevButton", "이전", red, new Vector2(100, 44), new Vector2(1, 0), new Vector2(-136, 20));

        // Page label (bottom-left)
        TMP_Text pageLabel = MakeText(panel.transform, "1 / 1", 20, TextAlignmentOptions.Left);
        var plRt = pageLabel.rectTransform;
        plRt.anchorMin = plRt.anchorMax = plRt.pivot = new Vector2(0, 0);
        plRt.sizeDelta = new Vector2(160, 32);
        plRt.anchoredPosition = new Vector2(28, 26);

        // ===== ScrollView =====
        var scroll = new GameObject("ScrollView", typeof(RectTransform), typeof(Image), typeof(ScrollRect));
        scroll.transform.SetParent(panel.transform, false);
        var scrollRt = scroll.GetComponent<RectTransform>();
        scrollRt.anchorMin = new Vector2(0, 0);
        scrollRt.anchorMax = new Vector2(1, 1);
        scrollRt.offsetMin = new Vector2(24, 72);
        scrollRt.offsetMax = new Vector2(-24, -72);
        scroll.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.25f);
        var scrollRect = scroll.GetComponent<ScrollRect>();
        scrollRect.horizontal = false;
        scrollRect.vertical = true;

        var viewport = new GameObject("Viewport", typeof(RectTransform), typeof(Image), typeof(RectMask2D));
        viewport.transform.SetParent(scroll.transform, false);
        var vpRt = viewport.GetComponent<RectTransform>();
        vpRt.anchorMin = Vector2.zero;
        vpRt.anchorMax = Vector2.one;
        vpRt.offsetMin = Vector2.zero;
        vpRt.offsetMax = Vector2.zero;
        viewport.GetComponent<Image>().color = new Color(1, 1, 1, 0.01f);

        var content = new GameObject("Content", typeof(RectTransform));
        content.transform.SetParent(viewport.transform, false);
        var contentRt = content.GetComponent<RectTransform>();
        contentRt.anchorMin = new Vector2(0, 1);
        contentRt.anchorMax = new Vector2(1, 1);
        contentRt.pivot = new Vector2(0.5f, 1);
        contentRt.sizeDelta = Vector2.zero;
        contentRt.anchoredPosition = Vector2.zero;
        var vlg = content.AddComponent<VerticalLayoutGroup>();
        vlg.spacing = 4;
        vlg.padding = new RectOffset(8, 8, 8, 8);
        vlg.childControlWidth = vlg.childControlHeight = true;
        vlg.childForceExpandWidth = true;
        vlg.childForceExpandHeight = false;
        var csf = content.AddComponent<ContentSizeFitter>();
        csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        scrollRect.viewport = vpRt;
        scrollRect.content = contentRt;

        // ===== wire CitizenDirectoryView =====
        var so = new SerializedObject(view);
        so.FindProperty("m_root").objectReferenceValue = panel;
        so.FindProperty("m_entryContainer").objectReferenceValue = contentRt;
        so.FindProperty("m_entryPrefab").objectReferenceValue = rowEv;
        so.FindProperty("m_sortNameButton").objectReferenceValue = nameBtn;
        so.FindProperty("m_sortFactionButton").objectReferenceValue = factionBtn;
        so.FindProperty("m_prevButton").objectReferenceValue = prevBtn;
        so.FindProperty("m_nextButton").objectReferenceValue = nextBtn;
        so.FindProperty("m_pageLabel").objectReferenceValue = pageLabel;
        so.ApplyModifiedPropertiesWithoutUndo();

        // ===== 책 오브젝트 (BombManual 옆) =====
        var book = new GameObject("CitizenDirectory");
        book.layer = 7; // 상호작용 레이어 (BombManual과 동일)
        GameObject bomb = GameObject.Find("BombManual");
        book.transform.position = bomb != null ? bomb.transform.position + new Vector3(1.5f, 0, 0) : new Vector3(0, 1, 0);
        var col = book.AddComponent<BoxCollider>();
        col.size = new Vector3(0.6f, 0.6f, 0.6f);
        var bookComp = book.AddComponent<CitizenDirectory>();
        var bso = new SerializedObject(bookComp);
        bso.FindProperty("m_view").objectReferenceValue = view;
        bso.ApplyModifiedPropertiesWithoutUndo();

        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        Selection.activeGameObject = canvasGo;
        Debug.Log($"DirectoryUiBuilder: 완료. CitizenDirectoryCanvas + 책(CitizenDirectory, pos={book.transform.position}) 생성·배선됨. 씬을 저장하세요.");
    }

    private static TMP_Text MakeText(Transform parent, string txt, int size, TextAlignmentOptions align)
    {
        var go = new GameObject("Text", typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var t = go.AddComponent<TextMeshProUGUI>();
        t.text = txt;
        t.fontSize = size;
        t.alignment = align;
        t.color = Color.white;
        t.enableWordWrapping = false;
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
        return t;
    }

    private static Button MakeButton(Transform parent, string name, string label, Color col, Vector2 size, Vector2 anchor, Vector2 pos)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = rt.pivot = anchor;
        rt.sizeDelta = size;
        rt.anchoredPosition = pos;
        go.GetComponent<Image>().color = col;
        var btn = go.AddComponent<Button>();
        MakeText(go.transform, label, 20, TextAlignmentOptions.Center);
        return btn;
    }
}
