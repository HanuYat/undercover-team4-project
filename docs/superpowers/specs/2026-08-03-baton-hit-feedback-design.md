# 진압봉 타격 피드백 — 먼지 + 타격음 + 히트마커 설계 (#478)

> 상태: 설계 확정 — 구현 플랜 대기
> 선행: #482(AudioListener 정비) — **이 작업에 포함한다**
> 관련: #483(SoundManager) 일부 선취 · #476/#477(피격 연출 1·3차) 미착수

## 0. 목표

진압봉으로 때렸을 때 **아무 표현이 없는** 상태를 없앤다. 지금은 `Baton`이 오너 콘솔에 찍는 디버그 텍스트가 명중 여부를 아는 유일한 수단이다.

#478 원안(임팩트 링·스파크·타수별 몸 플래시·손상 잔류)에서 **시각 연출을 먼지 하나로 줄이고 사운드에 무게를 싣는다.** 로봇을 치면 깡, 사람을 치면 퍽 — 무엇을 때렸는지가 소리로 읽히게 하는 것이 이 작업의 핵심이다.

### 원안에서 바뀐 점과 그 이유

**#478 이슈 본문의 전제가 틀려 있다.** 본문은 "1차(#476)·3차(#477)는 구현 완료"라고 적었으나 두 이슈 모두 OPEN이고, 코드에 `DamageHit`·`NpcShockView`·`BodyTint`가 **하나도 없다**(전체 검색 0건). 원안의 "`DamageHit` 구조체를 그대로 재사용", "`NpcShockView`를 `BodyTint` 위로 이관"은 지금 성립하지 않는다.

이 설계는 그 의존을 **회피한다**. 몸 색을 건드리는 연출(타수별 플래시·손상 잔류)을 이번 범위에서 빼기 때문에 `BodyTint`가 필요 없고, 따라서 #476·#477의 완료를 기다리지 않는다.

## 1. 범위

### 포함

