# 클라이언트 검거 서버 권위화 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 접속한 클라이언트도 호스트와 동일하게 검거(수갑 채널링·연행·근접 제압)를 수행할 수 있게, 검거 액션 전 과정을 클라 입력 → ServerRpc → 서버 실행 경로로 서버 권위화한다.

**Architecture:** 플레이어별 `PlayerEscorter`를 `NetworkBehaviour`로 승격해 검거 요청 허브로 삼는다. 오너 클라의 아이템/상호작용(`Handcuffs`, `NpcSubdueInteractable`)은 가드된 `NpcController` 메서드를 직접 부르지 않고 `PlayerEscorter`의 요청 API만 호출한다. `PlayerEscorter`는 `[Rpc(SendTo.Server)]`로 서버에 요청을 넘기고, 서버가 채널링 타이머·사거리·반응 판정을 실행한 뒤 서버 컨텍스트에서 `NpcController`의 서버 권위 메서드를 호출한다. 결과 상태는 기존 `NetworkVariable<NpcState>` 동기화로 전 피어에 반영된다.

**Tech Stack:** Unity 6000.3.15f1, Netcode for GameObjects (NGO 2.x 유니버설 RPC `[Rpc(SendTo.Server)]`), UniTask, NavMesh.

## Global Constraints

- 네이밍 컨벤션 (CLAUDE.md): `m_` private 인스턴스, `s_` static, `k_` const, `I` 인터페이스, `On` 이벤트, public 메서드/프로퍼티 PascalCase, 지역/매개변수 camelCase.
- 한국어 주석 충실히 (리뷰어 가독성).
- FSM 전이는 서버 권위 — 모든 `NpcController` 상태 변경 메서드는 `if (IsSpawned && !IsServer) return;` 가드 유지. 이 계획은 그 메서드를 **서버 컨텍스트에서** 호출하도록 진입 경로만 바꾼다. 가드 자체는 건드리지 않는다.
- 네트워크로 넘기는 대상 참조는 `Transform`이 아니라 `NetworkObjectReference`로 전달한다.
- 검증은 Multiplayer Play Mode(가상 플레이어 1 + 호스트) + Unity Console 로그로 한다. (이 저장소엔 유닛 테스트 인프라가 없고, NGO RPC 흐름은 실플레이 검증이 정석) 각 태스크는 컴파일 클린 확인 후 커밋한다.
- 오프라인(네트워크 세션 없이 단독 Play) 폴백 동작을 깨지 않는다 — `IsSpawned==false`일 때 기존처럼 로컬에서 즉시 실행되어야 한다.

---

## File Structure

- `Assets/Scripts/Player/PlayerEscorter.cs` (수정, `MonoBehaviour` → `NetworkBehaviour`): 플레이어별 검거/연행 서버 권위 허브. 오너 요청 API + `[Rpc(SendTo.Server)]` + 서버 채널링/사거리/반응/연행 실행. 채널링·사거리 설정값(`m_channelSeconds`, `m_captureRange`)을 여기로 이관.
- `Assets/Scripts/Item/Handcuffs.cs` (수정): 오너 클라에서 조준 대상 해석 + `PlayerEscorter` 요청 호출로 축소. 로컬 채널링/`NpcController` 직접 호출/반응 판정 제거.
- `Assets/Scripts/NPC/NpcSubdueInteractable.cs` (수정): 도주 NPC 근접 제압을 `PlayerEscorter.RequestSubdueCapture` 경유로 라우팅. (저항 타격은 기존 `NpcController.RequestSubdueHit` 유지 — 이미 RPC 경로 있음)
- `Assets/Prefabs/Player.prefab`, `Assets/Prefabs/Player_Handcuffs_Test.prefab` (에디터 수정): `PlayerEscorter`가 `NetworkBehaviour`가 되며 필요한 인스펙터 값(채널 시간·사거리) 세팅 확인.

**아키텍처 결정 근거:** 채널링과 연행은 본질적으로 플레이어별 상태다. 요청을 플레이어 소유 `NetworkObject`(=플레이어 본체)의 `PlayerEscorter`에서 `[Rpc(SendTo.Server)]`로 보내면, 오너→서버 전송이라 소유권 문제 없이 안전하고, 서버는 `this.transform`으로 요청자의 서버측 위치를 그대로 얻어 사거리를 검증할 수 있다. `NpcController`는 순수 서버 권위 상태 타깃으로 유지된다.

