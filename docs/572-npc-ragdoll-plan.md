# NPC 래그돌 재적용 계획 (#572)

> [이슈 #572](https://github.com/hyunjin0814/undercover-team4-project/issues/572) — "NPC에도 사망 래그돌
> 적용, 플레이어 리그(#506) 재사용, NavMesh·서버 권한만 새로."
>
> 이 문서는 **작업 전 계획**이다. NPC 래그돌 코드(`NpcRagdoll.cs`)와 에디터 도구(`RagdollSetup`·
> `RagdollRigCloner`)는 #571 작업 중에 이미 만들어져 있었지만, 플레이어 쪽(#506→#571→corpse-split)에서
> 뒤늦게 잡은 버그 셋을 반영하지 않은 채였다. [PR #599](https://github.com/hyunjin0814/undercover-team4-project/pull/599)에서
> NPC 프리팹의 리그를 일단 떼어냈고(`0bd6990`), 이 문서는 **무엇을 반영해서 다시 붙일지**를 정리한다.

> ⚠ **2026-08-11 갱신 — 범위가 커졌다.** **기절(`NpcStun`)에도 래그돌을 태우고 일정 시간 뒤 NavMesh를
> 복구하기로 정했다.** 그건 곧 **NPC에 부활이 생긴다**는 뜻이고, 아래 §0이 "작업량이 작다"고 판단한
> 근거 두 개가 정확히 *"NPC엔 부활이 없다"*에 기대고 있었다. 뒤집힌 문단은 **지우지 않고 표시만
> 해 둔다** — 어떤 근거가 왜 무효가 됐는지가 다음 판단의 재료다. 새로 생긴 작업은 §2에 있다.

---

## 진행 상황 — 인수인계 (2026-08-11)

> **여기부터 읽을 것.** 아래 §0~§6은 *작업 전* 계획이라 이미 뒤집힌 판단이 섞여 있다.
> 지금 상태의 정본은 이 절이고, 아래는 근거를 찾을 때 보는 참고서다.

작업은 **팀 확정 순서 넷**으로 쪼개 진행 중이다. 각 단계는 플레이테스트를 통과해야 다음으로 넘어간다.

| 단계 | 내용 | 상태 |
|---|---|---|
| 1 | **사망 래그돌** — 프리팹에 리그 재부착 | ✅ **완료 · 오프라인 Play 통과** (`40662d04`) |
| 2 | **시체 밧줄** — 골반 복제 + 권위 전용 | ✅ **완료 · Play 통과** (`d913a15` + 아래 「2단계에서 실제로 밟은 것」) |
| 3 | 기절 래그돌 | ✅ **완료 · Play 통과** (아래 「3단계에서 실제로 밟은 것」) |
| 4 | 기상 시 NavMesh 복구 | ✅ **완료** — 3단계에 포함됐다. §3-4가 진입·이탈을 한 단계로 잡고 있고, 이탈 없이는 플레이테스트 자체가 안 된다(NPC가 영영 안 일어난다) |

### 브랜치는 다시 하나다 (2026-08-12)

`feature/572-hips-replication`이 `feature/ragdoll-npc-forever`의 정확한 상위집합이라 **fast-forward로
합쳤다.** 이제 3·4단계는 `feature/ragdoll-npc-forever`에서 이어가면 되고, "여기서 가면 2단계를
건너뛴다"던 예전 경고는 해소됐다.

### 1단계 — 코드는 한 줄도 안 고쳤다

`0bd6990`(리그를 떼어낸 커밋)이 조상이고 **그 뒤로 NPC 프리팹을 건드린 커밋이 하나도 없어서**,
위저드를 다시 돌리지 않고 git에서 그대로 복원했다:

```bash
git checkout 0bd6990^ -- Assets/Prefabs/NPC/NPC_Citizen.prefab ...
```

복원분이 곧 #571에서 Play 검증된 리그다(뼈 11 · 관절 10 · `Ragdoll` 레이어). 반영하기로 했던
플레이어 쪽 수정 셋 중 **사망 경로에 걸리는 것이 없었다** — §1의 표가 그대로 답이다.
`NPC_Abductor`는 Variant라 상속한다.

### 2단계 계획 — 시체 밧줄

> **브랜치를 버리고 처음부터 다시 해도 이 절만 보면 된다.** 아래는 "무엇이 커밋돼 있나"가 아니라
> **"무엇을 어떻게 만드나"**다. 이미 작성된 것은 `feature/572-hips-replication`에 있고, 그걸 쓰든
> 새로 짜든 결과물은 같아야 한다.

#### 왜 하나 — 전 피어가 각자 밧줄을 묶고 있다

[`NpcRopeDrag.AttachCorpseRopeRpc`](../Assets/Scripts/NPC/Controller/NpcRopeDrag.cs)가
`[Rpc(SendTo.Everyone)]`이라 **모든 피어가 자기 로컬 시체에 관절 밧줄을 건다.** 앵커 위치가 피어마다
다르게 계산되므로(운반자가 원격이면 NetworkTransform 보간값 + 애니메이터가 얹는 걸음 흔들림, 그것도
피어마다 따로 평가된다) **같은 관절에 서로 다른 입력**이 들어가고 결과가 발산한다. 플레이어 쪽에서
같은 구조로 물렸고 같은 방식으로 잡았다 — §1-3, [ragdoll-corpse-split.md §7](ragdoll-corpse-split.md).

#### 설계 — 골반 하나만 복제하고 밧줄을 권위 피어 전용으로

시뮬레이션을 **하나로 줄이는 것**이 요점이다. 골반(뼈 11개 중 1개)만 `NetworkTransform`으로 복제하면
궤적의 주인이 루트에서 골반으로 넘어가고, 나머지 10개는 각 피어의 로컬 물리가 관절로 만들어 낸다
(흐느적임은 살아 있다). 그러면 **원격은 끌 이유가 없어진다** — 권위 피어가 굴린 결과가 그대로 온다.

> **기각한 대안: 전원이 묶되 원격 정렬을 세게 한다.** 플레이어에서 시도했다 발산으로 끝났다 —
> 정렬은 **동력이 아니라 표류 방지**다(불변식 7). 상한 없는 보정이 매 스텝 0.61m를 순간이동시키자
> 뼈가 지형에 박히며 평균 속도가 15m/s까지 올라갔다.

#### 바꿀 것 ① — `NpcRagdoll` (코드)

배선 존재 자체를 진실로 삼는 플래그 하나가 **세 곳을 함께** 가른다. 스위치를 따로 두지 않는 이유는
배선과 코드가 어긋날 여지를 없애기 위해서다(`PlayerRagdoll`과 같은 관례).

```csharp
// Awake — 프리팹에서 읽는다
m_hipsIsNetworkSynced =
    m_rig.HipsBody != null
    && m_rig.HipsBody.GetComponent<Unity.Netcode.Components.NetworkTransform>() != null;

// 신설 — EnterRagdoll·Unfreeze가 m_rig.SetKinematic(false) 대신 이걸 부른다
private void ReleaseBonesToPhysics()
{
    m_rig.SetKinematic(false);

    // 원격의 골반은 물리가 아니라 스트림의 소유물이다
    if (m_hipsIsNetworkSynced && !HasMoveAuthority && m_rig.HipsBody != null)
        m_rig.HipsBody.isKinematic = true;
}

// BeginRopePull — 녹이는 것은 전 피어, 묶는 것은 권위 피어만
Unfreeze();
if (m_hipsIsNetworkSynced && !HasMoveAuthority)
    return;
m_rope?.Attach(carrier);

// LateUpdate — 골반이 복제되면 정렬을 끈다(스트림이 놓은 골반을 루트로 밀어 서로 싸운다)
if (!m_hipsIsNetworkSynced && !HasMoveAuthority && m_state == RagdollState.Ragdoll)
    TickAlignBonesToRoot();
```

#### 바꿀 것 ② — 프리팹 배선 (NPC 4종)

붙는 자리는 **관절이 없는 뼈**(`Model/Root/Hips`). `NPC_Abductor`는 Variant라 상속한다.

| 컴포넌트 | 값 |
|---|---|
| `NetworkTransform` | `AuthorityMode: 0`(**서버** — NPC 루트 NT와 같다) · `Interpolate: 1` · `InLocalSpace: 0` · 위치 XYZ · **회전 XYZ 전 축**(시체는 굴러서 3축이 다 바뀐다) · 스케일 전부 0 |
| `NetworkRigidbody` | `UseRigidBodyForMotion: 1` · **`AutoUpdateKinematicState: 0`** |

#### 바꿀 것 ③ — 에디터 자동화 (`RagdollSetup`)

②를 **손으로 붙이지 않는다.** 자리가 중첩 프리팹(`Model`) 안이라 오버라이드로 저장되고, 리그를 다시
복제하면([`RagdollRigCloner`](../Assets/Scripts/Editor/RagdollRigCloner.cs)) 함께 걷힌다 — 손으로
붙이면 다시 만들 때마다 잊는다. `Run(prefabPath, rigOwnerPath, replicateHips)`로 받아
`Tools > Ragdoll > Finish Setup - NPC (전체)`가 붙이게 한다.

값은 **직렬화 이름**(`AuthorityMode`·`UseRigidBodyForMotion` …)으로 `SerializedObject`를 통해 쓴다 —
프리팹 YAML에 실제로 남는 이름이라 패키지가 공개 필드를 바꿔도 조용히 어긋나지 않는다(없으면 경고).

#### 함정 다섯 — 여기서 물린다

1. **권위 가드는 `Unfreeze()` 뒤다.** 플레이어와 갈리는 유일한 지점. 앞에 두면 원격 시체가
   **얼어붙은 채 골반만 끌려가는 조각상**이 된다 — 플레이어는 애초에 얼지 않아 맨 앞에서 반환해도
   됐던 자리다.
2. **`AutoUpdateKinematicState`는 반드시 `0`.** 스폰·소유권 변경 시점에만 도는 값이라 래그돌이
   사망·정착·밧줄에서 계속 토글하는 키네마틱과 서로 덮어쓴다.
3. **`RagdollRigCloner.StripExistingRagdoll`이 Rigidbody보다 `NetworkRigidbody`를 먼저 걷어야 한다.**
   `RequireComponent`라 남겨 두면 다시 복제할 때 Rigidbody 제거가 **조용히 막힌다.**
4. **`replicateHips`에 기본값을 두지 말 것.** 권위를 정하는 결정이다 — 이 자동화는 서버 권한을 쓰고,
   오너 권한으로 손수 배선된 **플레이어에 켜면 조용히 뒤집힌다.**
5. **`AttachCorpseRopeRpc`는 `SendTo.Everyone` 그대로 둔다.** 호출부를 안 건드리고 `NpcRagdoll`
   안에서만 가르는 것이 안전장치다 — 프리팹에서 골반 NT를 빼면 자동으로 옛 동작(전원이 묶음)으로
   돌아간다.

#### 검증

- **오프라인** — 묶기 · 끌기 · E로 놓기 · 재결박이 #571 §11-5 그대로인가 (권위가 항상 자기라
  옛 경로와 같아야 정상. 여기가 깨지면 가드 위치를 의심할 것 — 함정 1)
- **MPPM 2~3인이 본론이다** — 호스트가 끄는 시체가 클라 화면에서 **같은 궤적**을 그리는가,
  멈췄을 때 자세가 일치하는가(플레이어 실측은 정지 시 1mm 이내). 클라가 끄는 경우도
- ⚠ **유치장 수감 때 시체가 순간이동하는가, 맵을 가로질러 미끄러지는가** — 2단계가 새로 만들 수 있는
  아티팩트다. `ServerPlaceCorpse`는 "얼린 뼈는 루트의 키네마틱 자식"이라는 전제로 **루트만 옮기는데**,
  골반은 이제 `Interpolate: 1`인 자기 NetworkTransform을 갖는다 — 원격에서 루트는 즉시 튀고 **골반(과
  그 밑 전신)은 보간으로 따라갈** 수 있다. 트랜스폼 직접 대입은 NGO의 텔레포트 플래그를 세우지 않는다.
  실제로 보이면 배치 시 골반 NT에 텔레포트를 알리는 것이 고침 방향이다.
  → **미끄러짐은 보고되지 않았다.** 대신 눈에 띈 것은 **자세 박제**였고 그건 따로 고쳤다
  (위 「2단계에서 실제로 밟은 것」 고침 ②). 이 예측 자체는 아직 유효하니 MPPM에서 다시 볼 것
- **끌다가 시체를 90° 이상 굴린 뒤 조준이 몸에 맞는가** — 위 「밝혀진 것」 2·3의 캡슐 방향.
  **여기서 어긋나도 2단계는 통과**시킨다(궤적 일치가 완료 기준이다). 어긋난 정도를 적어 두는 것이
  3단계의 재료다 — 기절한 NPC는 검거 대상이라 거기서는 못 넘긴다
- 놓는 순간 관성으로 미끄러지는 것은 **알려진 미해결**이다 — [571 §14](571-npc-death-ragdoll.md),
  고칠지는 따로 판단
- 막히면 로그를 심어 좁힐 것: `m_hipsIsNetworkSynced` 값, 피어별 골반 `isKinematic`,
  밧줄이 실제로 걸린 피어

#### 되돌리는 법

**프리팹에서 골반 `NetworkTransform`을 빼면 코드는 그대로 두고 옛 동작으로 돌아간다.** 조건이 전부
그 컴포넌트 하나에 걸려 있어서다 — 되돌리려고 코드를 고칠 필요가 없다.

### 2단계에서 실제로 밟은 것 — 정착이 밧줄을 모른다 (2026-08-12)

**위 2단계 계획은 그대로 통과했다.** 골반 복제·권위 전용 밧줄은 손댈 것이 없었다. 대신 Play에서
**2단계가 만들지 않은 버그** 하나가 드러났고, 그게 *"끌던 시체가 도중에 굳고 밧줄만 늘어난다"* 였다.

#### 원인 — 정착 판정에 밧줄이라는 개념이 없다

계측으로 두 경로가 다 나왔다:

| 로그 | 무슨 일 |
|---|---|
| `정착(타임아웃)` `경과=5.0s` `정지=0.0s` `속도=7.73` | **초속 7.73m로 끌려가는 중에** 5초 하드 타임아웃이 발동 |
| `정착(멈춤)` `경과=0.3s` `정지=0.3s` `속도=0.01` | 죽자마자 묶고 **서 있는 0.3초** 동안 "멈춤"으로 판정 |

얼면 골반이 키네마틱이 되어 관절 밧줄의 장력이 **하나도 안 걸린다.** 그런데 녹이는 것은
`BeginRopePull`이 **묶는 순간**에만 하므로, 묶인 **뒤에** 얼면 되돌릴 사람이 없다. "언제는 따라오고
언제는 고정"이 이것이다 — 묶고 바로 뛰면 5초까지 되고, 잠깐 서 있으면 0.3초 만에 굳는다.

⚠ **이건 #571 8단계(권위 반전)가 만든 버그다.** 플레이어 정착은 `RestToPhysics`로 끝나 뼈가 동적으로
남으므로 **정착한 플레이어 시체도 그대로 끌린다.** NPC만 "정착 = 얼림"이 되면서 정착과 밧줄이
**배타적**이 됐는데, 그 사실이 정착 판정에 반영되지 않은 채 남아 있었다.

#### 고침 둘

1. **끌리는 동안에는 정착 판정을 돌리지 않는다** — `NpcRagdoll.Update`의 권위 가드 바로 뒤. 타이머를
   0으로 되돌리므로 **놓는 순간부터 다시 잰다**(놓은 몸은 마저 무너져야 한다).
   ⚠ 조건은 `IsAttached`가 아니라 신설한 **`RagdollRope.IsBeingCarried`**(관절 + 운반자 생존)다 —
   `RagdollRope.Tick`은 운반자를 잃으면 **관절을 남긴 채 조용히 쉬므로**, `IsAttached`로 미루면
   반대쪽 구멍이 생긴다: 시체가 영영 안 굳는다.
2. **유치장 배치 뒤에 다시 녹인다** — `ServerPlaceCorpse` 끝의 `Unfreeze()` 한 줄. 얼린 자세는
   끌려오던 순간의 자세라 감옥 바닥과 맞지 않아 **팔이 들린 채 박제됐다.** 옮기는 **동안**에는 여전히
   얼어 있어야 한다(동적 리지드바디는 부모 트랜스폼을 안 따라가므로 녹인 채 루트만 옮기면 루트만
   간다) — 얼림은 순간이동의 **수단**이고 녹임은 그 뒤처리다.

#### 검증 결과

- 오프라인 — 묶기 · 끌기(**5초 넘겨서**) · 묶어놓고 정지 후 출발 · E로 놓기 · 재결박 ✅
- 유치장 수감 — 바닥에 맞게 무너진 뒤 정착 ✅
- **MPPM 다인 — ✅ 통과.** 어색한 지점 없음. 골반 복제의 목적이 정확히 여기였다(호스트가 끄는 시체가
  클라 화면에서 같은 궤적을 그리는가) — **2단계의 완료 기준이 충족됐다**

#### 계측은 걷어냈다

원인을 좁힌 도구는 `NpcRagdoll`에 붙인 **한 줄짜리 `Diag`**였다 — 상태·경과·정지 타이머·골반
키네마틱·밧줄이 걸린 피어를 한 줄에 담고, 상태 전이마다 + 1초에 한 번 찍었다. 3단계에서 같은
계열의 증상을 만나면 같은 형태로 다시 붙이는 것이 빠르다(여러 줄로 쪼개면 콘솔을 도구로 읽을 때
잘린다).

### 3단계에서 실제로 밟은 것 — 에이전트를 끄면 전이가 깨진다 (2026-08-12)

#### 팀이 정한 것 (§2-6의 미정 항목)

| | 결정 | 실측·근거 |
|---|---|---|
| ① 기상 블렌드 | **없음 — 즉시 복귀** | 계획서 권고 (b). 플레이어가 아직 못 고친 §2-5(부활 시 큰 회전)를 물려받지 않는다 |
| ③ 기절 경로 | **오버레이만** (`HasStunOverlay`) | 넉백 착지 KO는 `EndKnockback`이 에이전트를 <b>켜면서 Warp</b>하는데 래그돌은 손을 떼야 해서 소유권이 정면으로 부딪힌다 |
| ④ 짧은 기절 | **하한 없음** | 테이저 2.67초 − 기상 모션 0.585초 = 실제로 누워 있는 시간 **약 2.09초**. 넉다운은 5초 → 약 4.4초 |

#### 구조 — 진입·이탈을 식 하나로

`WantsRagdoll()` 하나가 둘 다 답한다. 따로 쓰면 어긋나는 순간 몸이 눕지도 서지도 못하고 낀다.

```
사망            → 참 (영구, 이탈 없음)
오버레이 아님    → 거짓
밧줄 걸림       → 거짓   ← 산 대상의 밧줄은 위치 대입으로 끈다. 켜 두면 TickRootFollow와 루트를 다툰다
IsProne 거짓    → 거짓   ← 기상 모션 시작 시점
```

마지막 줄이 §2-4의 요구("이탈 시점 = `RaiseStandUp` 시점")를 그대로 표현한다 —
[`NpcAnimationDriver.IsProne`](../Assets/Scripts/NPC/NpcAnimationDriver.cs)이 `HandleStandUp`에서 정확히
그 순간 거짓이 되기 때문에 **타이밍을 따로 계산할 필요가 없었다.** 오버레이 기절도
`HandleStunnedChanged` → `HandleStateChanged(Stunned)`로 접혀 들어와 같은 값에 실린다.

이탈은 `RestoreBindPose()`(§1-2) → 애니메이터 복귀 → NavMesh 재부착 순이고, 앞의 둘은 전 피어,
재부착은 권위 피어만 한다.

#### ⚠ 밟은 것 — "에이전트를 끈다"가 전이를 깬다

**두 번 추측해서 두 번 다 부분만 맞혔고, 계측을 붙여서야 확정됐다.** 기록해 둔다.

처음 설계는 사망과 똑같이 `agent.enabled = false`였다. 그러자 기절한 NPC를 밧줄로 묶을 때 터졌다:

```
[전이진단] 래그돌이 에이전트를 껐다 (진입)
[전이진단] Attack → Escorted | 에이전트 enabled=False onNavMesh=False 묶임=False/True
→ "Resume" can only be called on an active agent...  (NpcResistState.Exit)
```

- 1차 시도는 `NpcEscortedState.Enter`만 고쳤다 → 다음 판에는 **나가는 상태의 `Exit()`**에서 터졌다.
  `Exit`·`Enter` 어느 쪽이든 에이전트를 만지면 깨진다는 뜻이라, 호출부 하나씩 막는 방식은 끝이 없다.
- 로그가 보여준 결정적 사실: 전이 시점에 **`묶임=False/True`** — 즉 `IsTethered`는 이미 참인데
  `IsRoped`는 아직 거짓이다. 커스터디 전이가 `StartRopeDrag`보다 **앞**이기 때문이고
  (그쪽 주석이 못박는 계약이다), 그래서 밧줄로 추론하는 가드는 전부 이 순간을 놓친다.

**근본 원인은 불변식 위반이다.** 코드베이스의 전제는 *"전이 시점에 에이전트는 살아 있다"*이고
(`NpcRopeDrag.StartRopeDrag` 주석 — "뒤에 하면 직전 상태의 Exit이 꺼진 에이전트를 건드린다"),
기절 래그돌은 **산 NPC에** 얹히는 첫 사례라 그 전제 안에 있어야 했다.

#### 고침 — 끄지 않고 손만 뗀다

| 경로 | 에이전트 |
|---|---|
| **기절** | `updatePosition`/`updateRotation`만 `false`. **`enabled`는 참으로 남는다** |
| **사망** | 지금처럼 통째로 끈다 — 시체는 NavMesh로 돌아가지 않는다(#571), 죽은 몸에는 깨질 전이도 없다 |

막아야 했던 것은 "에이전트가 매 프레임 트랜스폼을 NavMesh 위로 써 버리는 것"뿐인데 그건
`updatePosition`이 하는 일이라, 그것만 떼면 `isStopped`·`SetDestination`이 계속 합법으로 남는다.

⚠ **되돌릴 때 두 플래그는 밧줄·넉백 가드보다 <b>먼저</b>, 무조건 되돌린다.** 가드 뒤에 두면 기절 중
묶인 NPC는 **영영 걷지 못한다** — 나중에 `StopRopeDrag`가 에이전트를 켜도 위치 갱신이 꺼진 채라서다.

#### 곁다리로 남긴 것

- **`NpcController.AgentReady`** 신설(`agent != null && enabled && isOnNavMesh`). 같은 식이 이미 4곳에
  흩어져 있었고 그중 `SetFrozen`을 이걸로 바꿨다. `NpcEscortedState.Enter`의 가드도 `IsRoped` 단독에서
  `|| !AgentReady`로 넓혔다 — 위 고침으로 그 경로는 이제 안 걸리지만, **넉백 비행·밧줄 끌기·사망은
  여전히 에이전트를 끄는 구간**이라 그때 전이가 오면 같은 에러가 난다. 원인 열거가 아니라 결과를
  보는 가드다.
- `NpcController.TickNavMeshRecovery` 주석 갱신 — "이 상태를 만드는 셋"이 넷이 됐고,
  **꺼 둔 쪽이 반드시 스스로 켜야 하는 이유**(회수는 `enabled == true`만 잡는다)를 명시했다.
- §2-3-3이 예고한 `StopRopeDrag`의 래그돌 가드는 **넣지 않았다.** 밧줄과 래그돌이 상호배타가 되어
  (위 `WantsRagdoll`) 필요가 없어졌고, 죽은 몸은 기존 `IsDead` 가드가 이미 막는다.

#### 검증 결과

- 기절 → 눕기 → 기상 → 도주 ✅ (에러 없음, 흐름은 이어진다)
- 기절한 NPC를 밧줄로 묶기 ✅ (에러 없음)
- 묶었다 놓은 뒤 실제로 걷기 ✅ (위 플래그 복구가 먹는 지점)
- 반복 기절 ✅

#### 실측 — 래그돌 유지 구간

한때 "기절하면 여전히 클립 자세로 눕는 것 같다"는 관찰이 있었는데, **시각을 넣은 계측이 정상임을
확정했다.** 넉다운(5초) 1회의 실측:

```
t=9.69   기절 시작 (지속 5.00s) → <b>같은 프레임</b>에 래그돌 진입 (뼈 11, 애니 off, 골반 동적)
t=14.09  기상 모션 발행 (경과 4.42s = 5.00 − 0.585)  ← 같은 프레임에 래그돌 이탈
t=14.69  기절 종료 (정확히 5.00s)
```

**래그돌 유지 4.40초**, 이탈 시점이 기상 모션과 **같은 프레임**이다 — §2-4가 요구한 정합이 그대로
나왔다.

⚠ **다만 눈으로는 헷갈리기 쉽다.** 몸은 0.3초쯤 만에 정착해 `Frozen`(전 뼈 키네마틱)이 되므로,
유지 구간의 대부분은 **움직이지 않는 자세**다. 테이저(2.67초 → 래그돌 2.09초)는 특히 짧아 "무너지는
그림"이 거의 안 보이고 굳은 자세만 남는다. 어색해 보이면 손댈 곳은 진입이 아니라
`m_settleHoldSeconds`·`m_settleSpeedThreshold`(정착이 너무 이르다)다.

**교훈: 시각(`Time.time`) 없는 상태 로그는 "됐다/안 됐다"를 못 가른다.** 처음 계측에는 조건만 있고
시각이 없어서, 조건이 전부 정상인데도 원인을 좁히지 못했다.

#### ⚠ 원격이 얼면 안 된다 — 플레이어가 먼저 갔다 되돌아온 길 (2026-08-12)

**증상: 착지가 끝나는 순간 전체 모델이 잠깐 땅속으로 꺼졌다 나온다. 호스트는 멀쩡하고 클라만.**

계측 실측(클라, 정착 직후 30프레임):

```
+1프레임   골반로컬y = 0.234   루트y = 0.235   최저뼈y = +0.297
+10프레임  골반로컬y = -0.001  루트y = 0.130   최저뼈y = -0.025   ← 지면 아래
+30프레임  골반로컬y = -0.001  루트y = 0.030   최저뼈y = -0.141
```

같은 순간 호스트는 골반−루트 간격 0.146을 끝까지 유지했다. 읽히는 것은 하나다 —
`ApplyFrozenPose`가 넣은 **골반 로컬 오프셋이 0으로 덮이고**, 골반이 루트에 들러붙어
**루트 보간을 따라 몸 전체가 지면 아래로 끌려 내려간다.** 잃은 높이와 가라앉은 양이 거의 같다.

**시도했다 버린 고침: 얼린 자세를 매 프레임 다시 주장하기.** 스트림과 싸우는 방식이라 미끄러진다.

**채택한 고침: 골반 복제 구성에서 <u>원격은 얼지 않는다</u>.** 골반만 스트림에 매인 키네마틱으로 두고
나머지 열 개는 로컬 물리에 맡긴다 — 자세 동기화도 원격에서는 쓰지 않는다.

⚠ **플레이어가 정확히 이 길을 먼저 갔다 되돌아왔다.** `PlayerRagdoll`에는 얼림 상태도 포즈
브로드캐스트도 **아예 없다**(정착 = `RestToPhysics`). 그쪽 주석이 근거다:

> **전 피어가 물리를 유지한다.** 원격에서 뼈를 키네마틱으로 굳혔다가 되돌렸다 — 굳히면 시체가
> **루트 높이 하나에 매달린 조각상**이 되어, 그 높이가 조금이라도 틀리면 흡수할 수단이 없어
> 바닥에 박히거나 공중에 뜬다. 물리가 있으면 중력·접촉이 흡수한다. (506 §10-3)

NPC가 갈라진 지점은 #571 8단계(정착 = 얼림)였고, 2단계 골반 복제가 그 충돌을 드러냈다.
**호스트는 그대로 얼린다** — 유치장 수감이 `transform.position` 한 줄이 되는 것과 "정착한 시체는
매 프레임 비용 0"이 거기 걸려 있고, 호스트에서는 문제가 없었다.

#### 관절 밧줄을 산 대상까지 넓혔다 (팀 확정)

기절한 NPC를 묶는 순간 몸이 래그돌에서 **클립 자세로 튀는** 문제가 있었다. 계측이 원인을 확정했다:

```
t=10.00  오버레이=True  묶임=False/False  → 래그돌 (정상)
t=11.39  오버레이=False 묶임=True/True    → ExitRagdoll → 애니메이터 복귀
```

`ServerApplyRopeDrag`가 묶자마자 `ExitStun`을 부르고(#292 — 안 걷으면 묶자마자 도망친다), 거기에
"밧줄이 걸리면 래그돌이 물러난다"는 가드가 겹쳤다. 그 가드를 둔 이유는 **산 대상의 밧줄이 위치
대입**이라 `TickRootFollow`와 루트를 다투기 때문이었다.

**팀 확정: 산 대상도 시체와 같은 관절 밧줄로 끈다.** 코드 주석이 이미 그 방향을 적어 두고 있었다 —
*"갈리는 기준은 「대상이 래그돌이냐」다"*. 지금까지 래그돌 = 시체라 사망 게이트 하나로 같은 효과가
났을 뿐이고, 기절에 래그돌이 붙으면서 둘이 갈렸다.

| 바꾼 곳 | 내용 |
|---|---|
| `NpcController.Update` | 밧줄 틱을 **래그돌이면 건너뛴다**. ⚠ `return`이 아니라 건너뛰기다 — 아래 `m_stun.Tick()`이 기절 타이머를 굴리므로 끊으면 끌려가는 동안 기절이 영영 안 풀린다 |
| `NpcRopeDrag` | 관절 밧줄 부착 조건 `IsDead` → `UsesRagdollRope`. 뗄 때는 지금 상태가 아니라 **걸었다는 사실**(`m_corpseRopeAttached`)을 본다 — 끌리는 도중에 래그돌을 벗어날 수 있어서다 |
| `NpcRagdoll.WantsRagdoll` | 밧줄 조건을 **뒤집었다**(물러남 → 유지). ⚠ 오버레이와 **함께** 봐야 한다 — 오버레이만 보면 `ExitStun` 때문에 묶는 그 순간 풀린다 |

`IsProne`이 `IsRopeProne`까지 포함하므로 **줄이 풀려 기상이 예약되는 순간** 래그돌이 자동으로 빠진다 —
기절과 밧줄이 값 하나로 덮인다.

#### 남은 어색함 (별도 이슈 예정)

**줄을 풀면 애니메이터가 즉시 켜져 몸이 튄다.** 기상 블렌드를 넣지 않기로 한 결정(§2-6①(b))의 대가가
밧줄 경로에서 드러난 것이다. 팀이 별도 이슈로 다루기로 했다 — 손볼 자리는 `ExitRagdoll`이고,
재료(`RagdollPoseBlend`)는 이미 공유 코드에 있다.
- 계측(`[전이진단]`)은 걷어냈다. 전이마다 한 줄 + 에이전트를 껐다 켠 주체를 한 줄로 찍는 형태였고,
  **호출부를 하나씩 추측해 막던 것을 한 번에 끝낸 것이 이 계측이다.**

---

### 작업하며 밝혀진 것 셋

1. **§5(조준 마스크)는 기우였다 — §3-1은 필요 없다.** `PlayerInteractor.m_interactMask`가 `~0`이라던
   것은 **코드 기본값**이고, `Player.prefab`의 실제 값은 `128` = `Interactable`(7) 하나다. 래그돌
   뼈(레이어 10)는 마스크 밖이라 조준 레이에 안 걸리고, `m_losBlockMask`도 `1`(Default)이라 가시선을
   막지 않는다. **3단계가 "기절한 NPC를 못 겨냥한다"로 막힐 걱정은 없다.**

2. **대신 볼 것은 `NpcProneCollider`의 방향이다.** 사망·기절은 항상 `IsProne`이라 누운 캡슐
   (1.8m × 0.6m)이 적용되는데, 그 캡슐은 **루트의 forward 축에 눕고** `NpcRagdoll.TickRootFollow`는
   **yaw를 일부러 안 돌린다**(리지드바디 없는 뼈만 계층을 따라 돌아 목이 비틀리므로). 물리로 굴러
   방향이 바뀐 몸은 **조준 캡슐과 보이는 몸이 엇갈린다.** 사망만 있을 때는 부수적이지만
   **기절한 NPC는 검거 대상이라 3단계에서 무게가 커진다.**

   ⚠ **2단계가 이것을 "가끔"에서 "상시"로 바꾼다.** 가만히 정착한 시체는 한 방향으로 굳어 있지만
   **끌리는 시체는 물리로 계속 구르므로**, 밧줄을 붙이는 순간 어긋남이 끌기 내내 드러난다. 루트가
   골반에 붙는 것도 더해진다 — 누운 캡슐의 중심이 `(0, 0.3, -0.1)`이라 골반보다 0.3m 더 뜬다.
   **2단계의 완료 기준(전 피어 궤적 일치)을 막지는 않으므로** 대응(캡슐을 골반 회전에 맞추기 vs 뼈
   콜라이더를 조준 마스크에 넣기)은 3단계에서 정하고, 2단계에서는 검증 항목으로만 본다(아래 「검증」).

3. **NPC 고유 컴포넌트 셋 중 2단계에 걸리는 것은 없다.** 플레이어는 `CharacterController` 하나만
   보면 됐지만 NPC 루트에는 `NavMeshAgent`·`Rigidbody`·`CapsuleCollider`가 더 붙어 있어 처리할 것이
   늘어난다고 봤다. **실측 결과 둘은 이미 닫혀 있었고, 남는 하나도 물리로는 안 걸린다:**

   | 컴포넌트 | 2단계에 필요한가 | 근거 |
   |---|---|---|
   | `NavMeshAgent` | **없음 — 사망 경로는 이미 닫혀 있다** | 끄는 곳이 둘([NpcRopeDrag#L156](../Assets/Scripts/NPC/Controller/NpcRopeDrag.cs#L156) 끌기 시작 · [NpcRagdoll#L310](../Assets/Scripts/NPC/View/NpcRagdoll.cs#L310) 래그돌 진입의 재확인)이고, 놓을 때는 `IsDead` 가드가 막는다([#L219](../Assets/Scripts/NPC/Controller/NpcRopeDrag.cs#L219)). NavMesh가 실제로 무거워지는 것은 **기절(3·4단계)** 뿐이고 그건 §2-3이 이미 세워 뒀다 |
   | 루트 `Rigidbody` | **없음 — 사실상 없는 것과 같다** | 프리팹에서 `m_IsKinematic: 1` · `NetworkRigidbody` 없음, 그리고 **`Assets/Scripts` 전체에 이 rb를 참조하는 코드가 하나도 없다**(`Rigidbody`를 만지는 파일은 전부 뼈 쪽 — `Ragdoll` 계열·`Taser`·`NpcFleeState`). 영원히 키네마틱이라 뼈와 물리로 다툴 수 없다. 붙어 있는 이유는 트리거 감지로 보인다 |
   | `CapsuleCollider` | **물리로는 없음. 남는 것은 방향뿐** | 아래 두 문단 |

   **몸통 캡슐이 뼈를 미는 일은 없다 — 레이어 매트릭스가 막는다.**
   `ProjectSettings/DynamicsManager.asset`의 `m_LayerCollisionMatrix`를 파싱한 결과 **`Ragdoll`(10)은
   `Default`(0)하고만 충돌한다.** NPC 루트는 `m_Layer: 7`(`Interactable`)이라, 골반 위치로 따라온
   (`TickRootFollow`) 몸통 캡슐이 **자기 뼈를 걷어칠 수 없다.** 캡슐 때문에 넣을 코드는 없고,
   **#567(시체 떨림)에서 "몸통 캡슐이 뼈를 비빈다"는 가설도 이걸로 지워진다** — §4④가 세어 둔
   후보(밧줄 발산·뼈 길이 누적)에는 원래 없었지만, NPC에만 있는 부품이라 리그를 붙이는 순간 새로
   의심할 자리였다. 두 후보는 그대로 남는다.

   다만 캡슐(7)은 `OwnBody`(6)와는 충돌하므로 **끄는 플레이어가 자기가 끄는 시체 캡슐에 걸릴 수는
   있다** — 밧줄 길이 안쪽이라 실제로 닿는지는 플레이테스트에서 볼 것.

   ⚠ **루트 Rigidbody에 딸린 함정 하나 — 「바꿀 것 ②」의 `NetworkRigidbody`는 루트가 아니라
   `Model/Root/Hips`다.** 루트에 붙이면 `UseRigidBodyForMotion`이 키네마틱 rb로 이동하려 들어
   [`TickRootFollow`](../Assets/Scripts/NPC/View/NpcRagdoll.cs#L415)의 `transform.position` 직접 대입과
   **매 프레임 싸운다.** 루트 NT는 지금처럼 `NetworkRigidbody` 없이 트랜스폼만 복제하게 둔다.

---

## 0. ~~결론 — 이슈 본문이 시사하는 것보다 작업량이 작다~~ → 기절을 태우면 다시 커진다

> **아래 판단은 "사망만 태운다"를 전제로 한 것이다. 전제가 바뀌어 결론이 뒤집혔다** — 이 절 끝의
> 갱신된 표를 볼 것.

이슈의 "할 일" 대부분(`NpcRagdoll` 신규, NavMesh 처리, Animator 역할 분리, 서버 권위, 얼림 메커니즘)은
**#571에서 이미 끝나 있고 코드도 남아 있다.** 남은 것은 플레이어 쪽 버그 수정 세 개를 NPC에도
반영해야 하는지 판단하는 것뿐이고, 그중 실제로 할 일이 있는 건 하나뿐이다.

| | 플레이어 쪽 수정 | NPC에 필요한가 |
|---|---|---|
| ① `Physics.SyncTransforms()` | `SetKinematic(false)` 앞에 추가 (T포즈 시작 방지) | **이미 적용됨.** `RagdollRig.SetKinematic`(공유 코드)에 들어 있어 `NpcRagdoll`도 자동으로 받는다. 손댈 것 없음 |
| ② 뼈 길이 드리프트 방지 (`RagdollPose.Copy` + `RestoreBindPose`) | 살아있는 리그 ↔ 시체 리그를 오가며 뼈 길이가 누적되던 버그 | ~~해당 없음~~ → **절반이 필요하다.** 아래 §1-2 |
| ③ 밧줄 권위 전용 + 골반 복제 | 전 피어가 각자 밧줄을 묶어 발산하던 버그 | **포팅 필요.** 아래 §1-3 |

### 갱신된 전체 그림

| | 무엇 | 상태 |
|---|---|---|
| ① `SyncTransforms` | — | 공짜 (§1-1) |
| ② 뼈 길이 드리프트 | 기상 시 `RestoreBindPose()` | **필요** (§1-2) |
| ③ 밧줄 권위 전용 + 골반 복제 | `NpcRagdoll` 안에서 가르기 | 필요 (§1-3) |
| ④ **기절 래그돌 진입** | `PollDeath`를 사망 + 기절로 확장 | **신규** (§2-1) |
| ⑤ **기상 = NavMesh 복구 경로** | `NpcKnockback.EndKnockback`의 형제 | **신규** (§2-2·2-3) |
| ⑥ **기상 클립 타이밍 정합** | `RaiseStandUp` 시점 ↔ 애니메이터 복귀 | **신규** (§2-4) |
| ⑦ **밧줄의 에이전트 재활성 가드** | `StopRopeDrag`에 래그돌 가드 한 줄 | **신규** (§2-3-3) |

④~⑦은 이슈 본문에도, 이 문서의 원래 판단에도 없던 작업이다.

---

## 1. 왜 이렇게 갈리는지

### 1-1. ① SyncTransforms — 공짜

`RagdollRig.SetKinematic(bool)`은 플레이어와 NPC가 **같은 코드**를 쓴다(`RagdollRig`는 네트워크·권위·
이동 프록시를 모르는 순수 물리 컴포넌트라는 것이 애초에 #506의 설계다). 그 안에 넣은
`Physics.SyncTransforms()` 호출은 호출부와 무관하게 걸리므로, NPC가 `EnterRagdoll`이나 `Unfreeze`에서
`m_rig.SetKinematic(false)`를 부르는 순간 이미 적용된다.

### 1-2. ② 뼈 길이 드리프트 — ⚠ **뒤집혔다: 이제 해당된다**

> **원래 판단 (무효):** 이 버그는 정확히 두 조건이 겹쳐야 나온다 — (1) **살아있는 리그와 시체 리그가
> 별도 오브젝트**라 `RagdollPose.Copy`로 포즈를 왕복 복사한다 (2) **부활해서 같은 리그를 다시 쓴다**.
> NPC는 리그가 하나뿐이고 부활이 없으므로 둘 다 없다 — 포팅할 코드가 없다.
>
> **무효가 된 곳: 조건 (2)다.** 기절 래그돌은 정의상 부활이다. NPC는 죽으면
> [`NpcSpawner.ResetSpawnState`](../Assets/Scripts/NPC/NpcSpawner.cs#L132)가 `Destroy`하고 다음
> 라운드는 [`Instantiate`](../Assets/Scripts/NPC/NpcSpawner.cs#L188)로 새로 만들지만, **기절은 같은
> 인스턴스가 몇 번이고 깨어난다.**

그리고 **리그가 하나라는 것이 여기서는 면제가 아니라 "걸러 줄 코드가 없다"는 뜻이다.** 조건 (1)이
없다는 것은 `RagdollPose.Copy`를 안 쓴다는 뜻인데, 플레이어의 고침 **두 조치 중 하나가 바로 그
`Copy`**였다([ragdoll-corpse-split.md §8-2](ragdoll-corpse-split.md)). NPC엔 그 필터가 놓일 자리가 없다.

리그 하나짜리 누적 경로:

```
1차 래그돌   물리가 관절을 늘린다 → 뼈 localPosition이 바인드 포즈에서 벗어난다
기상        애니메이터 복귀 — 애니메이터는 회전만 쓴다 → 늘어난 길이가 그대로 남는다
2차 래그돌   connectedAnchor는 바인드 포즈 기준으로 구워져 있으므로
            (프리팹이 AutoConfigureConnectedAnchor: 1)
            첫 물리 스텝부터 관절이 위반된 채 출발 → 사지가 늘어나며 바닥을 뚫는다
```

**고침 — 플레이어의 짝 중 뒤쪽만 필요하다.** 재료는 공유 코드에 이미 있다:

| | 위치 | NPC에 필요한가 |
|---|---|---|
| `RagdollPose.Copy`가 관절 달린 뼈의 `localPosition`을 안 옮긴다 | `Common/Ragdoll/RagdollPose.cs` | **해당 없음** — `Copy`를 안 쓴다 |
| 부활 시 `RagdollRig.RestoreBindPose()` | [RagdollRig.cs#L352](../Assets/Scripts/Common/Ragdoll/RagdollRig.cs#L352) | **필요** — 기상 시 부른다 |
| 진단값 `MaxBindPositionDrift` | [RagdollRig.cs#L322](../Assets/Scripts/Common/Ragdoll/RagdollRig.cs#L322) | 검증에 쓴다 (§6) |

⚠ **`뼈길이드리프트` 진단을 반드시 켜고 검증할 것.** 이 버그는 1차에서 0.0055m, 2차에서 0.0624m처럼
**조용히 누적된 다음 터진다** — 플레이어 쪽에서 회전만 재던 진단이 세 번이나 "복사는 정상"이라고
말하는 동안 뼈 길이가 11배로 늘고 있었다(같은 문서 §8-2 "이 아크의 교훈").

### 1-3. ③ 밧줄 권위 전용 — 그대로 필요하다

[`NpcRopeDrag.AttachCorpseRopeRpc`](../Assets/Scripts/NPC/Controller/NpcRopeDrag.cs#L331)는 지금도
`[Rpc(SendTo.Everyone)]`이고, 안에서 부르는 `AttachCorpseRope`가 `NpcRagdoll.BeginRopePull`을 무조건
호출한다 — **전 피어가 각자 밧줄을 묶는다.** 이건 플레이어가 고치기 전(`ragdoll-corpse-split.md §1`)과
정확히 같은 구조다:

- 앵커 위치가 피어마다 다르게 계산된다(운반자가 원격이면 NetworkTransform 보간값 + 애니메이터가
  얹는 흔들림)
- 매 물리 스텝 같은 관절에 다른 입력이 들어가 결과가 발산한다

다만 앵커 자체는 이미 고쳐져 있다 — `NpcRopeDrag.AttachCorpseRope`가 부르는
`PlayerHeldItemView.ResolveRopeAnchor(carrier)`는 플레이어·NPC가 공유하는 static 메서드라, 손이
아니라 운반자 루트를 돌려주도록 고친 것이 NPC에도 이미 적용돼 있다. **입력 자체가 다른** 것(전 피어가
각자 시뮬레이션)만 남았다.

---

## 2. 기절 래그돌 — 새로 만들 것

### 2-1. 진입 — `PollDeath`를 사망 + 기절로 확장

[`NpcRagdoll.PollDeath`](../Assets/Scripts/NPC/View/NpcRagdoll.cs#L377)는 지금 `m_owner.Death.IsDead`
하나만 본다. 주석이 **"부활 분기가 없다"**고 명시하고, 클래스 주석도 *"부활이 없으므로 기상 블렌드·
기상 클립용 루트 yaw 정렬이 전부 필요 없고, 부활 오인 문제(506 §9-19)가 통째로 사라진다"*고 쓴다.
**두 문단 다 갱신 대상이다.**

기절 쪽 조건값은 [`NpcStun.IsStunned`](../Assets/Scripts/NPC/Controller/NpcStun.cs#L43)를 쓴다 —
경로를 가리지 않는 일반 질문으로 이미 설계돼 있고(오버레이 + 넉백 착지 KO를 함께 답한다) 세션 중
동기화 값이라 전 피어가 같은 값을 본다. 폴링 구조가 그대로 맞는다.

⚠ **`m_skipThisEpisode`(늦게 접속한 피어는 이번 사망을 건너뛴다)를 기절에 그대로 쓰면 안 된다.**
사망은 한 번이라 "이미 지난 과거"로 통째로 건너뛰는 것이 맞지만, 기절은 반복되므로 그 플래그가
켜진 채 남으면 **그 피어는 이후 모든 기절 래그돌을 영구히 건너뛴다.** 에피소드 단위로 리셋되게 갈라야
한다.

### 2-2. 이탈 — NavMesh 복구는 `NpcKnockback.EndKnockback`의 형제다

찾던 패턴이 [`NpcKnockback.EndKnockback`](../Assets/Scripts/NPC/Controller/NpcKnockback.cs#L153)에
이미 구현돼 있다. **새로 설계할 것이 아니라 이 함수가 넉백 비행에 대해 하는 일을 래그돌 기절에 대해
하는 것이다:**

```csharp
// 통행 마스크로 착지점을 찾는다 — 못 가는 영역(Jail)에 Warp되면 경로가 안 잡혀 고착된다 (#415)
NavMesh.SamplePosition(transform.position, out NavMeshHit ground,
                       config.KnockbackLandSampleDistance, agent.areaMask)
agent.enabled = true;
agent.Warp(landing);          // NavMesh 위 착지점에 다시 붙인다
if (!agent.isOnNavMesh) { 경고 + 상태 전이 포기 }   // TickNavMeshRecovery(#557)가 1초 뒤 재시도
```

이 함수에는 **이미 값을 치른 판단이 두 개** 박혀 있다 — `areaMask`를 쓰는 이유(Jail 같은 통행 불가
영역에 `Warp`하면 고착, #415)와 `Warp` 실패 시 상태 전이를 안 시키는 이유(상태 클래스가 곧바로
에이전트를 건드려 에러). 기절 기상도 같은 함정을 지나므로 그대로 따른다.

기상 시 해야 할 일 세 가지:

1. `RestoreBindPose()` — §1-2
2. 애니메이터 복귀 (`m_animator.enabled = true` + `SetSkinsAlwaysVisible(false)`)
3. 위 NavMesh 재부착

### 2-3. ⚠ NavMesh 복구 주체가 지금 어긋나 있다 — 조용히 실패한다

[`NpcStun.EnterStunned`](../Assets/Scripts/NPC/Controller/NpcStun.cs#L178)는 에이전트를 **끄지 않는다** —
`isStopped = true` + `ResetPath()`만 하고 직전 `isStopped`를 `m_agentStoppedBefore`에 기억해 둔다.
반면 [`NpcRagdoll.EnterRagdoll`](../Assets/Scripts/NPC/View/NpcRagdoll.cs#L311)은
`m_agent.enabled = false`를 강제한다(켜져 있으면 매 프레임 NavMesh 위로 끌어내려 몸이 못 눕는다).

기절이 래그돌을 타면 [`ExitStun`](../Assets/Scripts/NPC/Controller/NpcStun.cs#L258)의 복구가

```csharp
if (agent.enabled && agent.isOnNavMesh)   // ← enabled가 false라 조용히 건너뛴다
    agent.isStopped = m_agentStoppedBefore;
```

**경고 한 줄 없이 통과한다.** 에이전트는 영원히 꺼진 채고, 물리로 굴러간 몸은 NavMesh 밖일 수도 있다.
(`isOnNavMesh` 검사는 NavMesh 밖에서 `isStopped`를 읽으면 Unity가 에러를 뱉는 것을 막으려 들어간
것이다, #557 — 이 실패를 감추려던 것이 아니다.)

**복구 주체 = A안(`NpcRagdoll`이 되돌린다). 확정됐다** — 아래 §2-3-1의 인벤토리가 근거다.

### 2-3-1. 에이전트 소유권 인벤토리 (조사 결과, 서버 기준)

| 구간 | 끄는 곳 | 켜는 곳 |
|---|---|---|
| 클라이언트 전체 | [NpcController.cs#L148](../Assets/Scripts/NPC/Controller/NpcController.cs#L148) | 없음 — **영구** (이동은 NetworkTransform이 쥔다) |
| 넉백 비행 | [NpcKnockback.cs#L78](../Assets/Scripts/NPC/Controller/NpcKnockback.cs#L78) | [#L159](../Assets/Scripts/NPC/Controller/NpcKnockback.cs#L159) + `Warp` |
| 밧줄 끌기 | [NpcRopeDrag.cs#L158](../Assets/Scripts/NPC/Controller/NpcRopeDrag.cs#L158) | [#L222](../Assets/Scripts/NPC/Controller/NpcRopeDrag.cs#L222) + `TryWarpNear` 2단 폴백 |
| 사망 | [NpcDeath.cs#L122](../Assets/Scripts/NPC/Controller/NpcDeath.cs#L122) | 없음 — **영구, 의도적** ("시체는 NavMesh 위로 돌아가지 않는다") |
| 기절 오버레이 | **끄지 않는다** — `isStopped`만 | `ExitStun`이 `isStopped` 복구 |
| **래그돌 진입** | [NpcRagdoll.cs#L311](../Assets/Scripts/NPC/View/NpcRagdoll.cs#L311) | **없음** ← 구멍 |

**규칙은 "끈 쪽이 켠다"이고 3/3 예외가 없다.** `NpcRagdoll`이 네 번째 소유자이므로 그쪽이 되돌린다.

**B안(`ExitStun`이 한다)은 탈락.** `ExitStun`은 자기가 끈 것이 아니라 진입 시 기억한
`m_agentStoppedBefore`만 되돌리는 대칭 구조다. 여기에 `enabled` 복구를 넣으면 *끄지도 않은 것을 켜는*
함수가 되고, `NpcDeath`가 **영구히** 꺼 둔 에이전트까지 되살릴 수 있다 — `ExitStun`은 사망 경로에서도
불린다([NpcCustody.cs#L129](../Assets/Scripts/NPC/Controller/NpcCustody.cs#L129),
[NpcStandUp.cs#L88](../Assets/Scripts/NPC/Controller/NpcStandUp.cs#L88)).
**C안(전용 부품)도 불필요** — `NpcRagdoll`이 이미 그 부품이다.

### 2-3-2. ⚠ 회수 안전망이 이 경우를 잡지 않는다

[`NpcController.TickNavMeshRecovery`](../Assets/Scripts/NPC/Controller/NpcController.cs#L410)는

```csharp
if (!m_agent.enabled || m_agent.isOnNavMesh) { 리셋; return; }
```

로 시작하고, 주석이 못박는다 — *"에이전트를 **꺼 둔 구간**(넉백 비행·밧줄 끌기)은 위치를 그쪽이 쥐고
있어 굳은 것이 아니다 — **건너뛴다.**"*

즉 회수는 **`enabled == true`인데 NavMesh 밖**만 잡는다. **래그돌이 꺼 놓고 아무도 켜지 않으면 회수는
영원히 오지 않는다.** `TickNavMeshRecovery`는 `enabled = true`로 되돌린 **다음의** 폴백이지, 안 켜도
되는 이유가 아니다.

> 덧붙여 그 주석이 이 상태를 만드는 셋을 *"밧줄 놓기·넉백 착지·**기절 해제**"*로 열거하는데,
> 기절 해제가 거기 있는 이유는 **지금 기절이 에이전트를 안 끄기 때문**이다. 래그돌을 태우면 기절이
> 그 목록에서 "꺼 둔 구간"으로 옮겨가 회수 대상에서 벗어난다 — **저 주석도 갱신 대상이다.**

### 2-3-3. ⚠ 밧줄과 이중 소유가 충돌한다

[`NpcRopeDrag.StopRopeDrag`](../Assets/Scripts/NPC/Controller/NpcRopeDrag.cs#L222)는 되살리기 전에
가드 둘을 둔다:

```csharp
if (m_owner.Knockback.IsKnockedBack) return false;   // 넉백이 쥐고 있다
if (m_owner.Death.IsDead) return false;              // 시체는 안 돌아온다
agent.enabled = true;
```

**래그돌 기절 가드가 없다.** 기절해 누운 NPC를 묶었다 놓으면 `StopRopeDrag`가 에이전트를 켜고,
`EnterRagdoll` 주석이 경고한 그 상태가 된다 — *"켜져 있으면 매 프레임 NavMesh 위로 끌어내려 몸이 못
눕는다."* **세 번째 가드가 필요하다:**

```csharp
if (m_ragdoll != null && m_ragdoll.IsRagdollActive) return false;   // 래그돌이 쥐고 있다
```

`m_ragdoll`은 이미 필드로 있다([NpcRopeDrag.cs#L14](../Assets/Scripts/NPC/Controller/NpcRopeDrag.cs#L14) —
리그 없는 프리팹에서 null 허용이 관례). 넉백 가드와 **정확히 같은 성격**이다(일시적 소유권 양보).

### 2-3-4. 순서 제약 — 상태 전이가 에이전트보다 먼저다

[`NpcDeath.ServerEnterDead`](../Assets/Scripts/NPC/Controller/NpcDeath.cs#L105) 주석 ⑤:
*"상태 전이 — 에이전트를 끄기 **전**이다. 직전 상태의 `Exit()`이 살아 있는 에이전트를 정리해야 한다."*
[`NpcStunnedState.SetAgentStopped`](../Assets/Scripts/NPC/States/NpcStunnedState.cs#L87)가 이 전제가
깨졌던 이력(#571 — 넉백 비행 중 사망하면 꺼진 에이전트를 든 채 `Exit`이 불렸다)을 주석에 남기고 가드를
달아 뒀다. **기상도 같은 순서다.**

### 2-3-5. `ExitStun`을 부르는 경로 전부가 래그돌 이탈을 타야 한다

A안이라 복구 주체는 `NpcRagdoll`이지만, **기절이 풀리는 사실을 알아채는 지점**은 여전히 여럿이다.
`PollDeath`가 폴링이라 값만 보면 자동으로 덮이지만(그래서 폴링이 유리하다), 이탈 시점이 기상 클립과
맞아야 하므로(§2-4) 각 경로가 어떤 시점에 오는지는 확인해야 한다:

| 호출부 | 상황 | `resumeReaction` |
|---|---|---|
| [NpcStun.Tick#L217](../Assets/Scripts/NPC/Controller/NpcStun.cs#L217) | 시간 만료 — 정상 기상 | `true` |
| [NpcStandUp#L88](../Assets/Scripts/NPC/Controller/NpcStandUp.cs#L88) | 줄이 풀려 일어나기 예약 | `false` |
| [NpcCustody#L129](../Assets/Scripts/NPC/Controller/NpcCustody.cs#L129) | 수감 | `false` |
| [PlayerEscortCommands#L523](../Assets/Scripts/Player/Escort/PlayerEscortCommands.cs#L523) | 연행 시작 | `false` |
| `ClearStunOverlay` — [NpcDeath#L98](../Assets/Scripts/NPC/Controller/NpcDeath.cs#L98) | 사망이 오버레이를 걷는다 | — (사망 래그돌로 이어짐) |
| `ClearStunOverlay` — [NpcKnockback#L53](../Assets/Scripts/NPC/Controller/NpcKnockback.cs#L53) | 넉백이 오버레이를 이긴다 | — (§2-6③의 이중 위험) |

### 2-4. 기상 클립 타이밍 정합

[`NpcStun.Tick`](../Assets/Scripts/NPC/Controller/NpcStun.cs#L204)이 `m_stunDuration - StandUpSeconds`
지점에서 `RaiseStandUp()`으로 **기상 클립을 전 피어에 재생**한다. 래그돌이 그보다 늦게 풀리면 클립은
이미 돌고 있는데 뼈는 아직 물리에 있다 — 애니메이터가 꺼져 있으니 화면에는 아무 일도 안 일어나고,
풀리는 순간 클립 중간부터 튄다.

즉 **래그돌 이탈 시점 = `RaiseStandUp()` 시점**이어야 한다. `NpcStun.Tick`의 그 분기가 이미
"밧줄이 걸려 있으면 모션을 내지 않는다"로 갈라져 있으므로(줄에 눕혀진 몸은 일어날 수 없다) 래그돌
이탈도 같은 자리에서 같은 조건으로 가르는 것이 맞다.

### 2-5. 물려받는 미해결 — 부활 시 큰 회전

[ragdoll-corpse-split.md §4](ragdoll-corpse-split.md)가 **미해결**로 남긴 것: 플레이어가 누웠다가
일어날 때 몸이 **거의 180° 뒤집히며** 애니메이션으로 돌아온다. 원인 규명이 *"원격에서 애니메이터가
컬링돼(`CullUpdateTransforms`) `Animator.Update(0f)`가 뼈를 쓰지 않아 블렌드 목표를 못 만든다"*에서
멈춰 있고, `m_rootYawOffset`이 루트에 닿지 않는 이유도 아직 모른다.

기상 블렌드를 NPC에 넣으면 **아직 아무도 못 고친 문제를 NPC에서 다시 만난다.**

**다만 NPC가 유리한 점이 하나 있다 — 스킨을 끌 필요가 없다.** 플레이어에서 컬링이 걸린 원인은
`m_liveSkin.SetActive(false)`(시체 모델과 살아있는 모델이 갈려 있어서)인데, NPC는 리그가 하나라 끌
살아있는 스킨이 없다. `StopAnimator`가 이미 `SetSkinsAlwaysVisible(true)`를 부르고 있어 컬링 함정
자체를 피할 여지가 있다.

→ **NPC 쪽이 플레이어 §4의 대조군이 될 수 있다.** 같은 블렌드를 컬링 없는 조건에서 돌려 보면 "컬링이
원인"이라는 가설이 갈린다. 순서상 NPC를 먼저 하는 편이 이득일 수 있다.

### 2-6. 정해야 할 것

| | 항목 | 후보 |
|---|---|---|
| ① | 기상에 포즈 블렌드를 넣나 | (a) `RagdollPoseBlend`를 NPC에도 — 부드럽지만 §2-5를 물려받는다 (b) 블렌드 없이 즉시 애니메이터 복귀 — 한 프레임 튀지만 단순하다. **(b)로 시작해 필요하면 올리는 쪽을 권한다** |
| ② | ~~NavMesh 복구 주체~~ | **확정 — A안(`NpcRagdoll`이 되돌린다).** 근거·부산물은 §2-3-1~2-3-5 |
| ③ | 기절 경로 전부를 태우나 | `NpcStun` 오버레이(테이저·체력 0 넉다운)와 `NpcState.Stunned`(넉백 착지 KO)가 갈려 있다. 넉백 착지는 `NpcKnockback`이 이미 물리 비행을 굴리므로 **이중이 될 수 있다** |
| ④ | 짧은 기절에도 태우나 | 테이저 기절이 짧으면 눕고 일어나느라 무력화 시간이 다 간다. `StunSeconds` 실측 후 하한을 둘지 결정 |

---

## 3. 작업 순서

### 3-1. ~~조준 마스크 확인~~ — 확인 결과 필요 없었다 (§5)

~~리그를 다시 붙이기 **전에** 확인해야 한다.~~ 조준 마스크가 뼈를 잡지 않는다는 것이 확인돼
**이 선행 작업은 사라졌다.** 근거는 §5의 갱신 블록.

### 3-2. `NpcRagdoll`에 권위 판정 이식

`PlayerRagdoll`의 패턴을 그대로 미러링한다. **부활과 무관하므로 원안 그대로다.**

```csharp
// Awake — PlayerRagdoll과 같은 자동 감지
m_hipsIsNetworkSynced = m_rig.HipsBody?.GetComponent<NetworkTransform>() != null;
```

- `LateUpdate`의 `TickAlignBonesToRoot()` 호출을 `!m_hipsIsNetworkSynced`일 때만 돌게 (지금은 조건 없이
  돈다)
- `EnterRagdoll`·`Unfreeze`가 `m_rig.SetKinematic(false)`를 부른 직후, 골반이 네트워크 동기화 중이면
  비권위 피어의 골반만 다시 키네마틱으로 되돌린다(`PlayerRagdoll.ReleaseBonesToPhysics`와 같은 자리)

### 3-3. 밧줄을 권위 피어 전용으로

`NpcRagdoll.BeginRopePull`에 가드 하나:

```csharp
public void BeginRopePull(Transform carrier)
{
    if (m_hipsIsNetworkSynced && !HasMoveAuthority)
        return;
    Unfreeze();
    m_rope?.Attach(carrier);
}
```

⚠ `NpcRopeDrag.AttachCorpseRopeRpc`는 `SendTo.Everyone` 그대로 둔다 — 플레이어 때처럼 호출부를 안
건드리고 `NpcRagdoll` 안에서만 가른다. 조건이 `m_hipsIsNetworkSynced`라서, 골반 복제(3-5)를 프리팹에서
빼면 자동으로 옛 동작(전원이 묶음)으로 돌아가는 안전장치가 그대로 유지된다.

### 3-4. **기절 진입·이탈 신설** (신규)

§2 전체. 순서상 여기가 맞는 이유는 3-2·3-3이 사망 경로만 건드려 **되돌리기 쉬운 상태에서 먼저
끝나기** 때문이다. 이 단계에서 코드가 가장 많이 늘어난다.

- 진입: `PollDeath` → 사망 + 기절 (§2-1, `m_skipThisEpisode` 갈라내기 포함)
- 이탈: `RestoreBindPose` + 애니메이터 복귀 + NavMesh 재부착 — **`NpcRagdoll`이 한다** (§2-2·2-3-1)
- `NpcRopeDrag.StopRopeDrag`에 래그돌 가드 추가 (§2-3-3)
- `TickNavMeshRecovery`의 "이 상태를 만드는 셋" 주석 갱신 (§2-3-2)
- `ExitStun`을 부르는 경로 6개가 각각 어떤 시점에 오는지 확인 (§2-3-5)

### 3-5. 골반 복제를 NPC 리그에 추가

`Model/Root/Hips`(NPC 리그 골반)에 `NetworkTransform` + `NetworkRigidbody`(`UseRigidBodyForMotion`)를
추가한다. 플레이어의 `Corpse/Root/Hips`와 같은 구성.

`RagdollSetup.cs`의 NPC 경로(`rigOwnerPath = "Model"`)에 이 두 컴포넌트 부착을 자동화할지, 4개
프리팹에 수동으로 붙일지 정한다 — 자동화하면 리그를 다시 만들 때마다 안 잊는다.

### 3-6. 기상 클립 타이밍 정합

§2-4. 프리팹에 리그를 붙인 뒤에야 실제로 보이는 문제라 3-7과 왕복할 수 있다.

### 3-7. 프리팹에 리그 재부착

`RagdollRigCloner` + `RagdollSetup`을 4개 프리팹(`NPC_Citizen`/`_Generic`/`Rioter`/`Streaker`)에 다시
돌린다 — `0bd6990`에서 뗀 것과 같은 작업의 역방향. 이번엔 3-2~3-5가 반영된 상태로 진행한다.

> `NPC_Abductor`는 `0bd6990`이 건드리지 않았다 = 원래 리그가 없었다. 이번에도 뺄지 확인할 것.

---

## 4. 이슈의 "정해야 할 것" 현황

| 항목 | 상태 |
|---|---|
| ① 진입 조건 | ⚠ **뒤집혔다.** 원래는 *"이미 결정됨 — `NpcRagdoll.PollDeath`가 사망(`Die`) 하나로만 진입시킨다. 기절은 안 태운다"*였다. **기절도 태우기로 정했다** → §2 전체가 그 결과다 |
| ② 넉백과의 관계 | 미정, **그리고 더 중요해졌다.** `EnterRagdoll(Vector3 impulse)`가 임펄스 인자를 이미 받아 연결할 자리는 있다. 다만 넉백 착지 KO는 `NpcKnockback`이 물리 비행을 이미 굴리므로 래그돌과 **이중이 될 수 있다** (§2-6③) |
| ③ 시체 수명 | 유치장 배치(`NpcCustody.SendCorpseToJail` → `NpcRagdoll.ServerPlaceCorpse`)는 이미 구현돼 있다. 라운드 끝까지 유지하는지, 동시 활성 상한을 두는지는 미정 |
| ④ #567(시체 떨림) 선행 여부 | **재검토됨.** 떨림의 유력한 원인이던 것들(밧줄 발산, 뼈 길이 누적)이 플레이어 작업으로 규명됐다. §3-3(밧줄 권위화)만으로 떨림도 같이 사라질 가능성이 있어 따로 먼저 잡을 필요 없이 합칠 수 있어 보인다. ⚠ 단 **뼈 길이 누적은 이제 NPC에도 해당되므로**(§1-2) "NPC엔 그 버그가 없다"는 근거는 더 못 쓴다 |
| ⑤ **기상 블렌드 / NavMesh 복구 주체** | **신규.** §2-6 |

---

## 5. ~~조준 판정 마스크가 걸릴 수 있다~~ → 안 걸린다 (해소됨)

> ⚠ **2026-08-11 — 이 절의 전제가 틀렸다.** 아래는 `m_interactMask`의 **코드 기본값**(`~0`)을 읽고
> 쓴 것이고, `Player.prefab`의 실제 값은 `128` = `Interactable`(7) 하나다. 래그돌 뼈(레이어 10)는
> 마스크 밖이라 조준 레이에 걸리지 않고, `m_losBlockMask`도 `1`(Default)이라 가시선을 막지 않는다.
> **§3-1(리그 부착 전 선행 확인)은 필요 없다.** 대신 실제로 볼 것은 `NpcProneCollider`의 방향이다 —
> 위 「진행 상황」 절의 "밝혀진 것 둘"을 볼 것. 지우지 않고 남기는 것은 **어떤 근거가 왜 무효가
> 됐는지**가 다음 판단의 재료이기 때문이다.

이슈의 "`NpcProneCollider`(#363)와의 충돌 정리" 항목을 확인하다가 나왔다.

[`PlayerInteractor.m_interactMask`](../Assets/Scripts/Interaction/PlayerInteractor.cs#L10)가 `~0`
(전 레이어)이다. 즉 **래그돌 뼈 콜라이더(`Ragdoll` 레이어)와 `NpcProneCollider`의 몸통 캡슐이 둘 다
조준 레이에 걸릴 수 있다.**

`RagdollRig` 클래스 주석 자체가 이 문제를 플레이어 쪽에서 이미 한 번 언급한다 — 래그돌 전용 레이어를
만든 이유 중 하나가 "안 만들면 뼈가 조준 레이·가시선 판정(Default 마스크)에 걸린다"는 것인데, 그
전제는 조준 마스크가 **Default만** 본다는 것이다. `PlayerInteractor`가 실제로는 `~0`을 쓰고 있으므로,
플레이어가 이 문제를 어떻게(혹은 실제로) 피했는지 확인이 안 된 상태다.

**리그를 다시 붙이기 전에 확인할 것:**
- `Taser.EvaluateAim`·`InteractionFeedback`도 같은 마스크를 쓰는지
- 겹치는 경우 실제로 조준이 어느 쪽에 맞는지(레이캐스트 히트 순서·거리 우선인지)
- 완료 기준의 "누운 시체를 겨냥하면 조준이 맞는다"를 충족하려면 마스크에서 `Ragdoll` 레이어를 빼야
  하는지, 아니면 다른 방식으로 갈라야 하는지

⚠ **기절 래그돌이 이 항목의 무게를 키운다.** 사망만 태울 때는 "시체를 겨냥"이 부수적이었지만,
**기절한 NPC는 검거 대상이다** — 조준이 안 맞으면 기절시켜 놓고 잡을 수가 없다. 게임플레이가 막히는
경로이므로 §3-1을 건너뛰지 말 것.

---

## 6. 검증 계획

플레이어 때 썼던 것과 같은 패턴:

- MPPM 3인, `Map_Apocalypse`
- NPC를 죽이고 **부하가 걸리는 조건**(견인, 계단·경사, 실내 층 겹침)에서 관찰
- `골반↔최저뼈`·`루트↔골반수평` 같은 침하 진단(`PlayerRagdoll.TickSinkDiagnostics` 패턴)을 NPC에도
  임시로 넣어 실측
- **`뼈길이드리프트`(`RagdollRig.MaxBindPositionDrift`)를 반드시 포함한다** — §1-2에 따라 이제
  해당되므로 원래 계획에서 "뺄 수 있다"고 한 것이 뒤집혔다
- 조준 판정(상호작용 레이·테이저·윤곽선)이 누운 시체를 맞히는지 — §5
- 계단·경사·실내에서 지형을 뚫거나 허공에 굳지 않는지 (맵 밖으로 떨어진 경우의 최후 배수 포함)
- NPC 여러 체가 동시에 쓰러져도 프레임이 무너지지 않는지

### 기절 전용 (신규)

- **같은 NPC를 3회 이상 반복 기절**시키고 매번 `뼈길이드리프트`가 0인지 — §1-2의 누적은 2차부터
  드러난다. 1회만 보면 통과한다
- 기상 후 **NavMesh로 실제 복귀**하는지 (걷기 재개, `isOnNavMesh`) — §2-3의 조용한 실패를 잡는 지점
- 기상 클립이 뼈 복귀와 맞는지 (튐·중간부터 재생) — §2-4
- **NavMesh 밖에서 기상**시켰을 때 (경사에서 굴러 떨어진 뒤) 회수되는지 — `TickNavMeshRecovery`(#557)
- 기절 중 밧줄·수감·재기절이 겹칠 때 (`ExitStun` 경로 전부, §2-3)
- **기상 시 큰 회전이 나오는지** — 안 나오면 플레이어 §4의 "컬링이 원인" 가설이 강해진다 (§2-5)

---

## 관련 문서

- [571-npc-death-ragdoll.md](571-npc-death-ragdoll.md) — NPC 기절/사망 분리 + 래그돌 코드의 원래 진행 기록. §14(시체를 놓을 때 관성) 같은 알려진 이슈가 남아 있다
- [ragdoll-corpse-split.md](ragdoll-corpse-split.md) — 플레이어 쪽에서 밧줄 발산·뼈 길이 누적·T포즈 시작 버그를 잡은 기록. §7·§8이 이 문서 §1의 근거이고, **§4(부활 시 큰 회전)가 §2-5의 미해결 상속분**이다
- [ragdoll.md](ragdoll.md) — 래그돌 불변식 정본. 특히 불변식 8(밧줄 권위 전용)·10(뼈 길이는 포즈가 아니다)·11(`SyncTransforms`)
