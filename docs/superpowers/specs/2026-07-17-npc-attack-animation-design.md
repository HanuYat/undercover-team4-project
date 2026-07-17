# NPC 공격 애니메이션 리팩토링 (#220)

- **이슈:** #220 [리팩토링] NPC 공격 애니메이션 리팩토링
- **브랜치:** `feature/220/npc-attack-animation` (base: `main`)
- **작성일:** 2026-07-17

## 목적

NPC의 공격이 **언제 일어나는지, 언제 플레이어가 맞는지** 눈에 보이지 않는다. 두 가지 원인:

1. **공격 애니메이션이 "치는 자세"로만 보인다.** 저항형 NPC(`NpcResistState`)는 FSM `Attack` 상태에 진입하면 `NPC_Attack1H_Loop.anim`(1.4초, 루프 ON)을 **끊임없이 반복 재생**한다. 개별 스윙이 구분되지 않아 "때리는 자세를 잡고만 있는" 것처럼 보인다.
2. **데미지가 눈에 보이는 타격과 어긋난다.** 실제 데미지는 애니메이션과 무관한 별도 타이머로 들어간다. 저항형은 `ResistAttackInterval`(1.5초) 주기의 범위 타격, 괴한(`ThugAttacker`)은 스윙 **시작 프레임(0초)에 즉시** 적용 — 팔이 닿기도 전에 HP가 깎인다. 그래서 플레이어는 "왜, 언제 맞았는지" 알 수 없다.

부가로 공격 처리가 두 갈래로 갈라져 있다: 저항형은 FSM 상태 기반 루프, 괴한은 `OnAttack` 이벤트로 0.6초만 Attack 재생 후 복귀(1.4초 루프 클립을 0.6초만 잘라 써서 동작이 뚝 끊김). 연출 규칙이 서로 다르다.

이 리팩토링은 **공격 1회 = 단발 스윙(준비→타격→복귀), 데미지는 타격 프레임에 동기화**로 통일한다.

## 확정 사항

| 항목 | 결정 | 근거 |
|---|---|---|
| 범위 | **저항형 NPC + 괴한 통일** | 이슈 상세의 "플레이어 타격 기능 이슈와 맞물리는 전투 연출 정합성". 두 공격 경로가 같은 연출 규칙을 쓴다. |
| 타격 동기화 | **코드 타이밍 오프셋** | 서버가 스윙 시작 후 고정 오프셋(클립 타격 프레임)에 데미지 적용. Netcode 서버 권위가 깔끔하고 클립 변경에 강건. 애니 이벤트는 전 피어 발화라 서버 가드가 필요하고 클립 교체 시 쉽게 깨진다. |
| 스윙 사이 자세 | **버틴 자세(Idle 재사용)** | 단발 스윙 사이엔 제자리 Idle로 버팀 → 준비→타격→복귀가 또렷하게 보인다. 새 클립 불필요. |
| 회피 창(wind-up) | **포함** | 데미지가 스윙 시작 ~0.45초 뒤에 들어가므로 준비 동작이 곧 예고이자 회피 창이 된다. 준비 중 사거리를 벗어나면 빗나간다. "언제 맞는지"를 정면으로 해결. |

## 컴포넌트

### 1. 애니메이터 `Assets/Animation/NPC.controller`

**변경 최소화 — 클립 루프 OFF 하나.** 스윙 표현은 드라이버가 State int를 잠깐 Attack으로 펄스했다가 되돌리는 방식으로 하므로, 애니메이터 구조(상태·전환·파라미터)는 건드리지 않는다.

| 변경 | 내용 |
|---|---|
| 클립 루프 OFF | `NPC_Attack1H_Loop.anim` → 루프 해제(단발 스윙). 이름을 `NPC_AttackSwing.anim`으로 정리(guid 유지되어 컨트롤러 참조 안 깨짐 · 선택). |

> **트리거 오버레이를 안 쓰는 이유.** 이 컨트롤러의 로코모션 전이는 전부 **Any State(`State == N`)**다. 스윙을 트리거로 base 레이어에 얹으면, 저항 NPC는 `State==3`(Attack)이 계속 참이라 "Any State → Attack" 전이가 스윙을 매 프레임 즉시 끊는다. 별도 오버라이드 레이어는 빈 상태가 base를 덮어쓰는 부작용을 또 처리해야 한다. **State int 펄스**는 한 순간 하나의 int만 참이라 이 충돌이 없고, 괴한 드라이버가 이미 쓰던 검증된 방식이다.
>
> `Attack`(int 3) 상태 모션은 그대로 **단발 스윙 클립**이다(교체 안 함). 저항 중 "버틴 자세"는 드라이버가 FSM Attack을 Idle(0)로 매핑해 표현하고, int 3은 스윙 펄스로만 진입한다. `State==3 → Attack` Any State 전이는 `CanTransitionToSelf=0`이라 진입 후 클립을 재시작하지 않고 1회 재생 후 마지막 프레임을 유지한다(펄스 종료 시 드라이버가 base로 되돌림). — MCP 연결 후 이 값만 확인.

