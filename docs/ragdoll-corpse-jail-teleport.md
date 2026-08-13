# 시체 유치장 순간이동 — 클라이언트에서 바닥에 박히는 문제

> 브랜치: `feature/ragdoll-hotfix`
>
> **증상:** 죽은 NPC(래그돌 상태)를 감옥에 수감하면 **클라이언트 화면에서만** 시체가 바닥에 박힌다.
> 호스트에서는 대체로 정상이다.
>
> 이 문서는 **원인 진단 + 수정 계획**이다. 선행 문서는 [571-npc-death-ragdoll.md](571-npc-death-ragdoll.md),
> [572-npc-ragdoll-plan.md](572-npc-ragdoll-plan.md), [ragdoll-corpse-split.md](ragdoll-corpse-split.md)이고,
> 이 버그는 **#571과 #572 사이에 생긴 전제 불일치**라 그 둘을 함께 봐야 한다.

---

## 0. 결론 — #572가 원격의 얼림을 없앴는데, #571의 순간이동이 그 얼림에 기대고 있다

`NpcRagdoll.ServerPlaceCorpse`(#571)의 설계 근거는 **"얼리고 옮기면 원격에 따로 보낼 것이 없다"**였다.
문서 주석([NpcRagdoll.cs:369-371](../Assets/Scripts/NPC/View/NpcRagdoll.cs#L369))이 그대로 적고 있다:

> 얼린 뼈는 루트의 키네마틱 자식이라 루트를 옮기면 딸려 오고, 그 루트는 NetworkTransform이 이미
> 복제하고 있다 — 그래서 **원격에 따로 보낼 것이 없다.**

이 전제는 **원격도 얼어 있을 때만** 성립한다. 그런데 #572가 원격을 **"절대 얼리지 않는다"**로 바꿨고
([NpcRagdoll.cs:338-349](../Assets/Scripts/NPC/View/NpcRagdoll.cs#L338) 조기 return), 위 주석은 갱신되지 않았다.

같은 파일이 반대 방향으로 같은 성질을 이미 적어 두고 있다
([NpcRagdoll.cs:379-381](../Assets/Scripts/NPC/View/NpcRagdoll.cs#L379)):

> **동적 리지드바디는 부모 트랜스폼을 따라가지 않아** 녹인 채 루트만 옮기면 **루트만 가고 몸은 남는다.**

**그게 클라이언트의 상시 상태다.** 즉 이 문장은 "그러니 얼려야 한다"는 경고인데, 클라이언트는 얼 수
없으므로 경고가 그대로 실현된다.

---

## 1. 배제한 것 — 좌표와 지면 마스크는 건강하다

Unity MCP로 `Map_Cyberpunk`에서 실제 배치 함수를 300회 샘플링해 측정했다.

| 항목 | 값 |
|---|---|
| `JailZone.RandomRestPointInRoom()` 결과 y | **0.261 고정** (300/300) |
| 감옥 바닥(`Floor_*`) y | 0.250 |
| 여유 | **+1.1cm** (바닥 위) |
| 방 부피 밑면 y | −0.200 (기존 실측과 일치) |
| Default 마스크 레이캐스트 실패 | **0회** |
| 바닥 아래로 나온 배치점 | **0회** |
| 잡힌 바닥 콜라이더 | `Floor_0_0`~`Floor_2_2` 9개 전부 |

`NpcRagdoll`의 지면 탐색 배선도 정상이다 — `m_groundMask` = `m_Bits: 1`(Default), 감옥 바닥이 Default
레이어다. `m_groundProbeDistance` = 1.5.

**따라서 다음은 원인이 아니다:**

- ❌ `RandomRestPointInRoom`이 바닥 속 좌표를 준다 (`RandomPointInRoom`과 혼동한 경우 — 그건 #571이 이미 갈라 놓았다)
- ❌ `m_groundMask`가 감옥 바닥을 놓친다
- ❌ 방 부피 밑면(−0.200)이 그대로 쓰인다

---

## 2. ⚠ 막다른 길 — `Warp`를 NetworkTransform 이동으로 바꾸는 것 (하지 말 것)

처음 세운 가설은 "`NpcJailedState.Enter()`의 `NavMeshAgent.Warp`가 문제이니 `transform` 직접 이동 +
NetworkTransform 복제로 바꾼다"였다. **이 경로는 시체에서 한 번도 실행되지 않는다.**

```
JailIntake.ServerAdmitHeldBy
 └ npc.Death.IsDead → ServerAdmitCorpse            ← 산 신병과 여기서 갈린다
    ├ ArrestJudge.JudgeCorpse
    ├ PlayerEscorter.ReleaseAllTethersOnCorpse
    ├ JailZone.RandomRestPointInRoom()             ← 좌표 (측정 결과 정상)
    └ NpcCustody.SendCorpseToJail
       └ NpcCustody.ServerMoveCorpse
          └ NpcRagdoll.ServerPlaceCorpse           ← 이미 transform.position 직접 대입
```

죽은 NPC는 `NpcState.Jailed`로 **전이하지 않는다** — `NpcStateMachine`이 `Dead` 이탈을 거부한다. 그래서
`NpcJailedState.Enter()`도, 그 안의 `Warp`도 시체에서는 돌지 않는다.

**부수적으로 확인한 것:** 산 신병 경로의 `Warp`는 그대로 두는 것이 맞다. `Warp`는 위치 이동 + **NavMesh
재부착**이고, 감옥은 도시와 경로가 없는 별도 NavMesh 섬이라 재부착이 필수다. `transform.position` 직접
대입으로 바꾸면 (a) 에이전트가 다음 프레임에 transform을 문 앞으로 되돌리거나, (b) 에이전트가 옛 섬에
매핑된 채 남아 `NpcJailedState.Tick`의 감옥 배회가 영영 돌지 않는다.

---

## 3. 원인 — 피어별로 시체 구성이 갈린다

### 3-1. 클라이언트는 얼지 않고, 서버 자세도 버린다

[NpcRagdoll.cs:338-349](../Assets/Scripts/NPC/View/NpcRagdoll.cs#L338) — `ApplyFrozenPose`는
`m_hipsIsNetworkSynced && !HasMoveAuthority`이면 **`Freeze()`도 `ApplyLocalPose()`도 부르지 않고 return**한다.
현재 프리팹 배선(골반에 `NetworkTransform`)이 정확히 이 조건이라, **모든 클라이언트가 이 갈래**다.

이건 #572의 **의도된** 설계다. 그쪽 주석의 실측 근거: 원격에서 얼리면 시체가 "루트 높이 하나에 매달린
조각상"이 되고, 정착 직후 골반 로컬 오프셋이 0.234 → −0.001로 무너져 **최저 뼈가 지면 −0.141까지**
내려갔다. 그래서 원격은 물리로 흡수하게 두는 쪽을 골랐다.

### 3-2. 그 결과 — 골반만 스트림, 나머지는 로컬 물리

[NpcRagdoll.cs:207-213](../Assets/Scripts/NPC/View/NpcRagdoll.cs#L207) `ReleaseBonesToPhysics`:

| | 골반 | 나머지 뼈 ~10개 |
|---|---|---|
| **호스트(권위)** | 이동 중 **키네마틱** | 이동 중 **키네마틱** |
| **클라이언트** | 키네마틱 — 골반 NT 스트림이 구동 | **동적 로컬 물리** (관절로 골반에 매달림) |

프리팹 배선 (`NPC_Citizen.prefab` 외 NPC 4종):

| NetworkTransform | AuthorityMode | 공간 | Interpolate | MaxInterpolationTime |
|---|---|---|---|---|
| 루트 | 0 (서버) | world | 1 | 0.1 |
| 골반 | 0 (서버) | world (`InLocalSpace: 0`) | 1 | 0.1 |

### 3-3. 그래서 순간이동이 클라에서는 순간이동이 아니다

**호스트** — `ServerPlaceCorpse`: 얼림 → 옮김 → 녹임. 뼈가 **전부 키네마틱인 채로** 순간이동하므로 몸이
통째로 도착하고, `Unfreeze()` 뒤 **제자리에서** 정착한 다음 `ServerFreezeInPlace()`가 골반 밑 레이캐스트로
루트를 지면에 다시 맞춘다. → 정상.

**클라이언트** — 얼림 단계가 없다.

1. 골반 NT가 골반을 문 앞 → 감옥으로 **~100ms에 걸쳐 보간해 쓸고 간다** (`Interpolate: 1`,
   `PositionMaxInterpolationTime: 0.1`). 순간이동이 아니라 **벽·바닥을 통과하는 고속 이동**이다.
2. 나머지 10개 뼈는 **동적**이라 순간이동하지 않고 **관절에 끌려간다.** 수십 m를 100ms에 당기면 PhysX가
   팔다리를 거대한 속도로 채고, 그 속도로 도착해 **얇은 실내 바닥을 뚫고 내려간다**
   (같은 파일이 이미 경고하는 그 얇은 바닥 — [NpcRagdoll.cs:948](../Assets/Scripts/NPC/View/NpcRagdoll.cs#L948)).
3. 바닥 아래로 내려가면 **중력만 남고 밀어 올릴 것이 없다.** 클라에는 정착 판정도, 지면 레이캐스트도,
   루트 재정렬도 없다.
4. 서버가 정착 후 다시 뿌리는 자세도 클라는 **버린다**(3-1의 조기 return).

**한 줄:** 서버는 `얼림 → 이동 → 녹임 → 정착 → 재얼림` 시퀀스를 갖지만, 클라는 **얼림이 없어 이동이
순간이동으로 성립하지 않는다.**

### 3-4. 곁다리 — 구현이 없는 주석

[NpcRagdoll.cs:537-538](../Assets/Scripts/NPC/View/NpcRagdoll.cs#L537)에 "얼린 뒤에는 반대로 골반 스트림을
눌러야 한다"는 주석만 있고 **구현이 없다.** 다만 클라는 `Frozen`에 진입 자체를 안 하므로(3-1) **지금은
죽은 의도**다. 이 수정에서 원격의 얼림을 되살리지 않는 한 건드릴 필요가 없다 — 되살린다면 #572가
측정한 −0.141이 함께 돌아온다.

---

## 4. 수정 계획

방향은 **"루트를 어떻게 옮기냐"가 아니라 "순간이동을 클라에 사실로 알린다"**다. 둘이 같이 필요하다 —
하나만 하면 반쪽이 남는다.

### 4-1. 골반 NT의 보간을 억제한다

`NetworkTransform.Teleport(position, rotation, scale)`는 해당 상태 갱신 1회에 대해 보간을 끄는 NGO
기본기다. NGO **2.13.0**에 있고, **프로젝트 전체에 사용처가 0건**이다.

- 대상은 **루트가 아니라 골반** NT다 — 클라에서 몸의 궤적을 쥔 쪽이 골반이다(#572).
- `Warp` **대체가 아니라 추가**다 (§2).

이것만으로는 부족하다: 골반이 스냅해도 **동적 팔다리는 여전히 관절에 끌려간다.**

### 4-2. 각 피어가 자기 뼈를 직접 옮긴다

순간이동 사실을 RPC로 뿌리고, 각 피어가 **전 뼈를 델타만큼 평행이동 + 속도 0으로 지우기**를 한다.

이건 #571이 없앤 경로다 — `ServerPlaceCorpse` 주석이 "예전에는 뼈를 피어마다 평행이동시키고 정렬을
0.5초 재워야 했다"고 적고 있다. **없앤 근거가 §0의 잘못된 전제였다.** 다만 그때의 "정렬 0.5초"까지
되살릴 필요는 없다 — 정렬(`TickAlignBonesToRoot`)은 골반 복제 이전의 수단이고, 지금은 골반이 궤적을
주므로 **평행이동 + 속도 지우기**만으로 충분한지가 검증 항목이다(§6).

### 4-3. 손댈 곳

| 파일 | 내용 |
|---|---|
| `NpcRagdoll.cs` | 순간이동 진입점을 원격에도 여는 메서드. 골반 NT `Teleport` + 전 뼈 평행이동 + 속도 0 |
| `NpcDeath.cs` | RPC 배선 — `NpcRagdoll`은 `NetworkBehaviour`가 아니라 여기가 창구다(`ServerSendFrozenPose`와 같은 자리) |
| `NpcCustody.cs` | `SendCorpseToJail`/`ServerMoveCorpse` 주석 갱신 — "원격에 보낼 것이 없다"가 뒤집혔다 |
| `RagdollRig.cs` | 전 뼈 평행이동 + 속도 지우기 유틸이 없으면 추가 (`SetKinematic`이 이미 속도를 지우는 자리를 갖고 있으니 그 옆) |

프리팹 배선 변경은 **없을 전망**이다 — 골반 NT가 이미 있고 `Teleport`는 코드 호출이다.

---

## 5. 함정

1. **`ServerPlaceCorpse`의 얼림은 없애면 안 된다.** 서버에서 이동 *중* 얼림은 순간이동의 **수단**이다
   (§0의 379-381행). 원격 처리를 추가하는 것이지 서버 시퀀스를 바꾸는 게 아니다.
2. **원격을 다시 얼리려는 유혹.** #572가 실측(−0.141)으로 되돌린 길이다. 얼리지 말고 **옮기고 속도만
   지운다.**
3. **`ServerExitJail` 경로도 같은 구멍이다.** `JailIntake.ServerExitRopedCorpses` →
   `NpcCustody.ServerMoveCorpse`는 **같은 함수**를 탄다. 퇴장 자리는 이동 거리가 짧아 눈에 덜 띄지만
   원인이 동일하다 — 수정은 `ServerMoveCorpse` 한 곳에 넣으면 둘 다 덮인다.
4. **밧줄.** 수감 경로는 배치 **전에** 관절까지 걷는다(`ReleaseAllTethersOnCorpse`). 퇴장 경로는
   **걷지 않는다** — 줄이 걸린 채 옮긴다. 뼈 평행이동이 관절 장력과 싸우지 않는지 확인할 것.
5. **오프라인 Play.** `IsSpawned == false`면 RPC를 보낼 곳이 없다. 지금 동작(서버 = 유일 피어)이
   깨지지 않게 게이트할 것.

---

## 6. 검증

**Multiplayer Play Mode 2인(호스트 + 클라 1)이 최소 구성이다.** 호스트 단독으로는 재현되지 않는다 —
그게 이 버그의 정의다.

| # | 항목 | 기준 |
|---|---|---|
| 1 | 시체 수감 후 클라 화면 | 몸이 바닥 위에 눕는다 (박히지 않음) |
| 2 | 최저 뼈 y − 바닥 y (클라 계측) | ≥ 0 |
| 3 | 골반 로컬 오프셋 (클라 계측) | 0 근처로 무너지지 않음 (#572 실측 0.234 → −0.001이 실패 신호) |
| 4 | 이동 구간 시각 | 벽·바닥을 통과해 쓸려 가는 그림이 없다 |
| 5 | 밧줄 시체 퇴장(`ServerExitRopedCorpses`) | 함정 3·4 — 같은 기준으로 |
| 6 | 오프라인 Play | 기존 동작 유지 (함정 5) |
| 7 | 산 신병 수감 | 회귀 없음 — `Warp` 경로를 건드리지 않았음의 확인 |

계측은 #572와 같은 방식(`EditorApplication.update` 훅 + 한 줄 `Debug.Log`)으로 붙이고 **통과 후 걷어낸다**.

---

## 7. 남은 불확실성

§3의 **1·2·4는 코드로 확정**했고(조기 return, 동적 뼈, 자세 버림), §1은 **측정으로 배제**했다. 확정하지
않은 것은 **묻히는 최종 메커니즘**이다:

- (a) 팔다리가 고속으로 얇은 바닥을 **터널링**해 관통하는 것인지
- (b) 관절 장력이 골반을 아래로 **끌어내리는** 것인지
- (c) 도착 후 로컬 물리가 바닥 **아래에서 정착**해 버리는 것인지

셋 다 §4의 수정(평행이동 + 속도 0)으로 덮이므로 **수정 착수를 막지는 않는다.** 다만 §6-2·3 계측을
수정 **전에** 한 번 찍어 두면 어느 것인지 갈리고, 수정 후 같은 값을 비교할 기준선이 된다. **권장:
계측 먼저.**

### 이슈 번호

이 문서는 브랜치명(`feature/ragdoll-hotfix`)만 갖고 있어 **이슈 번호가 비어 있다.** 이슈를 열면 파일명을
`<번호>-corpse-jail-teleport.md`로 옮기고 이 절을 지울 것 (docs/ 관례).
