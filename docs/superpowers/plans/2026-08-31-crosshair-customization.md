# 크로스헤어 커스터마이징 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 크로스헤어 모양(십자/점/십자+점/원)·색·크기·굵기를 플레이어가 직접 고르고, 계정 클라우드에 저장해 다른 PC에서 로그인해도 그대로 따라오게 한다.

**Architecture:** 값 흐름은 로봇 색과 완전히 같은 다섯 자리를 그대로 미러링한다 — `CosmeticLoadout`(로컬 캐시+이벤트) → `CosmeticsSaveService`/`CosmeticsSaveData`(클라우드 정본, 버전 마이그레이션) → 소비자 둘: 실제 게임 크로스헤어(`CrosshairUI`, HUD)와 설정 패널의 실시간 미리보기(`CrosshairPreviewView`, 신규 경량 뷰). 두 소비자가 같은 그리기 규칙을 쓰도록 순수 정적 유틸리티 `CrosshairRenderer`로 로직을 한 곳에 모은다. 렌더링은 스프라이트 하나가 아니라 선분(위/아래/좌/우) + 점 + 원(고정 두께 링 스프라이트) 조합으로 바꿔 굵기·크기 슬라이더가 실제로 반영되게 한다.

**Tech Stack:** Unity 6000.3.15f1, C# (UGUI, TextMeshPro), UniTask(비동기 없음 — 이번 기능엔 불필요), Unity Cloud Save(`Unity.Services.CloudSave`), Unity MCP(에디터 자동화).

## Global Constraints

- 테스트 프레임워크 코드가 없는 프로젝트다(CLAUDE.md) — 모든 "테스트" 스텝은 컴파일 확인 + Unity Editor 수동 검증으로 대체한다.
- 네이밍: `m_`(private 인스턴스) · `s_`(static) · `k_`(const) · PascalCase(public) — CLAUDE.md/GDD 10-5.
- 색·모양은 GDD 568행의 안전 신호(동료 조준 시 명중색 전환)를 절대 가리면 안 된다 — 커스텀 색은 "기본(중립)" 상태에만 적용하고, 상호작용/무기 조준 색은 고정값을 유지한다(설계 문서 §4).
- 스크립트 수정 후 Unity Console에 컴파일 에러가 없는지 반드시 확인한다(Unity MCP `refresh_unity` + `read_console`).
- 씬/프리팹 편집은 Unity MCP `execute_code`로 한다 — 편집 전후 `git diff --stat`으로 의도치 않은 변경(다른 오브젝트의 무관한 필드 변화 등)이 없는지 확인한다.
- 설계 문서: `docs/superpowers/specs/2026-08-31-crosshair-customization-design.md`

---

## Task 1: 데이터 모델 — `ECrosshairShape`, `CrosshairSettings`

**Files:**
- Create: `Assets/Scripts/UI/Hud/ECrosshairShape.cs`
- Create: `Assets/Scripts/Save/CrosshairSettings.cs`

**Interfaces:**
- Produces: `enum ECrosshairShape { Cross, Dot, CrossDot, Circle }` / `class CrosshairSettings { public ECrosshairShape Shape; public int ColorIndex; public float Size; public float Thickness; public static CrosshairSettings Default() }` — 이후 모든 태스크가 이 두 타입을 가져다 쓴다.

- [ ] **Step 1: `ECrosshairShape` 작성**

```csharp
// Assets/Scripts/UI/Hud/ECrosshairShape.cs

/// <summary>크로스헤어 모양 프리셋 (#945). 값 추가는 끝에만 — 저장된 정수와 순서가 맞아야 한다.</summary>
public enum ECrosshairShape
{
    Cross,
    Dot,
    CrossDot,
    Circle,
}
```

- [ ] **Step 2: `CrosshairSettings` 작성**

```csharp
// Assets/Scripts/Save/CrosshairSettings.cs
using System;

/// <summary>
/// 크로스헤어 커스터마이징 한 벌 (#945) — <see cref="CosmeticLoadout"/>·<see cref="CosmeticsSaveData"/>·
/// <see cref="CrosshairRenderer"/>가 이 값 하나로 통신한다. 로봇 색과 같은 계정 클라우드 경로를 탄다.
/// </summary>
[Serializable]
public class CrosshairSettings
{
    public ECrosshairShape Shape;

    /// <summary>색 팔레트(<see cref="PlayerColorPalette"/> 재사용) 인덱스 — 로봇 색과 같은 인덱스 방식.</summary>
    public int ColorIndex;

    /// <summary>선 길이 / 원 지름 기준(px). 슬라이더 범위는 <see cref="CrosshairRenderer"/>가 정한다.</summary>
    public float Size;

    /// <summary>선 굵기(px). 원 모양에는 적용되지 않는다(§4 참고 — 고정 두께 링 스프라이트 사용).</summary>
    public float Thickness;

    public static CrosshairSettings Default() => new CrosshairSettings
    {
        Shape = ECrosshairShape.Cross,
        ColorIndex = 0,
        Size = 8f,
        Thickness = 2f,
    };
}
```

- [ ] **Step 3: 컴파일 확인**

Unity MCP로 확인:
```csharp
// execute_code
System.Type t1 = System.Type.GetType("ECrosshairShape, Assembly-CSharp");
System.Type t2 = System.Type.GetType("CrosshairSettings, Assembly-CSharp");
return "shape=" + (t1 != null) + " settings=" + (t2 != null);
```
Expected: `shape=True settings=True`, 콘솔에 `error CS` 없음.

- [ ] **Step 4: 커밋**

```bash
git add "Assets/Scripts/UI/Hud/ECrosshairShape.cs" "Assets/Scripts/UI/Hud/ECrosshairShape.cs.meta" "Assets/Scripts/Save/CrosshairSettings.cs" "Assets/Scripts/Save/CrosshairSettings.cs.meta"
git commit -m "크로스헤어 설정 데이터 모델을 추가한다 (#945)"
```
(.meta 파일은 Unity가 스크립트 저장 시 자동 생성한다 — 커밋 전 `git status`로 존재를 확인할 것.)

---

## Task 2: `CosmeticLoadout` — 로컬 캐시 Get/Set + 이벤트

**Files:**
- Modify: `Assets/Scripts/Save/CosmeticLoadout.cs`

**Interfaces:**
- Consumes: `CrosshairSettings`(Task 1), `CrosshairSettings.Default()`
- Produces: `CosmeticLoadout.GetCrosshairSettings(): CrosshairSettings`, `CosmeticLoadout.SetCrosshairSettings(CrosshairSettings settings): void`, `CosmeticLoadout.ApplyCrosshairSettings(CrosshairSettings settings): void`, `event Action OnCrosshairSettingsChanged` — Task 3·5·7이 이 넷을 쓴다.

기존 색·치장과 같은 자리 규칙(`k_playerColorKeyPrefix` 등)을 그대로 반복한다 — 필드 하나당 키 하나(JSON 블록 대신), 이 파일의 기존 스타일과 맞춘다.

- [ ] **Step 1: 필드·키·이벤트 추가**

`Assets/Scripts/Save/CosmeticLoadout.cs`의 기존 키 상수 옆(21~23행 부근)에 추가:

```csharp
    private const string k_crosshairKeyPrefix = "settings.crosshair.";
```

`s_accessories` 필드(36~38행) 아래에 추가:

```csharp
    private static CrosshairSettings s_crosshair = CrosshairSettings.Default();
```

`OnAccessoryChanged` 이벤트(46행) 아래에 추가:

```csharp
    /// <summary>내 크로스헤어 설정이 바뀌었다 — 게임 크로스헤어·설정 패널 미리보기가 되읽는다. (#945)</summary>
    public static event Action OnCrosshairSettingsChanged;
```

- [ ] **Step 2: Get/Set 추가**

`SetAccessory` 메서드(69~78행) 아래에 추가:

```csharp
    /// <summary>현재 크로스헤어 설정 — 값 자체를 그대로 돌려준다(참조 공유 주의: 호출부는 읽기 전용으로 쓸 것).</summary>
    public static CrosshairSettings GetCrosshairSettings() => s_crosshair;

    public static void SetCrosshairSettings(CrosshairSettings settings)
    {
        if (settings == null)
            return;

        s_crosshair = settings;
        SaveCrosshairToPrefs(settings);
        OnCrosshairSettingsChanged?.Invoke();
    }
```

- [ ] **Step 3: 클라우드 적용 메서드 추가**

`ApplyAccessories` 메서드(100~116행) 아래에 추가:

```csharp
    /// <summary>클라우드에서 받은 값을 적용한다 — 캐시에도 남긴다. null이면 기본값을 그대로 둔다(옛 레코드). (CosmeticsSaveService, #945)</summary>
    public static void ApplyCrosshairSettings(CrosshairSettings settings)
    {
        if (settings == null)
            return;

        s_crosshair = settings;
        SaveCrosshairToPrefs(settings);
        OnCrosshairSettingsChanged?.Invoke();
    }
```

- [ ] **Step 4: 계정 전환·부팅 로드에 연결**

`LoadAccount()` 메서드(142~146행)를 다음으로 교체:

```csharp
    private static void LoadAccount()
    {
        LoadPlayerColors();
        LoadAccessories();
        LoadCrosshairSettings();
    }
```

`AccessoryKey` 메서드(153~154행) 아래에 키 헬퍼 3개 추가:

```csharp
    private static string CrosshairShapeKey() => k_crosshairKeyPrefix + s_account + ".shape";
    private static string CrosshairColorKey() => k_crosshairKeyPrefix + s_account + ".color";
    private static string CrosshairSizeKey() => k_crosshairKeyPrefix + s_account + ".size";
    private static string CrosshairThicknessKey() => k_crosshairKeyPrefix + s_account + ".thickness";
```

파일 맨 아래(`LoadAccessories` 메서드 뒤, 175행 `}` 앞)에 추가:

```csharp
    private static void SaveCrosshairToPrefs(CrosshairSettings settings)
    {
        PlayerPrefs.SetInt(CrosshairShapeKey(), (int)settings.Shape);
        PlayerPrefs.SetInt(CrosshairColorKey(), settings.ColorIndex);
        PlayerPrefs.SetFloat(CrosshairSizeKey(), settings.Size);
        PlayerPrefs.SetFloat(CrosshairThicknessKey(), settings.Thickness);
    }

    // 캐시에서 다시 읽는다. 저장된 적이 없으면 기본값이다.
    private static void LoadCrosshairSettings()
    {
        CrosshairSettings fallback = CrosshairSettings.Default();
        s_crosshair = new CrosshairSettings
        {
            Shape = (ECrosshairShape)PlayerPrefs.GetInt(CrosshairShapeKey(), (int)fallback.Shape),
            ColorIndex = PlayerPrefs.GetInt(CrosshairColorKey(), fallback.ColorIndex),
            Size = PlayerPrefs.GetFloat(CrosshairSizeKey(), fallback.Size),
            Thickness = PlayerPrefs.GetFloat(CrosshairThicknessKey(), fallback.Thickness),
        };
        OnCrosshairSettingsChanged?.Invoke();
    }
```

- [ ] **Step 5: `Load()`의 정적 이벤트 리셋에 추가**

`Load()` 메서드(129~140행)의 이벤트 리셋 줄에 한 줄 추가:

```csharp
        OnPlayerColorChanged = null;
        OnAccessoryChanged = null;
        OnCrosshairSettingsChanged = null;
```

- [ ] **Step 6: 컴파일 확인**

```csharp
// execute_code
bool compiling = UnityEditor.EditorApplication.isCompiling;
System.Type t = System.Type.GetType("CosmeticLoadout, Assembly-CSharp");
var m = t.GetMethod("GetCrosshairSettings");
return "compiling=" + compiling + " methodFound=" + (m != null);
```
Expected: `compiling=False methodFound=True`, 콘솔 에러 없음.

- [ ] **Step 7: 수동 확인 — 로컬 캐시 왕복**

```csharp
// execute_code (Play 모드 아니어도 static 클래스라 에디터에서 바로 호출 가능)
CrosshairSettings s = new CrosshairSettings { Shape = ECrosshairShape.Circle, ColorIndex = 2, Size = 12f, Thickness = 3f };
CosmeticLoadout.SetCrosshairSettings(s);
CrosshairSettings read = CosmeticLoadout.GetCrosshairSettings();
return "shape=" + read.Shape + " color=" + read.ColorIndex + " size=" + read.Size + " thickness=" + read.Thickness;
```
Expected: `shape=Circle color=2 size=12 thickness=3`. 이 값은 다음 스텝에서 기본값으로 되돌려 둘 것(`CosmeticLoadout.SetCrosshairSettings(CrosshairSettings.Default())`) — 그렇지 않으면 이후 수동 테스트에서 예상치 못한 초기값을 보게 된다.

- [ ] **Step 8: 커밋**

```bash
git add "Assets/Scripts/Save/CosmeticLoadout.cs"
git commit -m "CosmeticLoadout에 크로스헤어 설정 로컬 캐시를 추가한다 (#945)"
```

---

## Task 3: 클라우드 저장 — `CosmeticsSaveData` 확장 + `CosmeticsSaveService` 배선

**Files:**
- Modify: `Assets/Scripts/Save/CosmeticsSaveService.cs`

**Interfaces:**
- Consumes: `CosmeticLoadout.GetCrosshairSettings()`, `SetCrosshairSettings()`, `ApplyCrosshairSettings()`, `OnCrosshairSettingsChanged`(Task 2)
- Produces: `CosmeticsSaveData.Crosshair: CrosshairSettings`(버전 4), 이후 태스크는 이 서비스를 직접 호출하지 않는다(구독만으로 자동 저장됨).

- [ ] **Step 1: 버전 올리고 필드 추가**

`CosmeticsSaveData` 클래스(227~246행)를 다음으로 교체:

```csharp
[Serializable]
public class CosmeticsSaveData
{
    // v2에서 치장(Accessories), v3에서 보유함·토큰(Owned·Tokens), v4에서 크로스헤어(Crosshair)가 추가됐다.
    // 옛 레코드는 버리지 않고 있는 것만 살린다 (CosmeticsSaveService.ReadAsync)
    public const int k_version = 4;

    public int Version = k_version;

    /// <summary>인덱스 = <see cref="EBodyPart"/>, 값 = 팔레트 색 인덱스.</summary>
    public int[] Colors;

    /// <summary>인덱스 = <see cref="EAccessorySlot"/>, 값 = 카탈로그 인덱스(0 = 안 씀). v1에는 없다.</summary>
    public int[] Accessories;

    /// <summary>자판기로 얻은 치장 (#818 D) — 기본 지급 세트는 카탈로그가 정하므로 여기 없다. v2 이하에는 없다.</summary>
    public List<CosmeticSlotOwnership> Owned;

    /// <summary>남은 뽑기 토큰 (#818 D). v2 이하에는 없어 0으로 읽힌다.</summary>
    public int Tokens;

    /// <summary>크로스헤어 설정 (#945). v3 이하에는 없어 null로 읽힌다 — 그 경우 기본값을 유지한다.</summary>
    public CrosshairSettings Crosshair;
}
```

- [ ] **Step 2: `Capture()`에 포함**

`Capture()` 메서드(145~164행)의 `return new CosmeticsSaveData { ... }` 안에 한 줄 추가:

```csharp
        return new CosmeticsSaveData
        {
            Colors = colors,
            Accessories = accessories,
            Owned = CosmeticInventory.Capture(),
            Tokens = CosmeticInventory.Tokens,
            Crosshair = CosmeticLoadout.GetCrosshairSettings(),
        };
```

