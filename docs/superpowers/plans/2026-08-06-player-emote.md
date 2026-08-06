# 플레이어 감정표현 (#219) 구현 플랜

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 로비에서 구성한 8칸 감정표현 휠을 인게임에서 `T` 홀드로 꺼내 발동하고, 애니메이션과 머리 위 이모지가 전 피어에 동기화되게 한다.

**Architecture:** 감정표현 정의는 `EmoteCatalog`(ScriptableObject) 하나에 모으고, **리스트 인덱스를 네트워크 계약**으로 삼는다. 재생 상태는 서버 권위 `NetworkVariable<sbyte>` 하나이며 전 피어가 이를 구독해 애니메이터를 구동한다(기존 `Down`/`Crouch`/`Airborne`과 같은 결). 순수 로직 두 개(슬롯 저장, 휠 각도 계산)는 별도 asmdef로 떼어 EditMode 테스트를 붙인다.

**Tech Stack:** Unity 6000.3.15f1 · Netcode for GameObjects · Input System · Unity Localization · Unity Test Framework 1.6.0 · Kevin Iglesias Human Dance Animations

**설계 정본:** [docs/superpowers/specs/2026-08-06-player-emote-design.md](../specs/2026-08-06-player-emote-design.md)

## Global Constraints

- **네이밍** — private/protected 인스턴스 멤버 `m_`, static `s_`, const `k_`, 인터페이스 `I`, 제네릭 `T`, 매개변수/지역변수 camelCase, public 프로퍼티/메서드·enum 값 PascalCase, 이벤트 `On` 접두사 (CLAUDE.md 코드 컨벤션)
- **전역 접근** — `App` 파사드만 사용. `FindFirstObjectByType`으로 매니저 검색 금지, 신규 `static Instance` 싱글톤 금지 (architecture R1·R2)
- **매니저 상속** — 새 매니저는 `CommonManagerBase`/`NetworkedManagerBase` 상속, `Awake`/`OnDestroy`는 `protected override` + `base` 호출 (R4·R5). **이 플랜은 새 매니저를 만들지 않는다**
- **씬 전환** — `App.LoadScene`만 사용 (R7)
- **UI 패널** — `PanelBase` 상속, 열고 닫기는 `App.UI.Current.OpenPanel<T>()` 경유. `SetActive` 직접 호출 금지
- **서드파티** — `Assets/Imported/` 아래 코드·셰이더 수정 금지
- **주석** — 한국어. "무엇을"이 아니라 **"왜 이렇게 했는가"**를 남긴다 (기존 코드 관례)
- **컴파일 확인** — 스크립트 수정 후 Unity Console에 에러가 없는지 확인한 뒤 다음 태스크로 넘어간다 (MCP `read_console`, `editor_state.isCompiling` 폴링)
- **슬롯 수** — 8칸 고정. `EmoteWheelGeometry.k_slotCount`와 `EmoteLoadout.k_slotCount`가 같은 값을 봐야 한다
- **Play 모드 검증은 사용자 몫** — 각 태스크는 컴파일 통과까지 책임진다

## 태스크 순서

번호 순서대로 가되 **두 곳만 예외**다 — 두 태스크가 서로의 타입을 참조해 한쪽만 적용하면 컴파일이 깨진다:

- **Task 11 → Task 7** — `PlayerEmoteView`가 `PlayerLook.SetEmoteView`를 부른다
- **Task 10 → Task 9** — `PlayerEmoteInput`이 `EmoteWheelView`를 참조한다

즉 실제 순서는 `1 → 2 → 3 → 4 → 5 → 6 → 11 → 7 → 8 → 10 → 9 → 12 → 13`이다. 각 태스크 안에도 같은 주의를 적어 뒀다.

---

## 스펙에서 조정한 점

**`PlayerEmoteCamera`를 별도 컴포넌트로 만들지 않고 `PlayerLook`에 넣는다.**

스펙 §6·§7의 컴포넌트 표는 `PlayerEmoteCamera`(오너 전용)를 따로 뒀다. 실측해 보니 `PlayerLook`의 클래스 주석이 **정확히 이 분리를 하지 말라고 못박아** 두었다:

> 회전과 카메라 자세를 한 컴포넌트에 둔 이유는 `m_pitch`·`m_downYaw`·`m_downLookTaken`를 양쪽이 **읽고 쓰기** 때문이다 — 입력은 `HandleLook`이 넣고, 쓰러진 동안의 강제 자세와 기상 시 범위 복귀는 `UpdateCameraPose`가 넣는다. **나누면 이 셋을 두 컴포넌트가 주고받게 된다.**

