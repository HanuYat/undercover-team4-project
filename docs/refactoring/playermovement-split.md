# PlayerMovement 책임 분리

> 작성: 2026-08-01 · 브랜치 `refactoring/playermovement-hotfix` (커밋 `7123d26` → `9a47543` → `12d8d2e`)
> 판단 기준은 [PlayerLoadout 분리 §2](playerloadout-split.md#2-판단-기준--unity에서-무엇이-위험한가)와 동일.
> 의도한 동작 변경 **1건** 있음 (§5-2). 규칙의 정본은 [architecture.md](../architecture.md).

## 1. 한 문장 요약

`PlayerMovement`(646줄)에서 **추종 이동**과 **시점·카메라**를 컴포넌트로 빼내 353줄로 줄였다.
`Update()`의 3-way 분기가 1-way가 됐고, 남은 것은 "이동 권한의 소유자 + 오너 프레임 조율자"다.

## 2. 무엇이 바뀌었나

| 지표 | 이전 | 이후 |
|---|---:|---:|
| `PlayerMovement` 줄 수 | 646 (실코드 395) | **353 (실코드 191)** |
| `SerializeField` | 22 | **5** |
| `Update()` 분기 | 3-way | **1-way** |
| 중력 적분 지점 | 2곳 (중복) | **1곳** (`IntegrateGravity`) |

### 최종 구성

| 파일 | 줄 수 | 담당 | 종류 |
|---|---:|---|---|
| `PlayerMovement.cs` | 353 | 입력 이동·중력·넉백, 서버 포즈 제어, **프레임 순서 조율** | `NetworkBehaviour` |
| `PlayerLook.cs` | 231 | 시점 회전 + 카메라 자세 (신규) | `MonoBehaviour` |
| `PlayerTowedMotion.cs` | 246 | 호송·운반 추종 (신규) | `MonoBehaviour` |
| `PlayerJump.cs` | 120 | 점프 입력·공중 상태 | 기존 |
| `PlayerCrouch.cs` | 199 | 앉기 블렌딩·콜라이더 | 기존 |
| `PlayerHeadLook.cs` | 90 | 머리 본 pitch 전파 | 기존 |

## 3. 절단면을 어떻게 찾았나

파일이 **이미 그렇게 나뉘어 있었다.** 기존 `[Header]` 그룹과 `SerializeField` 22개가 거의 정확히 갈렸다:

| 기존 `[Header]` | 필드 수 | 간 곳 |
|---|---:|---|
| `이동` | 4 | `PlayerMovement` (잔류) |
| `넉백 (폭발 등 외력)` | 1 | `PlayerMovement` (잔류) |
| `운반되는 쪽 — 끌려가기 (#365)` | 5 | `PlayerTowedMotion` |
| `1인칭 시점` | 6 | `PlayerLook` |
| `다운(무력화) 시점` | 6 | `PlayerLook` |

그리고 `Update()`가 이미 3-way 분기였다 — 새 경계를 만드는 게 아니라 **있는 경계를 컴포넌트로 승격**하는 작업이었다.

### 3-1. 기본 이동은 "빼내는 것"이 아니라 "남는 것"

초기 제안은 ①기본이동 ②카메라 ③끌려가기 **셋 다 분리**였으나, ①은 잔류로 결론냈다.
`m_verticalVelocity`를 네 곳이 만지기 때문이다 — 입력 이동, 운반 추종(중력 재적분), 텔레포트 리셋, 넉백 주입.
①을 별도 컴포넌트로 떼면 ③과 서버 포즈 제어가 남의 필드를 계속 건드리게 된다
([§2-3 "가짜 분리"](playerloadout-split.md#2-3-컴포넌트-분리가-가짜-분리가-되는-경우)와 같은 함정).

**해결:** 소유자는 `PlayerMovement`로 유지하고 **연산만 빌려준다.**

```csharp
internal void MoveWithGravity(Vector3 horizontalStep);  // 운반 추종(#365)이 사용
internal void SetControllerEnabled(bool value);         // 호송 추종(#279)이 사용
```

`m_verticalVelocity`는 `private`이고 **파일 하나에만 나타난다.** 밖에서 만지는 코드는 0줄이다.

## 4. 분리 1 — `PlayerTowedMotion` (호송 #279 + 운반 #365)

### 두 모드를 왜 합쳤나

구현은 거의 공유하지 않는다:

| | 호송(#279) | 운반(#365) |
|---|---|---|
| 상황 | 오검거 페널티 — NPC 2명이 호송 | 동료가 **밧줄**로 Die 몸을 끌기 |
| CharacterController | **끔** (transform 직접) | **켬** (`Move`) |
| 중력 | 없음 | 자기가 적분 |
| 수식 | 단순 Lerp | SmoothDamp + sway |
| 앵커 | 2개 | 1개 |

**공유하는 것은 구현이 아니라 규칙이다** — 상호배제·우선순위·정리 경로·진입 시 접지 보고·입력 이동 억제.

특히 상호배제는 **실제로 대가를 치른 규칙**인데 주인이 없었다.
`WrongfulArrestPenalty.HandlePenaltyCaught`에 이런 가드가 있다:

```csharp
// 기능 정지(Die)된 몸은 접수하지 않는다 (#365). …
// 상태만 Die로 둔 채 광장으로 순간이동하고, 본부 부활 장치에 안치해 둔 몸이면 동료 눈앞에서 사라진다.
if (incap != null && incap.IsDead) return;
```

규칙이 세 군데에 흩어져 있었다:

| 위치 | 하는 일 |
|---|---|
| `WrongfulArrestPenalty.Carry.cs` | Die면 호송 접수 안 함 (진입 차단) |
| `PlayerMovement.Update()` | `m_carried`를 **먼저** 검사 — 암묵적 우선순위 |
| `PlayerHealth.ServerResetState()` | despawn이 안 타는 경로 보정 |

`Update()`의 분기 **순서**가 우선순위를 정하고 있었는데 코드 어디에도 안 적혀 있었다.
합치면 그 컴포넌트가 **규칙의 주인**이 된다.

> **나중에 갈라야 할 신호:** 세 번째 모드가 생기거나 한쪽이 150줄을 넘을 때.
> 그때는 이 컴포넌트가 이미 규칙의 주인이라 그 아래로 전략만 빼면 된다.

### 결과

```csharp
public bool IsActive { get; }   // 둘 중 하나라도 활성
public void Tick();             // 활성 모드로 분기 (호송 우선)
public void StopAll();          // 정리 한 경로
```

`OnNetworkDespawn`의 `EndCarriedFollow()` + `EndDraggedFollow()` 두 줄도 `StopAll()` 하나가 됐다.

## 5. 분리 2 — `PlayerLook` (시점 + 카메라)

### 5-1. 왜 카메라만 뗄 수 없었나

세 상태를 회전과 카메라가 **양방향으로** 주고받는다:

| 상태 | `HandleLook`이 | `UpdateCameraPose`가 |
|---|---|---|
| `m_pitch` | 마우스 입력으로 씀 | 다운 시 강제, 기상 시 범위 복귀 |
| `m_downYaw` | 쓰러진 동안 누적 | 기상 시 0으로 복귀 |
| `m_downLookTaken` | 마우스 움직이면 켬 | 기상 시 끔 |

카메라만 뗐으면 이 셋을 컴포넌트 둘이 매 프레임 왕복시켰을 것이다.

**이름이 `PlayerCamera`가 아닌 이유:** `HandleLook`이 평상시 yaw를 `transform.Rotate`로 **몸통까지** 돌린다
(쓰러진 동안만 카메라 로컬). 카메라가 아니라 조준이다.

### 5-2. 의도한 동작 변경 — 호송 중 시점 허용

예전에는 두 추종 모드의 시점 허용이 **달랐다**:

```csharp
if (m_carried)     { UpdateCarriedFollow(); UpdateCameraPose(); return; }   // HandleLook 없음
if (m_dragCarrier) { HandleLook(); UpdateDraggedFollow(); …    return; }    // HandleLook 있음
```

즉 오검거 호송 중에는 주변을 못 보고, 동료에게 운반될 때는 볼 수 있었다.
`m_carried` 분기의 주석은 *"행동불능 상태라 시점 입력은 어차피 막혀 있고(IsMovementLocked)"* 라고 설명했지만
**사실이 아니다** — `IsMovementLocked`는 `HandleMove`만 막고, `HandleLook`은 쓰러진 상태의 시점을
**명시적으로 허용**한다(#252). 진짜 이유는 그냥 `HandleLook`을 안 부르기 때문이었다.

→ 팀 결정으로 **두 모드 모두 허용**으로 통일했다. #252("쓰러져도 주변은 볼 수 있어야 한다")가
호송에서만 빠져 있던 셈이다.

이 차이는 예전엔 `Update()` 분기 구조에 묻혀 있었는데, 합치면서 드러났다 —
**리팩토링이 결정을 강제한 사례**다.

## 6. 실행 순서를 명시적으로 잡았다

`PlayerLook`·`PlayerTowedMotion` 둘 다 **자체 `Update`가 없다.** `PlayerMovement.Update`가 순서대로 돌린다:

```csharp
private void Update()
{
    if (m_towed != null && m_towed.IsActive)
    {
        m_look?.HandleLook();
        m_towed.Tick();
        m_look?.UpdateCameraPose();
        return;
    }

    m_look?.HandleLook();       // 몸통 yaw를 먼저 돌리고
    m_look?.UpdateCameraPose();
    HandleMove();               // 그 yaw를 기준으로 이동 방향을 잡는다
}
```

**각자 `Update`를 갖게 두면 안 되는 이유:** 시점이 몸통 yaw를 돌리고 이동이 그 yaw로 방향을 잡으므로
같은 프레임에서 시점 → 이동 순서가 보장돼야 한다. Unity는 컴포넌트 간 `Update` 순서를 정하지 않으므로
**이동이 이전 프레임 회전을 쓰는 1프레임 지연**이 생길 수 있다 — 스트레이프 반응이 미묘하게 둔해지는,
눈치채기 어려운 종류의 버그다.

**부수 효과:** 둘 다 `NetworkBehaviour`가 아니어도 된다. `PlayerMovement`가 비오너에서 `enabled = false`가
되므로 자동으로 오너 전용이 되고, `NetworkBehaviourId`에도 영향이 없다.

## 7. 정리 — `IntegrateGravity` (커밋 `12d8d2e`)

접지 클램프 + 중력 적분이 `HandleMove`와 `MoveWithGravity`에 **두 벌** 있었다.
리팩토링 전부터 있던 중복으로(예전엔 `UpdateDraggedFollow`에 인라인), 분리 때 이름만 붙이고 합치지는 않았던 것.

`HandleMove`가 `MoveWithGravity`를 그냥 부르지 못하는 이유는 **순서 제약**이다:

```
1. grounded 읽기        ← Move() 앞의 값이어야 함 (점프 자격 판정)
2. 클램프 + 중력 적분
3. 점프 임펄스 덮어쓰기  ← 적분 뒤여야 클램프에 안 먹힘
4. 입력·넉백 합산 후 Move()
```

→ 공통부만 `IntegrateGravity()`로 뽑고, `-2f`는 `k_groundedStickVelocity`로 승격했다.

## 8. 옮긴 public 표면

| API | 소비자 | 변경 |
|---|---|---|
| `Pitch` | `PlayerHeadLook` | → `PlayerLook.Pitch` |
| `SetLayerRecursively` | `PlayerHandView`(2), `PlayerHeldItemView` | → `PlayerLook.SetLayerRecursively` |
| 카메라 on/off·OwnBody 레이어 | `OnNetworkSpawn` | → `PlayerLook.ApplyOwnerView(IsOwner)` |
| `BeginCarriedFollow`/`End` | `PlayerPenaltyView` | → `PlayerTowedMotion.BeginEscortFollow`/`End` |
| `BeginDraggedFollow`/`End` | `PlayerCarrier` | → `PlayerTowedMotion` (이름 유지) |
| `DragFollowDistance` | `RopeDragView` | → `PlayerTowedMotion` |

`IsRoundOver`는 `internal`로 `PlayerMovement`에 남겼다 — 예외 플래그를 켜는
`SetIgnoreRoundEndFreeze`(#107)가 거기 있어 단독 소유가 맞다.

## 9. 프리팹 처리 — 겪은 문제 하나

`PlayerTowedMotion` 추가 시 **의도하지 않은 변경 2종**이 diff에 섞였다:

| 변경 | 내용 |
|---|---|
| `GlobalObjectIdHash` | `152330586` → `3828970125` |
| RectTransform 3개 | 인벤토리 슬롯 앵커·위치 |

`GlobalObjectIdHash`는 NGO가 런타임에 프리팹을 식별하는 값이라 그냥 넘길 수 없었다.
이력을 확인하니 이 값은 계속 안정적이었고 직전 프리팹 수정에서도 안 바뀌었다.
**되돌린 뒤 다시 적용했더니 재현되지 않았다** — Unity가 저장 시점에 재계산한 일회성 현상.
RectTransform 쪽은 에디터에서 저장하지 않고 있던 UI 작업이 딸려 나온 것이었다.

> **교훈: 프리팹을 저장한 뒤에는 반드시 `git diff`로 hunk 수를 확인할 것.**
> 컴포넌트 하나 추가는 hunk 3개(컴포넌트 참조 1줄 · 필드 이동 · 새 MonoBehaviour 블록)여야 한다.

`PlayerLook` 추가 시에는 프리팹 **내부 참조**(`playerCamera`·`m_ownBodyRoot`)를 MCP가 자동 해석하지 못해
`{fileID: 0}`으로 남겼다. 원본 fileID로 직접 연결한 뒤 에디터에서 실제 물림을 확인했다
(`Camera` / `SM_Gen_Chr_Robot_01`).

## 10. 검증 방법

시점·카메라는 **모든 상황에서 항상 도는 코드**라 회귀 범위가 넓다. 전부 통과 확인:

**추종 (#279/#365)**
1. 오검거 페널티 → 광장까지 끌려감 → 도착 후 **이동 복구** (CC 다시 켜짐)
2. **끌려가는 동안 마우스** → 주변 보임 (§5-2 변경)
3. 동료가 Die 몸 운반 → 흔들리며 따라감, 계단·경사 정상
4. 끌려가는 중 라운드 종료/상점 복귀 → 추종 풀림, 이동 복구 (`StopAll`)

**시점·카메라**
5. **좌우 보면서 스트레이프** → 이동 방향이 시점을 즉시 따라옴 (§6 실행 순서)
6. 앉은 채 점프 → 공중에서 카메라 높이 안 변함 (#189)
7. 다운 → 바닥 시점 → 마우스로 둘러보기 → 기상 시 각도 복귀 (#252)
8. **다른 플레이어 화면** → 내 카메라 안 켜짐, 머리 본이 pitch 추종 (#348)
9. 1인칭 손·장착 아이템 레이어 (`SetLayerRecursively`)

**중력 (§7)**
10. 낮은 천장 아래 점프 → 머리 박으면 즉시 낙하
11. 경사·계단 → 접지 깜빡임 없음
12. 낙하 중 텔레포트(오검거 매달기) → 도착지에서 안 튐

## 11. 하지 않은 것

- **`HandleMove` 추가 분할** (중력/점프/넉백/속도선택) — 넷 다 `m_verticalVelocity` 하나를 정해진 순서로
  만지고, 그 순서 자체가 버그 수정의 산물이다(#189 천장 상쇄, 접지 보고 시점). 쪼개면 순서가 컴포넌트
  경계로 흩어져 지금보다 나빠진다. **크기가 아니라 응집도 기준으로 여기가 바닥이다.**
- **`IsDraggedFollowing` 제거** — 소비자가 없는 죽은 public API지만, 기존 표면이라 그대로 옮겨 뒀다.