- [ ] **Step 3: `RestoreAsync()`의 클라우드 적용 블록에 포함**

`RestoreAsync()` 안의 `Apply(() => { ... })` 블록(61~69행)에 한 줄 추가:

```csharp
        Apply(() =>
        {
            CosmeticLoadout.ApplyPlayerColors(data.Colors);
            CosmeticLoadout.ApplyAccessories(data.Accessories); // v1 레코드면 null — 그쪽에서 무시한다
            CosmeticInventory.Apply(data.Owned, data.Tokens);
            CosmeticLoadout.ApplyCrosshairSettings(data.Crosshair); // v3 이하 레코드면 null — 기본값 유지
        });
```

- [ ] **Step 4: 변경 구독 추가 (자동 저장)**

`Hook()` 메서드(96~106행)에 구독 한 줄 추가:

```csharp
    private static void Hook()
    {
        if (s_hooked)
            return;

        s_hooked = true;
        CosmeticLoadout.OnPlayerColorChanged += HandleColorChanged;
        CosmeticLoadout.OnAccessoryChanged += HandleAccessoryChanged;
        CosmeticLoadout.OnCrosshairSettingsChanged += HandleCrosshairChanged;
        CosmeticInventory.OnOwnedChanged += HandleInventoryChanged;
        CosmeticInventory.OnTokensChanged += HandleInventoryChanged;
    }
```

`HandleAccessoryChanged` 메서드(114~118행) 아래에 핸들러 추가:

```csharp
    private static void HandleCrosshairChanged()
    {
        if (!s_applying)
            QueueSave();
    }
```

- [ ] **Step 5: 컴파일 확인**

```csharp
// execute_code
System.Type t = System.Type.GetType("CosmeticsSaveData, Assembly-CSharp");
var f = t.GetField("Crosshair");
return "versionField=" + t.GetField("k_version") + " crosshairField=" + (f != null ? f.FieldType.Name : "MISSING");
```
Expected: `crosshairField=CrosshairSettings`, 콘솔 에러 없음.

- [ ] **Step 6: 마이그레이션 수동 확인**

기존 v3 레코드(Crosshair 필드 없음)를 흉내 낸 JSON으로 역직렬화가 안전한지 확인:

```csharp
// execute_code
string oldJson = "{\"Version\":3,\"Colors\":[0,0],\"Accessories\":[0,0],\"Tokens\":5}";
CosmeticsSaveData data = JsonUtility.FromJson<CosmeticsSaveData>(oldJson);
return "crosshairIsNull=" + (data.Crosshair == null) + " colorsLen=" + data.Colors.Length;
```
Expected: `crosshairIsNull=True colorsLen=2` — 즉 v3 레코드를 읽어도 예외 없이 `Crosshair`만 null로 비어 있고, `ApplyCrosshairSettings(null)`(Task 2 Step 3에서 null 가드 있음)이 조용히 기본값을 유지한다.

- [ ] **Step 7: 커밋**

```bash
git add "Assets/Scripts/Save/CosmeticsSaveService.cs"
git commit -m "크로스헤어 설정을 계정 클라우드 저장 경로(v4)에 배선한다 (#945)"
```

---

## Task 4: 크로스헤어 색 팔레트 에셋

**Files:**
- Create: `Assets/Data/CrosshairColorPalette.asset`

**Interfaces:**
- Consumes: 기존 `PlayerColorPalette` 클래스(신규 코드 없음, 재사용)
- Produces: 색 8개짜리 팔레트 에셋 경로 — Task 5(미리보기)·Task 7(설정 패널 스와치)이 이 에셋을 참조한다.

- [ ] **Step 1: 에셋 생성 + 값 채우기**

```csharp
// execute_code
PlayerColorPalette palette = ScriptableObject.CreateInstance<PlayerColorPalette>();
var so = new UnityEditor.SerializedObject(palette);
var colorsProp = so.FindProperty("m_colors");

Color[] colors = new Color[] {
    Color.white,
    new Color(0.973f, 0.443f, 0.443f), // 빨강
    new Color(0.290f, 0.871f, 0.502f), // 초록
    new Color(0.290f, 0.780f, 0.871f), // 시안
    new Color(0.984f, 0.749f, 0.141f), // 노랑
    new Color(0.871f, 0.290f, 0.780f), // 마젠타
    new Color(1.000f, 0.549f, 0.000f), // 주황
    new Color(1.000f, 0.412f, 0.706f), // 핑크
};

colorsProp.arraySize = colors.Length;
for (int i = 0; i < colors.Length; i++)
    colorsProp.GetArrayElementAtIndex(i).colorValue = colors[i];
so.ApplyModifiedProperties();

UnityEditor.AssetDatabase.CreateAsset(palette, "Assets/Data/CrosshairColorPalette.asset");
UnityEditor.AssetDatabase.SaveAssets();
return "created, count=" + palette.Count;
```
Expected: `created, count=8`.

- [ ] **Step 2: 커밋**

```bash
git add "Assets/Data/CrosshairColorPalette.asset" "Assets/Data/CrosshairColorPalette.asset.meta"
git commit -m "크로스헤어 색 팔레트 에셋을 추가한다 (#945)"
```

---

## Task 5: `CrosshairRenderer` 정적 유틸리티 — 모양·크기·굵기 계산

**Files:**
- Create: `Assets/Scripts/UI/Hud/CrosshairRenderer.cs`

**Interfaces:**
- Consumes: `CrosshairSettings`(Task 1), `PlayerColorPalette`(기존 클래스)
- Produces: `struct CrosshairVisualRefs { RectTransform Up, Down, Left, Right, Dot; RectTransform CircleRing; Image CircleImage; }`, `static class CrosshairRenderer { const float k_minSize, k_maxSize, k_minThickness, k_maxThickness; static void ApplyShape(CrosshairVisualRefs refs, CrosshairSettings settings); static void ApplyColor(CrosshairVisualRefs refs, CrosshairSettings settings, Color colorOverride, PlayerColorPalette palette); }` — Task 6(`CrosshairUI`)과 Task 7의 미리보기 뷰가 둘 다 이 두 메서드만 부른다.

원 모양은 두께 슬라이더가 적용되지 않는다(§4의 스코프 결정) — 기존에 프로젝트에 있는 링 스프라이트(`Pictoicon_Ring`, 고정 두께)를 크기만 조절해 쓴다. 십자·점·십자+점 세 모양만 굵기가 실제로 반영된다.

- [ ] **Step 1: 스프라이트 경로·범위 상수, `CrosshairVisualRefs` 작성**

