# 폭발 사망 래그돌 — 설계 계획 (#506)

> **상태: 구현 진행 중.** 2026-08-05 계획 작성 → 같은 날 커밋 ①②(프리팹 래그돌 + `PlayerRagdoll`) 착수.
> **아직 검증되지 않았다** — 사망 시 시체가 마지막에 몇 번 튀는 현상을 추적 중이고, 그 과정에서 밝혀진
> 물리 쪽 함정은 **§9**에 정리했다. 진단용 임시 코드도 아직 들어 있다(§9-6).
> 게임 규칙(폭탄·데미지)의 정본은 [GDD.md](GDD.md) 6-4·7-5, 코드 구조 규칙은 [architecture.md](architecture.md).

## 1. 한눈에

지금 폭발 넉백은 **선 자세를 유지한 채 포물선으로 미끄러진다**. 여기에 래그돌을 넣되, 진입 조건을 "폭발에 맞았다"가 아니라 **"죽었다(`IncapacitationCause.Die`)"** 하나로 잡는다.

```
폭발 → 거리 감쇠 데미지 → HP 0 → Die → 래그돌 진입 → 넉백 임펄스 → 착지·정착 → (본부 이송) → 부활 블렌드 → StandUp
                        └ 살아남으면 → 기존 슬라이드 넉백 (PlayerMovement.AddKnockback) 그대로
```

진입을 Die로 통일하면 폭발만이 아니라 **진압봉 사망·납치 린치 사망도 자동으로 래그돌**이 된다. 폭발이 특별한 것은 임펄스가 붙는다는 점 하나뿐이다.

### 확정된 결정 (2026-08-05)

| # | 결정 | 근거 |
|---|---|---|
| 1 | 래그돌 진입은 **`Die` 전이 하나**. 폭발은 임펄스만 추가 | 사망 경로가 여럿인데 폭발만 특수 처리하면 나머지가 그냥 무너진다 |
| 2 | 폭발 데미지에 **거리 감쇠** 도입 (폭심 즉사 / 가장자리 생존) | 현재 균일 60 데미지로는 풀피(100)가 안 죽어 래그돌을 볼 일이 거의 없다. 감쇠를 넣으면 "가까운 놈은 날아가고 먼 놈은 밀린다"가 자동으로 나온다 |
| 3 | 죽는 본인도 **자기 몸이 보이게** 한다 (Die 동안 3인칭 + OwnBody 레이어 해제) | 오너는 자기 메시가 컬링돼 있어(§2) 지금 구조로는 자기 래그돌을 못 본다. 이 기능 재미의 절반이 거기 있다 |
| 4 | 래그돌은 **표현 계층 전용**. 뼈를 동기화하지 않는다 | #506 본문의 합의. 판정은 서버 트랜스폼이 계속 쥔다 |
| 5 | 이번 범위는 **플레이어만**. NPC 래그돌은 후속 | 검증 표면을 줄인다. 빌더·컴포넌트는 NPC 재사용을 염두에 두고 만든다 |

> ⚠ **#506 본문은 "사망 래그돌은 이번 범위 밖"으로 적혀 있다.** 위 결정 1이 그 문장을 뒤집는다. 이슈 코멘트 초안은 부록 A에 있다 — 구현 시작 전에 달 것.

---

## 2. 현재 기준점 (코드 확인 결과)

| 축 | 현재 구현 | 파일 |
|---|---|---|
| 데미지 | 반경 8m 안 **균일 60**, 거리 감쇠 없음. HP 최대 100 | [BombDevice.cs:51](../Assets/Scripts/Events/Bomb/BombDevice.cs), [PlayerHealth.cs:7](../Assets/Scripts/Player/PlayerHealth.cs) |
| 사망 | HP 0 → `Incapacitate(Die)` → `m_causeSynced`로 전 피어 동기화 | [PlayerHealth.cs:93](../Assets/Scripts/Player/PlayerHealth.cs) |
| 넉백(플레이어) | `BombExplosionView`가 각 피어에서 **자기 오너에게만** `AddKnockback` | [BombExplosionView.cs](../Assets/Scripts/Events/Bomb/BombExplosionView.cs) |
| 넉백(NPC) | 서버가 `NpcController.ServerApplyKnockback` 직접 호출 | [BombDevice.cs](../Assets/Scripts/Events/Bomb/BombDevice.cs) |
| 쓰러짐 표현 | `PlayerAnimationDriver`가 `IsProne` 폴링 → Animator `Down` → `Knockdown_Fall`→`Ground`(루프) | [PlayerAnimationDriver.cs:135](../Assets/Scripts/Player/View/PlayerAnimationDriver.cs) |
| 부활 | `HqRevivalDevice` → `ServerRevive` → `Recover()` → `Down=false` → `Knockdown_StandUp`(1.17초) → `Locomotion` | [HqRevivalDevice.cs](../Assets/Scripts/Interaction/HqRevivalDevice.cs) |
| 카메라 | 프론 시 높이 0.35m·피치 −20°·좌우 ±100° | [PlayerLook.cs:166](../Assets/Scripts/Player/Movement/PlayerLook.cs) |

