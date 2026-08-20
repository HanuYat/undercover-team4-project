# 플레이어 래그돌을 NPC 구조로 (#763, 계획)

> 브랜치: `feature/759-player-ragdoll` · **1단계 = 권위(§0~§7) · 2단계 = 모델 단일화(§8)**
>
> 선행 문서(읽는 순서): [npc-ragdoll.md](npc-ragdoll.md) §2(정착은 상태가 아니다) →
> [728-player-pose-streaming.md](728-player-pose-streaming.md) §1·§4 →
> [ragdoll-corpse-split.md](ragdoll-corpse-split.md)(2단계에서 뒤집을 구조)
>
> 이 문서는 **계획서**다. 진행하며 실측이 붙으면 여기에 덧쓴다.

---

## 0. 한 줄

**죽은 플레이어를 NPC와 같은 것으로 만든다** — 사망 중에는 그 `NetworkObject`의 소유권을 서버가
가진다. 스트리머의 `m_authority`도, 루트 `NetworkTransform`의 `AuthorityMode`도 건드리지 않는다.
둘 다 **오너**를 가리키고 있고, 그 오너가 서버가 되기 때문이다.

---

## 1. 왜 이 방향인가 — 지금까지 측정으로 배제·확정된 것

| # | 가설 | 상태 | 근거 |
|---|---|---|---|
| ③ | 권위 피어의 물리가 실시간보다 느리다(프레임 클램프) | **배제** | 전 피어 `비율=1.00` · `클램프 없음` · 55~98fps (#759 코멘트) |
| ① | 도착 지터가 재생 늘어짐이 된다 | **배제** | `패킷 125/125` · `평균간격 40ms`(기대치와 동일) · `재생배율 1.00` · `홀드 0%` |
| — | 오너→서버→전원 프록시 RPC 경로 | **결백** | 유실 0. [728 §4 함정 1](728-player-pose-streaming.md)이 "유일한 미지수"로 남긴 홉이다 |
| ② | 밧줄 장력이 **시체 오너의 PC**에서 계산돼 왕복이 두 배 | **확정에 가까움** | 로그의 `[운반] 종료(대상을 놓침 — 너무 멀어짐)` 3회가 전부 `권한=False` 구간. 사용자 관측 "내 시체는 정상, 남의 시체는 무겁고 줄이 끊긴다"와 일치 |
| ?? | 원격에서 적용된 자세가 덮인다 | **미해결** | 패킷이 정상 도착(재생배율 1.00)하는데 관측자의 골반 이동량이 권위보다 작다. **1단계로 안 닫힐 수 있다** — §6 |

②의 구조가 이렇다:

```
끄는 사람(클라 B)이 걷는다 → (서버) → 시체 오너(클라 A)가 장력 계산 → (서버) → B가 자세를 받는다
```

**B의 화면에서 자기 걸음에 대한 반응이 4홉 뒤에 온다.** NPC는 권위가 서버라 이 경로가 절반이고,
빌드 테스트에서 NPC에 같은 증상이 없었다는 사실이 그것과 맞는다.

---

## 2. 설계 — 권위를 enum이 아니라 **소유권**으로 옮긴다

### 2-1. 왜 `m_authority = Server`가 답이 아닌가

스트리머는 **루트 NetworkTransform과 권위가 같아야 한다**고 못박고 있다
(`RagdollPoseStreamer`의 `m_authority` 툴팁: *"어긋나면 몸과 루트가 서로 다른 피어에서 계산돼 시체가
이름표를 두고 떠난다"*). 그런데 루트 NT는 `AuthorityMode: 1`(Owner)이고, 그 값은 **살아있는 이동**이
요구하는 것이다(`PlayerMovement`는 오너에서만 돌고 원격 인스턴스는 `enabled = false`).

그래서 enum만 뒤집으면 몸=서버 / 루트=오너로 갈린다. **소유권을 옮기면 둘 다 서버를 가리킨다** —
고칠 배선이 하나도 없다는 것이 이 안의 핵심이다.

### 2-2. 전이 지점 — 사망·부활의 단일 통로에 건다

서버에서 사망 원인이 바뀌는 자리는 하나다: `PlayerIncapacitation.SetCause`(서버 전용 경로).

| 시점 | 하는 일 |
|---|---|
| `Cause`가 `Die`가 될 때 | `NetworkObject.ChangeOwnership(NetworkManager.ServerClientId)` |
| `Cause`가 `None`으로 풀릴 때(부활·라운드 리셋) | `ChangeOwnership(원래 오너)` — **원래 오너를 기억해 둔다** |

⚠ **원래 오너를 어디에 두는가.** `OwnerClientId`는 이관하는 순간 덮이므로 사망 직전 값을 서버가
따로 들고 있어야 한다. 접속이 끊긴 오너에게 돌려주면 안 되므로 `ConnectedClients`에 아직 있는지
확인한 뒤 반환한다(없으면 서버 소유로 둔다 — §4 함정 4).

### 2-3. 그러면 NPC와 무엇이 같아지나

| | NPC | 플레이어(1단계 뒤) |
|---|---|---|
| 시체 자세를 굴리는 피어 | 서버 | **서버** |
| 루트를 옮기는 피어 | 서버 | **서버**(오너 권한 NT + 오너=서버) |
| 밧줄 장력 계산 | 서버 | **서버** — 왕복 4홉 → 2홉 |
| 정착·깨어남 | `m_settled` + `AllAsleep` 폴링 | **같음** (이미 이식 완료 — `a2444639`·`e87050fe`·`a17b845d`) |
| 모델 | 리그 한 벌 | 두 벌 — **2단계에서 통일** |

---

## 3. 단계 (커밋 단위)

각 단계 끝에 컴파일을 확인한다:

```bash
dotnet build Assembly-CSharp.csproj
```

### A-1. 소유권 이관 배선

- `PlayerIncapacitation`(서버 경로)에 `Die` 진입/해제 훅
- 원래 오너 보관 + 복귀. 이관·복귀는 **서버만** 호출한다(NGO 제약)
- 계측 한 줄: `[소유권] 시체#N 오너X→Y 원인=Die/None`

### A-2. 소유권 변경을 받는 곳 만들기

지금 이 프로젝트에는 `OnGainedOwnership`/`OnLostOwnership`을 다루는 코드가 **한 곳도 없다**(grep 0).
라운드 중에 오너가 뒤집히는 것이 처음이므로, §4의 감사에서 "즉시 뒤집힘"으로 분류된 곳에 대응을 넣는다.

### A-3. 밧줄·운반 경로 확인

`PlayerTowedMotion.BeginDraggedFollow`는 `BeginDraggedRpc`로 **전 피어에서 돈다**(스택으로 확인).
경로 선택은 `m_ragdoll.IsRagdollActive`라는 지역 판정이고, 소유권이 서버로 간 뒤 `HasMoveAuthority`가
서버에서 참이 되므로 `RagdollRope.Attach`도 서버에서 걸린다 — **여기가 ②를 닫는 지점이다.**

### A-4. 계측 정리

권위가 바뀌면 `[밧줄추적]`의 `권한=True` 줄이 **호스트에서만** 나온다. 그 자체가 이관이 먹었다는
증거이므로 검증 전까지 계측은 켜 둔다.

---

## 4. 함정 — `IsOwner` 감사 (2026-08-20, `Assets/Scripts/Player`)

**분류 기준:** 스폰 1회(`OnNetworkSpawn`)에서 읽는 값은 이관해도 **안 뒤집힌다**. 매 호출마다 읽는
값은 **이관 순간 뒤집힌다**.

### 뒤집히지 않는다 — 손댈 필요 없음

| 위치 | 시점 | 사망 중 뜻 |
|---|---|---|
| `PlayerMovement:223` `ApplyOwnerView(IsOwner)` | 스폰 1회 | **1인칭 시점이 유지된다**(카메라가 남에게 안 넘어감) |
| `PlayerInputHandler:244·281` | 스폰/디스폰 1회 | 입력 구독은 그대로 |
| `PlayerHpUI:51` · `PlayerNameTag:86` · `PlayerReviveHud:51·64` · `PlayerReviver:56·65` | 스폰 1회 | HUD 유지 |

### 즉시 뒤집힌다 — 확인 대상

| 위치 | 사망 중 의미 | 조치 |
|---|---|---|
| `PlayerLooter:333·368` (`IsServer && !IsOwner`) | **약탈은 시체를 대상으로 한다(#487)** — 오너가 서버가 되면 이 분기가 반대로 간다. **1순위** | 대상의 오너가 아니라 **약탈하는 쪽**을 기준으로 묻도록 고친다 |
| `PlayerCarrier:381` (`IsServer && !IsOwner`) | 운반 피드백 RPC 대상 판정 | 위와 같은 계열 |
| `PlayerHeadLook:65·87` (Update/LateUpdate) | `!IsOwner`면 시선을 동기화값으로 읽는다 | 사망 중엔 관전 카메라라 무해할 가능성이 높다 — **실측으로 확인** |
| `PlayerMovement:454·485`, `PlayerCrouch:62·101·111`, `PlayerJump:48·57·65·103` | 이동·앉기·점프 | 사망 중 무의미. 부활 시 소유권이 돌아온 **뒤에** 입력이 살아나는 순서만 확인 |
| `PlayerInputHandler:153·203` (`SetSuspended` 등) | `!IsOwner`면 무동작 → **사망 중 입력 정지/재개 지시가 안 먹는다** | 순서 확인(A-2) |
| `PlayerLoadout:174·238·522`, `PlayerHeldItemView:102·223`, `PlayerHandView:210` | 들고 있던 아이템·손 표시 | 시체가 든 물건 표시가 깨지지 않는지 |
| `PlayerEmote` · `PlayerEscortCommands` · `PlayerTeamStatusInput` | 사망 중 입력 없음 | 회귀만 |

### NGO 쪽 함정

1. **`ChangeOwnership`은 서버만 부를 수 있다.** 클라에서 부르면 예외다.
2. **플레이어 오브젝트의 소유권 이전은 이 프로젝트에서 처음이다.** 아이템은 이미 하고 있다
   (`PlayerLoadout:220`, `PlayerLooter:277`)지만 `PlayerObject`는 다르다 —
   `NetworkManager.ConnectedClients[id].PlayerObject` 연결이 유지되는지 **A-1에서 먼저 확인**한다.
3. **오너가 나가면 NGO가 그 오브젝트를 파괴한다**(`DontDestroyWithOwner=false`). 죽은 채로 나간
   클라의 몸은 **서버 소유**라 파괴되지 않고 남을 수 있다 — [287-client-teardown-fix.md](287-client-teardown-fix.md)와
   충돌하는지 본다.
4. **원래 오너가 없어졌으면 되돌리지 않는다.** 반환 전에 `ConnectedClients`를 확인한다.
5. **오프라인 Play는 회귀 없음** — `!IsSpawned`면 `HasMoveAuthority`가 항상 참이다.

---

## 5. 검증 — MPPM 2인이 최소, **2 PC 빌드가 본 검증**

②가 지연에서 나오는 증상이므로 로컬 단독으로는 못 본다.

| # | 항목 | 기준 |
|---|---|---|
| 1 | 남의 시체를 밧줄로 끌기 | 무겁지 않다. `[운반] 종료(대상을 놓침)`이 안 뜬다 — **②의 판정** |
| 2 | 같은 `시체#N`의 `[밧줄추적]`을 두 PC에서 비교 | `골반이동`·`최고속도`가 양쪽에서 비슷하다 |
| 3 | `[밧줄추적] 권한=True`가 나오는 PC | **항상 호스트** — 이관이 먹었다는 직접 증거 |
| 4 | 낙하 곡선 | `권한=True`/`False`의 `낙하곡선`이 같은 모양 |
| 5 | 부활 | 소유권이 돌아오고 입력·이동·카메라가 정상 |
| 6 | 약탈(#487) | 시체를 약탈하는 창이 정상적으로 열린다 — §4 1순위 항목 |
| 7 | 운반 중 라운드 종료·씬 전환 | 회귀 없음 |
| 8 | 죽은 채로 접속 종료 | 몸이 남지 않는다(§4 함정 3) |
| 9 | 오프라인 Play | 회귀 없음 |

로그 수집(빌드):

```bash
grep -E "소유권|밧줄|자세도착|낙하속도|운반경로" "$USERPROFILE/AppData/LocalLow/DefaultCompany/undercover-team4-project/Player.log"
```

---

## 6. ⚠ 1단계로 안 닫힐 수 있는 것

**"원격에서 적용된 자세가 덮인다"**는 미해결 항목이다. 패킷이 40ms마다 정상 도착하는데(재생배율
1.00) 관측자의 골반 이동량이 권위보다 작았다. 그 경로는 **non-authority 피어에서 도는 코드**라
권위가 어디로 가든 그대로 돈다.

따라서 1단계 뒤에도 남으면 용의자는 둘이다:

- 리그 두 벌 구조에서 오는 것(→ **2단계 모델 단일화**가 지운다)
- 루트 NT 보간과 자세 적용의 순서(→ 적용 시점을 옮겨 보는 별개 수정)

---

## 7. 되돌리기

1단계는 **커밋 되돌리기 한 번**이면 원상복구된다 — 프리팹 값도, 스트리머 enum도 안 건드리기
때문이다. 회귀가 나오면 그대로 되돌리고 2단계로 간다.

---

## 8. 2단계 — 모델 단일화 (리그 한 벌로)

> **왜 하는가.** 1단계와 목적이 다르다. 이쪽은 **활성화 시 자세를 일치시키는 로직(`CopyPose`)을
> 지우는 것**이 목적이고, 그 다음이 "NPC와 같은 모양이면 버그를 고치기 쉽다"는 것이다.
> 커스터마이징에서 이미 한 번 물렸다 — `04e341eb`("죽었을 때 시체가 원래 색으로 돌아오던 것을
> 고친다", #432): 시체 모델의 렌더러가 평시에 꺼져 있어 밑색 칠하기가 건너뛰었다. **색은 전
> 렌더러를 순회해 덮을 수 있었지만, 파츠·메시가 갈리는 커스터마이징은 그 방법이 없다.**

### 8-1. 지금 구조와 목표

```
지금                                      목표 (NPC와 같은 모양)
Player                                    Player
├── Root                (살아있는 리그)    ├── Root      ← 애니메이터와 물리가 번갈아 쥔다
├── SM_Gen_Chr_Robot_01 (살아있는 스킨)    └── SM_Gen_Chr_Robot_01
└── Corpse
    ├── Root            (시체 리그)
    └── SM_Gen_Chr_Robot_01
```

`RagdollSetup.RunPlayer()`가 이미 `rigOwnerPath: ""`(루트)를 넘기고 있고, 살아있는 `Root`가 Player
루트의 직속 자식이므로 **단일화하면 그 값이 그대로 맞는다.** 그 함수 주석이 "별도 작업으로 남긴다"고
적어 둔 그 작업이 이것이다.

### 8-2. 단계

| # | 누가 | 작업 |
|---|---|---|
| **B-1** | 코드 | `RagdollRigCloner`에 **플레이어 메뉴** 추가 — `Corpse/Root`의 완성된 리그를 같은 프리팹의 살아있는 `Root`로 복제. 뼈대가 같다는 전제를 **뼈마다 좌표로 검증**하고 하나라도 어긋나면 아무것도 고치지 않는다(그 도구의 기존 안전장치) |
| **B-2** | 프리팹(에디터) | ① B-1 메뉴 실행 ② `Tools > Ragdoll > Finish Setup - Player` 실행(레이어·충돌 매트릭스·`isKinematic`·보간·관절 preprocessing) ③ `RagdollRig`·`RagdollRope`를 **Player 루트**로 옮김 ④ `Corpse` 삭제 |
| **B-3** | 코드 | `PlayerRagdoll`에서 두 모델 배선 제거 — `ShowCorpse`/`HideCorpse`/`SetCorpseVisible`/`CopyPose`/`m_corpse`/`m_liveSkin`/`m_liveBoneRoot`/`m_expectedPoseBones`. 대신 NPC와 같은 `StopAnimator`(진입) + `RestoreBindPose`(이탈) |
| **B-4** | 코드 | 부활 블렌드를 같은 리그로 — `RagdollPoseBlend(m_rig.BoneRoot)`. 순서는 NPC의 `ExitRagdoll` 그대로: `StopStreaming` → `SetKinematic(true)` → `blend.Begin()` → `RestoreBindPose()` → 애니메이터 켜기 |

### 8-3. 새로 필요해지는 것 — 뼈 길이 복원

지금은 시체 리그가 따로여서 **살아있는 뼈가 물리에 늘어나지 않았다.** 리그가 한 벌이 되면
애니메이터는 회전만 쓰므로 물리가 바꿔 놓은 `localPosition`이 남고, 기절·부활을 반복하면
**사지가 늘어나며 바닥을 뚫는다**(NPC가 이미 밟은 것 — `NpcRagdoll.ExitRagdoll`의 `RestoreBindPose`
주석). `RagdollRig.RestoreBindPose()`가 그 짝이고 이탈 경로에 반드시 넣는다.

### 8-4. 함정

1. ⚠ **1인칭 팔(FPArm) 리그가 뼈 이름이 같은 복사본이다** — `Camera/FPArm_Right/Root/...`에
   `Hips`·`Spine_02`·`Shoulder_R`가 그대로 있다. 이름 탐색 범위를 **루트 직속**으로 좁혀야 하고,
   `RagdollRig`가 이미 그렇게 하고 있다(그 툴팁의 실측: 안 좁히면 사망 시 1인칭 팔이 물리로 풀려
   바닥에 떨어진다). `Corpse`가 사라지면 직속 `Root`가 하나뿐이라 오히려 안전해진다.
2. **평시에 애니메이터와 물리가 같은 리그를 쥔다** — 그래서 `isKinematic = true` 초기화와
   진입 시 `StopAnimator`가 필수다. 순서가 뒤집히면 애니메이터가 매 프레임 물리 결과를 덮는다.
3. **캡슐 무시(`IgnoreOwnCapsule`)의 시점이 바뀐다** — 지금은 "시체를 켜는 순간"이 유일한 시점이지만
   (비활성 콜라이더에 `Physics.IgnoreCollision`이 에러다), 단일 리그에서는 뼈 콜라이더가 항상 활성이라
   스폰에서 한 번 걸면 된다. 껐다 켜는 경로에서 다시 거는 처리(`RefreshCapsuleIgnoreOnReenable`)는 남는다.
4. **`BodyTint`의 "꺼진 렌더러도 칠한다"(#432)** — 단일화 뒤에는 필요 없어지지만, NPC 시체도 같은
   함수를 쓰는지 확인하고 지운다.
5. **스트리머는 안 건드린다** — `GetComponentInChildren<RagdollRig>(true)`가 새 위치도 그대로 집는다.

### 8-5. 검증

| # | 항목 | 기준 |
|---|---|---|
| 1 | 사망 | 죽는 순간 자세가 튀지 않는다 — **`CopyPose`가 없어도** 같은 몸이 그대로 무너지므로 원리적으로 이음새가 없다 |
| 2 | 부활 → 재사망 3회 | 사지가 늘어나지 않는다(8-3) |
| 3 | 1인칭 팔 | 사망·부활에서 팔이 떨어지거나 굳지 않는다 |
| 4 | 색 커스터마이징 | 시체가 자기 색으로 보인다 — 이제 **같은 스킨**이라 자동 |
| 5 | 원격 화면 | 자세 스트리밍 회귀 없음 |
| 6 | 약탈·이름표·조준 히트박스 | 회귀 없음 |
