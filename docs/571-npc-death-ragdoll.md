# NPC 사망 + 래그돌 (#571) — 진행 기록

**브랜치:** `feature/ragdoll-npc`

한 줄 요약: **NPC 체력 규칙을 "임계에서 눕고 0에서 죽는다"로 바꾸고, 죽은 NPC에 래그돌을 입히는
작업.** 1~4단계 코드가 전부 들어갔고, **`NpcRagdoll`은 아직 Play로 한 번도 안 돌려봤다.**

---

## 0. 지금 어디까지 됐나

| 단계 | 내용 | 상태 |
|---|---|---|
| 1 | HP 규칙 교체 (임계 넉다운 / 0 사망) | ✅ Play 테스트 통과 |
| 2 | 규칙 전파 + 현상금 집계 + 이벤트 정리 | ✅ Play 테스트 통과 |
| 3 | 리그 준비 (셋업 스크립트 일반화 + `NPC_Citizen` 리그) | ✅ 완료 |
| 4 | `NpcRagdoll` 작성 + `NPC_Citizen`에 부착 | ⚠️ **코드만 — 미검증** |
| **4-1** | **Play 확인 (§7)** | ⬜ **다음 작업** |
| 5 | 나머지 NPC 프리팹에 리그 복제 | ⬜ 4-1이 통과한 뒤 |
| 6 | (후속) 폭발 사망 임펄스 | ⬜ 선택 |

### 커밋 (전부 `origin/feature/ragdoll-npc`에 푸시됨)

```
3787599  시체를 물리에 넘기는 NpcRagdoll을 붙인다 (#571)
340e820  래그돌 셋업을 NPC에도 쓸 수 있게 일반화하고 시민 리그를 만든다 (#571)
9db7ec2  죽은 대상도 검거로 계상하고 시체를 붙든 참조를 끊는다 (#571)
804361d  NPC 체력 0을 기절이 아니라 사망으로 보낸다 (#571)
```

---

## 1. 규칙 — 무엇이 바뀌었나

기준 수치: `MaxHp 100` · 진압봉 `34` · `KnockdownHpRatio 0.4` → **임계 HP 40**

```
1대: 100 → 66   아무 일 없음
2대:  66 → 32   ← 넉다운 (기존 기절 그대로, 5초 뒤 기상)
3대:  32 →  0   ← 사망
```

### 회복 지점이 사라졌다

예전에는 기절에서 깨어날 때 체력을 만피로 되돌렸다(`ServerRestoreHp`). 그 함수와 호출부 둘
(`NpcStun.ExitStun` · `NpcStunnedState.Exit`)을 전부 걷었다.

⚠ 그 두 곳에는 **"회복이 빠지면 그 NPC가 라운드 내내 무적이 된다"**는 긴 주석이 있었다. 근거는
"HP 0인 채로 깨어나면 0 도달 엣지가 다시 안 걸린다"였는데, **HP 0이 깨어나는 상태가 아니게 되면서
그 경로 자체가 사라졌다.** 두 자리에 왜 사라졌는지를 남겨 뒀으니 지우지 말 것 — 없으면 다음 사람이
버그로 보고 되돌린다.

넉다운도 **임계를 내려가는 한 방향 엣지**라 개체당 한 번이다. 그게 의도다: 임계 아래로 내려간
몸이 다음에 맞으면 다시 눕는 게 아니라 죽는다.

### ⚠ 임계 비율과 진압봉 데미지는 짝이다

`0.2`로 잡으면 `32 → 0` 타격이 임계 교차와 0 도달을 **동시에** 만족해 **넉다운 구간이 아예
생기지 않는다.** 데미지를 바꾸면 이 값도 같이 봐야 한다. 한 타격이 둘 다 만족하면 **사망이 이긴다**
(`NpcHealth.SetHp` — 순서를 뒤집으면 쓰러지기만 하고 사망이 영영 안 걸린다).

---

## 2. 사망을 FSM 상태로 둔 이유

