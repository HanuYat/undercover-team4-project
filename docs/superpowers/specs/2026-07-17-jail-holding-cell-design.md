# 유치장 구현 — 판정 구역과 범인 관리 구역 분리 (#228)

- **이슈:** #228 [Feature] 유치장 구현 — 판정 구역과 범인 관리 구역 분리
- **브랜치:** `feature/228/jail-holding-cell` (base: `main`)
- **작성일:** 2026-07-17

## 목적

지금은 **판정이 곧 끝**이다. 연행된 NPC가 인계존(`HqDropoffZone`)에 닿으면 `ArrestJudge`가 진범/오검거를 판정하고 로그를 찍은 뒤, 진범이든 무고한 시민이든 **똑같이 그 자리에서 수갑 찬 채(`Captured`) 멈춰 선다**(`ArrestJudge.cs:105-115`). 검거된 범인이 어디로 가는지, 오검거된 시민이 어떻게 풀려나는지가 게임 안에 없다.

이 이슈는 인계존(**판정 구역**)과 유치장(**관리 구역**)을 분리해, 판정 이후의 행선지를 만든다:

- **진범·경범죄** → 유치장으로 이송·수용. 수용 인원은 전 피어에 동기화된다.
- **무고한 시민** → 수갑 해제 후 석방(배회 복귀).

동시에 GDD 12장의 보류 항목 "검거 후 자동 분류를 트리거하는 '특정 장소' 정의"를 **본부 인계존**으로 확정한다.

## 확정 사항

