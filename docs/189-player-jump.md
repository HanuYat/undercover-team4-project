# #189 — 플레이어 점프 구현 정리

- **날짜:** 2026-07-26 (최초) / 2026-07-27 (후속 — 5-6, 5-7)
- **브랜치:** `feature/189-player-jump`
- **범위:** 점프 이동·애니메이션 + 공중 앉기(웅크림). GDD에는 점프 항목이 없어(`docs/` 전체 검색 0건) 신규 기능으로 설계했다.

---

## 1. 한 줄 요약

점프 로직 자체가 없던 상태(`Assets/Scripts` 전체에 "jump" 문자열 0건)에서 시작해, **입력 → 수직 임펄스 → 애니메이터 상태 머신 → 원격 동기화**를 붙였다. 클립·입력 액션·리그는 이미 프로젝트에 다 있어서 새 에셋 임포트는 없었다. 구현 중 플레이 모드 계측으로 **천장 붙음 버그**를 찾아 함께 고쳤고, 이후 체감 피드백 4건을 전부 수치로 원인을 특정해 수정했다.

후속 플레이테스트에서 2건을 더 잡았다 — **클라 화면에서 호스트만 앉기 자세가 안 보이던 회귀**(5-6)와 **공중에서 웅크린 채 움직일 때 몸이 굳어 보이던 문제**(5-7).

---

## 2. 기반 구조 (작업 전 파악한 것)

프리팹에 **Rigidbody가 없다.** `CharacterController` 하나로 PhysX 시뮬레이션과 무관하게 중력을 직접 적분하는 구조다.

```
CharacterController: height=2, radius=0.3, center=(0, 1.03, 0)
                     slopeLimit=45°, stepOffset=0.3, skinWidth=0.03
Rigidbody: 없음 / Animator: applyRootMotion=False
```

[`HandleMove()`](../Assets/Scripts/Player/PlayerMovement.cs)는 `FixedUpdate`가 아니라 **`Update`**에서 돈다:

1. 수평 — 입력을 정규화해 속도를 바로 곱한다(가감속·관성 없음)
2. 수직 — `isGrounded`면 `-2f`로 클램프(접지 유지용 상시 하향 압력) 후 `m_gravity * dt` 적분
3. 셋을 **한 벡터로 합쳐 `Move()` 한 번**
4. 넉백 지수 감쇠

`m_gravity`는 `[SerializeField] -9.81f`로 프리팹에 박힌 별개 필드다 — `Physics.gravity`를 바꿔도 안 따라온다.

**핵심 발견:** [`AddKnockback()`](../Assets/Scripts/Player/PlayerMovement.cs)이 상향 성분을 `m_verticalVelocity`에 `Max`로 병합하고 있었다. 즉 **수직 임펄스를 쏘는 경로가 이미 있었고**, 점프는 그 채널을 그대로 쓰면 되는 일이었다.

### 이미 있던 것

| 항목 | 상태 |
|---|---|
| 점프 클립 | `Kevin Iglesias/.../Male/Movement/Jump/` — `Jump01 - Begin`(0.67s) / `Fall01`(1.0s, 루프) / `Jump01 - Land`(0.60s) |
| 리그 | 전부 **Humanoid** — 기존 Walk/Run/Crouch/Knockdown과 동일, 리타깃 그대로 먹음 |
| 입력 | `InputSystem_Actions.inputactions`에 `Jump`(Button, Space) **이미 정의됨**, 다만 아무도 안 물고 있었음 |

---

## 3. 설계

### 3-1. 컴포넌트 분리

