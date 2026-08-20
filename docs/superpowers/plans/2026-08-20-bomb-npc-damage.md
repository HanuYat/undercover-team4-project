# 폭탄 폭발이 NPC에도 피해를 준다 (#768) 구현 플랜

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 추격 폭탄이 터질 때 반경 안 NPC도 플레이어와 같은 곡선으로 피해를 받아 죽고, 즉사한 시체는 폭심 반대쪽으로 날아가게 한다.

**Architecture:** `BombDevice.ServerKnockbackNpcs`(넉백 전용)를 `ServerBlastNpcs`로 넓혀, NPC마다 환경 피해를 넣은 뒤 **결과에 따라 갈린다** — 죽었으면 래그돌 임펄스, 살았으면 기존 넉백. 피해 세기·감쇠·임펄스는 플레이어 경로가 이미 쓰는 `EvaluateDamage`/`EvaluateKnockback`/`EvaluateRagdollImpulse`를 그대로 재사용하므로 새 튜닝 값이 없다. NPC는 `RagdollPoseStreamer`가 서버에서만 물리를 굴려 원격에 자세를 흘리므로 플레이어와 달리 RPC가 필요 없다.

**Tech Stack:** Unity 6000.3.15f1 · Netcode for GameObjects · NavMesh(AI Navigation)

**설계 정본:** [docs/superpowers/specs/2026-08-20-bomb-npc-damage-design.md](../specs/2026-08-20-bomb-npc-damage-design.md)

## Global Constraints

- **네이밍** — private/protected 인스턴스 멤버 `m_`, static `s_`, const `k_`, 인터페이스 `I`, 제네릭 `T`, 매개변수/지역변수 camelCase, public 프로퍼티/메서드·enum 값 PascalCase, 이벤트 `On` 접두사 (CLAUDE.md 코드 컨벤션)
- **전역 접근** — `App` 파사드만 사용. `FindFirstObjectByType` 금지, 신규 `static Instance` 금지 (architecture R1·R2). **이 플랜은 새 매니저를 만들지 않는다**
- **`?.` 금지** — Unity 오브젝트는 `!= null`로 검사한다. `?.`는 파괴된 오브젝트의 fake null을 우회한다 (`HqPanelView` 관례)
- **서버 권위** — 이 플랜이 건드리는 코드는 전부 서버(또는 오프라인) 전용 경로다. 클라이언트에서 실행되는 분기를 새로 만들지 않는다
- **서드파티** — `Assets/Imported/` 아래 수정 금지
- **주석** — 한국어, **짧게**. "왜 이렇게 했는가"만 남기고 긴 근거는 커밋 메시지로 보낸다
- **컴파일 확인** — 스크립트 수정 후 Unity Console에 에러가 없는지 확인한 뒤 다음 태스크로 넘어간다 (MCP `read_console`, `editor_state.isCompiling` 폴링)
- **Play 모드 검증은 사용자 몫** — 각 태스크는 컴파일 통과까지 책임진다

## 선행 작업 — 이 플랜 밖

**#688(`SpawnedNpcEventBase`에 사망 처리 없음)을 먼저 머지해야 한다.** 별도 이슈·브랜치이고 이 플랜에 태스크로 넣지 않는다. 그쪽이 없으면 폭발로 난동자·나체 난동꾼·소매치기가 한 번에 죽을 때 `"죽은 NPC를 Run으로 되돌리려 했다"` 에러가 대량으로 뜨고 이벤트 슬롯이 소란 수명 끝까지 점유돼, Task 2의 Play 모드 검증이 그 스팸에 묻힌다.

## 스펙에서 조정한 점

**Task 1을 "동작이 변하지 않는" 태스크로 따로 뗐다.** 스펙은 중복 제거·버퍼 확대를 `ServerBlastNpcs` 설계 안에 함께 적었지만, 둘은 **지금 코드에서도 이미 필요한 안전화**이고 피해 추가와 독립적으로 검토·되돌리기가 가능하다. 먼저 깔아 두면 Task 2가 피해를 얹을 때 중복 피격 위험이 이미 사라진 상태다.

