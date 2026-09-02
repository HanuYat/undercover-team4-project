# 스캔 카드 미스캔 비주얼 교체 (#942) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 미스캔 대상의 스캔 카드가 완전 불투명 검정 배경 + 흰 `?`만 뜨는 상태를, 반투명 배경 + 얇은 카드 + 정적 노이즈 오버레이로 교체한다.

**Architecture:** `ScanInfoView.cs`의 마스킹 로직(`SetMasked`)은 그대로 두고, `ScanInfoCard.prefab`의 색상/크기/계층 구조와 신규 노이즈 텍스처 에셋만 바꾼다. `m_maskedRoot`가 가리키는 대상을 기존 `MaskedText` 단독에서 `MaskedText` + `NoiseOverlay`를 담는 새 컨테이너 `MaskedRoot`로 바꿔치기해, 기존 `SetActive(masked)` 토글 코드가 노이즈까지 자동으로 켜고 끄게 만든다.

**Tech Stack:** Unity 6000.3.15f1 Editor, UGUI(Image/TextMeshProUGUI), Unity MCP(`execute_code`)로 Editor C# 스크립트 실행.

## Global Constraints

- 테스트 프레임워크 코드가 없는 프로젝트 — 검증은 Unity Console 로그 확인 + 프리팹 재로드 후 값 대조로 대체한다.
- `.cs` 파일을 수정하면 Unity가 컴파일한다 — 새 코드를 쓰기 전 Unity Console에 컴파일 에러가 없는지 반드시 확인한다(`read_console`).
- 프리팹의 `[SerializeField]` 기본값은 C# 코드만 바꿔선 기존 인스턴스에 반영되지 않는다 — 프리팹 파일에 `SerializedObject`로 직접 써야 한다.
- 커밋 메시지에 이슈 번호 `#942`를 포함한다. 브랜치는 이미 `feature/942-scan-card-ui`로 만들어져 있다.
- 이번 작업은 `ScanInfoView.cs`의 마스킹 판정 로직(`ShowMasked`/`SetMasked` 호출부, `ScanResultPresenter.cs`)은 건드리지 않는다 — 시각 요소만 바꾼다.

---

### Task 1: 노이즈 텍스처 에셋 생성

**Files:**
- Create: `Assets/Textures/UI/ScanCardNoise.png`

**Interfaces:**
- Produces: `Assets/Textures/UI/ScanCardNoise.png` — Sprite로 임포트된 128×128 그레이스케일+알파 랜덤 노이즈 텍스처. Task 3에서 `Image.sprite`로 참조한다.

- [ ] **Step 1: Unity MCP `execute_code`로 노이즈 텍스처를 생성하고 PNG로 저장**

`mcp__unityMCP__execute_code`에 아래 C#을 전달해 실행한다(Editor 컨텍스트, `using System.IO; using UnityEngine; using UnityEditor;` 포함):

```csharp
int size = 128;
var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
var rng = new System.Random(20260901);
var pixels = new Color32[size * size];
for (int i = 0; i < pixels.Length; i++)
{
    byte gray = (byte)rng.Next(0, 256);
    byte alpha = (byte)rng.Next(40, 200);
    pixels[i] = new Color32(gray, gray, gray, alpha);
}
tex.SetPixels32(pixels);
tex.Apply();

byte[] png = tex.EncodeToPNG();
string dir = Path.Combine(Application.dataPath, "Textures/UI");
Directory.CreateDirectory(dir);
string fullPath = Path.Combine(dir, "ScanCardNoise.png");
File.WriteAllBytes(fullPath, png);
Object.DestroyImmediate(tex);

AssetDatabase.ImportAsset("Assets/Textures/UI/ScanCardNoise.png");
Debug.Log("ScanCardNoise.png written: " + File.Exists(fullPath));
```

- [ ] **Step 2: 콘솔에서 결과 확인**

`mcp__unityMCP__read_console` 호출. Expected: `"ScanCardNoise.png written: True"` 로그가 보이고 에러 없음.

- [ ] **Step 3: Sprite 임포트 설정 적용**

