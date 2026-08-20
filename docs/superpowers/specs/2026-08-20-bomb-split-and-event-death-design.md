# BombDevice 분할 · 스폰형 이벤트 사망 처리 · #768 리뷰 지적 정리

작성일: 2026-08-20 · 이슈 #688 + 신규(분할) · 브랜치 `feature/768-bomb-npc-damage`

선행 스펙: [2026-08-20-bomb-npc-damage-design.md](2026-08-20-bomb-npc-damage-design.md) (#768)

## 배경

#768(폭발이 NPC에도 피해)이 구현·리뷰를 통과했고, 그 리뷰가 세 가지를 남겼다.

1. **주석 3건이 거짓이거나 자리를 잘못 잡았다.** 특히 `BombDevice.cs:649`의 "래그돌이 이미 물리를
   쥐고 있다"는 **주된 경우에 틀렸다** — `NpcDeath.ServerEnterDead`는 래그돌에 진입하지 않고
   (`NpcRagdoll.Update`의 폴링이 다음 프레임에 한다), 그 줄이 도는 순간 상태는 아직 `Animated`다.
   프로젝트 규칙이 "틀린 왜는 없는 것보다 나쁘다"이므로 머지 전에 고친다.

2. **선행 #688이 저장소 어디에도 없다.** `SpawnedNpcEventBase`에 사망 처리가 전혀 없어, #768이
   만들어 낸 "폭발로 죽는 난동자·나체 난동꾼·소매치기"가 그대로 함정에 빠진다. 원래 별도 브랜치로
   먼저 머지할 계획이었으나, 팀 결정으로 이 브랜치에 함께 싣는다.

3. **`BombDevice`가 693줄로 커졌다.** #768이 +51줄을 얹으면서 한 클래스가 상태 기계 · 네트워크
   동기화 · 수명 타이머 · NavMesh 추격 · 폭발 세기 계산 · 폭발 적용과 RPC를 모두 쥐게 됐다.
   폭심 감쇠식을 읽으려면 NGO RPC 코드를 지나쳐야 한다.

세 묶음은 서로 독립적이지만 한 브랜치에 순서대로 얹는다(팀 결정 2026-08-20). 분할이 마지막이어야
1의 주석 수정이 이동 대상 코드에 먼저 반영된다.

## 결정 사항

### 사망 처리 (#688)

- **기존 `ReleaseToCity` 경로를 재사용한다 — 새 종료 경로를 만들지 않는다.** 사망은 이벤트에게
  "이탈"과 같은 종류의 사건이다: NPC는 남고, 마커도 남고, 이벤트만 손을 뗀다. 실제로 필요한 것은
  `HandleStateChanged`에 `Dead` 분기 하나뿐이다.

- **시체를 없애지 않는다.** GDD 9장(384행)이 "시체를 유치장까지 끌고 가면 인계로 판정된다"를 이미
  규정하고, `JailIntake`가 `NpcState.Dead`를 인계 대상으로 받는다(`JailIntake.cs:344`). 시체를
  Despawn하면 그 경로가 사라지고, 눈앞에서 시체가 지워지는 것도 보인다.

- **`MisdemeanorLoiterer`를 시체에도 붙인다.** 붙여도 안전하다 — `Dead`는 `Captured`/`Escorted`/
  `Jailed` 분기에 걸리지 않고, `m_riotPending`·`m_rioting`이 둘 다 false라 `TryIgniteRiot`도
  불리지 않는다. 얻는 것은 **라운드 종료 정리**다(시체가 다음 라운드로 누수되지 않는다). 탈옥
  방출로 `BeginRiot`이 시체에 걸려도 상태가 `Idle`/`Walk`가 아니라 점화되지 않는다.

- **죽은 소매치기의 훔친 물건은 시체에 남고, 회수 경로는 없다.** 별도 처리를 만들지 않는다 —
  물건은 라운드 종료 시 `MisdemeanorLoiterer`가 `ServerLoseStolenItem()`으로 손실 확정한다.
  유령 아이템이 남지 않으므로 코드 수정은 필요 없다.

  > ⚠ **처음 이 결정을 "시체를 유치장에 끌고 가면 회수된다"로 적었는데 거짓이다.** 시체는
  > `NpcState.Jailed`에 **도달하지 못한다** — `NpcStateMachine.cs:47`이 `Dead` 이탈을 거부하고
  > `NpcCustody.SendCorpseToJail`(`:186`)은 상태를 바꾸지 않고 **순간이동만** 시킨다. 그래서
  > `MisdemeanorLoiterer.Update`의 `Captured or Escorted or Jailed` 분기가 시체에는 영영 걸리지
  > 않고 `ServerDropStolenItem()`도 불리지 않는다. 소매치기를 죽이면 훔친 물건은 잃는다 —
  > 시체 인계로 되찾을 수 없다. 이것을 바꾸려면 별도 이슈가 필요하다.

- **`m_releaseQueued`로 지연 처리한다 — `ReleaseToCity()`를 직접 부르지 않는다.** `Idle`/`Walk`
  이탈 분기와 같은 관례다(#310): `OnStateChanged` 안에서 곧바로 종료 처리를 하면 전이 통지가
  중첩된다.

- **`m_pendingStart`를 함께 내린다.** `ServerTick`이 `m_pendingStart`를 `m_releaseQueued`보다
  **먼저** 보기 때문이다. 스폰 프레임에 폭발이 겹치면 시체에 `ApplyBehavior()`가 걸린다.
  이것이 이 변경에서 유일하게 자명하지 않은 줄이다.

### BombDevice 분할

- **파셜 클래스가 아니라 컴포넌트로 쪼갠다.** 파셜은 파일만 늘리고 결합은 그대로 남긴다 — 같은
  클래스이므로 모든 private 필드가 여전히 서로에게 열려 있다. 컴포넌트로 나누면 경계가 공개
  API가 되어 실제로 지켜진다.

- **`BombDevice`는 파사드로 남는다.** 소비자 8개가 전부 `GetComponent(InParent)<BombDevice>`로
  붙어 있다(`BombChaseEvent`·`BombCrate`·`Baton`·뷰 5종). `State`·`IsCountingDown`·`CanBeStruck`·
  `RemainingSeconds`·`OnExploded`·`ServerDeploy`/`ServerArm`/`ServerDetonate`·`Active`를 그대로
  들고, 외부에서 실제로 쓰이는 폭발 API(`EvaluateKnockback` — `BombExplosionView` 한 곳)는
  한 줄 위임으로 유지한다. **결과적으로 소비자는 전원 무수정이다.**

  리팩터의 목표는 구조이지 API 변경이 아니다. 호출부를 함께 흔들면 "동작 무변화"를 검증할 수
  없다.

- **상태 주인은 끝까지 `BombDevice` 하나다.** `BombBlast.ServerExplode()`는 폭발만 수행하고
  `SetState(BombState.Exploded)`는 `BombDevice`가 부른다. 중복 폭발 가드(`if (m_state == Exploded)
  return`)도 상태를 아는 쪽에 둔다. 상태 전이가 두 컴포넌트로 갈리면 원격 동기화 경로
  (`HandleStateSyncedChanged`)가 어느 쪽을 믿어야 하는지 모호해진다.

- **`BombBlast`는 `NetworkBehaviour`여야 한다.** `BlastDeathsClientRpc`가 ClientRpc이기 때문이다.
  한 `NetworkObject`에 `NetworkBehaviour` 여러 개는 NGO에서 정상이며, `NetworkVariable` 2개
  (`m_stateSynced`·`m_explodeTimeSynced`)는 상태 주인인 `BombDevice`에 남는다.

- **`BombChaseDriver`는 순수 `MonoBehaviour`다.** 추격에 동기화 상태가 없다 — 이동 결과는
  `NetworkTransform`이 실어 보내고, 원격 피어는 에이전트를 끈다. `NetworkBehaviour`로 만들
  이유가 없다.

- **감쇠식은 `BombBlastProfile`(순수 C# `[Serializable]`)로 뺀다.** 이 세 함수는 `transform`
  하나만 참조하는 순수 계산이고, 주석이 180줄(실측표·탄도 공식·튜닝 근거)로 파일의 최대
  덩어리다. Unity 타입에서 떼어 내면 폭심 감쇠를 읽을 때 네트워크·수명 코드를 지나치지 않는다.

  `ScriptableObject`로 하지 않는다 — 라운드당 폭탄 1종이라 에셋으로 공유할 대상이 없고,
  에셋이 늘면 프리팹과 에셋 두 곳을 맞춰야 한다.

- **폭심은 인자로 받는다.** `Profile`에는 `transform`이 없다. 거리 0일 때의 대체 방향
  (현재 `transform.forward`)도 인자로 넘긴다. `BombBlast`가 기존 시그니처의 래퍼를 유지하므로
  호출부 모양은 바뀌지 않는다.

- **`m_wakeRadius`는 `BombChaseDriver`가 갖는다.** 값의 의미가 "표적 탐색 반경"이라 추격 쪽이다.
  `BombDevice`의 `Dormant` 분기는 `m_chase.PollWakeTrigger()`를 묻는다 — 순수 질의가 아니라
  재타겟 시계를 함께 미루므로(원본 인라인 코드와 같은 동작) 이름에 `Poll`을 붙였다.

- **컴포넌트 누락은 `RequireComponent`로 막는다.** 프리팹에서 하나가 빠지면 폭발이 조용히
  일어나지 않는다 — 컴파일이 잡아 주지 않는 종류의 사고다.

- **동작을 바꾸지 않는다.** 리뷰가 후속으로 분류한 것들(`damage <= 0`이 넉백까지 끊는 문제,
  폭발 로그의 NPC 미포함, 정적 버퍼 참조 유지, 레이어 마스크 부재)은 **이 분할에서 건드리지
  않는다.** 구조 변경과 동작 변경을 섞으면 회귀의 원인을 가릴 수 없다.

### 선행 스펙 정정

#768 스펙의 두 문단이 사실이 아니다. 그대로 두면 다음 사람이 같은 잘못된 결론을 낸다.

- **"이미 정착한 시체는 `EnterRagdoll`이 스스로 거부한다"(52~54행·138행) — 거짓이다.** 정착한
  시체는 `m_state == RagdollState.Ragdoll`이므로 `EnterRagdoll`의 첫 분기에 걸려 임펄스를
  **받는다**. 실제 방어는 구현이 넣은 `wasAlive` 검사 하나뿐이다. `NpcRagdoll.cs:334`의
  `// 이미 정착했다 — 다시 날리지 않는다` 주석도 같은 오해의 근원이다(도달 불가능한 가드다).

- **"왜 이 순서인가"의 근거(94~98행) — 낡았다.** `NpcKnockback.ServerApplyKnockback`은 #634부터
  `Dead`·`Jailed`·`Intruding`을 거른다. 분기는 여전히 옳지만 이유가 다르다 — 시체를 되살리지
  않기 위해서가 아니라 **시체를 임펄스로 날리기 위해서**다.

## 설계

### 변경 파일

| 순서 | 파일 | 변경 |
|---|---|---|
| (a) | `BombDevice.cs` | 주석 2건 정정 + `wasAlive` 선언 위치 |
| (a) | `NpcRagdoll.cs` | 죽은 가드의 주석 문구 정정 (가드 자체는 유지) |
| (a) | `2026-08-20-bomb-npc-damage-design.md` | 위 "선행 스펙 정정" 2건 |
| (b) | `SpawnedNpcEventBase.cs` | `HandleStateChanged`에 `Dead` 분기 |
| (c) | `BombDevice.cs` | 파사드로 축소 (309줄) |
| (c) | `BombChaseDriver.cs` | 신규 (121줄) |
| (c) | `BombBlast.cs` | 신규 (204줄) |
| (c) | `BombBlastProfile.cs` | 신규 (139줄) |
| (c) | `Bomb.prefab` | 컴포넌트 2개 추가 + 인스펙터 값 이전 |

### (b) 사망 분기

`HandleStateChanged`의 `Captured` 분기 뒤, `Idle`/`Walk` 이탈 분기 앞에 둔다.

```
if (state == NpcState.Dead)
    m_pendingStart = false      // 스폰 프레임 사망 — 시체에 ApplyBehavior가 걸린다
    m_releaseQueued = true      // 전이 통지 중첩 회피 (#310 관례)
    return
```

결과: 이벤트 슬롯이 즉시 해제되고(`IsActive == false`), 소란 수명 타이머가 시체에
`StartFlee(null)`을 거는 경로가 사라지고, 마커와 라운드 종료 정리는 `MisdemeanorLoiterer`가
물려받는다.

`HandleArrestJudged`는 손대지 않는다 — 시체가 인계되는 시점에 이 이벤트는 이미 `m_npc == null`이고,
판정·수익은 마커를 보는 `ArrestJudge`가 처리한다.

### (c) 컴포넌트 경계

```
Bomb.prefab (NetworkObject · NavMeshAgent)
├── BombDevice        NetworkBehaviour  — 파사드 · 상태 · 수명 · 타이머
│     소유: BombState, m_stateSynced, m_explodeTimeSynced, m_state,
│           m_armAtLocal, m_explodeAtLocal, s_active
│     노브: m_emergeSeconds, m_countdownSeconds, m_lockSeconds, m_armOnStart
│     공개: Active, State, IsCountingDown, CanBeStruck, RemainingSeconds,
│           OnExploded, ServerDeploy, ServerArm, ServerDetonate
│           + EvaluateKnockback / ExplosionRadius / KnockbackForce (m_blast로 위임)
├── BombChaseDriver   MonoBehaviour     — NavMesh 추격
│     소유: m_agent, m_target, m_nextRetargetTime
│     노브: m_chaseSpeed, m_wakeRadius, m_retargetInterval,
│           m_retargetHysteresis, m_targetSearchRadius
│     공개: Tick(), Stop(), PollWakeTrigger(), ResetRetargetClock(), DisableAgent()
└── BombBlast         NetworkBehaviour  — 폭발 적용
      소유: m_blastBuffer, m_deathBuffer, s_blastColliders, s_blastNpcs
      노브: m_profile (BombBlastProfile)
      공개: ServerExplode(), EvaluateKnockback/Damage/RagdollImpulse (래퍼)
      비공개: NotifyBlastDeaths, BlastDeathsClientRpc, ApplyBlastRagdoll, ServerBlastNpcs

BombBlastProfile   [Serializable] 순수 C# — 감쇠식과 튜닝 문서
      노브: m_explosionRadius, m_explosionDamage, m_damageEdgeFalloff,
            m_knockbackForce, m_knockbackUpwardRatio, m_knockbackEdgeFalloff,
            m_ragdollImpulseScale, m_ragdollLiftRatio
      공개: Radius, Damage,
            Knockback(Vector3 delta, Vector3 fallbackDirection),
            Damage(Vector3 delta), RagdollImpulse(Vector3 delta, Vector3 fallback)
```

`BombDevice.Update`가 오케스트레이터로 남는다. 상태별 위임만 바뀌고 판단은 그대로다:

| 상태 | 지금 | 분할 후 |
|---|---|---|
| `Emerging` | 시각 도달 시 `SetState(Dormant)` | 그대로 |
| `Dormant` | `FindNearestFieldPlayer(m_wakeRadius)` | `m_chase.PollWakeTrigger()` |
| `Armed` | `TickChase()` | `m_chase.Tick()` |
| `Armed`→`Locked` | `StopAgent()` + `SetState` | `m_chase.Stop()` + `SetState` |
| 폭발 시각 도달 | `ServerExplode()` | `m_blast.ServerExplode()` + `SetState(Exploded)` |

`OnNetworkSpawn`의 원격 에이전트 비활성화는 `m_chase.DisableAgent()`로 옮긴다 — 에이전트 소유자가
끄는 것이 맞다. 권위 판정(`IsAuthority`)은 `BombDevice`에 남는다.

### 프리팹 값 이전

**이 작업의 가장 큰 위험이다.** 새 `SerializeField`의 코드 기본값은 기존 프리팹에 전파되지 않으므로,
컴포넌트를 추가하면 노브가 전부 0으로 시작한다. 폭발 반경 0은 예외를 던지지 않고 **조용히 아무
피해도 주지 않는다.**

절차: 이전 전에 `BombDevice`의 직렬화 값 13개를 기록 → 컴포넌트 추가 → `SerializedObject`로
`BombChaseDriver`(5개)와 `BombBlast.m_profile`(8개)에 기입 → **기록과 대조**한다. 눈으로 확인하지
않고 넘기지 않는다.

## 엣지 케이스

| 상황 | 결과 | 근거 |
|---|---|---|
| 난동자가 폭발로 죽는다 | 이벤트가 손을 떼고 시체가 남는다 | (b) `Dead` 분기 |
| 스폰 프레임에 죽는다 | `ApplyBehavior` 미실행 | `m_pendingStart = false` |
| 죽은 소매치기의 시체를 수감 | **훔친 물건은 회수되지 않는다** | 시체는 `Jailed`에 못 간다 (`NpcCustody.cs:186`) |
| 죽은 소매치기를 방치 | 라운드 종료 시 손실 확정 | `ServerLoseStolenItem` |
| 죽은 난동자를 탈옥으로 방출 | 소란 재개 안 됨 | 상태가 `Idle`/`Walk`가 아니다 |
| 연행 중 신병이 죽는다 | 밧줄이 끊기고 시체로 남는다 | `ServerEnterDead` ④ (기존) |
| 프리팹에 `BombBlast`가 없다 | 컴포넌트가 자동 추가된다 | `RequireComponent` |
| 원격 피어의 추격 | 에이전트가 꺼진다 | `m_chase.DisableAgent()` (기존 동작) |
| 오프라인 Play (스폰 전) | 전부 권위 | `IsAuthority`는 `BombDevice`에 유지 |

## 테스트

컴파일 에러 0건이 각 커밋의 완료 조건이다. Play 모드는 사용자가 확인한다.

**(b) #688**

1. 시민 무리에 난동자를 섞어 놓고 폭발 → 콘솔 에러 0건, 특히
   `"죽은 NPC를 Run으로 되돌리려 했다"`가 없어야 한다
2. 같은 종류의 돌발 이벤트가 다시 추첨된다 (슬롯이 해제됐다)
3. 죽은 난동자의 시체를 유치장에 인계 → 경범죄 수익 지급
4. 죽은 소매치기의 시체를 인계 → **훔친 물건이 회수되지 않는 것이 현재 정상이다**(위 ⚠ 참고).
   라운드를 끝내 손실 확정 로그가 한 번만 뜨는지 본다

**(c) 분할 — 동작 무변화 회귀**

#768 스펙의 테스트 7항목을 그대로 다시 돈다. 추가로 분할 고유 지점:

5. 상자 등장 → 대기 → 사람이 다가오면 무장 → 추격 → 폭심 확정 → 폭발 전 구간
6. 진압봉 즉발 (`Baton` → `ServerDetonate`)
7. 카운트다운 UI·소리·HUD가 원격 클라에서 그대로 (뷰 무수정 확인)
8. **폭발 세기가 분할 전과 같다** — 프리팹 값 이전 검증. 폭심 즉사 3.3m가 유지되는지

## 범위 밖

- 리뷰가 후속으로 분류한 동작 변경 4건 (`damage <= 0` 결합, 폭발 로그 NPC 미포함, 정적 버퍼
  참조 유지, 레이어 마스크 부재) — 별도 이슈
- `NpcRagdoll.cs:334`의 도달 불가능한 가드 **제거** — 주석 문구만 고치고 코드는 남긴다
- `BombDevice.Active` 정적 참조를 App 파사드로 옮기는 것 — 스폰물이라 현재 구조가 의도된 것이다
- `BombCrate`·`BombChaseEvent`·`BombLocatorHud` 등 형제 파일의 크기 — 전부 200줄 이하다
- `BombDevice.cs:483`의 선재 `?.` — 이번 변경이 건드리지 않는 줄