---

## Task 1: PlayerEscorter를 NetworkBehaviour로 승격 (동작 동일 유지)

리팩터의 발판. 이 태스크만으로는 동작이 바뀌지 않아야 한다(호스트 여전히 동작, 클라 여전히 안 됨). 이후 태스크에서 경로를 실제로 바꾼다.

**Files:**
- Modify: `Assets/Scripts/Player/PlayerEscorter.cs`

**Interfaces:**
- Produces: `class PlayerEscorter : NetworkBehaviour`, 기존 `public NpcController EscortingNpc { get; }`, `public bool IsEscorting { get; }`, `public void StartEscort(NpcController npc)`, `public void Release()` 시그니처 그대로 유지.

- [ ] **Step 1: using·베이스 클래스 교체**

`Assets/Scripts/Player/PlayerEscorter.cs` 상단과 클래스 선언:

```csharp
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 플레이어의 검거·연행 서버 권위 허브. (#59, #56 네트워크 전환)
/// 오너 클라의 아이템/상호작용이 이 컴포넌트의 요청 API를 호출하면,
/// 요청을 서버로 넘겨(ServerRpc) 서버가 채널링·사거리·반응 판정을 실행한다.
/// 한 번에 1명만 연행 가능 (동시 1명 제약).
/// </summary>
public class PlayerEscorter : NetworkBehaviour
{
```

- [ ] **Step 2: Update()의 참조 정리 로직을 서버 전용으로 가드**

기존 `Update()`는 연행 대상이 스스로 풀렸을 때 참조를 정리한다. `EscortingNpc`가 이제 서버 권위 상태이므로 서버(또는 오프라인)에서만 정리한다:

```csharp
    private void Update()
    {
        // 참조 정리는 서버(또는 오프라인)에서만 — 연행 상태 자체가 서버 권위다 (#56)
        if (IsSpawned && !IsServer)
            return;

        // 거리 이탈 등으로 NPC 쪽에서 연행이 스스로 풀린 경우 참조를 정리한다
        if (EscortingNpc != null && EscortingNpc.CurrentState != NpcState.Escorted)
            EscortingNpc = null;
    }
```

- [ ] **Step 3: 컴파일 확인**

MCP: `refresh_unity(compile=request)` 후 `read_console(types=[Error])` — Expected: 에러 0.
(수동: Unity로 포커스 전환해 컴파일, Console 에러 0 확인)

- [ ] **Step 4: 프리팹 확인**

`Player.prefab`·`Player_Handcuffs_Test.prefab`의 `PlayerEscorter` 컴포넌트가 `NetworkBehaviour` 승격 후에도 Missing 없이 붙어 있는지 에디터에서 확인. (같은 GameObject의 다른 스크립트 참조가 깨지지 않아야 함)

- [ ] **Step 5: 커밋**

```bash
git add Assets/Scripts/Player/PlayerEscorter.cs
git commit -m "refactor: PlayerEscorter를 NetworkBehaviour로 승격 (검거 서버 권위화 준비, #<ISSUE>)"
```

---

## Task 2: PlayerEscorter에 검거 요청 API + 서버 RPC + 서버 채널링 추가

**Files:**
- Modify: `Assets/Scripts/Player/PlayerEscorter.cs`

**Interfaces:**
- Consumes: `NpcController` 서버 권위 메서드 — `StartEscort(Transform)`, `StopEscort()`, `StartFlee(Transform)`, `StartResist()`, `CaptureBySubdue()`, `CurrentState`, `IsSpawned`, `IsServer`; `CitizenIdentity.Reaction`; `ReactionType`.
- Produces (오너 클라가 호출):
  - `public void RequestCapture(NpcController target)` — 수갑 채널링/즉시 재연행 진입점.
  - `public void CancelCapture()` — 채널링 취소(이동·뗌).
  - `public void RequestRelease()` — 연행 놓기.
  - `public void RequestSubdueCapture(NpcController target)` — 도주 NPC 근접 제압.
- 기존 `StartEscort`/`Release`는 서버 내부용으로 유지(서버 로직이 호출). 오프라인 폴백에서도 그대로 쓰인다.

- [ ] **Step 1: 필드·상수 추가**

