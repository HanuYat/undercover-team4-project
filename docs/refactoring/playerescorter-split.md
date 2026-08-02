# PlayerEscorter 책임 분리 — partial 해소

> 작성: 2026-08-02 · 브랜치 `refactoring/criminalassigner-hotfix` (커밋 `0d85210` → `6cdf6ff`)
> 판단 기준은 [PlayerLoadout 분리 §2](playerloadout-split.md#2-판단-기준--unity에서-무엇이-위험한가)와 동일.
> 게임 동작 변화 없음. 규칙의 정본은 [architecture.md](../architecture.md).

## 1. 한 문장 요약

`partial` 2파일 **1,022줄**(실코드 623)짜리 한 클래스를 컴포넌트 3개로 갈랐다.
`PlayerEscorter`는 실코드 **247줄**이 됐고, `partial`은 없앴다.

## 2. 이 문서가 남는 이유 — partial의 반례

[PlayerLoadout 분리 §2-2](playerloadout-split.md#2-2-partial-분할은-목적지가-아니라-도구다)에
`partial`은 결합도를 줄이지 않는다고 적어 뒀는데, 이 파일이 그 실증이다.

`PlayerEscorter.cs`(436줄) + `PlayerEscorter.RopeDrag.cs`(586줄)로 **이미 나뉘어 있었지만**:

- `RopeDrag.cs`가 본체의 `m_channel`·`Loadout`·`IsCarryingPlayer`·`IsInRange`·`NotifyOwner`·
  `m_channelSeconds`를 자유롭게 썼고
- 본체가 `RopeDrag.cs`의 `m_tethered`·`TetheredCount`·`ReleaseDrag`·`TickTetherCleanup`을 자유롭게 썼다

**경계가 없었다.** 파일만 둘인 한 덩어리였고, 그래서 이 저장소에서 가장 큰 클래스로 남아 있었다.
→ 이번에는 파일이 아니라 **컴포넌트**로 갈랐고, `partial` 자체를 제거했다.

## 3. 이 파일만의 하드 제약 — NetworkList / NetworkVariable

```csharp
private readonly NetworkList<RopeTether> m_tetheredSynced = ...;
private readonly NetworkVariable<float> m_dragSpeedFactorSynced = ...;
```

둘 다 **`NetworkBehaviour`에만 살 수 있다.** `HeldItems`(상태가 Transform 계층)나
`SuspectRevealer`(상태가 남의 List)처럼 **평범한 C# 객체로 뽑을 수 없었다** —
여기는 동기화 자체가 상태다.

→ 이번만은 **컴포넌트 분리가 유일한 선택지**였다. 다행히 대상이 프리팹이고 `SerializeField`가
4개뿐이라 [CriminalAssigner](criminalassigner-split.md)(씬 12개)보다 훨씬 유리했다.

## 4. 절단면을 어디서 찾았나 — 두 번의 수정

### 첫 안 (기각)

목록(150) + 물리(80)를 빼고 나머지를 남기는 3분할. **잡동사니 390줄이 남는 게 문제**였다 —
요청·RPC·검증·채널링·인계·로그가 이름 붙일 수 없는 덩어리로 뭉친다.

### 채택안 — 구동 주체 축

| | `PlayerEscortCommands` | `PlayerEscorter` |
|---|---|---|
| 언제 도나 | 플레이어 입력이 올 때 (좌클릭·E) | 서버에서 **매 프레임** |
| 무엇을 | 요청 5 · RPC 5 · 진입 판정 · 채널링 2종 · 인계 | 목록 2벌 · 동기화 · 정리 · 끊김 · 놓기 |

의존은 **`Commands → Escorter` 한 방향**뿐이고 순환이 없다. Commands는 목록을 직접 만지지 않고
`AddTether`/`RemoveTether`/`ReleaseDrag`로 위임한다.

**이름은 상태 쪽이 가져갔다** — "Escorter(연행자)"가 답해야 할 질문은 "누구를 연행 중인가"이고,
`static FindEscorterOf`도 그 상태를 묻기 때문이다.

### 2단계 — 소비자 축

남은 `PlayerEscorter`에서 무게·목줄(#398)을 다시 뺐다. 근거는 **소비자가 다르다**는 것:

- 연결 목록 → 표시(`RopeDragView`)·검증(`Rope`·`NpcSubdueInteractable`)·인계(`HqDropoffTerminal`)
- 무게·목줄 → **`PlayerMovement`만**

## 5. 단일 진실을 지킨 두 값

물리를 떼면서 양쪽이 같은 값을 봐야 하는 것이 둘 나왔다. 둘 다 `PlayerEscorter`에 남기고
`internal`로 빌려준다:

| 값 | 왜 하나여야 하나 |
|---|---|
| `RopeBreakDistance` | 끊김 판정(Escorter)과 목줄 반경(Load)이 같은 값을 봐야 한다. 따로 두면 **"줄은 끊기는데 이동은 안 막히는"** 구간이 생긴다 |
| `IsLeashedTo` | 끊김 판정(서버)과 이동 제한(오너)이 같은 기준을 봐야 한다는 규칙이 **원래 주석에 명시돼 있었다.** 목줄 여부는 연결의 성질이라 연결 소유자가 답한다 |

## 6. 실행 순서를 명시적으로

```csharp
// PlayerEscorter.Update — 서버 전용
TickTetherCleanup();       // 끊긴 연결 정리 (목록이 바뀐다)
Load?.ServerTickWeight();  // 그 결과로 무게·자리 재계산
```

예전에는 `TickTetherCleanup` 끝에서 `AssignDragSlotsAndSpeed`를 부르는 **암묵적 순서**였다.
정리가 먼저 끝나야 사라진 대상이 무게에 잡히지 않는다 —
[PlayerMovement](playermovement-split.md#6-실행-순서를-명시적으로-잡았다)가
`PlayerLook`·`PlayerTowedMotion`을 돌리는 방식과 같다.

## 7. 호출부 — 4곳이 참조를 둘로 나눴다

명령과 상태를 **둘 다 쓰는** 파일이 4개였다. 각각 참조를 분리했다(파사드로 감싸지 않았다 —
그러면 방금 그은 경계가 도로 흐려진다):

| 파일 | 명령 (`PlayerEscortCommands`) | 상태 (`PlayerEscorter`) |
|---|---|---|
| `Rope` | 요청 5개 · `CanUnrope` · `ServerCancelChannel` | `IsAtRopeCapacity` |
| `NpcSubdueInteractable` | `RequestRopeResume` | `IsTetheredTo` · `IsDraggingNpc` |
| `HqDropoffTerminal` | `RequestDeliver` | `TetheredCount` · `GetTetheredNpc` |
| `PlayerInteractor` | `RequestRelease` | `IsDraggingNpc` |

`PlayerMovement`는 2곳(`DragSpeedFactor`·`ConstrainByTautRopes`)이 `RopeDragLoad`로 갔다.
`FindEscorterOf`만 쓰는 나머지 6곳(`ArrestJudge`·`JailbreakEvent`·`MisdemeanorLoiterer`·
`SpawnedNpcEvent`·`SuspectRevealer`)은 **무변경**이다.

## 8. 결과

| 파일 | 줄 수 | 실코드 |
|---|---:|---:|
| `PlayerEscortCommands.cs` | 521 | 348 |
| `PlayerEscorter.cs` | 426 | **239** |
| `RopeDragLoad.cs` | 150 | 88 |

`PlayerEscorter` 실코드 **623 → 239줄 (−62%)**.
(줄 수는 main 리베이스 이후 기준 — §9의 중복 제거가 반영돼 있다.)

**프리팹:** 컴포넌트 2개를 목록 **끝**에 추가 → `NetworkBehaviour` 22·23번.
`PlayerEscorter`는 6번 그대로라 기존 `NetworkBehaviourId`가 밀리지 않았다.
**RPC 5개가 통째로 옮겨갔지만 기존 라우팅은 무영향.** `SerializeField` 3개 이동,
`GlobalObjectIdHash` 불변 확인.

## 9. main 리베이스로 흡수한 것 (PR #475)

작업 중 Scanner 정리(#475)가 main에 머지되면서 이 분할과 정면으로 겹치는 변경 둘이 들어왔다.
리베이스하며 그쪽 방식으로 맞췄다.

| main의 변경 | 이 분할에 미친 영향 |
|---|---|
| `NotifyOwner`+`OwnerLogRpc`를 `ChanneledInteractionBehaviour`로 승격 (+`toast` 인자) | 분할하며 양쪽에 복제해 뒀던 쌍을 **삭제**하고 기반 것을 쓴다 |
| 사거리+가시선을 `PlayerInteractor.IsWithinReach`로 통합 | `Commands`의 `IsInRange`·`AimOriginPosition`을 그 정적 판정에 위임 |

**첫 줄은 이 문서가 원래 "별도 정리 대상"으로 남겨 뒀던 항목이다** — 팀원이 먼저 해결했고,
그 덕에 이 분할이 남길 뻔한 중복이 없어졌다.

### 그 결과로 생긴 어긋남 하나

`PlayerEscorter`는 채널링을 하지 않으므로 분할 직후엔 평범한 `NetworkBehaviour`였다. 그런데
`NotifyOwner`가 채널링 기반으로 올라가면서, 줄 끊김·놓기를 오너에게 알리려면
`ChanneledInteractionBehaviour`를 상속해야 한다 — **이름이 내용과 안 맞는다.**

복제본을 되살리는 것(팀원이 방금 없앤 중복을 다시 만드는 것)보다 낫다고 판단해 상속을 택했고,
근거를 클래스 주석에 ⚠로 남겼다. 근본 해결은 기반을 "채널링 게이지"와 "오너 피드백"으로 가르는
것이지만, 그 기반은 `ItemBase`도 상속하므로 영향 범위가 넓어 **후속 과제**로 둔다.

## 10. 하지 않은 것

- **진입점 보일러플레이트 4중복** — `if (target == null) / if (!IsSpawned || IsServer) / if (!IsOwner) /
  if (!IsTargetNetworkReady) / Rpc(...)` 패턴이 4번 반복된다. 델리게이트로 뽑으면
  `Action<NpcController>` + `Action<NetworkObjectReference>`를 넘기는 형태가 되어 **읽기가 더 나빠진다.**
  20줄 줄이자고 간접층을 넣을 값어치가 없다.
- **채널링 분리** — `m_channel` 하나를 합류·풀기가 공유하는 게 설계 축이다("대상 상태가 갈라 주므로
  채널 하나면 충분하다"는 주석이 근거). 나누면 중복 방지가 깨진다.
- **`FindEscorterOf` 레지스트리화** — 지금 `FindObjectsByType`으로 씬 전수 순회를 하고 소비자가
  6곳이다. 레지스트리로 바꾸면 성능·구조가 나아지지만 **이번 분할과 축이 다르다.** 별도 작업.
- **단위 테스트** — 저장소에 asmdef가 없고, 이 PR의 대상은 전부 `NetworkBehaviour`·`NetworkList`에
  묶여 있어 Unity 없이 테스트할 수 없다.
