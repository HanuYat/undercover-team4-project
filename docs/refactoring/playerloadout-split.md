# PlayerLoadout 책임 분리 (PR #467)

> 작성: 2026-08-01 · 브랜치 `playerLoadout-hotfix` (커밋 `bf722e4`)
> 게임 동작 변화 없음. 규칙의 정본은 [architecture.md](../architecture.md).

## 1. 한 문장 요약

`PlayerLoadout`(669줄)에 몰려 있던 아이템 소지 로직에서 **개념 3개를 협력자로 뽑아내** 467줄로 줄였다.
public 표면과 프리팹 직렬화 값은 한 글자도 바꾸지 않았다.

## 2. 판단 기준 — Unity에서 무엇이 위험한가

이 작업을 시작하기 전에 정한 기준이다. 이후 [PlayerMovement 분리](playermovement-split.md)도 같은 기준을 썼고,
남은 400줄+ 파일에도 그대로 쓰면 된다.

**핵심: 리팩토링 위험도는 "코드가 얼마나 꼬였나"가 아니라 "외부 표면을 바꾸나"에 비례한다.**
Unity 컴포넌트 모델에서 결합은 **타입 이름 + public 멤버 + 프리팹 직렬화 값**에만 걸린다.
그 셋을 안 건드리면 내부를 아무리 옮겨도 외부는 못 느낀다.

| 등급 | 방식 | 외부 영향 | 프리팹 영향 | 검증 |
|---|---|---|---|---|
| 🟢 안전 | `partial`로 파일만 분할 | 없음 | 없음 | 컴파일러가 100% 보증 |
| 🟡 주의 | private 로직을 **평범한 C# 객체**로 추출 | 없음 | 없음 | 컴파일 + 눈 |
| 🔴 위험 | 새 `MonoBehaviour`/`NetworkBehaviour`로 분리 | `GetComponent` 호출부 전부 | **SerializeField 재배선 필요** | 플레이 테스트만 |

### 2-1. 진짜 위험은 코드가 아니라 프리팹에 있다

```
Assets/Prefabs/Player.prefab
  m_Script: {guid: f15d4559…}   ← PlayerLoadout
  m_startingGear: [스캐너, 밧줄, 진압봉]
```

`SerializeField`를 새 컴포넌트로 옮기면 **이 참조가 그냥 사라진다.** 컴파일은 통과하고, 에러도 안 나고,
플레이하면 아이템만 안 나온다. 게다가 프리팹 diff는 git 머지 충돌이 나면 손으로 못 고친다.

→ **작업 전에 프리팹에서 실제 직렬화 값을 확인하고, 옮길 값 목록을 먼저 적어 둘 것.**

### 2-2. `partial` 분할은 목적지가 아니라 도구다

`partial`은 결합도를 1도 줄이지 않는다 — 같은 클래스라 private 상태를 전부 공유하고, 캡슐화 경계가 안 생기고,
독립 테스트도 안 된다. 파일만 3개가 된 669줄짜리 클래스일 뿐이다.
파일을 정리해 진짜 절단면이 보이게 만드는 **중간 단계**로는 쓸모 있지만, 그것만 하고 끝내면 겉보기다.

### 2-3. 컴포넌트 분리가 "가짜 분리"가 되는 경우

`PlayerLoadout`을 서버/오너 두 `NetworkBehaviour`로 쪼갠다고 해 보자. 진짜 상태(누가 뭘 들었나)는
컴포넌트가 아니라 **씬 그래프(부착된 자식)** 에 있으므로, 둘 다 같은 Transform 계층을 읽고 쓰게 된다.
캡슐화가 생기는 게 아니라 공유 가변 상태를 둘이 나눠 만지는 것 — **결합도는 그대로인데 RPC 표면과
프리팹 재배선 비용만 늘어난다.**

→ **판단 규칙: 진짜 상태의 소유자가 하나로 유지되는가?** 아니면 분리하지 말 것.

### 2-4. 이 저장소의 추가 제약

- **asmdef 0개** — 전부 `Assembly-CSharp` 한 덩어리. 스크립트 하나 고치면 전체 재컴파일.
- **테스트 0개** — 동작 보존을 자동으로 확인할 방법이 없다. 그래서 "컴파일러가 검증해 주는 리팩토링"과
  "사람이 플레이해야 아는 리팩토링"의 격차가 유난히 크다.

## 3. 무엇이 바뀌었나