클래스 상단 필드부에 채널링 설정과 서버측 상태를 추가한다 (기존 `EscortingNpc` 프로퍼티 위/아래 적절히):

```csharp
    [Header("수갑 채널링 (서버 권위)")]
    [Tooltip("체포 채널링 시간(초)")]
    [SerializeField] private float m_channelSeconds = 3f;
    [Tooltip("채널링 시작/완료 시 대상이 이 거리(m)를 벗어나면 실패")]
    [SerializeField] private float m_captureRange = 2.5f;

    // 서버에서 진행 중인 채널링 취소 토큰 — 오너가 이동/뗌으로 취소하거나 대상이 사라지면 끊는다
    private System.Threading.CancellationTokenSource m_channelCts;
    private bool m_isChanneling; // 서버 기준 채널링 진행 여부(중복 시작 방지)
```

using에 추가:

```csharp
using System;
using System.Threading;
using Cysharp.Threading.Tasks;
```

- [ ] **Step 2: 오너 요청 API (클라 → 서버 진입점) 추가**

```csharp
    // ---- 오너 클라 진입점 (아이템/상호작용이 호출) ----

    /// <summary>체포 시도 — 오너가 호출. 오프라인이면 즉시 서버 로직을, 네트워크면 서버로 요청을 넘긴다.</summary>
    public void RequestCapture(NpcController target)
    {
        if (target == null)
            return;

        if (!IsSpawned) // 오프라인 폴백 — 단독 Play
        {
            ServerBeginCapture(target);
            return;
        }
        if (!IsOwner)
            return; // 남의 플레이어 오브젝트에서 온 호출 방지

        CaptureRequestRpc(new NetworkObjectReference(target.NetworkObject));
    }

    /// <summary>채널링 취소 — 오너가 호출(이동·뗌 등).</summary>
    public void CancelCapture()
    {
        if (!IsSpawned) { ServerCancelCapture(); return; }
        if (!IsOwner) return;
        CancelCaptureRpc();
    }

    /// <summary>연행 놓기 — 오너가 호출.</summary>
    public void RequestRelease()
    {
        if (!IsSpawned) { Release(); return; }
        if (!IsOwner) return;
        ReleaseRpc();
    }

    /// <summary>도주 NPC 근접 제압 — 오너가 호출.</summary>
    public void RequestSubdueCapture(NpcController target)
    {
        if (target == null) return;
        if (!IsSpawned) { ServerSubdueCapture(target); return; }
        if (!IsOwner) return;
        SubdueCaptureRpc(new NetworkObjectReference(target.NetworkObject));
    }
```

- [ ] **Step 3: 서버 RPC 추가**

```csharp
    // ---- 서버 RPC (오너 → 서버) ----
    // NGO 2.x 유니버설 RPC: 오너가 자기 플레이어 오브젝트에서 서버로 보내므로 소유권 문제 없음

    [Rpc(SendTo.Server)]
    private void CaptureRequestRpc(NetworkObjectReference targetRef)
    {
        if (targetRef.TryGet(out NetworkObject targetObj) &&
            targetObj.TryGetComponent(out NpcController target))
        {
            ServerBeginCapture(target);
        }
    }

    [Rpc(SendTo.Server)]
    private void CancelCaptureRpc() => ServerCancelCapture();

    [Rpc(SendTo.Server)]
    private void ReleaseRpc() => Release();

    [Rpc(SendTo.Server)]
    private void SubdueCaptureRpc(NetworkObjectReference targetRef)
    {
        if (targetRef.TryGet(out NetworkObject targetObj) &&
            targetObj.TryGetComponent(out NpcController target))
        {
            ServerSubdueCapture(target);
        }
    }
```

- [ ] **Step 4: 서버 실행 로직 (채널링·사거리·반응) 추가**