```csharp
// Assets/Scripts/UI/Hud/CrosshairRenderer.cs
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 크로스헤어 시각 요소 참조 묶음 — 실제 게임 크로스헤어(<see cref="CrosshairUI"/>)와 설정 패널
/// 미리보기(<see cref="CrosshairPreviewView"/>)가 같은 계층 구조를 각자 인스턴스로 들고 이 구조체에 담아 넘긴다.
/// </summary>
public struct CrosshairVisualRefs
{
    public RectTransform Up;
    public RectTransform Down;
    public RectTransform Left;
    public RectTransform Right;
    public RectTransform Dot;
    public RectTransform CircleRing;
    public Image CircleImage;
}

/// <summary>
/// 크로스헤어 모양·크기·굵기·색 계산을 한 곳에 모은다 (#945). 정적 순수 함수라 게임 크로스헤어와
/// 설정 패널 미리보기가 완전히 같은 그림을 그린다 — 둘이 따로 계산하면 미리보기와 실제가 어긋난다.
/// </summary>
public static class CrosshairRenderer
{
    public const float k_minSize = 4f;
    public const float k_maxSize = 24f;
    public const float k_minThickness = 1f;
    public const float k_maxThickness = 6f;

    // 중심에서 선분이 시작되는 간격(px) — 간격 슬라이더는 이번 범위 밖(설계 문서 §6)이라 고정값.
    private const float k_lineGap = 4f;

    /// <summary>모양·크기·굵기를 반영해 각 조각의 표시 여부·RectTransform 크기를 다시 잡는다.</summary>
    public static void ApplyShape(CrosshairVisualRefs refs, CrosshairSettings settings)
    {
        float size = Mathf.Clamp(settings.Size, k_minSize, k_maxSize);
        float thickness = Mathf.Clamp(settings.Thickness, k_minThickness, k_maxThickness);

        bool showLines = settings.Shape == ECrosshairShape.Cross || settings.Shape == ECrosshairShape.CrossDot;
        bool showDot = settings.Shape == ECrosshairShape.Dot || settings.Shape == ECrosshairShape.CrossDot;
        bool showCircle = settings.Shape == ECrosshairShape.Circle;

        // vertical=세로선(길이가 y축)인가, direction=중심에서 어느 쪽으로 밀어낼지(+1/-1).
        // 회전값으로 방향을 추측하지 않는다 — Task 7에서 만드는 네 오브젝트는 전부 회전 0인
        // 평범한 RectTransform이라, 어느 쪽 선인지는 여기서 명시적으로 정해 준다.
        SetLine(refs.Up, showLines, vertical: true, direction: 1f, size, thickness, k_lineGap);
        SetLine(refs.Down, showLines, vertical: true, direction: -1f, size, thickness, k_lineGap);
        SetLine(refs.Left, showLines, vertical: false, direction: -1f, size, thickness, k_lineGap);
        SetLine(refs.Right, showLines, vertical: false, direction: 1f, size, thickness, k_lineGap);

        if (refs.Dot != null)
        {
            refs.Dot.gameObject.SetActive(showDot);
            if (showDot)
                refs.Dot.sizeDelta = new Vector2(thickness * 2f, thickness * 2f);
        }

        if (refs.CircleRing != null)
        {
            refs.CircleRing.gameObject.SetActive(showCircle);
            if (showCircle)
                refs.CircleRing.sizeDelta = new Vector2(size * 2f, size * 2f);
        }
    }

    // 선 하나 — vertical이면 sizeDelta.y가 길이(세로선), 아니면 sizeDelta.x가 길이(가로선).
    // direction(+1/-1)이 중심에서 어느 쪽으로 밀어낼지를 정한다. 둘 다 호출부(ApplyShape)가 명시한다.
    private static void SetLine(RectTransform line, bool visible, bool vertical, float direction, float size, float thickness, float gap)
    {
        if (line == null)
            return;

        line.gameObject.SetActive(visible);
        if (!visible)
            return;

        line.sizeDelta = vertical ? new Vector2(thickness, size) : new Vector2(size, thickness);

        float offset = gap + size * 0.5f;
        line.anchoredPosition = vertical ? new Vector2(0f, direction * offset) : new Vector2(direction * offset, 0f);
    }

    /// <summary>
    /// 지금 보이는 조각(선/점 또는 원) 전부에 색을 칠한다. <paramref name="colorOverride"/>가 있으면
    /// 그 색(상호작용/무기 조준 등 기능 색)을 쓰고, 없으면 설정의 <see cref="CrosshairSettings.ColorIndex"/>를
    /// 팔레트에서 찾아 쓴다 — 기본(중립) 상태에서만 커스텀 색이 보이는 이유가 이 분기다 (#945, GDD 568행).
    /// </summary>
    public static void ApplyColor(CrosshairVisualRefs refs, CrosshairSettings settings, Color? colorOverride, PlayerColorPalette palette)
    {
        Color color = colorOverride ?? (palette != null ? palette.Get(settings.ColorIndex) : Color.white);

        SetGraphicColor(refs.Up, color);
        SetGraphicColor(refs.Down, color);
        SetGraphicColor(refs.Left, color);
        SetGraphicColor(refs.Right, color);
        SetGraphicColor(refs.Dot, color);

        if (refs.CircleImage != null)
            refs.CircleImage.color = color;
    }

    private static void SetGraphicColor(RectTransform target, Color color)
    {
        if (target == null)
            return;

        Graphic graphic = target.GetComponent<Graphic>();
        if (graphic != null)
            graphic.color = color;
    }
}
```

- [ ] **Step 2: 컴파일 확인**

```csharp
// execute_code
System.Type t = System.Type.GetType("CrosshairRenderer, Assembly-CSharp");
return "found=" + (t != null);
```
Expected: `found=True`, 콘솔 에러 없음.

- [ ] **Step 3: 커밋**

```bash
git add "Assets/Scripts/UI/Hud/CrosshairRenderer.cs"
git commit -m "크로스헤어 모양·크기·굵기·색 계산을 정적 유틸리티로 분리한다 (#945)"
```

---

## Task 6: `CrosshairUI` 확장 — 조합형 렌더링 + 계정 설정 반영

**Files:**
- Modify: `Assets/Scripts/UI/Hud/CrosshairUI.cs`

**Interfaces:**
- Consumes: `CrosshairVisualRefs`·`CrosshairRenderer.ApplyShape/ApplyColor`(Task 5), `CosmeticLoadout.GetCrosshairSettings()`·`OnCrosshairSettingsChanged`(Task 2), `CrosshairColorPalette.asset`(Task 4)
- Produces: 기존 공개 API(`SetVisible`·`SetInteractable`·`SetWeaponTargeting`·`ShowHit`·`ShowKill`)는 시그니처 그대로 유지 — `InteractionFeedback`·`Baton`·`PlayerKillCredit` 등 기존 호출부를 고칠 필요가 없다.

- [ ] **Step 1: 필드 교체 — 단일 Image를 조합 참조로**

`m_crosshairImage` 필드(16행)를 다음으로 교체:

```csharp
    [Header("조합형 크로스헤어 (#945)")]
    [SerializeField] private RectTransform m_lineUp;
    [SerializeField] private RectTransform m_lineDown;
    [SerializeField] private RectTransform m_lineLeft;
    [SerializeField] private RectTransform m_lineRight;
    [SerializeField] private RectTransform m_dot;
    [SerializeField] private RectTransform m_circleRing;
    [SerializeField] private Image m_circleImage;
    [SerializeField] private RectTransform m_crosshairRoot; // SetVisible 대상 — 전 조각의 공통 부모
    [SerializeField] private PlayerColorPalette m_colorPalette;

    private CrosshairVisualRefs VisualRefs => new CrosshairVisualRefs
    {
        Up = m_lineUp, Down = m_lineDown, Left = m_lineLeft, Right = m_lineRight,
        Dot = m_dot, CircleRing = m_circleRing, CircleImage = m_circleImage,
    };

    // 마지막으로 적용된 설정 — SetInteractable/SetWeaponTargeting이 색만 덮어쓸 때 모양은 그대로 둬야 하므로 기억해 둔다.
    private CrosshairSettings m_currentSettings = CrosshairSettings.Default();
```

- [ ] **Step 2: `SetVisible`을 새 루트 대상으로 교체**

`SetVisible` 메서드(33~37행)를 다음으로 교체:

```csharp
    public void SetVisible(bool visible)
    {
        if (m_crosshairRoot != null)
            m_crosshairRoot.gameObject.SetActive(visible);
    }
```

- [ ] **Step 3: `SetInteractable`/`SetWeaponTargeting`을 `CrosshairRenderer.ApplyColor` 경유로 교체**

두 메서드(40~54행)를 다음으로 교체:

```csharp
    /// <summary>조준 대상의 상호작용 가능 여부에 따라 크로스헤어 색을 바꾼다.</summary>
    public void SetInteractable(bool interactable)
    {
        Color? overrideColor = interactable ? m_interactableColor : (Color?)null;
        CrosshairRenderer.ApplyColor(VisualRefs, m_currentSettings, overrideColor, m_colorPalette);
    }

    /// <summary>
    /// 조준 무기(<see cref="IAimedWeapon"/>) 사용 중, 명중 가능한 대상을 겨눴는지에 따라 색을 바꾼다. (#328/#217)
    /// 이 무기들은 NPC 윤곽선을 끄므로(InteractionFeedback) 크로스헤어가 유일한 조준 피드백이다.
    /// </summary>
    public void SetWeaponTargeting(bool onTarget)
    {
        Color? overrideColor = onTarget ? m_weaponTargetColor : (Color?)null;
        CrosshairRenderer.ApplyColor(VisualRefs, m_currentSettings, overrideColor, m_colorPalette);
    }
```

