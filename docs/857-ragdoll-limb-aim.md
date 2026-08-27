# #857 — 래그돌 상태 대상의 팔다리 조준이 안 잡힌다

- **날짜:** 2026-08-27
- **브랜치:** `Bug/857-ragdoll-limb-aim`
- **상태:** 코드 수정 완료 (`PlayerIncapacitation.cs` / `PlayerInteractor.cs` / `InteractionFeedback.cs`).
  컴파일 확인 완료(에러 0). **플레이 테스트 필요**(§6) — 술어가 `NetworkVariable` 의존이라
  정적 검증으로는 재현할 수 없다.
- **이슈:** [#857](https://github.com/hyunjin0814/undercover-team4-project/issues/857)

---

## 1. 한 줄 요약

쓰러진 몸의 조준 판정이 **골반 중심의 박스 콜라이더 하나뿐**이라 뻗어나간 팔다리가 판정 밖이었다.
조준 레이를 **2발로 나눠**(기존 `Interactable` 1발 + `Ragdoll` 레이어 전용 보조 1발), 보조 레이가
받은 뼈 중 "지금 조준 대상인 몸의 뼈"만 남기고 더 가까운 쪽을 채택한다.

---

## 2. 원인

| 층 | 실제 값 |
|---|---|
| 조준 히트박스 | `Player.prefab` 루트 직속 `ReviveHitbox` — `BoxCollider` 트리거, `size 1.5×1×1.5`, `center (0, 0.5, 0)`, 레이어 7 `Interactable` |
| 조준 레이 마스크 | `PlayerInteractor.m_interactMask` = **128** (레이어 7 단독) |
| 래그돌 뼈 콜라이더 | 11개, 레이어 **10 `Ragdoll`** |

루트가 골반을 따라가므로 박스도 골반 중심에 있고, 골반에서 **0.75m 넘게 뻗은 팔다리는 박스 밖**이다.
뼈는 레이어 10이라 조준 레이에 **절대 잡히지 않는다**.

**NPC도 같은 병.** 래그돌 NPC의 조준 콜라이더도 루트 캡슐 하나다. `NpcProneCollider`(#363)가 누울 때
캡슐을 눕혀 주지만(r 0.3 / h 1.8 / 로컬 Z), 그 클래스 주석은 *"벌어진 팔까지 덮지 않는 것은 의도"* 라고
못박고 근거로 *"팔까지 넣으면 옆 사람을 겨눠도 이 NPC가 잡힌다"* 를 든다. 즉 #363이 거부한 것은
**뭉툭한 캡슐로 팔을 덮는 것**이지 팔을 겨냥 가능하게 만드는 것 자체가 아니다. 실제 팔 모양인 뼈
콜라이더로 덮으면 그 부작용이 없다.

---

## 3. 수정

### 3-1. `PlayerIncapacitation` — 조준 가능 조건을 프로퍼티로 추출

```csharp
public bool IsAimTargetable => IsOutOfAction && !IsBodyLost;
```

`RefreshAimHitbox`가 인라인으로 쓰던 식 그대로다(동작 변화 없음). **히트박스를 켜는 조건과 보조
레이의 술어가 반드시 같은 값을 봐야** 하므로 한 곳에 모았다 — 갈라지면 "몸통은 잡히는데 팔은
안 잡히는" #857이 방향만 바꿔 되살아난다.

`!IsBodyLost`가 여기 들어가는 것이 **필수**다. 몸 소실(#775/#819)은 `PlayerRagdoll.HideLostBody`가
**렌더러만 끄고** 뼈 콜라이더는 켜진 채 남기기 때문에, 이 게이트가 없으면 보조 레이가 **투명한
시체를 조준**한다.

### 3-2. `PlayerInteractor` — 보조 레이 1발 추가

```
1발 (기존 그대로) : Interactable 레이어  → 문·아이템·NPC 캡슐·시체 박스
2발 (신규)        : Ragdoll 레이어만     → 뼈만
                    └ IsAimableRagdollBone으로 "지금 조준 대상인 몸의 뼈"만 남긴다
→ 두 결과 중 더 가까운 쪽을 채택 → 그 뒤는 기존 코드 그대로
```

- `TryAimRagdollBone` — `RaycastNonAlloc`로 전부 받아 술어로 거른 뒤 최근접을 고른다.
- `IsAimableRagdollBone` — 내 몸 제외 → `PlayerIncapacitation.IsAimTargetable` → `NpcRagdoll.IsRagdollActive`.
- `RagdollAimMask` — 레이어 인덱스를 인스펙터에 굳히지 않고 `RagdollRig.k_layerName`으로 조회해 캐시.
- `RagdollAimRejectReason` — `m_logLineOfSight` 디버그 전용. "마스크 밖"으로만 찍히면 의도적
  제외(살아있는 동료)와 배선 실수를 구분할 수 없어서 사유를 덧붙인다.

**왜 2발인가.** 뼈 콜라이더는 살아있을 때도 항상 켜져 있다. `m_interactMask`에 레이어 10을 그냥
더하면 `Physics.Raycast`가 최근접 하나만 돌려주므로 **상태 술어를 걸 자리가 물리적으로 없고**,
살아있는 동료의 뼈가 그 뒤의 문·아이템 조준을 통째로 가로챈다. 2발로 나누면 "버린 뼈"가 1발
결과에 아무 영향을 주지 못한다.

**왜 이것만으로 전 기능이 되는가.** 쓰러진 몸의 상호작용은 `IInteractable`이 아니라 전부 "조준
대상 되짚기" 패턴이고, 이미 `GetComponentInParent`로 루트를 찾는다:

| 기능 | 되짚는 곳 |
|---|---|
| 구조(E) | `PlayerReviver.FindAllyTarget` → `GetComponentInParent<PlayerHealth>` |
| 약탈(R) | `PlayerLooter.FindLootTarget` → `GetComponentInParent<LootableBodyInteractable>` |
| 밧줄 운반 | `Rope.ResolveCarryTarget` → `GetComponentInParent<PlayerCarrier>` |
| 밧줄 NPC | `Rope.ResolveTarget` → `GetComponentInParent<NpcController>` |
| 부활 키트 | `ReviveKit.ResolveTarget` → `GetComponentInParent<PlayerHealth>` |
| 내려놓기 | `PlayerInteractor` → `CurrentTarget.transform.IsChildOf(carried)` |
| 스캔 | `ScanResultPresenter` → `GetComponentInParent<CitizenIdentity>` |
| 윤곽선·프롬프트 | `InteractionFeedback` → `GetComponentInParent<PlayerIncapacitation>` |

뼈는 플레이어에서 `Player/Root/Hips/...`, NPC에서 `NPC/Model/Root/Hips/...` — 둘 다 루트 하위다.
`Taser.EvaluateAim`은 이미 마스크 `~0`으로 쏴서 뼈를 맞고 `GetComponentInParent<NpcController>()`로
정상 동작한다(선례).

### 3-3. `InteractionFeedback` — 거리 기준을 몸 루트로 되돌린다

`inRange`는 서버 거리 검증(`PlayerInteractor.IsWithinReach`, **루트 기준**)과 같은 공식이어야 한다는
것이 #184의 결론인데, 조준 대상이 자식이 되면 `root`가 몸이 아니라 그 자식이 된다. 오늘은 히트박스
오프셋이 0.5m라 폭이 작아 안 보였지만 뼈로 넓어지면 **골반→손 ≈0.9m**까지 벌어진다.

그래서 `aimedIncap`을 거리 계산보다 **먼저** 해석하고, `rangeAnchor`를 두어 쓰러진 몸이면 몸 루트에서
잰다. 엄격해지는 방향뿐이다.

> **플랜 문서에서 넓힌 부분.** 원안은 ③(동료 몸) 갈래에만 루트 기준을 적용했으나, ①아이템 갈래
> (`itemUsable`)도 같은 `inRange`를 쓴다. 밧줄·부활 키트를 든 채 시체의 뻗은 팔을 겨누면 크로스헤어는
> 켜지는데 서버가 거부하는 #184 계열 불일치가 그쪽으로 새로 생긴다. `inRange` 계산 자체를 올려 세
> 갈래를 한 번에 막았다.

`IsOutOfAction` → `IsAimTargetable`로 바꾼 것도 **필수**다. 오늘은 몸 소실 시 히트박스가 꺼져
무해했지만, 뼈 레이가 들어오면 **투명한 시체에 윤곽선이 켜진다**.

---

## 4. 회귀 방어 — 왜 안 깨지는가

| 위험 | 방어 |
|---|---|
| 살아있는 동료 뼈가 뒤의 문·아이템 조준을 가로챈다 | 보조 레이가 술어에서 걸러 **후보에서 완전히 빠진다**. 게다가 가림 판정은 `m_losBlockMask = 1`(Default)만 보므로 레이어 10은 애초에 그 레이에 안 잡힌다 — "가림으로도 안 친다"가 코드 추가 없이 성립 |
| 밧줄 전원 놓기(#638)가 깨진다 | `aimedInteraction`은 `CurrentInteractable`만 본다. 플레이어 루트에 `IInteractable` 구현이 **하나도 없다**(`LootableBodyInteractable`은 #725에서 인터페이스를 뗐다) → 뼈를 겨눠도 `null` → 오늘 `ReviveHitbox`를 겨눴을 때와 완전히 동일한 경로 |
| 운반 중 내려놓기 판정 | `CurrentTarget.transform.IsChildOf(carried)` — 뼈는 `carried` 루트의 자손이라 그대로 참. **업은 동료의 팔을 겨눠도 내려놓기가 되는 개선** |
| 1인칭 팔 리그(뼈 이름이 몸통과 동일) | 레이어 8 `Viewmodel`이라 마스크 밖 + `IsChildOf(transform)` 자기 제외에도 걸린다 (이중) |
| 내 시체가 조준된다 | `IsChildOf(transform)`로 제외. 사망 시 카메라가 자기 흉곽 안이라 이 제외가 없으면 매 프레임 distance≈0 히트가 난다 |
| `hit.point`가 원점(0,0,0) | `distance <= 0f` 컷 — 원점이 콜라이더 안이면 `hit.point`가 채워지지 않아 가시선이 월드 원점 방향을 잰다 (#853 §5.5에서 실제로 밟은 버그). 그 구간은 히트박스가 덮는다 |
| `AreaScanner` | 마스크에서 Ragdoll 레이어를 **명시적으로 제외**한다(`AreaScanner.HitLayers`) — 영향 없음 |
| `NpcHealthBarPresenter` | `CurrentTarget`을 쓰지 않고 자체 레이캐스트(`m_probeMask = 129` = Default+Interactable) — 영향 없음 |
| 그 외 `CurrentTarget` 소비처 | 전수 확인(`InteractionFeedback`·`PlayerItemUser`·`PlayerLooter`·`PlayerReviver`·`TutorialDirector`·`ScanResultPresenter`·`ShopStandPresenter`) — 전부 `GetComponentInParent`로 루트를 되짚어 안전 |
| 성능 | 오너 1명당 프레임당 얇은 레이 1개 + 히트당 `GetComponentInParent` 1회. 거리 컷을 술어보다 **앞에** 둬서 대부분 조회를 건너뛴다 |

### 깨면 안 되는 불변식 (이 수정은 셋 다 지킨다)

1. 뼈 레이어(Ragdoll 10)는 **런타임에 절대 바뀌지 않는다** — `RagdollRig.Collect`가 레이어를 수집
   필터로 쓰므로, 바꾸면 `m_bodies`가 빈 배열이 되어 래그돌이 통째로 죽는다.
2. 뼈 콜라이더는 살아있을 때도 항상 `enabled = true`다 — 껐다 켜면 자기 캡슐 `IgnoreCollision`이
   초기화된다(`docs/506-explosion-ragdoll.md`).
3. 몸 소실은 렌더러만 끈다 → 뼈 콜라이더가 살아 있으므로 `!IsBodyLost` 게이트가 필수.

---

## 4-1. NPC 쪽에서 달라지는 것

**NPC는 별도 코드 변경이 없다** — 술어에 `NpcRagdoll.IsRagdollActive` 갈래가 들어가는 것이 전부다.
`NpcProneCollider`·`NpcRagdoll.TickRootFollow`·`NpcSubdueInteractable`은 손대지 않았다.

- **래그돌 NPC에 걸리는 상호작용은 이미 있다.** `IsProne`은 `Dead` 외에 밧줄에 묶인 대상
  (`Captured`·`Escorted`)과 기절을 포함하고, `NpcSubdueInteractable.CanInteract`는 `Captured`·`Jailed`·
  "내 줄인 `Escorted`"에서 true다.
- **시체는 밧줄 대상이다** — `NpcStateRules.CanRopeBind`는 `Death.IsDead`면 즉시 true. 뻗은 팔을 겨눠
  밧줄을 걸 수 있게 되는 것이 NPC 쪽 주된 이득.
- **`m_settled` 이후 루트 추종이 멈추는 공백**과 **루트 yaw가 쓰러지기 직전 값에 얼어붙는 공백**
  (`TickRootFollow`는 위치만 대입하는데 누운 캡슐은 로컬 Z축)이 함께 덮인다.
- **서 있는 NPC는 그대로다** — `IsRagdollActive` 게이트로 후보에서 빠진다.
- **바뀌지 않는 것:** 시체를 겨눈 E는 `CanInteract`가 false라 "끌던 대상 전원 놓기"로 흘러간다.
  루트 캡슐이 이미 L7이라 **오늘도 그렇게 동작한다** — 뼈 조준은 이 분기가 트리거되는 화면 면적만
  넓힌다. 플레이 테스트 관찰 항목.

---

## 5. 기각한 대안 (다시 제안하지 말 것)

**`m_interactMask`에 레이어 10을 그냥 더하는 단일 레이.** `Physics.Raycast`는 최근접 하나만
돌려주므로 상태 술어를 걸 자리가 물리적으로 없다. 살아있는 동료의 뼈가 항상 켜져 있어(불변식 2)
동료 뒤의 문·아이템·NPC 조준을 통째로 가로챈다.

**프리팹에 뼈마다 `Interactable` 트리거 자식 11개 추가.** 레이어 7 행은 충돌 매트릭스에서 거의
전부 ON이라 트리거 11개가 살아있는 동안에도 브로드페이즈에 상주하며 애니메이션마다 재삽입된다.
더 나쁜 것은 `RefreshAimHitbox`가 쥔 "조준 가능한가"라는 단일 진실이 12개 오브젝트로 흩어지는 것 —
하나만 안 꺼져도 불변식 3의 "투명한 몸이 조준되는" 버그가 난다.

**런타임 레이어 전환(사망 시 뼈를 Ragdoll → Interactable).** 불변식 1·3을 정면으로 깬다. 레이어
7↔7이 ON이라 옮기는 순간 뼈끼리 자기충돌해 래그돌이 스스로 폭발하고, `RagdollRig.Collect`가
레이어를 수집 필터로 쓰므로 전환 중 재수집이 일어나면 `m_bodies`가 빈 배열이 된다.
`docs/506-explosion-ragdoll.md`가 바로 그 조합들을 의도적으로 OFF로 못박은 문서다.

---

## 6. 상태 · 남은 일

### 완료
- 코드 3파일 수정, `dotnet build Assembly-CSharp.csproj` **에러 0** (경고는 전부 기존 것 —
  서드파티 `Easy performant outline` CS0618 + `SaveService`/`AccessoryCatalog` 등 CS0649).
- ⚠ 빌드 시점에 `Assembly-CSharp.csproj`가 스테일이라 `PlayerKillCredit.cs`가 빠져 있었다
  (`CS0246` 3건). 그 파일을 끼운 임시 csproj로 검증했고 **우리 수정과 무관**하다. Unity를 한 번
  포커스하면 csproj가 재생성된다.

### 플레이 테스트 (사용자만 할 수 있다 — PR 전 반드시 요청)
1. 사지를 벌린 시체의 **팔뚝·정강이·머리**를 겨눠 구조(E)·약탈(R)·밧줄 운반·부활 키트가 잡히는가
2. **살아있는 동료를 겨눴을 때** 반응이 그대로 없고, **그 동료 뒤/옆의 문·아이템 조준이 막히지
   않는가** — 최대 회귀
3. 다운(아직 래그돌 아님, 애니메이션 자세) 동료의 뻗은 팔로 구조 E가 되는가
4. NPC를 끌고 가며 **겨냥 없이 E → 전원 밧줄 풀기**가 그대로인가 (#638)
5. 운반 중 **문 앞에서 E**가 문으로 가는가 (뼈가 문을 이기지 않는가)
6. 맨홀/UFO로 사라진 몸이 있던 자리를 겨눠 **아무 반응이 없는가** (#775/#819)
7. 벽 너머 시체의 뻗은 팔이 벽을 뚫고 잡히지 않는가 (#853 유지)
8. `m_logLineOfSight`를 켜고 `[LOS/조준/뼈]`가 실제 뼈 이름(`Elbow_L` 등)을 찍는가
9. 죽은 NPC의 뻗은 팔/다리를 겨눠 **밧줄이 걸리는가**
10. 밧줄로 눕혀 놓아둔 신병(`Captured`)의 뻗은 팔을 겨눠 E(밧줄 풀기)가 되는가
11. **서 있는/걷는 NPC** 조준이 그대로인가 — 밧줄·테이저·스캔·체력바·윤곽선
12. 굴러서 옆으로 누운 NPC(캡슐 yaw 어긋남)의 몸을 겨눠 잡히는가

### 이번 범위에서 뺀 것 (후속 이슈)

**조준 콜라이더가 허공을 잡는 문제.** `ReviveHitbox`(`1.5×1×1.5`)는 서 있는 자세 기준이라 누운 몸
(두께 0.3m 남짓) 위 **약 0.7m 허공**이 조준된다. NPC 누운 캡슐도 yaw가 어긋나 같은 성격의 허공을
잡는다. 이번 수정은 "안 잡히는" 방향인데 축소는 "잡히던 게 안 잡히는" **반대 방향 회귀**를 새로
만들어, 한 커밋에 섞으면 플레이 테스트에서 원인 분리가 안 된다. 뼈가 덮는 범위가 확정된 **뒤에**
히트박스에 남길 몫을 재는 것이 옳다.
→ 후속 이슈 제목: *"누운 몸 조준 콜라이더가 몸 위 허공을 잡는다 (#857 후속)"*

**손끝·발끝은 이번 수정으로도 안 잡힌다.** 뼈 콜라이더는 플레이어·NPC 모두 **11개**(Hips, Spine_02,
Shoulder_L/R, Elbow_L/R, Head, UpperLeg_L/R, LowerLeg_L/R)뿐이고 `Hand`·`Ankle`·`Ball`·`Toes`·
`Clavicle`·`Neck`·`Spine_01/03`에는 콜라이더가 **아예 없다.** 즉 잡히는 최말단은 **팔뚝(Elbow)·
정강이(LowerLeg)** 까지다. `docs/npc-ragdoll.md`가 이미 같은 결론과 처방("`RagdollSetup`에 발·손
캡슐을 더하는 프리팹 작업")을 적어 뒀다. 리그에 콜라이더를 늘리는 것은 질량 분포·관절이 바뀌는
별건이다. **이슈에 코멘트로 남길 것** — 제보자가 "팔다리 끝"을 손끝/발끝으로 이해했다면 이번
수정으로 완전히 해결되지 않는다.

---

## 7. 참고 문서

| 문서 | 왜 |
|---|---|
| `docs/853-aim-occlusion.md` | 조준·가림 판정의 현행 설계. §5.5에 `hit.point == zero` 버그 |
| `docs/506-explosion-ragdoll.md` | Ragdoll 레이어·충돌 매트릭스의 원 설계 근거 |
| `docs/ragdoll.md` · `docs/ragdoll-rig.md` | 래그돌 구조·불변식의 정본. ⚠ `ragdoll.md`의 `Corpse` 분리 구조 서술은 스테일(현재 프리팹에 `Corpse` 없음, 리그는 `Player/Root` 한 벌) |
| `docs/npc-ragdoll.md` | 발·손 콜라이더 부재와 처방 |
| `docs/player-ragdoll.md` | 캡슐 추종·`IgnoreCollision` 근거 |