```csharp
    // ---- 서버 실행 (권위) ----

    /// <summary>체포 진입 — 대상 상태에 따라 즉시 연행 또는 채널링 시작. 서버(또는 오프라인)에서만 실행.</summary>
    private void ServerBeginCapture(NpcController target)
    {
        if (m_isChanneling)
            return; // 중복 채널링 방지
        if (IsEscorting)
        {
            // 이미 연행 중이면 이번 입력은 "놓기"
            Release();
            return;
        }
        if (target.CurrentState == NpcState.Escorted)
            return; // 이미(타인이) 연행 중 — 가로채기 방지
        if (!IsInRange(target))
            return; // 사거리 밖이면 시작조차 안 함

        // 이미 체포되어 멈춰 있는 대상은 채널링 없이 즉시 재연행
        if (target.CurrentState == NpcState.Captured)
        {
            StartEscort(target);
            return;
        }

        ServerChannelAsync(target).Forget();
    }

    private async UniTaskVoid ServerChannelAsync(NpcController target)
    {
        m_isChanneling = true;
        m_channelCts = new CancellationTokenSource();
        try
        {
            await UniTask.Delay(TimeSpan.FromSeconds(m_channelSeconds), cancellationToken: m_channelCts.Token);

            if (target == null || !IsInRange(target))
            {
                Debug.Log("구속 실패 — 대상이 범위를 벗어남");
                return;
            }

            // 채널링 성공 순간 반응 판정 (GDD 6-1, #76)
            ReactionType reaction = ResolveReaction(target);
            switch (reaction)
            {
                case ReactionType.Flee:
                    Debug.Log($"체포 실패 — 뿌리치고 도주: {target.name}");
                    target.StartFlee(transform); // 이 플레이어(서버측 transform)로부터 도주
                    break;
                case ReactionType.Resist:
                    Debug.Log($"체포 실패 — 저항 시작: {target.name}");
                    target.StartResist();
                    break;
                default:
                    Debug.Log($"NPC 구속됨: {target.name}");
                    StartEscort(target); // 체포 성공 → 이 플레이어를 따라 연행
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            Debug.Log("구속 취소됨");
        }
        finally
        {
            m_isChanneling = false;
            m_channelCts?.Dispose();
            m_channelCts = null;
        }
    }

    private void ServerCancelCapture() => m_channelCts?.Cancel();

    /// <summary>도주 NPC 근접 제압 — 서버 실행. 도주 중일 때만 그 자리에서 체포.</summary>
    private void ServerSubdueCapture(NpcController target)
    {
        if (target.CurrentState == NpcState.Run)
            target.CaptureBySubdue();
    }

    private bool IsInRange(NpcController target)
    {
        return (target.transform.position - transform.position).sqrMagnitude
            <= m_captureRange * m_captureRange;
    }

    /// <summary>채널링 성공 순간의 반응. 기절 중이거나 신원이 없으면 순응(즉시 연행). (#76)</summary>
    private static ReactionType ResolveReaction(NpcController target)
    {
        if (target.CurrentState == NpcState.Stunned)
            return ReactionType.Compliant;

        CitizenIdentity identity = target.GetComponent<CitizenIdentity>();
        return identity != null ? identity.Reaction : ReactionType.Compliant;
    }
```

- [ ] **Step 5: 기존 StartEscort의 서버 가드 유지 확인**

`StartEscort`는 서버 로직(ServerChannelAsync)에서 불리므로 서버 컨텍스트다. 내부에서 부르는 `npc.StartEscort(transform)`의 `NpcController.StartEscort`는 자체 `!IsServer` 가드가 있어 안전. `EscortingNpc = npc;` 대입도 서버에서만 일어난다. 별도 수정 없이 그대로 둔다.

- [ ] **Step 6: 채널링 정리 — 파괴/디스폰 시 취소**

```csharp
    public override void OnNetworkDespawn()
    {
        ServerCancelCapture();
    }

    private void OnDestroy()
    {
        m_channelCts?.Cancel();
        m_channelCts?.Dispose();
    }
```

- [ ] **Step 7: 컴파일 확인**

MCP: `refresh_unity(compile=request)` → `read_console(types=[Error])` — Expected: 에러 0.

- [ ] **Step 8: 커밋**

```bash
git add Assets/Scripts/Player/PlayerEscorter.cs
git commit -m "feat: 검거 채널링·연행을 PlayerEscorter 서버 RPC로 서버 권위화 (#<ISSUE>)"
```

---

## Task 3: Handcuffs를 서버 요청 경로로 전환

**Files:**
- Modify: `Assets/Scripts/Item/Handcuffs.cs`

**Interfaces:**
- Consumes: `PlayerEscorter.RequestCapture(NpcController)`, `PlayerEscorter.RequestRelease()`, `PlayerEscorter.CancelCapture()`, `PlayerEscorter.IsEscorting`.