감정표현 시점도 같은 세 값을 건드린다(몸통 대신 카메라 로컬 yaw 누적, 피치 유지, 카메라 로컬 위치 오프셋). 별도 컴포넌트로 빼면 `UpdateCameraPose`가 매 프레임 `localPosition`·`localEulerAngles`를 통째로 덮어쓰기 때문에 밖에서 얹은 오프셋이 그 프레임에 지워진다 — 화면 흔들림(#477)이 이미 밟은 함정이고, 그래서 흔들림도 `UpdateCameraPose` **안**에서 조립된다.

따라서 `PlayerLook`에 `SetEmoteView(bool)` 진입점 하나를 열고 자세 계산은 기존 조립 지점 안에서 한다. 호출은 `PlayerEmoteView`가 오너 가드 뒤에서 한다. 스펙 §7 표의 `PlayerEmoteCamera` 행은 Task 11에서 문서와 함께 정리한다.

**`EmoteLoadout`은 카탈로그를 모른다.** 스펙 §9는 "카탈로그에 없는 id" 처리를 `EmoteLoadout` 테스트 대상으로 적었으나, asmdef 분리(Task 2) 때문에 `EmoteLoadout`은 `EmoteCatalog`를 참조할 수 없다(그리고 참조하면 순수 로직이 아니게 된다). id 유효성은 **읽는 쪽**(`EmoteWheelView`·`EmoteLoadoutPanel`)이 판정하고, `EmoteLoadout`은 문자열 왕복만 책임진다.

---

## 파일 구조

### 생성

| 경로 | 책임 |
|---|---|
| `Assets/Scripts/Player/Emote/Core/Undercover.Emote.asmdef` | 순수 로직 어셈블리 (테스트 대상) |
| `Assets/Scripts/Player/Emote/Core/EmoteWheelGeometry.cs` | 방향 벡터 → 슬롯 인덱스 |
| `Assets/Scripts/Player/Emote/Core/EmoteLoadout.cs` | 8칸 슬롯 배치 + PlayerPrefs 왕복 |
| `Assets/Tests/EditMode/Undercover.Emote.Tests.asmdef` | EditMode 테스트 어셈블리 |
| `Assets/Tests/EditMode/EmoteWheelGeometryTests.cs` | 각도 매핑 테스트 |
| `Assets/Tests/EditMode/EmoteLoadoutTests.cs` | 슬롯 배치·직렬화 테스트 |
| `Assets/Scripts/Data/EmoteDefinition.cs` | 감정표현 1종 정의 (SO) |
| `Assets/Scripts/Data/EmoteCatalog.cs` | 감정표현 목록 (SO) |
| `Assets/Scripts/Player/Emote/PlayerEmote.cs` | 서버 권위 재생 상태 |
| `Assets/Scripts/Player/Emote/PlayerEmoteView.cs` | 전 피어 애니메이터·말풍선 반영 |
| `Assets/Scripts/Player/Emote/PlayerEmoteInput.cs` | 오너 입력 — 휠 열기·발동·취소 |
| `Assets/Scripts/Player/Emote/EmoteBubbleView.cs` | 머리 위 빌보드 아이콘 |
| `Assets/Scripts/UI/EmoteWheelView.cs` | 8칸 방사형 휠 렌더 |
| `Assets/Scripts/UI/EmoteWheelSlotView.cs` | 휠 칸 하나 — 아이콘·이름·강조 (로비 패널도 재사용) |
| `Assets/Scripts/UI/Panels/EmoteLoadoutPanel.cs` | 로비 슬롯 편집 패널 |

### 수정

| 경로 | 변경 |
|---|---|
| `Assets/Scripts/Editor/PlayerAnimatorControllerBuilder.cs` | Emote 서브스테이트 자동 생성 추가 |
| `Assets/Scripts/Player/Movement/PlayerLook.cs` | 감정표현 3인칭 시점 모드 |
| `Assets/Scripts/Player/Movement/PlayerInputHandler.cs` | `Emote` 액션 배선 |
| `Assets/InputSystem_Actions.inputactions` | `Emote` 액션(키 `T`) 추가 |
| `Assets/Scripts/Player/PlayerState.cs` | `Dance` → `Emote` |
| `docs/GDD.md` | 10-2 enum 갱신 |
| `docs/superpowers/specs/2026-08-06-player-emote-design.md` | §6·§7 컴포넌트 표 조정 |

---

### Task 1: 댄스 애니메이션 에셋 임포트

**Files:**
- Import: `~/Downloads/Human Dance Animations.unitypackage`
- Create: `Assets/Imported/Kevin Iglesias/Human Dance Animations/` (이동 결과)

**Interfaces:**
- Consumes: 없음
- Produces: `Assets/Imported/Kevin Iglesias/Human Dance Animations/Animations/Male/Social/Dance/Steps/HumanM@Dance01.fbx` ~ `HumanM@Dance18.fbx` — Task 4 카탈로그와 Task 5 빌더가 참조

- [ ] **Step 1: 패키지 임포트**

Unity MCP `execute_code`로:

```csharp
string package = System.IO.Path.Combine(
    System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
    "Downloads", "Human Dance Animations.unitypackage");
if (!System.IO.File.Exists(package))
    return "패키지를 찾지 못했습니다: " + package;
UnityEditor.AssetDatabase.ImportPackage(package, false);
return "임포트 시작: " + package;
```

- [ ] **Step 2: 임포트 완료 대기 후 경로 확인**

`editor_state`의 `isImporting`/`isCompiling`이 false가 될 때까지 폴링한 뒤:

```csharp
return UnityEditor.AssetDatabase.IsValidFolder("Assets/Kevin Iglesias/Human Animations")
    ? "임포트 완료" : "임포트 폴더 없음 — 재시도 필요";
```

Expected: `"임포트 완료"`

- [ ] **Step 3: 프로젝트 관례 경로로 이동**

기존 `Assets/Imported/Kevin Iglesias/Human Animations`와 **합치지 않는다** — 패키지에 `HumanM@Idle01.fbx` 중복본이 있어 합치면 충돌한다.

```csharp
string error = UnityEditor.AssetDatabase.MoveAsset(
    "Assets/Kevin Iglesias/Human Animations",
    "Assets/Imported/Kevin Iglesias/Human Dance Animations");
if (!string.IsNullOrEmpty(error))
    return "이동 실패: " + error;
UnityEditor.AssetDatabase.DeleteAsset("Assets/Kevin Iglesias");
UnityEditor.AssetDatabase.Refresh();
return "이동 완료";
```

Expected: `"이동 완료"`

- [ ] **Step 4: 클립 존재 확인**

```csharp
var results = new System.Text.StringBuilder();
for (int i = 1; i <= 18; i++)
{
    string path = $"Assets/Imported/Kevin Iglesias/Human Dance Animations/Animations/Male/Social/Dance/Steps/HumanM@Dance{i:00}.fbx";
    var clip = UnityEditor.AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
    if (clip == null) results.Append($"{i:00} 없음; ");
}
return results.Length == 0 ? "18종 모두 확인" : results.ToString();
```

Expected: `"18종 모두 확인"`

- [ ] **Step 5: 콘솔 에러 확인**

MCP `read_console`로 `types: ["Error"]` 조회. Expected: 임포트 관련 에러 없음.

- [ ] **Step 6: 커밋**

```bash
git add "Assets/Imported/Kevin Iglesias/Human Dance Animations"
git commit -m "$(cat <<'EOF'
Human Dance Animations를 임포트한다 (#219)

패키지가 Assets/Kevin Iglesias/로 풀리므로 프로젝트 관례인 Assets/Imported/
아래로 옮긴다. 기존 Human Animations 폴더와 합치지 않는 이유는 패키지에
HumanM@Idle01.fbx 중복본이 들어 있어서다 — 합치면 같은 경로에 두 파일이 온다.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: 테스트 어셈블리 + `EmoteWheelGeometry`

**Files:**
- Create: `Assets/Scripts/Player/Emote/Core/Undercover.Emote.asmdef`
- Create: `Assets/Scripts/Player/Emote/Core/EmoteWheelGeometry.cs`
- Create: `Assets/Tests/EditMode/Undercover.Emote.Tests.asmdef`
- Test: `Assets/Tests/EditMode/EmoteWheelGeometryTests.cs`

**Interfaces:**
- Consumes: 없음
- Produces:
  - `EmoteWheelGeometry.k_slotCount` → `int` (8)
  - `EmoteWheelGeometry.k_deadZone` → `float` (0.35f)
  - `EmoteWheelGeometry.SlotFromDirection(Vector2 direction, float deadZone = k_deadZone)` → `int` (0~7, 데드존이면 -1)
  - `EmoteWheelGeometry.SlotCenterDegrees(int slot)` → `float` (12시 기준 시계방향 각도)

> **왜 asmdef를 만드는가:** Unity는 `Assembly-CSharp`(asmdef 없는 모든 스크립트가 들어가는 기본 어셈블리)를 **맨 마지막에** 컴파일한다. 그래서 asmdef 어셈블리는 `Assembly-CSharp`를 참조할 수 없고, 테스트 어셈블리(반드시 asmdef여야 한다)가 거기 있는 코드를 볼 수 없다. 순수 로직만 별도 asmdef로 떼면 테스트가 가능해지고, `Assembly-CSharp`는 모든 asmdef를 자동 참조하므로 나머지 코드는 평소대로 쓴다.

- [ ] **Step 1: 로직 어셈블리 정의 생성**

`Assets/Scripts/Player/Emote/Core/Undercover.Emote.asmdef`:

```json
{
    "name": "Undercover.Emote",
    "rootNamespace": "",
    "references": [],
    "includePlatforms": [],
    "excludePlatforms": [],
    "allowUnsafeCode": false,
    "overrideReferences": false,
    "precompiledReferences": [],
    "autoReferenced": true,
    "defineConstraints": [],
    "versionDefines": [],
    "noEngineReferences": false
}
```

`autoReferenced: true`가 핵심이다 — `Assembly-CSharp`가 이 어셈블리를 자동으로 참조하므로 `PlayerEmoteInput` 등에서 `using` 없이 그대로 쓸 수 있다.

- [ ] **Step 2: 테스트 어셈블리 정의 생성**

`Assets/Tests/EditMode/Undercover.Emote.Tests.asmdef`:

```json
{
    "name": "Undercover.Emote.Tests",
    "rootNamespace": "",
    "references": [
        "Undercover.Emote",
        "UnityEngine.TestRunner",
        "UnityEditor.TestRunner"
    ],
    "includePlatforms": ["Editor"],
    "excludePlatforms": [],
    "allowUnsafeCode": false,
    "overrideReferences": true,
    "precompiledReferences": ["nunit.framework.dll"],
    "autoReferenced": false,
    "defineConstraints": ["UNITY_INCLUDE_TESTS"],
    "versionDefines": [],
    "noEngineReferences": false
}
```

- [ ] **Step 3: 실패하는 테스트 작성**

`Assets/Tests/EditMode/EmoteWheelGeometryTests.cs`:

```csharp
using NUnit.Framework;
using UnityEngine;

/// <summary>
/// 휠 방향 → 슬롯 인덱스 매핑. 경계각이 어느 쪽으로 떨어지는지가 조작감을 정하므로
/// 눈으로 확인하기 어려운 이 계산만 테스트로 못박는다. (#219)
/// </summary>
public class EmoteWheelGeometryTests
{
    [Test]
    public void 위쪽은_0번_슬롯이다()
    {
        Assert.AreEqual(0, EmoteWheelGeometry.SlotFromDirection(Vector2.up));
    }

    [Test]
    public void 시계방향으로_인덱스가_증가한다()
    {
        Assert.AreEqual(2, EmoteWheelGeometry.SlotFromDirection(Vector2.right), "오른쪽");
        Assert.AreEqual(4, EmoteWheelGeometry.SlotFromDirection(Vector2.down), "아래");
        Assert.AreEqual(6, EmoteWheelGeometry.SlotFromDirection(Vector2.left), "왼쪽");
    }

    [Test]
    public void 대각선도_제자리에_떨어진다()
    {
        Assert.AreEqual(1, EmoteWheelGeometry.SlotFromDirection(new Vector2(1f, 1f)), "오른위");
        Assert.AreEqual(7, EmoteWheelGeometry.SlotFromDirection(new Vector2(-1f, 1f)), "왼위");
    }

    [Test]
    public void 경계각은_다음_슬롯으로_넘어간다()
    {
        // 22.5도가 0번과 1번의 경계 — 경계 바로 앞뒤가 서로 다른 슬롯이어야 한다
        Assert.AreEqual(0, EmoteWheelGeometry.SlotFromDirection(DirectionAt(22f)), "22도");
        Assert.AreEqual(1, EmoteWheelGeometry.SlotFromDirection(DirectionAt(23f)), "23도");
    }

    [Test]
    public void 한바퀴_돌아도_같은_슬롯이다()
    {
        Assert.AreEqual(
            EmoteWheelGeometry.SlotFromDirection(DirectionAt(30f)),
            EmoteWheelGeometry.SlotFromDirection(DirectionAt(390f)));
    }

    [Test]
    public void 데드존_안에서는_선택이_없다()
    {
        Assert.AreEqual(-1, EmoteWheelGeometry.SlotFromDirection(Vector2.zero), "정중앙");
        Assert.AreEqual(-1, EmoteWheelGeometry.SlotFromDirection(Vector2.up * 0.1f), "데드존 안");
    }

    [Test]
    public void 슬롯_중심각은_45도_간격이다()
    {
        Assert.AreEqual(0f, EmoteWheelGeometry.SlotCenterDegrees(0), 0.001f);
        Assert.AreEqual(45f, EmoteWheelGeometry.SlotCenterDegrees(1), 0.001f);
        Assert.AreEqual(315f, EmoteWheelGeometry.SlotCenterDegrees(7), 0.001f);
    }

    // 12시에서 시계방향으로 degrees만큼 돈 단위 벡터
    private static Vector2 DirectionAt(float degrees)
    {
        float radians = degrees * Mathf.Deg2Rad;
        return new Vector2(Mathf.Sin(radians), Mathf.Cos(radians));
    }
}
```

- [ ] **Step 4: 테스트가 실패하는지 확인**

MCP `run_tests`로 `mode: "EditMode"` 실행.
Expected: `EmoteWheelGeometry` 타입이 없어 **컴파일 에러**. (에러 메시지에 `The name 'EmoteWheelGeometry' does not exist`)

- [ ] **Step 5: 최소 구현 작성**

`Assets/Scripts/Player/Emote/Core/EmoteWheelGeometry.cs`:

```csharp
using UnityEngine;

/// <summary>
/// 감정표현 휠의 기하 계산 — 마우스 방향을 8칸 중 하나로 환산한다. (#219)
///
/// 순수 함수만 두는 이유는 이 계산이 <b>화면에서 검증하기 가장 어려운 부분</b>이기 때문이다.
/// 경계각이 한 칸씩 밀려 있어도 화면에서는 "가끔 엉뚱한 게 나온다"로만 보이고, 그 상태로
/// 휠 UI·입력·네트워크를 다 지나가면 원인을 좁히는 데 오래 걸린다.
///
/// 좌표계는 <b>12시가 0번, 시계방향</b>이다 — 휠 UI가 아이콘을 배치하는 순서와 같아야 하므로
/// <see cref="SlotCenterDegrees"/>를 같은 곳에서 제공한다. 두 곳에서 각자 각도를 계산하면
/// 한쪽만 고쳤을 때 그림과 판정이 조용히 어긋난다.
/// </summary>
public static class EmoteWheelGeometry
{
    /// <summary>휠 칸 수 — 8칸 고정. EmoteLoadout.k_slotCount와 같아야 한다.</summary>
    public const int k_slotCount = 8;

    /// <summary>
    /// 이 반경 안에서는 선택이 없다(정규화 기준). 휠을 열었다가 아무것도 고르지 않고
    /// 그냥 떼는 길을 남기기 위한 것 — 없으면 휠을 여는 순간 무조건 뭔가 발동된다.
    /// </summary>
    public const float k_deadZone = 0.35f;

    private const float k_degreesPerSlot = 360f / k_slotCount;

    /// <summary>
    /// 방향 벡터를 슬롯 인덱스로 환산한다. 데드존 안이면 -1.
    /// 벡터 크기는 정규화된 값(휠 반경 기준)으로 넘길 것 — 데드존 판정에 쓴다.
    /// </summary>
    public static int SlotFromDirection(Vector2 direction, float deadZone = k_deadZone)
    {
        if (direction.magnitude < deadZone)
            return -1;

        // Atan2(x, y)로 넣으면 12시가 0도, 시계방향이 +가 된다 (일반적인 Atan2(y, x)와 축이 바뀐 형태).
        float degrees = Mathf.Atan2(direction.x, direction.y) * Mathf.Rad2Deg;

        // 슬롯 중심을 기준으로 반 칸 밀어 내림하면 경계가 칸 사이에 놓인다.
        // Repeat을 쓰는 이유는 음수 각도(-180~0)와 360 넘김을 한 번에 접기 위해서다.
        float shifted = Mathf.Repeat(degrees + k_degreesPerSlot * 0.5f, 360f);
        return Mathf.FloorToInt(shifted / k_degreesPerSlot) % k_slotCount;
    }

    /// <summary>슬롯의 중심 각도(도) — 12시가 0, 시계방향. 휠 UI의 아이콘 배치가 쓴다.</summary>
    public static float SlotCenterDegrees(int slot) => slot * k_degreesPerSlot;
}
```

- [ ] **Step 6: 테스트 통과 확인**

MCP `run_tests`로 `mode: "EditMode"` 실행.
Expected: `EmoteWheelGeometryTests` 7개 전부 PASS.

- [ ] **Step 7: 커밋**

```bash
git add Assets/Scripts/Player/Emote Assets/Tests
git commit -m "$(cat <<'EOF'
휠 각도 계산을 테스트와 함께 넣는다 (#219)

경계각이 한 칸 밀려도 화면에서는 "가끔 엉뚱한 게 나온다"로만 보여 원인을
좁히기 어렵다. 그래서 이 계산만 순수 함수로 떼어 테스트로 못박았다.

프로젝트 첫 asmdef다. Unity는 Assembly-CSharp를 맨 나중에 컴파일하므로
테스트 어셈블리가 그 안의 코드를 볼 수 없다 — 순수 로직만 별도 어셈블리로
빼야 테스트가 가능하다. autoReferenced를 켜 두어 나머지 코드는 평소대로 쓴다.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 3: `EmoteLoadout` — 8칸 슬롯 배치

**Files:**
- Create: `Assets/Scripts/Player/Emote/Core/EmoteLoadout.cs`
- Test: `Assets/Tests/EditMode/EmoteLoadoutTests.cs`

**Interfaces:**
- Consumes: `EmoteWheelGeometry.k_slotCount`
- Produces:
  - `EmoteLoadout.k_slotCount` → `int` (8)
  - `new EmoteLoadout()` — 전 칸 비어 있음
  - `EmoteLoadout.GetSlot(int slot)` → `string` (없으면 `null`)
  - `EmoteLoadout.SetSlot(int slot, string emoteId)` → `void` (범위 밖이면 무시)
  - `EmoteLoadout.Serialize()` → `string`
  - `EmoteLoadout.Deserialize(string raw)` → `void`
  - `EmoteLoadout.Save()` / `EmoteLoadout.Load()` → `void` (PlayerPrefs)

- [ ] **Step 1: 실패하는 테스트 작성**

`Assets/Tests/EditMode/EmoteLoadoutTests.cs`:

```csharp
using NUnit.Framework;

/// <summary>
/// 로비에서 구성한 8칸 배치의 저장·복원. 사용자가 공들여 맞춘 구성이 조용히 날아가는 것이
/// 이 클래스에서 나올 수 있는 가장 나쁜 버그라 왕복을 테스트로 못박는다. (#219)
/// </summary>
public class EmoteLoadoutTests
{
    [Test]
    public void 새_구성은_전부_비어있다()
    {
        var loadout = new EmoteLoadout();
        for (int slot = 0; slot < EmoteLoadout.k_slotCount; slot++)
            Assert.IsNull(loadout.GetSlot(slot), $"{slot}번 칸");
    }

    [Test]
    public void 넣은_값을_그대로_돌려준다()
    {
        var loadout = new EmoteLoadout();
        loadout.SetSlot(3, "dance01");
        Assert.AreEqual("dance01", loadout.GetSlot(3));
    }

    [Test]
    public void 빈_문자열은_빈_칸으로_취급한다()
    {
        var loadout = new EmoteLoadout();
        loadout.SetSlot(2, "cheer01");
        loadout.SetSlot(2, "");
        Assert.IsNull(loadout.GetSlot(2));
    }

    [Test]
    public void 범위_밖_인덱스는_무시한다()
    {
        var loadout = new EmoteLoadout();
        Assert.DoesNotThrow(() => loadout.SetSlot(-1, "dance01"));
        Assert.DoesNotThrow(() => loadout.SetSlot(EmoteLoadout.k_slotCount, "dance01"));
        Assert.IsNull(loadout.GetSlot(-1));
        Assert.IsNull(loadout.GetSlot(EmoteLoadout.k_slotCount));
    }

    [Test]
    public void 직렬화_왕복이_구성을_보존한다()
    {
        var source = new EmoteLoadout();
        source.SetSlot(0, "dance01");
        source.SetSlot(4, "cheer01");
        source.SetSlot(7, "clap01");

        var restored = new EmoteLoadout();
        restored.Deserialize(source.Serialize());

        Assert.AreEqual("dance01", restored.GetSlot(0));
        Assert.IsNull(restored.GetSlot(1));
        Assert.AreEqual("cheer01", restored.GetSlot(4));
        Assert.AreEqual("clap01", restored.GetSlot(7));
    }

    [Test]
    public void 짧은_문자열을_읽어도_칸_수는_그대로다()
    {
        // 저장 포맷이 바뀌었거나 파일이 잘린 경우 — 남은 칸은 비워 두고 살아남아야 한다
        var loadout = new EmoteLoadout();
        loadout.Deserialize("dance01|cheer01");

        Assert.AreEqual("dance01", loadout.GetSlot(0));
        Assert.AreEqual("cheer01", loadout.GetSlot(1));
        Assert.IsNull(loadout.GetSlot(7));
    }

    [Test]
    public void 긴_문자열은_넘치는_부분을_버린다()
    {
        var loadout = new EmoteLoadout();
        loadout.Deserialize("a|b|c|d|e|f|g|h|i|j");

        Assert.AreEqual("h", loadout.GetSlot(7), "마지막 칸");
        Assert.IsNull(loadout.GetSlot(EmoteLoadout.k_slotCount), "칸 수를 넘은 자리");
        // 넘친 값이 8칸 안으로 밀려 들어오지 않았는지 — 직렬화하면 8칸치만 나와야 한다
        Assert.AreEqual("a|b|c|d|e|f|g|h", loadout.Serialize());
    }

    [Test]
    public void 빈_문자열을_읽으면_전부_빈_칸이다()
    {
        var loadout = new EmoteLoadout();
        loadout.SetSlot(0, "dance01");
        loadout.Deserialize("");

        Assert.IsNull(loadout.GetSlot(0));
    }

    [Test]
    public void null을_읽어도_예외가_없다()
    {
        var loadout = new EmoteLoadout();
        Assert.DoesNotThrow(() => loadout.Deserialize(null));
    }
}
```

- [ ] **Step 2: 테스트가 실패하는지 확인**

MCP `run_tests`로 `mode: "EditMode"` 실행.
Expected: 컴파일 에러 — `The name 'EmoteLoadout' does not exist`

- [ ] **Step 3: 최소 구현 작성**

`Assets/Scripts/Player/Emote/Core/EmoteLoadout.cs`:

```csharp
using System;
using UnityEngine;

/// <summary>
/// 감정표현 휠 8칸의 배치 — 어느 칸에 어떤 감정표현을 넣어 뒀는가. (#219)
///
/// <b>로컬 관심사다.</b> 네트워크로 올리지 않는다 — 남이 볼 필요가 있는 것은 "지금 무엇을
/// 재생 중인가"(<see cref="PlayerEmote"/>)뿐이고, 내가 몇 번 칸에 뭘 넣어 뒀는지는 아무도
/// 알 필요가 없다.
///
/// <b>인덱스가 아니라 id 문자열을 담는 이유:</b> 카탈로그 순서가 바뀌어도 사용자가 공들여
/// 맞춘 구성이 살아남아야 한다. 재생 동기화 쪽은 반대로 인덱스를 쓴다 — 그쪽은 1바이트로
/// 끝내는 것이 이득이고 같은 빌드끼리만 통신하므로 순서가 바뀔 일이 없다.
///
/// MonoBehaviour가 아닌 이유는 <see cref="LoadoutSlots{T}"/>와 같다 — 순수 인덱스 연산이라
/// Unity·Netcode와 무관하게 단위 테스트할 수 있다.
///
/// <b>카탈로그를 모른다.</b> 저장된 id가 지금 카탈로그에 있는지는 읽는 쪽이 판정한다.
/// 여기서 검증하면 이 클래스가 카탈로그에 묶여 순수 로직이 아니게 된다.
/// </summary>
public class EmoteLoadout
{
    /// <summary>휠 칸 수 — EmoteWheelGeometry.k_slotCount와 같아야 한다.</summary>
    public const int k_slotCount = EmoteWheelGeometry.k_slotCount;

    private const string k_prefsKey = "Emote.Loadout";

    // id에 들어갈 수 없는 문자여야 한다. 카탈로그 id는 영숫자·밑줄만 쓴다는 전제.
    private const char k_separator = '|';

    private readonly string[] m_slots = new string[k_slotCount];

    /// <summary>칸에 든 감정표현 id — 비었거나 범위 밖이면 null.</summary>
    public string GetSlot(int slot) =>
        slot >= 0 && slot < k_slotCount ? m_slots[slot] : null;

    /// <summary>칸을 채우거나(id) 비운다(null·빈 문자열). 범위 밖이면 아무것도 하지 않는다.</summary>
    public void SetSlot(int slot, string emoteId)
    {
        if (slot < 0 || slot >= k_slotCount)
            return;

        m_slots[slot] = string.IsNullOrEmpty(emoteId) ? null : emoteId;
    }

    /// <summary>PlayerPrefs에 담을 한 줄 문자열로 만든다.</summary>
    public string Serialize()
    {
        var parts = new string[k_slotCount];
        for (int slot = 0; slot < k_slotCount; slot++)
            parts[slot] = m_slots[slot] ?? string.Empty;

        return string.Join(k_separator.ToString(), parts);
    }

    /// <summary>
    /// 저장 문자열에서 구성을 복원한다. 칸 수가 맞지 않아도 살아남는다 —
    /// 모자라면 남은 칸을 비우고, 넘치면 버린다. 저장 포맷이 바뀌거나 값이 잘렸을 때
    /// 예외로 죽는 대신 기본 구성으로 계속 굴러가는 쪽이 낫다.
    /// </summary>
    public void Deserialize(string raw)
    {
        Array.Clear(m_slots, 0, k_slotCount);

        if (string.IsNullOrEmpty(raw))
            return;

        string[] parts = raw.Split(k_separator);
        int count = Mathf.Min(parts.Length, k_slotCount);
        for (int slot = 0; slot < count; slot++)
            SetSlot(slot, parts[slot]);
    }

    /// <summary>구성을 로컬에 저장한다 — 로비에서 편집을 마칠 때 부른다.</summary>
    public void Save()
    {
        PlayerPrefs.SetString(k_prefsKey, Serialize());
        PlayerPrefs.Save();
    }

    /// <summary>저장된 구성을 읽는다. 저장된 적이 없으면 전 칸이 빈 상태로 남는다.</summary>
    public void Load()
    {
        Deserialize(PlayerPrefs.GetString(k_prefsKey, string.Empty));
    }
}
```

- [ ] **Step 4: 테스트 통과 확인**

MCP `run_tests`로 `mode: "EditMode"` 실행.
Expected: `EmoteLoadoutTests` 9개 + `EmoteWheelGeometryTests` 7개 = 16개 PASS.

- [ ] **Step 5: 커밋**

```bash
git add Assets/Scripts/Player/Emote/Core/EmoteLoadout.cs Assets/Tests/EditMode/EmoteLoadoutTests.cs
git commit -m "$(cat <<'EOF'
휠 8칸 배치와 저장을 테스트와 함께 넣는다 (#219)

사용자가 맞춰 둔 구성이 조용히 날아가는 것이 여기서 나올 수 있는 가장 나쁜
버그라 왕복을 테스트로 못박았다. 칸 수가 맞지 않는 저장값(포맷 변경·잘림)도
예외 대신 빈 칸으로 접고 살아남게 했다.

인덱스가 아니라 id 문자열을 담는다 — 카탈로그 순서가 바뀌어도 구성이 살아야
한다. 재생 동기화는 반대로 인덱스를 쓴다(1바이트, 같은 빌드끼리만 통신).

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 4: `EmoteDefinition` / `EmoteCatalog` + 카탈로그 에셋

**Files:**
- Create: `Assets/Scripts/Data/EmoteDefinition.cs`
- Create: `Assets/Scripts/Data/EmoteCatalog.cs`
- Create: `Assets/Scripts/Data/Emotes/` (개별 정의 에셋 8개)
- Create: `Assets/Scripts/Data/EmoteCatalog.asset`

**Interfaces:**
- Consumes: Task 1의 댄스 클립
- Produces:
  - `EmoteDefinition.Id` → `string`
  - `EmoteDefinition.DisplayName` → `UnityEngine.Localization.LocalizedString`
  - `EmoteDefinition.Icon` → `Sprite`
  - `EmoteDefinition.Clip` → `AnimationClip` (없을 수 있음)
  - `EmoteDefinition.Loop` → `bool`
  - `EmoteDefinition.BubbleSprite` → `Sprite` (없을 수 있음)
  - `EmoteDefinition.DurationSeconds` → `float` (루프면 0)
  - `EmoteCatalog.Count` → `int`
  - `EmoteCatalog.Get(int index)` → `EmoteDefinition` (범위 밖이면 null)
  - `EmoteCatalog.IndexOf(string id)` → `int` (없으면 -1)
  - `EmoteCatalog.IsValidIndex(int index)` → `bool`

- [ ] **Step 1: `EmoteDefinition` 작성**

`Assets/Scripts/Data/EmoteDefinition.cs`:

```csharp
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 감정표현 1종의 정의 — 무엇이 재생되고 무엇이 머리 위에 뜨는가. (#219)
///
/// <b>종류 enum을 두지 않았다.</b> "댄스" / "이모지만" / "춤추면서 이모지" 세 경우가
/// <see cref="Clip"/>·<see cref="BubbleSprite"/>의 <b>있음/없음 조합</b>으로 자연히 나오므로
/// 분기 코드가 생기지 않는다. enum을 두면 조합마다 값이 필요하고, 새 조합이 생길 때마다
/// 소비자 쪽 switch를 전부 찾아 고쳐야 한다.
///
/// 길이 필드를 따로 두지 않는 것도 같은 이유다 — 클립이 이미 길이를 알고 있는데 값을 복사해
/// 두면 클립을 갈아끼웠을 때 한쪽만 남아 조용히 어긋난다.
/// </summary>
[CreateAssetMenu(fileName = "Emote", menuName = "Scriptable Objects/Emote Definition")]
public class EmoteDefinition : ScriptableObject
{
    [Tooltip("안정적 키 — 로비 구성 저장에 쓴다. 영숫자·밑줄만. '|'는 저장 구분자라 쓸 수 없다")]
    [SerializeField]
    private string m_id;

    [Tooltip("휠·로비 목록에 표시할 이름")]
    [SerializeField]
    private LocalizedString m_displayName;

    [Tooltip("휠 칸 아이콘")]
    [SerializeField]
    private Sprite m_icon;

    [Tooltip("재생할 애니메이션. 비우면 애니메이션 없이 이모지만 뜬다")]
    [SerializeField]
    private AnimationClip m_clip;

    [Tooltip("켜면 취소할 때까지 무한 반복, 끄면 클립 1회 후 자동 종료")]
    [SerializeField]
    private bool m_loop = true;

    [Tooltip("머리 위에 띄울 아이콘. 비우면 표시하지 않는다")]
    [SerializeField]
    private Sprite m_bubbleSprite;

    /// <summary>안정적 키 — 로비 구성 저장용. 네트워크에는 카탈로그 인덱스가 실린다.</summary>
    public string Id => m_id;

    public LocalizedString DisplayName => m_displayName;
    public Sprite Icon => m_icon;
    public AnimationClip Clip => m_clip;
    public bool Loop => m_loop;
    public Sprite BubbleSprite => m_bubbleSprite;

    /// <summary>
    /// 자동 종료까지의 길이(초). 루프이거나 클립이 없으면 0 — 서버가 시간으로 끊지 않는다는 뜻이다.
    /// </summary>
    public float DurationSeconds => m_loop || m_clip == null ? 0f : m_clip.length;
}
```

- [ ] **Step 2: `EmoteCatalog` 작성**

`Assets/Scripts/Data/EmoteCatalog.cs`:

```csharp
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 감정표현 전체 목록. (#219)
///
/// <b>리스트 인덱스가 네트워크 계약이다.</b> 재생 동기화는 이 인덱스를 sbyte로 실어 1바이트로
/// 끝낸다 — 모든 피어가 같은 빌드의 같은 에셋을 갖고 있으므로 문자열 id를 보낼 이유가 없다.
///
/// 그래서 <b>배포 후에는 순서를 바꾸지 않는다.</b> 항목은 뒤에 추가하고, 없앨 때는 자리를
/// 비운다(null 허용 — 조회가 무시한다). 개발 중에는 자유롭게 바꿔도 되지만, 순서가 바뀐
/// 빌드끼리는 서로 다른 감정표현을 재생하게 된다.
///
/// 로비 구성 저장은 반대로 id를 쓰므로(<see cref="EmoteLoadout"/>) 순서 변경에 견딘다.
/// 두 표현을 잇는 것이 <see cref="IndexOf"/>다.
/// </summary>
[CreateAssetMenu(fileName = "EmoteCatalog", menuName = "Scriptable Objects/Emote Catalog")]
public class EmoteCatalog : ScriptableObject
{
    [Tooltip("인덱스가 네트워크 계약 — 배포 후에는 순서를 바꾸지 말 것. 추가는 뒤에")]
    [SerializeField]
    private EmoteDefinition[] m_emotes;

    // id → 인덱스. 첫 조회 때 만든다 — 에셋 로드 시점에 만들면 도메인 리로드마다 비용이 든다.
    private Dictionary<string, int> m_indexById;

    public int Count => m_emotes?.Length ?? 0;

    public bool IsValidIndex(int index) => index >= 0 && index < Count && m_emotes[index] != null;

    /// <summary>인덱스로 정의를 얻는다 — 범위 밖이거나 비워 둔 자리면 null.</summary>
    public EmoteDefinition Get(int index) => IsValidIndex(index) ? m_emotes[index] : null;

    /// <summary>id로 인덱스를 찾는다 — 없으면 -1. 로비 구성(id)을 재생(인덱스)으로 잇는 지점.</summary>
    public int IndexOf(string id)
    {
        if (string.IsNullOrEmpty(id) || Count == 0)
            return -1;

        if (m_indexById == null)
        {
            m_indexById = new Dictionary<string, int>(Count);
            for (int i = 0; i < m_emotes.Length; i++)
            {
                if (m_emotes[i] == null || string.IsNullOrEmpty(m_emotes[i].Id))
                    continue;

                // 중복 id는 앞선 것을 남긴다 — 뒤엣것을 덮으면 어느 쪽이 이겼는지 알기 어렵다
                if (!m_indexById.ContainsKey(m_emotes[i].Id))
                    m_indexById.Add(m_emotes[i].Id, i);
            }
        }

        return m_indexById.TryGetValue(id, out int index) ? index : -1;
    }
}
```

- [ ] **Step 3: 컴파일 확인**

MCP `read_console`로 `types: ["Error"]` 조회. Expected: 에러 없음.

- [ ] **Step 4: 정의 에셋 8개 + 카탈로그 생성**

MCP `execute_code`로:

```csharp
const string dance = "Assets/Imported/Kevin Iglesias/Human Dance Animations/Animations/Male/Social/Dance/Steps";
const string emotions = "Assets/Imported/Kevin Iglesias/Human Animations/Animations/Male/Social/Emotions";

var entries = new (string id, string clipPath, bool loop)[]
{
    ("dance01", $"{dance}/HumanM@Dance01.fbx", true),
    ("dance02", $"{dance}/HumanM@Dance02.fbx", true),
    ("dance03", $"{dance}/HumanM@Dance03.fbx", true),
    ("dance04", $"{dance}/HumanM@Dance04.fbx", true),
    ("cheer",   $"{emotions}/HumanM@Cheer01.fbx", false),
    ("clap",    $"{emotions}/HumanM@HandClap01.fbx", false),
    ("angry",   $"{emotions}/HumanM@Angry01.fbx", false),
    ("fear",    $"{emotions}/HumanM@Fear01.fbx", false),
};

if (!UnityEditor.AssetDatabase.IsValidFolder("Assets/Scripts/Data/Emotes"))
    UnityEditor.AssetDatabase.CreateFolder("Assets/Scripts/Data", "Emotes");

var definitions = new EmoteDefinition[entries.Length];
var log = new System.Text.StringBuilder();

for (int i = 0; i < entries.Length; i++)
{
    var (id, clipPath, loop) = entries[i];

    // fbx 안의 AnimationClip은 서브에셋이라 LoadAssetAtPath로는 못 잡는다
    AnimationClip clip = null;
    foreach (var sub in UnityEditor.AssetDatabase.LoadAllAssetsAtPath(clipPath))
    {
        if (sub is AnimationClip candidate && !candidate.name.StartsWith("__preview__"))
        {
            clip = candidate;
            break;
        }
    }
    if (clip == null) { log.Append($"{id}: 클립 없음({clipPath}); "); continue; }

    var def = ScriptableObject.CreateInstance<EmoteDefinition>();
    var so = new UnityEditor.SerializedObject(def);
    so.FindProperty("m_id").stringValue = id;
    so.FindProperty("m_clip").objectReferenceValue = clip;
    so.FindProperty("m_loop").boolValue = loop;
    so.ApplyModifiedPropertiesWithoutUndo();

    string assetPath = $"Assets/Scripts/Data/Emotes/Emote_{id}.asset";
    UnityEditor.AssetDatabase.CreateAsset(def, assetPath);
    definitions[i] = def;
}

var catalog = ScriptableObject.CreateInstance<EmoteCatalog>();
var catalogSo = new UnityEditor.SerializedObject(catalog);
var array = catalogSo.FindProperty("m_emotes");
array.arraySize = definitions.Length;
for (int i = 0; i < definitions.Length; i++)
    array.GetArrayElementAtIndex(i).objectReferenceValue = definitions[i];
catalogSo.ApplyModifiedPropertiesWithoutUndo();

UnityEditor.AssetDatabase.CreateAsset(catalog, "Assets/Scripts/Data/EmoteCatalog.asset");
UnityEditor.AssetDatabase.SaveAssets();
UnityEditor.AssetDatabase.Refresh();

return log.Length == 0 ? "카탈로그 8종 생성 완료" : log.ToString();
```

Expected: `"카탈로그 8종 생성 완료"`

- [ ] **Step 5: 카탈로그 내용 확인**

```csharp
var catalog = UnityEditor.AssetDatabase.LoadAssetAtPath<EmoteCatalog>("Assets/Scripts/Data/EmoteCatalog.asset");
if (catalog == null) return "카탈로그 없음";
var log = new System.Text.StringBuilder($"Count={catalog.Count} ");
for (int i = 0; i < catalog.Count; i++)
{
    var def = catalog.Get(i);
    log.Append($"[{i}]{def?.Id}/{(def?.Clip != null ? "clip" : "noclip")}/{(def != null && def.Loop ? "loop" : "once")} ");
}
log.Append($"IndexOf(cheer)={catalog.IndexOf("cheer")}");
return log.ToString();
```

Expected: `Count=8`, 인덱스 0~3이 `dance01~04`/`clip`/`loop`, `IndexOf(cheer)=4`

- [ ] **Step 6: 커밋**

```bash
git add Assets/Scripts/Data
git commit -m "$(cat <<'EOF'
감정표현 카탈로그를 넣는다 (#219)

종류 enum을 두지 않고 클립·이모지 스프라이트를 각각 옵셔널로 뒀다 — "댄스",
"이모지만", "춤추며 이모지"가 조합으로 나오므로 소비자에 분기가 생기지 않고,
새 조합이 생겨도 switch를 찾아다닐 일이 없다.

리스트 인덱스가 네트워크 계약이다(sbyte 1바이트). 배포 후 순서를 바꾸지 말라는
근거를 카탈로그 주석에 남겼다. 로비 구성은 반대로 id를 쓰므로 순서 변경에
견디고, IndexOf가 두 표현을 잇는다.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 5: 애니메이터 빌더에 Emote 상태 추가

**Files:**
- Modify: `Assets/Scripts/Editor/PlayerAnimatorControllerBuilder.cs`
- Regenerate: `Assets/Animation/Player.controller`

**Interfaces:**
- Consumes: `EmoteCatalog.Count`, `EmoteCatalog.Get(int)`, `EmoteDefinition.Clip`, `EmoteDefinition.Loop`
- Produces: `Player.controller`에 파라미터 `Emote`(bool) · `EmoteIndex`(int), 상태 `Emote_00` ~ `Emote_NN`

- [ ] **Step 1: 상수 추가**

`PlayerAnimatorControllerBuilder.cs`의 기존 상수 블록(`k_combatFolder` 선언 아래)에 추가:

```csharp
    // 감정표현 상태 머신 (#219) — 카탈로그 순서대로 상태를 자동 생성한다.
    // 클립 경로를 여기 적지 않는 이유: 어떤 감정표현이 있는지는 EmoteCatalog 하나가 정하고,
    // 빌더는 그걸 읽기만 한다. 두 곳에 목록을 두면 카탈로그에 추가하고 빌더를 안 고쳐
    // "휠에는 뜨는데 재생은 안 되는" 상태가 난다.
    private const string k_emoteParam = "Emote";
    private const string k_emoteIndexParam = "EmoteIndex";
    private const string k_emoteStatePrefix = "Emote_";
    private const string k_emoteCatalogPath = "Assets/Scripts/Data/EmoteCatalog.asset";
```

- [ ] **Step 2: int 파라미터 헬퍼 추가**

`EnsureBoolParameter` 아래에 추가 (기존 `EnsureFloatParameter`와 같은 형태):

```csharp
    private static void EnsureIntParameter(AnimatorController controller, string name)
    {
        foreach (AnimatorControllerParameter parameter in controller.parameters)
        {
            if (parameter.name == name)
                return;
        }

        controller.AddParameter(name, AnimatorControllerParameterType.Int);
    }
```

- [ ] **Step 3: `SetupEmoteStates` 추가**

`SetupDownStates` 메서드 **아래**에 추가:

```csharp
    /// <summary>
    /// 감정표현 상태를 카탈로그 순서대로 만든다. (#219)
    ///
    /// <b>다운 상태 머신보다 나중에 불러야 한다</b> — 감정표현 중 쓰러질 때 Locomotion을
    /// 경유하지 않고 곧장 Knockdown_Fall로 가야 하는데, 그 전이를 걸려면 Fall 상태가 이미
    /// 있어야 한다. 경유하면 쓰러짐이 한 박자 늦게 보이고, Knockdown의 즉시성은
    /// PlayerAnimationDriver가 주석으로 거듭 강조하는 지점이다.
    ///
    /// 비루프 클립의 종료를 exit time에 맡기지 않는 이유: 재생 여부의 진실은 서버의
    /// NetworkVariable 하나이고, 애니메이터가 자기 판단으로 먼저 빠져나오면 서버는 아직
    /// 재생 중인데 화면만 멈춘 상태가 난다.
    /// </summary>
    private static void SetupEmoteStates(AnimatorController controller)
    {
        var catalog = AssetDatabase.LoadAssetAtPath<EmoteCatalog>(k_emoteCatalogPath);
        if (catalog == null)
        {
            Debug.LogWarning(
                $"[PlayerAnimatorControllerBuilder] 감정표현 카탈로그가 없어 건너뜁니다: {k_emoteCatalogPath}");
            return;
        }

        EnsureBoolParameter(controller, k_emoteParam);
        EnsureIntParameter(controller, k_emoteIndexParam);

        AnimatorStateMachine stateMachine = controller.layers[0].stateMachine;
        RemoveEmoteStates(stateMachine); // 재실행 시 중복 방지 — 카탈로그가 줄었을 수도 있다

        AnimatorState locomotion = FindState(stateMachine, k_stateName);
        AnimatorState fallState = FindState(stateMachine, k_fallState);
        AnimatorState jumpBegin = FindState(stateMachine, k_jumpBeginState);

        int created = 0;
        for (int index = 0; index < catalog.Count; index++)
        {
            EmoteDefinition definition = catalog.Get(index);
            if (definition == null || definition.Clip == null)
                continue; // 이모지 전용 항목은 애니메이터에 자리가 필요 없다

            AnimatorState state = stateMachine.AddState($"{k_emoteStatePrefix}{index:00}");
            state.motion = definition.Clip;
            created++;

            // Locomotion → Emote_i : Emote가 켜지고 인덱스가 맞으면 즉시 진입.
            // 앉기·점프 상태에서는 감정표현을 시작할 수 없으므로(서버가 막는다) 진입은 Locomotion에서만 온다.
            if (locomotion != null)
            {
                AnimatorStateTransition enter = locomotion.AddTransition(state);
                enter.hasExitTime = false;
                enter.duration = 0.15f;
                enter.AddCondition(AnimatorConditionMode.If, 0f, k_emoteParam);
                enter.AddCondition(AnimatorConditionMode.Equals, index, k_emoteIndexParam);

                AnimatorStateTransition exit = state.AddTransition(locomotion);
                exit.hasExitTime = false;
                exit.duration = 0.15f;
                exit.AddCondition(AnimatorConditionMode.IfNot, 0f, k_emoteParam);
            }

            // Emote_i → Knockdown_Fall : 춤추다 맞고 쓰러지는 순간 곧장 넘어간다.
            if (fallState != null)
            {
                AnimatorStateTransition toFall = state.AddTransition(fallState);
                toFall.hasExitTime = false;
                toFall.duration = 0.1f;
                toFall.AddCondition(AnimatorConditionMode.If, 0f, k_downParam);
            }

            // Emote_i → 점프 시작 : 서버가 공중 진입과 함께 감정표현을 끊지만, 애니메이터가
            // Locomotion을 한 번 거치면 이륙 모션이 잘린다.
            if (jumpBegin != null)
            {
                AnimatorStateTransition toJump = state.AddTransition(jumpBegin);
                toJump.hasExitTime = false;
                toJump.duration = 0.1f;
                toJump.AddCondition(AnimatorConditionMode.If, 0f, k_airborneParam);
            }
        }

        Debug.Log($"[PlayerAnimatorControllerBuilder] 감정표현 상태 {created}개 생성");
    }

    // 재실행 시 이전 감정표현 상태를 걷어낸다 — 카탈로그에서 항목을 빼면 그만큼 상태가 줄어야 한다.
    private static void RemoveEmoteStates(AnimatorStateMachine stateMachine)
    {
        foreach (ChildAnimatorState child in stateMachine.states)
        {
            if (child.state != null && child.state.name.StartsWith(k_emoteStatePrefix))
                stateMachine.RemoveState(child.state);
        }
    }
```

> **참고:** 위에서 쓴 `k_downParam`(`"Down"`, 27행) · `k_fallState`(`"Knockdown_Fall"`, 28행) · `k_airborneParam`(`"Airborne"`, 51행) · `k_jumpBeginState`(`"Jump_Begin"`, 52행)는 이 파일에 이미 있는 상수다 — 새로 선언하지 말 것.

- [ ] **Step 4: `Create()`에서 호출**

`Create()`의 `SetupDownStates(controller);` **다음 줄**에 추가:

```csharp
        SetupEmoteStates(controller); // 감정표현 상태 추가/갱신 (#219) — 다운 상태 뒤에 와야 Fall 전이를 걸 수 있다
```

- [ ] **Step 5: 컴파일 확인**

MCP `read_console`로 `types: ["Error"]` 조회. Expected: 에러 없음.

- [ ] **Step 6: 빌더 실행**

MCP `execute_menu_item`으로 `Tools/Player/Create Animator Controller` 실행.

Expected 콘솔: `감정표현 상태 8개 생성` + 기존 `생성/갱신 완료` 로그, 에러 없음.

- [ ] **Step 7: 컨트롤러 검증**

```csharp
var controller = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEditor.Animations.AnimatorController>("Assets/Animation/Player.controller");
if (controller == null) return "컨트롤러 없음";
var log = new System.Text.StringBuilder();
bool hasEmote = false, hasIndex = false;
foreach (var p in controller.parameters)
{
    if (p.name == "Emote") hasEmote = true;
    if (p.name == "EmoteIndex") hasIndex = true;
}
int emoteStates = 0;
foreach (var c in controller.layers[0].stateMachine.states)
    if (c.state != null && c.state.name.StartsWith("Emote_")) emoteStates++;
log.Append($"Emote={hasEmote} EmoteIndex={hasIndex} states={emoteStates}");
return log.ToString();
```

Expected: `Emote=True EmoteIndex=True states=8`

- [ ] **Step 8: 커밋**

```bash
git add Assets/Scripts/Editor/PlayerAnimatorControllerBuilder.cs Assets/Animation/Player.controller
git commit -m "$(cat <<'EOF'
애니메이터에 감정표현 상태를 자동 생성한다 (#219)

카탈로그 순서대로 상태를 만들므로 감정표현을 늘릴 때 빌더 메뉴만 다시 돌리면
된다 — 클립 경로를 빌더에 적지 않은 이유다. 두 곳에 목록을 두면 카탈로그에만
추가하고 "휠에는 뜨는데 재생은 안 되는" 상태가 난다.

다운 상태 머신 뒤에 부른다. 감정표현 중 쓰러질 때 Locomotion을 경유하면
쓰러짐이 한 박자 늦게 보이는데, Fall로 직행하는 전이를 걸려면 Fall 상태가
이미 있어야 한다.

비루프 클립 종료를 exit time에 맡기지 않는다 — 재생 여부의 진실은 서버의
NetworkVariable 하나이고, 애니메이터가 먼저 빠져나오면 서버는 재생 중인데
화면만 멈춘 상태가 난다.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 6: `PlayerEmote` — 서버 권위 재생 상태

**Files:**
- Create: `Assets/Scripts/Player/Emote/PlayerEmote.cs`

프리팹 부착은 이 태스크에서 하지 않는다 — 코드가 다 올라간 뒤 「에디터 배선」에서 한 번에 한다.

**Interfaces:**
- Consumes: `EmoteCatalog`, `PlayerIncapacitation.IsIncapacitated`, `PlayerCrouch.IsCrouching`, `PlayerJump.IsAirborne`, `PlayerCarrier.IsBeingCarried`
- Produces:
  - `PlayerEmote.k_none` → `sbyte` (-1)
  - `PlayerEmote.ActiveEmote` → `sbyte`
  - `PlayerEmote.IsEmoting` → `bool`
  - `PlayerEmote.Catalog` → `EmoteCatalog`
  - `PlayerEmote.OnActiveEmoteChanged` → `event Action<sbyte>`
  - `PlayerEmote.RequestEmote(int index)` → `void` (오너가 부른다)
  - `PlayerEmote.CancelEmote()` → `void` (오너가 부른다)

- [ ] **Step 1: 구현 작성**

`Assets/Scripts/Player/Emote/PlayerEmote.cs`:

```csharp
using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 감정표현 재생 상태 — 서버 권위. (#219)
///
/// <b>이벤트가 아니라 상태로 동기화한다.</b> 일회성 RPC(Baton.PlaySwingRpc 방식)를 쓰지 않는
/// 이유는 이 기능에 <b>취소가 있기</b> 때문이다. 시작과 취소를 각각 RPC로 보내면 하나가
/// 유실되거나 순서가 뒤집혔을 때 남의 화면에서 영원히 춤추는 플레이어가 남고 되돌릴 길이 없다.
/// 취소는 이동 입력마다 일어나므로 드문 사고가 아니다. 상태값 하나면 마지막 값으로 수렴하고,
/// 재생 도중 다가온 피어도 진행 중인 감정표현을 그대로 본다.
///
/// <b>이동 정지 판정을 서버에서 하지 않는다.</b> 이동 권한이 아직 오너에 있어(#55 이전) 서버가
/// 보는 속도가 정확하지 않고, 서버까지 이동을 감지해 끊으면 취소 경로가 둘이 되어 지연 탓에
/// 내 화면보다 남의 화면에서 먼저 끊긴다. 감정표현은 게임 유불리가 0이라 오너를 믿는 비용이
/// 없다 — 서버는 <b>자기가 확실히 아는 것</b>(무력화·앉기·공중·업힘)만 본다.
/// </summary>
[RequireComponent(typeof(PlayerIncapacitation))]
public class PlayerEmote : NetworkBehaviour
{
    /// <summary>재생 중인 감정표현이 없음.</summary>
    public const sbyte k_none = -1;

    [Tooltip("감정표현 목록 — 모든 피어가 같은 에셋을 봐야 한다")]
    [SerializeField]
    private EmoteCatalog m_catalog;

    private readonly NetworkVariable<sbyte> m_activeEmote = new(
        k_none,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    private PlayerIncapacitation m_incapacitation;
    private PlayerCrouch m_crouch;
    private PlayerJump m_jump;
    private PlayerCarrier m_carrier;

    // 서버 전용 — 비루프 클립의 자동 종료 시각. 0이면 시간으로 끊지 않는다(루프).
    private float m_autoStopTime;

    public EmoteCatalog Catalog => m_catalog;
    public sbyte ActiveEmote => m_activeEmote.Value;
    public bool IsEmoting => m_activeEmote.Value != k_none;

    /// <summary>재생 대상이 바뀌었다 — 전 피어에서 발화한다. 인자는 새 인덱스(k_none이면 종료).</summary>
    public event Action<sbyte> OnActiveEmoteChanged;

    private void Awake()
    {
        m_incapacitation = GetComponent<PlayerIncapacitation>();
        m_crouch = GetComponent<PlayerCrouch>();
        m_jump = GetComponent<PlayerJump>();
        m_carrier = GetComponent<PlayerCarrier>();
    }

    public override void OnNetworkSpawn()
    {
        m_activeEmote.OnValueChanged += HandleActiveEmoteChanged;

        // 늦게 합류한 피어도 이미 재생 중인 감정표현을 보게 한다 — 값 구독만으로는 이 시점의
        // 값이 전달되지 않는다(변경이 없었으므로).
        if (IsEmoting)
            OnActiveEmoteChanged?.Invoke(m_activeEmote.Value);

        if (IsServer)
            m_incapacitation.OnIncapacitatedChanged += HandleIncapacitatedChanged;
    }

    public override void OnNetworkDespawn()
    {
        m_activeEmote.OnValueChanged -= HandleActiveEmoteChanged;

        if (IsServer && m_incapacitation != null)
            m_incapacitation.OnIncapacitatedChanged -= HandleIncapacitatedChanged;
    }

    /// <summary>감정표현 발동을 요청한다 — 오너가 부른다. 서버가 조건을 보고 결정한다.</summary>
    public void RequestEmote(int index)
    {
        if (!IsOwner)
            return;

        RequestEmoteServerRpc((sbyte)index);
    }

    /// <summary>재생 중인 감정표현을 끊는다 — 오너가 부른다(이동·공격 등).</summary>
    public void CancelEmote()
    {
        if (!IsOwner)
            return;

        CancelEmoteServerRpc();
    }

    [ServerRpc]
    private void RequestEmoteServerRpc(sbyte index)
    {
        if (m_catalog == null || !m_catalog.IsValidIndex(index))
            return;

        if (!CanStartEmote())
            return;

        m_activeEmote.Value = index;

        float duration = m_catalog.Get(index).DurationSeconds;
        m_autoStopTime = duration > 0f ? Time.time + duration : 0f;
    }

    [ServerRpc]
    private void CancelEmoteServerRpc() => ServerStopEmote();

    // 서버가 확실히 아는 조건만 본다 — 이동 정지 여부는 오너가 판정한다(클래스 주석 참고).
    private bool CanStartEmote()
    {
        if (m_incapacitation != null && m_incapacitation.IsIncapacitated)
            return false;

        if (m_crouch != null && m_crouch.IsCrouching)
            return false;

        if (m_jump != null && m_jump.IsAirborne)
            return false;

        if (m_carrier != null && m_carrier.IsBeingCarried)
            return false;

        return true;
    }

    private void Update()
    {
        if (!IsServer || !IsEmoting)
            return;

        // 비루프 클립의 자동 종료. 애니메이터 exit time에 맡기지 않는 이유는 재생 여부의 진실을
        // 이 값 하나로 유지하기 위해서다 — 둘로 나뉘면 서버는 재생 중인데 화면만 멈춘 상태가 난다.
        if (m_autoStopTime > 0f && Time.time >= m_autoStopTime)
        {
            ServerStopEmote();
            return;
        }

        // 서버가 아는 사유로 끊는다. 오너의 취소 요청과 별개로 도는 이유는, 오너가 응답할 수
        // 없는 상황(다운·업힘)에서도 감정표현이 멈춰야 하기 때문이다.
        if (!CanStartEmote())
            ServerStopEmote();
    }

    private void HandleIncapacitatedChanged(bool incapacitated)
    {
        if (incapacitated)
            ServerStopEmote();
    }

    private void ServerStopEmote()
    {
        if (!IsServer || !IsEmoting)
            return;

        m_activeEmote.Value = k_none;
        m_autoStopTime = 0f;
    }

    private void HandleActiveEmoteChanged(sbyte previous, sbyte current)
    {
        OnActiveEmoteChanged?.Invoke(current);
    }
}
```

- [ ] **Step 2: 컴파일 확인**

MCP `read_console`로 `types: ["Error"]` 조회. Expected: 에러 없음.

- [ ] **Step 3: 커밋**

```bash
git add Assets/Scripts/Player/Emote/PlayerEmote.cs
git commit -m "$(cat <<'EOF'
감정표현 재생 상태를 서버 권위로 둔다 (#219)

일회성 RPC가 아니라 NetworkVariable 하나로 간다. 이 기능에는 취소가 있고
그것도 이동할 때마다 일어나는데, 시작·취소를 각각 RPC로 보내면 하나만 유실돼도
남의 화면에서 영원히 춤추는 플레이어가 남고 되돌릴 길이 없다.

이동 정지 판정은 서버에서 하지 않는다. 이동 권한이 아직 오너에 있어 서버가
보는 속도가 정확하지 않고, 양쪽이 각자 끊으면 지연 탓에 남의 화면에서 먼저
끊긴다. 감정표현은 유불리가 0이라 오너를 믿는 비용이 없다.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 7: `PlayerEmoteView` — 애니메이터 반영

**Files:**
- Create: `Assets/Scripts/Player/Emote/PlayerEmoteView.cs`

**Interfaces:**
- Consumes: `PlayerEmote.OnActiveEmoteChanged`, `PlayerEmote.ActiveEmote`, `PlayerEmote.Catalog`
- Produces: `PlayerEmoteView.OnEmoteVisualChanged` → `event Action<EmoteDefinition>` (null이면 종료) — Task 8의 말풍선이 구독

- [ ] **Step 1: 구현 작성**

`Assets/Scripts/Player/Emote/PlayerEmoteView.cs`:

```csharp
using System;
using UnityEngine;

/// <summary>
/// 재생 상태를 화면에 옮긴다 — 애니메이터 파라미터와 이모지 표시. 전 피어에서 돈다. (#219)
///
/// <b>PlayerAnimationDriver에 넣지 않은 이유:</b> 그 파일은 "이동·자세를 폴링해 애니메이터에
/// 반영한다"는 축이 뚜렷하고 이미 크다. 감정표현을 얹으면 두 가지를 하는 파일이 된다.
///
/// 폴링이 아니라 <b>값 변경 구독</b>인 것도 차이다 — 이동·자세는 매 프레임 값이 바뀌지만
/// 감정표현은 시작·종료 두 순간에만 바뀐다.
/// </summary>
[RequireComponent(typeof(PlayerEmote))]
public class PlayerEmoteView : MonoBehaviour
{
    private static readonly int s_emoteHash = Animator.StringToHash("Emote");
    private static readonly int s_emoteIndexHash = Animator.StringToHash("EmoteIndex");

    [Tooltip("비우면 자식에서 자동으로 찾는다")]
    [SerializeField]
    private Animator m_animator;

    private PlayerEmote m_emote;
    private PlayerLook m_look; // 오너 3인칭 시점 전환 (#219) — 오너에만 있다

    /// <summary>표시할 감정표현이 바뀌었다 — null이면 종료. 말풍선이 구독한다.</summary>
    public event Action<EmoteDefinition> OnEmoteVisualChanged;

    private void Awake()
    {
        m_emote = GetComponent<PlayerEmote>();
        m_look = GetComponent<PlayerLook>();

        if (m_animator == null)
            m_animator = GetComponentInChildren<Animator>();
    }

    private void OnEnable()
    {
        m_emote.OnActiveEmoteChanged += HandleActiveEmoteChanged;
        Apply(m_emote.ActiveEmote); // 활성화 시점의 값을 한 번 반영 — 늦게 켜져도 어긋나지 않게
    }

    private void OnDisable()
    {
        m_emote.OnActiveEmoteChanged -= HandleActiveEmoteChanged;
        Apply(PlayerEmote.k_none); // 꺼질 때 자세를 남기지 않는다
    }

    private void HandleActiveEmoteChanged(sbyte index) => Apply(index);

    private void Apply(sbyte index)
    {
        EmoteCatalog catalog = m_emote.Catalog;
        EmoteDefinition definition = catalog != null ? catalog.Get(index) : null;
        bool active = definition != null;

        if (m_animator != null)
        {
            // 인덱스를 먼저 넣는다 — Emote를 먼저 켜면 그 프레임에 이전 인덱스의 상태로 진입할 수 있다.
            m_animator.SetInteger(s_emoteIndexHash, active ? index : PlayerEmote.k_none);
            m_animator.SetBool(s_emoteHash, active && definition.Clip != null);
        }

        // 3인칭 전환은 오너에서만 의미가 있다 — 남의 카메라는 애초에 꺼져 있다.
        // PlayerLook은 오너 로컬 전용이라 비오너 인스턴스에는 컴포넌트가 있어도 동작하지 않는다.
        if (m_look != null)
            m_look.SetEmoteView(active);

        OnEmoteVisualChanged?.Invoke(definition);
    }
}
```

> **주의:** `PlayerLook.SetEmoteView`는 Task 11에서 만든다. 이 태스크만 먼저 적용하면 컴파일 에러가 난다 — **Task 11을 먼저 하거나**, 두 태스크를 한 번에 진행할 것. 순서를 지키려면 Task 11 → Task 7 순으로 해도 된다.

- [ ] **Step 2: Task 11이 아직이면 그것부터**

`PlayerLook.SetEmoteView`가 없으면 Task 11을 먼저 수행한 뒤 이 태스크로 돌아온다.

- [ ] **Step 3: 컴파일 확인**

MCP `read_console`로 `types: ["Error"]` 조회. Expected: 에러 없음.

- [ ] **Step 4: 커밋**

```bash
git add Assets/Scripts/Player/Emote/PlayerEmoteView.cs
git commit -m "$(cat <<'EOF'
재생 상태를 애니메이터와 화면에 옮긴다 (#219)

PlayerAnimationDriver에 얹지 않았다 — 그 파일은 "이동·자세를 매 프레임 폴링해
반영한다"는 축이 뚜렷하고 이미 크다. 감정표현은 시작·종료 두 순간에만 바뀌므로
폴링이 아니라 값 변경 구독이라는 점에서도 결이 다르다.

EmoteIndex를 Emote보다 먼저 넣는다. 순서를 바꾸면 그 프레임에 이전 인덱스의
상태로 잠깐 진입한다.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 8: `EmoteBubbleView` — 머리 위 이모지

**Files:**
- Create: `Assets/Scripts/Player/Emote/EmoteBubbleView.cs`

**Interfaces:**
- Consumes: `PlayerEmoteView.OnEmoteVisualChanged`, `EmoteDefinition.BubbleSprite`
- Produces: 없음 (표시 전용)

- [ ] **Step 1: 구현 작성**

`Assets/Scripts/Player/Emote/EmoteBubbleView.cs`:

```csharp
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 머리 위 이모지 아이콘 — 감정표현 재생 중에만 뜬다. 전 피어에서 돈다. (#219)
///
/// 이름표(<see cref="PlayerNameTag"/>)와 같은 자리에 서지만 컴포넌트를 합치지 않았다 —
/// 이름표는 거리·음소거·발화를 보고 상시 떠 있고, 이모지는 재생 중에만 뜬다. 표시 조건이
/// 전혀 다른 둘을 한 컴포넌트에 두면 어느 조건이 어느 그림을 끄는지 읽기 어려워진다.
///
/// 빌보드는 매 프레임 카메라를 향하게 돌린다 — 월드 공간에 놓인 스프라이트라 그대로 두면
/// 옆에서 볼 때 납작해진다.
/// </summary>
public class EmoteBubbleView : MonoBehaviour
{
    [Tooltip("이모지를 그릴 Image — 감정표현이 없을 때는 이 오브젝트를 끈다")]
    [SerializeField]
    private Image m_icon;

    [Tooltip("카메라를 향해 돌릴 루트. 비우면 아이콘의 부모를 쓴다")]
    [SerializeField]
    private Transform m_billboardRoot;

    [SerializeField]
    private PlayerEmoteView m_emoteView;

    private Transform m_camera;

    private void Awake()
    {
        if (m_emoteView == null)
            m_emoteView = GetComponentInParent<PlayerEmoteView>();

        if (m_billboardRoot == null && m_icon != null)
            m_billboardRoot = m_icon.transform.parent;

        Hide();
    }

    private void OnEnable()
    {
        if (m_emoteView != null)
            m_emoteView.OnEmoteVisualChanged += HandleEmoteVisualChanged;
    }

    private void OnDisable()
    {
        if (m_emoteView != null)
            m_emoteView.OnEmoteVisualChanged -= HandleEmoteVisualChanged;

        Hide();
    }

    private void LateUpdate()
    {
        if (m_billboardRoot == null || m_icon == null || !m_icon.gameObject.activeSelf)
            return;

        // 카메라는 씬 로드·시점 전환으로 바뀔 수 있어 매번 확인한다 — 캐시만 하면 낡은 참조로 남는다.
        if (m_camera == null)
        {
            if (Camera.main == null)
                return;

            m_camera = Camera.main.transform;
        }

        m_billboardRoot.forward = m_camera.forward;
    }

    private void HandleEmoteVisualChanged(EmoteDefinition definition)
    {
        // 클립만 있고 이모지가 없는 감정표현(순수 댄스)은 머리 위에 아무것도 띄우지 않는다.
        if (definition == null || definition.BubbleSprite == null)
        {
            Hide();
            return;
        }

        if (m_icon == null)
            return;

        m_icon.sprite = definition.BubbleSprite;
        m_icon.gameObject.SetActive(true);
    }

    private void Hide()
    {
        if (m_icon != null)
            m_icon.gameObject.SetActive(false);
    }
}
```

- [ ] **Step 2: 컴파일 확인**

MCP `read_console`로 `types: ["Error"]` 조회. Expected: 에러 없음.

- [ ] **Step 3: 커밋**

```bash
git add Assets/Scripts/Player/Emote/EmoteBubbleView.cs
git commit -m "$(cat <<'EOF'
머리 위 이모지를 붙인다 (#219)

이름표와 같은 자리에 서지만 컴포넌트를 합치지 않았다 — 이름표는 거리·음소거·
발화를 보고 상시 떠 있고 이모지는 재생 중에만 뜬다. 조건이 전혀 다른 둘을 한
곳에 두면 어느 조건이 어느 그림을 끄는지 읽기 어려워진다.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 9: `Emote` 입력 액션 + `PlayerEmoteInput`

**Files:**
- Modify: `Assets/InputSystem_Actions.inputactions`
- Modify: `Assets/Scripts/Player/Movement/PlayerInputHandler.cs`
- Create: `Assets/Scripts/Player/Emote/PlayerEmoteInput.cs`

**Interfaces:**
- Consumes: `PlayerInputHandler.MoveInput`, `PlayerEmote.RequestEmote/CancelEmote`, `EmoteWheelGeometry`, `EmoteLoadout`, `EmoteCatalog.IndexOf`
- Produces:
  - `PlayerInputHandler.OnEmoteWheelOpened` → `event Action`
  - `PlayerInputHandler.OnEmoteWheelClosed` → `event Action`
  - `PlayerEmoteInput.WheelDirection` → `Vector2` (휠 UI가 읽는다, 정규화)
  - `PlayerEmoteInput.IsWheelOpen` → `bool`

- [ ] **Step 1: 입력 액션 추가**

`Assets/InputSystem_Actions.inputactions`의 `Player` 맵 `actions` 배열에 추가 (기존 `ToggleInventory` 항목 형식을 그대로 따른다):

```json
{
    "name": "Emote",
    "type": "Button",
    "id": "0e5c9b7a-3f21-4a6d-9c8e-1b2d4f6a8c30",
    "expectedControlType": "Button",
    "processors": "",
    "interactions": "",
    "initialStateCheck": false
}
```

같은 맵의 `bindings` 배열에 추가:

```json
{
    "name": "",
    "id": "7d4a1e92-5c68-4b03-8e17-9a2f5c3d6b41",
    "path": "<Keyboard>/t",
    "interactions": "",
    "processors": "",
    "groups": "Keyboard&Mouse",
    "action": "Emote",
    "isComposite": false,
    "isPartOfComposite": false
}
```

> `id`는 GUID면 무엇이든 되지만 파일 안에서 유일해야 한다. 충돌하면 Unity가 임포트 시 경고한다.

- [ ] **Step 2: `PlayerInputHandler`에 액션 배선**

SerializeField 블록에 추가 (기존 `m_toggleInventoryAction` 아래):

```csharp
    [SerializeField]
    private InputActionReference m_emoteAction; // T 홀드 — 감정표현 휠 (#219)
```

이벤트 선언에 추가 (`OnJumpPressed` 아래):

```csharp
    public event Action OnEmoteWheelOpened; // T 누름 — 감정표현 휠 열기 (#219)
    public event Action OnEmoteWheelClosed; // T 뗌 — 가리키던 칸 발동 (#219)
```

`SetActionsEnabled`의 배열에 `m_emoteAction` 추가 (배열 마지막 `m_jumpAction,` 다음):

```csharp
            m_jumpAction,
            m_emoteAction,
```

> 주석의 "12개 액션"을 "13개 액션"으로 함께 고칠 것.

`OnNetworkSpawn`의 구독 목록 끝에 추가:

```csharp
        m_emoteAction.action.started += OnEmoteStartedHandler;
        m_emoteAction.action.canceled += OnEmoteCanceledHandler;
```

`OnNetworkDespawn`의 해제 목록에 대응하는 두 줄 추가:

```csharp
        m_emoteAction.action.started -= OnEmoteStartedHandler;
        m_emoteAction.action.canceled -= OnEmoteCanceledHandler;
```

핸들러를 기존 핸들러들 옆에 추가:

```csharp
    // 홀드 방식이라 started/canceled 두 지점을 모두 쓴다 — performed 하나로는 "누르고 있는 동안"을
    // 표현할 수 없다. 크라우치(m_crouchAction)가 같은 형태다.
    private void OnEmoteStartedHandler(InputAction.CallbackContext context) =>
        OnEmoteWheelOpened?.Invoke();

    private void OnEmoteCanceledHandler(InputAction.CallbackContext context) =>
        OnEmoteWheelClosed?.Invoke();
```

- [ ] **Step 3: `PlayerEmoteInput` 작성**

`Assets/Scripts/Player/Emote/PlayerEmoteInput.cs`:

```csharp
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 감정표현 입력 — 휠 열기·조준·발동·취소. 오너 전용. (#219)
///
/// <b>조준을 마우스 위치가 아니라 델타 누적으로 하는 이유:</b> 게임 중에는 커서가 화면 중앙에
/// 잠겨 있어(CursorLock) 절대 좌표를 읽을 수 없다. 휠을 여는 동안만 커서를 푸는 방법도 있지만,
/// 그러면 화면 밖으로 커서가 나가거나 다른 창으로 포커스가 새는 길이 열린다.
///
/// <b>취소를 여기서 판정하는 이유:</b> 이동 정지 여부는 오너가 가장 정확히 안다(이동 권한이
/// 오너에 있다). 서버까지 감지해 끊으면 취소 경로가 둘이 되어 남의 화면에서 먼저 끊긴다.
/// 서버는 오너가 응답할 수 없는 사유(무력화·업힘)만 본다 — PlayerEmote 참고.
/// </summary>
[RequireComponent(typeof(PlayerEmote))]
[RequireComponent(typeof(PlayerInputHandler))]
public class PlayerEmoteInput : MonoBehaviour
{
    // 휠 조준 감도 — 마우스 델타(픽셀)를 정규화 반경 1로 채우는 데 필요한 픽셀 수.
    // 작을수록 조금만 움직여도 끝까지 간다.
    private const float k_aimPixelsToFull = 220f;

    // 이 이상 이동 입력이 들어오면 취소한다. 0으로 두면 스틱 드리프트·키 채터링에도 끊긴다.
    private const float k_moveCancelThreshold = 0.2f;

    [SerializeField]
    private EmoteWheelView m_wheelView;

    private PlayerEmote m_emote;
    private PlayerInputHandler m_inputHandler;

    private readonly EmoteLoadout m_slots = new EmoteLoadout();
    private Vector2 m_aim;

    /// <summary>휠이 열려 있는가 — 휠 UI와 취소 판정이 본다.</summary>
    public bool IsWheelOpen { get; private set; }

    /// <summary>지금 가리키는 방향(정규화, 크기 0~1) — 휠 UI가 강조 표시에 쓴다.</summary>
    public Vector2 WheelDirection => m_aim;

    private void Awake()
    {
        m_emote = GetComponent<PlayerEmote>();
        m_inputHandler = GetComponent<PlayerInputHandler>();
        m_slots.Load();
    }

    private void OnEnable()
    {
        m_inputHandler.OnEmoteWheelOpened += OpenWheel;
        m_inputHandler.OnEmoteWheelClosed += CloseWheelAndFire;
    }

    private void OnDisable()
    {
        m_inputHandler.OnEmoteWheelOpened -= OpenWheel;
        m_inputHandler.OnEmoteWheelClosed -= CloseWheelAndFire;

        if (IsWheelOpen)
            CloseWheel();
    }

    private void Update()
    {
        if (IsWheelOpen)
        {
            AccumulateAim();
            return;
        }

        if (m_emote.IsEmoting && ShouldCancel())
            m_emote.CancelEmote();
    }

    private void OpenWheel()
    {
        if (m_emote.IsEmoting)
            m_emote.CancelEmote(); // 갈아타기: 새로 고르는 동안 이전 것은 끊는다

        IsWheelOpen = true;
        m_aim = Vector2.zero;

        if (m_wheelView != null)
            m_wheelView.Open(m_slots, m_emote.Catalog, this);
    }

    private void CloseWheelAndFire()
    {
        if (!IsWheelOpen)
            return;

        int slot = EmoteWheelGeometry.SlotFromDirection(m_aim);
        CloseWheel();

        if (slot < 0)
            return; // 데드존 — 아무것도 고르지 않고 뗀 것

        string emoteId = m_slots.GetSlot(slot);
        if (string.IsNullOrEmpty(emoteId))
            return; // 빈 칸

        EmoteCatalog catalog = m_emote.Catalog;
        if (catalog == null)
            return;

        // id → 인덱스. 저장된 id가 지금 카탈로그에 없으면(에셋이 빠졌다) 조용히 넘어간다.
        int index = catalog.IndexOf(emoteId);
        if (index < 0)
            return;

        if (!CanStartHere())
            return;

        m_emote.RequestEmote(index);
    }

    private void CloseWheel()
    {
        IsWheelOpen = false;

        if (m_wheelView != null)
            m_wheelView.Close();
    }

    private void AccumulateAim()
    {
        if (Mouse.current == null)
            return;

        m_aim += Mouse.current.delta.ReadValue() / k_aimPixelsToFull;
        m_aim = Vector2.ClampMagnitude(m_aim, 1f);
    }

    // 오너가 아는 시작 조건 — 서버가 보는 것(무력화·앉기·공중·업힘)과 겹치지 않는 부분만 본다.
    private bool CanStartHere() =>
        m_inputHandler.MoveInput.magnitude <= k_moveCancelThreshold;

    // 재생 중 취소 사유. 서버가 아는 것(무력화·업힘)은 서버가 따로 끊으므로 여기서 보지 않는다.
    private bool ShouldCancel() =>
        m_inputHandler.MoveInput.magnitude > k_moveCancelThreshold;
}
```

> **참고:** 점프·공격·상호작용에 의한 취소는 서버의 `CanStartEmote` 재검사(`PlayerEmote.Update`)가 공중·앉기를 잡아 준다. 공격·아이템 사용까지 오너에서 끊고 싶으면 `PlayerInputHandler.OnUseItemStarted`·`OnInteractStarted`를 구독해 `m_emote.CancelEmote()`를 부르는 두 줄을 `OnEnable`/`OnDisable`에 더한다.

- [ ] **Step 4: 컴파일 확인**

MCP `read_console`로 `types: ["Error"]` 조회.
Expected: `EmoteWheelView`가 아직 없어 에러 — Task 10을 먼저 하거나 함께 진행할 것.

- [ ] **Step 5: 커밋 (Task 10 완료 후)**

```bash
git add Assets/InputSystem_Actions.inputactions Assets/Scripts/Player/Movement/PlayerInputHandler.cs Assets/Scripts/Player/Emote/PlayerEmoteInput.cs
git commit -m "$(cat <<'EOF'
감정표현 입력을 붙인다 — T 홀드로 휠 조준 (#219)

조준을 커서 위치가 아니라 마우스 델타 누적으로 한다. 게임 중에는 커서가 화면
중앙에 잠겨 있어 절대 좌표를 읽을 수 없고, 휠을 여는 동안만 커서를 풀면 화면
밖으로 나가거나 포커스가 새는 길이 열린다.

취소는 오너가 판정한다 — 이동 권한이 오너에 있어 정지 여부를 가장 정확히 알고,
서버까지 감지해 끊으면 지연 탓에 남의 화면에서 먼저 끊긴다.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 10: `EmoteWheelView` — 8칸 방사형 휠

**Files:**
- Create: `Assets/Scripts/UI/EmoteWheelView.cs`

**Interfaces:**
- Consumes: `EmoteWheelGeometry.SlotCenterDegrees/SlotFromDirection/k_slotCount`, `EmoteLoadout.GetSlot`, `EmoteCatalog.IndexOf/Get`, `PlayerEmoteInput.WheelDirection`
- Produces:
  - `EmoteWheelView.Open(EmoteLoadout slots, EmoteCatalog catalog, PlayerEmoteInput input)` → `void`
  - `EmoteWheelView.Close()` → `void`

- [ ] **Step 1: 구현 작성**

`Assets/Scripts/UI/EmoteWheelView.cs`:

```csharp
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 감정표현 휠 — 8칸 방사형. 홀드 중에만 떠 있다. (#219)
///
/// <b>PanelBase가 아니다.</b> 패널은 ESC 스택에 쌓이고 열고 닫기가 App.UI를 거치는 '창'인데,
/// 이 휠은 키를 누르고 있는 동안만 떠 있는 조준 보조 표시라 ESC로 닫을 것도, 다른 창과 겹칠
/// 일도 없다. 스택에 넣으면 홀드를 뗀 순간의 닫기와 ESC 처리가 서로를 밟는다.
///
/// 칸 배치 각도를 <see cref="EmoteWheelGeometry"/>에서 받아 오는 이유는 <b>그림과 판정이
/// 같은 값을 봐야</b> 하기 때문이다. 각자 계산하면 한쪽만 고쳤을 때 "가리킨 것과 다른 게
/// 나오는" 어긋남이 조용히 생긴다.
/// </summary>
public class EmoteWheelView : MonoBehaviour
{
    [Tooltip("휠 전체 루트 — 닫을 때 끈다")]
    [SerializeField]
    private GameObject m_root;

    [Tooltip("칸 8개. 인덱스 = 슬롯 번호(0 = 12시, 시계방향)")]
    [SerializeField]
    private EmoteWheelSlotView[] m_slotViews = new EmoteWheelSlotView[EmoteWheelGeometry.k_slotCount];

    [Tooltip("칸 중심이 놓일 반경(px)")]
    [SerializeField]
    private float m_radius = 160f;

    private PlayerEmoteInput m_input;

    private void Awake()
    {
        LayOutSlots();
        Close();
    }

    /// <summary>휠을 띄우고 현재 구성을 채운다. 홀드가 시작될 때 PlayerEmoteInput이 부른다.</summary>
    public void Open(EmoteLoadout slots, EmoteCatalog catalog, PlayerEmoteInput input)
    {
        m_input = input;

        for (int slot = 0; slot < m_slotViews.Length; slot++)
        {
            if (m_slotViews[slot] == null)
                continue;

            // 저장된 id가 지금 카탈로그에 없으면 빈 칸으로 그린다 — EmoteLoadout은 카탈로그를
            // 모르므로(순수 로직) 유효성 판정은 읽는 쪽인 여기 몫이다.
            EmoteDefinition definition = null;
            if (catalog != null)
            {
                int index = catalog.IndexOf(slots?.GetSlot(slot));
                definition = catalog.Get(index);
            }

            m_slotViews[slot].Bind(definition);
            m_slotViews[slot].SetHighlighted(false);
        }

        if (m_root != null)
            m_root.SetActive(true);
    }

    /// <summary>휠을 내린다.</summary>
    public void Close()
    {
        m_input = null;

        if (m_root != null)
            m_root.SetActive(false);
    }

    private void Update()
    {
        if (m_input == null || m_root == null || !m_root.activeSelf)
            return;

        int highlighted = EmoteWheelGeometry.SlotFromDirection(m_input.WheelDirection);
        for (int slot = 0; slot < m_slotViews.Length; slot++)
        {
            if (m_slotViews[slot] != null)
                m_slotViews[slot].SetHighlighted(slot == highlighted);
        }
    }

    // 칸을 반경 위에 균등 배치한다 — 인스펙터에서 손으로 놓으면 판정 각도와 어긋나기 쉽다.
    private void LayOutSlots()
    {
        for (int slot = 0; slot < m_slotViews.Length; slot++)
        {
            if (m_slotViews[slot] == null)
                continue;

            float radians = EmoteWheelGeometry.SlotCenterDegrees(slot) * Mathf.Deg2Rad;
            var position = new Vector2(Mathf.Sin(radians), Mathf.Cos(radians)) * m_radius;
            m_slotViews[slot].SetAnchoredPosition(position);
        }
    }
}
```

- [ ] **Step 2: 칸 뷰 작성**

`Assets/Scripts/UI/EmoteWheelSlotView.cs`:

```csharp
using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// 감정표현 휠의 칸 하나 — 아이콘·이름·강조. (#219)
/// 배치 각도는 EmoteWheelView가 정한다(판정과 같은 값을 써야 하므로).
/// </summary>
public class EmoteWheelSlotView : MonoBehaviour
{
    [SerializeField]
    private RectTransform m_root;

    [SerializeField]
    private Image m_icon;

    [SerializeField]
    private TextMeshProUGUI m_label;

    [Tooltip("가리키는 동안 켜지는 강조 표시")]
    [SerializeField]
    private GameObject m_highlight;

    [Tooltip("빈 칸일 때의 아이콘 투명도")]
    [SerializeField]
    private float m_emptyAlpha = 0.25f;

    // 지금 구독 중인 표시 이름 — 갈아끼울 때 이전 구독을 끊기 위해 들고 있는다
    private UnityEngine.Localization.LocalizedString m_boundName;

    private void Awake()
    {
        if (m_root == null)
            m_root = transform as RectTransform;
    }

    public void SetAnchoredPosition(Vector2 position)
    {
        if (m_root == null)
            m_root = transform as RectTransform;

        if (m_root != null)
            m_root.anchoredPosition = position;
    }

    /// <summary>칸에 감정표현을 채운다 — null이면 빈 칸으로 그린다.</summary>
    public void Bind(EmoteDefinition definition)
    {
        bool filled = definition != null;

        if (m_icon != null)
        {
            m_icon.sprite = filled ? definition.Icon : null;
            m_icon.enabled = filled && definition.Icon != null;

            Color color = m_icon.color;
            color.a = filled ? 1f : m_emptyAlpha;
            m_icon.color = color;
        }

        // LocalizedString은 비동기 조회라 즉시 값이 없을 수 있어 준비되면 채우도록 구독한다.
        // 구독을 갈아끼울 때 이전 것을 반드시 끊는다 — 로비 편집에서는 같은 칸이 계속 다시
        // Bind되므로, 안 끊으면 구독이 쌓여 한 칸에 여러 문구가 번갈아 들어온다.
        UnsubscribeLabel();
        m_boundName = filled ? definition.DisplayName : null;

        if (m_boundName != null)
            m_boundName.StringChanged += SetLabelText;
        else
            SetLabelText(string.Empty);
    }

    private void OnDestroy() => UnsubscribeLabel();

    private void UnsubscribeLabel()
    {
        if (m_boundName == null)
            return;

        m_boundName.StringChanged -= SetLabelText;
        m_boundName = null;
    }

    public void SetHighlighted(bool highlighted)
    {
        if (m_highlight != null)
            m_highlight.SetActive(highlighted);
    }

    private void SetLabelText(string value)
    {
        if (m_label != null)
            m_label.text = value;
    }
}
```

> `StringChanged` 구독을 `m_boundName`으로 들고 있다가 `Bind` 재호출과 `OnDestroy`에서 끊는 것이 위 코드의 요지다. 로비 편집에서는 같은 칸이 계속 다시 `Bind`되므로 안 끊으면 구독이 쌓여 한 칸에 여러 문구가 번갈아 들어온다.

- [ ] **Step 3: 컴파일 확인**

MCP `read_console`로 `types: ["Error"]` 조회. Expected: 에러 없음 (Task 9의 `PlayerEmoteInput`과 함께 컴파일된다).

- [ ] **Step 4: 커밋**

Task 9의 Step 5 커밋에 함께 담거나 별도로:

```bash
git add Assets/Scripts/UI/EmoteWheelView.cs Assets/Scripts/UI/EmoteWheelSlotView.cs
git commit -m "$(cat <<'EOF'
감정표현 휠 UI를 넣는다 (#219)

PanelBase로 만들지 않았다 — 패널은 ESC 스택에 쌓이는 '창'인데 이 휠은 키를
누르고 있는 동안만 뜨는 조준 표시라 ESC로 닫을 것도, 다른 창과 겹칠 일도 없다.
스택에 넣으면 홀드를 뗀 순간의 닫기와 ESC 처리가 서로를 밟는다.

칸 배치 각도를 EmoteWheelGeometry에서 받아 온다. 그림과 판정이 각자 각도를
계산하면 한쪽만 고쳤을 때 "가리킨 것과 다른 게 나오는" 어긋남이 조용히 생긴다.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 11: `PlayerLook`에 감정표현 3인칭 시점

**Files:**
- Modify: `Assets/Scripts/Player/Movement/PlayerLook.cs`

**Interfaces:**
- Consumes: 없음
- Produces: `PlayerLook.SetEmoteView(bool active)` → `void` — Task 7의 `PlayerEmoteView`가 부른다

> **왜 별도 컴포넌트가 아닌가:** 이 파일 상단 클래스 주석 참고 — `m_pitch`·카메라 로컬 자세를 `HandleLook`과 `UpdateCameraPose`가 함께 읽고 쓰기 때문에 분리하면 값을 주고받게 된다. 게다가 `UpdateCameraPose`가 매 프레임 `localPosition`·`localEulerAngles`를 통째로 대입하므로, 밖에서 얹은 오프셋은 그 프레임에 지워진다(화면 흔들림 #477이 같은 이유로 이 메서드 안에서 조립된다).

- [ ] **Step 1: SerializeField 추가**

`[Header("다운(무력화) 시점")]` 블록 **아래**에 추가:

```csharp
    [Header("감정표현 시점 (#219)")]
    [Tooltip("감정표현 재생 중 카메라를 뒤로 뺄 거리(m)")]
    [SerializeField] private float m_emoteCamDistance = 2.5f;

    [Tooltip("감정표현 재생 중 카메라를 위로 올릴 높이(m)")]
    [SerializeField] private float m_emoteCamHeight = 0.4f;

    [Tooltip("1인칭↔감정표현 시점 전환 보간 속도")]
    [SerializeField] private float m_emoteCamLerpSpeed = 6f;

    [Tooltip("3인칭 카메라가 벽을 파고들지 않게 띄울 반경(m)")]
    [SerializeField] private float m_emoteCamProbeRadius = 0.25f;

    [Tooltip("3인칭 카메라 충돌 판정에 쓸 레이어 — 플레이어·트리거는 빼 둘 것")]
    [SerializeField] private LayerMask m_emoteCamCollision = ~0;
```

- [ ] **Step 2: 상태 필드 추가**

`private bool m_downLookTaken;` 아래에 추가:

```csharp
    private bool m_emoteView;       // 감정표현 3인칭 시점이 요청됐는가 (#219)
    private float m_emoteCamBlend;  // 1인칭(0) ↔ 3인칭(1) 보간 진행도
    private float m_emoteYaw;       // 감정표현 중 누적한 카메라 좌우 각 — 몸은 돌리지 않는다
    private int m_ownBodyLayer = -1; // OwnBody 레이어 번호 캐시 (-1 = 아직 조회 전)
```

- [ ] **Step 3: 진입점 추가**

`ApplyOwnerView` 메서드 **아래**에 추가:

```csharp
    /// <summary>
    /// 감정표현 3인칭 시점을 켜고 끈다 — 재생 중에만 켠다. (#219)
    ///
    /// 1인칭에서는 <see cref="ApplyOwnerView"/>가 내 몸을 OwnBody 레이어로 옮겨 내 카메라에서
    /// 걷어내므로, 이걸 켜지 않으면 감정표현을 발동해도 <b>내 화면에는 아무 일도 일어나지 않는다</b>.
    ///
    /// 컬링 마스크를 되살리는 것과 카메라를 빼는 것을 같은 진입점에 묶는 이유: 둘 중 하나만
    /// 걸리면 "몸은 보이는데 얼굴 안쪽이 보이는" 화면이나 "뒤로 빠졌는데 아무것도 없는" 화면이 된다.
    /// </summary>
    public void SetEmoteView(bool active)
    {
        if (m_emoteView == active)
            return;

        m_emoteView = active;

        if (m_playerCamera == null)
            return;

        if (m_ownBodyLayer < 0)
            m_ownBodyLayer = LayerMask.NameToLayer("OwnBody");

        if (m_ownBodyLayer < 0)
            return; // 레이어가 없는 구성(테스트 씬 등) — 카메라만 빠지고 몸은 안 보인다

        int mask = 1 << m_ownBodyLayer;
        if (active)
            m_playerCamera.cullingMask |= mask;
        else
            m_playerCamera.cullingMask &= ~mask;
    }
```

- [ ] **Step 4: `HandleLook`에 분기 추가**

`if (IsProne)` 블록 **다음**, `transform.Rotate(...)` **앞**에 추가:

```csharp
        // 감정표현 중에는 몸을 돌리지 않는다 (#219) — 춤추는 중에 몸통이 돌면 클립이 제자리
        // 회전하는 그림이 되고 그건 남의 화면에도 그대로 간다. 쓰러진 동안과 같은 처리다.
        // 마우스를 움직여도 감정표현이 취소되면 안 되므로 여기서 취소를 걸지 않는다.
        if (m_emoteView)
        {
            m_emoteYaw += m_smoothedLook.x; // 3인칭은 한 바퀴 돌 수 있어야 하므로 범위를 두지 않는다
            m_pitch = Mathf.Clamp(m_pitch - m_smoothedLook.y, m_minPitch, m_maxPitch);
            return;
        }
```

- [ ] **Step 5: `UpdateCameraPose`에 자세 적용**

`Vector3 euler = new Vector3(m_pitch, m_downYaw, 0f);` **앞**에 삽입:

```csharp
        // 감정표현 3인칭 — 카메라를 시선 뒤쪽으로 뺀다. (#219)
        // 여기서 조립하는 이유는 흔들림(#477)과 같다: 이 메서드가 매 프레임 localPosition을
        // 통째로 대입하므로 밖에서 얹은 오프셋은 그 프레임에 지워진다.
        m_emoteCamBlend = Mathf.Lerp(m_emoteCamBlend, m_emoteView ? 1f : 0f, m_emoteCamLerpSpeed * Time.deltaTime);

        if (!m_emoteView && m_emoteCamBlend < 0.01f)
        {
            m_emoteYaw = 0f; // 1인칭으로 완전히 돌아온 뒤에만 각도를 버린다 — 도중에 버리면 화면이 튄다
        }
        else if (m_emoteCamBlend > 0.001f)
        {
            // 붐은 카메라가 보는 방향 기준이다 — 몸통이 아니라 m_emoteYaw를 축으로 돈다.
            Vector3 boom = Quaternion.Euler(0f, m_emoteYaw, 0f)
                * new Vector3(0f, m_emoteCamHeight, -m_emoteCamDistance);

            // 벽을 파고들지 않게 당긴다. SphereCast 1회로만 처리한다 — 맵 교체가 예정돼 있어
            // 여기서 완벽한 충돌 대응을 만들 이유가 없다.
            Vector3 pivot = transform.TransformPoint(localPos);
            Vector3 direction = transform.TransformDirection(boom);
            float distance = direction.magnitude;
            if (distance > 0.001f)
            {
                direction /= distance;
                if (Physics.SphereCast(pivot, m_emoteCamProbeRadius, direction, out RaycastHit hit,
                        distance, m_emoteCamCollision, QueryTriggerInteraction.Ignore))
                {
                    boom = boom.normalized * Mathf.Max(hit.distance - m_emoteCamProbeRadius, 0f);
                }
            }

            localPos += boom * m_emoteCamBlend;
        }
```

그리고 바로 아래의 euler 조립을 다음으로 교체:

```csharp
        // 좌우 각은 쓰러진 동안(m_downYaw)과 감정표현 중(m_emoteYaw) 각각 쓰이며 동시에 켜지지 않는다.
        Vector3 euler = new Vector3(m_pitch, m_downYaw + m_emoteYaw * m_emoteCamBlend, 0f);
```

- [ ] **Step 6: 컴파일 확인**

MCP `read_console`로 `types: ["Error"]` 조회. Expected: 에러 없음.

- [ ] **Step 7: 커밋**

```bash
git add Assets/Scripts/Player/Movement/PlayerLook.cs
git commit -m "$(cat <<'EOF'
감정표현 중 3인칭으로 시점을 뺀다 (#219)

1인칭에서는 ApplyOwnerView가 내 몸을 OwnBody 레이어로 걷어내므로, 이게 없으면
감정표현을 발동해도 내 화면에는 아무 일도 일어나지 않는다.

별도 컴포넌트로 빼지 않았다 — 이 파일 클래스 주석이 이미 짚었듯 m_pitch와
카메라 로컬 자세를 HandleLook과 UpdateCameraPose가 함께 읽고 쓴다. 게다가
UpdateCameraPose가 매 프레임 localPosition을 통째로 대입해서, 밖에서 얹은
오프셋은 그 프레임에 지워진다(화면 흔들림 #477이 같은 이유로 이 안에 있다).

몸통은 돌리지 않고 카메라만 돈다. 춤추는 중에 몸통이 돌면 클립이 제자리 회전하는
그림이 되고 그건 남의 화면에도 그대로 간다. 마우스 이동으로 취소되지 않아야
한다는 요구와도 맞는다.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 12: `EmoteLoadoutPanel` — 로비 슬롯 편집

**Files:**
- Create: `Assets/Scripts/UI/Panels/EmoteLoadoutPanel.cs`

**Interfaces:**
- Consumes: `EmoteLoadout`, `EmoteCatalog`, `PanelBase`
- Produces: 없음 (로비 전용 UI)

- [ ] **Step 1: 구현 작성**

`Assets/Scripts/UI/Panels/EmoteLoadoutPanel.cs`:

```csharp
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 로비에서 감정표현 휠 8칸을 구성하는 패널. (#219)
///
/// 로비 전용인 이유: 로비는 세션당 한 번만 거치는 씬이고(이후 Shop↔Game 루프), 인게임 편집을
/// 열면 라운드 중에 휠 구성이 바뀌는 경우를 재생 상태와 함께 다뤄야 한다. 그 값어치가 아직 없다.
///
/// <b>구성은 네트워크로 올리지 않는다.</b> 남이 알아야 하는 것은 "지금 무엇을 재생 중인가"뿐이고,
/// 내가 몇 번 칸에 뭘 넣었는지는 아무도 볼 일이 없다. PlayerPrefs에 로컬 저장하므로 다음 실행
/// 에도 유지된다.
///
/// 조작: 왼쪽 카탈로그 목록에서 하나 고르고 → 오른쪽 8칸 중 하나를 누르면 그 칸에 들어간다.
/// 선택 없이 칸을 누르면 그 칸을 비운다.
/// </summary>
public class EmoteLoadoutPanel : PanelBase
{
    [Tooltip("감정표현 목록")]
    [SerializeField]
    private EmoteCatalog m_catalog;

    [Tooltip("카탈로그 항목 버튼을 담을 부모")]
    [SerializeField]
    private RectTransform m_catalogContent;

    [Tooltip("카탈로그 항목 버튼 프리팹 — EmoteWheelSlotView + Button")]
    [SerializeField]
    private GameObject m_catalogEntryPrefab;

    [Tooltip("휠 8칸 미리보기 — 인덱스 = 슬롯 번호")]
    [SerializeField]
    private EmoteWheelSlotView[] m_slotViews = new EmoteWheelSlotView[EmoteLoadout.k_slotCount];

    [Tooltip("칸을 누를 버튼 8개 — m_slotViews와 같은 순서")]
    [SerializeField]
    private Button[] m_slotButtons = new Button[EmoteLoadout.k_slotCount];

    [SerializeField]
    private Button m_closeButton;

    private readonly EmoteLoadout m_loadout = new EmoteLoadout();
    private int m_selectedCatalogIndex = -1;

    public override bool CanCloseWithESC => true;
    public override bool IsStackable => true;

    protected override void Awake()
    {
        base.Awake();

        m_loadout.Load();
        BuildCatalogList();
        RefreshSlots();

        for (int slot = 0; slot < m_slotButtons.Length; slot++)
        {
            int captured = slot; // 클로저가 루프 변수를 잡지 않게 복사
            if (m_slotButtons[slot] != null)
                m_slotButtons[slot].onClick.AddListener(() => AssignToSlot(captured));
        }

        if (m_closeButton != null)
            m_closeButton.onClick.AddListener(ClosePanel);
    }

    public override void ClosePanel()
    {
        // 닫을 때 저장한다 — 칸을 누를 때마다 저장하면 디스크 쓰기가 잦고, 저장 버튼을 따로 두면
        // 안 누르고 나가는 길이 생긴다.
        m_loadout.Save();
        base.ClosePanel();
    }

    private void BuildCatalogList()
    {
        if (m_catalog == null || m_catalogContent == null || m_catalogEntryPrefab == null)
            return;

        for (int index = 0; index < m_catalog.Count; index++)
        {
            EmoteDefinition definition = m_catalog.Get(index);
            if (definition == null)
                continue;

            GameObject entry = Instantiate(m_catalogEntryPrefab, m_catalogContent);

            EmoteWheelSlotView view = entry.GetComponent<EmoteWheelSlotView>();
            if (view != null)
                view.Bind(definition);

            int captured = index;
            Button button = entry.GetComponent<Button>();
            if (button != null)
                button.onClick.AddListener(() => SelectCatalogEntry(captured));
        }
    }

    private void SelectCatalogEntry(int catalogIndex)
    {
        m_selectedCatalogIndex = catalogIndex;
    }

    private void AssignToSlot(int slot)
    {
        EmoteDefinition definition = m_catalog != null ? m_catalog.Get(m_selectedCatalogIndex) : null;

        // 고른 것이 없으면 그 칸을 비운다 — 별도의 '지우기' 조작을 만들지 않기 위한 규칙이다.
        m_loadout.SetSlot(slot, definition != null ? definition.Id : null);
        RefreshSlots();
    }

    private void RefreshSlots()
    {
        for (int slot = 0; slot < m_slotViews.Length; slot++)
        {
            if (m_slotViews[slot] == null)
                continue;

            EmoteDefinition definition = null;
            if (m_catalog != null)
                definition = m_catalog.Get(m_catalog.IndexOf(m_loadout.GetSlot(slot)));

            m_slotViews[slot].Bind(definition);
        }
    }
}
```

- [ ] **Step 2: 컴파일 확인**

MCP `read_console`로 `types: ["Error"]` 조회. Expected: 에러 없음.

- [ ] **Step 3: 커밋**

```bash
git add Assets/Scripts/UI/Panels/EmoteLoadoutPanel.cs
git commit -m "$(cat <<'EOF'
로비에서 휠 8칸을 구성하는 패널을 넣는다 (#219)

로비 전용이다. 인게임 편집을 열면 라운드 중 구성 변경을 재생 상태와 함께
다뤄야 하는데 그 값어치가 아직 없다.

구성은 네트워크로 올리지 않는다 — 남이 알아야 할 것은 "지금 무엇을 재생 중인가"
뿐이고 몇 번 칸에 뭘 넣었는지는 아무도 볼 일이 없다.

저장은 닫을 때 한 번 한다. 칸마다 저장하면 디스크 쓰기가 잦고, 저장 버튼을
따로 두면 안 누르고 나가는 길이 생긴다.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 13: `PlayerState` 정리 + 문서 갱신

**Files:**
- Modify: `Assets/Scripts/Player/PlayerState.cs`
- Modify: `docs/GDD.md` (10-2)
- Modify: `docs/superpowers/specs/2026-08-06-player-emote-design.md` (§6·§7)

**Interfaces:**
- Consumes: 없음
- Produces: 없음

- [ ] **Step 1: enum 값 변경**

`Assets/Scripts/Player/PlayerState.cs`의 `Dance,`를 다음으로 교체:

```csharp
    Emote,
```

그리고 클래스 주석 끝에 한 줄 추가:

```csharp
/// Dance는 Emote로 일반화했다 (#219) — 개별 감정표현 종류는 이 enum이 아니라 EmoteCatalog의
/// id로 다룬다. 에셋을 추가할 때마다 enum과 GDD를 함께 고치지 않기 위해서다.
```

- [ ] **Step 2: 컴파일 확인**

MCP `read_console`로 `types: ["Error"]` 조회.
Expected: 에러 없음 (이 enum은 코드 어디에서도 참조되지 않는다 — 선언만 존재).

확인용:

```bash
grep -rn "PlayerState" Assets/Scripts --include=*.cs
```

Expected: `Assets/Scripts/Player/PlayerState.cs` 한 줄만.

- [ ] **Step 3: GDD 10-2 갱신**

`docs/GDD.md`의 10-2 Player 상태 enum 표에서 `Dance`를 `Emote`로 바꾸고 근거 한 줄을 붙인다:

> `Emote` — 감정표현 재생 중. 개별 종류(댄스·환호·박수 등)는 enum이 아니라 `EmoteCatalog`(ScriptableObject)의 id로 다룬다. 애니메이션 에셋을 추가해도 코드·네트워크 계약이 바뀌지 않게 하기 위함이다. (#219)

- [ ] **Step 4: 설계 문서의 컴포넌트 표 조정**

`docs/superpowers/specs/2026-08-06-player-emote-design.md` §6 표에서 `PlayerEmoteCamera` 행을 지우고, §7 첫 문단을 다음으로 교체:

```markdown
`PlayerLook.ApplyOwnerView`가 자기 몸을 `OwnBody` 레이어로 옮겨 자기 카메라에서 컬링한다. 그대로 두면 감정표현을 발동해도 **내 화면에는 아무 일도 일어나지 않는다** — 머리 위 이모지도 시야 밖이다.

3인칭 전환은 **`PlayerLook` 안에서** 한다(`SetEmoteView(bool)`). 별도 컴포넌트로 빼지 않는 이유는 그 파일의 클래스 주석이 이미 짚어 둔 것과 같다 — `m_pitch`·카메라 로컬 자세를 `HandleLook`과 `UpdateCameraPose`가 함께 읽고 쓰므로 나누면 값을 주고받게 된다. 게다가 `UpdateCameraPose`가 매 프레임 `localPosition`·`localEulerAngles`를 통째로 대입하므로 밖에서 얹은 오프셋은 그 프레임에 지워진다(화면 흔들림 #477이 같은 이유로 그 메서드 안에서 조립된다). 호출은 `PlayerEmoteView`가 한다.
```

- [ ] **Step 5: 커밋**

```bash
git add Assets/Scripts/Player/PlayerState.cs docs/GDD.md docs/superpowers/specs/2026-08-06-player-emote-design.md
git commit -m "$(cat <<'EOF'
PlayerState.Dance를 Emote로 일반화한다 (#219)

개별 감정표현 종류는 enum이 아니라 EmoteCatalog의 id로 다룬다 — 에셋을 추가할
때마다 enum과 GDD를 함께 고치지 않기 위해서다. 이 enum은 지금 코드 어디에서도
참조되지 않아(선언만 존재) 이름만 정리하고 사용처를 새로 만들지는 않았다.

설계 문서의 PlayerEmoteCamera 행도 함께 정리했다. PlayerLook이 카메라 자세를
매 프레임 통째로 대입하는 구조라 별도 컴포넌트로는 오프셋이 유지되지 않는다.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

## 에디터 배선 (사용자 몫)

코드가 다 올라간 뒤 Unity Editor에서 손으로 해야 하는 것들이다. **Claude가 대신 하기 어렵거나(프리팹 시각 배치) 취향이 들어가는 부분**이라 목록만 남긴다.

1. **`Player.prefab`에 컴포넌트 부착** — `PlayerEmote`(카탈로그 지정) · `PlayerEmoteView` · `PlayerEmoteInput`(휠 뷰 지정)
2. **`PlayerInputHandler`의 `m_emoteAction`** — `InputSystem_Actions`의 `Player/Emote` 지정
3. **머리 위 이모지** — 이름표 앵커 하위에 Image 하나 + `EmoteBubbleView` 부착
4. **휠 UI** — 인게임 캔버스에 루트 + 칸 8개(`EmoteWheelSlotView`) 배치, `EmoteWheelView`에 배열 채우기
5. **로비 패널** — 로비 캔버스에 `EmoteLoadoutPanel` + 카탈로그 목록/8칸 미리보기, 로비 화면에 여는 버튼
6. **아이콘 스프라이트** — 카탈로그 8종의 `m_icon`·`m_bubbleSprite`. 임시 도형으로 시작해도 된다
7. **표시 이름** — 8종의 `m_displayName`을 Localization 테이블 키에 연결
8. **`m_emoteCamCollision`** — 3인칭 카메라 충돌 레이어에서 Player·트리거 제외

## Play 모드 검증 (사용자 몫)

Multiplayer Play Mode로 2인 이상 띄우고 확인한다:

1. `T`를 누르면 휠이 뜨고, 마우스를 움직이면 가리키는 칸이 강조된다
2. 떼면 그 감정표현이 재생되고 **카메라가 3인칭으로 빠져 내 몸이 보인다**
3. **상대 화면에서도** 같은 동작과 머리 위 이모지가 보인다
4. 이동하면 즉시 끊기고 1인칭으로 돌아온다 — 상대 화면에서도 함께 끊긴다
5. 춤추는 중에 맞아 쓰러지면 Locomotion을 경유하지 않고 곧장 쓰러진다
6. 재생 도중 다른 플레이어가 다가와도 춤추는 중임이 보인다 (늦은 합류)
7. 로비에서 구성을 바꾸고 게임을 껐다 켜도 구성이 유지된다
8. 비루프 감정표현(환호·박수)은 클립이 끝나면 스스로 종료된다

## 완료 기준 대응 (#219)

| 이슈 완료 기준 | 대응 |
|---|---|
| 감정표현 애니메이션 1종 이상 + 이모지 표시 동작 | Task 4 (8종) · Task 5 · Task 7 · Task 8 |
| 전 피어 동기화 | Task 6 (`NetworkVariable<sbyte>`) · Task 7 |
| 상태 enum 정리 방향 확정 (GDD 10-2 반영) | Task 13 |