| 지표 | 이전 | 이후 |
|---|---|---|
| `PlayerLoadout` 줄 수 | 669 (실코드 410) | **467 (실코드 267)** |
| `ItemParent` 자식 순회 | 4곳에 중복 | **1곳** (`HeldItems`) |
| 참조 해석 대기 구현 | 2개 파일에 각자 + 상수 `120` 2벌 | **1곳** (`NetworkRefResolver`) |
| 오너 통지 호출 | `SyncHeldItemsRpc(m_held.BuildRefs())` 4회 반복 | `ServerNotifyHeldItemsChanged()` 4회 |
| public 표면 | — | **무변경** |
| 프리팹 직렬화 값 | — | **fileID·GUID 그대로 이동** |

### 최종 구성

| 파일 | 줄 수 | 담당 |
|---|---:|---|
| `Player/PlayerLoadout.cs` | 467 | 줍기·버리기 **권위 검증**, 오너 슬롯 선택, 서버↔오너 동기화 다리 |
| `Player/PlayerItemSupply.cs` | 130 | 라운드 경계 **지급·회수** (신규) |
| `Player/HeldItems.cs` | 135 | 부착된 아이템 **집합** (신규) |
| `Network/NetworkRefResolver.cs` | 85 | `NetworkObjectReference` **해석 대기** (신규) |
| `Player/LoadoutSlots.cs` | 173 | 칸 **배치** (기존 — 이 패턴의 원본) |

## 4. 추출 1 — `HeldItems` (🟡 평범한 C# 객체)

### 문제

`ItemParent`의 자식을 순회하는 코드가 **네 곳**에 있었다: 개수 세기, 밧줄 개수 세기, 참조 목록 만들기, 디스폰.
우연한 중복이 아니라 **"플레이어에게 부착된 아이템 집합"이라는 개념이 이름을 못 얻은 상태**였다.

### 결과

```csharp
public sealed class HeldItems          // MonoBehaviour 아님
{
    public int Count { get; }
    public int CountOf<TComponent>();  // 밧줄 개수 등
    public bool Holds(NetworkObject);
    public void Attach(NetworkObject);
    public NetworkObjectReference[] BuildRefs();
    public int DespawnAll();
}
```

`PlayerLoadout`에서 `ItemParent` 프로퍼티가 **완전히 사라졌다** — 이제 이 클래스는 Transform 계층을
직접 만지지 않는다. 소지품에 대한 모든 질문이 `m_held`를 거친다.

### 정직한 한계

`LoadoutSlots<T>`와 달리 **Unity 없이 테스트할 수 없다.** Transform 계층 자체가 상태이기 때문이다.
얻은 것은 캡슐화와 단일 접근 경로지 테스트 가능성이 아니다.

## 5. 추출 2 — `NetworkRefResolver` (🟡, 파일 2개에 걸친 중복)

### 문제