```csharp
var importer = (TextureImporter)AssetImporter.GetAtPath("Assets/Textures/UI/ScanCardNoise.png");
importer.textureType = TextureImporterType.Sprite;
importer.spriteImportMode = SpriteImportMode.Single;
importer.filterMode = FilterMode.Bilinear;
importer.wrapMode = TextureWrapMode.Clamp;
importer.alphaIsTransparency = true;
importer.SaveAndReimport();

var sprite = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/Textures/UI/ScanCardNoise.png");
Debug.Log("Sprite loaded: " + (sprite != null) + ", textureType=" + importer.textureType + ", filterMode=" + importer.filterMode + ", wrapMode=" + importer.wrapMode);
```

- [ ] **Step 4: 콘솔 재확인**

`mcp__unityMCP__read_console` 호출. Expected: `"Sprite loaded: True, textureType=Sprite, filterMode=Bilinear, wrapMode=Clamp"`, 에러 없음.

- [ ] **Step 5: 커밋**

```bash
git add Assets/Textures/UI/ScanCardNoise.png Assets/Textures/UI/ScanCardNoise.png.meta
git commit -m "미스캔 스캔 카드용 정적 노이즈 텍스처 추가 (#942)"
```

---

### Task 2: 스캔 카드 마스킹 배경/텍스트 값 조정

**Files:**
- Modify: `Assets/Scripts/UI/Scan/ScanInfoView.cs:17` (Tooltip 문구), `Assets/Scripts/UI/Scan/ScanInfoView.cs:82` (XML 문서 주석)
- Modify: `Assets/Prefabs/NPC/ScanInfoCard.prefab`

**Interfaces:**
- Consumes: 없음 (기존 `ScanInfoView` 필드 `m_maskedColor`, 루트 `RectTransform`, `Background/MaskedText`의 `TMP_Text` 그대로 사용)
- Produces: 변경된 `m_maskedColor` 값, 카드 `RectTransform.sizeDelta`, `MaskedText`의 폰트 크기/색 — Task 3에서 그대로 이어받아 작업(구조는 안 건드림).

- [ ] **Step 1: 스테일 코멘트 수정 (`?`가 더는 "까맣게"가 아니므로)**

`Assets/Scripts/UI/Scan/ScanInfoView.cs:17`을 다음으로 교체:

```csharp
    [Tooltip("카드 배경 — 마스킹 여부에 따라 색이 바뀐다(미스캔 시 반투명 노이즈로 마스킹)")]
```

`Assets/Scripts/UI/Scan/ScanInfoView.cs:82`를 다음으로 교체:

```csharp
    /// <summary>미스캔 NPC — 카드를 반투명 노이즈로 마스킹하고 물음표만 띄운다(이름·아이콘은 통째로 숨김).</summary>
```

- [ ] **Step 2: 컴파일 확인**

`mcp__unityMCP__read_console` 호출(또는 `editor_state.isCompiling` 폴링). Expected: 컴파일 에러 없음.

- [ ] **Step 3: `execute_code`로 프리팹 값 수정**

```csharp
string prefabPath = "Assets/Prefabs/NPC/ScanInfoCard.prefab";
GameObject root = PrefabUtility.LoadPrefabContents(prefabPath);
try
{
    var scanView = root.GetComponent<ScanInfoView>();
    var so = new SerializedObject(scanView);
    so.FindProperty("m_maskedColor").colorValue = new Color(0.08f, 0.09f, 0.11f, 0.55f);
    so.ApplyModifiedProperties();

    var cardRect = root.GetComponent<RectTransform>();
    cardRect.sizeDelta = new Vector2(280f, 68f);

    var maskedText = root.transform.Find("Background/MaskedText").GetComponent<TMPro.TMP_Text>();
    maskedText.fontSize = 64f;
    maskedText.fontSizeBase = 64f;
    maskedText.color = new Color(1f, 1f, 1f, 0.75f);

    PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
    Debug.Log("Task2 saved. maskedColor=" + scanView.GetType().GetField("m_maskedColor", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(scanView));
}
finally
{
    PrefabUtility.UnloadPrefabContents(root);
}
```

- [ ] **Step 4: 저장 확인을 위해 프리팹을 다시 읽어 값 대조**