- [ ] **Step 1: 로컬 채널링·NpcController 직접 호출 제거, 요청 호출로 교체**

`Use`를 다음으로 교체한다. (로컬 `RestrainAsync`/`ResolveReaction`/`IsInRange`/`m_cts`/`m_isRestraining` 및 채널·사거리 필드는 삭제 — 서버(PlayerEscorter)로 이관됨)

```csharp
    public override bool CanUse() => true;

    public override void Use(GameObject aimTarget)
    {
        if (m_escorter == null)
        {
            Debug.LogWarning("Handcuffs: PlayerEscorter를 찾지 못함 — 검거 불가", this);
            return;
        }

        // 연행 중이면 이번 입력은 "놓기"
        if (m_escorter.IsEscorting)
        {
            m_escorter.RequestRelease();
            return;
        }

        NpcController target = ResolveTarget(aimTarget);
        if (target == null)
        {
            Debug.Log("체포할 대상이 없음 (NPC를 겨냥하지 않음)");
            return;
        }

        // 대상 상태 판정(즉시 재연행/채널링/사거리)은 서버가 수행한다 — 여기서는 요청만 넘긴다
        m_escorter.RequestCapture(target);
    }

    /// <summary>진행 중인 구속 채널링을 취소한다. (이동·피격 등 방해 시 호출)</summary>
    public void CancelRestrain() => m_escorter != null ? (System.Action)m_escorter.CancelCapture : null;
```

> 주의: 위 `CancelRestrain`의 삼항식은 컴파일되지 않는다 — 아래 Step 2에서 올바른 형태로 교체한다. (플랜 오탈자 방지용 표시)

- [ ] **Step 2: CancelRestrain·라이프사이클 정리**

`CancelRestrain`과 `OnDisable`/`OnDestroy`를 다음으로 확정한다:

```csharp
    /// <summary>진행 중인 구속 채널링을 취소한다. (이동·피격 등 방해 시 호출)</summary>
    public void CancelRestrain() => m_escorter?.CancelCapture();

    private void OnDisable()
    {
        CancelRestrain();
    }
```

`OnDestroy`는 로컬 `m_cts`가 사라졌으므로 삭제한다. `ResolveTarget`(정적 대상 해석)은 그대로 유지한다. `m_channelSeconds`·`m_captureRange` 필드와 `System.Threading`/`Cysharp` using은 더 이상 필요 없으면 제거한다(`ResolveTarget`만 남으면 `UnityEngine`만 필요).

- [ ] **Step 3: 컴파일 확인**

MCP: `refresh_unity(compile=request)` → `read_console(types=[Error])` — Expected: 에러 0.

- [ ] **Step 4: 커밋**

```bash
git add Assets/Scripts/Item/Handcuffs.cs
git commit -m "refactor: Handcuffs를 PlayerEscorter 서버 요청 경로로 전환 (#<ISSUE>)"
```

---

## Task 4: NpcSubdueInteractable 도주 제압을 서버 경로로 라우팅

**Files:**
- Modify: `Assets/Scripts/NPC/NpcSubdueInteractable.cs`

**Interfaces:**
- Consumes: `PlayerEscorter.RequestSubdueCapture(NpcController)` (interactor GameObject에서 조회), `NpcController.RequestSubdueHit()` (기존 유지).

- [ ] **Step 1: Interact에서 도주 제압을 요청 경로로 교체**

```csharp
    public void Interact(GameObject interactor)
    {
        switch (m_controller.CurrentState)
        {
            case NpcState.Run:
                // 도주 제압도 서버 권위 — 요청자(플레이어)의 PlayerEscorter를 통해 서버로 넘긴다
                PlayerEscorter escorter = interactor != null
                    ? interactor.GetComponentInParent<PlayerEscorter>() : null;
                if (escorter != null)
                {
                    Debug.Log($"도주 NPC 제압 요청: {m_controller.name}");
                    escorter.RequestSubdueCapture(m_controller);
                }
                break;

            case NpcState.Attack:
                // 저항 타격은 이미 자체 RPC 경로(RequestSubdueHit → SubdueHitRpc)를 가진다 (#79)
                Debug.Log($"저항 NPC 제압 타격: {m_controller.name}");
                m_controller.RequestSubdueHit();
                break;
        }
    }
```

- [ ] **Step 2: 컴파일 확인**

