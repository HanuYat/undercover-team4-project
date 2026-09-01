# PR #959 리뷰 후속 — 3건 (2건 완료 · 1건 조사만) (2026-09-01)

> 브랜치: `feature/903-865-death-ragdoll` (PR [#959](https://github.com/hyunjin0814/undercover-team4-project/pull/959) 열려 있음)
>
> 관련 정본: [506-explosion-ragdoll.md](506-explosion-ragdoll.md) §13~16 · [865-down-ragdoll.md](865-down-ragdoll.md) · [player-ragdoll.md](player-ragdoll.md)

## 0. 한 줄 요약

| # | 항목 | 상태 |
|---|---|---|
| 1 | `CollectLaunched`의 `FindObjectsByType`을 등록 목록 순회로 교체 | **코드 완료** · 컴파일 확인 · **플레이 미검증** |
| 2 | 빔 래치를 풀 때 기상 블렌드 중인 뼈가 물리로 열리던 것 | **코드 완료** · 컴파일 확인 · **플레이 미검증** |
| 3 | `[Netcode] Rpc message received from a client without permission` 로그 | **조사만 끝났다 — 수정 안 들어갔다** |

컴파일은 `dotnet build Assembly-CSharp.csproj` **오류 0개**(경고는 전부 기존 `CS0618`/`CS0649`). Unity Editor Console·Play 모드는 확인하지 않았다.

### 이어받는 세션이 처음 할 일

1. **§3의 결정 하나를 받는다** — 접지 보고를 무력화 중에 막을지. 방향은 정해져 있고 코드만 안 넣었다.
2. §1·§2의 **플레이 검증 항목**(각 절 끝)을 돌린다. 둘 다 정적으로만 확인된 상태다.
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

### 3-5. 제안하는 수정 (합의 대기 — 코드 안 넣었다)

1. **무력화된 몸은 접지 보고를 하지 않는다.** 다운·사망·빔 중인 몸의 공중 플래그는 애니메이터가 래그돌에 밀려 쓰지도 않는 값이다. 이걸 막으면 8초 미룸 창 전체가 조용해진다. `ReportGroundedOnEnter`가 필요한 것은 **살아서** 끌려가는 오검거 호송·운반뿐이라 그쪽은 그대로 남는다.
2. **`OnGainedOwnership`에서 중복 제거 래치(`m_reportedAirborne`)를 무효화한다** — 소유권을 되받은 뒤 첫 보고가 무조건 나가게 해서 §3-4의 고착을 막는다.

버린 안: `[ServerRpc(RequireOwnership = false)]` + 발신자 검사. 로그는 사라지지만 원인이 아니라 증상을 덮는다. 1·2번이 안 통할 때만 되살릴 것.

⚠ **같은 레이스가 `PlayerCrouch`(14)·`PlayerEmote`(27)에도 구조적으로 있다** — 둘 다 오너 게이트 + 구형 `[ServerRpc]`다. 로그에 14/27이 뜨면 같은 진단을 적용하면 된다.