```csharp
GameObject verifyRoot = PrefabUtility.LoadPrefabContents("Assets/Prefabs/NPC/ScanInfoCard.prefab");
try
{
    var scanView = verifyRoot.GetComponent<ScanInfoView>();
    var maskedColorField = scanView.GetType().GetField("m_maskedColor", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
    Color maskedColor = (Color)maskedColorField.GetValue(scanView);
    Vector2 size = verifyRoot.GetComponent<RectTransform>().sizeDelta;
    var maskedText = verifyRoot.transform.Find("Background/MaskedText").GetComponent<TMPro.TMP_Text>();

    Debug.Log($"verify maskedColor={maskedColor}, size={size}, fontSize={maskedText.fontSize}, textAlpha={maskedText.color.a}");
}
finally
{
    PrefabUtility.UnloadPrefabContents(verifyRoot);
}
```

- [ ] **Step 5: 콘솔에서 값 확인**

`mcp__unityMCP__read_console` 호출. Expected: `verify maskedColor=RGBA(0.080, 0.090, 0.110, 0.550), size=(280.0, 68.0), fontSize=64, textAlpha=0.75` (부동소수점 표시 형식은 Unity 로그 포맷에 따라 다를 수 있음 — 값이 근사치로 일치하는지만 확인). 저장이 조용히 실패해 옛날 값이 나오면(알려진 MCP 함정) Step 3을 재시도한다.

- [ ] **Step 6: 커밋**

```bash
git add Assets/Scripts/UI/Scan/ScanInfoView.cs Assets/Prefabs/NPC/ScanInfoCard.prefab
git commit -m "스캔 카드 미스캔 배경을 반투명으로, 카드를 얇게 조정 (#942)"
```

---

### Task 3: 마스킹 컨테이너 재구성 + 노이즈 오버레이 추가

**Files:**
- Modify: `Assets/Prefabs/NPC/ScanInfoCard.prefab`

**Interfaces:**
- Consumes: `Assets/Textures/UI/ScanCardNoise.png`의 Sprite (Task 1 산출물)
- Produces: 새 GameObject `Background/MaskedRoot`(자식: `NoiseOverlay`[Image] → `MaskedText`[TMP], 순서대로) — `ScanInfoView.m_maskedRoot`가 이 `MaskedRoot`를 가리키도록 재배선됨. 이후 코드는 그대로 `SetActive(masked)`만 호출하면 됨(추가 배선 불필요).

- [ ] **Step 1: `execute_code`로 계층 재구성 + 노이즈 오버레이 추가**

```csharp
string prefabPath = "Assets/Prefabs/NPC/ScanInfoCard.prefab";
GameObject root = PrefabUtility.LoadPrefabContents(prefabPath);
try
{
    var scanView = root.GetComponent<ScanInfoView>();
    Transform background = root.transform.Find("Background");
    Transform maskedText = background.Find("MaskedText");

    var maskedRootGO = new GameObject("MaskedRoot", typeof(RectTransform));
    var maskedRootRect = (RectTransform)maskedRootGO.transform;
    maskedRootRect.SetParent(background, false);
    maskedRootRect.anchorMin = Vector2.zero;
    maskedRootRect.anchorMax = Vector2.one;
    maskedRootRect.offsetMin = Vector2.zero;
    maskedRootRect.offsetMax = Vector2.zero;
    maskedRootRect.pivot = new Vector2(0.5f, 0.5f);

    maskedText.SetParent(maskedRootRect, false);

    var noiseGO = new GameObject("NoiseOverlay", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
    var noiseRect = (RectTransform)noiseGO.transform;
    noiseRect.SetParent(maskedRootRect, false);
    noiseRect.anchorMin = Vector2.zero;
    noiseRect.anchorMax = Vector2.one;
    noiseRect.offsetMin = Vector2.zero;
    noiseRect.offsetMax = Vector2.zero;

    noiseRect.SetSiblingIndex(0);
    maskedText.SetSiblingIndex(1);

    var noiseImage = noiseGO.GetComponent<Image>();
    noiseImage.sprite = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/Textures/UI/ScanCardNoise.png");
    noiseImage.color = new Color(1f, 1f, 1f, 0.18f);
    noiseImage.raycastTarget = false;
    noiseImage.type = Image.Type.Simple;

    var so = new SerializedObject(scanView);
    so.FindProperty("m_maskedRoot").objectReferenceValue = maskedRootGO;
    so.ApplyModifiedProperties();

    PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
    Debug.Log("Task3 saved.");
}
finally
{
    PrefabUtility.UnloadPrefabContents(root);
}
```