MCP: `refresh_unity(compile=request)` → `read_console(types=[Error])` — Expected: 에러 0.

- [ ] **Step 3: 커밋**

```bash
git add Assets/Scripts/NPC/NpcSubdueInteractable.cs
git commit -m "refactor: 도주 NPC 근접 제압을 PlayerEscorter 서버 경로로 라우팅 (#<ISSUE>)"
```

---

## Task 5: Multiplayer Play Mode 통합 검증

**Files:** (코드 변경 없음 — 검증 전용)

- [ ] **Step 1: 가상 플레이어 구성**

Multiplayer Play Mode 창에서 Virtual Player 1개 활성화. 메인 에디터 = Host, 가상 플레이어 = Client 로 접속(NetworkBootstrap OnGUI 버튼 사용).

- [ ] **Step 2: 클라이언트 검거 — 순응형**

가상 플레이어(클라)로 순응형 NPC를 겨냥하고 수갑 사용(공격 입력) → 3초 후:
- 기대: NPC가 **Captured→Escorted**로 전이하고 클라를 따라 이동. **호스트 화면과 클라 화면 양쪽 모두** 동일하게 반영.
- 확인: Console에 서버측 `NPC 구속됨` 로그, `NpcController` 상태 동기화.

- [ ] **Step 3: 클라이언트 검거 — 도주형/저항형**

- 도주형 NPC 채널링 → `체포 실패 — 뿌리치고 도주` 로그 + NPC가 Run 진입(양 피어 동기화). 이후 클라가 도주 NPC를 근접 제압(상호작용 Hold) → Captured 전이.
- 저항형 NPC 채널링 → `체포 실패 — 저항 시작` + Attack 진입. 클라가 제압 타격 → 게이지 감소 → Captured.

- [ ] **Step 4: 취소·사거리 검증**

채널링 중 클라가 대상에서 멀어지면(사거리 밖) → `구속 실패 — 대상이 범위를 벗어남`. 연행 중 재입력 시 → 놓기(Captured로 정지).

- [ ] **Step 5: 호스트 회귀 확인**

호스트 본인 조작으로도 위 흐름이 그대로 동작하는지(기존 동작 회귀 없음) 확인.

- [ ] **Step 6: 오프라인 폴백 확인**

네트워크 미접속 단독 Play에서 검거가 기존처럼 즉시 동작(`IsSpawned==false` 경로)하는지 확인.

- [ ] **Step 7: main 선머지 후 PR**

```bash
git fetch origin && git merge origin/main   # 충돌 예방 (PR 워크플로우)
# 충돌 해결 후
gh pr create --base main --title "fix: 클라이언트 검거 서버 권위화 (#<ISSUE>)" --body "<요약>"
```

---

## Self-Review

- **Spec coverage:** 클라 검거 불가의 원인(가드된 서버 권위 메서드를 클라가 직접 호출) → Task 2~4가 전 경로(수갑 채널링·연행·놓기·도주 제압)를 서버 RPC로 이관. 저항 타격은 기존 RPC 유지. ✅
- **오프라인 폴백:** `RequestCapture`/`CancelCapture`/`RequestRelease`/`RequestSubdueCapture` 모두 `!IsSpawned` 분기로 서버 로직을 직접 호출 → 단독 Play 유지. ✅
- **타입 일관성:** `RequestCapture(NpcController)`, `RequestSubdueCapture(NpcController)`, `RequestRelease()`, `CancelCapture()` — Task 2 정의와 Task 3·4 호출 시그니처 일치. `ServerBeginCapture`/`ServerChannelAsync`/`ServerCancelCapture`/`ServerSubdueCapture`/`IsInRange`/`ResolveReaction` 내부 일관. ✅
- **Placeholder scan:** Task 3 Step 1의 잘못된 삼항식은 의도적으로 표시하고 Step 2에서 올바른 `m_escorter?.CancelCapture()`로 확정 — 실제 반영 코드에 오탈자 없음. ✅
- **주의:** `NpcController.NetworkObject` 접근은 `NetworkBehaviour` 기본 프로퍼티라 유효. `[Rpc(SendTo.Server)]`는 NGO 2.x에서 오너 클라가 자기 소유 오브젝트에서 서버로 보낼 때 소유권 제약 없음(기존 `SubdueHitRpc` 패턴과 동일).