- [ ] **Step 4: 계정 설정 반영 — Awake 구독 + 적용 메서드**

`CommonManagerBase`를 상속하므로 `Awake`/`OnDestroy`는 `protected override` + `base` 호출이 필수다(R5). 기존 `OnDestroy`(107~111행) 바로 위에 `Awake` 오버라이드를 새로 추가하고, `OnDestroy`에 구독 해제를 더한다:

```csharp
    protected override void Awake()
    {
        base.Awake();
        CosmeticLoadout.OnCrosshairSettingsChanged += HandleCrosshairSettingsChanged;
        ApplyCurrentSettings();
    }

    // 계정 설정이 바뀔 때마다(설정 패널에서 슬라이더를 움직이는 즉시) 다시 그린다.
    private void HandleCrosshairSettingsChanged() => ApplyCurrentSettings();

    private void ApplyCurrentSettings()
    {
        m_currentSettings = CosmeticLoadout.GetCrosshairSettings();
        CrosshairRenderer.ApplyShape(VisualRefs, m_currentSettings);
        CrosshairRenderer.ApplyColor(VisualRefs, m_currentSettings, null, m_colorPalette);
    }
```

`OnDestroy`(현재 107~111행)를 다음으로 교체:

```csharp
    protected override void OnDestroy()
    {
        CosmeticLoadout.OnCrosshairSettingsChanged -= HandleCrosshairSettingsChanged;
        CancelKillFade();
        base.OnDestroy();
    }
```

- [ ] **Step 5: 컴파일 확인**

```csharp
// execute_code
bool compiling = UnityEditor.EditorApplication.isCompiling;
System.Type t = System.Type.GetType("CrosshairUI, Assembly-CSharp");
var m = t.GetMethod("SetVisible");
return "compiling=" + compiling + " methodFound=" + (m != null);
```
Expected: `compiling=False methodFound=True`, 콘솔에 `error CS` 없음. **이 시점에는 아직 프리팹에 새 필드가 안 걸려 있어(다음 태스크) 씬을 열면 크로스헤어가 안 보일 수 있다 — 정상이다, Task 7에서 고친다.**

- [ ] **Step 6: 커밋**

```bash
git add "Assets/Scripts/UI/Hud/CrosshairUI.cs"
git commit -m "CrosshairUI를 선분+점+원 조합형 렌더링으로 확장한다 (#945)"
```

---

## Task 7: `HUD.prefab` — 크로스헤어 하위 오브젝트 구성

**Files:**
- Modify: `Assets/Prefabs/UI/HUD.prefab` (Unity MCP `execute_code`로 편집 — 직접 텍스트 편집 금지)

**Interfaces:**
- Consumes: `CrosshairUI`(Task 6)의 새 필드, `Assets/Data/CrosshairColorPalette.asset`(Task 4)
- Produces: `HUD.prefab`의 `Crosshair` 오브젝트가 Up/Down/Left/Right/Dot/CircleRing 다섯 자식을 갖고 `CrosshairUI`의 새 필드에 전부 연결된 상태 — Task 9의 실기 검증이 이 결과물을 확인한다.

기존 `Crosshair` GameObject(15×15 흰 `Image`)를 **루트 컨테이너**로 재활용한다 — 그 자신의 `Image` 컴포넌트는 제거하고 자식 다섯 개를 새로 붙인다.

- [ ] **Step 1: 현재 구조 확인**

```csharp
// execute_code
GameObject prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/UI/HUD.prefab");
Transform crosshair = null;
foreach (Transform t in prefab.GetComponentsInChildren<Transform>(true))
    if (t.name == "Crosshair") crosshair = t;
if (crosshair == null) return "Crosshair 오브젝트를 못 찾음";
RectTransform rt = crosshair.GetComponent<RectTransform>();
return "found, sizeDelta=" + rt.sizeDelta + " childCount=" + crosshair.childCount;
```
Expected: `found, sizeDelta=(15, 15) childCount=0` (지금은 자식이 없다).

- [ ] **Step 2: 루트의 기존 Image 제거 + 다섯 자식 생성**

```csharp
// execute_code
string prefabPath = "Assets/Prefabs/UI/HUD.prefab";
GameObject root = UnityEditor.PrefabUtility.LoadPrefabContents(prefabPath);

Transform crosshair = null;
foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
    if (t.name == "Crosshair") crosshair = t;
if (crosshair == null) return "Crosshair 오브젝트를 못 찾음";

UnityEngine.UI.Image rootImg = crosshair.GetComponent<UnityEngine.UI.Image>();
if (rootImg != null) UnityEngine.Object.DestroyImmediate(rootImg);

// 선 4개 — Up/Down은 세로선(길이=y), Left/Right는 가로선(길이=x). 앵커는 전부 중앙.
System.Collections.Generic.Dictionary<string, Vector2> linePositions = new System.Collections.Generic.Dictionary<string, Vector2> {
    { "LineUp", new Vector2(0f, 1f) },
    { "LineDown", new Vector2(0f, -1f) },
    { "LineLeft", new Vector2(-1f, 0f) },
    { "LineRight", new Vector2(1f, 0f) },
};
System.Collections.Generic.Dictionary<string, RectTransform> lines = new System.Collections.Generic.Dictionary<string, RectTransform>();
foreach (var kv in linePositions)
{
    GameObject go = new GameObject(kv.Key, typeof(RectTransform));
    go.transform.SetParent(crosshair, false);
    RectTransform rt = go.GetComponent<RectTransform>();
    rt.anchorMin = new Vector2(0.5f, 0.5f);
    rt.anchorMax = new Vector2(0.5f, 0.5f);
    rt.pivot = new Vector2(0.5f, 0.5f);
    rt.anchoredPosition = kv.Value; // 실제 간격은 CrosshairRenderer.SetLine이 매 호출마다 다시 계산해 덮어쓴다
    rt.sizeDelta = new Vector2(2f, 8f);
    UnityEngine.UI.Image img = go.AddComponent<UnityEngine.UI.Image>();
    img.color = Color.white;
    img.raycastTarget = false;
    lines[kv.Key] = rt;
}

// 점
GameObject dotGo = new GameObject("Dot", typeof(RectTransform));
dotGo.transform.SetParent(crosshair, false);
RectTransform dotRt = dotGo.GetComponent<RectTransform>();
dotRt.anchorMin = new Vector2(0.5f, 0.5f);
dotRt.anchorMax = new Vector2(0.5f, 0.5f);
dotRt.pivot = new Vector2(0.5f, 0.5f);
dotRt.anchoredPosition = Vector2.zero;
dotRt.sizeDelta = new Vector2(4f, 4f);
UnityEngine.UI.Image dotImg = dotGo.AddComponent<UnityEngine.UI.Image>();
dotImg.color = Color.white;
dotImg.raycastTarget = false;
dotGo.SetActive(false);

// 원(고정 두께 링 스프라이트)
GameObject circleGo = new GameObject("CircleRing", typeof(RectTransform));
circleGo.transform.SetParent(crosshair, false);
RectTransform circleRt = circleGo.GetComponent<RectTransform>();
circleRt.anchorMin = new Vector2(0.5f, 0.5f);
circleRt.anchorMax = new Vector2(0.5f, 0.5f);
circleRt.pivot = new Vector2(0.5f, 0.5f);
circleRt.anchoredPosition = Vector2.zero;
circleRt.sizeDelta = new Vector2(16f, 16f);
UnityEngine.UI.Image circleImg = circleGo.AddComponent<UnityEngine.UI.Image>();
circleImg.sprite = UnityEditor.AssetDatabase.LoadAssetAtPath<Sprite>(
    "Assets/Imported/Layer Lab/GUI Pro-CasualGame/ResourcesData/Sprites/Components/Icon_PictoIcons/256/Pictoicon_Ring.Png");
circleImg.color = Color.white;
circleImg.raycastTarget = false;
circleImg.preserveAspect = true;
circleGo.SetActive(false);

// CrosshairUI 필드 배선 + 팔레트 연결
UnityEngine.Object crosshairUiComp = crosshair.GetComponent(System.Type.GetType("CrosshairUI, Assembly-CSharp"));
UnityEditor.SerializedObject so = new UnityEditor.SerializedObject(crosshairUiComp);
so.FindProperty("m_lineUp").objectReferenceValue = lines["LineUp"];
so.FindProperty("m_lineDown").objectReferenceValue = lines["LineDown"];
so.FindProperty("m_lineLeft").objectReferenceValue = lines["LineLeft"];
so.FindProperty("m_lineRight").objectReferenceValue = lines["LineRight"];
so.FindProperty("m_dot").objectReferenceValue = dotRt;
so.FindProperty("m_circleRing").objectReferenceValue = circleRt;
so.FindProperty("m_circleImage").objectReferenceValue = circleImg;
so.FindProperty("m_crosshairRoot").objectReferenceValue = crosshair.GetComponent<RectTransform>();
so.FindProperty("m_colorPalette").objectReferenceValue =
    UnityEditor.AssetDatabase.LoadAssetAtPath<PlayerColorPalette>("Assets/Data/CrosshairColorPalette.asset");
so.ApplyModifiedProperties();

bool saved;
UnityEditor.PrefabUtility.SaveAsPrefabAsset(root, prefabPath, out saved);
UnityEditor.PrefabUtility.UnloadPrefabContents(root);
return "saved=" + saved;
```
Expected: `saved=True`.

