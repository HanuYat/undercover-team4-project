# NPC 체력 시스템 구현 플랜 (#366)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** NPC에 체력을 도입하고 기존 저항 제압 게이지(`SubdueGauge`)를 흡수시켜, 체력이 0이 되면 기절(`Stunned`)하게 만든다.

**Architecture:** `NpcController`는 이미 9개 파일로 partial 분할된 `NetworkBehaviour`다. 여기에 `NpcController.Health.cs`를 추가해 HP를 얹고 `IDamageable`을 구현한다. HP는 `NetworkVariable<int>`(동기화) + `int`(서버·오프라인 진실값) 이중 구조로, 같은 클래스의 `m_networkState`·`m_subdueGauge`가 쓰던 패턴을 그대로 따른다. NPC 프리팹 4개는 건드리지 않는다.

**Tech Stack:** Unity 6000.3.15f1 / Netcode for GameObjects / ScriptableObject 튜닝 데이터 / C# partial class

**설계 근거:** [docs/superpowers/specs/2026-07-27-npc-health-stun-design.md](../specs/2026-07-27-npc-health-stun-design.md)

## Global Constraints

모든 태스크에 암묵적으로 적용된다.

- **서버 권위 + 오프라인 폴백** — HP 변경·상태 전이는 서버(또는 오프라인) 전용. 가드는 `if (IsSpawned && !IsServer) return;` 형태로 통일한다. 클라이언트는 `NetworkVariable`로 동기화된 결과만 읽는다 (#56 패턴).
- **이중 구조** — 동기화 변수와 서버 진실값 필드를 함께 둔다. `CurrentXxx => IsSpawned ? m_syncedXxx.Value : m_xxx;`
- **네이밍 (GDD 10-5)** — `m_`(private/protected 인스턴스), `s_`(static), `k_`(const), `I` 접두사(인터페이스), camelCase(매개변수/지역변수), PascalCase(public 프로퍼티/메서드/enum 값), `On` 접두사(이벤트).
- **튜닝 데이터는 ScriptableObject** (GDD 10-4) — 수치를 코드에 하드코딩하지 않는다.
- **밸런스 불변** — 게이지 100 / 타격 34 = 3방이었다. `MaxHp 100` / `SubdueHitPower 34`로 옮겨 그대로 3방을 유지한다.
- **주석은 "왜"를 적는다** — 이 저장소의 기존 주석 밀도와 톤을 따른다. 이슈 번호(`#366`)를 함께 남긴다.
- **한 태스크 = 한 커밋.** 커밋 메시지 끝에 `Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>`.

### 검증 방식에 대한 주의

**이 저장소에는 자동화 테스트가 없다** (CLAUDE.md: "Unity Test Framework — 아직 테스트 코드 없음"). 테스트 하네스 도입은 #366 범위 밖이다. 따라서 각 태스크의 검증은 다음 둘로 한다:

1. **컴파일 확인** — Unity Editor로 포커스를 옮겨 컴파일이 끝나기를 기다린 뒤 Console에 에러가 없는지 본다. (MCP 연결 시: `refresh_unity` → `read_console(types:["error"])`)
2. **Play 모드 수동 검증** — 태스크마다 명시된 절차를 따른다.

새 타입·멤버를 쓰기 전에 반드시 컴파일이 통과했는지 확인할 것.

---

## File Structure

| 파일 | 책임 | 태스크 |
|---|---|---|
| `Assets/Scripts/NPC/Config/NpcCommonConfig.cs` | 컨트롤러 레벨 공용 튜닝값 — `MaxHp`·`SubdueHitPower` 추가 | 1 |
| `Assets/Scripts/NPC/NpcController.Health.cs` | **신규** — HP 저장소, `IDamageable`, HP 0 → 기절 | 2 |
| `Assets/Scripts/NPC/Controller/NpcController.cs` | `InitBehavior`에서 HP 초기화 호출 / 게이지 멤버 삭제 | 2, 3 |
| `Assets/Scripts/NPC/NpcController.Reaction.cs` | 타격 요청 경로를 HP로 전환 | 3 |
| `Assets/Scripts/NPC/States/NpcResistState.cs` | 게이지 참조 제거 / 제한시간 도주 제거 / 표적 상실 복귀 | 3, 5 |
| `Assets/Scripts/NPC/Config/NpcResistConfig.cs` | 게이지·제한시간 값 삭제, `NoTargetIdleSeconds` 신설 | 3, 5 |
| `Assets/Scripts/NPC/NpcSubdueGaugeHud.cs` | 게이지 바 → HP 바 | 3 |
| `Assets/Scripts/NPC/States/NpcStunnedState.cs` | 기절 해제 시 풀피 회복 + 배회 복귀 | 4 |
| `Assets/Scripts/NPC/States/NpcStateRules.cs` | `HasSubdueInteraction`에 배회 상태 추가 | 6 |
| `Assets/Scripts/NPC/Controller/NpcSubdueInteractable.cs` | `Interact` 분기를 규칙과 일치시킴 | 6 |
| `Assets/Scripts/Events/Bomb/BombDevice.cs` | 낡은 주석 갱신 (주석만) | 7 |
| `docs/GDD.md` | 7-4 저항 플로우 변경 반영 | 7 |

---

## Task 1: 체력 튜닝값을 NpcCommonConfig에 추가

`NpcCommonConfig`는 "특정 FSM 상태에 속하지 않는 컨트롤러 레벨 공용 값"을 담는 SO다. HP와 타격량의 자리로 맞다. 이 태스크는 **순수 추가**라 기존 동작을 바꾸지 않는다.

**Files:**
- Modify: `Assets/Scripts/NPC/Config/NpcCommonConfig.cs`

**Interfaces:**
- Produces: `NpcCommonConfig.MaxHp` (int), `NpcCommonConfig.SubdueHitPower` (int) — Task 2·3이 읽는다.

- [ ] **Step 1: 필드와 프로퍼티 추가**

`Assets/Scripts/NPC/Config/NpcCommonConfig.cs`에서 `m_knockbackObstacleMask` 선언 **바로 아래**, `public float SpawnSpeedMultiplierMin` 줄 **바로 위**에 다음을 삽입한다:

```csharp

    [Header("체력 — #366")]
    [Tooltip("NPC 최대 체력 — 0이 되면 기절(Stunned)한다. 저항 제압 게이지(구 SubdueGaugeMax)를 대체한 값")]
    [SerializeField] private int m_maxHp = 100;
    [Tooltip("제압 홀드 성공 1회가 깎는 체력 — 기본값 기준 3회로 기절. NpcResistConfig에서 이관 (#366)")]
    [SerializeField] private int m_subdueHitPower = 34;
```

그리고 프로퍼티 블록의 `public LayerMask KnockbackObstacleMask => m_knockbackObstacleMask;` **아래**에 추가한다:

```csharp

    /// <summary>NPC 최대 체력 — HUD가 비율 계산에, NpcController가 초기화·회복에 읽는다. (#366)</summary>
    public int MaxHp => m_maxHp;

    /// <summary>제압 홀드 1회가 깎는 체력. (#366 — 구 NpcResistConfig.SubdueHitPower)</summary>
    public int SubdueHitPower => m_subdueHitPower;
```

또한 클래스 XML 주석의 `/// 스폰 시 개체 편차, 넉백 비행 물리(#232)를 담는다.` 줄을 다음으로 교체한다:

```csharp
/// 스폰 시 개체 편차, 넉백 비행 물리(#232), 체력(#366)을 담는다.
```

- [ ] **Step 2: 컴파일 확인**

Unity Editor로 포커스를 옮기고 컴파일이 끝나기를 기다린다.
기대: Console에 에러 0건. (MCP 연결 시 `refresh_unity` 후 `read_console(types:["error"])`가 빈 배열)

- [ ] **Step 3: 커밋**

```bash
git add Assets/Scripts/NPC/Config/NpcCommonConfig.cs
git commit -m "NPC 체력 — 튜닝값(MaxHp/SubdueHitPower)을 NpcCommonConfig에 추가 (#366)"
```

---

## Task 2: HP 저장소와 IDamageable 구현

`NpcController.Health.cs`를 신설한다. 이 시점에는 아무도 `TakeDamage`를 부르지 않으므로 **동작 변화가 없다** — 게이지와 HP가 잠시 공존한다. Task 3에서 전환한다.

**Files:**
- Create: `Assets/Scripts/NPC/NpcController.Health.cs`
- Modify: `Assets/Scripts/NPC/Controller/NpcController.cs` (`InitBehavior` 1줄 추가)

**Interfaces:**
- Consumes: `NpcCommonConfig.MaxHp` (Task 1)
- Produces:
  - `NpcController.MaxHp` → `int`
  - `NpcController.CurrentHp` → `int`
  - `NpcController.TakeDamage(int amount, GameObject attacker)` → `void` (IDamageable)
  - `NpcController.ServerRestoreHp()` → `void`

- [ ] **Step 1: NpcController.Health.cs 생성**

```csharp
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// NPC 체력 (#366) — 저항 제압 게이지(#76/#79)를 대체한다. 0이 되면 기절(Stunned)한다.
///
/// 서버 권위 + 오프라인 폴백: 서버(또는 오프라인)만 값을 바꾸고 클라이언트는 동기화 값을 읽는다.
/// <see cref="PlayerData"/>의 HP와 같은 규칙이며, m_networkState와 같은 이중 구조를 쓴다 (#56 패턴).
///
/// <b>지속형이다</b> — 교전이 끝나도 깎인 체력은 남는다(구 게이지는 저항 진입마다 리셋됐다).
/// 회복 지점은 기절에서 깨어나는 순간 하나뿐이다(<see cref="ServerRestoreHp"/>, NpcStunnedState).
/// </summary>
public partial class NpcController : IDamageable
{
    // 서버 권위 HP — 서버만 쓰고 모든 클라이언트가 읽는다. m_hp가 서버·오프라인의 진실값.
    private readonly NetworkVariable<int> m_syncedHp = new NetworkVariable<int>();
    private int m_hp;

    /// <summary>최대 체력 — HUD가 비율 계산에 읽는다. (#366)</summary>
    public int MaxHp => m_commonConfig.MaxHp;

    /// <summary>현재 체력. 세션 중에는 동기화 값이라 클라이언트에서도 안전하게 읽을 수 있다. (#366)</summary>
    public int CurrentHp => IsSpawned ? m_syncedHp.Value : m_hp;

    /// <summary>체력 초기화 — InitBehavior에서 서버(또는 오프라인) 1회 호출된다.</summary>
    private void InitHealth()
    {
        SetHp(MaxHp, null);
    }

    /// <summary>
    /// 피해 적용 (<see cref="IDamageable"/>) — 진압봉 타격 등 모든 데미지 소스의 공통 경로. (#366)
    ///
    /// 신병을 확보했거나 오검거 페널티가 진행 중인 상태(<see cref="NpcStateRules.CanBeStunned"/>가 false)
    /// 에서는 <b>피해 자체를 무시</b>한다. 이 게이트가 없으면 호송 중인 NPC를 때려 기절시켜 신병에서
    /// 빼내는 우회가 생긴다 — 테이저가 같은 상태들에서 no-op인 것과 같은 기준이다(#289).
    /// 무시하는 쪽을 택한 이유: HP만 깎고 기절은 막으면 "HP 0인데 기절 아님" 상태가 생겨,
    /// 엣지 트리거 특성상 그 NPC가 영영 기절하지 않게 된다.
    /// </summary>
    /// <param name="amount">깎을 체력. 0 이하는 무시한다.</param>
    /// <param name="attacker">가해자 — 기절 시 위협 대상으로 넘긴다. null 허용.</param>
    public void TakeDamage(int amount, GameObject attacker)
    {
        if (IsSpawned && !IsServer)
            return;
        if (amount <= 0)
            return;
        if (!NpcStateRules.CanBeStunned(CurrentState))
            return;

        SetHp(Mathf.Clamp(CurrentHp - amount, 0, MaxHp), attacker);
    }

    /// <summary>
    /// 체력 완전 회복 — 기절에서 깨어나는 순간 <see cref="NpcStunnedState"/>가 호출한다. (#366)
    /// 이 회복이 빠지면 HP 0인 채로 깨어나고, 아래 엣지 트리거 때문에 두 번 다시 기절하지 않는다.
    /// </summary>
    public void ServerRestoreHp()
    {
        if (IsSpawned && !IsServer)
            return;

        SetHp(MaxHp, null);
    }

    // 0에 '도달하는 순간'에만 기절시킨다 — PlayerData.SetHp의 무력화 진입과 같은 엣지 트리거.
    // 덕분에 이미 0인 대상에 대한 추가 타격이 기절 타이머를 리셋하지 못한다.
    private void SetHp(int value, GameObject attacker)
    {
        int previous = CurrentHp; // 변경 전 값 — 0 도달 '순간'을 잡기 위함
        m_hp = value;
        if (IsSpawned && IsServer)
            m_syncedHp.Value = value;

        if (value == 0 && previous > 0)
            EnterStunned(attacker != null ? attacker.transform : null);
    }
}
```

- [ ] **Step 2: InitBehavior에서 초기화 호출**

`Assets/Scripts/NPC/Controller/NpcController.cs`의 `InitBehavior()` 안, `m_stateMachine.ChangeState(NpcState.Idle);` **바로 위**에 추가한다:

```csharp
        // 체력은 FSM 시동 전에 채운다 — 첫 틱부터 CurrentHp가 유효해야 한다 (#366)
        InitHealth();

```

- [ ] **Step 3: 컴파일 확인**

Unity Console 에러 0건. `IDamageable`은 `Assets/Scripts/Interaction/Core/IDamageable.cs`에 이미 있으므로 별도 using이 필요 없다(전역 네임스페이스).

- [ ] **Step 4: 커밋**

```bash
git add Assets/Scripts/NPC/NpcController.Health.cs Assets/Scripts/NPC/NpcController.Health.cs.meta Assets/Scripts/NPC/Controller/NpcController.cs
git commit -m "NPC 체력 — HP 저장소 + IDamageable 구현, HP 0 시 기절 (#366)"
```

> ⚠️ `.meta` 파일을 반드시 함께 스테이징한다. Unity가 새 `.cs`에 대해 생성하며, 빠뜨리면 팀원마다 GUID가 새로 발급된다.

---

## Task 3: 타격 경로를 게이지에서 HP로 전환

여기서 게이지가 사라진다. 게이지를 참조하는 코드가 전부 한 번에 바뀌어야 컴파일이 통과하므로 한 태스크로 묶는다. **이 태스크 이후 저항 NPC를 3번 때리면 체포가 아니라 기절한다.**

**Files:**
- Modify: `Assets/Scripts/NPC/Controller/NpcController.cs` (게이지 멤버 3개 삭제)
- Modify: `Assets/Scripts/NPC/NpcController.Reaction.cs` (타격 경로 전환)
- Modify: `Assets/Scripts/NPC/States/NpcResistState.cs` (게이지 참조 제거)
- Modify: `Assets/Scripts/NPC/Config/NpcResistConfig.cs` (게이지 값 삭제)
- Modify: `Assets/Scripts/NPC/NpcSubdueGaugeHud.cs` (HP 바로 전환)

**Interfaces:**
- Consumes: `NpcController.TakeDamage`, `CurrentHp`, `MaxHp` (Task 2), `NpcCommonConfig.SubdueHitPower` (Task 1)
- Produces: `NpcController.RequestSubdueHit()` → `void` (시그니처 불변, 내부 동작만 전환). `SubdueGauge`·`SubdueGaugeMax`·`ApplySubdueHit`·`ResetSubdueGauge`는 **삭제되어 더 이상 존재하지 않는다.**

- [ ] **Step 1: NpcController.cs에서 게이지 멤버 삭제**

다음 세 블록을 통째로 지운다.

```csharp
    // 서버 권위 제압 게이지 — 저항(Attack) 상태에서만 의미. 진행도 UI(후속)를 위해 동기화한다 (#76)
    private readonly NetworkVariable<float> m_syncedSubdueGauge = new NetworkVariable<float>(0f);
    private float m_subdueGauge; // 서버·오프라인의 진실값 — m_networkState와 같은 이중 구조
```

```csharp
    /// <summary>저항 제압 게이지 최대치 — HUD가 게이지 비율 계산에 읽는다. (#76/#79)</summary>
    public float SubdueGaugeMax => m_resistConfig.SubdueGaugeMax;
```

```csharp
    /// <summary>현재 제압 게이지. 네트워크 세션 중에는 동기화된 값이라 클라이언트에서도 읽을 수 있다. (#76)</summary>
    public float SubdueGauge => IsSpawned ? m_syncedSubdueGauge.Value : m_subdueGauge;
```

- [ ] **Step 2: NpcController.Reaction.cs의 타격 경로 전환**

`ResetSubdueGauge`·`ApplySubdueHit`·`SetSubdueGauge` 세 메서드를 **삭제**하고, `RequestSubdueHit`/`SubdueHitRpc`를 아래로 교체한다.

삭제 대상:

```csharp
    /// <summary>저항 진입 시 게이지를 최대로 리셋한다 — NpcResistState.Enter 전용.</summary>
    public void ResetSubdueGauge()
```
```csharp
    public void ApplySubdueHit(float amount)
```
```csharp
    // 게이지는 서버 진실값과 동기화 변수에 함께 기록한다 — 오프라인에서는 NetworkVariable에 쓰지 않는다 (#56 상태 패턴과 동일)
    private void SetSubdueGauge(float value)
```

교체본 (기존 `RequestSubdueHit` + `SubdueHitRpc` 자리에):

```csharp
    /// <summary>
    /// 제압 타격 요청 — 상호작용 경로(NpcSubdueInteractable)가 호출한다. (#79/#366)
    /// 클라이언트에서 불리면 서버로 전달되므로 비호스트 플레이어의 타격도 반영된다.
    /// 타격량은 서버가 자기 config 값을 쓴다 — 클라이언트가 수치를 보낼 수 없다.
    ///
    /// 상태 게이트는 TakeDamage가 CanBeStunned로 건다 (#366). 예전의 "저항 중이 아니면 무시"
    /// (배회 NPC 폭행 방지)는 체력이 지속형이 되면서 없어졌다 — 배회 중인 NPC도 때릴 수 있다.
    /// </summary>
    // TODO: 상호작용 네트워크 전환(#55 계열)에서 거리·조준 서버 검증 추가 (지금은 요청 자체는 신뢰)
    public void RequestSubdueHit()
    {
        if (IsSpawned && !IsServer)
        {
            SubdueHitRpc();
            return;
        }

        TakeDamage(m_commonConfig.SubdueHitPower, null);
    }

    // 가해자를 넘기지 않는 이유: RPC가 요청자를 싣지 않기 때문이다(기존 경로와 동일).
    // 기절에서 깨어나면 도주가 아니라 배회로 복귀하므로(#366 결정 5) 위협 대상이 필요 없다.
    [Rpc(SendTo.Server)]
    private void SubdueHitRpc()
    {
        TakeDamage(m_commonConfig.SubdueHitPower, null);
    }
```

- [ ] **Step 3: NpcResistState.cs에서 게이지 참조 제거**

`Enter()`에서 이 줄을 지운다:

```csharp
        m_owner.ResetSubdueGauge();
```

`Tick()` 맨 앞의 이 블록을 통째로 지운다:

```csharp
        // 게이지가 다 깎이면 제압 성공 — 체포
        if (m_owner.SubdueGauge <= 0f)
        {
            Debug.Log($"저항 제압됨: {m_owner.name}");
            // 체포로 반응이 끝나므로 위협 참조를 여기서 정리한다. Exit()에 넣으면 안 된다 —
            // Defeat()의 StartFlee()가 세팅한 위협을 그 직후 Exit()가 지워 도주 전환이 깨진다 (#205).
            m_owner.ClearThreat();
            m_owner.StateMachine.ChangeState(NpcState.Captured);
            return;
        }
```

클래스 XML 주석의 이 줄을

```csharp
/// ApplySubdueHit로 제압 게이지가 0이 되면 체포(Captured)된다 — 여럿이 때리면 빨리 끝난다(협동 인센티브).
```

다음으로 교체한다:

```csharp
/// 제압 타격으로 체력이 0이 되면 기절(Stunned)한다 — 여럿이 때리면 빨리 끝난다(협동 인센티브).
/// 전이 자체는 NpcController.SetHp가 걸므로 이 상태 클래스는 체력을 보지 않는다 (#366).
```

- [ ] **Step 4: NpcResistConfig.cs에서 게이지 값 삭제**

필드 두 줄을 지운다:

```csharp
    [Tooltip("저항 제압 게이지 최대치 — ApplySubdueHit로 깎여 0이 되면 체포된다")]
    [SerializeField] private float m_subdueGaugeMax = 100f;
    [Tooltip("제압 홀드 성공 1회가 깎는 제압 게이지량")]
    [SerializeField] private float m_subdueHitPower = 34f;
```

프로퍼티 두 줄을 지운다:

```csharp
    public float SubdueGaugeMax => m_subdueGaugeMax;
    public float SubdueHitPower => m_subdueHitPower;
```

클래스 XML 주석의 이 줄을

```csharp
/// 제압 게이지·타격량은 컨트롤러의 제압 요청 경로도 이 값을 읽는다.
```

다음으로 교체한다:

```csharp
/// 체력·타격량은 NpcCommonConfig로 이관됐다 (#366) — 저항 전용 값이 아니게 됐기 때문이다.
```

- [ ] **Step 5: NpcSubdueGaugeHud.cs를 HP 바로 전환**

`OnGUI()`의 표시 게이트를 교체한다. 기존:

```csharp
        // 저항 상태에서만 표시 — 배회·도주 NPC에는 제압 게이지 개념이 없다
        if (m_controller.CurrentState != NpcState.Attack)
            return;
```

교체본:

```csharp
        // 다친 NPC만 표시 — 체력이 지속형이라 상태로 거를 수 없다(#366). 상태로 걸면 배회 시민
        // 전원 머리 위에 바가 상시로 뜨고, 저항 상태로만 걸면 도망친 부상 NPC의 체력이 숨는다.
        if (m_controller.CurrentHp >= m_controller.MaxHp)
            return;
```

`fill` 계산을 교체한다. 기존:

```csharp
        float fill = m_controller.SubdueGaugeMax > 0f
            ? Mathf.Clamp01(m_controller.SubdueGauge / m_controller.SubdueGaugeMax)
            : 0f;
```

교체본:

```csharp
        float fill = m_controller.MaxHp > 0
            ? Mathf.Clamp01((float)m_controller.CurrentHp / m_controller.MaxHp)
            : 0f;
```

클래스 XML 주석 첫 두 줄을

```csharp
/// [임시] 저항(Attack) NPC의 제압 게이지 표시 — 머리 위 게이지 바. (#76/#79)
/// 저항 중일 때만 모든 클라 화면에 그려지며, 홀드 타격으로 게이지가 깎이는 진행도를 보여준다.
/// 값은 <see cref="NpcController.SubdueGauge"/>·<see cref="NpcController.CurrentState"/>(동기화 값)를 읽으므로
```

다음으로 교체한다:

```csharp
/// [임시] NPC 체력 표시 — 머리 위 바. (#76/#79/#366)
/// 체력이 최대보다 낮을 때만 모든 클라 화면에 그려지며, 타격으로 깎이는 진행도를 보여준다.
/// 값은 <see cref="NpcController.CurrentHp"/>(동기화 값)를 읽으므로
```

> 파일·클래스명은 `NpcSubdueGaugeHud`로 둔다. 이름 변경은 `.meta`/컴포넌트 참조가 따라와야 해서 별도 작업이다(스펙 비목표).

- [ ] **Step 6: JailZone의 낡은 주석 갱신**

`Assets/Scripts/Interaction/Jail/JailZone.cs:189`가 삭제되는 `SetSubdueGauge`를 예시로 참조하고 있다. 기존:

```csharp
    // 이벤트를 직접 발행한다 (NpcController.SetSubdueGauge / HandleFsmStateChanged와 동일 구조)
```

교체본:

```csharp
    // 이벤트를 직접 발행한다 (NpcController.HandleFsmStateChanged와 동일 구조)
```

- [ ] **Step 7: 컴파일 확인**

Unity Console 에러 0건. 게이지 심볼이 남아 있으면 여기서 드러난다 — `SubdueGauge`/`SubdueGaugeMax`/`ApplySubdueHit`/`ResetSubdueGauge`/`SetSubdueGauge`를 참조하는 코드가 없어야 한다.

확인 명령:

```bash
git grep -n "SubdueGauge\|ApplySubdueHit\|ResetSubdueGauge" -- "Assets/Scripts/*.cs"
```

기대: `NpcSubdueGaugeHud.cs`(파일·클래스명)와 `NpcCommonConfig.SubdueHitPower` 외에 **실제 게이지 멤버 참조가 없음**.

- [ ] **Step 8: Play 모드 검증**

Unity Play 모드(단독). 저항형 NPC를 만들어 수갑 채널링으로 저항을 유발한 뒤:

1. E 홀드로 3회 타격 → **기절(`Stunned`)** 로 쓰러지는지 확인. `Captured`가 아니어야 한다.
2. 머리 위 바가 3분의 1씩 줄어드는지 확인.
3. 기절한 NPC에 밧줄을 쓸 수 있는지 확인(`IsRopeable`은 `Stunned`만 허용).

- [ ] **Step 9: 커밋**

```bash
git add Assets/Scripts/NPC/Controller/NpcController.cs Assets/Scripts/NPC/NpcController.Reaction.cs Assets/Scripts/NPC/States/NpcResistState.cs Assets/Scripts/NPC/Config/NpcResistConfig.cs Assets/Scripts/NPC/NpcSubdueGaugeHud.cs Assets/Scripts/Interaction/Jail/JailZone.cs
git commit -m "NPC 체력 — 제압 게이지를 체력으로 통합, 제압 결과를 기절로 변경 (#366)"
```

---

## Task 4: 기절 해제 시 풀피 회복 + 배회 복귀

**이 태스크가 빠지면 Task 3의 결과가 반쪽이다.** HP 0 → 기절은 엣지 트리거라, 회복 없이 깨어난 NPC는 두 번 다시 기절하지 않는 무적이 된다.

**Files:**
- Modify: `Assets/Scripts/NPC/States/NpcStunnedState.cs`

**Interfaces:**
- Consumes: `NpcController.ServerRestoreHp()` (Task 2), `NpcController.ClearThreat()` (기존)

- [ ] **Step 1: Tick()의 깨어나는 경로 교체**

기존:

```csharp
        // 깨어나면 배회가 아니라 도주다 — 무력화가 풀린 용의자는 그대로 서 있지 않는다 (#269 확정).
        // 위협은 기절시킨 상대(테이저 사수)이거나, 밧줄로 끌고 다닌 플레이어다.
        // 주변에 추격자가 아무도 없으면 도주 상태가 스스로 배회로 돌려보낸다(NpcFleeState) —
        // 아무도 없는 곳에 두고 온 NPC가 혼자 전력 질주하지 않는다.
        if (m_timer >= m_config.StunSeconds)
            m_owner.StartFlee(m_owner.ThreatTarget);
```

교체본:

```csharp
        // 깨어나면 배회로 돌아간다 — 도주하지 않는다 (#366 결정 5, #269의 도주 복귀를 대체).
        //
        // 체력 회복이 이 지점의 핵심이다: HP 0 → 기절은 '0에 도달하는 순간'만 걸리는 엣지
        // 트리거라(NpcController.SetHp), 0인 채로 깨어나면 두 번 다시 기절하지 않는 무적이 된다.
        // 회복을 상태 진입(Enter)이 아니라 여기 두는 이유는, 기절해 있는 동안에는 HP 0이
        // 유지돼야 "기절 중 추가 타격이 타이머를 리셋하지 않는다"는 성질이 성립하기 때문이다.
        if (m_timer >= m_config.StunSeconds)
        {
            m_owner.ServerRestoreHp();
            m_owner.ClearThreat(); // 도주하지 않으므로 위협 참조를 남길 이유가 없다
            m_owner.StateMachine.ChangeState(NpcState.Idle);
        }
```

- [ ] **Step 2: 클래스 XML 주석 갱신**

기존 줄:

```csharp
/// 시간이 지나면 일어나(#269 StandUp 모션) 스스로 도주한다. 진입은 NpcController.EnterStunned() —
/// 테이저 아이템(후속 이슈)이 호출한다.
```

교체본:

```csharp
/// 시간이 지나면 일어나(#269 StandUp 모션) 체력을 회복하고 배회로 돌아간다 (#366 — 도주 폐지).
/// 진입은 NpcController.EnterStunned() — 테이저와 체력 0 도달(NpcController.SetHp)이 호출한다.
```

- [ ] **Step 3: 컴파일 확인**

Unity Console 에러 0건.

- [ ] **Step 4: Play 모드 검증**

1. 저항 NPC를 3회 타격해 기절시킨다.
2. 방치한다 → `StunSeconds`(기본 3초) 후 일어나서 **도주하지 않고 배회**하는지 확인.
3. 머리 위 바가 사라지는지 확인(풀피 = HUD 비표시).
4. **같은 NPC를 다시 3회 타격 → 또 기절하는지 확인.** 이것이 무적 구멍 회귀 테스트다.

- [ ] **Step 5: 커밋**

```bash
git add Assets/Scripts/NPC/States/NpcStunnedState.cs
git commit -m "NPC 체력 — 기절 해제 시 풀피 회복 + 도주 대신 배회 복귀 (#366)"
```

---

## Task 5: 저항 제한시간 도주 제거 + 표적 상실 시 배회 복귀

제한시간 도주를 없애면 `Attack` 상태의 시간 기반 출구가 사라진다. 표적을 잃은 NPC가 영구히 굳지 않도록 **같은 태스크에서** 5초 복귀를 넣는다. 둘을 나누면 중간 커밋에 고착 버그가 남는다.

**Files:**
- Modify: `Assets/Scripts/NPC/Config/NpcResistConfig.cs`
- Modify: `Assets/Scripts/NPC/States/NpcResistState.cs`

**Interfaces:**
- Produces: `NpcResistConfig.NoTargetIdleSeconds` → `float`
- `NpcResistConfig.DefeatSeconds`는 **삭제되어 더 이상 존재하지 않는다.**

- [ ] **Step 1: NpcResistConfig에서 DefeatSeconds를 NoTargetIdleSeconds로 교체**

기존 필드:

```csharp
    [Tooltip("저항 시작 후 이 시간(초) 안에 제압당하지 않으면 플레이어 패배 — 도주형으로 전환된다 (GDD 7-4)")]
    [SerializeField] private float m_defeatSeconds = 15f;
```

교체본:

```csharp
    [Tooltip("표적이 사라진 채 이 시간(초)이 지나면 배회로 복귀한다 — 제한시간 도주를 없애면서 생긴 Attack 상태의 유일한 시간 기반 출구 (#366)")]
    [SerializeField] private float m_noTargetIdleSeconds = 5f;
```

기존 프로퍼티:

```csharp
    public float DefeatSeconds => m_defeatSeconds;
```

교체본:

```csharp
    public float NoTargetIdleSeconds => m_noTargetIdleSeconds;
```

- [ ] **Step 2: NpcResistState의 필드 교체**

기존:

```csharp
    private float m_resistStartTime;
```

교체본:

```csharp
    // 표적을 찾지 못한 채 흐른 시간(초) — 표적이 다시 잡히면 0으로 리셋해 연속일 때만 누적한다 (#366)
    private float m_noTargetSeconds;
```

- [ ] **Step 3: Enter()의 초기화 교체**

기존:

```csharp
        m_resistStartTime = Time.time;
```

교체본:

```csharp
        m_noTargetSeconds = 0f;
```

- [ ] **Step 4: Tick()에서 제한시간 도주 삭제**

이 블록을 통째로 지운다:

```csharp
        // 제한 시간 안에 못 꺾었으면 제압 실패 — 뿌리치고 도주 (GDD 7-4 '제압 실패')
        if (Time.time - m_resistStartTime > m_config.DefeatSeconds)
        {
            Defeat("제압 제한 시간 초과");
        }
```

- [ ] **Step 5: Tick()에 표적 상실 복귀 추가**

기존:

```csharp
        // 표적을 정하고(유발자 우선), 사거리 밖이면 추격·안이면 멈춰 타격, 그리고 표적을 향해 돈다 (#254·#220)
        Transform target = ResolveTarget();
        ChaseTarget(target);
```

교체본:

```csharp
        // 표적을 정하고(유발자 우선), 사거리 밖이면 추격·안이면 멈춰 타격, 그리고 표적을 향해 돈다 (#254·#220)
        Transform target = ResolveTarget();

        // 표적이 사라진 채로 일정 시간이 지나면 배회로 돌아간다 (#366 결정 7).
        // 제한시간 도주(DefeatSeconds)를 없애면서 Attack 상태의 시간 기반 출구가 사라졌는데,
        // 표적이 없으면 ChaseTarget이 에이전트를 세우고 사거리 밖이라 스윙도 하지 않아
        // 그대로 두면 NPC가 이 상태로 영구히 굳는다. 도주가 아니라 배회다 —
        // 때릴 상대가 사라진 NPC가 혼자 전력 질주할 이유가 없다(결정 5와 같은 방향).
        if (target == null)
        {
            m_noTargetSeconds += Time.deltaTime;
            if (m_noTargetSeconds >= m_config.NoTargetIdleSeconds)
            {
                Debug.Log($"저항 종료(표적 상실) — 배회 복귀: {m_owner.name}");
                m_owner.ClearThreat();
                m_owner.StateMachine.ChangeState(NpcState.Idle);
                return;
            }
        }
        else
        {
            m_noTargetSeconds = 0f;
        }

        ChaseTarget(target);
```

- [ ] **Step 6: 클래스 XML 주석 갱신**

기존 두 줄:

```csharp
/// 제한 시간 안에 제압당하지 않거나 교전 중인 플레이어가 전원 무력화되면
/// 플레이어 패배 — 도주형으로 전환되어 달아난다 (GDD 7-4 3항).
```

교체본:

```csharp
/// 교전 중인 플레이어가 전원 무력화되면 플레이어 패배 — 도주형으로 전환되어 달아난다 (GDD 7-4 3항).
/// 제한 시간으로 뿌리치고 도주하던 경로는 폐지됐다 (#366) — 저항 NPC는 체력이 0이 될 때까지 버틴다.
/// 표적이 사라지면 NoTargetIdleSeconds 후 배회로 돌아간다(고착 방지).
```

- [ ] **Step 7: 컴파일 확인**

Unity Console 에러 0건.

```bash
git grep -n "DefeatSeconds\|m_resistStartTime" -- "Assets/Scripts/*.cs"
```

기대: 결과 없음.

- [ ] **Step 8: Play 모드 검증**

1. 저항형 NPC의 저항을 유발한 뒤 **때리지 않고 15초 이상 붙어 있는다** → 예전처럼 뿌리치고 도주하지 **않는지** 확인(계속 저항해야 한다).
2. 저항 중 멀리 도망간다 → **5초 후** NPC가 배회(`Idle`)로 돌아가는지 확인. 도주(`Run`)가 아니어야 한다.
3. 2번 도중 5초가 지나기 **전에** 다시 접근한다 → 타이머가 리셋되고 저항이 이어지는지 확인.

- [ ] **Step 9: 커밋**

```bash
git add Assets/Scripts/NPC/Config/NpcResistConfig.cs Assets/Scripts/NPC/States/NpcResistState.cs
git commit -m "NPC 체력 — 저항 제한시간 도주 제거 + 표적 상실 시 5초 후 배회 복귀 (#366)"
```

---

## Task 6: 배회 중인 NPC도 타격 가능하게

`NpcStateRules.HasSubdueInteraction`과 `NpcSubdueInteractable.Interact`의 분기 집합은 **반드시 일치해야 한다**(#184). 둘을 같은 태스크에서 바꾼다.

**Files:**
- Modify: `Assets/Scripts/NPC/States/NpcStateRules.cs`
- Modify: `Assets/Scripts/NPC/Controller/NpcSubdueInteractable.cs`

**Interfaces:**
- Consumes: `NpcController.RequestSubdueHit()` (Task 3)

- [ ] **Step 1: HasSubdueInteraction 확장**

기존:

```csharp
    /// <summary>E 상호작용(제압·타격·재연행)이 반응하는 상태인가.
    /// 포함 목록 방식 — 새 상태는 기본 'E 불가'이므로 열어야 하면 여기 추가할 것.
    /// NpcSubdueInteractable.Interact의 분기 집합과 반드시 일치해야 한다.</summary>
    public static bool HasSubdueInteraction(NpcState state) =>
        state is NpcState.Run or NpcState.Attack or NpcState.Captured;
```

교체본:

```csharp
    /// <summary>E 상호작용(제압·타격·재연행)이 반응하는 상태인가.
    /// 포함 목록 방식 — 새 상태는 기본 'E 불가'이므로 열어야 하면 여기 추가할 것.
    /// NpcSubdueInteractable.Interact의 분기 집합과 반드시 일치해야 한다.
    /// 배회(Idle/Walk)가 열린 것은 체력이 지속형이 되면서다 (#366) — 예전에는 '배회 NPC 폭행 방지'로
    /// 막혀 있었지만, 이제 아무 때나 때려 체력을 깎을 수 있다.</summary>
    public static bool HasSubdueInteraction(NpcState state) =>
        state is NpcState.Idle or NpcState.Walk or NpcState.Run or NpcState.Attack or NpcState.Captured;
```

- [ ] **Step 2: Interact의 분기 확장**

기존 `case NpcState.Attack:` 블록을

```csharp
            case NpcState.Attack:
                // 저항 타격은 이미 자체 RPC 경로(RequestSubdueHit → SubdueHitRpc)를 가진다 (#79)
                Debug.Log($"저항 NPC 제압 타격: {m_controller.name}");
                m_controller.RequestSubdueHit();
                break;
```

다음으로 교체한다:

```csharp
            case NpcState.Idle:
            case NpcState.Walk:
            case NpcState.Attack:
                // 타격은 자체 RPC 경로(RequestSubdueHit → SubdueHitRpc)를 가진다 (#79).
                // 배회(Idle/Walk)도 같은 경로다 — 체력이 지속형이 되면서 저항 중이 아니어도
                // 때려서 깎을 수 있다 (#366). 상태 게이트는 TakeDamage가 CanBeStunned로 건다.
                Debug.Log($"NPC 제압 타격: {m_controller.name}");
                m_controller.RequestSubdueHit();
                break;
```

- [ ] **Step 3: 클래스 XML 주석과 switch 머리 주석 갱신**

`Interact` 안의 이 주석을

```csharp
        // 도주·저항·체포 상태일 때만 반응 — 배회 중인 NPC 오작동 방지 (체포는 수갑 채널링이 정식 경로)
```

다음으로 교체한다:

```csharp
        // 배회·도주·저항·체포 상태에서 반응한다 (체포는 수갑 채널링이 정식 경로).
        // 배회가 열린 것은 체력이 지속형이 되면서다 (#366)
```

클래스 XML 주석의 이 줄을

```csharp
/// 저항(Attack) 중이면 제압 타격 1회로 게이지를 깎는다 — 여럿이 함께 누르면 그만큼 빨리
/// 제압된다 (GDD 7-4 협동 인센티브).
```

다음으로 교체한다:

```csharp
/// 배회(Idle/Walk)·저항(Attack) 중이면 타격 1회로 체력을 깎는다 — 여럿이 함께 누르면 그만큼
/// 빨리 기절시킨다 (GDD 7-4 협동 인센티브, #366).
```

- [ ] **Step 4: 컴파일 확인**

Unity Console 에러 0건.

- [ ] **Step 5: Play 모드 검증**

1. **배회 중인** 시민을 조준 → E 프롬프트/윤곽선이 뜨는지 확인(`CanInteract`).
2. E로 3회 타격 → 체력이 깎이고 기절하는지 확인.
3. **연행(`Escorted`) 중인 NPC를 타격** → 체력이 깎이지 **않고** 연행이 유지되는지 확인. 신병 우회 차단(Task 2의 `CanBeStunned` 게이트) 회귀 테스트다.

- [ ] **Step 6: 커밋**

```bash
git add Assets/Scripts/NPC/States/NpcStateRules.cs Assets/Scripts/NPC/Controller/NpcSubdueInteractable.cs
git commit -m "NPC 체력 — 배회 중인 NPC도 타격 대상에 포함 (#366)"
```

---

## Task 7: 멀티플레이 검증 + 문서 갱신

코드 변경의 마지막 관문은 클라이언트 경로다. `RequestSubdueHit`의 RPC 분기는 단독 Play에서 절대 타지 않으므로 여기서 확인한다.

**Files:**
- Modify: `Assets/Scripts/Events/Bomb/BombDevice.cs` (주석 1줄)
- Modify: `docs/GDD.md`

- [ ] **Step 1: Multiplayer Play Mode 2인 검증**

Multiplayer Play Mode로 가상 플레이어 1명을 띄워 호스트 + 클라이언트로 실행한다.

1. **클라이언트에서** NPC를 E로 타격 → 호스트·클라 **양쪽** 화면에서 체력 바가 같이 줄어드는지 확인(`SubdueHitRpc` 경로).
2. 클라이언트가 3회 타격 → 양쪽에서 NPC가 기절 모션으로 쓰러지는지 확인(`m_networkState` 동기화).
3. 기절 해제 → 양쪽에서 배회로 돌아가고 체력 바가 사라지는지 확인.
4. 호스트와 클라가 **번갈아** 타격 → 협동 누적이 되는지 확인(1명이 3방이 아니라 둘이 합쳐 3방).

> 클라이언트(가상 플레이어) 콘솔은 Unity MCP `read_console`로 보이지 않는다. `Library/VP/mppm*/Logs/Editor.log`를 직접 읽어야 한다.

- [ ] **Step 2: BombDevice의 낡은 주석 갱신**

`Assets/Scripts/Events/Bomb/BombDevice.cs:322`의 주석을 교체한다. 기존:

```csharp
        // 반경 내 행동 가능한 플레이어에게 피해 (NPC는 HP가 없어 피해 대신 뷰의 넉백 연출만 받는다)
```

교체본:

```csharp
        // 반경 내 행동 가능한 플레이어에게 피해.
        // NPC는 이제 체력이 있지만(#366) 폭발 피해는 아직 연결하지 않았다 — 넉백 착지가 이미
        // Stunned로 보내고 있어 중복 정리가 필요하다(후속 이슈). 지금은 넉백만 받는다.
```

> 실제 폭발 피해 연결은 스펙의 비목표다. 주석만 사실에 맞춘다.

- [ ] **Step 3: GDD 7-4 갱신**

`docs/GDD.md`에서 저항형 전투 흐름 3항을 찾는다:

```markdown
  3. **플레이어 패배(제압 실패 / HP 소진) 시 → 대상 도주형 전환** 후 도주 시작.
```

교체본:

```markdown
  3. **플레이어 패배(교전 인원 HP 소진) 시 → 대상 도주형 전환** 후 도주 시작. **제압 실패(제한 시간 초과)로 뿌리치는 경로는 폐지됐다** (#366) — 저항형은 체력이 0이 될 때까지 버티며, 물러나지 않는다. 표적이 사라지면 5초 후 배회로 돌아간다.
```

같은 절에서 제압 성공의 결과를 서술한 문장을 찾아, 제압 결과가 체포가 아니라 **기절**임을 반영한다:

```markdown
> **NPC 체력 (#366, 2026-07-27 확정):** 저항 제압 게이지를 NPC 체력이 흡수했다. 타격으로 체력이 0이 되면 **기절(Stunned)** 하며, 즉시 체포되지 않는다 — 신병을 확보하려면 밧줄(8-3)로 끌고 가야 한다. 체력은 지속형이라 교전 사이에 누적되고, 기절에서 깨어나는 순간에만 완전 회복된다. 배회 중인 NPC도 타격 대상이다. 연행·수감·오검거 페널티 진행 중인 대상은 타격이 무시된다(신병 우회 차단).
```

- [ ] **Step 4: 커밋**

```bash
git add Assets/Scripts/Events/Bomb/BombDevice.cs docs/GDD.md
git commit -m "NPC 체력 — GDD 저항 플로우 갱신 + 폭탄 주석 사실화 (#366)"
```

---

## 완료 후

PR 본문은 팀 고정 4섹션(요약 / 테스트 방법 / 씬·프리팹 변경 / 후속으로 미룬 것)으로 작성한다.

- **씬/프리팹 변경:** 코드상 **없음**(NPC 프리팹 4개를 건드리지 않는 것이 A안의 이점). 단 `NpcCommonConfig.asset`·`NpcResistConfig.asset`을 Editor에서 열어 저장했다면 신규/삭제 필드가 YAML에 반영되므로 **함께 커밋하고 이 섹션에 기재**할 것.
- **후속으로 미룬 것:** 폭탄 → NPC 피해 연결 / 시민 폭행 페널티 / `NpcSubdueGaugeHud` 클래스명 변경 / HUD 정식화(#65 계열).
