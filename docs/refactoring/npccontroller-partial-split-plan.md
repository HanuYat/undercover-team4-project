# NpcController 도메인 partial 분리 계획

> **[대체됨 — 2026-08-05 · 해소 완료 2026-08-09]** 이 문서의 partial 분리는 #259로 완료됐고(12파일 1,978줄),
> 그 partial은 [npccontroller-component-split-plan.md](npccontroller-component-split-plan.md)(#503)의 컴포넌트
> 분리로 **전부 사라졌다 — 지금 partial은 0개다.**
> 아래 § 1의 "컴포넌트 분리 비추천" 판단은 그 문서 § 2의 실측으로 **뒤집혔다.** § 5 검증 절차도 그 문서 § 7이
> 이어받았다(순수 이동 PR에서 무엇만 골라 회귀할지가 보태져 있다).
> **이 문서는 partial 분리 시절의 기록으로만 남긴다 — 수치·절차 모두 새 문서가 정본이다.**

> 작성: 2026-07-21 · 브랜치 `feature/101-penalty`에서 실제로 1회 수행 후 **되돌린** 작업의 기록이다.
> 되돌린 이유: NPC 담당자가 NpcController를 활발히 작업 중이라, 지금 분리하면 그쪽 브랜치와 대규모 충돌이 난다.
> **NPC 담당자의 진행 중 작업이 머지된 뒤**, 이 문서대로 다시 수행하면 된다 (검증 방법 포함 — 한 번 해 본 절차라 그대로 따라가면 된다).

## 1. 왜 분리하나

NpcController.cs가 **847줄**이다 (오검거 페널티 #277~#279 이후 기준). 이 파일은 "모든 상태의 튜닝 필드 + 전이 API"가
모이는 허브라 **새 도메인이 생길 때마다 계속 자란다**. 실제로 연행(#59)→검거 반응(#76/#79)→패닉(#81)→수감(#228)→
침입(#231)→오검거 페널티(#277~)가 전부 이 한 파일에 쌓였다.

**partial class 분리**를 쓰면:
- 클래스는 하나 그대로 — **직렬화·프리팹·씬 배선·상태 클래스의 `m_owner.X` 참조 전부 무변경**
- NGO RPC도 문제없음 (ILPP는 파일이 아니라 클래스 단위로 처리)
- diff가 "이동만"임을 리뷰어가 확인하기 쉬움
- 이후 새 도메인은 새 partial 파일로 추가 → 본체가 더 안 자람

별도 컴포넌트(`NpcPenaltyAgent` 등)로 빼는 대안은 **비추천**: 프리팹 수정 + 상태 클래스들의 참조 경로 변경
(`m_owner.X` → `m_owner.Penalty.X`)이 필요해서 "이동만" 리팩토링이 아니게 된다.

## 2. 목표 파일 구성 (실측 줄 수 — 1회 수행 결과)

| 파일 | 줄 수 | 담당 도메인 |
|---|---|---|
| `NpcController.cs` | ~192 | **코어** — 배회 튜닝, FSM 구축(Awake), 상태 동기화(#56), Start/InitBehavior/Update, SetFrozen |
| `NpcController.Custody.cs` | ~184 | 연행·수감·석방·수갑·인계 방치 (#59/#228/#229/#230) |
| `NpcController.Reaction.cs` | ~220 | 검거 반응 — 도주·저항·제압 게이지·기절·공격 스윙 (#76/#79/#205/#213/#220) |
| `NpcController.Panic.cs` | ~104 | 패닉·소란 전파 (#81) |
| `NpcController.Intrude.cs` | ~47 | 침입 — 범인 탈출 이벤트 (#231) |
| `NpcController.Penalty.cs` | ~155 | 오검거 페널티 — 수용·추격·호송 (#277~#279) |

## 3. 멤버 배정표 (이대로 옮기면 된다)

각 파일은 `public partial class NpcController { ... }` — 어트리뷰트(`[RequireComponent]`)와 상속 선언은 **본체에만** 남긴다.
본체 클래스 선언에 `partial` 키워드 추가를 잊지 말 것.

### 코어 (NpcController.cs에 남김)
- 필드: 배회 반경/지점/Idle/긴 대기/속도 편차 헤더 블록, `m_agent`, `m_stateMachine`, `m_frozen`, `m_networkState`
- 프로퍼티: `Agent`, `StateMachine`, `Wander*`, `Idle*`, `LongIdle*`, `CurrentState`, `OnStateChanged`
- 메서드: `Awake`(FSM 상태 등록 전부), `OnNetworkSpawn/Despawn`, `Start`, `InitBehavior`, `Update`,
  `HandleFsmStateChanged`, `HandleNetworkStateChanged`, `SetFrozen`
- 참고: `Update`가 Panic 파트의 `EmitDisturbancePulse()`를 호출한다 — 같은 클래스라 그대로 됨

### Custody (연행·수감·수갑)
- 필드: `[Header("연행 (#59)")]` 블록, `[Header("인계 방치 (#230)")]` 블록
- 프로퍼티: `Escort*` 4종, `CapturedEscape*` 2종, `EscortTarget`, `JailCell`, `OnJailed`, `IsDelivered`
- 메서드: `MarkDelivered`, `ClearDelivered`, `StartEscort`, `StopEscort`, `SendToJail`, `NotifyJailed`,
  `ReleaseFromCustody`, `HasHandcuffs`, `FindHeldHandcuffs`, `DropHandcuffs`

### Reaction (검거 반응)
- 상수: `k_threatSearchRadiusMultiplier`
- 필드: `[Header("검거 반응 (#76)")]` 블록(+ `m_subdueGaugeMax`, `m_stunSeconds`), `[Header("저항 전투 (#79)")]` 블록,
  `m_syncedSubdueGauge`, `m_subdueGauge`
- 프로퍼티: `Flee*` 5종, `SubdueGaugeMax`, `StunSeconds`, `Resist*`, `Strike*`, `Swing*` 3종(+`NextSwingVariant`/`SwingImpactOffset`),
  `AttackConeAngle`, `AttackTurnSpeed`, `ThreatSearchRadius`, `SubdueGauge`, `ThreatTarget`, `OnAttackSwing`
- 메서드: `RaiseAttackSwing`, `PlayAttackSwingClientRpc`, `StartFlee`, `ClearThreat`, `StartResist`,
  `ResetSubdueGauge`, `ApplySubdueHit`, `RequestSubdueHit`, `SubdueHitRpc`, `CaptureBySubdue`, `EnterStunned`, `SetSubdueGauge`

### Panic (패닉·소란)
- 필드: `[Header("패닉 (#81)")]` 블록, `s_disturbanceBuffer`, `m_nextDisturbancePulseTime`
- 프로퍼티: `Panic*` 3종, `PanicSource`, `LastDisturbedTime`
- 메서드: `EmitDisturbancePulse`, `RequestDisturbancePulse`, `BroadcastDisturbance`(static), `EnterPanic`

### Intrude (침입)
- 프로퍼티: `IntrudeTarget`, `IntrudeUnlockSeconds`, `OnIntrudeFinished`, `OnIntrudeUnlockStarted`
- 메서드: `StartIntrude`, `NotifyIntrudeFinished`, `NotifyIntrudeUnlockStarted`

### Penalty (오검거 페널티)
- 필드: `[Header("오검거 추격 (#278)")]` 블록 6종
- 프로퍼티: `Chase*` 6종(튜닝), `DetentionSpot`, `ChaseTarget`, `PenaltyConvergeTarget`, `ChaseRepelBy`,
  `ChaseRepelUntil`, `PenaltyEscortLeader/Offset/Goal`, `OnPenaltyCaught`
- 메서드: `NotifyPenaltyCaught`, `SendToDetention`, `StartPenaltyChase`, `SetChaseTarget`, `StartPenaltyConverge`,
  `ApplyChaseRepel`, `StartPenaltyEscort`, `EndPenaltyDuty`

### 파일별 using
- 코어: `System`, `Unity.Netcode`, `UnityEngine`, `UnityEngine.AI`, `Random = UnityEngine.Random`
- Custody: `System`, `Unity.Netcode`, `UnityEngine`
- Reaction: `System`, `Unity.Netcode`, `UnityEngine`
- Panic: `UnityEngine`
- Intrude: `System`, `UnityEngine`
- Penalty: `System`, `UnityEngine`

## 4. 절차

1. 본체 클래스 선언에 `partial` 추가
2. 위 배정표대로 블록을 **그대로 잘라 붙이기** (수정 금지 — "이동만"이 리뷰 근거)
3. 새 파일 5개 + `.meta`(에디터가 생성) — 커밋에 meta 포함할 것
4. 단독 커밋으로 분리: "이동만, 동작 변화 없음" 명시

## 5. 검증 (1회 수행 때 실제로 쓴 방법)

- **컴파일**: Unity Console 에러 0 — partial 누락·중복이 있으면 여기서 다 걸린다
- **심볼 중복 검사**: 대표 심볼들이 전체 파일에서 정확히 1회씩만 정의되는지
  ```bash
  cd Assets/Scripts/NPC
  for sym in "float m_wanderRadius" "float m_escortFollowDistance" "float m_fleeSpeedMultiplier" \
             "float m_disturbanceRadius" "Transform IntrudeTarget" "float m_chaseMaxSpeed"; do
    echo "$sym: $(cat NpcController*.cs | grep -c "$sym")회"   # 전부 1이어야 함
  done
  ```
- **멤버 유실 검사**: 분리 전(HEAD) 멤버 시그니처 목록과 분리 후 합본을 비교 — 비어야 정상
  ```bash
  git show HEAD:Assets/Scripts/NPC/NpcController.cs \
    | grep -oE "(public|private|protected)[a-zA-Z<>, _\[\]]+ [A-Za-z_]+\s*(\(|=>|\{ get|;|=)" | sort > /tmp/old.txt
  cat Assets/Scripts/NPC/NpcController*.cs \
    | grep -oE "(public|private|protected)[a-zA-Z<>, _\[\]]+ [A-Za-z_]+\s*(\(|=>|\{ get|;|=)" | sort > /tmp/new.txt
  comm -23 /tmp/old.txt /tmp/new.txt   # 출력이 비어야 정상
  ```
- **플레이 확인**: NPC 스폰·배회·체포·연행 한 사이클 (프리팹 인스펙터 값 유지 확인 포함)

## 6. 주의사항

- **인스펙터 헤더 순서가 바뀔 수 있다** — partial 파일 순서에 따라 필드 표시 순서가 재배열된다.
  직렬화는 필드 이름 기준이라 **프리팹 저장 값은 전부 유지**된다 (코스메틱 이슈).
- 상수·private 필드도 partial 간 자유롭게 참조 가능하지만, **필드와 그 접근 프로퍼티는 같은 파일에** 두는 것이
  나중에 읽기 편하다 (배정표가 그렇게 짜여 있다).
- `WrongfulArrestPenalty`는 같은 방식으로 이미 분리되어 머지됨(본체=정책 / `.Carry.cs`=호송 연출) — 선례 참고.
