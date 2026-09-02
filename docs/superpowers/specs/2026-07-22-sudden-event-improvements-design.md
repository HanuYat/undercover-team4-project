# 돌발 이벤트 개선 설계 (#291 + 난동꾼 이송 통일 + 괴한→차저)

- **날짜:** 2026-07-22
- **연결 이슈:** #291 (리팩토링 — 돌발 이벤트 구성/발생 구조 개선)
- **범위:** 단일 스펙 / 단일 PR (세 파트를 커밋 단위로 분리)
- **마일스톤:** 프로토타입 (2026-07-31)

## 개요

돌발 이벤트 시스템에 세 가지를 함께 적용한다:

- **A. SuddenEventManager 리팩토링** — 이벤트 풀을 자동수집에서 인스펙터 명시 리스트로 전환 (#291 본체, 순수 리팩토링).
- **B. 경범죄 이벤트 NPC 이송 통일** — 판정 후 그 자리에서 사라지던 난동자/난동꾼을 "임시 거처"로 걸어가 도착 시 소멸하도록 변경 (동작 변경).
- **C. 괴한 → 차저** — `ThugAttacker`의 추격+주기타격을 돌진(charge) 사이클로 재작성 (동작 변경).

> ⚠️ **리뷰 주의:** A는 순수 리팩토링, B·C는 동작 변경이다. 커밋을 A/B/C로 분리하고 PR 본문에서 파트를 명확히 구분해 리뷰·롤백을 쉽게 한다.

## 공통 제약 (팀 규약)

- **서버 권위 + 오프라인 폴백** — 발생·판정·이동·타격은 서버(또는 오프라인) 전용, 클라이언트는 NetworkTransform/NetworkVariable로 동기화된 결과만 표현 (#56 패턴).
- **네이밍** — `m_`(private 인스턴스), `s_`(static), `k_`(const), `On` 접두사(이벤트), public PascalCase (GDD 10-5).
- **튜닝 데이터는 ScriptableObject** (GDD 10-4, #259 선례) — 차저 튜닝값은 config SO로 분리.
- **밸런싱 수치는 별개** — 발생 확률·데미지·속도 등 수치 튜닝은 이 스펙의 목표가 아니다 (에셋에서 조정).

## 비목표 (Out of Scope)

- 돌발 이벤트 발생 확률/간격 밸런싱.
- 차저의 애니메이션 클립 제작·블렌드 트리 구성(코드 훅만 제공, 클립은 Editor 작업).
- 임시 거처·차저 관련 신규 씬 오브젝트의 최종 맵 배치(팀이 맵 구성 확정 시 결정, GDD 12장).
- 유치장(#228)·원한구역(#277) 로직 변경.

---

## A. SuddenEventManager 리팩토링

### 목표
같은 오브젝트의 `ISuddenEvent` 컴포넌트를 `GetComponents`로 자동수집하던 것을, 인스펙터에 **명시한 목록**으로 구성하고 항목별로 켜고 끌 수 있게 한다. 특정 이벤트만 켜서 반복 테스트/디버깅하는 경로를 만든다.

### 변경
- **풀 구성:** 자동수집(`GetComponents`) 제거 → 인스펙터 직렬화 목록. 각 항목은 `ISuddenEvent` 구현 컴포넌트 참조 + `enabled`(bool) + (선택) 라벨.
  - 직렬화 방식: `ISuddenEvent`는 인터페이스라 유니티가 직접 직렬화 못 한다. `MonoBehaviour` 참조로 받아 런타임에 `as ISuddenEvent` 캐스팅(실패 시 경고 로그로 배선 실수 표면화)하거나, 항목 구조체(`[Serializable] class SuddenEventEntry { MonoBehaviour component; bool enabled; }`)로 감싼다. → 플랜에서 확정.
  - `enabled=false` 항목은 추첨 풀에서 제외.
- **강제발동 디버그 API:** `public void ForceTrigger(int index)` + `[ContextMenu]` 진입점. 서버(또는 오프라인)에서만 동작, 라운드 진행 중 특정 이벤트를 즉시 발동.
- **RequireComponent 규약 완화:** `[RequireComponent(typeof(SuddenEventManager))]` 기반 "같은 오브젝트 강제"를 리스트 기반으로 정리 — 리스트는 다른 오브젝트의 이벤트 컴포넌트도 참조 가능. (씬 배선 최종형은 팀 결정)

### 불변
- 추첨·틱·정리 생명주기(`SuddenEventManager.cs`의 발생/틱/리셋), `ISuddenEvent`/`ISuddenEventProvider` 인터페이스 계약, 서버 권위·오프라인 동작.

### 데이터 흐름
라운드 InProgress → 매니저가 **enabled인 리스트 항목** 중 랜덤(또는 `ForceTrigger`로 지정) → 기존과 동일하게 `Begin/Tick/End` 호출.

### 테스트
- 리스트에 이벤트 3종 등록, 하나만 enabled → 그 이벤트만 반복 발생 확인.
- `ForceTrigger`로 특정 이벤트 즉시 발동 확인.
- Play 모드 컴파일·콘솔 에러 없음.

---

## B. 경범죄 이벤트 NPC → 임시 거처 이송 후 소멸

### 목표
난동자(Resist)·난동꾼(Flee)이 경범죄 판정 후 **그 자리에서 즉시 사라지는** 현재 동작을, **씬에 배치한 "임시 거처" 지점으로 걸어가 도착 시 소멸**하도록 바꾼다. 눈앞에서 뻥 사라지는 어색함을 없애되, 유치장(진범·탈옥 메카닉 전용)은 건드리지 않는다.

### 개념 분리
원한구역(`Detained` #277 — 오검거 시민이 모여 추격 출동)과 **다른 지점, 다른 목적**이다. 재사용하지 않고 전용 경로를 둔다.

### 컴포넌트
- **`NpcController` 신규 진입 (부분클래스 `NpcController.Holding.cs`, #259 분할과 일관):**
  - `public void SendToHolding(Transform spot)` — `spot`으로 걸어가는 상태로 전이. `spot==null`이면 전이하지 않음(호출부가 폴백 처리).
  - 신규 FSM 상태 `NpcState.Holding` — Walk 모션으로 `spot`까지 이동, 도착(임계 거리) 시 `OnReachedHolding` 이벤트 발행 후 정지. 서버 권위.
  - `public event Action<NpcController> OnReachedHolding`.
  - enum 추가 시 **끝에만 추가**(값=Animator 번호 규약, `NpcState.cs` 주석). Holding은 Jailed/Intruding처럼 대응 Animator 상태 없음 → `NpcAnimationDriver`가 Walk/Idle 모션 대여.
- **`SpawnedNpcEvent` 변경:**
  - `[SerializeField] private Transform m_holdingPoint`(씬 배치).
  - `HandleArrestJudged`: 기존 `m_despawnQueued=true`(즉시 despawn) 대신 —
    - `m_holdingPoint != null` → `m_npc.SendToHolding(m_holdingPoint)` + `OnReachedHolding` 구독 → 도착 콜백에서 `Despawn()`.
    - `m_holdingPoint == null` → **기존 그 자리 despawn 폴백** 유지.
  - 방치 타임아웃·라운드 종료(`ServerReset`) despawn 경로는 그대로.

### 데이터 흐름
판정(`OnArrestJudged`, 경범죄) → 연행 해제(기존 #228 순서 유지) → `SendToHolding` → `Holding` 상태로 NavMesh 보행 → 도착 → `OnReachedHolding` → `SpawnedNpcEvent.Despawn()`.

### 에러 처리
- 홀딩 지점 경로 실패(NavMesh 도달 불가): 일정 시간/거리 판단 후 그 자리 despawn로 폴백(무한 대기 방지) — 침입 상태의 경로 실패 폴백과 동일 사고.
- 이송 중 라운드 종료: 기존 `ServerReset`→`Despawn`이 정리.

### 테스트
- 난동자·난동꾼 각각 제압→연행→본부 판정 후 임시 거처까지 보행→도착 시 소멸 확인.
- 홀딩 지점 미배치 씬: 그 자리 despawn 폴백 확인.
- 네트워크(호스트+클라): 이송·소멸이 클라에서도 동기화되는지 확인.

---

## C. 괴한 → 차저

### 목표
`ThugAttacker`의 "NavMesh 추격 + 정면 부채꼴 주기 타격"을 **돌진(charge) 사이클**로 재작성한다. 제압 불가·임시 위협 성격은 유지한다.

### 동작 사이클 (서버 권위)
1. **접근** — 표적(가장 가까운 행동 가능 현장 플레이어)에게 NavMesh로 붙는다(기존 표적 선정·추격 재사용).
2. **윈드업** — 돌진 사거리 진입 시 표적 방향을 고정하고 짧게 준비(텔레그래프). 이 구간이 **회피 창**이다 (#220 "준비 동작=회피 창" 철학과 동일).
3. **돌진** — 고정 방향으로 직선 고속 이동. 벽 판정은 넉백(#232)의 `SphereCast` 장애물 스윕을 재사용.
4. **결과:**
   - **명중** → `IDamageable.TakeDamage`(데미지) + `PlayerMovement.AddKnockback`(플레이어 넉백; 플레이어 넉백은 오너 캐릭터가 적용하므로 서버가 대상 오너에 트리거).
   - **빗맞음 / 벽 충돌** → 짧은 **경직**(반격 창) 후 회복.
5. 회복 후 1로 복귀.

### 유지
- **제압 불가** — `NpcSubdueInteractable` 없음, E 반응 없음.
- **임시 위협** — 지속시간 관리·despawn은 `ThugAssaultEvent`가 그대로 담당.
- **소란 펄스**(#81) — 주변 시민 패닉 전파 유지.

### 튜닝 데이터 (config SO — #259 패턴)
차저 수치를 `ThugAttacker`의 흩어진 `[SerializeField]`에서 **`ThugChargerConfig : ScriptableObject`**로 분리:
- 표적 탐색 반경·재탐색 주기
- 돌진 개시 사거리, 윈드업 시간, 돌진 속도, 최대 돌진 거리/시간
- 명중 데미지, 넉백 세기, 경직 시간
- 정면 판정 각도(명중 콘), 소란 반경·주기
- `[CreateAssetMenu(menuName = "Undercover/Events/Thug Charger Config")]`, 대응 `.asset` 생성 후 괴한 프리팹에 배선.
- ⚠️ 코드 기본값 ≠ 에셋 값 (SerializeField 기본값 변경은 기존 .asset에 자동 반영 안 됨 — 에셋 직접 수정).

### 상태 구현
`ThugAttacker` 내부에 돌진 사이클을 페이즈(접근/윈드업/돌진/경직) 상태 머신으로 구현(NPC FSM과 별개, 컴포넌트 로컬). 클라이언트는 NetworkTransform으로 위치만 표현.

### 애니메이션
`ThugAnimationDriver`에 돌진/경직 표현 훅 추가(속도·페이즈 기반). 실제 클립·블렌드 트리는 Editor 작업(스펙 밖).

### 데이터 흐름
`ThugAssaultEvent`가 괴한 스폰 → `ThugAttacker`가 config로 돌진 사이클 구동 → 명중 시 `PlayerData` HP 동기화 + 플레이어 넉백 → 지속시간 후 `ThugAssaultEvent`가 despawn.

### 테스트
- 윈드업 중 옆으로 피하면 빗나가는지(회피 창) 확인.
- 명중 시 데미지+넉백, 벽 충돌 시 경직(반격 창) 확인.
- 제압(E) 무반응 유지 확인.
- 네트워크: 돌진·넉백이 클라에서 동기화되는지 확인.

---

## 파일 영향 요약

| 파트 | 파일 | 변경 |
|---|---|---|
| A | `Assets/Scripts/Events/Core/SuddenEventManager.cs` | 리스트 기반 풀 + 토글 + ForceTrigger |
| B | `Assets/Scripts/NPC/NpcController.Holding.cs` (신규) | `SendToHolding`·`OnReachedHolding` |
| B | `Assets/Scripts/NPC/States/NpcState.cs` | `Holding` enum 값 추가(끝에) |
| B | `Assets/Scripts/NPC/NpcHoldingState.cs` (신규) | 보행→도착 통보 상태 |
| B | `Assets/Scripts/NPC/Controller/NpcController.cs` | Awake에 Holding 상태 등록 |
| B | `Assets/Scripts/NPC/View/NpcAnimationDriver.cs` | Holding 모션 대여 |
| B | `Assets/Scripts/Events/SpawnedNpcEvent.cs` | 홀딩 지점 이송 + 도착 시 despawn + 폴백 |
| C | `Assets/Scripts/Events/ThugAttacker.cs` | 돌진 사이클 재작성 |
| C | `Assets/Scripts/Events/Config/ThugChargerConfig.cs` (신규) | 차저 튜닝 SO |
| C | `Assets/Scripts/Events/ThugAnimationDriver.cs` | 돌진/경직 표현 훅 |
| B/C | 씬·프리팹·`.asset` | 임시 거처 지점 배치, 차저 config 에셋 생성·배선 (Editor) |

## 리스크

- **B enum 추가**: `NpcState`에 값 추가 시 Animator 번호 규약 때문에 반드시 끝에 추가. 기존 값 순서 변경 금지.
- **C 재작성 범위**: `ThugAttacker` 전면 재작성이라 기존 습격 이벤트 밸런스가 흔들릴 수 있음(수치는 config에서 조정).
- **단일 PR 혼합**: 리팩토링+동작변경 혼재 → 커밋 분리로 완화.
- **접점**: #290(수갑 회수)·#292(테이저 스턴)·#269(밧줄)는 NPC/이벤트 도메인 접점이나 이 스펙과 직접 충돌 없음(파일·로직 분리). #294(NpcController 분할)가 먼저 머지되어야 `NpcController.Holding.cs` 부분클래스가 자연스럽다.

## 완료 기준

- A: 인스펙터 리스트로만 풀 구성, 항목 토글·강제발동 동작, 컴파일·런타임 무에러.
- B: 경범죄 이벤트 NPC가 임시 거처로 보행 후 소멸(미배치 폴백 포함), 호스트+클라 동기화.
- C: 괴한이 윈드업→돌진→명중(데미지+넉백)/빗맞음(경직) 사이클로 동작, 제압 불가 유지, 튜닝값 config SO 분리.