- [ ] **Step 3: 컴파일·배선 확인**

```csharp
// execute_code
GameObject prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/UI/HUD.prefab");
Transform crosshair = null;
foreach (Transform t in prefab.GetComponentsInChildren<Transform>(true))
    if (t.name == "Crosshair") crosshair = t;
UnityEngine.Object comp = crosshair.GetComponent(System.Type.GetType("CrosshairUI, Assembly-CSharp"));
UnityEditor.SerializedObject so = new UnityEditor.SerializedObject(comp);
return "lineUp=" + (so.FindProperty("m_lineUp").objectReferenceValue != null) +
    " dot=" + (so.FindProperty("m_dot").objectReferenceValue != null) +
    " circle=" + (so.FindProperty("m_circleImage").objectReferenceValue != null) +
    " palette=" + (so.FindProperty("m_colorPalette").objectReferenceValue != null) +
    " childCount=" + crosshair.childCount;
```
Expected: 전부 `True`, `childCount=6`.

- [ ] **Step 4: 무관한 변경 없는지 diff 확인**

```bash
git diff --stat -- "Assets/Prefabs/UI/HUD.prefab"
```
`Crosshair` 관련 항목만 늘어났는지 확인 — HUD.prefab에는 히트마커·킬 라벨 등 다른 시스템도 있으므로, 그쪽 필드 값이 실수로 바뀌지 않았는지 diff를 눈으로 훑는다.

- [ ] **Step 5: 커밋**

```bash
git add "Assets/Prefabs/UI/HUD.prefab"
git commit -m "HUD 크로스헤어를 선분+점+원 조합 오브젝트로 재구성한다 (#945)"
```

---

## Task 8: `CrosshairPreviewView` — 설정 패널용 경량 미리보기

**Files:**
- Create: `Assets/Scripts/UI/Cosmetics/CrosshairPreviewView.cs`

**Interfaces:**
- Consumes: `CrosshairVisualRefs`·`CrosshairRenderer`(Task 5)
- Produces: `class CrosshairPreviewView : MonoBehaviour { public void Refresh(CrosshairSettings settings); }` — Task 9의 설정 패널이 이 컴포넌트를 인스턴스화해 슬라이더 변경마다 `Refresh`를 부른다.

`CrosshairUI`(Task 6)는 `CommonManagerBase`라 씬당 하나만 등록 가능하다 — 설정 패널 미리보기는 별도의 가벼운 `MonoBehaviour`로 만든다. 계층 구조(Up/Down/Left/Right/Dot/CircleRing)는 HUD.prefab의 것과 동일하게 만든다(Task 9에서 구성).

- [ ] **Step 1: 스크립트 작성**

```csharp
// Assets/Scripts/UI/Cosmetics/CrosshairPreviewView.cs
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 크로스헤어 설정 패널의 실시간 미리보기 (#945) — 실제 게임 크로스헤어(HUD의 CrosshairUI)와
/// 같은 <see cref="CrosshairRenderer"/> 계산을 쓰므로 미리보기와 실제가 항상 같은 그림이 된다.
/// CommonManagerBase가 아닌 평범한 컴포넌트다 — 씬에 이미 있는 CrosshairUI와 별개로,
/// 패널이 열려 있는 동안만 켜지는 인스턴스라 매니저 등록이 필요 없다.
/// </summary>
public class CrosshairPreviewView : MonoBehaviour
{
    [SerializeField] private RectTransform m_lineUp;
    [SerializeField] private RectTransform m_lineDown;
    [SerializeField] private RectTransform m_lineLeft;
    [SerializeField] private RectTransform m_lineRight;
    [SerializeField] private RectTransform m_dot;
    [SerializeField] private RectTransform m_circleRing;
    [SerializeField] private Image m_circleImage;
    [SerializeField] private PlayerColorPalette m_colorPalette;

    private CrosshairVisualRefs VisualRefs => new CrosshairVisualRefs
    {
        Up = m_lineUp, Down = m_lineDown, Left = m_lineLeft, Right = m_lineRight,
        Dot = m_dot, CircleRing = m_circleRing, CircleImage = m_circleImage,
    };

    /// <summary>패널이 열려 있는 동안 슬라이더·모양·색이 바뀔 때마다 부른다.</summary>
    public void Refresh(CrosshairSettings settings)
    {
        CrosshairRenderer.ApplyShape(VisualRefs, settings);
        CrosshairRenderer.ApplyColor(VisualRefs, settings, null, m_colorPalette);
    }
}
```

- [ ] **Step 2: 컴파일 확인**

```csharp
// execute_code
System.Type t = System.Type.GetType("CrosshairPreviewView, Assembly-CSharp");
return "found=" + (t != null);
```
Expected: `found=True`.

- [ ] **Step 3: 커밋**

```bash
git add "Assets/Scripts/UI/Cosmetics/CrosshairPreviewView.cs"
git commit -m "설정 패널용 크로스헤어 미리보기 뷰를 추가한다 (#945)"
```

---

## Task 9: `CrosshairSettingsPanel` — 설정 UI (모양·색·크기·굵기 + 미리보기)

**Files:**
- Create: `Assets/Scripts/UI/Panels/CrosshairSettingsPanel.cs`
- Modify: `Assets/Prefabs/UI/SettingsCanvas.prefab` (신규 자식 프리팹/오브젝트로 패널 추가)

**Interfaces:**
- Consumes: `CosmeticLoadout.GetCrosshairSettings/SetCrosshairSettings`(Task 2), `CrosshairPreviewView.Refresh`(Task 8), `PlayerColorSwatchView`(기존 클래스, 재사용), `CrosshairColorPalette.asset`(Task 4)
- Produces: `App.UI.Current.TryGetPanel(out CrosshairSettingsPanel panel)`로 어디서든 찾을 수 있는 패널 — Task 10의 Look 탭 버튼이 이걸 연다.

- [ ] **Step 1: 패널 스크립트 작성**