`NpcState.Dead` + `NpcDeath` 부품. **기절(#292)과 정확히 반대다:**

| | 기절 | 사망 |
|---|---|---|
| 방식 | 오버레이 (상태 안 바꿈) | **상태 전이** |
| 목적 | 호송·수감·페널티 링크를 **지킨다** | 그 링크를 전부 **끊는다** |

상태로 두면 `NpcStateRules`의 제외 목록 한 곳만 고쳐도 체포·타격·밧줄·E가 함께 닫히고, 커스터디
표식 정리도 `HandleFsmStateChanged`가 이미 하던 경로를 탄다.

> **되돌리려면 큰 작업이다.** 한때 "시체를 유치장까지 운반해야 계상"으로 갈 뻔했는데, 그러면 시체가
> `Escorted` 상태를 타야 해서 **사망을 오버레이로 재설계**해야 한다. 그 갈림길의 근거는 §6에 있다.

### `NpcDeath.ServerEnterDead` — 순서가 사양이다

```
① 넉백 비행 중단 (ServerAbortFlight)   착지 처리를 태우면 에이전트를 되살린다
② 기절 오버레이 걷기                    남기면 IsStunned가 참이라 밧줄이 열린다
③ 기상 예약 취소
④ 밧줄 끊기 (ServerClearDrag)          ⚠ 사망 전이보다 앞이어야 한다 (아래)
⑤ FSM → Dead                          에이전트를 끄기 전이어야 이전 상태 Exit이 정리한다
⑥ NavMeshAgent OFF                     다시 켜지 않는다
⑦ ArrestJudge.JudgeDeath()             계상
⑧ OnDied 발행                          정리가 끝난 뒤
```

**④가 왜 앞인가:** `PlayerEscorter`의 매 프레임 정리는 커스터디를 벗어난 대상의 연결을 지우지만
`StopRopeDrag`까지는 부르지 않는다. 넉백은 착지 상태가 `Captured`라 목록 안에 남아서 안 걸렸는데,
사망은 목록 밖으로 나가므로 여기서 직접 끊지 않으면 **시체가 끌기 상태로 남아 죽은 뒤에도 장력을
받는다.**

### 사망 이탈은 구조로 막았다

`NpcStateMachine.ChangeState`가 `Dead`에서 나가는 전이를 거부하고 에러를 남긴다. 코어 Update의
사망 게이트만으로는 부족하다 — 오검거·납치 매니저는 게이트를 안 타고 `ChangeState`를 직접 건다.

> **이 에러가 뜨면 버그 신호다:**
> `NpcStateMachine: 죽은 NPC를 X(으)로 되돌리려 했다 — 무시한다`
> 그 시스템이 아직 시체를 붙들고 있다는 뜻이고, `NpcDeath.OnDied`를 구독해 정리해야 한다.

---

## 3. 현상금 집계 — 죽어도 검거로 인정된다

조사에서 나온 결정적 사실: **`JailZone.m_records`가 정산의 진짜 원장이고 유치장 점유
(`m_inmates`)와 별개다.** `TallySettlement`은 레코드만 훑고, 주석에 "NPC 오브젝트가 이미
파괴됐어도 계상된다"고 적혀 있다. → **시체를 옮길 필요가 없다.**

```
사망 → ArrestJudge.JudgeDeath()
      ├ 진범/경범죄 → JailZone.RecordDeceased()   원장에만, 점유에는 안 넣음
      └ 오검거      → WrongfulArrestPenalty.ServerCountWrongfulDeath()
```

**점유에 안 넣는 이유:** `InmateCount`는 유치장 표지판과 **탈옥 이벤트의 발동 전제**가 읽는다.
시체를 세면 없는 사람을 세고, 탈옥이 "풀어 줄 수감자가 있다"고 오판한다.

부수 효과로 **탈옥 방출 대상에서 자동 제외**된다 — `ReleaseAllInmates`가 `Inmates`를 순회하는데
시체는 거기 없고, `ReleaseInmate`를 직접 불러도 `m_inmates.Remove` 실패로 early return이라
**현상금이 취소되지 않는다.** 시체가 달아날 수 없으니 그게 맞다.

### 오검거 사살을 세는 이유

안 세면 **무고한 시민을 죽이는 것이 오검거 페널티를 통째로 회피하는 최적 전략**이 된다. 잡아서
인계하면 게이지가 오르는데 죽이면 아무 일도 안 일어나는 상태였다.

원한 구역 수용은 하지 않는다(시체는 걸어갈 수 없다). 그래서 `"구역 인원 == 팀 카운트"` 불변식이
이 경로에서만 깨지는데, `LaunchSquad`가 이미 그 상황을 받는다(남은 인원만 출동, 비면 광장 매달기
폴백).

### 🚨 병합 전 반드시 되돌릴 것

**`WrongfulArrestPenalty.m_enforcePenalty`의 기본값을 `true`로 되돌려야 한다.**

지금 `false`다 — 래그돌 작업 중 추격대가 몰려오면 시체를 관찰할 수 없고, 오검거한 시민이 원한
구역으로 걸어가 버려 그 NPC로 테스트할 수 없어서 껐다. 씬 체크박스가 아니라 **코드 기본값**으로
끈 것은 PR diff에 보이게 하려는 것이고, `OnNetworkSpawn`에서 라운드마다 경고도 찍는다.

---

## 4. 난이도 노브 자리 (예정)

팀 방향: **"죽어도 집계되는 NPC / 죽으면 집계 안 되는 NPC"로 난이도를 가른다.**

그 분기점은 **`ArrestJudge.JudgeDeath` 진입부 하나**다. 계상 경로를 거기 한 곳으로 모아 둔 이유가
그것이다 — 조건 하나만 세우면 된다.

---

## 5. 리그 (3단계) — 완료된 것과 주의점

### 셋업 스크립트

`PlayerRagdollSetup` → **`RagdollSetup`** 으로 개명·일반화. 메뉴:

```
Tools/Ragdoll/Finish Setup - Player
Tools/Ragdoll/Finish Setup - NPC Citizen
```

프리팹 경로 하드코딩을 걷고 인자 둘을 받는다: `Run(prefabPath, rigOwnerPath)`.

**`rigOwnerPath`가 일반화의 핵심이다.** `RagdollRig`는 `transform.Find("Root")`로 **직속 자식**에서
리그를 찾는데 그 자리가 개체마다 다르다:

| | 리그 소유자 | 이유 |
|---|---|---|
| Player | `""` (프리팹 루트) | 리그가 프리팹에 풀려 있다 |
| NPC | `"Model"` | 몸이 Synty 프리팹의 **중첩 인스턴스**이고 리그가 그 안에 있다 |

`EnsureRig()`가 방금 검증한 그 오브젝트에 `RagdollRig`를 붙인다(멱등). 손으로 붙이면 자리를
틀리기 쉽고, 틀려도 조용히 실패한다(`Find("Root")`가 null → 런타임 에러 한 줄).

### 위저드 절차

Synty 리그는 **Humanoid**라 Ragdoll Builder의 **Auto-Fill**이 13칸을 자동으로 채운다.
`Total Mass = 70`, `Flip Forward Axis` 끔.

⚠ **`Middle Spine`이 `Spine_02`인지 확인할 것** — Auto-Fill이 `Spine_01`(휴머노이드 Spine)을
집을 수 있다. 플레이어는 `Spine_02`다.

⚠ 발(`Ankle_*`)에는 아무것도 안 생기는 게 정상이다 — 위저드가 방향·바운즈 계산에만 쓴다.
**13칸을 채우면 뼈 11개가 나온다.**

### `NPC_Citizen` 결과 (플레이어와 동일)

```
Hips        10.94kg  Box      관절 없음 = 래그돌 루트
Spine_02    10.94kg  Box
Head         4.38kg  Sphere
UpperLeg_L/R 6.56kg  Capsule
LowerLeg_L/R 6.56kg  Capsule
Shoulder_L/R 4.38kg  Capsule
Elbow_L/R    4.38kg  Capsule
                     합계 70.00kg · 뼈 11개 · 관절 10개
```

전부 `layer 10 (Ragdoll)` · `isKinematic=1` · `interpolation=None` ·
`collisionDetection=ContinuousSpeculative`. 루트의 기존 Rigidbody(1kg)는 손대지 않았다 —
관절 기준 수집이 걸러 준다.

### NPC 쪽이 플레이어보다 쉬운 점

플레이어에서 가장 손이 많이 갔던 **자기 캡슐과의 충돌 무시(`IgnoreCollision` 3곳 재적용)가
불필요하다.** 플레이어 캡슐은 Default(지형과 같은 레이어)라 매트릭스로 못 갈랐지만,
**NPC 루트는 Interactable(7)이라 `Ragdoll`(Default만 충돌) 매트릭스가 이미 갈라 준다.**

---

## 6. 갈림길 기록 — 되돌리려면 읽을 것

### 죽은 NPC를 운반해야 하나?

**아니다 (자동 계상).** 한때 "유치장까지 끌고 가야 계상"으로 정했다가 되돌렸다. 그 방향의 대가:

1. **사망을 오버레이로 재설계해야 한다** — 밧줄 끌기가 `Escorted` 상태를 전제로 돌아가는데
   지금은 사망 이탈이 막혀 있다. 예외를 파면 4군데 이상으로 번진다.
2. **`RagdollRope`에 없는 기능 넷을 새로 넣어야 한다** — 줄다리기 합류(앵커가 1개뿐)·부채꼴
   배치·목줄/끊김 거리·무게 페널티. 플레이어 운반이 1:1이라 필요가 없었던 것들이다.
3. **난이도 축이 약해진다** — 산 놈이나 죽은 놈이나 어차피 끌고 가야 하므로.

### 넉다운 구간에도 래그돌을 입히나?

**나중에.** 지금은 사망만 래그돌이고 넉다운은 기존 모션 그대로다.

넉다운에 래그돌을 입히면 **부활 블렌드가 돌아온다**(다시 일어나야 하므로). 그래서 4단계에서
**블렌드 경로를 쓰지 않되 지우지도 않는다.**

셋 중 하나를 고르게 된다:
- **A. 사망만** (현재)
- **B. 쓰러지는 순간만 래그돌** — 정착하면 곧바로 애니메이터 누운 포즈로 복귀. `RagdollRope` 불필요,
  `NpcRopeDrag` 안 건드림. **비용 대비 효과가 가장 좋다**
- **C. 넉다운 구간 내내 래그돌** — 위 "운반" 항목의 2번과 같은 밧줄 작업이 붙는다. 별도 이슈급

---

## 7. 4단계 — `NpcRagdoll` (작성됨, 미검증)

`Assets/Scripts/NPC/View/NpcRagdoll.cs`. `NPC_Citizen` 프리팹에 부착됨.
**`RagdollRig`·`RagdollRope`는 한 줄도 안 고쳤다** — 갈리는 것은 "누가 위치를 쥐나"뿐이다.

| | PlayerRagdoll | NpcRagdoll |
|---|---|---|
| 이동 프록시 | CharacterController (끄고 위치 대입) | **NavMeshAgent** (끄고, **되살리지 않는다**) |
| 위치 권위 | **오너** 권한 NetworkTransform | **서버** 권한 → 서버 외 전원이 원격 |
| 진입 트리거 | `PlayerIncapacitation.IsDead` 폴링 | `NpcDeath.IsDead` 폴링 |
| 임펄스 | 폭발이 ClientRpc로 | **지금은 0** (그 자리에 무너짐) |
| 이탈(부활) | 정착 포즈 → 기상 블렌드 | **없다** |

```
서버:  NavMeshAgent OFF → 뼈 물리 ON → 매 프레임 transform.position = Hips.position
       → 서버 권위 NetworkTransform이 전 클라에 복제
클라:  로컬 물리로 같은 포즈를 만들고, 스트리밍된 루트로 뼈를 당겨온다(TickAlignBonesToRoot)
```

### 작성하며 드러난 NPC 고유 함정 셋

1. **루트 추종을 이 컴포넌트가 직접 돌린다.** 플레이어는 `PlayerMovement.Update`가 불러 주지만,
   `NpcController.Update`는 클라에서 즉시 return하고 서버에서도 사망 게이트에서 끊겨 **부를 자리가
   없다.** 그래서 `NpcRagdoll`이 자기 `Update`를 갖는 `MonoBehaviour`다.
2. **리그를 `GetComponentInChildren`으로 찾는다.** 몸이 중첩 Synty 프리팹이라 `RagdollRig`가
   `Model`에 붙는다 — `GetComponent`로 찾으면 **항상 null**이다.
3. **`CapsuleBottomOffset` 보정이 없다.** 그건 CharacterController 캡슐 밑면이 루트 원점보다 위에
   있어서 필요했던 것이고, NPC 루트 원점은 발밑이라 지면 점이 곧 루트다.

### 부활이 없어 빠진 것

`m_skipThisEpisode`만 남기고 `m_sawDeathThisEpisode`·`k_deathSyncGraceSeconds`는 넣지 않았다 —
**부활 오인 문제(`docs/506-explosion-ragdoll.md` §9-19)가 통째로 사라진다.** 그 버그는 "살아 있는데
래그돌이면 부활"이라는 전제에서 나왔는데, 되살아나지 않으면 전제 자체가 없다.

**yaw 추종(`FollowBodyYaw`)도 뺐다.** 플레이어는 기상 클립이 "루트 전방을 향해 누워 있다"를
전제해 필요했지만 시체는 일어나지 않는다. 넣으면 오히려 손해다 — 리지드바디 없는 뼈(Neck·손·발)만
계층을 따라 돌아 목이 비틀린다.

단 **넉다운 래그돌 여지 때문에 블렌드 경로는 `RagdollRig`에 그대로 남아 있다**(§6) —
`NpcRagdoll`이 안 쓸 뿐이다.

### 4-1. Play 확인 — 다음 작업

임펄스가 0이라 **정착이 거의 즉시 온다.** 그래서 첫 확인이 쉽다.

**오프라인 단독 (세션 없이 Play)**

1. 시민을 진압봉 3대 → **그 자리에 자연스럽게 무너지는가**
2. **시체가 화면에서 사라지지 않는가** — 안 보이면 `SetSkinsAlwaysVisible`. 래그돌의 고전적
   함정이고, `Model` 아래 SkinnedMeshRenderer가 20개(외형 카탈로그)라 여기서 갈릴 수 있다
3. **정착 순간 몸이 튀지 않는가** — 튀면 `Settle()`의 5단계 순서
4. 콘솔에 NavMeshAgent 관련 에러가 없는가
5. 시체가 지면을 뚫거나 공중에 뜨지 않는가 — `m_groundProbeDistance`

**멀티 (MPPM)** — ⚠ 가상 플레이어 콘솔은 안 읽히니 호스트 콘솔 + 화면 관찰로 판단할 것

6. 호스트가 죽인 시체가 클라 화면에서 **같은 자리·같은 자세**인가
7. 클라가 죽인 경우도 마찬가지인가 (판정은 서버가 한다)

막히면 `NpcRagdoll`의 상태 전이(`Animated → Ragdoll → Settled`)와 `IsReadyToSettle`의 두 조건에
로그를 심어 어디서 멈췄는지부터 좁힐 것.

### 알아둘 것

- `Model` 아래에 SkinnedMeshRenderer가 **20개**(외형 카탈로그, 하나만 활성)다.
  `RagdollRig.CollectSkins`가 전부 잡는다 — 동작엔 문제 없지만 예상과 다르면 여기다.
- 시체가 누적되면 뼈 11개 × N구가 물리에 남는다. 정착 후 재우는 것은 **실측하고 필요하면** 넣을 것.
- 다른 NPC가 시체를 밟고 지나간다(`Ragdoll` ↔ `Interactable` 충돌 없음 + NavMeshAgent는 콜라이더를
  안 본다). 플레이어 캡슐은 Default라 시체를 밀 수 있다. 실제로 보고 판단할 항목.

---

## 8. 남은 프리팹 (5단계)

`NPC_Citizen`만 리그가 있다. 4단계가 검증되면 나머지에 복제한다.

| 프리팹 | Model 소스 | 비고 |
|---|---|---|
| `NPC_Citizen` | `SM_Chr_CyberPunk_Male_01` | ✅ 리그 + `RagdollRig` + `NpcRagdoll` |
| `NPC_Abductor` | (Citizen의 **Variant**) | 상속 — 별도 작업 불필요할 것 |
| `NPC_Rioter` | `SM_Chr_CyberPunk_Male_01` | 같은 모델 |
| `NPC_Streaker` | **`SM_Gen_Chr_Underwear_Male_01`** | **다른 모델** — 비율 확인 필요 |
| `NPC_Citizen_Generic` | 중첩 없음 (unpack됨) | `rigOwnerPath` 확인 필요 |

프리팹마다 **세 가지**가 필요하다: ① 위저드로 리그, ② `Tools > Ragdoll > Finish Setup` (레이어·
물리값 + `RagdollRig` 부착), ③ **`NpcRagdoll` 부착**.

⚠ ③은 셋업 스크립트가 하지 않는다 — `RagdollRig`는 리그 소유자(`Model`)에 붙지만 `NpcRagdoll`은
NPC 루트에 붙어야 해서 자리가 다르고, 프리팹이 NPC인지 플레이어인지도 그 스크립트는 모른다.
메뉴에 대상을 추가할 때 함께 자동화할지 판단할 것.

⚠ 모델이 하나가 아니라서 **"Synty 프리팹의 Variant 하나를 만들어 전부 참조하게" 하는 안은
성립하지 않는다.** 프리팹별로 위저드를 돌려야 한다.

---

## 9. Editor 셋업 현황

전부 반영·저장됨:

- NPC 프리팹 4종에 `NpcDeath` **파일 저장** (`NPC_Abductor`는 Variant라 상속)
  - ⚠ `[RequireComponent]`는 **에디터 메모리에만** 붙인다. 런타임 인스턴스화에는 적용되지 않아
    파일에 없으면 `GetComponent<NpcDeath>()`가 null이 되어 `NpcController.Update`에서 터진다.
    실제로 이 상태를 한 번 밟았고 `git status`에 프리팹 변경이 안 잡혀서 발견했다.
- `NpcCommonConfig.m_knockdownHpRatio = 0.4`
- `ArrestJudge.m_jailZone` → 씬의 `Jail` (자동 탐색에 기대지 않고 명시적으로)
- `WrongfulArrestPenalty.m_enforcePenalty = false` (🚨 병합 전 복구)
- `NPC_Citizen` — 래그돌 리그 + `RagdollRig`(on `Model`) + `NpcRagdoll`(on 루트)
- `ProjectSettings/DynamicsManager.asset` — `Ragdoll` 레이어 충돌 매트릭스

## 10. 테스트 현황

**통과** — 진압봉 2대 넉다운(HP 32 유지) / 3대 사망 / 누운 대상 추가 타격(게이트 제거) /
진범 사망 시 진행도 반영 / 무고한 시민 사살 시 카운트만.

**미검증** — `NpcRagdoll` 전부 (§7의 4-1).

**진압봉이 NPC의 유일한 피해원이다** — 폭발은 NPC에 넉백만 준다(`BombDevice`에 "NPC 폭발 피해는
아직 연결하지 않았다" 주석). 그래서 테스트가 결정적이다.

---

## 11. 병합 전 체크리스트

- [ ] **`WrongfulArrestPenalty.m_enforcePenalty` 기본값을 `true`로 되돌린다** (§3)
- [ ] `NpcRagdoll` Play 검증 (§7 4-1)
- [ ] 나머지 NPC 프리팹 4종에 리그·`NpcRagdoll` (§8)
- [ ] 임계 비율(0.4)과 진압봉 데미지(34)를 팀과 확정 (§1 — 둘은 짝이다)
