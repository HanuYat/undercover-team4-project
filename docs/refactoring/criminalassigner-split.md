# CriminalAssigner 책임 분리

> 작성: 2026-08-02 · 브랜치 `refactoring/criminalassigner-hotfix` (커밋 `b4c1331` → `f433c23` → `60146ae`)
> 판단 기준은 [PlayerLoadout 분리 §2](playerloadout-split.md#2-판단-기준--unity에서-무엇이-위험한가)와 동일.
> 게임 동작 변화 없음(로그 요약 줄 1개 추가만). 규칙의 정본은 [architecture.md](../architecture.md).

## 1. 한 문장 요약

`CriminalAssigner`(607줄)에서 **승격·신원 생성·진단 로그**를 협력자로 뽑아 301줄로 줄였다.
전부 평범한 C# 객체이며 **씬 파일과 외부 표면은 건드리지 않았다.**

## 2. 무엇이 바뀌었나

| 지표 | 이전 | 이후 |
|---|---:|---:|
| `CriminalAssigner` 줄 수 | 607 (실코드 402) | **301 (실코드 187)** |
| `AssignAll` 루프의 프로필 생성·위조 | 30줄 | **5줄** |
| 루프 안 로그 조립 | 15줄 (3중 삼항 ×3) | **1줄** |
| 새 컴포넌트 | — | **0개** (전부 plain object) |
| 씬 변경 | — | **0** |
| 외부 표면 변경 | — | **0** |

### 최종 구성

| 파일 | 줄 수 | 담당 |
|---|---:|---|
| `CriminalAssigner.cs` | 301 | 배정 오케스트레이션 + 튜닝값 12개 |
| `CitizenProfileFactory.cs` | 176 | 신원 생성 — 이름·타입·세력·문양·위조 |
| `SuspectRevealer.cs` | 162 | 제보 전화 승격 (#102) |
| `NameForgery.cs` | 90 | 이름 오염 (#223) — **Unity 비의존, 테스트 가능** |
| `AssignmentLog.cs` | 77 | 진단 로그 — **데모 빌드 전 삭제 대상** |
| `ReactionRoll.cs` | 30 | 반응 추첨 — 배정·승격이 공유 |

## 3. 결정적 제약 — 프리팹이 아니라 씬

`SerializeField` 12개가 `Main Scene.unity`에 있다. 프리팹과 달리 **팀원이 동시에 편집할 확률이 높아
머지 충돌 시 손으로 고칠 수 없다.**

→ **이 파일은 `SerializeField`를 하나도 옮기지 않는 방향으로 갔다.**
필요한 값은 전부 생성자로 넘긴다. [PlayerMovement 분리](playermovement-split.md)에서 프리팹 필드
17개를 옮긴 것과 정반대의 선택이며, 이유는 대상이 프리팹이냐 씬이냐 하나다.

## 4. 분리 1 — `SuspectRevealer` (제보 전화 승격, #102)

### 경계를 어디서 찾았나

**구동 주체가 다르다:**

| | 배정 (`AssignAll`) | 승격 (`PromoteNext`) |
|---|---|---|
| 언제 | 라운드 시작 **1회** | 라운드 중 **전화마다** |
| 누가 | `NpcSpawner.OnSpawnCompleted` | `TipCallPhone` |
| 대상 | NPC **전원** | 예비 용의자 **1명** |

그리고 **소비자가 이미 갈려 있었다** — 이게 결정적 근거였다:

| 소비자 | 쓰는 것 | 쪽 |
|---|---|---|
| `AppearanceAssigner`·`DirectoryManager`·`TestCriminalLabel`·`RoundManager` | `CriminalNpcs`, `OnCriminalAssigned`, `TotalAssignedBounty` | 배정 |
| **`TipCallPhone`** | **`HasPendingSuspect`, `PromoteNext`** | **승격** |

### 무엇을 옮겼나

`PromoteNext`와 그 **전용 헬퍼 전부**다. `IsPending`·`FindNextPromotable`은 승격을 위해서만
존재하던 private 헬퍼라 함께 갔다 — 명단 조회만 빼고 `PromoteNext`를 남기는 안도 검토했으나,
**기능을 그 유일한 사용자와 분리하는 셈**이라 접었다.

### 공유 상태 2건

| 상태 | 처리 |
|---|---|
| 예비 용의자 명단 | 같은 `List` 인스턴스를 `IReadOnlyList`로 **빌려 준다**. 배정이 `Clear` 후 다시 채워도 그대로 보인다 (`HeldItems` 관례) |
| 현상금 총합 | 배정·승격이 함께 더하는 값이라 승격 쪽은 **증감분만 돌려주고**(`TryPromoteNext`의 `out`) 소유자가 반영한다 (`PlayerMovement`의 수직 속도와 같은 방침) |

### 덤 — 죽은 코드 발견

`RevealedCount`(18줄, public)의 호출자가 **0건**이었다. 주석에는 `RoundManager`가 쓴다고 적혀
있었으나 실제로는 `TotalAssignedBounty`만 쓴다. 삭제했다.
**600줄 안에서는 안 보이던 것이 경계를 그으니 드러났다.**

## 5. 분리 2 — `CitizenProfileFactory` · `NameForgery`

### 왜 팩토리가 인스턴스인가

이름 풀은 라운드 안에서 **중복이 없어야** 해서 인원수만큼 한 번에 확정해야 한다.
개별 호출로는 만들 수 없어 생성자에서 셔플하는 객체가 됐다 — 정적 유틸이 될 수 없는 이유다.

### `Create` / `ApplyForgery` 2단 분리

기존 `CitizenProfile.Initialize`가 **항상 정본 프로필을 만들고, 위조는 그 뒤에 표시값만 덮어쓰는**
2단 구조였다. 팩토리가 그 두 단계를 그대로 나눠 가진다 — 순서를 뒤집으면(위조 후 `Initialize`)
오염값이 정본으로 되돌아가는데, 이제 그 순서가 **API 모양에 드러난다.**

`ApplyForgery`는 어느 축을 오염했는지 `bool`로 돌려준다(문양이면 true) — 진단 로그가 구분해 찍는다.

### 결과

```csharp
CitizenProfile profile = factory.Create(i);

bool isForger = forgerIndices.Contains(i);
bool forgedSymbol = isForger && factory.ApplyForgery(profile, m_forgedCharCount);
```

세력 추첨 → 진짜 문양 조회 → 프로필 생성 → variant 개수 확인 → 50% 분기 → 문양/이름 위조가
전부 팩토리 안으로 들어가 **30줄이 5줄**이 됐다.

## 6. 분리 3 — `AssignmentLog`

배정 루프 한가운데에 3중 삼항 연산자 3개 + `StringBuilder` 조립으로 **15줄**이 박혀 있었다.
이 로그는 **정답(진범·위조범)이 그대로 노출**되어 데모 빌드 전 제거 대상인데(주석에 두 번 명시),
걷어내려면 루프를 헤집어야 하는 상태였다.

→ 이제 **파일 하나와 호출 3줄**(생성·`Add`·`Flush`)만 지우면 된다.

배정된 값은 `CitizenIdentity`에서 직접 읽는다(`Profile`·`IsCriminal`·`Reaction`·`Bounty`) —
로그를 위해 호출부가 값을 따로 들고 있을 필요가 없다. 신원에서 못 읽는 것만 인자로 받는다.

**요약 줄에 위조범 수를 추가했다** — 배정이 `m_forgerCount`와 맞는지 확인하려면 로그 20줄을
눈으로 세야 했는데, 이제 마지막 줄만 보면 된다. 이 PR의 유일한 출력 변화다.

## 7. 검증 — 순수 로직은 플레이 모드 없이 확인했다

리팩토링의 부수 이득이 여기서 드러난다. **분리 전에는 `CorruptName`이 `CriminalAssigner`의
private static이라 밖에서 부를 수 없었다.** 뽑아내고 나니 에디터에서 직접 호출해 검증할 수 있다.

`NameForgery.Corrupt` — **1500회 샘플, 위반 0건**

| 불변식 | 결과 |
|---|:---:|
| 정확히 한 글자만 변경 | ✅ |
| 모음↔모음 / 자음↔자음 유지 | ✅ |
| 대소문자 보존 | ✅ |
| 같은 글자로 치환되지 않음 | ✅ |
| 공백·기호 미변경 | ✅ |
| 경계(`count=0`·`null`·알파벳 없음·`count` 초과) | ✅ |

`CitizenProfileFactory.ApplyForgery` — **60회, 위반 0건** (진짜와 같은 index 0건, variant 범위 밖 0건)

`ReactionRoll.Roll` — **2만 회** (시민 50.2/25.1/24.7%, 범인 20.1/40.2/39.7%, 가중치 0이면 Compliant)

> 이 검증 코드는 asmdef가 생기면 **그대로 단위 테스트가 된다.**

### 실제 라운드 로그 대조

```
시민 프로필 배정 완료 (20명, 예비 용의자 5명 중 1명 공개):
  Zane Mercer | Human | Faction B  ← 수배 공개 (Compliant)  [현상금 8000원]
  Orin Blake  | Human | Faction B  (미끼: Resist)  [위조: 문양 0→1]  [현상금 700원]
  Hana Ryu    | Android | Faction B  ← 예비 용의자 · 미공개 (Compliant)  [위조: 문양 0→1]  [현상금 1000원]
  → 배정 현상금 총합 9700원 (돌발 이벤트 수익 별도) · 위조범 2명
```

공개 1 + 미공개 4 = 5(`m_maxWantedCount`) · 위조범 2(`m_forgerCount`) · 8000+700+1000 = 9700 전부 일치.
**`Hana Ryu`가 핵심 확인이다** — 미공개 예비 용의자이면서 위조범인 개체가 진범 몫이 아니라
위조범 몫(1000원)을 받는다(#395 규칙). 승격되면 `SuspectRevealer`가 진범 몫으로 다시 뽑는다.

## 8. 발견한 데이터 이슈 (코드 아님)

검증 중 드러난 것으로, 기획/에셋 쪽 판단 사항이다.

- **세력 variant가 2개뿐이다** (`FactionA: 2`, `FactionB: 2`). 문양 위조가 항상 `0↔1` 두 값
  사이에서만 일어나 본부 대조 시 판별이 쉬울 수 있다. 동작은 정상.
- `None`은 variant 0개라 배정 대상에서 제외된다 — #222 (b) 설계대로다.

## 9. 하지 않은 것

- **`AssignAll` 루프 추가 분해** — 남은 것은 "인덱스 추첨 → 신원 배정 → 목록 누적"이고, 순서가
  의미를 가진다(공개 수 판정이 `m_criminalNpcs.Count`에 의존). 더 쪼개면 그 순서가 흩어진다.
- **결정과 적용의 완전 분리** (`CitizenAssignment` 값 객체로 계획을 먼저 만들고 적용) — 가장
  테스트 가능한 형태지만 `CitizenProfile`이 `ScriptableObject`라 값 객체로 다루기 어렵고,
  테스트가 0개인 상태에서 동작 드리프트 위험이 크다. 이 3단계 위에 얹는 것이 순서상 맞다.
- **`AssignmentLog` 삭제** — 스캔 UI(#39)가 아직 없어 배정 확인 수단이 이것뿐이다.
  지울 준비만 해 뒀다.
