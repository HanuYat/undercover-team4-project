# NPC 래그돌 재적용 계획 (#572)

> [이슈 #572](https://github.com/hyunjin0814/undercover-team4-project/issues/572) — "NPC에도 사망 래그돌
> 적용, 플레이어 리그(#506) 재사용, NavMesh·서버 권한만 새로."
>
> 이 문서는 **작업 전 계획**이다. NPC 래그돌 코드(`NpcRagdoll.cs`)와 에디터 도구(`RagdollSetup`·
> `RagdollRigCloner`)는 #571 작업 중에 이미 만들어져 있었지만, 플레이어 쪽(#506→#571→corpse-split)에서
> 뒤늦게 잡은 버그 셋을 반영하지 않은 채였다. [PR #599](https://github.com/hyunjin0814/undercover-team4-project/pull/599)에서
> NPC 프리팹의 리그를 일단 떼어냈고(`0bd6990`), 이 문서는 **무엇을 반영해서 다시 붙일지**를 정리한다.

---

## 0. 결론 — 이슈 본문이 시사하는 것보다 작업량이 작다

이슈의 "할 일" 대부분(`NpcRagdoll` 신규, NavMesh 처리, Animator 역할 분리, 서버 권위, 얼림 메커니즘)은
**#571에서 이미 끝나 있고 코드도 남아 있다.** 남은 것은 플레이어 쪽 버그 수정 세 개를 NPC에도
반영해야 하는지 판단하는 것뿐이고, 그중 실제로 할 일이 있는 건 하나뿐이다.

| | 플레이어 쪽 수정 | NPC에 필요한가 |
|---|---|---|
| ① `Physics.SyncTransforms()` | `SetKinematic(false)` 앞에 추가 (T포즈 시작 방지) | **이미 적용됨.** `RagdollRig.SetKinematic`(공유 코드)에 들어 있어 `NpcRagdoll`도 자동으로 받는다. 손댈 것 없음 |
| ② 뼈 길이 드리프트 방지 (`RagdollPose.Copy` + `RestoreBindPose`) | 살아있는 리그 ↔ 시체 리그를 오가며 뼈 길이가 누적되던 버그 | **해당 없음.** 아래 §1-2 |
| ③ 밧줄 권위 전용 + 골반 복제 | 전 피어가 각자 밧줄을 묶어 발산하던 버그 | **포팅 필요.** 아래 §1-3 |

---

## 1. 왜 이렇게 갈리는지

### 1-1. ① SyncTransforms — 공짜

`RagdollRig.SetKinematic(bool)`은 플레이어와 NPC가 **같은 코드**를 쓴다(`RagdollRig`는 네트워크·권위·
이동 프록시를 모르는 순수 물리 컴포넌트라는 것이 애초에 #506의 설계다). 그 안에 넣은
`Physics.SyncTransforms()` 호출은 호출부와 무관하게 걸리므로, NPC가 `EnterRagdoll`이나 `Unfreeze`에서
`m_rig.SetKinematic(false)`를 부르는 순간 이미 적용된다.

### 1-2. ② 뼈 길이 드리프트 — NPC 구조상 발생하지 않는다

이 버그는 정확히 두 조건이 겹쳐야 나온다:

1. **살아있는 리그와 시체 리그가 별도 오브젝트**라 `RagdollPose.Copy`로 포즈를(그리고 예전엔 뼈
   길이까지) 왕복 복사한다
2. **부활해서 같은 리그를 다시 쓴다** — 그래서 누적된다

NPC는 둘 다 없다:

- 리그가 하나뿐이다. 죽으면 그 자리에서 `m_animator.enabled = false`로 애니메이터만 끄고, 별도
  시체 모델로 바꿔치기하지 않는다(`NpcRagdoll.StopAnimator`). `RagdollPose.Copy`를 아예 안 쓴다
- 부활이 없다. NPC는 죽으면 끝이고, 라운드가 바뀌면 그 인스턴스 자체가 사라진다 —
  [`NpcSpawner.ResetSpawnState`](../Assets/Scripts/NPC/NpcSpawner.cs#L132)가 `Destroy(npc.gameObject)`,
  다음 라운드는 [`Instantiate`](../Assets/Scripts/NPC/NpcSpawner.cs#L188)로 새로 만든다

그래서 뼈 길이가 리그 사이를 오갈 경로 자체가 없다. 포팅할 코드가 없다.

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

## 2. 작업 순서

### 2-1. 골반 복제를 NPC 리그에 추가

`Model/Root/Hips`(NPC 리그 골반)에 `NetworkTransform` + `NetworkRigidbody`(`UseRigidBodyForMotion`)를
추가한다. 플레이어의 `Corpse/Root/Hips`와 같은 구성.

`RagdollSetup.cs`의 NPC 경로(`rigOwnerPath = "Model"`)에 이 두 컴포넌트 부착을 자동화할지, 4개
프리팹에 수동으로 붙일지 정한다 — 자동화하면 리그를 다시 만들 때마다 안 잊는다.

### 2-2. `NpcRagdoll`에 권위 판정 이식

`PlayerRagdoll`의 패턴을 그대로 미러링한다:

```csharp
// Awake — PlayerRagdoll과 같은 자동 감지
m_hipsIsNetworkSynced = m_rig.HipsBody?.GetComponent<NetworkTransform>() != null;
```

- `LateUpdate`의 `TickAlignBonesToRoot()` 호출을 `!m_hipsIsNetworkSynced`일 때만 돌게 (지금은 조건 없이
  돈다)
- `EnterRagdoll`·`Unfreeze`가 `m_rig.SetKinematic(false)`를 부른 직후, 골반이 네트워크 동기화 중이면
  비권위 피어의 골반만 다시 키네마틱으로 되돌린다(`PlayerRagdoll.ReleaseBonesToPhysics`와 같은 자리)

### 2-3. 밧줄을 권위 피어 전용으로

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
건드리고 `NpcRagdoll` 안에서만 가른다. 조건이 `m_hipsIsNetworkSynced`라서, 골반 복제(2-1)를 프리팹에서
빼면 자동으로 옛 동작(전원이 묶음)으로 돌아가는 안전장치가 그대로 유지된다.

### 2-4. 프리팹에 리그 재부착

`RagdollRigCloner` + `RagdollSetup`을 4개 프리팹(`NPC_Citizen`/`_Generic`/`Rioter`/`Streaker`)에 다시
돌린다 — `0bd6990`에서 뗀 것과 같은 작업의 역방향. 이번엔 2-1~2-3이 반영된 상태로 진행한다.

### 2-5. 조준 마스크 확인 — §4 참고

리그를 다시 붙이기 **전에** 확인해야 한다. 아래 §4.

---

## 3. 이슈의 "정해야 할 것" 현황

| 항목 | 상태 |
|---|---|
| ① 진입 조건 | **이미 결정됨.** `NpcRagdoll.PollDeath`가 사망(`Die`) 하나로만 진입시킨다. 기절은 안 태운다 |
| ② 넉백과의 관계 | 미정. `EnterRagdoll(Vector3 impulse)`가 임펄스 인자를 이미 받게 설계돼 있어 넉백 경로를 연결할 자리는 있다. 연결 여부만 결정하면 된다 |
| ③ 시체 수명 | 유치장 배치(`NpcCustody.SendCorpseToJail` → `NpcRagdoll.ServerPlaceCorpse`)는 이미 구현돼 있다. 라운드 끝까지 유지하는지, 동시 활성 상한을 두는지는 미정 |
| ④ #567(시체 떨림) 선행 여부 | **재검토됨.** 떨림의 유력한 원인이던 것들(밧줄 발산, 뼈 길이 누적)이 이번 플레이어 작업으로 규명됐다. NPC는 애초에 뼈 길이 누적 버그가 없으므로(§1-2), §2-3(밧줄 권위화)만으로 떨림도 같이 사라질 가능성이 있다 — 따로 먼저 잡을 필요 없이 이 작업에 합칠 수 있어 보인다 |

---

## 4. 새로 발견한 것 — 조준 판정 마스크가 걸릴 수 있다

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

---

## 5. 검증 계획

플레이어 때 썼던 것과 같은 패턴:

- MPPM 3인, `Map_Apocalypse`
- NPC를 죽이고 **부하가 걸리는 조건**(견인, 계단·경사, 실내 층 겹침)에서 관찰
- `골반↔최저뼈`·`루트↔골반수평` 같은 침하 진단(`PlayerRagdoll.TickSinkDiagnostics` 패턴)을 NPC에도
  임시로 넣어 실측 — `뼈길이드리프트`는 §1-2에 따라 뺄 수 있다
- 조준 판정(상호작용 레이·테이저·윤곽선)이 누운 시체를 맞히는지 — §4
- 계단·경사·실내에서 지형을 뚫거나 허공에 굳지 않는지 (맵 밖으로 떨어진 경우의 최후 배수 포함)
- NPC 여러 체가 동시에 쓰러져도 프레임이 무너지지 않는지

---

## 관련 문서

- [571-npc-death-ragdoll.md](571-npc-death-ragdoll.md) — NPC 기절/사망 분리 + 래그돌 코드의 원래 진행 기록. §14(시체를 놓을 때 관성) 같은 알려진 이슈가 남아 있다
- [ragdoll-corpse-split.md](ragdoll-corpse-split.md) — 플레이어 쪽에서 밧줄 발산·뼈 길이 누적·T포즈 시작 버그를 잡은 기록. §7·§8이 이 문서 §1의 근거다
- [ragdoll.md](ragdoll.md) — 래그돌 불변식 정본. 특히 불변식 8(밧줄 권위 전용)·10(뼈 길이는 포즈가 아니다)·11(`SyncTransforms`)
