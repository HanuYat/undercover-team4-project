# 유치장 직접 이송 설계 (#492)

작성 2026-08-03 · 대상 이슈 [#492](https://github.com/hyunjin0814/undercover-team4-project/issues/492) · 선행 [#462](https://github.com/hyunjin0814/undercover-team4-project/issues/462)(PR #491, 벤치 착석)

> **이후 변경 (2026-08-03, 같은 날 구현 중):** 이 문서는 판정 트리거를 **Jail NavMesh 영역 진입**으로 잡았지만, 구현·플레이 후 **유치장 문 앞에 세운 보안 스캐너 게이트**(`JailScanner`)로 물러났다. 아래 [구현 중 실측이 필요한 것](#구현-중-실측이-필요한-것)의 "오검거의 원한 구역 경로" 항목이 그 위험을 예측하고 **"판정 지점을 문 바깥으로 살짝 빼는 것"** 을 대비책으로 적어 두었는데, 실제로 그 대비책을 택한 것이다. 통행 부여 기준도 '신병 여부'에서 '판정 통과 여부'로 함께 바뀌었다.
>
> 따라서 아래 본문에서 **"Jail 영역 안에서 판정"으로 읽히는 서술은 현행이 아니다** — R1의 기준만 게이트로 바뀌었고 R2(착석)는 그대로 Jail 영역이다. 확정 규칙의 정본은 **GDD 7-2의 #492 노트**이고, 구현은 `JailScanner`·`JailIntake` 주석이 설명한다. 이 문서는 그 시점의 판단 기록으로 남긴다.

## 배경

지금은 본부 인계 단말에서 E를 누르면 판정이 나고, NPC가 유치장 좌석까지 NavMesh로 스스로 걸어가 앉는다. 이 자동 이송을 폐기하고 **플레이어가 직접 끌고 들어가 앉히는 방식**으로 바꾼다 (2026-07-24 결정).

자동 이송에서 NPC가 제자리에 굳던 버그는 고치지 않는다 — 그 경로가 통째로 사라지기 때문이다.

## 확정 사항

2026-08-03 확정. 이후 논의는 이 표를 기준으로 한다.

| 항목 | 확정 |
|---|---|
| 판정 트리거 | Jail NavMesh 영역에 들어서는 순간 |
| 정산 계상(`Admit`) | 좌석에 앉힌 순간 — **판정과 분리** |
| 착석 조작 | 유치장 안에서 밧줄 놓기(E) → 가장 가까운 빈 좌석 |
| 인계존 계열 | 코드·씬 모두 제거 |
| 인계 방치 타이머(GDD 7-6) | 손대지 않음 |
| 유치장 문 자동 개폐 | 제거 — 무조건 E 토글 |
| 수감자 빼내기 | **밧줄 없이** E → 일어나 플레이어를 따라옴 |
| 따라오는 수감자 되돌리기 | 위치로 갈림 — Jail 영역 안이면 착석, 밖이면 그 자리 정지 |

판정과 계상을 나눈 이유: 무고한 시민을 좌석까지 끌고 가야 오검거를 알게 되는 것은 헛수고 비용이 과하다. 문턱에서 갈리면 오검거는 되돌아 나가면 되고, 진범은 앉혀야 돈이 된다.

## 구조

### 신규 `JailIntake`

`Assets/Scripts/Interaction/JailIntake.cs`. 서버 권위. 장소 오브젝트라 App 파사드에 등록하지 않는다 — `JailLock`·`HqDropoffZone`과 같은 관례로 씬 탐색을 쓴다.

`JailZone`이 이미 257줄이라 여기에 출입 로직까지 넣으면 팀 리뷰 기준(250줄)을 넘고, 정산 책임과 출입 책임이 한 클래스에 섞인다. **`JailZone`은 대장(수용 인원·현상금·정산 레코드·좌석 소유), `JailIntake`는 출입구**로 나눈다.

서버에서 0.1초 간격으로 훑으며(`JailDoor`와 같은 주기) 규칙 두 개를 집행한다:

- **R1 판정** — `Escorted`/`Captured` NPC가 Jail 영역 안이고 아직 판정 전(`!IsDelivered`)이면 `ArrestJudge.Judge` 호출. 결과가 수감 대상(`WantedCriminal`/`Misdemeanor`)이면 대상으로 기록해 둔다.
- **R2 착석** — R1에서 수감 대상으로 기록된 NPC가 `Captured` 상태로 Jail 영역 안에 있으면 좌석을 배정해 앉히고 `Admit`한다.

명시적 요청 하나를 받는다:

- **반출(`ServerExtract`)** — `JailZone.ReleaseInmate` + `NpcController.ClearDelivered` + `SetSeated(false)` + `StartEscort(플레이어)`. 밧줄은 걸지 않는다. 수감 대상 기록에서도 지운다 — 남겨두면 재판정 결과가 오검거로 바뀌어도 R2가 옛 기록을 보고 앉힌다.

**R1과 R2의 순서가 강제다.** 한 틱 안에서 R1을 먼저 돌려야 한다. 반출 직후 플레이어가 유치장 안에서 바로 E를 눌러 되돌리는 경우, 같은 틱에 재판정(R1)과 착석(R2)이 함께 일어나야 한 프레임 늦게 앉는 것을 피할 수 있다.

Jail 영역 판정은 `JailDoor.IsInsideJailArea`가 쓰는 NavMesh Jail 마스크 샘플링을 재사용한다(공용 위치로 옮긴다). 새 트리거 콜라이더를 만들지 않으므로 문·통행권·좌석이 전부 같은 기준을 본다.

### 파일별 변경

| 파일 | 변경 |
|---|---|
| `JailIntake.cs` (신규) | R1·R2·반출 |
| `JailZone.cs` | `ReserveSeat`에 "기준 위치에서 가장 가까운 빈 자리" 오버로드 추가 |
| `ArrestJudge.cs` | `TryDeliver`·`m_dropoffZone` 제거, **판정 후 밧줄 강제 해제 블록 제거** |
| `NpcJailedState.cs` | **로직 변경 없음** — 주석만 갱신 (아래 참고) |
| `NpcStateRules.cs` | `CanDeliver` 제거, `HasInteractKeyAction`에 `Jailed` 추가 |
| `PlayerEscortCommands.cs` | `RequestDeliver`/`DeliverRpc`/`ServerDeliver` 제거, 반출 요청 RPC 추가 |
| `NpcSubdueInteractable.cs` | `Jailed` 대상 E 분기 추가 |
| `CustodyRouter.cs` | 수감 분기 제거 (오검거 폴백 석방만 남음) |
| `JailDoor.cs` | 자동 개폐 일체 제거, `IsInsideJailArea`는 공용으로 이관 |
| `PlayerEscorter.cs` | **변경 없음** — 유치장을 몰라도 된다 |

### 좌석까지의 마지막 몇 미터는 계속 걸어간다

폐기하는 것은 **"NPC가 도시에서 유치장까지 스스로 간다"**이지 "벤치까지 두 걸음 간다"가 아니다.

실측하면 놓은 자리에서 가장 가까운 빈 좌석까지가 **1.5~5.6m**다(방 중앙 1.76m, 문 앞 1.51m, 앞자리가 다 찼을 때 최대 5.61m). 이 거리를 워프로 처리하면 눈에 띄는 순간이동이 된다.

그래서 `NpcJailedState`는 **그대로 둔다**. 이미 #462에서 다듬어 둔 코드다 — 도착 판정 0.25m까지 걸어간 뒤 남은 오차만 워프로 흡수하고, 경로 실패·타임아웃에는 좌석으로 옮겨 앉히는 폴백이 있다.

#462의 입구 고착이 재발하지 않는 근거: 그 버그는 절차적으로 계산한 자리가 문↔셀 통로에 떨어져 생겼다. 지금 좌석은 손으로 배치해 통로를 비켜 있고, 걷는 거리도 방 하나 안이며, 실패해도 폴백이 좌석에 앉힌다.

### 판정 시 밧줄을 풀지 않는 이유

`ArrestJudge.Judge`는 판정 직후 `ReleaseDrag`로 밧줄을 강제로 푼다. 원래 `CustodyRouter`가 곧바로 `Jailed`로 전이시키는 것을 대비한 순서 강제였다.

그대로 두면 **문 통과 = 판정 = 자동 줄 풀림 = `Captured` = R2가 즉시 착석**이 되어 "E로 놓아야 앉는다"가 무효가 된다. 이제 판정 직후 아무도 상태를 전이시키지 않으므로 이 강제 해제를 제거한다. 판정 후에도 신병은 묶인 채 남고, 플레이어가 좌석 앞에서 E로 놓을 때 앉는다.

오검거는 판정 즉시 `WrongfulArrestPenalty`가 `Detained`로 전이시키므로, 밧줄은 `PlayerEscorter.TickTetherCleanup`의 기존 커스터디 이탈 정리가 끊는다.

## 데이터 흐름

### 넣기

```
밧줄로 묶어 끌기 (Escorted)
  → 문 앞에서 E로 문 열기 (JailDoor 토글)
  → 끌고 Jail 영역 진입
      [R1] ArrestJudge.Judge
           · 진범/위조범/난동꾼 → 수감 대상 기록, 밧줄 유지
           · 오검거          → WrongfulArrestPenalty가 Detained로 전이
  → 좌석 앞에서 E로 놓기 (기존 ReleaseDrag → Captured)
      [R2] 가장 가까운 빈 좌석 배정
           → SendToJail(seat) → Jailed → 좌석 정렬 후 착석 (#462)
           → JailZone.Admit(npc, bounty)   ← 정산·할당량 진행도 상승
```

### 빼내기

```
앉은 수감자 조준 → E (NpcSubdueInteractable)
  → PlayerEscortCommands 반출 RPC → JailIntake.ServerExtract
      · JailZone.ReleaseInmate       — 좌석 반납, 정산·진행도에서 빠짐
      · NpcController.ClearDelivered — 다시 넣으면 재판정 (#358)
      · SetSeated(false) + StartEscort(플레이어)   ← 밧줄 없음
  → 일어나 따라온다
```

추종은 새로 만들지 않는다. `NpcEscortedState`가 밧줄 이전의 수갑 연행 추종·근접 정지(#97)를 그대로 들고 있고 `IsRoped`일 때만 건너뛰므로, `StartRopeDrag` 없이 `StartEscort`만 부르면 추종·속도 부스트·거리 이탈이 전부 동작한다.

### 되돌리기와 거리 이탈이 같은 규칙으로 흡수된다

따라오는 수감자에게 E를 누르면 `StopEscort` → `Captured`가 된다. 멀리 걸어가 `BreakDistance`를 넘겨도 같은 `Captured`가 된다. 둘 다:

- Jail 영역 **안** → R2가 잡아 좌석에 앉히고 `Admit`
- Jail 영역 **밖** → 그 자리에 서 있음, 방치 타이머 정상 작동

별도 예외 처리가 필요 없다.

### 정산 타이밍의 실질

`Admit`이 착석 시점으로 가면서 GDD 9-2의 "이송 중 라운드 종료 시 보상 없음"이 코드와 정확히 일치한다. 반대로 **판정만 받고 앉히지 않으면 0원**이다 — 문턱까지만 끌고 와 방치하면 수익이 없다. 이것이 "직접 넣게 만든다"의 실질적 강제력이다.

## 제거 대상

**코드**

- `HqDropoffTerminal.cs` · `HqDropoffZone.cs` (+ `.meta`)
- `ArrestJudge.TryDeliver` · `m_dropoffZone` · 판정 후 밧줄 강제 해제 블록
- `PlayerEscortCommands.RequestDeliver` / `DeliverRpc` / `ServerDeliver`
- `NpcStateRules.CanDeliver`
- `CustodyRouter`의 수감 분기
- `JailDoor`의 자동 개폐 — `IsJailBoundNpcNear` · `m_npcWasEnRoute`(#457) · `m_autoOpenRadius` · `m_proximityCheckInterval`

`NpcJailedState`는 제거 대상이 아니다 — 위 "좌석까지의 마지막 몇 미터" 참고.

**씬 (`Main Scene.unity`)**

- 인계 단말 · 인계 구역 오브젝트

#462와 같은 씬을 또 건드리므로 PR 본문에 삭제한 오브젝트 이름을 반드시 명시한다(팀 리뷰 규칙 — 씬 diff는 리뷰어가 읽을 수 없다).

## 경계 처리

| 상황 | 처리 |
|---|---|
| 판정 불가 NPC (신원·경범죄 마커 둘 다 없음) | `Judge`가 null을 반환하고 `MarkDelivered`도 안 불려 매 폴링마다 재시도 → 로그 폭주. **실패 대상을 기억해 한 번만 시도한다** |
| 좌석 정원(18석) 초과 | #462 규칙 유지 — 겹쳐 앉히고 경고 |
| 착석 직전 대상 파괴 (라운드 종료 잔류 정리) | Unity 가짜 null 검사 — `JailZone`의 기존 관례 |
| 자물쇠 재잠금이 플레이어를 가둠 | 자동 개폐 제거로 E 토글 래치가 유지되어 갇히지 않는다. 문이 열린 채 남으므로 직접 닫아야 한다 |
| 탈옥(#231) | `JailbreakEvent`의 방출이 반출과 같은 조작(`ReleaseInmate`+`ClearDelivered`)이라 그대로 동작 |

## 구현 중 실측이 필요한 것

**오검거의 원한 구역 경로.** Jail은 별도 NavMesh 영역이라 통행권이 없으면 경로가 안 잡힌다(#415). 오검거가 Jail 영역 안에서 `Detained`로 전이할 때 원한 구역까지 경로가 실제로 잡히는지 코드로는 확신할 수 없다.

막히면 대안은 **판정 지점을 문 바깥으로 살짝 빼는 것** — 문 앞 접근 지점 반경에서 판정하면 오검거가 애초에 Jail 영역에 들어가지 않는다.

## 검증

Unity Test Framework에 테스트가 없어 자동 검증 수단이 없다. Editor Play + Multiplayer Play Mode 2인(호스트+클라)으로 확인한다.

- [ ] 진범을 끌고 문 통과 → 판정 로그가 뜨고 **밧줄은 유지**된다
- [ ] 그 상태로 라운드를 끝내면 **진행도 금액이 오르지 않는다** (판정≠계상 확인)
- [ ] 좌석 앞에서 E로 놓으면 가장 가까운 빈 좌석에 앉고 그때 금액이 오른다
- [ ] 오검거를 끌고 문 통과 → 문턱에서 판정되고 페널티로 전이, 원한 구역까지 이동한다
- [ ] 앉은 수감자에 E → 일어나 따라온다 (밧줄 없음), 진행도 금액이 즉시 줄어든다
- [ ] 따라오는 수감자에 유치장 안에서 E → 다시 앉고 금액이 회복된다
- [ ] 따라오는 수감자에 유치장 밖에서 E → 그 자리에 서고 방치 타이머가 돈다
- [ ] 따라오는 수감자를 두고 멀리 가면 거리 이탈로 정지, 유치장 안이었으면 다시 앉는다
- [ ] 문이 E 토글로만 여닫히고, 수감자를 데리고 드나드는 동안 갇히지 않는다
- [ ] 정원 18석까지 채워도 입구가 막히지 않는다 (#462 회귀)
- [ ] 탈옥(#231) 방출·재검거가 정상 동작한다
- [ ] 클라이언트 화면에서도 착석·기립 표현이 맞다 (`IsSeated` 동기화)

## 문서 갱신

- **GDD 7-2** — "인계존에서 판정 → 유치장으로 스스로 걸어가 수용" 전면 수정
- **GDD 10-2** `Jailed` 설명 — "인계 판정 후 유치장으로 이송·수용"에서 이송이 빠진다
- **GDD 용어집** — 인계존 항목 제거, 유치장 항목 수정
- **GDD 7-6** — 타이머 동작은 그대로지만, 면제 기준을 설명하는 문장("본부 인계 판정이 끝난 대상은 제외")이 가리키는 지점이 인계존에서 유치장 진입으로 바뀌었다. 문구만 수정
