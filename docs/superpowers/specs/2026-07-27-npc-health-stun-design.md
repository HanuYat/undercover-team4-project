# NPC 체력 시스템 설계 (#366)

- **날짜:** 2026-07-27
- **연결 이슈:** #366 (기능 — NPC 체력 시스템, 체력 0 시 스턴) / 관련: #292(스턴 상태), #76·#79(저항·제압)
- **범위:** 단일 스펙 / 단일 PR
- **마일스톤:** 프로토타입

## 개요

NPC에 체력을 도입하고, 기존 **저항 제압 게이지(`SubdueGauge`)를 체력으로 통합**한다. 체력이 0이 되면 기절(`Stunned`)한다.

이슈 원문은 "플레이어 체력 시스템과 동일 구조"만 요구하지만, 그대로 신설하면 NPC 무력화 경로가 4개가 된다(제압 게이지 / 테이저 / 넉백 착지 / 신규 체력). 팀 결정으로 **게이지를 체력이 흡수하는 통합안**을 택했다 — 바가 하나로 줄어 개념이 단순해지는 대신, 저항 시스템(#76/#79)의 동작이 바뀐다.

> ⚠️ **리뷰 주의:** 이 스펙은 순수 추가가 아니라 **기존 저항·검거 플로우의 동작 변경**을 포함한다. 특히 저항형 NPC의 제압 결과가 `Captured`(즉시 체포)에서 `Stunned`(기절 → 밧줄 필요)로 바뀐다.

## 공통 제약 (팀 규약)

- **서버 권위 + 오프라인 폴백** — HP 변경·기절 전이는 서버(또는 오프라인) 전용. 클라이언트는 `NetworkVariable`로 동기화된 결과만 읽는다 (#56 패턴).
- **이중 구조** — `NetworkVariable<int> m_syncedHp` + `int m_hp`(서버·오프라인 진실값). `NpcController`의 `m_networkState`·`m_subdueGauge`가 이미 쓰는 패턴을 그대로 따른다.
- **네이밍** — `m_`(private 인스턴스), `s_`(static), `k_`(const), public PascalCase (GDD 10-5).
- **튜닝 데이터는 ScriptableObject** (GDD 10-4, #259 선례).
- **아키텍처 R1~R8** — 신규 매니저를 만들지 않으므로 R4/R5 해당 없음.

## 비목표 (Out of Scope)

- **폭탄 → NPC 피해 연결.** 넉백 착지가 이미 `Stunned`로 보내고 있어 중복 정리가 필요하다. 이번엔 `BombDevice`의 낡은 주석(`// NPC는 HP가 없어…`)만 갱신하고 실제 데미지 연결은 후속 이슈.
- **시민 폭행 페널티.** 배회 NPC를 때릴 수 있게 되면서 생기는 공백 — 오검거 페널티(#277~#279)와 엮이는 별도 설계.
- **HUD 정식화.** `NpcSubdueGaugeHud`는 임시 OnGUI 관례를 유지한다(정식 UI는 #65 계열). 클래스명 변경도 `.meta` 처리가 붙으므로 후속.
- **밸런싱 수치 튜닝.** 현행 게이지 값을 그대로 옮겨 체감을 유지한다(아래 4절).

---

## 결정 사항

팀 확정(2026-07-27):

1. **제압 게이지를 체력으로 통합** — `SubdueGauge`는 사라지고 HP가 그 역할을 흡수한다.
2. **HP 0 → 무조건 `Stunned`** — 상황별 분기 없음. `NpcResistState`의 `게이지 0 → Captured` 전이는 제거한다.
3. **HP는 지속형, 어디서든 피해** — `IDamageable`을 일반 구현하고, 배회 중인 NPC도 피격 대상이 된다. 피해는 교전 사이에 누적된다.
4. **기절에서 깨어날 때 풀피 회복** — 기절이 개념적으로 "한 번의 다운"이 된다.
5. **기절 종료 시 도주하지 않고 `Idle`(배회) 복귀** — 일어나는 모션(#269)은 유지한다.
6. **저항 제한시간 도주 제거** — `DefeatSeconds` 초과로 뿌리치고 도주하던 경로를 없앤다. "교전 플레이어 전원 무력화 → 도주"는 유지한다.
7. **표적을 잃은 저항 NPC는 5초 후 `Idle` 복귀** — 결정 6으로 사라진 `Attack` 상태의 출구를 대신한다(도주 아님).

---

## 1. 아키텍처 · 데이터 흐름

신규 파일 `Assets/Scripts/NPC/NpcController.Health.cs` (partial). `NpcController`는 이미 9개 파일로 partial 분할돼 있고(`Knockback`/`Reaction`/`Rope`…) 상태·게이지에 동일한 이중 구조를 쓰므로, 게이지 코드가 그대로 이사한다. **NPC 프리팹 4개(`NPC_Citizen`/`NPC_Citizen_Generic`/`NPC_Rioter`/`NPC_Streaker`)를 전혀 건드리지 않는다** — 리뷰어가 프리팹 diff를 읽을 수 없다는 점을 감안한 선택이다.

```csharp
public partial class NpcController : IDamageable
{
    private readonly NetworkVariable<int> m_syncedHp = new NetworkVariable<int>();
    private int m_hp;   // 서버·오프라인 진실값

    public int MaxHp => m_commonConfig.MaxHp;
    public int CurrentHp => IsSpawned ? m_syncedHp.Value : m_hp;

    public void TakeDamage(int amount, GameObject attacker);
    public void ServerRestoreHp();    // 기절 해제 시 풀피
    private void SetHp(int value);    // 0 도달 순간 → EnterStunned
}
```

**초기화** — `Awake`에서 `m_hp = MaxHp`(오프라인 폴백), `OnNetworkSpawn`에서 서버가 `SetHp(MaxHp)`. `PlayerData`와 동일.

**엣지 트리거** — `SetHp`는 `value == 0 && previous > 0`일 때만 기절을 건다. `PlayerData.SetHp`의 무력화 진입과 같은 규칙이다.

### 신병 확보 상태 보호 (중요)

`CanBeStunned` 게이트는 `EnterStunned` **안이 아니라 호출자(테이저)** 에 있다. HP 0 → 기절 경로에 같은 게이트를 걸지 않으면, 타격으로 **호송 중인 NPC를 신병에서 빼내는 우회**가 생긴다.

→ `TakeDamage`는 `NpcStateRules.CanBeStunned(CurrentState)`가 false이면 **피해 자체를 무시**한다(no-op). 테이저가 그 상태들에서 아무 일도 하지 않는 것과 같은 처리이며, "HP 0 ⟺ 기절 중"이라는 불변식도 유지된다.

제외 상태: `Escorted` / `Captured` / `Jailed` / `Detained` / `Chasing` / `PenaltyEscorting`.

### 데이터 흐름

```
진압봉 E 홀드 (클라)
  → NpcSubdueInteractable.Interact
  → NpcController.RequestSubdueHit()  [클라면 SubdueHitRpc로 서버 전달]
  → TakeDamage(SubdueHitPower, attacker)   [서버]
      ├─ CanBeStunned(CurrentState) == false → 무시(no-op)
      └─ SetHp(clamp(CurrentHp - amount, 0, MaxHp))
           └─ value==0 && previous>0 → EnterStunned(attacker.transform)
                → ChangeState(Stunned) → m_networkState 동기화 → 전 피어 표현
```

기절 해제:

```
NpcStunnedState.Tick — m_timer >= StunSeconds
  → ClearThreat()
  → ChangeState(Idle)

NpcStunnedState.Exit — Stunned를 벗어나는 모든 경로(위의 Idle 복귀 + 수갑 채포로 인한
                        Stunned → Captured 전이)에서 호출된다
  → ServerRestoreHp()  (HP = MaxHp)
```

> 최초 설계는 회복을 `Tick()`의 "시간 다 됨" 분기에 두었으나, 수갑 채포 경로(`Stunned → Captured`)가
> 그 분기를 거치지 않아 HP 0 무적이 발생함을 Task 4 리뷰에서 확인했다. 회복 지점을 `Exit()`으로
> 옮겨 모든 탈출 경로를 덮도록 정정했다(6절 참고).

---

## 2. 상태 전이 변경

| 파일 | 변경 |
|---|---|
| `NpcResistState.Enter` | `m_owner.ResetSubdueGauge()` **삭제** — HP는 지속형이라 교전마다 리셋하지 않는다 |
| `NpcResistState.Tick` | `if (SubdueGauge <= 0f) → ChangeState(Captured)` 블록 **삭제**. HP 0은 `SetHp`가 기절로 처리하므로 저항 상태를 알아서 빠져나가고, `NpcResistState.Exit()`이 에이전트 설정을 복원한다 |
| `NpcResistState.Tick` | `Time.time - m_resistStartTime > DefeatSeconds → Defeat()` 블록 **삭제** (결정 6). `m_resistStartTime` 필드도 함께 제거 |
| `NpcResistState.Tick` | `SwingAttack()` → 전원 무력화 시 `Defeat()` — **유지** |
| `NpcStunnedState.Tick` | `m_owner.StartFlee(m_owner.ThreatTarget)` → `ClearThreat()` + `ChangeState(Idle)` |
| `NpcStunnedState.Exit` | `ServerRestoreHp()` **추가** — 회복 지점을 `Tick()`에서 `Exit()`으로 옮김(Task 4 리뷰 정정, 6절 참고). Stunned를 벗어나는 모든 경로(시간 경과·수갑 채포)를 덮기 위함 |

`NpcStunnedState`의 밧줄 타이머 정지(`IsRoped`)와 일어나는 모션(`RaiseStandUp`, #269)은 그대로 둔다.

### 결정 6의 파생 — 표적을 잃은 저항 NPC

제한시간을 없애면 **저항(`Attack`) 상태를 빠져나갈 시간 기반 출구가 사라진다.** 표적이 사라진 경우(플레이어가 멀리 도망 / 접속 종료) `ResolveTarget()`이 null을 반환하고, `ChaseTarget(null)`이 에이전트를 세운 뒤 사거리 밖이라 스윙도 하지 않는다 — NPC가 `Attack` 상태로 **영구히 굳는다.** 기존에는 `DefeatSeconds`가 이 상황의 유일한 탈출구였다.

**확정(결정 7):** `ResolveTarget()`이 null인 상태가 **5초** 이어지면 `Idle`로 복귀시킨다. 도주가 아니라 배회다 — 결정 5와 같은 방향이고, 때릴 상대가 사라진 NPC가 혼자 전력 질주하지 않는다.

- 타이머는 `NpcResistState`의 인스턴스 필드로 두고, 표적이 다시 잡히는 틱마다 0으로 리셋한다(연속 null일 때만 누적).
- 값은 `NpcResistConfig.NoTargetIdleSeconds`(신설, 기본 5f)로 뺀다 — 삭제되는 `DefeatSeconds`의 자리를 대신한다.
- 복귀 시 `ClearThreat()`를 함께 호출한다. 위협 참조를 남기면 다음 반응이 사라진 대상을 물고 시작한다.

---

## 3. 타격 · 상호작용 경로

- `ApplySubdueHit(float amount)` → `TakeDamage(int amount, GameObject attacker)`로 대체.
- 기존의 `if (CurrentState != NpcState.Attack) return;`(배회 NPC 폭행 방지) 가드를 **제거** — 결정 3에 따라 배회 중인 NPC도 피격 대상이다.
- `RequestSubdueHit()` → `SubdueHitRpc()` 서버 전달 경로는 그대로 유지한다(비호스트 클라의 타격이 서버에 반영되는 경로).
- `NpcStateRules.HasSubdueInteraction`을 `Idle`·`Walk`까지 확장한다.
- 주석이 명시한 대로 `NpcSubdueInteractable.Interact`의 `switch` 분기 집합도 함께 넓힌다(둘은 반드시 일치해야 한다, #184). `Idle`/`Walk` 케이스는 `RequestSubdueHit()`로 보낸다.

> `CaptureBySubdue`(도주 중 NPC 근접 제압 → `Captured`)는 게이지와 무관한 별도 경로라 **변경하지 않는다.** 결과적으로 도주형은 즉시 체포, 저항형은 기절 → 밧줄로 갈린다 — 두 유형의 의도적 차별화로 본다.

---

## 4. Config

| 항목 | 위치 | 값 |
|---|---|---|
| `MaxHp` (int) | `NpcCommonConfig` **신설** | 100 |
| `SubdueHitPower` (float→int) | `NpcResistConfig` → `NpcCommonConfig` **이동** | 34 |
| `SubdueGaugeMax` | `NpcResistConfig`에서 **삭제** | — (`MaxHp`가 대체) |
| `DefeatSeconds` | `NpcResistConfig`에서 **삭제** | — (결정 6) |
| `NoTargetIdleSeconds` (float) | `NpcResistConfig` **신설** | 5 (결정 7) |

`NpcCommonConfig`는 "특정 FSM 상태에 속하지 않는 컨트롤러 레벨 공용 값"을 담는 SO라 HP·타격량의 자리로 맞다.

**밸런스는 현행 유지** — 게이지 100 / 타격 34 = 3방이었고, HP 100 / 타격 34로 옮겨도 그대로 3방이다. 체감 변화 없이 구조만 바꾼다.

> SO 에셋(`NpcCommonConfig.asset` 등)의 신규 필드는 Unity가 코드 기본값으로 직렬화한다. **에셋을 Editor에서 저장하면 값이 YAML에 박히므로**, 저장했다면 에셋 변경도 커밋하고 PR 본문 "씬/프리팹 변경"에 기재한다.

---

## 5. HUD

`NpcSubdueGaugeHud`의 표시 조건을 `CurrentState == NpcState.Attack`에서 **`CurrentHp < MaxHp`** 로 바꾼다. 그대로 두면 배회 시민 전원 머리 위에 바가 상시로 뜬다.

`SubdueGauge`/`SubdueGaugeMax` 참조를 `CurrentHp`/`MaxHp`로 교체한다. 게이지 색 해석(가득=위협 빨강 → 줄수록 초록)은 그대로 둔다.

---

## 6. 엣지 케이스

- **HP 0인 채로 깨어남** — 발생하지 않는다. 단, 회복 지점은 처음 설계(`Tick()`의 "시간 다 됨" 분기)가 아니라 **`Exit()`**이다(Task 4 리뷰에서 정정). 기절한 NPC에 수갑을 채우면 `Stunned → Captured`로 곧장 전이하는데(`IsCapturable`이 `Stunned`를 막지 않음 — 의도된 동작) 이 경로는 `Tick()`의 "시간 다 됨" 분기를 거치지 않는다. 회복을 그 분기에만 두면 이런 NPC는 HP 0인 채로 `Captured`에 남고, 이후 `ReleaseFromCustody()`(오검거 석방)나 `NpcCapturedState`의 인계 방치 타이머가 `Idle`/`Run`으로 돌려보내도 HP를 회복하지 않아 두 번 다시 기절하지 않는 무적이 된다. `Exit()`은 상태 머신이 `Stunned`를 벗어나는 모든 경로(시간 경과·수갑 채포)에서 호출되므로 이 문제를 막는다(결정 4).
- **기절 중 추가 타격** — HP가 이미 0이라 `SetHp`의 엣지 조건(`previous > 0`)이 false → no-op. 재기절로 타이머가 리셋되지 않는다.
- **신병 확보 중 타격** — 2절의 `CanBeStunned` 게이트로 무시.
- **넉백 착지** — `m_knockbackLandingState`가 이미 `Stunned`(검거 중이면 `Captured`)로 보낸다. HP를 거치지 않는 별도 경로이므로 이번 변경과 충돌하지 않는다. 이때 HP는 0이 아니지만 `Stunned` 상태이고, 깨어날 때 `ServerRestoreHp()`가 돌아도 이미 풀피라 무해하다.
- **오프라인 Play 테스트** — `IsSpawned == false`이면 `m_hp`만 쓴다. 기존 게이지와 동일.

---

## 7. 문서 갱신 필요

- **GDD 7-4 3항** — *"플레이어 패배(제압 실패 / HP 소진) 시 → 대상 도주형 전환"* 에서 **"제압 실패" 경로가 사라진다**(결정 6). 저항 NPC는 HP 0까지 반드시 깎아야 하며 스스로 물러나지 않는다.
- **GDD 7-4 / 10-2** — 저항 제압 결과가 `Captured`에서 `Stunned`로 바뀐 것을 반영.
- **GDD 8-2 계열** — 저항형 검거가 "제압 → 즉시 체포"에서 "제압 → 기절 → 밧줄로 끌기"로 바뀌어 밧줄(#269)의 역할이 확대된다.

---

## 8. 테스트

Unity Play 모드 단독 + **Multiplayer Play Mode 2인**(호스트 + 클라이언트).

1. 저항 NPC를 E 홀드로 3회 타격 → HP 0 → **기절**(체포 아님) 확인.
2. 기절 상태에서 밧줄로 묶어 끌기가 되는지 확인(`IsRopeable`은 `Stunned`만).
3. 기절 방치 → `StunSeconds` 후 일어나서 **도주하지 않고 배회**, HP가 **풀피**로 돌아왔는지 확인.
4. 깨어난 NPC를 다시 3회 타격 → 또 기절하는지 확인(무적 구멍 회귀 테스트).
5. **배회 중인** 시민을 E로 타격 → HP가 깎이는지 확인(결정 3).
6. 연행(`Escorted`) 중인 NPC를 타격 → **아무 일도 일어나지 않는지** 확인(신병 우회 차단).
7. **클라이언트에서** 타격 → 서버 HP가 깎이고 양쪽 화면 HUD가 같이 줄어드는지 확인(RPC 경로).
8. 저항 중 플레이어가 멀리 도망 → **5초 후** NPC가 `Idle`로 복귀하는지 확인(도주하지 않을 것). `Attack` 고착 회귀 테스트.
9. 위 8번 도중 5초가 지나기 전에 플레이어가 다시 접근 → 타이머가 리셋되고 저항이 이어지는지 확인.
