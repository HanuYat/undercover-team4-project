# 치장 아이템 착용 외형 구현 계획 (#818 A 슬라이스)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 플레이어가 로비에서 모자·수염을 골라 쓰면, 그 선택이 계정에 저장되고 남의 화면·CCTV에 첫 프레임부터 옳게 보인다.

**Architecture:** 로봇 색(#432·#790)이 이미 완성해 둔 "GameSettings(값) → CosmeticsSaveService(계정) → LobbyPlayerEntry(명부) → 서버가 스폰 때 심기 → 전 피어가 그린다" 경로에 값을 하나 더 태운다. 적용만 새 컴포넌트 `PlayerAccessories`로 가른다 — 색은 머티리얼 틴트고 치장은 자식 오브젝트 생성이라 메커니즘이 다르다.

**Tech Stack:** Unity 6000.3.15f1 · Netcode for GameObjects · Unity Cloud Save · UniTask

설계 정본: [2026-08-24-cosmetic-accessories-design.md](../specs/2026-08-24-cosmetic-accessories-design.md)

## Global Constraints

- **네이밍 (CLAUDE.md / GDD 10-5):** `m_` private 인스턴스 · `s_` static · `k_` const · `I` 인터페이스 · `On` 이벤트 · public은 PascalCase.
- **아키텍처 (docs/architecture.md):** 전역 매니저 접근은 `App` 파사드만. `FindFirstObjectByType` 금지, 신규 `static Instance` 금지, 씬 전환은 `App.LoadScene`만.
- **인덱스 0 = 안 씀.** 실제 아이템은 1번부터. 카탈로그 0번 원소는 프리팹 참조 없이 비워 둔다.
- **부착물 프리팹에 콜라이더 금지.** `PlayerInteractor` 조준 레이·`NpcResistState` 타격 `OverlapSphere`·진압봉 판정에 걸린다.
- **순수 코스메틱.** 판정·이동·시야에 영향을 주지 않는다. 서버는 값을 검증하지 않는다(색과 같은 방침).
- **`EAccessorySlot` 순서를 바꾸지 말 것** — 계정에 인덱스로 저장된다. 추가는 뒤에만.
- **주석은 짧게.** 근거가 길면 설계 문서를 가리킨다.

## 테스트에 관하여 — 이 계획에 단위 테스트가 없는 이유

이 저장소의 EditMode 테스트는 `Assets/Tests/EditMode/Undercover.Emote.Tests.asmdef` 하나뿐이고, 대상인 Emote 코어는 **자기 asmdef**(`Undercover.Emote.asmdef`)를 갖고 있다. 이번에 손대는 `GameSettings`·`CosmeticsSaveService`·`SessionRoster`는 전부 `Assembly-CSharp`에 있는데, **asmdef는 `Assembly-CSharp`를 참조할 수 없다**(의존이 단방향이다). 따라서 이들을 asmdef 테스트로 덮으려면 먼저 코어를 별도 asmdef로 떼어내야 하고, 그건 이 이슈의 범위를 넘고 팀 합의가 필요하다.

대신 각 태스크는 **Unity `execute_code`로 도는 검증 스니펫**과 **사람이 눈으로 보는 절차**를 갖는다. 스니펫은 그대로 붙여 넣어 돌릴 수 있고, 기대 출력이 적혀 있다.

> ⚠ 코드를 고친 뒤에는 **반드시 컴파일 에러 0을 먼저 확인**하고 다음 단계로 간다 (CLAUDE.md). 새 타입은 컴파일이 끝나야 쓸 수 있다.

## 파일 구조

| 파일 | 책임 | 상태 |
|---|---|---|
| `Assets/Scripts/Core/Enums.cs` | `EAccessorySlot` 추가 | 수정 |
| `Assets/Scripts/Player/View/AccessorySet.cs` | 슬롯별 인덱스 묶음 · 네트워크 직렬화 | 생성 |
| `Assets/Scripts/Data/Appearance/AccessoryCatalog.cs` | 슬롯 → 프리팹 배열. 인덱스가 곧 전송값 | 생성 |
| `Assets/Scripts/Core/GameSettings.cs` | 값 보관 · PlayerPrefs 캐시 · 변경 이벤트 | 수정 |
| `Assets/Scripts/Save/CosmeticsSaveService.cs` | 계정 저장 + v1 마이그레이션 | 수정 |
| `Assets/Scripts/Scene/LobbyPlayerEntry.cs` | 명부에 치장 값 태우기 | 수정 |
| `Assets/Scripts/Network/Session/SessionRoster.cs` | 자기 보고에 치장 포함 · 변경 시 재보고 | 수정 |
| `Assets/Scripts/Player/View/PlayerAccessories.cs` | 서버가 심고 전 피어가 붙인다 | 생성 |
| `Assets/Scripts/UI/Cosmetics/AccessoryPickerView.cs` | 한 슬롯의 선택 칸들 | 생성 |

---

### Task 1: 데이터 타입 — `EAccessorySlot` · `AccessorySet` · `AccessoryCatalog`

**Files:**
- Modify: `Assets/Scripts/Core/Enums.cs` (`EBodyPart` 선언 근처, 201행 뒤)
- Create: `Assets/Scripts/Player/View/AccessorySet.cs`
- Create: `Assets/Scripts/Data/Appearance/AccessoryCatalog.cs`

**Interfaces:**
- Produces: `EAccessorySlot { Headwear, FacialHair }` · `AccessorySet`(`byte this[EAccessorySlot]`, `static AccessorySet FromSettings()`, `INetworkSerializable`, `IEquatable<AccessorySet>`) · `AccessoryCatalog`(`int CountOf(EAccessorySlot)`, `GameObject Get(EAccessorySlot, int)`)
- Consumes: `GameSettings.GetAccessory` (Task 2에서 생긴다 — 이 태스크는 그 호출부를 **주석 처리한 채** 두지 않는다. Task 2를 먼저 하거나, 두 태스크를 한 커밋으로 묶어도 된다. 순서는 1 → 2 권장이며 그 사이 컴파일이 깨진다.)

> ⚠ `AccessorySet.FromSettings()`가 `GameSettings.GetAccessory`를 부르므로 **Task 1만 적용하면 컴파일이 깨진다.** Task 2까지 마친 뒤 컴파일을 확인한다. 두 태스크는 한 커밋으로 묶는다.

- [ ] **Step 1: `EAccessorySlot`을 Enums.cs에 추가**

`Assets/Scripts/Core/Enums.cs`의 `EBodyPart` 선언 바로 뒤에 넣는다.

```csharp
/// <summary>
/// 플레이어 치장 부위 (#818). 저장·전파 배열의 길이가 곧 이 enum의 크기다.
/// <b>순서를 바꾸지 말 것</b> — 계정에 인덱스로 저장된다. 추가는 뒤에만.
/// </summary>
public enum EAccessorySlot
{
    Headwear,
    FacialHair,
}
```

- [ ] **Step 2: `AccessorySet` 생성**

`Assets/Scripts/Player/View/AccessorySet.cs`:

```csharp
using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 한 사람의 치장 — 슬롯별 카탈로그 인덱스 묶음 (#818). <see cref="PlayerColorSet"/>과 같은 모양이다.
/// <b>0은 "안 씀"</b>이라 기본값이 그대로 안전한 상태다 — 색과 달리 미배정 플래그가 필요 없다.
/// </summary>
[Serializable]
public struct AccessorySet : INetworkSerializable, IEquatable<AccessorySet>
{
    public byte Headwear;
    public byte FacialHair;

    public byte this[EAccessorySlot slot]
    {
        get { return slot == EAccessorySlot.Headwear ? Headwear : FacialHair; }
        set
        {
            if (slot == EAccessorySlot.Headwear)
                Headwear = value;
            else
                FacialHair = value;
        }
    }

    /// <summary>지금 내가 고른 치장 — 값의 출처는 <see cref="GameSettings"/> 하나다.</summary>
    public static AccessorySet FromSettings() =>
        new AccessorySet
        {
            Headwear = ToIndex(GameSettings.GetAccessory(EAccessorySlot.Headwear)),
            FacialHair = ToIndex(GameSettings.GetAccessory(EAccessorySlot.FacialHair)),
        };

    public void NetworkSerialize<T>(BufferSerializer<T> serializer)
        where T : IReaderWriter
    {
        serializer.SerializeValue(ref Headwear);
        serializer.SerializeValue(ref FacialHair);
    }

    public bool Equals(AccessorySet other) =>
        Headwear == other.Headwear && FacialHair == other.FacialHair;

    public override bool Equals(object obj) => obj is AccessorySet other && Equals(other);

    public override int GetHashCode() => Headwear | (FacialHair << 8);

    // 카탈로그 길이는 여기서 보지 않는다 — 범위 밖 인덱스는 카탈로그가 null로 잘라 "안 씀"이 된다
    private static byte ToIndex(int index) => (byte)Mathf.Clamp(index, 0, byte.MaxValue);
}
```

- [ ] **Step 3: `AccessoryCatalog` 생성**

`Assets/Scripts/Data/Appearance/AccessoryCatalog.cs`:

```csharp
using System;
using UnityEngine;

/// <summary>
/// 슬롯별 치장 프리팹 목록 (#818) — <b>배열 인덱스가 곧 네트워크로 오가는 값</b>이다.
/// 인덱스 0은 "안 씀"이라 0번 원소는 비워 둔다. 수치·목록을 코드에 박지 않는다 (GDD 10-4).
/// </summary>
[CreateAssetMenu(fileName = "AccessoryCatalog", menuName = "Undercover/Player/Accessory Catalog")]
public class AccessoryCatalog : ScriptableObject
{
    [Serializable]
    private class SlotEntry
    {
        public EAccessorySlot Slot;

        [Tooltip("0번은 '안 씀'이라 비워 둘 것 — 실제 아이템은 1번부터. 콜라이더가 있는 프리팹을 넣지 말 것")]
        public GameObject[] Prefabs;
    }

    [SerializeField] private SlotEntry[] m_slots = new SlotEntry[0];

    /// <summary>그 슬롯의 항목 수 — 0번("안 씀")을 포함한 길이다. 목록이 없으면 1(안 씀만).</summary>
    public int CountOf(EAccessorySlot slot)
    {
        SlotEntry entry = Find(slot);
        return entry == null || entry.Prefabs == null ? 1 : Mathf.Max(1, entry.Prefabs.Length);
    }

    /// <summary>붙일 프리팹 — 0이거나 범위 밖이면 null("안 씀")이다.</summary>
    public GameObject Get(EAccessorySlot slot, int index)
    {
        if (index <= 0)
            return null;

        SlotEntry entry = Find(slot);
        if (entry == null || entry.Prefabs == null || index >= entry.Prefabs.Length)
            return null;

        return entry.Prefabs[index];
    }

    private SlotEntry Find(EAccessorySlot slot)
    {
        for (int i = 0; i < m_slots.Length; i++)
            if (m_slots[i] != null && m_slots[i].Slot == slot)
                return m_slots[i];

        return null;
    }
}
```

- [ ] **Step 4: Task 2를 마친 뒤 컴파일 확인**

Task 2 완료 후 Unity에서:

```
refresh_unity(compile="request", mode="force", scope="scripts")
read_console(action="get", types=["error"])
```

기대: 에러 0건.

- [ ] **Step 5: 타입이 실제로 올라왔는지 확인**

`execute_code`:

```csharp
var asm = System.AppDomain.CurrentDomain.GetAssemblies()
    .SelectMany(a => { try { return a.GetTypes(); } catch { return new System.Type[0]; } }).ToArray();
var sb = new System.Text.StringBuilder();
foreach (var n in new[]{"EAccessorySlot","AccessorySet","AccessoryCatalog"}) {
  var t = asm.FirstOrDefault(x => x.Name == n);
  sb.AppendLine(n + " = " + (t == null ? "<없음>" : t.Assembly.GetName().Name));
}
sb.AppendLine("슬롯 수 = " + System.Enum.GetValues(typeof(EAccessorySlot)).Length);
return sb.ToString();
```

기대:
```
EAccessorySlot = Assembly-CSharp
AccessorySet = Assembly-CSharp
AccessoryCatalog = Assembly-CSharp
슬롯 수 = 2
```

- [ ] **Step 6: 빈 카탈로그 에셋을 미리 만든다**

Task 5가 `Player.prefab`에 물릴 대상이 먼저 있어야 한다. 내용은 Task 7에서 채운다.

```csharp
if (!UnityEditor.AssetDatabase.IsValidFolder("Assets/Settings/Player"))
    UnityEditor.AssetDatabase.CreateFolder("Assets/Settings", "Player");

string path = "Assets/Settings/Player/AccessoryCatalog.asset";
if (UnityEditor.AssetDatabase.LoadAssetAtPath<AccessoryCatalog>(path) == null)
{
    UnityEditor.AssetDatabase.CreateAsset(ScriptableObject.CreateInstance<AccessoryCatalog>(), path);
    UnityEditor.AssetDatabase.SaveAssets();
}
return "카탈로그 = " + path + " · 슬롯 항목 수(Headwear) = "
     + UnityEditor.AssetDatabase.LoadAssetAtPath<AccessoryCatalog>(path).CountOf(EAccessorySlot.Headwear);
```

기대: `카탈로그 = Assets/Settings/Player/AccessoryCatalog.asset · 슬롯 항목 수(Headwear) = 1`
(비어 있어도 "안 씀" 하나는 항상 있다.)

- [ ] **Step 7: 커밋** (Task 2와 함께)

---

### Task 2: `GameSettings`에 치장 값 추가

**Files:**
- Modify: `Assets/Scripts/Core/GameSettings.cs`

**Interfaces:**
- Consumes: `EAccessorySlot` (Task 1)
- Produces: `GameSettings.GetAccessory(EAccessorySlot) : int` · `SetAccessory(EAccessorySlot, int)` · `ApplyAccessories(IReadOnlyList<int>)` · `event Action<EAccessorySlot> OnAccessoryChanged`

색(`s_playerColors` 계열)과 **같은 모양으로** 붙인다. 참고 위치: 37행 `k_playerColorKeyPrefix`, 90행 `s_playerColors`, 349~400행 색 API, 414행 `ColorKey`, 418행 `LoadPlayerColors`.

- [ ] **Step 1: 상수·저장소 필드 추가**

37행 `k_playerColorKeyPrefix` 선언 바로 뒤:

```csharp
    private const string k_accessoryKeyPrefix = "settings.accessory.";
```

90행 `s_playerColors` 선언 바로 뒤:

```csharp
    private static readonly int[] s_accessories = new int[Enum.GetValues(typeof(EAccessorySlot)).Length];
```

- [ ] **Step 2: 색 API 블록 뒤에 치장 API 추가**

`ApplyPlayerColors` 메서드가 끝나는 자리 바로 뒤에 넣는다.

```csharp
    /// <summary>내 치장이 바뀌었다 — 로비 명부 보고·선택 칸이 되읽는다. 인자는 바뀐 슬롯. (#818)</summary>
    public static event Action<EAccessorySlot> OnAccessoryChanged;

    /// <summary>그 슬롯에 쓴 카탈로그 인덱스 — <b>0은 안 씀</b>. 카탈로그 길이는 여기서 모른다.</summary>
    public static int GetAccessory(EAccessorySlot slot) => s_accessories[(int)slot];

    public static void SetAccessory(EAccessorySlot slot, int index)
    {
        int clamped = Mathf.Max(0, index);
        if (s_accessories[(int)slot] == clamped)
            return;

        s_accessories[(int)slot] = clamped;
        PlayerPrefs.SetInt(AccessoryKey(slot), clamped);
        OnAccessoryChanged?.Invoke(slot);
    }

    /// <summary>클라우드에서 받은 한 벌을 적용한다 — 캐시에도 남긴다. (CosmeticsSaveService)</summary>
    public static void ApplyAccessories(IReadOnlyList<int> accessories)
    {
        if (accessories == null)
            return;

        foreach (EAccessorySlot slot in Enum.GetValues(typeof(EAccessorySlot)))
        {
            int index = (int)slot;
            if (index >= accessories.Count)
                continue;

            int clamped = Mathf.Max(0, accessories[index]);
            s_accessories[index] = clamped;
            PlayerPrefs.SetInt(AccessoryKey(slot), clamped);
            OnAccessoryChanged?.Invoke(slot);
        }
    }
```

- [ ] **Step 3: 캐시 키와 로더 추가**

414행 `ColorKey` 바로 뒤:

```csharp
    // 색과 같은 자리 규칙 — 계정별로 갈라 둔다 (#818)
    private static string AccessoryKey(EAccessorySlot slot) =>
        k_accessoryKeyPrefix + s_account + "." + slot;
```

`LoadPlayerColors` 메서드 바로 뒤:

```csharp
    // 캐시에서 전 슬롯을 다시 읽어 적용한다. 저장된 값이 없으면 0(안 씀)이다.
    private static void LoadAccessories()
    {
        foreach (EAccessorySlot slot in Enum.GetValues(typeof(EAccessorySlot)))
        {
            s_accessories[(int)slot] = PlayerPrefs.GetInt(AccessoryKey(slot), 0);
            OnAccessoryChanged?.Invoke(slot);
        }
    }
```

- [ ] **Step 4: `LoadPlayerColors()` 호출부마다 `LoadAccessories()`를 나란히 부른다**

`LoadPlayerColors()`를 호출하는 자리를 전부 찾는다:

```bash
grep -n "LoadPlayerColors()" Assets/Scripts/Core/GameSettings.cs
```

각 호출 바로 다음 줄에 `LoadAccessories();`를 넣는다. (계정 전환 `LoadAccountSettings`와 초기화 경로 둘 다에 있어야 계정을 갈아탈 때 치장도 같이 갈린다.)

또한 `OnPlayerColorChanged = null;`로 이벤트를 비우는 자리(472행 근처)가 있으면 그 옆에 `OnAccessoryChanged = null;`을 넣는다.

- [ ] **Step 5: 컴파일 확인**

```
refresh_unity(compile="request", mode="force", scope="scripts")
read_console(action="get", types=["error"])
```

기대: 에러 0건.

- [ ] **Step 6: 값이 오가는지 확인**

`execute_code`:

```csharp
var sb = new System.Text.StringBuilder();
int seen = -1;
System.Action<EAccessorySlot> h = s => { seen = (int)s; };
GameSettings.OnAccessoryChanged += h;

sb.AppendLine("초기값 Headwear = " + GameSettings.GetAccessory(EAccessorySlot.Headwear));
GameSettings.SetAccessory(EAccessorySlot.Headwear, 3);
sb.AppendLine("설정 후 = " + GameSettings.GetAccessory(EAccessorySlot.Headwear) + " · 이벤트 슬롯 = " + seen);

seen = -1;
GameSettings.SetAccessory(EAccessorySlot.Headwear, 3);   // 같은 값 — 이벤트가 나면 안 된다
sb.AppendLine("같은 값 재설정 시 이벤트 = " + (seen == -1 ? "안 남(정상)" : "남(버그)"));

GameSettings.SetAccessory(EAccessorySlot.Headwear, -5);  // 음수 — 0으로 잘려야 한다
sb.AppendLine("음수 입력 후 = " + GameSettings.GetAccessory(EAccessorySlot.Headwear));

GameSettings.OnAccessoryChanged -= h;
GameSettings.SetAccessory(EAccessorySlot.Headwear, 0);   // 뒷정리
return sb.ToString();
```

기대:
```
초기값 Headwear = 0
설정 후 = 3 · 이벤트 슬롯 = 0
같은 값 재설정 시 이벤트 = 안 남(정상)
음수 입력 후 = 0
```

- [ ] **Step 7: 커밋** (Task 1 + Task 2)

```bash
git add Assets/Scripts/Core/Enums.cs Assets/Scripts/Player/View/AccessorySet.cs \
        Assets/Scripts/Data/Appearance/AccessoryCatalog.cs Assets/Scripts/Core/GameSettings.cs
git commit -m "치장 슬롯 값과 카탈로그를 둔다 (#818)"
```

---

### Task 3: 계정 저장 + v1 마이그레이션

**Files:**
- Modify: `Assets/Scripts/Save/CosmeticsSaveService.cs`

**Interfaces:**
- Consumes: `GameSettings.GetAccessory` · `GameSettings.ApplyAccessories` (Task 2)
- Produces: `CosmeticsSaveData.Accessories : int[]` · `CosmeticsSaveData.k_version == 2`

> ⚠ **이 태스크의 핵심은 마이그레이션이다.** `ReadAsync`는 지금 버전이 다르면 레코드를 통째로 무시한다. 그대로 2로 올리면 **기존 계정의 색이 날아간다.**

- [ ] **Step 1: `CosmeticsSaveData`에 필드 추가하고 버전을 올린다**

파일 맨 아래 `CosmeticsSaveData` 클래스를 이렇게 바꾼다:

```csharp
/// <summary>계정에 올리는 커스터마이징 한 벌 (#432 후속). 필드를 늘리면 <see cref="k_version"/>을 올릴 것.</summary>
[Serializable]
public class CosmeticsSaveData
{
    // v2에서 치장(Accessories)이 추가됐다 — v1 레코드는 색만 읽어 살린다 (CosmeticsSaveService.ReadAsync)
    public const int k_version = 2;

    public int Version = k_version;

    /// <summary>인덱스 = <see cref="EBodyPart"/>, 값 = 팔레트 색 인덱스.</summary>
    public int[] Colors;

    /// <summary>인덱스 = <see cref="EAccessorySlot"/>, 값 = 카탈로그 인덱스(0 = 안 씀). v1에는 없다.</summary>
    public int[] Accessories;
}
```

- [ ] **Step 2: `Capture()`가 치장도 담게 한다**

기존 `Capture()`를 이렇게 바꾼다:

```csharp
    private static CosmeticsSaveData Capture()
    {
        var parts = (EBodyPart[])Enum.GetValues(typeof(EBodyPart));
        var colors = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            colors[(int)parts[i]] = GameSettings.GetPlayerColor(parts[i]);

        var slots = (EAccessorySlot[])Enum.GetValues(typeof(EAccessorySlot));
        var accessories = new int[slots.Length];
        for (int i = 0; i < slots.Length; i++)
            accessories[(int)slots[i]] = GameSettings.GetAccessory(slots[i]);

        return new CosmeticsSaveData { Colors = colors, Accessories = accessories };
    }
```

- [ ] **Step 3: 버전 검사를 마이그레이션 경로로 바꾼다**

`ReadAsync` 안의 이 블록을

```csharp
            // 포맷이 어긋나면 읽지 않는다 — 색이 조용히 뒤섞이는 쪽이 기본색보다 나쁘다.
            if (data == null || data.Version != CosmeticsSaveData.k_version || data.Colors == null)
            {
                Debug.LogWarning(
                    $"[커스터마이징] 포맷이 달라 무시한다 — 저장 {data?.Version}, 현재 {CosmeticsSaveData.k_version}"
                );
                return null;
            }
```

이렇게 바꾼다:

```csharp
            // 모르는(미래) 포맷은 읽지 않는다 — 색이 조용히 뒤섞이는 쪽이 기본색보다 나쁘다.
            // 반대로 <b>옛 포맷은 버리지 않는다</b> (#818): v1은 색만 있으므로 색을 살리고 치장은 기본값으로 둔다.
            // 통째로 무시하면 치장을 추가했다는 이유로 기존 계정의 색이 날아간다.
            if (data == null || data.Colors == null || data.Version < 1 || data.Version > CosmeticsSaveData.k_version)
            {
                Debug.LogWarning(
                    $"[커스터마이징] 읽을 수 없는 포맷이라 무시한다 — 저장 {data?.Version}, 현재 {CosmeticsSaveData.k_version}"
                );
                return null;
            }

            if (data.Version < CosmeticsSaveData.k_version)
                Debug.Log($"[커스터마이징] v{data.Version} 레코드를 읽었다 — 색만 복원하고 치장은 기본값으로 둔다");
```

- [ ] **Step 4: `RestoreAsync`가 치장도 적용하게 한다**

`RestoreAsync` 끝부분의

```csharp
        Apply(() => GameSettings.ApplyPlayerColors(data.Colors));
```

를 이렇게 바꾼다:

```csharp
        Apply(() =>
        {
            GameSettings.ApplyPlayerColors(data.Colors);
            GameSettings.ApplyAccessories(data.Accessories); // v1 레코드면 null — 그쪽에서 무시한다
        });
```

- [ ] **Step 5: 치장이 바뀔 때도 저장을 예약한다**

`Hook()`을 이렇게 바꾼다:

```csharp
    private static void Hook()
    {
        if (s_hooked)
            return;

        s_hooked = true;
        GameSettings.OnPlayerColorChanged += HandleColorChanged;
        GameSettings.OnAccessoryChanged += HandleAccessoryChanged;
    }
```

`HandleColorChanged` 바로 뒤에 추가:

```csharp
    private static void HandleAccessoryChanged(EAccessorySlot part)
    {
        if (!s_applying)
            QueueSave();
    }
```

- [ ] **Step 6: 컴파일 확인**

```
refresh_unity(compile="request", mode="force", scope="scripts")
read_console(action="get", types=["error"])
```

기대: 에러 0건.

- [ ] **Step 7: 마이그레이션을 실제 JSON으로 검증**

`execute_code` — v1 레코드(치장 필드 없음)를 그대로 파싱해 색이 살아남는지 본다:

```csharp
var sb = new System.Text.StringBuilder();

// v1 — Accessories 필드가 아예 없는 옛 레코드
string v1 = "{\"Version\":1,\"Colors\":[2,5,7]}";
var a = JsonUtility.FromJson<CosmeticsSaveData>(v1);
sb.AppendLine("v1 파싱: Version=" + a.Version
  + " Colors=[" + string.Join(",", a.Colors.Select(c => c.ToString()).ToArray()) + "]"
  + " Accessories=" + (a.Accessories == null ? "null(정상)" : "비어있지 않음"));
sb.AppendLine("  마이그레이션 통과? " + (a.Colors != null && a.Version >= 1 && a.Version <= CosmeticsSaveData.k_version));

// v2 왕복
string v2 = JsonUtility.ToJson(new CosmeticsSaveData { Colors = new[]{1,2,3}, Accessories = new[]{4,5} });
var b = JsonUtility.FromJson<CosmeticsSaveData>(v2);
sb.AppendLine("v2 왕복: " + v2);
sb.AppendLine("  Accessories=[" + string.Join(",", b.Accessories.Select(c => c.ToString()).ToArray()) + "]");

// 미래 버전은 거부되어야 한다
var c = JsonUtility.FromJson<CosmeticsSaveData>("{\"Version\":99,\"Colors\":[1,1,1]}");
sb.AppendLine("v99 거부? " + (c.Version > CosmeticsSaveData.k_version));
return sb.ToString();
```

기대:
```
v1 파싱: Version=1 Colors=[2,5,7] Accessories=null(정상)
  마이그레이션 통과? True
v2 왕복: {"Version":2,"Colors":[1,2,3],"Accessories":[4,5]}
  Accessories=[4,5]
v99 거부? True
```

- [ ] **Step 8: 커밋**

```bash
git add Assets/Scripts/Save/CosmeticsSaveService.cs
git commit -m "치장을 계정에 저장하고 v1 레코드를 살려 읽는다 (#818)"
```

---

### Task 4: 명부 전파

**Files:**
- Modify: `Assets/Scripts/Scene/LobbyPlayerEntry.cs`
- Modify: `Assets/Scripts/Network/Session/SessionRoster.cs` (79·88·166·188행 근처)

**Interfaces:**
- Consumes: `AccessorySet` (Task 1) · `GameSettings.OnAccessoryChanged` (Task 2)
- Produces: `LobbyPlayerEntry.Accessories : AccessorySet`

- [ ] **Step 1: 명부 항목에 필드를 추가한다**

`LobbyPlayerEntry.cs`의 `Colors` 선언 뒤에:

```csharp
    /// <summary>치장 슬롯별 카탈로그 인덱스 (#818) — 색과 같이 순수 코스메틱이라 서버가 검증하지 않는다.</summary>
    public AccessorySet Accessories;
```

`NetworkSerialize`의 `Colors.NetworkSerialize(serializer);` 뒤에:

```csharp
        Accessories.NetworkSerialize(serializer);
```

`Equals`의 `&& Colors.Equals(other.Colors);`를 이렇게 바꾼다:

```csharp
        && Colors.Equals(other.Colors)
        && Accessories.Equals(other.Accessories);
```

> ⚠ `Equals`에 빠뜨리면 `NetworkList`가 "값이 안 바뀌었다"로 보고 갱신을 통째로 버린다 — 복제도 `OnListChanged`도 일어나지 않는다 (#430 주석 참고).

- [ ] **Step 2: 자기 보고에 치장을 싣는다**

`SessionRoster.cs`의 `BuildSelf()`에서 `Colors = PlayerColorSet.FromSettings(),` 뒤에:

```csharp
            Accessories = AccessorySet.FromSettings(),
```

- [ ] **Step 3: 치장이 바뀌면 재보고한다**

79행 `GameSettings.OnPlayerColorChanged += HandlePlayerColorChanged;` 뒤:

```csharp
        GameSettings.OnAccessoryChanged += HandleAccessoryChanged;
```

88행 `GameSettings.OnPlayerColorChanged -= HandlePlayerColorChanged;` 뒤:

```csharp
        GameSettings.OnAccessoryChanged -= HandleAccessoryChanged;
```

166행 `private void HandlePlayerColorChanged(EBodyPart _) => ReportSelf();` 뒤:

```csharp
    private void HandleAccessoryChanged(EAccessorySlot _) => ReportSelf();
```

- [ ] **Step 4: 컴파일 확인**

```
refresh_unity(compile="request", mode="force", scope="scripts")
read_console(action="get", types=["error"])
```

기대: 에러 0건.

- [ ] **Step 5: 직렬화 왕복과 Equals를 확인**

`execute_code`:

```csharp
var sb = new System.Text.StringBuilder();
var a = new LobbyPlayerEntry { ClientId = 1, MicMuted = false,
    Colors = new PlayerColorSet { Head = 1, Torso = 2, Legs = 3 },
    Accessories = new AccessorySet { Headwear = 4, FacialHair = 5 } };
var b = a;
sb.AppendLine("같은 값 Equals = " + a.Equals(b) + " (True 기대)");

b.Accessories.Headwear = 9;
sb.AppendLine("치장만 다를 때 Equals = " + a.Equals(b) + " (False 기대 — True면 명부 갱신이 버려진다)");

var writer = new Unity.Netcode.FastBufferWriter(64, Unity.Collections.Allocator.Temp);
var s1 = new Unity.Netcode.BufferSerializer<Unity.Netcode.BufferSerializerWriter>(
    new Unity.Netcode.BufferSerializerWriter(writer));
a.NetworkSerialize(s1);
sb.AppendLine("직렬화 바이트 = " + writer.Length);
writer.Dispose();
return sb.ToString();
```

기대:
```
같은 값 Equals = True (True 기대)
치장만 다를 때 Equals = False (False 기대 — True면 명부 갱신이 버려진다)
직렬화 바이트 = <0보다 큰 수>
```

- [ ] **Step 6: 커밋**

```bash
git add Assets/Scripts/Scene/LobbyPlayerEntry.cs Assets/Scripts/Network/Session/SessionRoster.cs
git commit -m "치장을 로비 명부에 태운다 (#818)"
```

---

### Task 5: `PlayerAccessories` — 서버가 심고 전 피어가 붙인다

**Files:**
- Create: `Assets/Scripts/Player/View/PlayerAccessories.cs`
- Modify: `Assets/Prefabs/Player.prefab` (컴포넌트 추가 + 카탈로그 연결)

**Interfaces:**
- Consumes: `AccessorySet`·`AccessoryCatalog` (Task 1) · `LobbyPlayerEntry.Accessories` (Task 4) · `App.Game.Roster`
- Produces: 없음 (말단 소비자)

- [ ] **Step 1: 컴포넌트를 만든다**

`Assets/Scripts/Player/View/PlayerAccessories.cs`:

```csharp
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 플레이어 치장 (#818) — 순수 코스메틱. <b>서버가 스폰 시점에 심고</b> 전 피어가 각자 붙인다.
///
/// 값의 주인이 서버인 이유는 <see cref="PlayerCosmetics"/>와 같다 (#790): 오너는 스폰 메시지를 받은
/// 뒤에야 쓸 수 있어 스폰 페이로드에 실을 방법이 없고, 그러면 남의 로봇이 맨머리로 한 번 보인 뒤
/// 왕복 지연만큼 늦게 모자가 생긴다.
///
/// <b>미배정 가드가 없다</b> — 인덱스 0이 "안 씀"이라 기본값 자체가 안전한 상태다. 색은 0이 곧 첫
/// 색이라 그 구분이 불가능해 별도 플래그를 둬야 했다.
///
/// 부착물은 <see cref="NetworkObject"/>가 아니다 — 각 피어가 복제된 인덱스를 보고 로컬로 만든다.
/// </summary>
public class PlayerAccessories : NetworkBehaviour
{
    [Tooltip("치장 카탈로그 — 로비 선택 칸과 반드시 같은 에셋을 물릴 것")]
    [SerializeField] private AccessoryCatalog m_catalog;

    [Tooltip("부착 기준 본 — 3인칭 몸통 리그의 머리. 1인칭 팔 리그를 물리지 말 것")]
    [SerializeField] private Transform m_headBone;

    private readonly NetworkVariable<AccessorySet> m_accessories = new NetworkVariable<AccessorySet>(
        default,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    // 슬롯별로 지금 붙어 있는 인스턴스 — 값이 바뀌면 지우고 다시 만든다
    private readonly GameObject[] m_spawned =
        new GameObject[System.Enum.GetValues(typeof(EAccessorySlot)).Length];

    public override void OnNetworkSpawn()
    {
        // 여기서 쓴 값은 스폰 페이로드에 실려 나간다 — 그래서 남의 화면도 첫 프레임부터 옳다 (#790)
        if (IsServer)
            m_accessories.Value = ResolveSpawnAccessories();

        if (IsOwner)
        {
            GameSettings.OnAccessoryChanged += HandleOwnerAccessoryChanged;

            // 안전망 — 명부 보고가 스폰을 앞지르지 못한 경합에서만 한 박자 늦게 고쳐진다.
            // 값이 같으면 NetworkVariable이 스스로 무시하므로 정상 경로에서는 대역폭을 먹지 않는다.
            if (!IsServer)
                ReportAccessoriesRpc(AccessorySet.FromSettings());
        }

        m_accessories.OnValueChanged += HandleAccessoriesChanged;
        Apply(); // late-join은 값을 복제만 받고 OnValueChanged를 못 받는다
    }

    public override void OnNetworkDespawn()
    {
        m_accessories.OnValueChanged -= HandleAccessoriesChanged;

        // 오너만 구독했지만 무조건 뗀다 — 아니면 죽은 로봇을 가리키는 static 구독이 쌓인다
        GameSettings.OnAccessoryChanged -= HandleOwnerAccessoryChanged;
    }

    /// <summary>
    /// 서버가 아는 이 플레이어의 치장 — 명부가 로비 입장 때 받아 둔 값이다.
    /// 명부가 없거나(세션 없이 씬 직접 Play) 보고가 아직 안 닿았으면 오너는 로컬 설정으로,
    /// 남은 기본값(전부 안 씀)으로 떨어진다 — 틀린 것을 입히는 것보다 맨머리가 낫다.
    /// </summary>
    private AccessorySet ResolveSpawnAccessories()
    {
        SessionRoster roster = App.Game.Roster;
        if (roster != null && roster.TryGetEntry(OwnerClientId, out LobbyPlayerEntry entry))
            return entry.Accessories;

        return IsOwner ? AccessorySet.FromSettings() : default;
    }

    private void HandleOwnerAccessoryChanged(EAccessorySlot _)
    {
        if (!IsOwner || !IsSpawned)
            return;

        AccessorySet set = AccessorySet.FromSettings();
        if (IsServer)
            m_accessories.Value = set; // 호스트 자신 — RPC를 돌 필요가 없다
        else
            ReportAccessoriesRpc(set);
    }

    // 값의 주인이 서버라 원격 오너는 보고만 한다. 코스메틱이라 서버가 검증하지 않는다.
    [Rpc(SendTo.Server)]
    private void ReportAccessoriesRpc(AccessorySet set) => m_accessories.Value = set;

    private void HandleAccessoriesChanged(AccessorySet previous, AccessorySet current) => Apply();

    private void Apply()
    {
        if (m_catalog == null || m_headBone == null)
        {
            Debug.LogWarning($"[{nameof(PlayerAccessories)}] 카탈로그 또는 머리 본이 연결되지 않았습니다 (#818)", this);
            return;
        }

        AccessorySet set = m_accessories.Value;

        foreach (EAccessorySlot slot in System.Enum.GetValues(typeof(EAccessorySlot)))
        {
            int i = (int)slot;

            if (m_spawned[i] != null)
            {
                Destroy(m_spawned[i]);
                m_spawned[i] = null;
            }

            GameObject prefab = m_catalog.Get(slot, set[slot]);
            if (prefab == null)
                continue; // 0(안 씀)이거나 범위 밖 — 아무것도 붙이지 않는다

            m_spawned[i] = Instantiate(prefab, m_headBone, false);
            m_spawned[i].name = prefab.name;
        }
    }
}
```

- [ ] **Step 2: 컴파일 확인**

```
refresh_unity(compile="request", mode="force", scope="scripts")
read_console(action="get", types=["error"])
```

기대: 에러 0건.

- [ ] **Step 3: `Player.prefab`에 컴포넌트를 붙이고 배선한다**

`execute_code` — 머리 본 경로는 이 프리팹에서 실측한 값이다:

```csharp
string path = "Assets/Prefabs/Player.prefab";
var root = UnityEditor.PrefabUtility.LoadPrefabContents(path);

var t = System.AppDomain.CurrentDomain.GetAssemblies()
    .SelectMany(a => { try { return a.GetTypes(); } catch { return new System.Type[0]; } })
    .FirstOrDefault(x => x.Name == "PlayerAccessories");

var comp = root.GetComponent(t) ?? root.AddComponent(t);
var head = root.transform.Find("Root/Hips/Spine_01/Spine_02/Spine_03/Neck/Head");

var so = new UnityEditor.SerializedObject(comp);
so.FindProperty("m_headBone").objectReferenceValue = head;
so.FindProperty("m_catalog").objectReferenceValue =
    UnityEditor.AssetDatabase.LoadAssetAtPath<AccessoryCatalog>("Assets/Settings/Player/AccessoryCatalog.asset");
so.ApplyModifiedPropertiesWithoutUndo();

UnityEditor.PrefabUtility.SaveAsPrefabAsset(root, path);
UnityEditor.PrefabUtility.UnloadPrefabContents(root);
UnityEditor.AssetDatabase.SaveAssets();
return "머리 본 = " + (head == null ? "<못 찾음 — 경로 확인 필요>" : head.name);
```

기대: `머리 본 = Head`

> 카탈로그 에셋은 Task 1 Step 6에서 이미 만들어 뒀다(비어 있음). 내용이 없는 동안에는 `Apply()`가 전부 "안 씀"으로 떨어져 아무것도 붙이지 않는다 — 의도된 중간 상태다.

- [ ] **Step 4: 프리팹 오버라이드에 씬 인스턴스 흔적이 없는지 확인**

`SaveAsPrefabAsset`은 씬 인스턴스 흔적을 남길 수 있다 (#721에서 실제로 겪었다).

```csharp
var asset = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Player.prefab");
var no = asset.GetComponent<Unity.Netcode.NetworkObject>();
var so = new UnityEditor.SerializedObject(no);
return "sceneMig=" + so.FindProperty("SceneMigrationSynchronization").boolValue
     + " inScene=" + so.FindProperty("InScenePlacedSourceGlobalObjectIdHash").uintValue
     + " hash=" + so.FindProperty("GlobalObjectIdHash").uintValue;
```

기대: `inScene=0`이고 `sceneMig`가 수정 전과 같을 것. 달라졌으면 그 오버라이드를 제거한다.

- [ ] **Step 5: 커밋**

```bash
git add Assets/Scripts/Player/View/PlayerAccessories.cs Assets/Prefabs/Player.prefab
git commit -m "치장을 서버가 스폰 시점에 심어 전 피어가 붙인다 (#818)"
```

---

### Task 6: 로비 선택 UI

**Files:**
- Create: `Assets/Scripts/UI/Cosmetics/AccessoryPickerView.cs`
- Modify: 로비 `PlayerColorPanel` 프리팹/씬 (슬롯별 선택 줄 배치)

**Interfaces:**
- Consumes: `AccessoryCatalog`·`GameSettings.Get/SetAccessory`·`OnAccessoryChanged`
- Produces: 없음

`PlayerColorPickerView`(`Assets/Scripts/UI/Cosmetics/PlayerColorPickerView.cs`)와 **같은 구조**다. 색 칸은 팔레트 색을 보여 주지만 치장 칸은 이름을 보여 준다 — 아이콘이 없기 때문이다.

- [ ] **Step 1: 선택 뷰를 만든다**

`Assets/Scripts/UI/Cosmetics/AccessoryPickerView.cs`:

```csharp
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 한 슬롯의 치장을 고르는 칸들 (#818) — <see cref="PlayerColorPickerView"/>와 같은 구조다.
/// 고르면 <see cref="GameSettings"/>에 쓰고, 남에게 나르는 일은 명부와 <c>PlayerAccessories</c>가 맡는다.
///
/// <b>보유 개념이 아직 없다</b> — 카탈로그의 전 항목을 고를 수 있다. 보유함 필터는 후속(#818 D)에서 얹는다.
/// </summary>
public class AccessoryPickerView : MonoBehaviour
{
    [Tooltip("이 줄이 고르는 슬롯")]
    [SerializeField] private EAccessorySlot m_slot;

    [Tooltip("치장 카탈로그 — Player 프리팹과 같은 에셋을 물릴 것")]
    [SerializeField] private AccessoryCatalog m_catalog;

    [Tooltip("칸을 넣을 부모 (Layout Group)")]
    [SerializeField] private RectTransform m_container;

    [Tooltip("칸 프리팹 — Button과 자식 Text 하나면 된다")]
    [SerializeField] private Button m_buttonPrefab;

    private readonly List<Button> m_buttons = new List<Button>();

    private void Awake()
    {
        if (m_catalog == null || m_container == null || m_buttonPrefab == null)
        {
            Debug.LogWarning($"[{nameof(AccessoryPickerView)}] 배선이 빠졌습니다 (#818)", this);
            enabled = false;
            return;
        }

        Build();
    }

    private void OnEnable()
    {
        GameSettings.OnAccessoryChanged += HandleAccessoryChanged;
        RefreshSelection();
    }

    private void OnDisable() => GameSettings.OnAccessoryChanged -= HandleAccessoryChanged;

    private void Build()
    {
        int count = m_catalog.CountOf(m_slot);
        for (int i = 0; i < count; i++)
        {
            int index = i; // 클로저가 루프 변수를 잡지 않게 사본을 넘긴다
            Button button = Instantiate(m_buttonPrefab, m_container);
            button.name = $"Accessory {index}";

            GameObject prefab = m_catalog.Get(m_slot, index);
            var label = button.GetComponentInChildren<Text>();
            if (label != null)
                label.text = prefab == null ? "없음" : prefab.name;

            button.onClick.AddListener(() => GameSettings.SetAccessory(m_slot, index));
            m_buttons.Add(button);
        }

        RefreshSelection();
    }

    private void HandleAccessoryChanged(EAccessorySlot slot)
    {
        if (slot == m_slot)
            RefreshSelection();
    }

    // 고른 칸만 상호작용을 끈다 — 색 칸의 SetSelected에 해당하는 최소 표시다
    private void RefreshSelection()
    {
        int selected = GameSettings.GetAccessory(m_slot);

        for (int i = 0; i < m_buttons.Count; i++)
            m_buttons[i].interactable = i != selected;
    }
}
```

> 칸 프리팹의 라벨이 `TMP_Text`라면 `using TMPro;`와 `TMP_Text`로 바꾼다 — 로비 패널이 쓰는 쪽에 맞출 것.

- [ ] **Step 2: 컴파일 확인**

```
refresh_unity(compile="request", mode="force", scope="scripts")
read_console(action="get", types=["error"])
```

기대: 에러 0건.

- [ ] **Step 3: 로비 패널에 줄 두 개를 배치한다**

`PlayerColorPanel`이 있는 프리팹/씬을 열고, 색 선택 줄 아래에 `AccessoryPickerView`를 얹은 오브젝트를 **두 개**(Headwear·FacialHair) 만든다. 각각 `m_slot`을 다르게 두고, `m_catalog`·`m_container`·`m_buttonPrefab`을 색 줄과 같은 방식으로 배선한다.

> 씬·프리팹 편집이라 이 단계는 Editor에서 사람이 한다. 자동화하면 레이아웃이 어긋난다.

- [ ] **Step 4: 커밋**

```bash
git add Assets/Scripts/UI/Cosmetics/AccessoryPickerView.cs
git commit -m "로비에서 치장을 고르는 칸을 만든다 (#818)"
```

---

### Task 7: 카탈로그 에셋 · 부착물 프리팹 · 최종 검증

**Files:**
- Create: `Assets/Settings/Player/AccessoryCatalog.asset`
- Create: 부착물 프리팹 (에셋 도착 후)

**Interfaces:**
- Consumes: 앞의 전부

- [ ] **Step 1: 카탈로그가 `Player.prefab`에 물려 있는지 확인**

에셋 자체는 Task 1 Step 6에서 만들었다. 여기서는 Task 5의 배선이 살아 있는지만 본다.

```csharp
var asset = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Player.prefab");
var t = System.AppDomain.CurrentDomain.GetAssemblies()
    .SelectMany(a => { try { return a.GetTypes(); } catch { return new System.Type[0]; } })
    .FirstOrDefault(x => x.Name == "PlayerAccessories");
var so = new UnityEditor.SerializedObject(asset.GetComponent(t));
var cat = so.FindProperty("m_catalog").objectReferenceValue;
var head = so.FindProperty("m_headBone").objectReferenceValue;
return "카탈로그 = " + (cat == null ? "<null — Task 5 Step 3을 다시 할 것>" : cat.name)
     + " · 머리 본 = " + (head == null ? "<null>" : head.name);
```

기대: `카탈로그 = AccessoryCatalog · 머리 본 = Head`

- [ ] **Step 2: 부착물 프리팹을 만들어 카탈로그에 넣는다**

에셋이 도착하면 슬롯마다 프리팹을 만든다. **규칙 셋:**
1. **콜라이더를 넣지 않는다** (Global Constraints).
2. 프리팹 루트의 위치·회전이 머리 본 기준 오프셋이다 — 로봇 두상에 맞게 여기서 잡는다.
3. 배열 **0번은 비워 둔다**(참조 없음). 실제 아이템은 1번부터.

인스펙터에서 `m_slots`에 슬롯 둘을 만들고 각각 `Prefabs` 배열을 채운다.

> ⚠ 배열 오버라이드는 `Array.size` 줄이 없으면 통째로 무시된다 — 인스펙터로 직접 편집하고, 스크립트로 쓴다면 size부터 설정할 것.

- [ ] **Step 3: 콜라이더가 없는지 기계적으로 확인**

```csharp
var cat = UnityEditor.AssetDatabase.LoadAssetAtPath<AccessoryCatalog>("Assets/Settings/Player/AccessoryCatalog.asset");
var sb = new System.Text.StringBuilder();
foreach (EAccessorySlot slot in System.Enum.GetValues(typeof(EAccessorySlot)))
  for (int i = 0; i < cat.CountOf(slot); i++) {
    var p = cat.Get(slot, i);
    if (p == null) { sb.AppendLine(slot + "[" + i + "] = 안 씀"); continue; }
    int cols = p.GetComponentsInChildren<Collider>(true).Length;
    sb.AppendLine(slot + "[" + i + "] = " + p.name + " 콜라이더 " + cols + (cols > 0 ? "  ⚠ 제거할 것" : ""));
  }
return sb.ToString();
```

기대: 모든 줄의 콜라이더가 0.

- [ ] **Step 4: 사람이 보는 최종 검증 (MPPM 2인 이상)**

**핵심 확인 사항**
1. 로비에서 각자 다른 모자를 고르고 게임 씬 진입 → **남의 화면에 첫 프레임부터** 제 모자가 보인다. 맨머리가 한 번 스쳤다가 바뀌면 실패다.
2. 본부 CCTV에서도 같게 보인다.
3. 로그아웃 → 재접속 후에도 착용 상태가 유지된다.

**회귀 지점**
- **기존 사용자의 색** — `k_version`을 2로 올렸다. 이미 색을 저장해 둔 계정으로 로그인해 색이 그대로인지 볼 것. 콘솔에 `[커스터마이징] v1 레코드를 읽었다`가 뜨고 색이 살아 있어야 한다.
- **게임플레이 무영향** — 모자 쓴 플레이어를 조준·진압봉 타격해 판정이 달라지지 않는지.

- [ ] **Step 5: 커밋**

```bash
git add Assets/Settings/Player
git commit -m "치장 카탈로그 에셋을 넣는다 (#818)"
```

---

## 자체 점검 결과

**스펙 대조**

| 스펙 항목 | 담당 태스크 |
|---|---|
| §2 색 경로 미러링 (5자리) | Task 2(값) · 3(저장) · 6(UI) · 4(명부) · 5(적용) |
| §3 데이터 모델 · 0=안 씀 | Task 1 |
| §4 서버가 스폰 때 심기 · 오너 보고 안전망 | Task 5 |
| §5 저장 + v1 마이그레이션 | Task 3 |
| §6 머리 본 · 3인칭만 · 런타임 Instantiate · 콜라이더 금지 | Task 5 · Task 7 Step 3 |
| §7 로비 UI (보유 필터 없음) | Task 6 |
| §9 검증 | Task 7 Step 4 |
| §10 에셋 오프셋 흡수 | Task 7 Step 2 규칙 2 |

**의존 순서:** 자체 점검에서 Task 5가 Task 7의 산출물을 참조하는 역방향 의존이 나와, 카탈로그 에셋 **생성을 Task 1 Step 6으로 앞당겼다.** 이제 Task 5는 이미 존재하는 (비어 있는) 에셋을 물리고, Task 7은 그 안을 채우기만 한다. 태스크 번호 순서대로 진행하면 막히는 지점이 없다.