[`PlayerJump`](../Assets/Scripts/Player/PlayerJump.cs) 신설. `PlayerCrouch`(#236)와 같은 서버 권위 패턴을 따르되 역할을 좁혔다:

- 입력 수집(`OnJumpPressed` → 단발 요청)과 **공중 여부 전파**만 담당
- 실제 수직 임펄스는 `PlayerMovement`가 준다 — `CharacterController`를 만지는 곳을 한 군데로 유지하려는 것

### 3-2. 원격 피어에 공중 상태를 어떻게 알릴 것인가

**문제:** `PlayerMovement`는 `OnNetworkSpawn`에서 오너가 아니면 `enabled = false`다. 즉 원격 인스턴스의 `CharacterController`는 `Move()`를 타지 않아 **`isGrounded`가 영원히 갱신되지 않는다.** 위치만 오너 권한 `NetworkTransform`으로 흘러온다.

**해결:** 접지를 아는 오너가 **변화 시점에만** 서버로 보고(`ServerRpc`) → 서버가 `NetworkVariable<bool>`로 전파. 점프 1회당 RPC 2회(이륙/착지).

```csharp
public bool IsAirborne =>
    IsSpawned && !IsServer && !IsOwner ? m_isAirborneSynced.Value : m_isAirborne;
```

**오너를 실참조 쪽에 넣은 것이 `PlayerCrouch`와 다른 점이다.** 앉기는 홀드라 RTT만큼 늦어도 티가 안 나지만, 점프는 누른 즉시 발이 떠야 해서 왕복을 기다리면 이륙 모션이 눈에 띄게 밀린다. 이동 자체가 클라 권위라 접지의 진실값은 오너가 쥐고 있으므로 권위 모델과도 맞다.

### 3-3. 트리거가 아니라 bool

애니메이터는 `Airborne`(bool) 하나로 구동한다. 트리거는 원격 피어에서 유실·중복되기 쉽지만 bool은 폴링이라 안전하다. 이륙/체공/착지 구분은 애니메이터가 알아서 나눈다.

---

## 4. 애니메이터 최종 구조

파라미터: `MoveX` `MoveZ` `Down` `Crouch` **`Airborne`**

상태 3개. **전용 착지 상태는 없다**(5-3 참고).

```
Locomotion ─(Airborne)────────────→ Jump_Begin ─(exitTime 0.8)→ Jump_Air
Crouch ────(Airborne + Crouch)────→ Jump_Air_Crouch        ← Begin 건너뜀
Crouch ────(Airborne)─────────────→ Jump_Begin             ← 앉기 뗀 채 난간 낙하용 보루
Begin/Air ─(Crouch)───────────────→ Jump_Air_Crouch
Jump_Air_Crouch ─(!Crouch)────────→ Jump_Air
공중 3상태 ─(!Airborne + !Crouch)─→ Locomotion             ← 착지, 0.1초 블렌딩
공중 3상태 ─(!Airborne + Crouch)──→ Crouch
공중 3상태 ─(Down)────────────────→ Knockdown_Fall
```

**전환 등록 순서가 곧 우선순위다.** 착지 전환을 다른 전환보다 먼저 달아야, 착지 프레임에 앉기 키를 누르거나 떼도 공중 상태끼리(`Air ↔ Air_Crouch`) 한 번 들렀다 오는 군더더기가 안 생긴다. 같은 이유로 `Crouch → Jump_Air_Crouch`가 `Crouch → Jump_Begin`보다 앞에 있다.

공중 웅크림(`Jump_Air_Crouch`)은 **지상 앉기와 같은 구성의 블렌드 트리**다 — 중앙에 Crouch Idle, 반경 `k_walkParam`(1)에 CrouchWalk 8방향. 전용 턱(tuck) 클립이 에셋에 없어 앉기 클립을 재사용하되, 단일 Idle이 아니라 트리로 물려 공중 이동 중에도 자세가 살아 있게 했다 (5-7).

> ⚠️ **Animator 창에서 손으로 고치지 말 것.** `Tools > Player > Create Animator Controller`를 다시 돌리면 점프 상태·전환이 통째로 재생성돼 수정이 날아간다. `Jump_Air_Crouch`는 상태만 살아남고 전환과 블렌드 트리 내용은 매번 다시 채워진다(5-7). 값 조정은 [`SetupJumpStates()`](../Assets/Scripts/Editor/PlayerAnimatorControllerBuilder.cs) 안에서 하고 메뉴를 재실행하는 흐름이다. (Locomotion 블렌드트리·Crouch·Knockdown도 같은 구조)

---

## 5. 발견해서 고친 문제들

5-1 ~ 5-5는 플레이 모드에서 프레임 단위로 계측해 원인을 특정했다. 체감 보고만으로 추측하지 않은 것이 결정적이었다. 5-6 ~ 5-7은 후속 플레이테스트 보고에서 시작해 **코드 경로를 따라가 원인을 짚은** 건이다(계측 아님 — 근거의 성격이 다르니 구분해 둔다).

### 5-1. 천장에 머리를 박으면 0.4초간 매달림

```
0.047s y=0.753 vY=3.76   ← 위로 밀고 있는데
0.199s y=0.753 vY=2.24   ← y가 전혀 안 변함 (천장에 막힘)
0.426s y=0.753 vY=0.06   ← 상승 속도가 다 소진될 때까지
0.446s y=0.747 vY=-0.20  ← 그제서야 낙하
```

`CharacterController`는 이동이 막혀도 속도를 스스로 지우지 않는다. 접지 쪽 `-2f` 클램프와 대칭인 처리를 위쪽에 넣었다:

```csharp
if ((m_controller.collisionFlags & CollisionFlags.Above) != 0 && m_verticalVelocity > 0f)
    m_verticalVelocity = 0f;
```

수정 후 접촉 즉시(`vY 1.78 → 0.00 → -0.25`) 낙하로 전환. 천장이 낮은 실내(경찰서)에서 바로 드러나는 문제였다.

### 5-2. 착지 모션이 120ms 늦게 재생됨

```
0.677s grounded=T IsAirborne=T   ← 발 닿았는데 플래그는 아직 T
0.698s grounded=T IsAirborne=F   ← 이제 전환 시작
0.797s                    Land   ← 전환 완료
```

| 원인 | 지연 |
|---|---|
| `ReportGrounded`가 `Move()` **이전**의 `isGrounded`를 읽음 | ~21ms |
| `Air → Land` 크로스페이드 100ms 고정 | ~99ms |

`isGrounded`는 `Move()`가 갱신하므로 프레임 앞에서 읽으면 직전 프레임 결과가 나온다. 보고를 `Move()` 뒤로 옮겼다. **점프 가능 판정은 반대로 프레임 앞의 값이 맞다** — 그 시점의 마지막 확정 접지이기 때문. → 120ms → 41ms.

### 5-3. 착지 상태가 다음 동작을 막음 → `Jump_Land` 제거

착지 클립을 한 번 끼우면 그 길이만큼 다음 동작이 밀려 조작이 굼떠 보인다. 배속을 1.6배로 올려도 근본 해결이 안 돼서 **전용 착지 상태를 없애고** 공중 상태에서 지상 상태로 직행하게 했다.

```
0.667s grounded=T   Air
0.688s              Air=>Locomotion   ← 착지 즉시 다음 동작으로
0.789s              Locomotion
```

착지 후 정착까지 **380ms → 122ms**. `k_landBlend`(0.1초) 블렌딩이 곧 착지 연출이다. 0.03초까지 줄여봤지만 체공 자세에서 Idle로 자세가 튀어서 0.1초로 되돌렸다.

### 5-4. "점프 힘이 떨어진다" — 실제로는 카메라 문제였다

| | 초기 vY | 최고 높이 |
|---|---|---|
| 일반 점프 | 3.96 m/s | 0.742m *(천장에 막힘)* |
| 상승 중 앉기 | 3.96 m/s | **0.846m** |
| 앉은 채 점프 | 3.96 m/s | 0.829m |

임펄스는 동일했고, 앉으면 오히려 천장을 안 박아 더 높이 떴다. 진짜 원인:

```
0.129s 발y=0.452  카메라월드=2.052   ← 잘 올라가는 중
0.203s 발y=0.630  카메라월드=1.865   ← 발은 오르는데 카메라는 내려감
0.260s 발y=0.730  카메라월드=1.590   ← 출발 높이보다 낮아짐
```

공중에서 앉으면 `CrouchHeadDrop`이 0→0.8로 커지면서 **시점이 0.8m 꺼져** 상승분을 잡아먹었다. 1인칭에서는 정확히 "뛰다 말고 힘이 빠지는" 그림.

**해결:** 공중에서는 앉기에 따른 시점 높이 변화를 **얼린다.** 몸이 웅크리는 건 다리를 접는 동작이지 머리가 내려가는 게 아니다. 이륙 시점의 자세를 유지하므로 앉은 채 뛰면 앉은 시점, 서서 뛰면 선 시점으로 난다.

지상 복귀 시 스냅을 막으려고 `PlayerCrouch.HeadDropRate`(= `0.8m ÷ 0.12초` = 6.67 m/s)를 공개해 **콜라이더와 같은 속도로** 쫓아가게 했다. 속도가 같으니 지상에서 추가 지연이 안 붙는다 — "카메라를 한 번 더 감쇠하지 않는다"는 #236 주석의 취지를 유지한 것.

수정 후 카메라 월드 높이 **1.675 → 2.431 → 1.669**, 깨끗한 포물선.

### 5-5. 앉은 채 점프 시 선 자세 이륙이 75ms 스침

```
0.017s Crouch=>Begin
0.073s Begin           ← 선 자세 이륙이 여기서 노출
0.092s Begin=>AirCrouch
```

`Crouch → Jump_Air_Crouch` 전환을 `Crouch → Jump_Begin`보다 **먼저** 달아 가로챘다. 수정 후 20ms 간격 계측에서 `Begin`이 한 프레임도 안 나온다.

### 5-6. 클라 화면에서 **호스트만** 앉기 자세가 안 보임 (회귀, 2026-07-27)

**증상:** 이 브랜치 이전에는 클라이언트에서 호스트가 앉는 게 보였는데 안 보이게 됐다. 클라↔클라는 정상 — **호스트만** 안 보인다.

**원인:** 6장에서 애니메이션 소스를 `IsCrouching` → `IsCrouchRequested`로 바꿨는데(공중 웅크림을 위해 필요한 변경), 원격 피어용으로 신설한 `m_isCrouchRequestedSynced`를 채우는 트리거가 `UpdateServerState()` 안의 **발산 검사**였다:

```csharp
// 문제가 된 코드
if (m_isCrouchRequested != m_crouchRequested)   // ← 호스트에서는 영영 false
{
    m_isCrouchRequested = m_crouchRequested;
    if (IsSpawned && IsServer)
        m_isCrouchRequestedSynced.Value = m_crouchRequested;
}
```

호스트는 **오너이자 서버**라 두 필드가 같은 호출 안에서 동시에 채워진다:

| 순서 | 코드 | 결과 |
|---|---|---|
| 1 | `HandleCrouchInput` — 오너 자격 낙관 반영 | `m_isCrouchRequested = true` |
| 2 | 바로 다음 줄 `RequestCrouchServerRpc` — **호스트 ServerRpc는 인라인 실행** | `m_crouchRequested = true` |
| 3 | 다음 `Update`의 발산 검사 | 두 값이 이미 같음 → **동기화 변수 미기록** |

클라가 오너일 때는 오너 인스턴스와 서버 인스턴스가 별개라 서버 쪽 `m_isCrouchRequested`가 `false`로 시작한다 — 그래서 발산이 생기고 정상 전파됐다. 기존에 쓰던 `IsCrouching`(`m_isCrouchingSynced`)은 오너십과 무관하게 서버가 항상 기록해서 호스트도 잘 보였던 것.

**해결:** 전파를 `Update`의 조건부에서 떼어내, RPC가 받는 즉시 무조건 쓰게 했다. `PlayerJump.ReportAirborneServerRpc`가 이미 쓰던 형태 — 그래서 점프 애니메이션은 같은 함정을 피해 갔다.

```csharp
[ServerRpc]
private void RequestCrouchServerRpc(bool pressed)
{
    m_crouchRequested = pressed;
    m_isCrouchRequested = pressed;              // 서버 인스턴스의 자세 값
    m_isCrouchRequestedSynced.Value = pressed;  // 오너가 곧 서버(호스트)여도 반드시 전파
}
```

**교훈:** 호스트에서는 "오너 로컬 낙관 반영"과 "서버 권위 값"이 **같은 필드를 공유한다**. 그 필드의 변화를 전파 트리거로 삼으면 호스트에서만 새어 나간다. 낙관 반영 필드와 전파 트리거를 섞지 말고, 전파는 서버 진입점(RPC)에서 무조건 할 것.

### 5-7. 공중에서 웅크린 채 움직이면 몸이 굳어 보임 (2026-07-27)

**증상:** 앉아서 점프한 뒤 이동하면 자세가 앉은 Idle로 굳어 있다. 발은 안 움직이는데 몸만 옆으로 미끄러진다.

**원인:** `Jump_Air_Crouch`가 `Crouch01_Idle` **단일 클립**이었다. 지상 앉기(`Crouch`)는 8방향 CrouchWalk 블렌드 트리인데 공중 웅크림만 정적 클립이라, 이동 파라미터가 아무리 들어와도 반응할 모션이 없었다.

**해결:** 지상 앉기와 같은 구성의 블렌드 트리를 물렸다. `MoveX`/`MoveZ`는 `PlayerAnimationDriver`가 **위치 변화량**으로 계산하므로 공중에서도 그대로 도는 값이라, 드라이버는 손댈 게 없었다.

구현에서 걸린 건 빌더의 "지웠다 다시 만들기" 패턴과의 충돌이었다:

| 문제 | 처리 |
|---|---|
| `RemoveJumpStates`가 매 실행 상태를 삭제 → 블렌드 트리 서브에셋이 컨트롤러에 **고아로 남음** | `Jump_Air_Crouch`만 상태를 남기고 **전환만 비운다**. 나가는 전환(착지 → Locomotion/Crouch)은 목적지가 점프 상태가 아니라 기존 루프가 못 지우므로 명시적으로 비움 |
| 지상 앉기 트리를 그대로 공유하면? | **공유하지 않고 따로 하나 더** 둔다 — 한쪽 상태를 지울 때 다른 쪽 모션까지 날아갈 위험. 대신 `FillCrouchBlendTree()`로 구성을 한곳에서 맞춘다 |

검증: 갱신된 `Player.controller`에 블렌드 트리가 정확히 3개(`Locomotion` / `Crouch` / `Jump_Air_Crouch`), 공중 웅크림 트리에 모션 9개, `MoveX`/`MoveZ` · `FreeformDirectional2D`. 고아 서브에셋 없음.

**남긴 어색함:** 공중 이동 속도는 앉기 속도가 아니라 걷기/달리기 속도라(9장 마지막 항목), 앉은 걷기 클립이 실제 이동보다 느린 발놀림으로 재생된다. 발이 땅에 닿아 있지 않아 발 미끄러짐으로는 안 보인다 — 신경 쓰이면 공중 웅크림일 때만 앉기 속도로 정규화하도록 드라이버를 손보면 된다.

---

## 6. 공중 앉기 동작 명세

앉기를 **상태**와 **자세**로 쪼갰다. `PlayerCrouch`에 `IsCrouchRequested`를 신설:

| | 따르는 값 | 공중에서 |
|---|---|---|
| **이동 속도** | `IsCrouching` | 보류 (착지해야 걸림) |
| **콜라이더 · 카메라** | `IsCrouchRequested` | 즉시 반영 |
| **애니메이션 자세** | `IsCrouchRequested` | 즉시 반영 |

지상에서는 두 값이 같아 기존 동작은 그대로다. `IsCrouchRequested`도 오너는 왕복을 기다리지 않는다(체공이 0.7초라 기다리면 자세가 거의 안 보인다) — 남의 화면용으로는 별도 `NetworkVariable`로 전파한다. **이 전파 경로에서 호스트 전용 버그가 났다 (5-6).**

계측 확인:

```
0.275s h=1.87 req=T crouching=F   ← 공중, 콜라이더 줄기 시작
0.387s h=1.20 req=T crouching=F   ← 0.12초만에 축소 완료
0.673s h=1.20 req=T crouching=T   ← 착지 시 앉기 상태 걸림
```

앉은 채 점프도 허용했다(`PlayerMovement`의 `!IsCrouching` 가드 제거). 애니메이터가 `Crouch → Jump_Air_Crouch`로 웅크린 자세를 유지한다.

---

## 7. 물리 적용 횟수 검증

**정적:** `Assets/Scripts` 전체에서 `Move()`/`SimpleMove()` 호출은 [`PlayerMovement.cs:457`](../Assets/Scripts/Player/PlayerMovement.cs) **단 한 곳**. 수평 입력·넉백·수직속도가 한 벡터로 합쳐져 한 번만 넘어간다. 루트 트랜스폼을 직접 쓰는 두 곳(`SetPose`, `UpdateCarriedFollow`)은 **둘 다 `CharacterController`를 끈 상태**다.

**런타임:** 점프와 넉백을 동시에 걸고 매 프레임 `Δ위치`와 `합성속도 × dt`를 비교 — 충돌이 없는 모든 프레임에서 **x·y·z 동시에 비율 1.000**.

```
0.062s dt=0.0211 | Δ=(0.0442, 0.0750, 0.0265)  예상=(0.0442, 0.0750, 0.0265)  비율 1.000/1.000/1.000
```

Move가 두 번 불리면 2.0, 중력이 두 번 적분되면 y만 어긋나는데 둘 다 없다.

---

## 8. 현재 튜닝값

| 값 | 위치 | 현재 |
|---|---|---|
| 점프 높이 | `PlayerJump.m_jumpHeight` (프리팹) | **0.8m** (초기속도 3.96 m/s, 체공 0.81초) |
| 착지 블렌딩 | `PlayerAnimatorControllerBuilder.k_landBlend` | 0.1초 |
| 앉기 콜라이더 | `PlayerCrouch.m_crouchHeight` | 1.2m |
| 앉기 블렌딩 | `PlayerCrouch.k_blendDuration` | 0.12초 |

---

## 9. 남은 작업 / 알려진 이슈

### 검증 못 한 것

- **실제 Space 키 입력** — 배선(`m_jumpAction = Player/Jump`)과 구독 코드는 확인했지만, 계측은 전부 리플렉션으로 입력 경로를 우회했다. 게임 뷰 포커스가 필요해 MCP로는 누를 수 없었다. **수동 확인 필요.**
- **원격 피어 시야** — Multiplayer Play Mode로 가상 플레이어 2명을 띄워, 남의 점프·공중 앉기가 제대로 보이는지 확인 필요. 5-6의 호스트 버그가 여기서 나왔으니 다음 4가지를 나눠 볼 것: ① 클라 화면에서 **호스트**의 앉기·공중 웅크림, ② 호스트 화면에서 **클라**의 같은 동작(기존 경로 회귀 확인), ③ 앉은 채 접속해 있는 플레이어를 **늦게 들어온 클라**가 볼 때 콜라이더·자세 일치, ④ 공중에서 이동 중인 웅크림이 남의 화면에서도 걷기 모션으로 보이는지.

### 설계상 남긴 것

- **`Jump_Air`가 거의 안 보인다.** 이륙 클립(0.67초)이 체공 시간(0.67초)을 거의 다 먹어서 체공 루프가 20ms만 재생된다. 선택지 셋:
  1. `Jump_Begin` 상태 speed를 1.5배 — 높이·맵 영향 없음, **추천**
  2. `Begin → Air` exitTime 0.8 → 0.35 — 이륙이 잘리는 느낌 가능
  3. `m_jumpHeight` 0.8 → 1.3m — 모션은 가장 자연스럽지만 맵 올라타기 위험 증가
- **크라우치 점프 가능.** 앉으면 콜라이더가 1.2m라 천장을 안 박아 0.742m → 0.83m까지 뜬다. 서서 뛸 땐 못 올라가는 곳에 올라간다. 자세와 콜라이더 일치를 우선해 의도적으로 열어둔 것.
- **공중 이동 속도가 앉기 속도가 아니다.** `IsCrouching`이 공중에서 false라, 앉아 기다가 점프하면 체공 중엔 5~8 m/s로 움직인다. 연속으로 앉은 점프를 하면 콜라이더는 작은데 빠르게 이동하는 토끼뜀이 된다. 막으려면 `PlayerMovement`의 속도 선택도 `IsCrouchRequested`로 바꾸면 되지만(한 줄), 그러면 "앞으로 뛰다가 공중에서 앉기"에서 체공 이동이 뚝 느려진다. 부수적으로 5-7의 공중 웅크림 걷기 모션도 이 속도 불일치를 물려받는다(발놀림이 실제 이동보다 느림).
- **맵 검증 미실시.** GDD에 점프가 없어 맵이 점프를 전제로 설계되지 않았다. 올라타면 안 되는 구조물이 나오면 `m_jumpHeight`부터 낮출 것.

### 무관한 사항

- `Main Scene.unity`에 **Rope 프리팹 인스턴스**가 `(-2.7, 1.2, 47.96)`에 추가된 미커밋 변경이 있다(66줄). 이 작업과 무관해 **커밋에서 제외**했다.
- Main 씬 로드 시 `BoxCollider does not support negative scale or size` 에러 14건. 맵 지오메트리(음수 스케일)의 기존 문제로 이 작업과 무관.