```csharp
// Assets/Scripts/UI/Panels/CrosshairSettingsPanel.cs
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 크로스헤어 커스터마이징 창 (#945) — 설정 창 Look 탭 버튼으로 연다. 모양 4종 토글, 색 스와치,
/// 크기·굵기 슬라이더를 <see cref="CosmeticLoadout"/>에 즉시 쓰고(설정 창의 "즉시 적용" 관례,
/// docs/design/settings-ui.md), 같은 값으로 미리보기(<see cref="CrosshairPreviewView"/>)를 다시 그린다.
/// </summary>
public class CrosshairSettingsPanel : PanelBase
{
    [Header("모양")]
    [SerializeField] private Toggle m_shapeCrossToggle;
    [SerializeField] private Toggle m_shapeDotToggle;
    [SerializeField] private Toggle m_shapeCrossDotToggle;
    [SerializeField] private Toggle m_shapeCircleToggle;

    [Header("색")]
    [SerializeField] private RectTransform m_swatchContainer;
    [SerializeField] private PlayerColorSwatchView m_swatchPrefab;
    [SerializeField] private PlayerColorPalette m_colorPalette;

    [Header("크기·굵기")]
    [SerializeField] private Slider m_sizeSlider;
    [SerializeField] private Slider m_thicknessSlider;

    [Header("미리보기")]
    [SerializeField] private CrosshairPreviewView m_preview;

    [SerializeField] private Button m_closeButton;

    public override bool CanCloseWithESC => true;
    public override bool IsStackable => true;

    private readonly System.Collections.Generic.List<PlayerColorSwatchView> m_swatches =
        new System.Collections.Generic.List<PlayerColorSwatchView>();

    protected override void Awake()
    {
        base.Awake();

        if (m_closeButton != null)
            m_closeButton.onClick.AddListener(ClosePanel);

        m_sizeSlider.minValue = CrosshairRenderer.k_minSize;
        m_sizeSlider.maxValue = CrosshairRenderer.k_maxSize;
        m_sizeSlider.wholeNumbers = false;
        m_sizeSlider.onValueChanged.AddListener(HandleSizeChanged);

        m_thicknessSlider.minValue = CrosshairRenderer.k_minThickness;
        m_thicknessSlider.maxValue = CrosshairRenderer.k_maxThickness;
        m_thicknessSlider.wholeNumbers = false;
        m_thicknessSlider.onValueChanged.AddListener(HandleThicknessChanged);

        m_shapeCrossToggle.onValueChanged.AddListener(isOn => { if (isOn) HandleShapeChanged(ECrosshairShape.Cross); });
        m_shapeDotToggle.onValueChanged.AddListener(isOn => { if (isOn) HandleShapeChanged(ECrosshairShape.Dot); });
        m_shapeCrossDotToggle.onValueChanged.AddListener(isOn => { if (isOn) HandleShapeChanged(ECrosshairShape.CrossDot); });
        m_shapeCircleToggle.onValueChanged.AddListener(isOn => { if (isOn) HandleShapeChanged(ECrosshairShape.Circle); });

        BuildSwatches();
    }

    private void BuildSwatches()
    {
        for (int i = 0; i < m_colorPalette.Count; i++)
        {
            int index = i;
            PlayerColorSwatchView swatch = Instantiate(m_swatchPrefab, m_swatchContainer);
            swatch.name = $"Swatch {index}";
            swatch.Bind(m_colorPalette.Get(index), () => HandleColorChanged(index));
            m_swatches.Add(swatch);
        }
    }

    public override void OpenPanel()
    {
        base.OpenPanel();
        SyncFromSettings();
    }

    // 설정 창 관례(SettingsPanel.SyncFromSettings)와 같다 — WithoutNotify로 넣어야 되먹임이 안 생긴다.
    private void SyncFromSettings()
    {
        CrosshairSettings settings = CosmeticLoadout.GetCrosshairSettings();

        m_shapeCrossToggle.SetIsOnWithoutNotify(settings.Shape == ECrosshairShape.Cross);
        m_shapeDotToggle.SetIsOnWithoutNotify(settings.Shape == ECrosshairShape.Dot);
        m_shapeCrossDotToggle.SetIsOnWithoutNotify(settings.Shape == ECrosshairShape.CrossDot);
        m_shapeCircleToggle.SetIsOnWithoutNotify(settings.Shape == ECrosshairShape.Circle);

        m_sizeSlider.SetValueWithoutNotify(settings.Size);
        m_thicknessSlider.SetValueWithoutNotify(settings.Thickness);

        for (int i = 0; i < m_swatches.Count; i++)
            m_swatches[i].SetSelected(i == settings.ColorIndex);

        RefreshPreview(settings);
    }

    private void HandleShapeChanged(ECrosshairShape shape) => ApplyChange(s => s.Shape = shape);
    private void HandleColorChanged(int index) => ApplyChange(s => s.ColorIndex = index);
    private void HandleSizeChanged(float value) => ApplyChange(s => s.Size = value);
    private void HandleThicknessChanged(float value) => ApplyChange(s => s.Thickness = value);

    // 현재 값을 복사해 한 필드만 바꾸고 그대로 저장한다 — CosmeticLoadout 쪽 값 자체를 직접
    // 변형하지 않는 이유는 참조 공유로 인한 사고를 막기 위해서다(§2 GetCrosshairSettings 주석 참고).
    private void ApplyChange(System.Action<CrosshairSettings> mutate)
    {
        CrosshairSettings settings = CosmeticLoadout.GetCrosshairSettings();
        CrosshairSettings next = new CrosshairSettings
        {
            Shape = settings.Shape,
            ColorIndex = settings.ColorIndex,
            Size = settings.Size,
            Thickness = settings.Thickness,
        };
        mutate(next);

        CosmeticLoadout.SetCrosshairSettings(next);

        for (int i = 0; i < m_swatches.Count; i++)
            m_swatches[i].SetSelected(i == next.ColorIndex);

        RefreshPreview(next);
    }

    private void RefreshPreview(CrosshairSettings settings)
    {
        if (m_preview != null)
            m_preview.Refresh(settings);
    }
}
```

- [ ] **Step 2: 컴파일 확인**

```csharp
// execute_code
System.Type t = System.Type.GetType("CrosshairSettingsPanel, Assembly-CSharp");
return "found=" + (t != null);
```
Expected: `found=True`, 콘솔에 `error CS` 없음.

- [ ] **Step 3: 패널 UI 오브젝트 구성 (SettingsCanvas.prefab)**

`PlayerColorPanel`과 같은 구조(배경 딤 + 창 + 닫기 버튼)로 새 자식을 만들고, Task 8의 `CrosshairPreviewView` 계층(Up/Down/Left/Right/Dot/CircleRing, Task 7의 HUD 구성과 동일한 방식으로)을 미리보기 영역에 인스턴스화한다. 이 스텝은 세부 좌표·레이아웃이 많아 Unity Editor에서 사람이 직접 배치하는 편이 낫다 — Unity MCP로는 다음만 자동화한다:

```csharp
// execute_code — SettingsCanvas.prefab에 빈 패널 오브젝트 뼈대만 만들어 둔다.
// (배경 이미지·버튼 비주얼·레이아웃은 이 스텝 이후 Editor에서 육안으로 배치)
string prefabPath = "Assets/Prefabs/UI/SettingsCanvas.prefab";
GameObject root = UnityEditor.PrefabUtility.LoadPrefabContents(prefabPath);

Transform existing = root.transform.Find("CrosshairSettingsPanel");
if (existing != null) return "이미 존재함 — 중복 생성 방지, 수동으로 확인할 것";

GameObject panelGo = new GameObject("CrosshairSettingsPanel", typeof(RectTransform));
panelGo.transform.SetParent(root.transform, false);
RectTransform panelRt = panelGo.GetComponent<RectTransform>();
panelRt.anchorMin = Vector2.zero;
panelRt.anchorMax = Vector2.one;
panelRt.offsetMin = Vector2.zero;
panelRt.offsetMax = Vector2.zero;
panelGo.AddComponent<CanvasGroup>();
panelGo.AddComponent(System.Type.GetType("CrosshairSettingsPanel, Assembly-CSharp"));
panelGo.SetActive(false); // PanelBase.Awake가 OpenOnAwake=false 기본값으로 다시 끄지만, 저장 시점부터 꺼둔다

bool saved;
UnityEditor.PrefabUtility.SaveAsPrefabAsset(root, prefabPath, out saved);
UnityEditor.PrefabUtility.UnloadPrefabContents(root);
return "scaffold created, saved=" + saved;
```