**Task 2를 더 쪼개지 않는다.** "피해 넣기"와 "죽으면 임펄스 / 살면 넉백" 분기를 나누면, 중간 상태가 **죽은 NPC에게 넉백을 거는 코드**가 된다. 그건 `ServerApplyKnockback`이 `Dead`를 거르지 않아 시체를 `Stunned`로 되살리는 알려진 함정이다(`TrafficVehicle.ServerHitNpc` 주석). 분기는 최적화가 아니라 안전 장치이므로 피해와 원자적으로 들어가야 한다.

---

## Task 1: 폭발 대상 수집을 안전하게 만든다 (동작 불변)

**Files:**
- Modify: `Assets/Scripts/Events/Bomb/BombDevice.cs:147-148` (버퍼 선언)
- Modify: `Assets/Scripts/Events/Bomb/BombDevice.cs:622-634` (`ServerKnockbackNpcs`)

**Interfaces:**
- Consumes: 없음 (첫 태스크)
- Produces: `s_blastColliders`(크기 256), `s_blastNpcs`(`HashSet<NpcController>`) — Task 2가 그대로 쓴다

`using System.Collections.Generic;`는 파일 1행에 이미 있다. 새 `using`이 필요 없다.

- [ ] **Step 1: 버퍼 선언을 바꾼다**

`BombDevice.cs`의 아래 두 줄을

```csharp
    // NPC 넉백 대상 수집용 공유 버퍼 — 서버(또는 오프라인)에서만 쓰므로 정적으로 공유해도 안전하다
    private static readonly Collider[] s_blastColliders = new Collider[64];
```

이렇게 바꾼다:

```csharp
    // 폭발 대상 수집용 공유 버퍼 — 서버(또는 오프라인)에서만 쓰므로 정적으로 공유해도 안전하다.
    // 사람 하나가 래그돌 뼈 콜라이더 여러 개로 잡혀 64칸은 대여섯 명이면 포화된다 (#768).
    private static readonly Collider[] s_blastColliders = new Collider[256];

    // 한 폭발에서 이미 처리한 NPC — 위 버퍼가 같은 사람을 여러 번 담기 때문이다 (#768).
    private static readonly HashSet<NpcController> s_blastNpcs = new HashSet<NpcController>();
```

- [ ] **Step 2: 수집 루프에 중복 제거와 포화 경고를 넣는다**

`ServerKnockbackNpcs()` 전체를 아래로 교체한다. **넉백 동작 자체는 바뀌지 않는다** — `ServerApplyKnockback`이 이미 비행 중 중복 호출을 무시하므로, 중복 제거는 화면상 차이를 만들지 않는다.

```csharp
    // 반경 내 NPC를 폭심 반대쪽으로 날린다. 한 사람이 콜라이더 여러 개로 잡히므로 집합으로 한 번만 민다.
    private void ServerKnockbackNpcs()
    {
        int hitCount = Physics.OverlapSphereNonAlloc(transform.position, m_explosionRadius, s_blastColliders);

        // 포화는 조용히 틀린다 — 넘친 대상은 아무 일도 겪지 않는다
        if (hitCount == s_blastColliders.Length)
            Debug.LogWarning($"[폭탄] 대상 버퍼({s_blastColliders.Length}) 포화 — 일부 NPC가 누락됐을 수 있다", this);

        s_blastNpcs.Clear();
        for (int i = 0; i < hitCount; i++)
        {
            NpcController npc = s_blastColliders[i].GetComponentInParent<NpcController>();
            if (npc == null || !s_blastNpcs.Add(npc))
                continue;

            npc.Knockback.ServerApplyKnockback(EvaluateKnockback(npc.transform.position));
        }
    }
```

- [ ] **Step 3: 컴파일 확인**

Unity Console에 에러가 없는지 확인한다. MCP를 쓰면:

```
mcp__unityMCP__refresh_unity(mode="force", scope="scripts", compile="request", wait_for_ready=true)
mcp__unityMCP__read_console(action="get", types=["error"], count="20")
```

기대: 에러 0건.

- [ ] **Step 4: 커밋**

```bash
git add Assets/Scripts/Events/Bomb/BombDevice.cs
git commit -m "폭발 대상 수집에서 중복을 걸러내고 버퍼를 늘린다 (#768)"
```