### 2. `NpcController` — 스윙 브로드캐스트

저항 NPC의 스윙을 전 피어에 알린다. `ThugAttacker.OnAttack`과 **동일 패턴**:

```
public event Action OnAttackSwing;         // 전 피어에서 발생
RaiseAttackSwing():                        // 서버/오프라인이 호출
    OnAttackSwing?.Invoke()                // 로컬 발행
    if (IsServer) PlayAttackSwingClientRpc()
[ClientRpc] PlayAttackSwingClientRpc():
    if (IsServer) return                   // 서버는 위에서 이미 발행
    OnAttackSwing?.Invoke()                // 원격 클라 중계
```

서버 권위 유지: 스윙 판단은 서버(또는 오프라인) FSM Tick에서만, 클라는 애니메이션만 표현. (#56 패턴)

### 3. `NpcResistState` — 타격을 오프셋 뒤로 예약

현재 `SwingAttack()`은 스윙 알림과 데미지를 **같은 순간**에 처리한다. 이를 분리한다:

```
Tick():
    게이지 0 → Captured (기존 그대로, #205 위협 정리 유지)
    if (Time.time >= m_nextAttackTime):
        m_nextAttackTime = Time.time + ResistAttackInterval
        m_owner.RaiseAttackSwing()                 // 스윙 애니 발행 (준비 시작)
        m_pendingStrikeTime = Time.time + m_strikeOffsetSeconds  // 타격 예약
    if (m_pendingStrikeTime 유효 && Time.time >= m_pendingStrikeTime):
        m_pendingStrikeTime = 무효
        if (SwingAttack())                          // 타격 순간에 범위 재수집+데미지
            Defeat("교전 플레이어 전원 무력화"); return
    제한시간 초과 → Defeat("제압 제한 시간 초과")  // 기존 그대로
```

`SwingAttack()`(범위 수집·데미지·전원 무력화 판정)은 그대로 두고 **호출 시점만** 스윙 시작 → 타격 프레임으로 옮긴다. 범위 재수집이 타격 순간에 일어나므로 준비 중 벗어난 플레이어는 자연히 빠진다.

> `m_strikeOffsetSeconds`는 `NpcController`에 `[SerializeField]`(기본 0.45f, Tooltip: "스윙 시작→타격 프레임 시간(초)")로 두고 상태가 참조. 튜닝 대상이므로 인스펙터 노출.

### 4. `ThugAttacker` — 타격을 오프셋 뒤로, 타격 시점 재검증

현재 `ChaseAndAttack`은 `NotifyAttack()`(→ `OnAttack`) 직후 즉시 `TakeDamage`. 분리한다:

```
사거리 안 && Time.time >= m_nextAttackTime:
    m_nextAttackTime = Time.time + m_attackInterval
    NotifyAttack()                                  // 스윙 애니 발행
    m_pendingStrikeTime = Time.time + m_strikeOffsetSeconds
    m_pendingTarget = target

Update 내 타격 예약 처리:
    if (m_pendingStrikeTime 유효 && Time.time >= m_pendingStrikeTime):
        m_pendingStrikeTime = 무효
        타깃이 여전히 유효(IsTargetable) && 사거리 안이면 TakeDamage  // 재검증
```

준비 중 표적이 벗어나거나 다운되면 빗나간다 — 회피 창.

### 5. 애니메이션 드라이버 통일 (State int 펄스)

두 드라이버 모두 **스윙 시 State를 Attack(3)으로 `m_swingAnimSeconds`(기본 0.9초) 동안 펄스했다가 base로 복귀**한다.

| 드라이버 | 변경 |
|---|---|
| `NpcAnimationDriver` | `m_controller.OnAttackSwing` 구독. `HandleAttackSwing`: State=Attack(3), `m_swingUntil` 설정. `Update`: `m_swingUntil` 경과 시 base로 복귀. FSM `Attack`의 base는 Idle(0)로 매핑(`AnimatorBaseState`). 상태 전이가 오면 진행 중 스윙을 취소하고 새 base를 즉시 적용. |
| `ThugAnimationDriver` | 기존 State 홀드 방식 유지하되 유지 시간을 `m_swingAnimSeconds`(0.9초)로 명명·연장. 스윙 사이는 기존 이동 판별(Run/Idle) 그대로. |

`m_swingAnimSeconds`는 타격 주기(저항 1.5초·괴한 1.2초)보다 짧고 타격 오프셋(0.45초)보다 길어야 한다 — 그래야 타격이 스윙 도중에 들어간다. 이동 속도 추정·발 미끄럼 보정 로직은 그대로 둔다(이번 범위 아님).

### 6. 정면 부채꼴 방향 판정 (#220 추가)

기존엔 반경 안이면 방향 무관하게(360°) 맞았다. 이제 **타격 프레임 순간 NPC 정면 부채꼴 안**에 있어야 맞는다.

| 요소 | 내용 |
|---|---|
| 각도 | `m_attackConeAngle`(기본 120° 전체 = 정면 ±60°). `NpcController`·`ThugAttacker` 각각 `[SerializeField]`. |
| 표적 바라보기 | 저항 NPC는 정지 상태라 안 돌아 부채꼴 기준이 엉뚱해진다 → `NpcResistState`가 매 Tick `FaceTarget()`으로 위협(없으면 사거리 내 최근접)을 향해 yaw 회전(`m_attackTurnSpeed` 540°/s). Enter에서 `Agent.updateRotation=false`(수동 회전과 다툼 방지), Exit에서 복구. 괴한은 추격으로 이미 표적을 바라봄. |
| 판정 | 타격 시점 `Vector3.Angle(forward_xz, to_xz) <= 각도/2`. 저항은 `SwingAttack`에서 부채꼴 밖 대상을 건너뛰고, 정면 교전 대상이 0명이면 허공 스윙(패배 판정 안 함). 괴한은 `ProcessPendingStrike`에서 사거리+부채꼴 통과 시에만 명중. |
| 동기화 | yaw 회전은 NetworkTransform `SyncRotAngleY=1`로 전 피어 복제 — 서버가 돌린 방향을 클라도 본다. 데미지 판정은 서버 transform 기준(권위). |

> **GDD 영향:** 저항형 선제 범위 타격(7-4)이 360°에서 **정면 120°**로 좁아졌다. 정면에서 제압하려는 플레이어는 여전히 맞지만 측면·배후로 돌면 안전 — 협동 회피 여지가 생긴다. 확정 시 GDD에 반영 필요.

## 동기화 (전 피어)

- **저항 NPC:** 스윙 → `NpcController.OnAttackSwing`(서버 로컬 + ClientRpc) → 전 피어에서 State int 펄스. base 상태는 기존 `m_networkState`로 동기화.
- **괴한:** 스윙 → `ThugAttacker.OnAttack`(기존 ClientRpc) → 전 피어에서 State int 펄스. 위치는 NetworkTransform.
- **데미지:** 서버 전용. HP는 `PlayerData` 동기화 HP로 전 클라 반영. 시각 스윙과 서버 데미지 순간이 오프셋만큼 뒤에서 일치.

## 완료 기준 매핑

- [ ] **공격 재생·전환 로직 정리** → State-루프/0.6초-홀드 이원화를 State int 펄스 단발 스윙 하나로 통일
- [ ] **저항형 NPC 회귀 없음 (#205 포함)** → 게이지·제압·제한시간·`ClearThreat`/`Defeat` 도주 전환 유지, 타격만 오프셋 뒤로
- [ ] **전 피어 애니메이션 동기화** → `OnAttackSwing`/`OnAttack` ClientRpc 브로드캐스트

## 비고

- 회피 창(오프셋)은 의도된 동작 변화다. 저항 NPC의 전원 무력화 판정·괴한의 명중 모두 **타격 프레임**에 평가된다.
- `m_strikeOffsetSeconds` 기본 0.45f는 클립의 팔이 닿는 대략 프레임. 클립 확정 후 눈으로 미세 튜닝.
- 정면 부채꼴(#220 추가, 섹션 6)은 이슈 원래 범위(애니메이션 리팩토링) 밖이지만 "맞는 지점"을 방향까지 명확히 하려고 함께 포함했다. `m_attackConeAngle`(120°)·`m_attackTurnSpeed`(540°/s)로 튜닝.