- [ ] **Step 4: 사람 확인 필요 — Editor에서 내부 UI 배치**

여기서부터는 Unity Editor를 직접 열어 `CrosshairSettingsPanel` 오브젝트 아래에 다음을 배치하고 인스펙터에서 배선한다(코드로 대신할 수 없는 시각 배치 작업):

1. 딤 배경 + 창 프레임(`PlayerColorPanel`의 배경/프레임을 참고해 톤 맞추기)
2. 모양 토글 4개(Cross/Dot/CrossDot/Circle) → `m_shapeCrossToggle` 등 4개 필드에 연결, 같은 `ToggleGroup`으로 묶어 하나만 선택되게 함
3. 색 스와치 컨테이너(Horizontal/Grid Layout Group) → `m_swatchContainer`, `m_swatchPrefab`(기존 `PlayerColorSwatchView` 프리팹 재사용), `m_colorPalette`(Task 4 에셋) 연결
4. 크기·굵기 슬라이더 2개 → `m_sizeSlider`/`m_thicknessSlider`
5. 미리보기 영역에 Task 8의 `CrosshairPreviewView` 계층(Task 7과 동일 구성: Up/Down/Left/Right/Dot/CircleRing) 배치 → `m_preview`에 연결, 그 컴포넌트 내부 6개 필드에도 각 RectTransform/Image 연결 + `m_colorPalette` 연결
6. 닫기 버튼 → `m_closeButton`

배치가 끝나면 `git diff --stat -- "Assets/Prefabs/UI/SettingsCanvas.prefab"`로 다른 탭 오브젝트가 실수로 안 건드려졌는지 확인.

- [ ] **Step 5: 커밋**

```bash
git add "Assets/Scripts/UI/Panels/CrosshairSettingsPanel.cs" "Assets/Prefabs/UI/SettingsCanvas.prefab"
git commit -m "크로스헤어 설정 패널(모양·색·크기·굵기+미리보기)을 추가한다 (#945)"
```

---

## Task 10: `SettingsPanel` Look 탭 — 진입 버튼

**Files:**
- Modify: `Assets/Scripts/UI/Panels/SettingsPanel.cs`
- Modify: `Assets/Prefabs/UI/SettingsCanvas.prefab` (LookPage에 버튼 추가)

**Interfaces:**
- Consumes: `App.UI.Current.TryGetPanel<CrosshairSettingsPanel>()`(Task 9)

- [ ] **Step 1: 버튼 필드·핸들러 추가**

`SettingsPanel.cs`의 Look 탭 관련 필드 선언부(감도·룩스무딩·FOV 슬라이더 근처)에 추가:

```csharp
    [Tooltip("크로스헤어 설정 패널을 여는 버튼 (#945)")]
    [SerializeField] private Button m_crosshairSettingsButton;
```

`Awake()`의 슬라이더 셋업 블록(117~134행 부근)에 리스너 추가:

```csharp
        if (m_crosshairSettingsButton != null)
            m_crosshairSettingsButton.onClick.AddListener(HandleCrosshairSettingsClicked);
```

메서드 영역에 핸들러 추가:

```csharp
    // PlayerColorPanel과 같은 열기 방식 — App.UI.Current에 등록된 인스턴스를 찾아 연다 (#945)
    private void HandleCrosshairSettingsClicked()
    {
        if (App.UI.Current != null && App.UI.Current.TryGetPanel(out CrosshairSettingsPanel panel))
            panel.OpenPanel();
        else
            Debug.LogWarning("SettingsPanel: 크로스헤어 설정 패널을 찾지 못했다");
    }
```

`OnDestroy()`(혹은 리스너 해제 블록)에 대응 라인 추가:

```csharp
        if (m_crosshairSettingsButton != null)
            m_crosshairSettingsButton.onClick.RemoveListener(HandleCrosshairSettingsClicked);
```

- [ ] **Step 2: 컴파일 확인**

```csharp
// execute_code
bool compiling = UnityEditor.EditorApplication.isCompiling;
return "compiling=" + compiling;
```
Expected: `compiling=False`, 콘솔에 `error CS` 없음.

- [ ] **Step 3: LookPage에 버튼 배치 (Editor 수동)**

`SettingsCanvas.prefab`의 `LookPage` 아래에 버튼 하나를 추가하고(기존 슬라이더 행과 같은 톤), `SettingsPanel`의 `m_crosshairSettingsButton`에 연결한다. 코드로 자동화하지 않는 이유는 다른 Look 탭 항목들과 시각적으로 나란히 배치해야 해서다(정확한 좌표는 기존 슬라이더 행 레이아웃을 보고 맞춘다).

- [ ] **Step 4: 커밋**

```bash
git add "Assets/Scripts/UI/Panels/SettingsPanel.cs" "Assets/Prefabs/UI/SettingsCanvas.prefab"
git commit -m "설정 창 Look 탭에 크로스헤어 설정 진입 버튼을 추가한다 (#945)"
```

---

## Task 11: 통합 수동 검증

**Files:** 없음(코드 변경 없음 — 전체 플로우 검증)

- [ ] **Step 1: Play 모드 — 모양·색·크기·굵기 즉시 반영**

Unity Editor Play 모드로 게임 씬에 들어가 설정 → Look 탭 → 크로스헤어 설정 버튼을 누른다. 모양 4종을 각각 골라 화면 크로스헤어가 그 즉시 바뀌는지, 색 스와치·크기·굵기 슬라이더도 실시간으로 반영되는지 확인한다.

- [ ] **Step 2: 안전 신호 유지 확인 (GDD 568행)**

임의의 커스텀 색(예: 초록)을 고른 채로 상호작용 가능한 대상과 무기 조준 가능 대상을 각각 겨눈다 — 크로스헤어가 커스텀 색이 아니라 기존 고정 노랑/빨강으로 바뀌는지 확인한다. 조준을 풀면 다시 커스텀 색으로 돌아오는지도 확인한다.

- [ ] **Step 3: 계정 클라우드 왕복 확인**

로그아웃 후 다른 계정으로 로그인 → 크로스헤어가 기본값(또는 그 계정에 저장된 값)으로 바뀌는지 확인. 원래 계정으로 재로그인 → 방금 고른 커스텀 설정이 그대로 돌아오는지 확인(로컬 PlayerPrefs 캐시 확인이라 같은 PC 안에서도 계정 전환만으로 검증 가능 — 다른 PC 동기화는 Unity Cloud Save 콘솔 또는 두 번째 기기로 별도 확인).

- [ ] **Step 4: 회귀 확인 — 히트마커·킬 컨펌**

Task 6에서 `SetVisible` 대상을 바꿨으므로(단일 Image → 루트 컨테이너), 무력화·입력 정지 시 크로스헤어 전체가 여전히 숨겨지는지, 히트마커(진압봉 명중)와 킬 컨펌 텍스트가 여전히 정상 표시·페이드되는지 확인한다(둘 다 `m_crosshairRoot`가 아니라 `m_hitMarker`/`m_killLabel`을 직접 켜고 끄므로 영향이 없어야 정상).

- [ ] **Step 5: 최종 커밋 없음 — 문제 발견 시 해당 태스크로 돌아가 수정 후 그 태스크 커밋에 포함**