---

## Task 2: 폭발이 NPC에 피해를 주고, 죽은 NPC를 날린다

**Files:**
- Modify: `Assets/Scripts/NPC/Controller/NpcController.cs:107` 부근 (부품 접근자 목록)
- Modify: `Assets/Scripts/Events/Bomb/BombDevice.cs:483-486` (호출부)
- Modify: `Assets/Scripts/Events/Bomb/BombDevice.cs` (`ServerKnockbackNpcs` → `ServerBlastNpcs`)

**Interfaces:**
- Consumes: Task 1의 `s_blastColliders`(256), `s_blastNpcs`
- Produces:
  - `NpcController.Ragdoll` → `NpcRagdoll` (읽기 전용 프로퍼티)
  - `BombDevice.ServerBlastNpcs()` → `void` (private, 서버 전용)

기존 API 중 이 태스크가 쓰는 것 (이미 존재한다 — 새로 만들지 않는다):

| 호출 | 시그니처 | 비고 |
|---|---|---|
| `npc.Health.TakeEnvironmentalDamage` | `void (int amount, GameObject attacker)` | 죽은 대상은 `CanTakeEnvironmentalDamage`가 막는다 |
| `npc.Death.IsDead` | `bool` | `CurrentState == NpcState.Dead` |
| `npc.Knockback.ServerApplyKnockback` | `void (Vector3)` | ⚠ `Dead`를 거르지 않는다 |
| `npc.Ragdoll.EnterRagdoll` | `void (Vector3 impulse)` | 멱등. 정착한 시체는 스스로 거부 |
| `EvaluateDamage` | `int (Vector3 targetPosition)` | 기존 private 메서드 |
| `EvaluateKnockback` | `Vector3 (Vector3 targetPosition)` | 기존 private 메서드 |
| `EvaluateRagdollImpulse` | `Vector3 (Vector3 targetPosition)` | 기존 private 메서드 |

- [ ] **Step 1: `NpcController`에 래그돌 접근자를 연다**

`m_ragdoll`은 이미 135행에서 캐싱된다. 공개 접근자만 없다. `Penalty`와 `Reaction` 사이(알파벳 순서)에 넣는다:

```csharp
    /// <summary>특수 임무 — 오검거·납치·소매치기의 수용·추격·수렴·호송 (#277~#279/#371/#303)</summary>
    public NpcDutyAgent Penalty => m_penalty;
    /// <summary>래그돌 — 리그가 없는 프리팹에서는 null이다 (#571/#768)</summary>
    public NpcRagdoll Ragdoll => m_ragdoll;
    /// <summary>검거 반응 — 위협 대상·도주·저항·스윙 (#76/#205/#213/#220)</summary>
    public NpcReaction Reaction => m_reaction;
```

- [ ] **Step 2: `ServerKnockbackNpcs`를 `ServerBlastNpcs`로 바꾼다**

Task 1에서 만든 메서드 전체를 아래로 교체한다:

```csharp
    // 반경 내 NPC에 피해를 주고, 죽으면 시체를 날리고 살아 있으면 넉백만 건다.
    // 한 사람이 콜라이더 여러 개로 잡히므로 집합으로 한 번만 처리한다.
    private void ServerBlastNpcs()
    {
        int hitCount = Physics.OverlapSphereNonAlloc(transform.position, m_explosionRadius, s_blastColliders);

        // 포화는 조용히 틀린다 — 넘친 대상은 아무 일도 겪지 않는다
        if (hitCount == s_blastColliders.Length)
            Debug.LogWarning($"[폭탄] 대상 버퍼({s_blastColliders.Length}) 포화 — 일부 NPC가 누락됐을 수 있다", this);

        s_blastNpcs.Clear();
        for (int i = 0; i < hitCount; i++)
        {
            NpcController npc = s_blastColliders[i].GetComponentInParent<NpcController>();
            if (npc == null || !s_blastNpcs.Add(npc))
                continue;

            Vector3 position = npc.transform.position;
            int damage = EvaluateDamage(position);
            if (damage <= 0)
                continue;

            // 환경 피해로 넣는다 — TakeDamage의 게이트는 연행 중을 막는다 (차량과 같은 이유, #690)
            npc.Health.TakeEnvironmentalDamage(damage, gameObject);

            // ⚠ 죽은 NPC에 넉백을 걸면 시체가 Stunned로 되살아난다 — 이 분기가 그 방지다
            if (npc.Death.IsDead)
            {
                if (npc.Ragdoll != null)
                    npc.Ragdoll.EnterRagdoll(EvaluateRagdollImpulse(position));
            }
            else
            {
                npc.Knockback.ServerApplyKnockback(EvaluateKnockback(position));
            }
        }
    }
```

