# PR #959 리뷰 후속 — 3건 (2026-09-01, 개정 2판)

> 브랜치: `feature/903-865-death-ragdoll` (PR [#959](https://github.com/hyunjin0814/undercover-team4-project/pull/959) 열려 있음)
>
> 관련 정본: [506-explosion-ragdoll.md](506-explosion-ragdoll.md) §13~16 · [865-down-ragdoll.md](865-down-ragdoll.md) · [player-ragdoll.md](player-ragdoll.md)

## 0. 한 줄 요약

| # | 항목 | 상태 |
|---|---|---|
| 1 | `CollectLaunched`의 `FindObjectsByType`을 등록 목록 순회로 교체 | **코드 완료** · 컴파일 확인 · **플레이 미검증** |
| 2 | 빔 래치를 풀 때 기상 블렌드 중인 뼈가 물리로 열리던 것 | **코드 완료** · 컴파일 확인 · **플레이 미검증** |
| 3 | `[Netcode] Rpc message received from a client without permission` 로그 | **원인 확정 · UFO 쪽만 이 브랜치에서 고쳤다** — 일반 수정은 [#962](https://github.com/hyunjin0814/undercover-team4-project/issues/962)로 뺐다 |

컴파일은 `dotnet build Assembly-CSharp.csproj` **오류 0개**(경고는 전부 기존 `CS0618`/`CS0649`). Unity Editor Console·Play 모드는 확인하지 않았다.

### 이어받는 세션이 처음 할 일

1. §1·§2·§3의 **플레이 검증 항목**(각 절 끝)을 돌린다. 셋 다 정적으로만 확인된 상태다.
2. §3의 로그가 **그래도 재현되면** [#962](https://github.com/hyunjin0814/undercover-team4-project/issues/962)로 넘어간다 — 계측 한 줄로 발신자를 확정하는 것이 첫 단계다.
3. `FindObjectsByType` 전반 정리는 이 브랜치 범위가 아니다 — 이슈 [#961](https://github.com/hyunjin0814/undercover-team4-project/issues/961)로 떼어 놨다.

---

## 1. `CollectLaunched`가 네트워크 틱마다 씬을 전수 검색했다

`PlayerHealth.CollectLaunched`는 차량 한 대마다 틱마다 불린다(`TrafficVehicle.ServerApplyLaunchedHits`). 그 안에서 `FindObjectsByType<PlayerHealth>`를 돌고 있었다 — 씬 전체 검색 + 배열 할당이 상시 비용으로 깔린 것이다.

**고침** — 이미 있던 등록 목록 `PlayerIncapacitation.All`(#365에서 도입, `OnEnable`/`OnDisable` 자기 등록·해제, 무할당 `IReadOnlyList`)을 순회한다. [PlayerHealth.cs:68](../Assets/Scripts/Player/PlayerHealth.cs)

동작이 같은 근거 넷:

- `PlayerIncapacitation`은 `OnEnable`/`OnDisable`로 등록·해제 → `FindObjectsByType(FindObjectsSortMode.None)`의 기본값(비활성 제외)과 **집합이 같다**. 이 컴포넌트를 끄거나 플레이어 루트를 `SetActive(false)` 하는 경로는 코드에 없다(확인함).
- 원래 필터 `IsDamageable && !IsTargetable`이 참이 되는 경우는 `HP>0 && IsLaunched`뿐이다. 그래서 `IsLaunched`를 먼저 보고, 최종 판정은 **원래 조건 그대로** 남겼다.
- `PlayerIncapacitation`이 없는 `PlayerHealth`(테스트 리그)는 원래도 이 필터를 통과 못 한다 — `m_incapacitation == null`이면 `IsDamageable == IsTargetable`이다.
- `GetComponent<PlayerHealth>()`는 비행 중 + 반경 안일 때만 돈다 → 평시 0회.

**플레이 검증 항목**

- [ ] 폭발로 날아가는 사람을 **차가 치면** 피해가 들어간다 (이 함수의 주 소비자)
- [ ] **저항 NPC 범위 타격**에 날아가는 동료가 포함된다
- [ ] 멀쩡히 서 있는 사람이 차/NPC에 **한 번만** 맞는다 (`!IsTargetable`이 물리 쿼리와의 중복을 막는 자리 — 틀어지면 데미지 2배)
- [ ] 다운/기능 정지로 누운 몸이 차에 치일 때 기존과 같다 (확인사살 경로는 별도)
- [ ] **라운드 2회차·중도 접속 후에도** 위 1번이 된다 — 씬 스캔은 등록 타이밍이 무관했지만 이제는 `OnEnable` 등록에 의존한다 (이번 변경으로 **새로 생긴** 실패 모드)
- [ ] 날아가는 쪽이 **원격 클라이언트 플레이어**인 경우 (이 계열에서 "호스트는 되는데 클라는 안 된다"가 반복해서 나온 자리)

---

## 2. 빔 래치를 풀 때 기상 블렌드 중인 뼈가 물리로 열렸다

정본은 [506-explosion-ragdoll.md §14-5](506-explosion-ragdoll.md)에 표·프레임 순서까지 적어 뒀다. 요약만:

`ReleaseBeamedHold()`가 상태를 안 보고 `SetKinematic(false)`를 걸었는데, 이 함수는 뜻이 다른 두 상황에서 불린다 — ① 래그돌은 도는데 빔만 끝났다(풀어야 맞다) ② **래그돌 자체를 빠져나갔다**(`ExitToAnimator`가 기상 블렌드를 넘기려고 방금 일부러 얼린 것을 덮어쓴다). ②는 `FixedUpdate → Update → LateUpdate` 순서상 반드시 성립한다.

**고침** — 래치는 항상 내리되 `SetKinematic(false)`는 `m_state == Ragdoll`일 때만. [PlayerRagdoll.cs:799](../Assets/Scripts/Player/View/PlayerRagdoll.cs)

리뷰가 제시한 다른 안(`ExitToAnimator`가 얼리기 전에 `ReleaseBeamedHold`를 먼저 호출)은 쓰지 않았다 — 그 자리에서 `SetKinematic(false) → (true)`를 연달아 걸게 되고, 래그돌을 빠져나가는 경로가 하나 더 생기면 또 샌다.

**플레이 검증 항목**

- [ ] **끌려 올라가는 도중에 라운드를 끝낸다** (재현이 제일 쉬운 경로) → 몸이 정상적으로 일어나고 뼈가 늘어지거나 무너지지 않는다
- [ ] 15초 **흡입 타임아웃**(UFO 방치)도 같다
- [ ] 회귀 — **확정 흡입**(끝까지 빨려 들어가 죽음)이 전과 같다. 이쪽은 `Beamed → Die` 둘 다 래그돌 사유라 `m_state`가 `Ragdoll`을 안 벗어나 이번 수정에 영향받지 않아야 정상이다
- [ ] 빔에서 **상공에 놓아준 뒤 낙하**가 §14-4가 적어 둔 기존 동작 그대로다

---

## 3. `Rpc message received from a client without permission` — 조사 결과

관측 로그(UFO 사망 직후):

```
[Netcode] [Handle][Player(Clone)][NetworkBehaviourId:20] Rpc message received from a client
without permission to perform this operation!. Dropping RPC message
  ... Unity.Netcode.ServerRpcMessage:Handle (RpcMessages.cs:137)
```

재현 상황: **래그돌로 날아가던 플레이어가 UFO 빔에 빨려 들어가 죽은 직후.**

> **용어 — "접지 보고"** 는 `PlayerJump.ReportGrounded()`를 줄인 말이다. 게임 규칙이 아니라 **남의 화면에서 점프·낙하 애니메이션을 맞추기 위한 상태 동기화**다: 원격 인스턴스는 `Move()`를 안 타 `isGrounded`가 갱신되지 않으므로, 접지를 아는 오너가 값이 바뀔 때만 서버에 보고하고(`ReportAirborneServerRpc`) 서버가 `NetworkVariable`로 전 피어에 뿌린다. `PlayerAnimationDriver`가 그 값을 읽는다.

### 3-1. 확정 — 20번은 `PlayerJump`다

근거 둘이 독립적으로 맞는다.

1. **프리팹 컴포넌트 순서.** NGO는 `GetComponentsInChildren<NetworkBehaviour>` 순서로 id를 매긴다. `Player.prefab` 루트에서 미해석 스크립트는 둘뿐이고(`NetworkObject`, `NetworkTransform`) 나머지는 전부 프로젝트 스크립트로 해석된다. `NetworkObject`는 NetworkBehaviour가 아니라 빠지고 **`NetworkTransform`이 0번**이다:

   ```
   0 NetworkTransform      4 PlayerHealth       8 PlayerIncapacitation
   14 PlayerCrouch        19 PlayerHeadLook    20 PlayerJump        21 PlayerCarrier
   27 PlayerEmote         33 RagdollPoseStreamer
   ```

   ⚠ `PlayerRagdoll`·`RagdollRig`·`RagdollRope`·`PlayerTowedMotion`·`PlayerLook`은 **`MonoBehaviour`라 id를 받지 않는다**(확인함). id를 셀 때 빠뜨리지 말 것.

2. **스택이 `ServerRpcMessage`다** — 구형 `[ServerRpc]` 메시지 타입이다. 이 프리팹에서 구형 `[ServerRpc]`를 가진 컴포넌트는 **셋뿐**이다: `PlayerCrouch`(14) · `PlayerJump`(20) · `PlayerEmote`(27). 신형 `[Rpc(SendTo.Server)]`(`PlayerIncapacitation.DeathSettledRpc` 등)는 이 경로로 오지 않는다. 보고된 20이 그 셋과 맞는다.

`PlayerJump`의 ServerRpc는 하나다 — `ReportAirborneServerRpc`([PlayerJump.cs:116](../Assets/Scripts/Player/Movement/PlayerJump.cs)). `[ServerRpc]` 기본값이 `RequireOwnership = true`이고, 보내는 쪽은 `if (!IsOwner) return;`으로 이미 막혀 있다. **즉 코드가 잘못 부르는 게 아니라 소유권 이전 레이스다.**

### 3-2. 왜 하필 UFO 사망에서 — 창이 한 프레임이 아니다

- 빔 상승은 **호송(towed) 분기**를 탄다(§14-2 — 빔 구간만 `IsCapsuleFollowingBody`가 거짓인 이유). `UfoAbductor:242 → PlayerPenaltyView.StartTowedBy → [Rpc(SendTo.Owner)] StartCarriedRpc → BeginEscortFollow` 끝에서 [PlayerTowedMotion.cs:383](../Assets/Scripts/Player/Movement/PlayerTowedMotion.cs)이 **`m_jump.ReportGrounded(true)`를 명시적으로 부른다**(#189 — 공중에서 붙잡히면 낙하 애니메이션이 고착돼서 넣은 코드). 날아가던 중(airborne=true)에 빨려 들어갔으면 값이 뒤집혀 **RPC가 반드시 나간다.**
- 그리고 `Beamed → Die`는 래그돌이 도는 중이라 **#957의 이관 미룸**에 걸린다. 빔 중에는 뼈가 키네마틱이라 **정착을 안 하므로** `DeathSettledRpc`가 아니라 **8초 상한 타이머**가 이관을 끝낸다(`PlayerIncapacitation.ServerOwnershipHandoverTimeoutAsync`). 즉 **죽고 몇 초 뒤에** 오너가 클라 → 서버로 비동기 전환되고, 그 동안 클라는 자기가 오너라고 정당하게 믿는다. **"죽고 나서 로그가 떴다"가 이것으로 설명된다.**

### 3-3. 아직 추정인 것 · 확인 방법

드롭된 패킷이 (a) 흡입 시작의 `ReportGrounded(true)`인지, (b) 흡입이 끝나고 CC가 다시 켜진 뒤 정상 이동 분기(`PlayerMovement.cs:620`)가 낸 보고인지는 **정적으로 못 가린다.**

확인: `ReportGrounded`의 RPC 호출 직전에 `Debug.Log` 한 줄을 넣고 UFO 사망을 재현한다. 로그 시각이 **사망 직후냐 8초 뒤냐**로도 갈린다.

### 3-4. 영향

게임플레이는 대체로 무해하지만 하나 샌다 — 드롭된 값은 버려지는데 클라의 `m_reportedAirborne`은 **이미 갱신돼 있어서 같은 값으로는 다시 안 보낸다.** 부활 후 실제 상태가 드롭된 값과 같으면 **남의 화면에서 공중/낙하 애니메이션이 다음 전환까지 고착**될 수 있다.

### 3-5. 이 브랜치에서 한 것 — 몸이 사라지면 이관을 즉시 끝낸다

**접지 보고 쪽은 건드리지 않았다.** 흡입 시작의 `ReportGrounded(true)`는 그 시점에 오너가 아직 클라라 **정상 수락**된다(죽기 전이다) — 그러니 UFO 쪽 보고만 막아 봐야 로그가 안 없어질 공산이 크다. 대신 **창을 넓힌 쪽**을 닫았다.

`ServerKillByBodyLost`(UFO 삼킴 · 맨홀 납치가 함께 쓰는 결말 진입점)가 미뤄 둔 이관을 그 자리에서 끝낸다. [PlayerIncapacitation.cs:618](../Assets/Scripts/Player/PlayerIncapacitation.cs)

- **근거는 로그가 아니다.** 회수 불가로 사라진 몸(`BodyLost`)에는 지킬 물리 상태가 없는데, 빔 중에는 뼈가 키네마틱이라 정착 통보가 영영 오지 않아 이관이 **8초 상한 타이머**까지 붕 뜬다. `PlayerCarrier`가 운반 시작에서 [같은 이유로 같은 일](../Assets/Scripts/Player/Escort/PlayerCarrier.cs)을 한다("한창 끌고 가는 중에 권위가 뒤집힌다 — #957이 피하려던 바로 그 그림").
- 호출 위치는 `SetCause(Die)` **뒤**여야 한다(미룸을 세우는 쪽이 그것이다). 이미 `Die`였던 경로에도 미룸이 남을 수 있어 중복 호출 방어 분기 **밖**에 뒀다. 멱등이라 미룬 것이 없으면 무동작이다.

⚠ **이것으로 로그가 사라진다는 보장은 없다** — §3-3이 미확정이기 때문이다. 사라지지 않으면 [#962](https://github.com/hyunjin0814/undercover-team4-project/issues/962)로 간다.

**플레이 검증 항목**

- [ ] UFO에 삼켜져 죽는다 → **그 로그가 뜨는지** 확인 (뜨면 #962)
- [ ] 삼켜진 뒤 관전·라운드 종료가 정상 (이관 시점이 8초 뒤 → 즉시로 당겨진 영향)
- [ ] **맨홀 납치**(#775) 결말도 같다 — 같은 함수를 지난다
- [ ] 하강 중 폭탄 등으로 **이미 Die인 채** 몸이 소실되는 경로 (중복 호출 방어 밖으로 뺀 자리)

### 3-6. #962로 뺀 것 — 접지 보고 일반 수정

1. **래그돌이 몸을 쥔 동안(`IsRagdollCause`)은 접지 보고를 보내지 않는다.** ⚠ `IsIncapacitated`로 넓히지 말 것 — 매달기·기절·납치는 애니메이터가 계속 돌아 보고가 필요하다(`ReportGroundedOnEnter`가 노리는 것이 **살아서** 끌려가는 호송·운반이다).
2. **중복 제거 래치(`m_reportedAirborne`) 무효화** — 건너뛸 때·소유권 변경 때. §3-4의 고착을 막는다.

버린 안: `[ServerRpc(RequireOwnership = false)]` + 발신자 검사. 로그는 사라지지만 원인이 아니라 증상을 덮는다.

⚠ **같은 레이스가 `PlayerCrouch`(14)·`PlayerEmote`(27)에도 구조적으로 있다** — 둘 다 오너 게이트 + 구형 `[ServerRpc]`다. 로그에 14/27이 뜨면 같은 진단을 적용하면 된다.
