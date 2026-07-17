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

**단발 스윙을 State int와 독립한 트리거로 오버레이한다.**

| 변경 | 내용 |
|---|---|
| 클립 루프 OFF | `NPC_Attack1H_Loop.anim` → 루프 해제(단발). 이름을 `NPC_AttackSwing.anim`으로 정리(guid 유지되어 컨트롤러 참조 안 깨짐). |
| Trigger 추가 | `AttackSwing` (트리거 파라미터). |
| 스윙 상태 | 새 상태 `AttackSwing`(단발 클립). **Any State → AttackSwing**: 조건 `AttackSwing` 트리거, `HasExitTime=false`, 전환 0.1초(즉발). **AttackSwing → Exit**: `HasExitTime=true`, ExitTime ~0.9 → 스윙 끝나면 base 상태(State int)로 복귀. |
| `Attack`(int 3) 모션 교체 | 기존 루프 공격 클립 → **Idle 클립**(버틴 자세). enum=Animator번호 규약 유지: `State==3`은 여전히 유효한 "전투 대기" 상태이고, 저항 NPC는 스윙 사이에 이 자세로 버틴다. |

스윙은 base 상태(Idle/Run/Attack 대기) **위에** 얹혀 재생되고 끝나면 돌아온다. 저항 NPC든 추격 중 괴한이든 같은 트리거 하나로 동작한다.

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

### 5. 애니메이션 드라이버 통일

| 드라이버 | 변경 |
|---|---|
| `NpcAnimationDriver` | `m_controller.OnAttackSwing` 구독 → `SetTrigger("AttackSwing")`. FSM `Attack` 진입 시 base는 버틴 자세(int 3, 이제 Idle 모션). |
| `ThugAnimationDriver` | 0.6초 State 홀드 로직(`m_attackAnimUntil`, `m_attackAnimDuration`) 제거 → `HandleAttack`에서 `SetTrigger("AttackSwing")`. 스윙 사이는 기존 이동 판별(Run/Idle) 그대로. |

두 드라이버가 같은 트리거 방식으로 통일된다. 이동 속도 추정·발 미끄럼 보정 로직은 그대로 둔다(이번 범위 아님).

## 동기화 (전 피어)

- **저항 NPC:** 스윙 → `NpcController.OnAttackSwing`(서버 로컬 + ClientRpc) → 전 피어 트리거. base 상태는 기존 `m_networkState`로 동기화.
- **괴한:** 스윙 → `ThugAttacker.OnAttack`(기존 ClientRpc) → 전 피어 트리거. 위치는 NetworkTransform.
- **데미지:** 서버 전용. HP는 `PlayerData` 동기화 HP로 전 클라 반영. 시각 스윙과 서버 데미지 순간이 오프셋만큼 뒤에서 일치.

## 완료 기준 매핑

- [ ] **공격 재생·전환 로직 정리** → State-루프/0.6초-홀드 이원화를 트리거 단발 스윙 하나로 통일
- [ ] **저항형 NPC 회귀 없음 (#205 포함)** → 게이지·제압·제한시간·`ClearThreat`/`Defeat` 도주 전환 유지, 타격만 오프셋 뒤로
- [ ] **전 피어 애니메이션 동기화** → `OnAttackSwing`/`OnAttack` ClientRpc 브로드캐스트

## 비고

- 회피 창(오프셋)은 의도된 동작 변화다. 저항 NPC의 전원 무력화 판정·괴한의 명중 모두 **타격 프레임**에 평가된다.
- `m_strikeOffsetSeconds` 기본 0.45f는 클립의 팔이 닿는 대략 프레임. 클립 확정 후 눈으로 미세 튜닝.