1. **#482 선행 정비** — `Player.prefab` 카메라에 `AudioListener` 부착, Main Scene의 씬 리스너 제거
2. **`EffectManager`** — 파티클 오브젝트 풀링 (신규 매니저)
3. **`SoundManager`** — 원샷 3D SFX 재생 (신규 매니저, #483의 부분 선취)
4. **먼지 이펙트 1종** — 명중 지점에 짧게 (밧줄 끌기 먼지 `FX_RopeDust`와 같은 결)
5. **타격음 3갈래** — 스윙(휙) / 명중(깡·퍽) / 벽·소품(둔탁)
6. **히트마커** — 오너 크로스헤어, 아군 오사는 다른 색

### 제외 (#478에 남긴다)

- 임팩트 링 · 스파크 버스트 → 먼지로 대체
- 타수별 몸 플래시(흰→주황→적) → `BodyTint` 기반이 아직 없다
- 손상 잔류 → 원안도 기본 꺼짐이었다
- NPC 피격 리액션 애니메이션 → 원안대로 건드리지 않는다
- `NpcController.OnDamaged` 이벤트 → 몸 플래시가 빠지면서 소비자가 없어졌다

### 제외 (#482에 남긴다)

- `TipCallPhoneView.ApplyDistanceVolume`(수동 거리 감쇠) 제거 및 `spatialBlend = 1` 복귀

리스너를 옮겨도 이 코드는 계속 동작한다(불필요해질 뿐). 여기서 손대면 제보 전화 벨소리 회귀 검증까지 이 PR이 떠안는다.

## 2. 확정 결정

1. **`EffectManager`는 파티클만 관리한다.** 사운드는 `SoundManager`가 따로 가진다. 덕분에 먼지 프리팹이 대상 종류와 무관한 **1종**으로 줄고, 깡/퍽 분기는 소리 쪽 한 곳에만 존재한다.
2. **`SoundManager`는 얇은 버전으로 도입한다.** 원샷 3D SFX 재생까지만. BGM·씬 전환 유지·UI음은 #483에 남긴다. 단, #483이 요구한 구조(enum 키 + SO 매핑, R4/R5 준수, AppBootstrap 상주)를 처음부터 따라 나중에 BGM만 얹을 수 있게 한다.
3. **깡/퍽 판단은 서버가 한다.** 결정된 클립 id를 RPC에 실어 보낸다. `CitizenType`은 전 피어에 동기화되므로 클라가 다시 조회해도 되지만, 그러면 프로필 미배정 같은 예외 처리가 피어마다 흩어진다.
4. **일회성 연출은 RPC로 전파한다.** #483이 정한 "서버가 상태를 동기화 → 각 피어가 로컬 재생" 규칙은 **지속 상태**에 대한 것이고, 타격은 동기화할 상태가 없는 순간 이벤트다. `NpcDespawnVfx`(#310)와 `Baton.PlaySwingRpc`가 이미 같은 논리를 쓴다.
5. **오디오 클립은 이번에 넣지 않는다.** 배선(enum·SO 항목·인스펙터 슬롯)만 완성하고 클립 슬롯은 비워 둔 채 병합한다. 클립이 비면 조용히 무동작한다.

### 결정 3의 근거 — 소리로 타입을 드러내도 되는가

`OfficialRecords.CitizenType { Human, Android }`는 `CitizenData`로 전 피어에 동기화된다. 위조(#223)는 **표시 이름과 표시 문양만** 오염시키고 `m_typeView`는 건드리지 않으므로, 소리가 알려주는 종족은 스캐너가 보여주는 값과 항상 일치한다. 정보 누출도, 표시 모순도 생기지 않는다.

### 결정 5의 근거

클립 확보가 코드 작업과 독립적이고, 클립이 없다고 먼지·히트마커·풀링·리스너 정비의 검증이 막히지 않는다. 다만 **이 이슈의 완료 판정은 클립이 들어와야 난다** — 배선 병합은 중간 단계다.

## 3. 아키텍처

### 신규 매니저 2종

둘 다 R4를 따른다: 베이스 상속 + `[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]` 재명시 + `App` 필드/프로퍼티 추가. `Awake`/`OnDestroy`는 R5대로 `protected override` + `base` 호출.

#### `EffectManager` → `App.Game.Effect`

```
Assets/Scripts/Vfx/EffectManager.cs

[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class EffectManager : CommonManagerBase
{
    [SerializeField] private EffectLibrary m_library;

    public void Play(EEffect id, Vector3 position, Vector3 normal);
}
```

- **R3 근거**: ① 씬에 개념적으로 하나뿐인 서비스 ② Item(진압봉)·NPC(#476/#477)·Events(`NpcDespawnVfx` #310, `BombExplosionView` — 둘 다 현재 `Instantiate`/`Destroy`로 도는 이관 후보) — 두 도메인 초과
- **배치**: Main Scene 인게임 매니저. 로비·상점에는 없으므로 호출부는 `App.Game.Effect?.Play(...)`로 가드한다(R8의 "null 가드는 사용처에서" 관례).
- **`normal`의 용도**: 먼지가 맞은 표면에서 튀어나오도록 회전을 맞춘다. `Quaternion.LookRotation(normal)`.
- **풀**: `Dictionary<EEffect, Stack<GameObject>>`. 꺼낼 때 `SetActive(true)` + 위치·회전 지정, 수명이 지나면 `SetActive(false)` 후 반납.
- **수명 판정**: 카탈로그에 적힌 **고정 초**를 쓴다. `ParticleSystem.IsAlive()` 폴링을 하지 않는 이유는 매 프레임 비용이 있고, 파티클과 (프리팹에 오디오가 붙는 경우의) 소리 중 어느 쪽이 긴지를 코드가 판단해야 하기 때문이다. 값 하나를 적는 편이 예측 가능하다.
- **회수 루프**: 활성 인스턴스를 `(GameObject, 반납 시각)` 리스트로 들고 `Update`에서 순회한다. 동시 활성 개수가 한 자릿수라 순회 비용이 문제되지 않는다.
- **미등록 id · 프리팹 null**: 경고 1회 후 무동작. 매 프레임 도배하지 않도록 id별로 한 번만 경고한다.

```
Assets/Scripts/Vfx/EffectLibrary.cs

[CreateAssetMenu(fileName = "EffectLibrary", menuName = "Scriptable Objects/EffectLibrary")]
public class EffectLibrary : ScriptableObject
{
    [Serializable]
    public struct Entry
    {
        public EEffect Id;
        public GameObject Prefab;
        public float LifetimeSeconds;   // 이 시간 뒤 풀에 반납
        public int Prewarm;             // 시작 시 미리 만들어 둘 개수
    }

    [SerializeField] private Entry[] m_entries;

    public bool TryGet(EEffect id, out Entry entry);
}
```

#### `SoundManager` → `App.Sound`

```
Assets/Scripts/Audio/SoundManager.cs

[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class SoundManager : CommonManagerBase
{
    [SerializeField] private AudioLibrary m_library;
    [SerializeField] private int m_sourceCount = 16;

    public void PlaySfxAt(EAudioClip id, Vector3 position);
}
```

- **R3 근거**: ① 씬에 하나뿐 ② Item·NPC·Interaction·Round 등 다수 도메인이 소리를 낸다
- **배치**: **AppBootstrap 프리팹**(DontDestroyOnLoad). 지금은 SFX만 내지만, #483이 요구하는 "BGM이 씬 전환을 넘는다"를 이미 만족하는 자리다. 인게임에 뒀다가 나중에 옮기면 배선을 두 번 한다.
- **`App` 그룹**: `App.Sound` 신설. #483이 `App.SystemManager.Sound` 또는 `App.Sound` 중 하나로 적었고, 현재 App에 `SystemManager` 그룹이 없으므로 `App.Sound`를 쓴다.
- **2D 재생 API는 넣지 않는다.** 이번 3갈래가 전부 3D다(내 스윙도 남의 스윙도 각자 위치 기준으로 들려야 한다). UI음이 필요해지면 #483이 추가한다.
- **풀**: 자기 자식으로 `AudioSource`를 `m_sourceCount`개 만들어 둔다. 재생 시 노는 소스를 찾아 위치·클립·볼륨·거리 설정 후 `Play()`. 전부 사용 중이면 **가장 오래 재생 중인 소스를 뺏는다**(대사가 아니라 짧은 타격음이라 잘려도 티가 적다).
- **클립 null**: 조용히 무동작 + 경고 1회. `JailSirenButton`의 null 가드와 같은 방침.
- **전역 음량은 건드리지 않는다.** `GameSettings.MasterVolume`이 이미 `AudioListener.volume`으로 배율을 걸고 있다(#225).

```
Assets/Scripts/Audio/AudioLibrary.cs

[CreateAssetMenu(fileName = "AudioLibrary", menuName = "Scriptable Objects/AudioLibrary")]
public class AudioLibrary : ScriptableObject
{
    [Serializable]
    public struct Entry
    {
        public EAudioClip Id;
        public AudioClip Clip;
        [Range(0f, 1f)] public float Volume;
        public float MinDistance;    // 이 거리까지 감쇠 없음
        public float MaxDistance;    // 이 거리 밖은 들리지 않음
    }

    [SerializeField] private Entry[] m_entries;

    public bool TryGet(EAudioClip id, out Entry entry);
}
```

감쇠 거리를 항목별로 두는 이유: 스윙음(가까이서만)과 명중음(조금 더 멀리)의 도달 범위가 달라야 한다. 매니저에 고정값을 박으면 소리마다 조절할 수 없다.

#### 두 풀을 공용화하지 않는 이유

`EffectManager`는 GameObject를 활성/비활성으로 재사용하고, `SoundManager`는 컴포넌트 슬롯을 재사용한다. 수명 판정(고정 시간 vs `isPlaying`)도 다르다. 억지로 하나의 제네릭 풀로 묶으면 양쪽 다 어색해진다. 각각 30줄 남짓이므로 중복을 감수한다.

### enum 추가 (`Core/Enums.cs`)

기존 관례대로 전역 enum은 `Core/Enums.cs`에 모은다.

```csharp
public enum EEffect
{
    None,
    ImpactDust,   // 근접 타격 먼지 (#478)
}

public enum EAudioClip
{
    None,
    BatonSwing,     // 휙 — 휘두르는 순간
    BatonHitMetal,  // 깡 — 로봇(안드로이드 NPC · 동료 경찰)
    BatonHitFlesh,  // 퍽 — 인간 NPC
    BatonHitWorld,  // 둔탁 — 벽·소품
}
```

## 4. 데이터 흐름 — 진압봉 1스윙

```
[오너]  좌클릭 → Baton.Use → RequestSwingRpc(origin, direction)

[서버]  ServerSwing: 방향 검증 · 원점 검증 · 쿨다운 검사/소모
         ├→ PlaySwingRpc(Everyone)          ← 기존 RPC, 여기에 스윙음을 얹는다
         └→ 0.3초 대기 (k_swingImpactSeconds)
            → EvaluateSwing → 결과 분류
               ├ NoHit         → 아무것도 보내지 않는다
               ├ HitNonTarget  → PlayImpactRpc(point, normal, BatonHitWorld)
               ├ InvalidState  → PlayImpactRpc(point, normal, 대상별 클립)
               └ ValidTarget   → PlayImpactRpc(point, normal, 대상별 클립)
                               + NotifyHitRpc(Owner, friendlyFire)

[전 피어] PlaySwingRpc  → 스윙 애니메이션(기존)
                        + App.Sound?.PlaySfxAt(BatonSwing, 소지자 위치)

[전 피어] PlayImpactRpc → App.Game.Effect?.Play(EEffect.ImpactDust, point, normal)
                        + App.Sound?.PlaySfxAt(clip, point)

[오너]    NotifyHitRpc  → App.UI.Crosshair?.ShowHit(friendlyFire)
```

### `Baton`에 추가되는 RPC

```csharp
[Rpc(SendTo.Everyone)]
private void PlayImpactRpc(Vector3 point, Vector3 normal, EAudioClip clip);

[Rpc(SendTo.Owner)]
private void NotifyHitRpc(bool friendlyFire);
```

- `PlayImpactRpc`는 **먼지와 소리를 한 번에** 전파한다. 먼지가 1종으로 통일됐으므로 페이로드에 이펙트 id를 실을 필요가 없다.
- `NotifyHitRpc`는 기존 `NotifyOwner`(`ChanneledInteractionBehaviour`의 `SendTo.Owner` 경로)와 같은 방식이다. 기존 콘솔 로그는 그대로 둔다 — 디버깅에 계속 쓰인다.
- **오프라인(미스폰) 경로**: 기존 `PlaySwing()`이 `IsSpawned`를 보고 로컬 실행으로 분기하는 것과 같은 형태로, 임팩트·히트마커도 로컬 직접 호출 경로를 둔다.

### 클립 결정 (서버, `ServerResolveHitAtImpactAsync` 내부)

| 맞은 것 | 클립 |
|---|---|
| `NpcController` + `CitizenType.Android` | `BatonHitMetal` |
| `NpcController` + `CitizenType.Human` | `BatonHitFlesh` |
| `NpcController` + 프로필 미배정(`Profile == null`) | `BatonHitFlesh` (기본값) |
| `PlayerHealth`(동료 — 로봇 경찰) | `BatonHitMetal` |
| 그 외(벽·소품) | `BatonHitWorld` |

프로필 미배정 NPC는 라운드 시작 전 스폰 직후에만 존재한다. 어느 쪽을 골라도 무방하지만 갈래를 남기지 않기 위해 `BatonHitFlesh`로 고정한다.

## 5. 판정별 표현 대응표

| `EvaluateSwing` 결과 | 대상 | 먼지 | 소리 | 히트마커 |
|---|---|---|---|---|
| — (스윙 시작) | — | 없음 | **휙** (전 피어) | — |
| `NoHit` | 허공 | 없음 | 없음 | 없음 |
| `HitNonTarget` | 벽·소품 | ✅ | **둔탁** | 없음 |
| `TargetInvalidState` | 이미 제압된 NPC · 무력화된 동료 | ✅ | 대상별(깡/퍽) | **없음** |
| `ValidTarget` | NPC `Android` | ✅ | **깡** | 기본색 |
| `ValidTarget` | NPC `Human` | ✅ | **퍽** | 기본색 |
| `ValidTarget` | 동료 플레이어 | ✅ | **깡** | **오사색** |

### `TargetInvalidState`에서 소리는 내되 히트마커는 안 띄우는 이유

봉이 물리적으로 몸에 닿은 것은 맞다. 침묵하면 "입력이 씹혔나?"가 되고, 히트마커까지 띄우면 데미지가 들어간 것처럼 거짓말이 된다. **히트마커의 의미를 "유효타" 하나로 고정한다.**

### 동료 오사만 색을 다르게 하는 이유

동료(로봇 경찰)와 NPC 안드로이드가 **둘 다 깡소리**라 소리로는 구분되지 않는다. 히트마커 색이 아군 오사(#461)를 드러내는 유일한 수단이다.

### `CrosshairUI` 추가분

```csharp
/// <summary>명중 순간 짧게 표시. friendlyFire면 다른 색. (#478)</summary>
public void ShowHit(bool friendlyFire);
```

- 표시 시간 0.15초. 기존 `SetInteractable`/`SetWeaponTargeting`이 매 프레임 색을 덮어쓰므로, **히트마커는 색이 아니라 별도 `Image`(짧은 십자 마커)를 켜고 끄는 방식**으로 구현한다. 색으로 하면 다음 프레임에 조준 로직이 지워 버린다.

## 6. 먼지 이펙트

`RopeDragView.m_dustPrefab`이 쓰는 **`Assets/Prefabs/Items/FX_RopeDust.prefab`을 복제해 원샷으로 변형한다.** 밧줄 끌기 먼지와 같은 결로 보이는 것이 목표이므로, 별도 소스에서 새로 만들지 않고 그 프리팹에서 출발한다.

- 신규 프리팹 `Assets/Prefabs/Items/FX_ImpactDust.prefab` — 진압봉이 아이템이므로 기존 `FX_` 배치 관례(`Prefabs/<도메인>/FX_*`)에 따라 `Items/`에 둔다. 원본 `FX_RopeDust`는 수정하지 않는다(밧줄 끌기가 계속 쓴다).
- 원본과의 차이: `looping = false`, `playOnAwake = true`, 지속 0.3~0.4초의 **1회 버스트**. 밧줄 쪽은 끌리는 동안 계속 나는 지속형이다.
- 대상이 금속이든 살이든 **같은 먼지**를 쓴다. 구분은 전부 소리가 한다.

## 7. #482 선행 정비 (이 작업에 포함)

- `Assets/Prefabs/Player.prefab`의 카메라에 `AudioListener` 부착. `PlayerLook.ApplyOwnerView(IsOwner)`가 비오너 카메라를 `SetActive(false)` 하므로 원격 플레이어의 리스너는 자동으로 꺼진다 — 별도 오너 분기가 필요 없다.
- `Assets/Scenes/Main Scene.unity`의 씬 `AudioListener` 제거.
- Title·Lobby·Shop의 씬 리스너는 **유지**한다(플레이어가 없어 리스너가 0개가 된다).

현재 상태는 확인했다: `Player.prefab`에 `AudioListener` 0개, 4개 씬 모두 Main Camera에 리스너가 있다.

## 8. `docs/architecture.md` 추가분

일회성 연출의 전파 규칙을 명문화한다(#483이 사운드 규칙을 정하며 남긴 숙제와 같은 자리).

> **연출 전파**: 지속 상태(먹통·기절 등)는 서버가 `NetworkVariable`로 동기화하고 각 피어가 그 값을 보고 로컬 재생한다. **동기화할 상태가 없는 일회성 연출**(타격·소멸·스윙)은 서버 판정 지점에서 `SendTo.Everyone` RPC로 전파하고 각 피어가 로컬 생성한다 — 연출 오브젝트를 네트워크에 싣지 않으므로 프리팹 등록이 필요 없다. 선례: `NpcDespawnVfx`(#310), `Baton.PlaySwingRpc`(#217).

## 9. 파일

### 신규

| 경로 | 내용 |
|---|---|
| `Assets/Scripts/Vfx/EffectManager.cs` | 파티클 풀링 매니저 |
| `Assets/Scripts/Vfx/EffectLibrary.cs` | `EEffect` → 프리팹/수명/사전생성 |
| `Assets/Scripts/Audio/SoundManager.cs` | 원샷 3D SFX 재생 + `AudioSource` 풀 |
| `Assets/Scripts/Audio/AudioLibrary.cs` | `EAudioClip` → 클립/볼륨/거리 |
| `Assets/Scripts/Data/EffectLibrary.asset` | 카탈로그 인스턴스 — 기존 SO 에셋(`AppearanceDatabase`·`OfficialRecord`)과 같은 자리 |
| `Assets/Scripts/Data/AudioLibrary.asset` | 카탈로그 인스턴스 (클립 슬롯은 비워 둔다) |
| `Assets/Prefabs/Items/FX_ImpactDust.prefab` | 먼지 — `FX_RopeDust` 복제 후 원샷화 |

### 수정

| 경로 | 내용 |
|---|---|
| `Assets/Scripts/Core/Enums.cs` | `EEffect` · `EAudioClip` 추가 |
| `Assets/Scripts/Core/App.cs` | 필드 2개 + `App.Game.Effect` · `App.Sound` |
| `Assets/Scripts/Item/Weapons/Baton.cs` | 스윙음, `PlayImpactRpc`, `NotifyHitRpc`, 클립 결정 |
| `Assets/Scripts/UI/Hud/CrosshairUI.cs` | `ShowHit(bool friendlyFire)` + 마커 `Image` |
| `Assets/Prefabs/UI/HUD.prefab` | 히트마커 `Image` 추가 (`CrosshairUI`가 붙은 프리팹) |
| `Assets/Prefabs/Player.prefab` | 카메라에 `AudioListener` |
| `Assets/Prefabs/AppBootstrap.prefab` | `SoundManager` 배치 |
| `Assets/Scenes/Main Scene.unity` | 씬 `AudioListener` 제거 + `EffectManager` 배치 |
| `docs/architecture.md` | §8의 연출 전파 규칙 |


## 10. 완료 기준

- [ ] 진압봉을 휘두르면 명중 여부와 무관하게 **스윙음**이 전 피어에서 난다
- [ ] 명중하면 **맞은 자리**(`RaycastHit.point`)에 먼지가 뜬다 — 몸 중심이 아니다
- [ ] 안드로이드 NPC·동료는 **깡**, 인간 NPC는 **퍽**, 벽·소품은 **둔탁**으로 갈린다
- [ ] 허공을 치면 스윙음 외에 아무것도 나지 않는다
- [ ] 명중 순간 때린 사람의 크로스헤어에 히트마커가 뜨고, **아군 오사는 색이 다르다**
- [ ] 이미 제압된 대상을 치면 소리·먼지는 나지만 **히트마커는 뜨지 않는다**
- [ ] 연타해도 먼지 인스턴스가 무한히 늘지 않고 풀에서 재사용된다
- [ ] 클립이 비어 있어도 에러 없이 동작한다(경고 1회)
- [ ] 씬 전환 전 구간에서 `"There are N audio listeners"` 경고가 없다
- [ ] 호스트·원격 클라이언트 양쪽에서 동일하게 동작한다
- [ ] 오프라인(비네트워크) Play 모드에서도 동작한다

## 11. 검증

- **오프라인 Play**: 4갈래 소리·먼지 위치·히트마커·풀 재사용
- **MPPM 2인**: 남의 스윙과 타격이 **내 위치 기준 거리감**으로 들리는지 — #482가 실제로 먹혔는지 확인하는 지점이다
- **씬 전환**: Title → Lobby → Shop → Game 전 구간 리스너 경고 확인
- Play 모드 검증은 사용자가 직접 수행한다(팀 관례). 구현 측은 컴파일 통과까지 책임진다.

## 12. 리스크 · 후속

| 항목 | 내용 |
|---|---|
| **오디오 클립 미확보** | 배선만 병합한다(결정 5). 클립이 들어와야 이슈가 닫힌다 — 후속 작업으로 분리 |
| **NGO enum 파라미터** | 코드베이스에 enum을 RPC 인자로 싣는 선례가 없다(`PlayAttackSwingClientRpc(int variant)`처럼 int를 쓴다). NGO가 unmanaged enum을 직렬화하므로 `EAudioClip`을 그대로 실을 수 있어야 하지만, 컴파일·런타임에서 거부되면 `byte`로 캐스트해 싣고 수신 측에서 되돌린다 |
| **#483과의 경계** | `SoundManager`를 얇게 먼저 들여오는 것을 #483 작성자(이현진)와 합의해야 한다. #483은 `PlaySFXAt`을 자기 완료 기준에 포함하고 있다 |
| **#476/#477** | 몸 플래시·감전 연출이 들어올 때 `BodyTint`가 필요해진다. 이 작업은 그 지점을 건드리지 않으므로 충돌하지 않는다 |
| **기존 VFX 이관** | `NpcDespawnVfx`(#310)와 `BombExplosionView`가 `Instantiate`/`Destroy`로 돌고 있다. `EffectManager` 위로 옮길 수 있으나 **이번 범위 밖** — 별도 작업 |
| **#483 근거의 사실 오류** | #483은 "`ParticleSystem` 사용 0건"과 "Addressables 미설치"를 근거로 들었으나 둘 다 현재 사실과 다르다(`FX_BombExplosion`·`FX_RopeDust` 존재, `Assets/AddressableAssetsData/` 존재). 이 설계는 Addressables를 쓰지 않으므로 영향은 없지만, #483 진행 시 재검토가 필요하다 |

---
*작성: 2026-08-03 · 이슈 #478(축소 재설계) · 선행 #482 포함 · #483 부분 선취*