`PlayerLoadout`과 `PlayerHeldItemView`가 같은 정책("갓 스폰된 NetworkObject 참조는 즉시 해석되지 않을 수
있으니 몇 프레임 기다린다")을 각자 구현하고, 상수 `120`을 **따로 선언**하고, 주석으로 서로를 가리키고 있었다.

```
PlayerLoadout.cs      : const int k_maxWaitFrames = 120;
PlayerHeldItemView.cs : const int k_maxResolveWaitFrames = 120;
                        // "PlayerLoadout.ResolveAndRebuildAsync와 같은 방침"  ← 주석이 계약을 유지 중
```

**주석이 하고 있던 일을 코드가 하게 만든 것**이 이 추출의 요점이다. 한쪽에서 120을 바꾸면 다른 쪽은
조용히 어긋나고 컴파일러는 아무 말도 안 했다.

### 왜 `bool`이 아니라 `enum`인가

두 호출부의 **타임아웃 처리가 반대**였다:

| | 중단(디스폰) | 상한 초과 |
|---|---|---|
| `PlayerLoadout` | 건너뜀 | **재구성 진행** (해석된 것만 부분 반영) |
| `PlayerHeldItemView` | 폐기 | **폐기** (빈손 표시) |

그래서 `EResolveResult { Resolved, TimedOut, Aborted }`로 **사유를 돌려주고 처리는 각자** 하게 했다.
예전에는 이 차이가 "루프 뒤에 뭐가 오느냐"로 암묵적으로만 표현돼 있었다.

## 6. 추출 3 — `PlayerItemSupply` (🔴 컴포넌트 분리)

### 왜 이것만 컴포넌트인가

**구동 주체가 다르다.** 줍기/버리기는 플레이어 입력이 매 순간 부르지만, 지급/회수는 라운드 경계에서
매니저(`PlayerSpawnManager`·`ShopManager`)가 클라별로 한 번씩 부른다.

그리고 **얽힘 정도가 5배 차이**났다:

| | 필요한 협력자 | SerializeField |
|---|---|---|
| 지급/회수 | `HeldItems`, `OwnerClientId`, 통지 | `m_startingGear` 1개 |
| 줍기/버리기 | `HeldItems`, `PlayerInteractor`, `PlayerIncapacitation`, `PlayerItemUser`, `PlayerEscorter`, 통지 | `m_dropDistance` 1개 |

→ **지급/회수만 분리하고 줍기/버리기는 남겼다.** 후자를 떼면 형제 컴포넌트 5개 배선이 복제된다.

### 분리를 막던 제약 — RPC 수렴점

서버가 소지품을 바꾸는 **네 경로가 오너 통지 RPC 하나로 수렴**하는 것이 이 클래스의 설계 축이다:

```
GrantStartingGear ─┐
ServerClearHeld   ─┤
PickupRpc         ─┼─→ SyncHeldItemsRpc  [Rpc(SendTo.Owner)]
DropRpc           ─┘
```

나눠 가지면 해석 대기 기계장치가 복제되고 수렴이 깨진다.
→ `PlayerItemSupply`는 **자기 RPC를 갖지 않고** `PlayerLoadout`의 것을 빌려 쓴다.
의존은 `Supply → Loadout` **한 방향**뿐이다.

```csharp
// PlayerLoadout — 소유자
internal HeldItems Held => m_held;
internal void ServerNotifyHeldItemsChanged() => SyncHeldItemsRpc(m_held.BuildRefs());
```

그 수렴점에도 이름을 줬다. 예전에는 `SyncHeldItemsRpc(m_held.BuildRefs())`라는 **구현 세부가 4번 반복**됐다.

### 프리팹 처리

- 컴포넌트를 **목록 맨 끝**에 추가 → 기존 `NetworkBehaviourId`가 밀리지 않아 RPC 라우팅 무영향
- `m_startingGear`는 `fileID`·`guid` **바이트 단위로 동일**하게 이동 (스캐너·밧줄·진압봉)
- 삭제한 오브젝트·컴포넌트 없음

## 7. 호출부 변경

| 파일 | 변경 |
|---|---|
| `PlayerSpawnManager.cs:137` | `GetComponent<PlayerItemSupply>()?.ServerGrantStartingGear()` |
| `ShopManager.cs:53` | `GetComponent<PlayerItemSupply>()?.ServerClearHeldItems()` |
| `PlayerHeldItemView.cs` | 대기 루프 → `NetworkRefResolver.WaitAsync` |
| `docs/GDD.md` (2곳) | `PlayerLoadout.m_startingGear` → `PlayerItemSupply.m_startingGear` |

⚠ **로그 접두사 변경:** 지급·회수 로그가 `[PlayerLoadout]` → `[PlayerItemSupply]`.

## 8. 검증 방법

테스트 코드가 없으므로 **콘솔 로그 3개를 계측기로 썼다.** `m_startingGear`가 3개이므로 기대값은 전부 3:

```
[PlayerItemSupply] 기본 장비 지급 — client 0, 3개 (Game)
[PlayerItemSupply] 보유 아이템 회수 — client 0, 3개
[PlayerLoadout] 플레이어 정리와 함께 소지 아이템 3개 디스폰
```

이 리팩토링은 "세는 방식을 한 곳으로 모은 것"이라 **틀렸다면 거의 항상 숫자로 드러난다.**

핵심 시나리오(전부 통과 확인):

1. 게임 씬 진입 → 지급 3개, 인벤토리 3칸, 첫 칸 자동 장착
2. 3칸 채운 뒤 4번째 줍기 → **거부** (`Count` 판정)
3. 묶은 밧줄 버리기 → **거부** / 여분 있으면 **허용** (`CountOf<Rope>()`, #390)
4. 상점 복귀 → 회수 3개 → 다음 라운드 재지급, "이미 보유 중" 경고 없음
5. 로비 복귀 → 씬 원점에 아이템 안 남음 (#395)
6. 원격 클라 지급 (`NetworkRefResolver` 경로를 실제로 타는 유일한 케이스)

## 9. 하지 않은 것

- **줍기/버리기 분리** — §6 참고. 형제 컴포넌트 5개에 걸려 있어 배선만 복제된다.
- **`ResolveDropPosition` 추출** (드롭 위치 정책, ~20줄) — 파일 하나 늘릴 만한 이득이 아니다.
- **단위 테스트** — asmdef가 없어 붙일 자리가 없다. 다만 `LoadoutSlots<T>`처럼 Unity 비의존 객체를
  늘려 두면 나중에 붙일 수 있다.