### 프리팹을 뜯어보고 확인한 것 (설계에 직접 영향)

- **`Player` 루트에 Animator + CharacterController + NavMeshObstacle**이 함께 있고, 본은 `Root/Hips` 아래에 **직접** 붙어 있다. 별도 모델 래퍼 오브젝트가 없다.
- **SkinnedMeshRenderer는 `SM_Gen_Chr_Robot_01`** — `Root`의 자식이 아니라 **형제**다.
- **`PlayerLook.m_ownBodyRoot` = `SM_Gen_Chr_Robot_01`** (본이 아니다). 따라서:
  - `ApplyOwnerView`의 `SetLayerRecursively(OwnBody)`가 **뼈를 덮지 않는다** → #506 본문이 걱정한 "오너만 뼈 레이어가 다르다" 함정은 없다.
  - 대신 **뼈는 Default 레이어**라, 콜라이더를 그냥 붙이면 `PlayerInteractor` 조준 레이·상호작용 판정에 자기 뼈가 걸린다 → 전용 레이어는 여전히 필요하다.
  - 그리고 **오너는 자기 몸을 못 본다**(OwnBody 컬링) → 결정 3의 근거.
- 본 이름 (Synty 리그, 실제 확인):
  ```
  Root/Hips
    Spine_01 → Spine_02 → Spine_03 → { Neck → Head, Clavicle_L/R → Shoulder_L/R → Elbow_L/R → Hand_L/R }
    UpperLeg_L/R → LowerLeg_L/R → Ankle_L/R → Ball_L/R → Toes_L/R
  ```
  팔이 `UpperArm/LowerArm`이 아니라 **`Shoulder_*`/`Elbow_*`** 다. 래그돌 위저드·빌더에서 이름을 그대로 쓰면 안 맞는다.
- `HeldItemAnchor`는 `Hand_R` 아래 → 들고 있던 아이템은 부모를 따라가므로 손에 붙은 채로 함께 날아간다(추가 처리 불필요). 아이템 자체 콜라이더가 Default 레이어면 벽·레이 판정에 걸릴 수 있으니 구현 중 한 번 확인할 것.

---

## 3. 설계

### 3-1. 순서 보장 문제와 그 해법 — **멱등 진입**

서버(호스트)에서는 순서가 이미 맞다: `ServerExplode`가 `TakeDamage` → `SetState(Exploded)` → `OnResolved` 순으로 돈다.

**문제는 원격 클라이언트다.** 사망 사실은 `PlayerIncapacitation.m_causeSynced` 콜백으로, 폭발 사실은 `BombDevice.m_stateSynced` 콜백으로 온다 — **서로 다른 오브젝트의 NetworkVariable이라 같은 틱에 실려 와도 콜백 순서가 보장되지 않는다.** "이 사람 죽었나?"를 뷰가 물어보면 원격에서는 답이 틀릴 수 있다.

순서를 맞추려 들지 말고 **어느 쪽이 먼저 와도 결과가 같게** 만든다:

```csharp
// 두 번 불려도 안전. 이미 래그돌이면 임펄스만 누적한다.
public void EnterRagdoll(Vector3 impulse)
```

| 진입 경로 | 트리거 | 임펄스 |
|---|---|---|
| ① 사망 | `Cause`가 `Die`로 전이 | `Vector3.zero` (힘없이 무너짐 — 진압봉·린치 사망) |
| ② 폭발 | 서버가 보낸 사망자 목록 RPC | `EvaluateKnockback(위치)` |

②만 먼저 와도 래그돌이 켜지고, ①이 뒤늦게 와도 무동작이다. 반대 순서도 같다.

> **①은 이벤트가 아니라 폴링으로 잡아야 한다.** `OnIncapacitatedChanged`는 bool만 넘기고 **원인만 바뀌면 울리지 않는다** ([PlayerIncapacitation.cs:147](../Assets/Scripts/Player/PlayerIncapacitation.cs)). 납치 린치 사망은 `Abducted`/`Lynched` → `Die` 전이라 bool이 안 바뀌어 **이벤트가 아예 안 온다.** `PlayerAnimationDriver`가 `IsProne`을 Update에서 폴링하는 것과 같은 방식으로 `IsDead`를 폴링할 것. (또는 `PlayerIncapacitation`에 `OnCauseChanged`를 새로 추가 — 폴링 쪽이 기존 관례와 일치한다.)

### 3-2. 넉백 이중 적용 방지

`BombExplosionView`에서 "죽은 사람은 건너뛴다"는 판정은 위 이유로 신뢰할 수 없다. **죽은 사람에게 슬라이드 넉백이 들어가도 무해하게** 만드는 쪽으로 해결한다:

- `PlayerMovement.AddKnockback` — 래그돌 활성 중이면 무시.
- `PlayerRagdoll.EnterRagdoll` — 진입 시 `m_knockbackVelocity`·`m_verticalVelocity`를 0으로 클리어.

이러면 순서와 무관하게 항상 옳다.

### 3-3. `PlayerRagdoll` 상태 기계

모든 피어에서 로컬로 도는 MonoBehaviour. 새 매니저가 아니므로 `App` 파사드와 무관(R1~R8 해당 없음).

| 상태 | 내용 |
|---|---|
| **Animated** | 평시. 전 Rigidbody 키네마틱 |
| **Ragdoll** | `animator.enabled = false`, 전 rb `isKinematic = false`, 임펄스 적용 |
| **Settled** | 착지 정착 → 루트 재정렬 → 전 rb **다시 키네마틱** |
| **BlendingToAnimator** | 정착 포즈 → 애니메이터 포즈 보간(≈0.4초) 후 Animated로 |

공개 API 초안:

```csharp
public void EnterRagdoll(Vector3 impulse);   // 멱등
public void ForceSettle();                   // 운반 시작 등 외부 사유로 즉시 정착
public void ExitToAnimator(bool blend);      // 부활(true) / 라운드 리셋·씬 전환(false)
public bool IsRagdollActive { get; }         // PlayerMovement·PlayerLook·AnimationDriver가 읽는다
```

**임펄스 적용:** 전 rb에 `ForceMode.VelocityChange`로 `EvaluateKnockback` 속도를 **균일하게** 주고(몸 전체가 예측 가능하게 날아간다), 거기에 작은 `AddExplosionForce`를 폭심 기준으로 섞는다(사지가 흩어지고 스핀이 생긴다). 회전은 두 번째 성분이 만든다.

**정착 판정:** 전 rb `linearVelocity`(Unity 6 — `velocity`는 deprecated) 크기 합이 임계 이하인 상태가 0.3초 유지, 또는 진입 후 5초 타임아웃.

### 3-4. 정착 시 루트 재정렬 — **순서가 중요하다**

뼈는 루트의 자식이므로 루트를 옮기면 뼈도 딸려 간다. 다음 순서를 지키지 않으면 몸이 두 번 튄다:

```
① 전 뼈의 월드 포즈(position + rotation)를 캡처
② 전 rb를 isKinematic = true 로 전환
③ 루트(CharacterController)를 hips 위치·yaw로 이동          ← 오너(또는 오프라인)만
④ 캡처한 월드 포즈를 뼈에 다시 적용
```