- [ ] **Step 2: 콘솔 확인**

`mcp__unityMCP__read_console` 호출. Expected: `"Task3 saved."` 로그, 에러 없음(특히 `MissingComponentException`, `NullReferenceException` 없어야 함).

- [ ] **Step 3: 저장 결과를 다시 읽어 구조 검증**

```csharp
GameObject verifyRoot = PrefabUtility.LoadPrefabContents("Assets/Prefabs/NPC/ScanInfoCard.prefab");
try
{
    var scanView = verifyRoot.GetComponent<ScanInfoView>();
    var maskedRootField = scanView.GetType().GetField("m_maskedRoot", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
    var maskedRootObj = (GameObject)maskedRootField.GetValue(scanView);

    Transform background = verifyRoot.transform.Find("Background");
    Transform maskedRoot = background.Find("MaskedRoot");
    Transform noiseOverlay = maskedRoot != null ? maskedRoot.Find("NoiseOverlay") : null;
    Transform maskedText = maskedRoot != null ? maskedRoot.Find("MaskedText") : null;
    var noiseImage = noiseOverlay != null ? noiseOverlay.GetComponent<Image>() : null;

    Debug.Log($"verify: maskedRootField=={(maskedRoot != null ? maskedRoot.gameObject : null) == maskedRootObj}, " +
              $"backgroundChildren=[{string.Join(",", System.Linq.Enumerable.Range(0, background.childCount).Select(i => background.GetChild(i).name))}], " +
              $"maskedRootChildOrder=[{(maskedRoot != null ? string.Join(",", System.Linq.Enumerable.Range(0, maskedRoot.childCount).Select(i => maskedRoot.GetChild(i).name)) : "NULL")}], " +
              $"noiseSprite={(noiseImage != null ? noiseImage.sprite?.name : "NULL")}, noiseAlpha={(noiseImage != null ? noiseImage.color.a : -1f)}, " +
              $"maskedTextStillHasQuestionMark={(maskedText != null && maskedText.GetComponent<TMPro.TMP_Text>().text == "?")}");
}
finally
{
    PrefabUtility.UnloadPrefabContents(verifyRoot);
}
```

- [ ] **Step 4: 콘솔에서 검증 결과 확인**

`mcp__unityMCP__read_console` 호출. Expected: `maskedRootField==True`, `backgroundChildren=[Content,MaskedRoot]`, `maskedRootChildOrder=[NoiseOverlay,MaskedText]`, `noiseSprite=ScanCardNoise`, `noiseAlpha=0.18`, `maskedTextStillHasQuestionMark=True`. 하나라도 어긋나면(특히 `MissingComponentException`이나 순서가 뒤바뀐 경우) Step 1을 다시 실행한다.

- [ ] **Step 5: 커밋**

```bash
git add Assets/Prefabs/NPC/ScanInfoCard.prefab
git commit -m "스캔 카드 미스캔 상태에 노이즈 오버레이 추가 — MaskedRoot 컨테이너로 재구성 (#942)"
```

---

### Task 4: 눈으로 확인 — 프리뷰 렌더 + 실제 플레이 확인 안내

**Files:**
- 없음 (파일 변경 없이 확인만)

**Interfaces:**
- Consumes: Task 1~3에서 완성된 `ScanInfoCard.prefab`
- Produces: 없음 (검증 산출물인 PNG는 커밋 대상 아님)

- [ ] **Step 1: 격리된 프리뷰 씬에서 마스킹 상태를 렌더링해 PNG로 저장**

현재 열려 있는 씬은 건드리지 않는다(플레이 모드 중이면 편집 내용이 증발하므로 `editor_state.isPlaying`을 먼저 확인하고, 플레이 모드면 이 스텝은 건너뛰고 Step 3으로).