`?.`를 쓰지 않는 것에 주의한다 (Global Constraints).

- [ ] **Step 3: 호출부를 바꾼다**

`ServerExplode()` 안의 아래 세 줄을

```csharp
        // 반경 내 NPC 넉백 — 서버 권위. 플레이어와 달리 NPC 이동은 서버의 NavMeshAgent가 쥐고
        // 클라는 NetworkTransform으로 결과만 받으므로, 뷰가 아니라 여기서 직접 날린다.
        ServerKnockbackNpcs();
```

이렇게 바꾼다:

```csharp
        // 반경 내 NPC 피해·넉백 — 서버 권위. 플레이어와 달리 RPC가 없다: 시체 자세는
        // RagdollPoseStreamer가 서버에서만 굴려 원격에 흘린다(PoseAuthority.Server).
        ServerBlastNpcs();
```

- [ ] **Step 4: 컴파일 확인**

```
mcp__unityMCP__refresh_unity(mode="force", scope="scripts", compile="request", wait_for_ready=true)
mcp__unityMCP__read_console(action="get", types=["error"], count="20")
```

기대: 에러 0건. `NpcController.Ragdoll`이 새로 생겼으므로 `Assembly-CSharp`에 실제로 실렸는지도 확인한다:

```
mcp__unityMCP__execute_code(action="execute", code=
  "var t = System.AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == \"Assembly-CSharp\").GetType(\"NpcController\");
   return \"Ragdoll=\" + (t.GetProperty(\"Ragdoll\") != null);")
```

기대: `Ragdoll=True`.

- [ ] **Step 5: 커밋**

```bash
git add Assets/Scripts/Events/Bomb/BombDevice.cs Assets/Scripts/NPC/Controller/NpcController.cs
git commit -m "폭발이 NPC에도 피해를 주고 죽은 NPC를 날린다 (#768)"
```

---

## Task 3: 문서를 실제 동작에 맞춘다

**Files:**
- Modify: `docs/GDD.md:202` (6-4 돌발 이벤트 표)
- Modify: `docs/GDD.md:235` (추격 폭탄 설명 문단)
- Modify: `docs/GDD.md` (결정 노트 추가 — 235행 문단 바로 뒤)
- Modify: `Assets/Scripts/Traffic/TrafficVehicle.cs:405-406` 부근 (낡은 주석 정정)

**Interfaces:**
- Consumes: Task 2가 만든 동작
- Produces: 없음 (문서 전용)

- [ ] **Step 1: 6-4 표의 결과 문구를 고친다**

`docs/GDD.md` 202행에서 `제한시간이 끝나면 그 자리에서 폭발(피해·주변 넉백)` 을
`제한시간이 끝나면 그 자리에서 폭발(반경 안 사람·NPC에 피해, 살아남으면 넉백)` 으로 바꾼다.

- [ ] **Step 2: 235행 문장을 고친다**

`제한시간이 끝나면 반드시 폭발해 반경 안 플레이어에게 피해를 주고 주변 NPC를 넉백시킨다.` 를
아래로 바꾼다:

```
제한시간이 끝나면 반드시 폭발해 **반경 안의 플레이어와 NPC 모두에게 같은 곡선으로 피해를 준다** — 즉사하면 시체가 폭심 반대쪽으로 날아가고, 살아남으면 넉백만 받는다.
```

- [ ] **Step 3: 결정 노트를 단다**

235행이 속한 문단(`> **해체는 없다.** ...`) 바로 뒤에 인용 블록으로 넣는다:

```
> **NPC도 폭발에 죽는다 (2026-08-20 확정, #768).** 예전에는 반경 안 NPC가 넉백만 받아, 시민 무리 한가운데서 터져도 아무도 죽지 않았다. **새 규칙이 아니라 9장의 환경 피해 일반 규칙(위 383행)에 편입되는 것이다** — 폭발로 죽어 그대로 남은 시민은 개인 기록에도 팀 카운트에도 오르지 않고, 시체를 유치장까지 끌고 가면 인계로 판정된다(384행). 차에 치인 시민과 같은 취급이고, 애초에 그 조항이 폭탄을 명시하고 있었으므로 이번 변경은 **규칙이 이미 가리키던 상황을 실제로 만들어 준 것**에 가깝다. 세기는 플레이어와 같은 곡선이라 폭심 근처는 즉사하고 가장자리는 부상에 그친다. **반응은 유발하지 않는다** — 6-2 단서(171행)대로 환경 피해는 도주·저항의 트리거가 아니다.
```

- [ ] **Step 4: `TrafficVehicle`의 낡은 주석을 고친다**

`Assets/Scripts/Traffic/TrafficVehicle.cs`에서 아래 한 줄을

```csharp
    // 사망 처리(NpcDeath.ServerEnterDead ①)가 비행을 스스로 끊는다 — 즉 <b>죽는 시민은 그 자리에
    // 무너진다.</b> 시체를 날리는 임펄스는 폭발(#506)과 같은 전 피어 통로가 필요해 여기서는 걸지 않는다.
```

이렇게 바꾼다:

```csharp
    // 사망 처리(NpcDeath.ServerEnterDead ①)가 비행을 스스로 끊는다 — 즉 <b>죽는 시민은 그 자리에
    // 무너진다.</b> 시체 임펄스는 여기서 걸지 않는다.
    // ⚠ "전 피어 통로가 필요해서"라던 옛 근거는 #728 이후 사실이 아니다 — NPC 시체 자세는
    //   RagdollPoseStreamer가 서버에서만 굴려 흘리므로 서버 임펄스 하나면 된다 (폭탄이 그렇게 한다, #768).
```

- [ ] **Step 5: 컴파일 확인**

주석만 바꿨지만 파일이 재컴파일되므로 에러가 없는지 확인한다.

```
mcp__unityMCP__read_console(action="get", types=["error"], count="20")
```

기대: 에러 0건.

- [ ] **Step 6: 커밋**

```bash
git add docs/GDD.md Assets/Scripts/Traffic/TrafficVehicle.cs
git commit -m "GDD 6-4를 NPC 폭발 피해에 맞추고 차량의 낡은 주석을 고친다 (#768)"
```

---

## 사용자 검증 (Play 모드 — 태스크 밖)

세 태스크가 모두 컴파일을 통과한 뒤, `Map_Apocalypse` 호스트 + MPPM 클라이언트 1명으로 확인한다.

1. 시민 무리 한가운데서 폭발 → 폭심 3.3m 안 즉사·비행 / 6m까지 넉다운 / 그 밖 부상
2. **원격 클라이언트에서 시체 비행이 호스트와 같게 보인다** — `PoseAuthority.Server` 전제가 맞는지가 여기서 갈린다. **이 플랜에서 가장 위험한 지점이다.** 어긋나면 플레이어처럼 RPC 통로가 필요하다는 뜻이고, 설계로 돌아가야 한다
3. 밧줄로 끌던 신병이 폭발에 죽는다 → 밧줄이 끊기고 시체가 그 자리에 남는다
4. 진범을 폭발로 죽인 뒤 시체를 유치장까지 끌고 가 수감 → 현상금 정상 지급
5. 폭발 직후 콘솔 에러 0건 — 특히 `"죽은 NPC를 Run으로 되돌리려 했다"`가 없어야 한다 (#688 선행 필요)
6. 반경 안 NPC가 도주·저항으로 돌변하지 않는다
7. NPC 6명 이상이 겹친 자리에서 폭발 → 버퍼 포화 경고가 뜨는지, 뜬다면 누락이 있는지