- **③은 오너만 한다.** CharacterController + 오너 권한 NetworkTransform이므로 서버가 남의 캐릭터를 옮겨봤자 되돌아온다 (`BombExplosionView` 주석의 논리와 동일). 텔레포트는 `controller.enabled = false` → `transform.position/rotation` 대입 → `enabled = true` 순서로.
- **yaw까지 맞춘다.** `Knockdown_StandUp` 클립이 "루트 전방을 향해 등을 대고 누워 있다"를 전제하므로, 래그돌이 옆으로 굴러 있으면 부활 블렌드에서 몸이 휙 돌아간다. hips 전방을 지면에 투영한 값으로 루트 yaw를 맞춘다.
- **②가 이 설계의 핵심**이다. 정착 후 뼈가 다시 부모를 따라가므로, 동료가 시체를 운반(#365)할 때 시체가 같이 따라온다. 이게 없으면 캡슐만 끌려가고 몸은 바닥에 남는다.
- **원격 피어는 ③을 하지 않는다** — NetworkTransform이 옮겨 주는 오너 위치를 따른다. 자기 로컬 착지점과 오너 위치가 다르므로 **0.2~0.3초 수렴 lerp**를 넣어야 순간이동이 안 보인다. 폭발이 셀수록 편차가 커진다.

> 피어마다 착지점이 다른 것은 **의도된 감수**다(#506 본문 합의). 판정은 서버 트랜스폼이 쥐고 있고, 최종 위치는 오너 값으로 수렴한다.

### 3-5. 부활 블렌드

```
정착 포즈 캡처(각 뼈 localRotation + hips localPosition)
  → animator.enabled = true
  → Animator.Play(Knockdown_Ground, 0, 0)   // Down은 아직 true
  → LateUpdate에서 t: 0→1 (≈0.4초) Slerp/Lerp
  → 완료 후 Down = false 허용 → Knockdown_StandUp(1.17초) → Locomotion
```

**함정:** `Recover()`가 먼저 오면 `PlayerAnimationDriver`가 즉시 `Down = false`를 써 버려서 블렌드 도중에 StandUp이 시작된다. **블렌드 동안에는 AnimationDriver가 `Down`을 내리지 못하게** 막아야 한다(`IsRagdollActive`를 보고 파라미터 쓰기를 보류). 두 컴포넌트가 같은 Animator를 만지므로 역할 분담을 주석으로 명시할 것 — **AnimationDriver는 파라미터만, PlayerRagdoll은 Animator on/off + 뼈**.

### 3-6. 카메라 (결정 3)

Die 동안만:

- **메시 복원** — `SM_Gen_Chr_Robot_01`을 `OwnBody` → `Default` 레이어로 되돌린다 (`PlayerLook.SetLayerRecursively` 재사용). 부활 블렌드 시작 시 원상 복구.
- **카메라는 머리 뼈에 붙이지 않는다** (멀미). 대신 래그돌 활성 동안 **월드 공간에서 hips를 추종**하는 3인칭 오프셋으로 부드럽게 전환한다. 위치만 스무딩하고, 회전은 hips 회전을 따라가지 않는다(부드럽게 look-at만).
  - 캡슐에 고정하면 안 되는 이유: 캡슐은 폭심에 남아 있고 몸만 날아가므로, 정작 자기 몸이 화면 밖으로 사라진다.
  - **부수 효과 하나가 공짜로 따라온다** — 카메라가 루트의 자식이라 §3-4의 ③ 재정렬에서 원래는 화면이 튀는데, 월드 추종이면 그 튐이 안 보인다.
- 정착 후에도 시체 위 3인칭을 유지하다가, 부활 블렌드 시작 시 1인칭으로 복귀.
- 기존 `m_downCamHeight`/`m_downCamPitch` 경로는 **Die가 아닌 프론**(기절·매달기·납치)용으로 그대로 남긴다.

---

## 4. 데미지 거리 감쇠 (결정 2)

`EvaluateKnockback`과 같은 모양으로 `EvaluateDamage(Vector3 targetPosition)`를 만든다 — 넉백처럼 **감쇠식을 장치 한 곳에** 둔다.

```csharp
// 폭심 = m_explosionDamage, 반경 끝 = m_explosionDamage * m_damageEdgeFalloff 로 선형 감쇠
int EvaluateDamage(Vector3 targetPosition);
```

**튜닝 기준값 제안** (플레이테스트로 확정):

| 파라미터 | 값 | 결과 |
|---|---|---|
| `m_explosionDamage` | 60 → **150** | 폭심 즉사 |
| `m_damageEdgeFalloff` | **0.2** (신규) | 반경 끝 30 데미지 |
| `m_explosionRadius` | 8 (유지) | — |

이 값이면 **즉사 반경 ≈ 3.3m** 이다: `lerp(1, 0.2, d/8) × 150 ≥ 100` → `d ≤ 3.33`. 8m 반경 중 안쪽 3.3m는 죽고(래그돌), 그 밖은 살아서 밀린다(슬라이드 넉백). 수치를 바꿀 때 이 계산을 다시 할 것.

> **밸런스 주의:** GDD 365줄에 *"#524로 중간 완충이 사라져 한 번 쓰러지면 본부까지 왕복 — 폭발 등 기존 데미지 수치를 그대로 둘지 플레이테스트로 정한다"* 는 ⚠ 표기가 이미 있다. 6인 중 여럿이 폭심에 몰려 있으면 한 번에 전멸할 수 있다. 즉사 반경을 좁게 잡은 이유다.

**GDD 갱신 필요:** 6-4(시한폭탄 — "미해체 시 폭발(주변 넉백)"에 사망·래그돌 추가), 7-5(데미지 경로), 365줄의 ⚠ 항목. 368줄에 *"폭탄 등 다른 데미지 경로의 플레이어 피격 규칙은 여기서 다루지 않는다"* 로 비어 있는 자리를 채우는 셈이다.

---

## 5. 작업 순서 (커밋 분할)

### 커밋 ① 프리팹 래그돌 세팅 + 레이어 — **동작 변화 없음**

- **에디터 빌더 `PlayerRagdollBuilder`** (`Assets/Scripts/Editor/`) — Unity 내장 Ragdoll Wizard 대신. 이 저장소에 이미 `PlayerAnimatorControllerBuilder`·`NpcAnimatorControllerBuilder`·`FPArmGenerator`라는 같은 관례가 있고, 본 이름 기반 재실행 가능(idempotent)하게 만들면 NPC 확장 때 그대로 재사용된다.
- **래그돌 본 11개** (Synty 실제 이름):
  | 역할 | 본 |
  |---|---|
  | 골반 | `Hips` |
  | 중간 척추 | `Spine_02` |
  | 머리 | `Head` |
  | 팔(상완) | `Shoulder_L`, `Shoulder_R` |
  | 팔(전완) | `Elbow_L`, `Elbow_R` |
  | 다리(대퇴) | `UpperLeg_L`, `UpperLeg_R` |
  | 다리(하퇴) | `LowerLeg_L`, `LowerLeg_R` |

  발(`Ankle_*`)까지 넣으면 13개. 발은 있으면 착지가 자연스럽지만 없어도 무방 — 성능 여유를 보고 결정.
- 전 Rigidbody **초기 `isKinematic = true`** → 이 커밋만으로는 게임이 전혀 안 바뀐다.
- **신규 레이어 `Ragdoll`** (빈 슬롯 10번 — 현재 0~9만 사용) + 충돌 매트릭스:
  - 켠다: `Ragdoll` × `Default`(지형)
  - 끈다: `Ragdoll` × `Ragdoll`, × 플레이어 캡슐, × `Interactable`, × 아이템 리지드바디
  - 벽 관통 걱정은 없다 — 지형과 충돌하므로 래그돌은 벽을 넘지 않는다. NPC의 `SweepHitsObstacle` 같은 별도 벽 판정이 필요 없는 것이 오히려 래그돌 쪽의 이점이다.

### 커밋 ② `PlayerRagdoll` — 진입·정착·재정렬

- `PlayerRagdoll` 신규 (`Assets/Scripts/Player/View/`)
- `PlayerMovement.AddKnockback`에 래그돌 가드 (§3-2)
- `PlayerAnimationDriver`에 "래그돌 중 파라미터 쓰기 보류" 가드 (§3-5)
- `PlayerHeadLook` — 프론 시 가중치를 0으로 **블렌딩**하는데 감쇠에 시간이 걸린다. 래그돌 진입 순간 하드 0으로.
- `OnNetworkSpawn`에서 이미 `Die`면 래그돌을 건너뛰고 곧장 `Knockdown_Ground` 포즈 (늦게 접속한 클라 — `RefreshAimHitbox`가 스폰 시 한 번 맞추는 것과 같은 계열)

### 커밋 ③ 폭발 연동 — 감쇠 데미지 + 사망자 RPC

- `BombDevice.EvaluateDamage` 신설, `ServerExplode`에서 사용 (§4)
- `ServerExplode`가 데미지 적용 직후 **죽은 사람 목록**을 모아 ClientRpc로 전파
  - 폭심·반경·세기는 이미 전 피어가 아니까 **사망자 목록만** 보낸다(`NetworkObjectReference[]`). 임펄스는 각 피어가 기존 `EvaluateKnockback`으로 계산 — "넉백 식은 장치 한 곳" 원칙 유지.
  - 호스트 중복 발행 주의: 기존 `WrongCutClientRpc` 관례(서버는 로컬 직접 발행 + ClientRpc에서 `if (IsServer) return;`)를 따를 것.
- `BombExplosionView`는 그대로 둔다 — 죽은 사람에게 들어가도 §3-2 가드가 삼킨다.

### 커밋 ④ 카메라 + 부활 블렌드

- `PlayerLook` 래그돌 모드 (§3-6)
- 부활 블렌드 (§3-5)
- `PlayerCarrier.ServerBeginCarry`에서 `ForceSettle()` — 물리 중에 운반이 시작되면 캡슐만 움직인다
- `PlayerHealth.ServerResetState`·씬 전환·despawn 경로에서 `ExitToAnimator(blend: false)`

### 커밋 ⑤ 문서

- GDD 6-4·7-5·365줄 갱신 (§4)
- 이 문서를 결과 기준으로 갱신

---

## 6. 기존 코드와 부딪히는 지점 (체크리스트)

- [ ] **운반(#365)** — 정착 전에 운반이 시작되면 캡슐만 움직인다 → `ForceSettle()`
- [ ] **`ServerResetState`(라운드 리셋)** — `Recover()`가 곧장 걸린다 → 블렌드 없이 즉시 애니메이터 복귀
- [ ] **씬 전환 / despawn** — 래그돌 상태로 넘어가지 않게 리셋
- [ ] **`PlayerHeadLook`** — 머리 본 오버라이드가 래그돌과 싸운다 → 하드 0
- [ ] **`NavMeshObstacle`·`ReviveHitbox`** — 루트에 붙어 있으므로 §3-4 재정렬로 함께 해결된다
- [ ] **들고 있는 아이템** — 사망 시 드롭되지 않는다(`PlayerLoadout`은 사용만 막는다). 손에 붙어 함께 날아가는 건 정상이지만, 아이템 콜라이더가 Default 레이어면 벽·레이 판정에 걸릴 수 있다
- [ ] **`Die`는 다른 무력화로 덮이지 않는다** — `Incapacitate` 가드가 이미 막고 있어 매달기·기절과의 경합은 없다
- [ ] **린치 사망(`Abducted`/`Lynched` → `Die`)** — `OnIncapacitatedChanged`가 울리지 않는다. §3-1의 폴링 근거
- [ ] **`Knockdown_Fall` 클립** — 래그돌이 대체하므로 사실상 안 쓰이게 된다(`Ground`·`StandUp`은 부활 블렌드에서 계속 사용). 컨트롤러에서 지우지는 말 것 — 빌더 산출물이고 되돌릴 여지를 남긴다

---

## 7. 검증

1. **컴파일** — stale csproj 한 줄 제거 후 `dotnet build` (Unity MCP 없이 검증하는 기존 경로)
2. **오프라인 Play** — 비네트워크 폴백 경로가 살아 있다(`IsSpawned == false`). 폭발 → 래그돌 → 정착까지 혼자 확인
3. **호스트 + 클라 2인** (Multiplayer Play Mode)
   - 각 피어가 자기 화면에서 래그돌을 본다
   - 정착 위치가 서로 일치한다(수렴 lerp 확인)
   - 죽는 본인이 자기 몸이 날아가는 걸 본다
4. **전체 시퀀스** — 폭발 사망 → 동료가 운반 → 본부 부활 → 블렌드 → StandUp → 정상 이동
5. **다른 사망 경로** — 진압봉 사망·납치 린치 사망도 래그돌로 무너지는지
6. **가장자리 생존** — 반경 6~8m에서 맞은 사람은 기존 슬라이드 넉백으로 밀리는지

---

## 8. 미결 / 후속

- **NPC 래그돌** — #506 본문의 원래 범위. 플레이어가 검증되면 확장한다. NPC는 이동 권한이 서버 `NavMeshAgent`라 정착 후 `NavMesh.SamplePosition` → `Warp` 경로와 맞물려야 하고, `Stunned`(2.67초)·`Captured` 상태 유지 타이밍과 기상 모션이 어긋나면 안 된다. `NpcProneCollider`와의 관계도 정리 대상.
- **동시 래그돌 상한 / 거리 컬링** — 플레이어 6명 × 11 rb는 문제없지만, NPC로 확장하면 군중 폭발에서 수십 개가 된다. 상한·조기 sleep은 NPC 단계에서.
- **폭발 밸런스 재확인** — §4의 ⚠. 즉사 반경 3.3m가 실제로 적당한지는 플레이테스트 몫.
- **죽은 플레이어의 캡슐** — 누워 있는데 CharacterController 캡슐은 서 있는 채다(래그돌 이전에도 그랬다). 통행 방해·`NavMeshObstacle` 문제는 이번 범위 밖.

---

## 9. 구현 기록 — 물리에서 밟은 함정

증상은 하나였다: **"자연스럽게 쓰러진 뒤 마지막에 한두 번 발작하듯 튄다."** 원인이 하나가 아니라 겹쳐 있었고, 콘솔 로그로 하나씩 갈라냈다. 아래는 전부 실제로 밟은 것이고, 같은 함정이 NPC 래그돌(§8)에서 되풀이되기 쉬우니 남긴다.

### 9-1. 자기 캡슐과의 충돌 무시가 지워진다 — *쓰러지는 순간 튐*

래그돌은 죽는 순간 **자기 CharacterController 캡슐 안에서** 출발한다. 캡슐이 지형과 같은 `Default` 레이어라 충돌 매트릭스로는 못 가르고, 런타임 `Physics.IgnoreCollision`으로 끈다.

**이 상태는 콜라이더를 껐다 켜면 초기화된다(Unity 사양).** `Awake`에서 한 번 거는 것으로는 부족하다 — `PlayerMovement.SetPose`가 스폰 포즈를 적용할 때 **같은 프레임 안에서** 껐다 켜므로 폴링으로는 전이조차 볼 수 없다. 세 곳에서 다시 건다: 래그돌 진입 직전(결정적 지점) / `PinHipsOnly` / 캡슐이 여러 프레임 꺼졌다 켜지는 것이 관측될 때(호송·운반).

### 9-2. 관절 `projection`은 켜면 안 된다

늘어난 관절을 매 스텝 강제로 되당기는 기능인데, 두 번 물렸다:

- 좁게(0.02m / 5°) 잡으면 매 스텝 보정이 걸려 몸이 폭주한다
- 느슨하게(0.1m / 180°) 잡아도 **projection은 충돌을 무시하고 위치를 옮기는** 기능이라, 밧줄로 끌 때 관절이 늘어나는 순간 머리를 지형 안으로 밀어 넣는다

위저드 기본값도 꺼짐이다. 대신 solver 반복으로 관절을 붙든다 — 그런데 그게 §9-3 때문에 안 걸려 있었다.

### 9-3. `solverIterations`·`maxDepenetrationVelocity`는 프리팹에 저장되지 않는다

**Rigidbody의 직렬화 필드가 아니다.** `Player.prefab`의 Rigidbody 블록은 `m_CollisionDetection`에서 끝나고 해당 항목이 아예 없다. 에디터 스크립트에서 아무리 써도 저장되지 않고, 인스턴스가 만들어질 때마다 `DynamicsManager.asset`의 프로젝트 기본값으로 되돌아온다.

| | 의도(`PlayerRagdollSetup`) | 실제 프리팹 |
|---|---|---|
| `maxDepenetrationVelocity` | 3 | **10** (기본값) |
| `solverIterations` | 12 | **6** (기본값) |
| `solverVelocityIterations` | 4 | **1** (기본값) |

레이어·`isKinematic`·보간·CCD·관절 `preprocessing`/`projection`은 직렬화되므로 정상 반영됐다 — 그래서 셋업이 돌아간 것처럼 보였다. → **`PlayerRagdoll`이 `Awake`에서 런타임에 건다.** 에디터 스크립트로 되돌리지 말 것.

### 9-4. 정착 텔레포트에 `SetPose`의 짝이 빠져 있었다

`Settle()`은 루트를 시체 밑 지면으로 옮기면서 `PlayerMovement.SetPose`를 거치지 않고 트랜스폼을 직접 만진다. 그래서 `SetPose`가 #189 때문에 하던 두 가지가 빠져 있었다:

- **쌓인 수직 속도를 안 지웠다** → `ClearExternalVelocity()` 추가
- **루트 원점을 지면 점에 그대로 놓았다** → 루트 원점은 캡슐 밑면이 아니다. 캡슐은 `center.y ± height/2` 범위라 밑면이 루트보다 `center.y - height/2`만큼 위에 있고(이 프리팹은 3cm), 그대로 두면 캡슐이 떠서 출발한다. `isGrounded`는 `Move()`를 한 번 돌기 전까지 거짓이라 그동안 중력이 쌓인다

둘 다 캡슐을 밀고, **정착 후 골반은 키네마틱이라 캡슐에 매달려 있으므로** 그 이동이 시체를 통째로 끌어간다.

### 9-5. 원격 피어 — 시체가 아직 이동 중일 때 물리로 놓으면 안 된다

여기가 가장 오래 걸렸다. 정착 직후 원격의 시체는 두 가지가 동시에 끌어온다:

- **리그 루트** — `TickConverge`가 로컬 착지점과 오너 확정 위치의 차이를 흡수 (실측 **1.18~1.38m** / 0.25초)
- **플레이어 루트** — NetworkTransform이 오너 확정 자리로 이동 (실측 한 스텝 **219mm**)

그 이동 중에 `PinHipsOnly()`를 부르면 **골반만** 키네마틱이라 리그에 용접된 채 1m 넘게 끌려가고, 나머지 뼈는 이미 바닥에 눌러앉아 물리로 버틴다 → 관절 10개가 통째로 늘어났다 되튕긴다. 로그에서 Δv가 사지(`Elbow`·`Shoulder`·`Head`·`LowerLeg`)에만 몰리고 바닥과의 충격이 10을 넘던 것이 이 현상이다.

→ **움직임이 멎은 뒤에 놓아준다** (`BeginPhysicsRelease`/`TickPhysicsRelease`). 유예 동안에는 전 뼈가 키네마틱이라 스켈레톤이 강체로 함께 미끄러진다. 조건은 *수렴 종료 + 루트가 한 프레임 5mm 미만 이동*, 못 만나면 1초 타임아웃(움직이는 것 위에 얹힌 시체도 결국 운반·부활이 되어야 하므로).

그리고 그 유예를 걸고도 릴리스 직후 **접촉 없이 Δv 7.1m/s**(머리)가 남았다. 원인은 따로였다:

> **키네마틱 바디도 트랜스폼이 움직이면 PhysX가 그 이동에서 속도를 유도하고, 그 속도는 `isKinematic = false` 시점에 살아난다.**

수렴이 리그를 1.18m 끌어온 속도(≈4.7m/s, 회전 중심에서 먼 뼈는 더 빠름)를 그대로 물고 물리로 돌아간 것이다. Δv 순서가 머리 7.1 > 어깨·팔꿈치 6.0 > 척추 2.7로 **리그 루트에서 먼 순서 그대로**였던 것이 근거다. → 속도를 **양쪽 전이에서 모두** 지운다: 키네마틱으로 넘기기 *전에*, 물리로 돌려줄 때는 돌려준 *뒤에*(키네마틱 상태에서는 속도 대입이 적용되지 않는다).

### 9-6. 아직 남아 있는 임시 코드 — 지울 것

원인이 확정되면 한 번에 걷어낸다:

- `PlayerRagdoll` — `m_diagnoseBounce` / `m_bounceReportThreshold` / `BoneContactProbe` / `FixedUpdate` / `ReportBounce` / `SetUpBounceDiagnostics` / `ReportContact` 및 `m_diag*` 필드
- `PlayerMovement.DiagnosticVerticalVelocity`
- `m_debugLog`이 켜 놓은 진입·지면 판정·정착·물리 복귀 로그

그리고 **검증용 테스트 리그가 별도 커밋으로 올라가 있다** — 래그돌을 반복해서 보려면 폭발을 자주 일으켜야 해서 넣은 것이고, **PR 전에 그 커밋을 떨어뜨려야 한다**:

| 대상 | 값 | 원래 |
|---|---|---|
| `Main Scene`의 `SuddenEvents` | `m_startDelay`/`m_minInterval`/`m_maxInterval` = 5, 폭탄 외 이벤트 전부 off | 원래 간격·구성 |
| `Bomb.prefab` | `m_countdownSeconds` 15 | 90 |
| `Bomb.prefab` | `m_explosionDamage` 100 | 60 |

폭탄 데미지는 §4의 거리 감쇠(커밋 ③)가 들어가면 어차피 다시 잡는 값이다 — 지금의 100은 감쇠 없이 풀피를 즉사시켜 래그돌을 보려고 올린 임시값이지 §4의 결론이 아니다.

### 9-7. 다음 검증

- [ ] **오너(죽는 본인) 경로** — §9-4의 두 수정은 오너 분기 전용이라 아직 한 번도 검증되지 않았다. 지금까지의 로그는 전부 `권한 원격`이었다
- [ ] 원격에서 `[래그돌] 물리 복귀 — … (정지확인)` 직후 Δv가 잦아드는지
- [ ] 강한 폭발에서 관절이 늘어나 보이면 projection이 아니라 `solverIterations`를 올리는 쪽으로 (§9-2)

---

## 부록 A — #506에 달 코멘트 초안

> ## 범위 변경 — 진입 조건을 "폭발 넉백"에서 "사망"으로 바꾼다
>
> 구현 설계를 잡으며 확인한 두 가지 때문에 본문의 범위를 조정한다. 상세 설계는 [docs/506-explosion-ragdoll.md](../docs/506-explosion-ragdoll.md).
>
> **1. 폭발은 어차피 사망 연출이 맞다.** 본문은 "사망 래그돌은 이번 범위 밖"으로 적었지만, 실제 순서는 `폭발 데미지 → 사망 → 래그돌 → 넉백 임펄스`다. 래그돌 진입을 `IncapacitationCause.Die` 하나로 잡으면 폭발·진압봉·납치 린치가 전부 같은 경로를 타고, 폭발은 **임펄스가 붙는다는 점**만 다르다. 살아남은 사람은 지금의 슬라이드 넉백을 그대로 쓴다.
>
> **2. 지금 수치로는 폭발에 안 죽는다.** `m_explosionDamage = 60`, `m_maxHp = 100`이고 데미지에는 거리 감쇠가 없어 반경 8m 안이면 전원 균일하게 60이다. 그래서 **데미지에도 거리 감쇠를 넣는다** — 폭심은 즉사(래그돌), 가장자리는 생존(슬라이드 넉백). "가까운 놈은 날아가고 먼 놈은 밀린다"가 자동으로 나온다. 기준값 제안은 폭심 150 / 가장자리 비율 0.2 / 즉사 반경 ≈ 3.3m이고, GDD 365줄의 밸런스 ⚠ 항목과 함께 플레이테스트로 확정한다.
>
> ### 본문에서 확정된 항목
>
> - **카메라: (b) 3인칭.** 오너는 `OwnBody` 레이어 컬링 때문에 **자기 몸이 아예 안 보인다**(`m_ownBodyRoot`가 본이 아니라 스킨드 메시라서). 자기 몸이 날아가는 걸 보는 게 이 기능 재미의 절반이라 Die 동안만 메시를 Default로 되돌리고 카메라를 3인칭으로 뺀다. 머리 뼈에는 붙이지 않는다(멀미).
> - **콜라이더 레이어:** 신규 `Ragdoll` 레이어(슬롯 10) + 지형하고만 충돌. 본문이 걱정한 "오너만 레이어가 다르다"는 실제로는 없었다 — `SetLayerRecursively`가 메시만 덮고 뼈는 안 건드린다. 다만 뼈가 Default라 조준 레이에 걸리는 문제는 그대로라 전용 레이어는 필요하다.
> - **범위:** 1차는 **플레이어만**. NPC 래그돌(본문의 원래 범위)은 플레이어가 검증된 뒤 후속으로. NPC는 서버 `NavMeshAgent` Warp·`Stunned`/`Captured` 유지 타이밍·`NpcProneCollider`까지 얽혀 검증 표면이 훨씬 넓다.
>
> ### 완료 기준 (1차)
>
> - [ ] 플레이어 프리팹에 래그돌 구성, 폭발 사망 시 래그돌로 날아가고 착지 후 정착
> - [ ] 진압봉·린치 등 다른 사망 경로도 같은 래그돌로 무너짐
> - [ ] 반경 가장자리 생존자는 기존 슬라이드 넉백 유지
> - [ ] 래그돌 뼈 콜라이더가 조준 레이·상호작용 판정을 방해하지 않음
> - [ ] 정착 후 시각 몸통과 CharacterController가 어긋나지 않음 (운반 조준·본부 부활이 정상 동작)
> - [ ] 본부 부활 시 애니메이터로 부드럽게 복귀 (정착 포즈 → 기상 모션)
> - [ ] 멀티(호스트+클라 2인 이상)에서 각 피어가 래그돌을 보고, 정착 위치는 서로 일치