| 항목 | 결정 | 근거 |
|---|---|---|
| 이송 방식 | **NavMesh 자력 도보** | 판정 후 범인이 유치장까지 스스로 걸어간다. 텔레포트·디스폰과 달리 "수용됐다"가 눈에 보이고, 범인 탈출 이벤트(별도 이슈)가 얹힐 무대가 그대로 생긴다. |
| 오검거 카운트 | **범위 밖 — #101이 담당** | #101(오검거 페널티)이 이미 `ArrestJudge.OnArrestJudged`를 구독해 개인별 카운트 + 광장 매달기를 구현하기로 되어 있다(이슈 본문의 attach point). #228은 **수갑 해제·석방**만 하고 카운트/페널티는 건드리지 않는다 — 코드 충돌 방지. |
| 애니메이터 | **수정 없음** | 새 상태의 모션은 기존 `Escorted`(수갑 찬 채 걷기)·`Captured`(수갑 찬 대기) 클립을 드라이버가 빌려 쓴다. `NpcAnimationDriver`가 연행 근접 정지(#97)에서 이미 쓰는 기법. |
| 수용 인원 동기화 | **`NetworkVariable<int>`** | 서버 권위 판정 → 결과만 클라 동기화 (#56 패턴). 본부 UI(별도 이슈)가 읽을 수 있게 공개. |

## 컴포넌트

### 1. `NpcState` — `Jailed` 추가 (enum 끝에)

```
Jailed // 수감 — 판정 후 유치장으로 이송·수용 (#228)
```

> enum 값 = Animator 상태 번호 규약이지만, `Jailed`(=8)에 대응하는 Animator 상태는 **만들지 않는다**. 이송 중엔 `Escorted` 모션, 수용 후엔 `Captured` 모션을 드라이버가 대신 지정한다(아래 4번). 규약 예외이므로 enum 주석에 명시한다.

### 2. `NpcJailedState` — 유치장까지 걸어가 수용

이송과 수용을 한 상태 안에서 처리한다(연행이 "따라 걷기 ↔ 근접 정지"를 한 상태로 다루는 것과 같은 구조, #97):

```
Enter():
    Agent.isStopped = false; Agent.stoppingDistance = 0
    if (m_owner.JailCell != null) Agent.SetDestination(JailCell.position)
    m_admitted = false

Tick():
    if (m_admitted) return                       // 수용 완료 — 최종 상태, 스스로 나가지 않는다
    if (경로 계산 중) return
    if (남은 거리 > k_arriveDistance) return
    m_admitted = true
    Agent.isStopped = true; Agent.velocity = 0; Agent.ResetPath()
    m_owner.NotifyJailed()                       // 도착 1회 통보 → JailZone이 수용 인원 +1

Exit():
    Agent.isStopped = false; Agent.ResetPath()   // 탈출 이벤트(별도 이슈) 대비 복구
```

목적지 소실(`JailCell == null`)이면 그 자리에서 `m_admitted` 처리 — 유치장이 없는 테스트 씬에서 NPC가 멍하니 서 있지 않게 한다.

### 3. `NpcController` — 수감 진입점

기존 `StartEscort`/`StartFlee`와 동일한 서버 권위 가드 패턴:

```
public Transform JailCell { get; private set; }   // 서버에서만 유효
public event Action<NpcController> OnJailed;      // 수용 도달 — JailZone이 구독

public void SendToJail(Transform cell):
    if (IsSpawned && !IsServer) return
    EscortTarget = null; JailCell = cell
    m_stateMachine.ChangeState(NpcState.Jailed)

public void NotifyJailed():                       // NpcJailedState 전용
    OnJailed?.Invoke(this)

public void ReleaseFromCustody():                 // 수갑 해제 — 오검거 시민 석방
    if (IsSpawned && !IsServer) return
    if (CurrentState != NpcState.Captured) return
    JailCell = null
    m_stateMachine.ChangeState(NpcState.Idle)     // 배회 복귀
```

> `ReleaseFromCustody`는 #230(인계 방치 시 수갑 해제 후 도주) 브랜치가 만드는 해제 경로와 개념이 겹친다. 둘 다 미머지 상태이므로 **먼저 머지되는 쪽 기준으로 나중 브랜치가 합류**한다 — 이름·시그니처를 맞춰 두고, 리베이스 시 중복이면 하나로 합친다.

### 4. `NpcAnimationDriver` — 수감 모션 차용

`Escorted` 전용이던 "속도로 걷기/정지 모션 구분" 로직을 `Escorted`와 `Jailed` **둘 다**에 적용한다. `Jailed`는 Animator 상태가 없으므로 int를 직접 넘기지 않고:

| 상황 | Animator `State` |
|---|---|
| 유치장으로 이송 중(이동) | `(int)NpcState.Escorted` — 수갑 찬 채 걷기 |
| 유치장 도착·수용(정지) | `(int)NpcState.Captured` — 수갑 찬 대기 |

`HandleStateChanged`의 `state == Escorted` 초기화 분기도 `Jailed`를 포함하게 넓힌다(진입 시 이동 판별 시드). 상태 int를 그대로 넘기는 기본 경로에서 `Jailed`는 제외한다.

### 5. `JailZone` (신규, `NetworkBehaviour`) — 관리 구역

유치장 그 자체. **수용 지점 제공 + 수용 인원 동기화**만 한다.

```
[SerializeField] Transform[] m_cellPoints;        // 수용 지점(빈 GameObject). 비면 자기 transform
readonly NetworkVariable<int> m_inmateCount = new(0);

public int InmateCount => IsSpawned ? m_inmateCount.Value : m_localInmateCount;
public event Action<int> OnInmateCountChanged;    // 전 피어 — 본부 UI(별도 이슈)가 구독

public Transform ReserveCell()                    // 수용 지점 순차 배정 (서버 전용)
public void Admit(NpcController npc)              // 도착 통보 수신 → 카운트 +1, 중복 방어
public void ReleaseInmate(NpcController npc)      // 탈출 이벤트(별도 이슈)용 카운트 -1
```

- 수용 지점은 `m_cellPoints`를 **순차 배정**한다(모듈로 순환) — 여러 명이 한 점에 겹쳐 서지 않게. 지점 수보다 많이 들어오면 NavMesh 회피가 알아서 흩어 준다.
- 카운트는 **서버만 쓴다**. 오프라인 Play 테스트에서는 `NetworkVariable`에 쓰지 않고 로컬 값만 갱신 — `NpcController.SetSubdueGauge`의 이중 구조와 동일.
- 실제 수용은 `NpcController.OnJailed`(도착 통보)에서 +1 — **문을 통과한 시점**이지 판정 시점이 아니다. 이송 중 탈출(후속 이슈)이 카운트를 오염시키지 않는다.

### 6. `CustodyRouter` (신규, `MonoBehaviour`) — 판정 → 행선지

인계존/판정(`ArrestJudge`)과 관리 구역(`JailZone`) 사이를 잇는 얇은 라우터. `RoundManager`가 `OnArrestJudged`를 구독하는 것과 같은 방식이다:

```
OnEnable:  m_arrestJudge.OnArrestJudged += HandleArrestJudged
HandleArrestJudged(result):
    if (서버 아님) return                          // 판정 자체가 서버 전용이라 방어적 가드
    switch (result.Verdict):
        WantedCriminal, Misdemeanor:
            npc.OnJailed += m_jailZone.Admit  (1회 구독, 도착 시 해제)
            npc.SendToJail(m_jailZone.ReserveCell())
        WrongfulArrest:
            npc.ReleaseFromCustody()                // 수갑 해제 → 배회 복귀
```

판정(`ArrestJudge`)은 **누가 범인인가**만, 라우터는 **어디로 보내는가**만 안다. 라우터는 판정 직후 연행이 물리적으로 해제된(`deliverer.Release()` → `Captured`) 상태를 이어받는다.

### 6-1. `ArrestJudge` — 해제와 이벤트 순서 교체 (필수)

기존 `Judge()`는 **`OnArrestJudged` 발행 → 연행 해제** 순서다(`ArrestJudge.cs:102-115`). 이대로면 라우터가 `Jailed`로 보낸 **직후** `StopEscort()`의 `Captured` 전이가 행선지를 덮어써, NPC가 유치장으로 출발하지 못하고 그 자리에 선다. 석방 경로도 같은 이유로 깨진다(`ReleaseFromCustody`가 `Captured`를 요구하는데 그 시점엔 아직 `Escorted`).

→ **연행 해제를 이벤트 발행보다 앞으로** 옮긴다. 판정 → 신병 해제 → 행선지 라우팅 순서가 되어 구독자가 항상 `Captured`인 NPC를 넘겨받는다. 기존 구독자(`RoundManager`, 향후 #101)는 `ArrestResult`만 읽으므로 영향 없다.

> Play 모드에서 실제로 확인한 회귀다 — 순서 교체 전에는 `JailCell`이 배정된 채 상태만 `Captured`로 남았다.

### 7. `NpcStateRules` — 수감 상태 차단

```
IsCapturable(state) => state != Escorted && state != Captured && state != Jailed
```

유치장에 수감된 NPC에 수갑을 다시 채우지 못하게 한다. `HasSubdueInteraction`은 포함 목록 방식이라 `Jailed`가 자동으로 빠지므로 수정 불필요.

## 씬 구성

`Main Scene`에 **Jail** 오브젝트를 추가한다 (본부 인계존과 분리된 별도 구역):

- `Jail` (`JailZone`) — 자식으로 수용 지점 `Cell_0`~`Cell_3`(빈 GameObject). **NavMesh 위**에 둘 것.
- `CustodyRouter` — 인계존/판정과 같은 본부 관리 오브젝트에 얹는다(`ArrestJudge`가 붙은 오브젝트).
- `JailZone`은 `NetworkObject`가 필요하다 — 씬 배치 NetworkObject로 등록.

## 동기화 (전 피어)

| 대상 | 경로 |
|---|---|
| 판정·라우팅·이송 판단 | **서버 전용** (`ArrestJudge`가 이미 서버 게이트, FSM은 서버 권위) |
| NPC `Jailed` 상태 | 기존 `m_networkState` `NetworkVariable<NpcState>` |
| 유치장까지 걸어가는 위치 | 기존 `NetworkTransform` |
| 수용 인원 | `JailZone.m_inmateCount` `NetworkVariable<int>` → `OnInmateCountChanged` 전 피어 발행 |

## 완료 기준 매핑

- [ ] **인계존 판정 → 범인은 유치장으로 이동·수용** → `CustodyRouter` + `NpcJailedState` + `JailZone.ReserveCell`
- [ ] **시민 판정 시 수갑 해제** → `CustodyRouter`의 `WrongfulArrest` 분기 → `ReleaseFromCustody()`
- [ ] **오검거 카운트 증가** → **#101에 위임**(이미 `OnArrestJudged` attach point 확보). 이 이슈에서 구현하지 않음 — PR에 명시.
- [ ] **유치장 수용 인원이 전 피어 동기화** → `JailZone.InmateCount`(`NetworkVariable<int>`)
- [ ] **GDD 7-2 / 12장 / 부록 A 갱신** → 7-2에 '특정 장소' = 본부 인계존 + 유치장 수용/석방 흐름 명시, 12장 보류 항목 제거, 부록 A에 **인계존**·**유치장** 용어 추가

## 비고

- **자물쇠·탈출은 범위 밖.** 범인 탈출 이벤트(별도 이슈)가 이 무대 위에 얹힌다. `JailZone.ReleaseInmate`와 `NpcJailedState.Exit`의 이동 복구가 그 접합점이다.
- **수갑 반환 흐름(별도 이슈)** 도 범위 밖 — 이 이슈는 NPC 쪽 구속 해제까지만 하고, 플레이어가 수갑 아이템을 돌려받는 부분은 건드리지 않는다.