```csharp
using UnityEditor.SceneManagement;

var previewScene = EditorSceneManager.NewPreviewScene();
var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/NPC/ScanInfoCard.prefab");
var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, previewScene);
instance.SetActive(true);
instance.GetComponent<ScanInfoView>().ShowMasked();

var cameraGO = new GameObject("PreviewCamera");
UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(cameraGO, previewScene);
var cam = cameraGO.AddComponent<Camera>();
cam.transform.position = new Vector3(0f, 0f, -2f);
cam.transform.LookAt(instance.transform.position);
cam.orthographic = true;
cam.orthographicSize = 0.35f;
cam.clearFlags = CameraClearFlags.SolidColor;
cam.backgroundColor = new Color(0.12f, 0.13f, 0.15f);

var rt = new RenderTexture(512, 256, 16);
cam.targetTexture = rt;
cam.Render();
RenderTexture.active = rt;
var tex = new Texture2D(512, 256, TextureFormat.RGB24, false);
tex.ReadPixels(new Rect(0, 0, 512, 256), 0, 0);
tex.Apply();
RenderTexture.active = null;

string outPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ScanCardPreview.png");
System.IO.File.WriteAllBytes(outPath, tex.EncodeToPNG());
Debug.Log("Preview PNG: " + outPath);

Object.DestroyImmediate(tex);
cam.targetTexture = null;
rt.Release();
EditorSceneManager.ClosePreviewScene(previewScene);
```

- [ ] **Step 2: 콘솔에서 출력 경로 확인 후 이미지를 Read 도구로 열어 육안 확인**

`mcp__unityMCP__read_console`로 `"Preview PNG: ..."` 경로를 확인한 뒤, 그 절대경로를 `Read` 도구로 열어 카드가 얇고 반투명 + 노이즈로 보이는지, `?`가 과하게 튀지 않는지 확인한다. (`Path.GetTempPath()`는 OS 임시 폴더라 Unity 프로젝트의 `Temp/`와 다르다 — `Temp/` 경로는 Read 도구가 거부하니 절대 그쪽에 쓰지 않는다.)

- [ ] **Step 3: 실제 플레이 확인은 사용자에게 안내**

프리뷰 렌더는 정적 스냅샷일 뿐이라 NPC 머리 위 실제 배치(빌보드 회전, 거리별 스케일, 스캔 완료 시 전환 애니메이션 없음 등)는 확인하지 못한다. 사용자에게 다음을 직접 플레이 모드에서 확인해달라고 요청한다:

1. 스캔 안 된 NPC 옆에서 카드가 얇고 반투명 노이즈로 보이는지.
2. 스캐너로 스캔한 순간 기존 `m_scannedColor` 배경 + 이름/아이콘으로 정상 전환되는지.
3. 카메라에 여러 스캔 카드가 동시에 잡혀도 버벅임이 없는지.

이 스텝은 코드/에셋 변경이 아니므로 커밋하지 않는다.

---

## Self-Review

**스펙 커버리지**
- §2 색상/크기 값 → Task 2에서 반영.
- §3 노이즈 오버레이 + `m_maskedRoot` 자동 토글 → Task 1(에셋) + Task 3(배선).
- §4 범위 밖(MontageDegrader 애니메이션, `ScanResultPresenter` 판정 로직, 몽타주 UI) → 어느 Task에서도 건드리지 않음, 명시적으로 Global Constraints에 재확인.
- §5 검증 항목 3개 → Task 4 Step 3에 그대로 반영.

**타입/이름 일관성**: `m_maskedRoot`, `m_maskedColor`, `m_background`는 전부 기존 `ScanInfoView.cs`에 있는 실제 필드명과 대조 완료(코드 재확인함). `MaskedRoot`/`NoiseOverlay`라는 새 GameObject 이름은 Task 3 전체에서 동일하게 사용.

**플레이스홀더 스캔**: "TODO"/"나중에"/구현 없는 지시문 없음 — 각 스텝이 실행 가능한 완전한 C#/bash 코드를 담고 있음.
