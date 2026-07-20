# NPC 인계 방치 시 수갑 해제 후 도주 (#230)

- **이슈:** #230
- **브랜치:** `feature/230/npc-captured-escape-timer` (base: `feature/213/npc-flee-multi-threat`)
- **작성일:** 2026-07-16
- **선행 의존:** #213 (PR #238) — `NpcController.ThreatSearchRadius`, 다중 위협 회피 도주

## 목적

체포(`Captured`) 상태 NPC는 현재 **스스로 빠져나가지 않는 최종 상태**다. 그래서 수갑만 채워두고 방치하면 NPC는 영원히 그 자리에 서 있고, "일단 여러 명 수갑 채워놓고 나중에 한꺼번에 인계" 전략이 성립한다.

이 규칙은 **방치를 차단**한다 — 잡았으면 바로 데려가게 만든다.

> GDD 7장에 없는 신규 규칙. 확정 후 GDD 검거 시스템에 추가 필요 (이슈 완료 기준).

## 규칙 (확정)

| 항목 | 값 | 근거 |
|---|---|---|
| 방치 타이머 | **30초** | 라운드 제한시간 기본값 180초의 1/6. 방치 차단이 목적이므로 짧게 — "잠깐 놓고 다른 용의자 보고 오지"를 확실히 징벌한다. |
| 경고 구간 | **마지막 5초** | 소란 펄스로 예고. 예고 없이 사라지면 플레이어가 버그로 오해한다. |
| 타이머 리셋 | **`Captured` 진입마다** | 리셋하려면 직접 걸어가 E를 눌러야 하니 꼼수 가치가 낮다 — 거기 있으면 그냥 데려가면 된다. |
| 적용 대상 | **반응 유형 무관 전원** | 순응형 무고 시민도 방치하면 수갑 풀고 간다. 판정 회피 목적의 고의 방치는 어차피 할당량 손해라 악용 가치가 없다. |
| 재검거 | **가능** | 도주 → 제압 홀드 → 다시 `Captured` → 타이머 30초 리셋. 인계하면 끝나므로 무한 루프가 아니다. |
| 판정 완료 NPC | **타이머 제외** | 인계 후 본부에 남는 NPC는 유치장 시스템(#228)이 가져갈 몫이다. **판정 전까지만 건드린다.** |

### 경고를 소란으로 하는 이유

"수갑 풀려고 몸부림치는 소동"은 기존 소란 시스템(#81 `BroadcastDisturbance`)의 의미와 정확히 맞다. 주변 시민이 패닉해 흩어지므로 **현장(직접 목격)과 본부(CCTV) 양쪽이 눈치챈다** — 본부·현장 소통이라는 게임 컨셉과도 맞고, 기존 API 재사용이라 구현이 거의 공짜다.

## 컴포넌트

### 1. `NpcCapturedState` — 타이머 소유

상태의 행동은 상태 클래스가 갖는 기존 구조를 따른다. `Enter()`가 곧 리셋이므로 리셋 정책이 공짜로 성립하고, 별도 `Exit()` 정리가 필요 없다.

```
Enter()  : m_escapeTime = Time.time + CapturedEscapeSeconds
Tick()   : IsDelivered면 아무것도 안 함 (판정 완료 = 유치장 몫)
           남은 시간 <= 경고 구간이면 RequestDisturbancePulse()
           만료되면 → 도주 전환
Exit()   : 기존 그대로 (Agent.isStopped 복구)
```

**도주 전환**은 `NpcResistState.Defeat`과 같은 패턴을 쓴다:

```
threat = SuddenEventUtil.FindNearestFieldPlayer(위치, ThreatSearchRadius)
threat != null → StartFlee(threat)   // #213 다중 위협 회피 로직을 그대로 탄다
threat == null → Idle                // 근처에 아무도 없다 = 조용히 탈출, 배회 복귀
```

반경을 `ThreatSearchRadius`로 쓰는 것이 요점 — 저항 폴백(#205)·도주 회피(#213)와 **같은 기준**이어야 "도망칠 상대"와 "피할 상대"가 어긋나지 않는다.

### 2. `NpcController` — 튜닝값 · 인계 플래그 · 소란 훅

| 추가 | 내용 |
|---|---|
| `m_capturedEscapeSeconds` | 기본 30f, `SerializeField` + Tooltip |
| `m_capturedEscapeWarningSeconds` | 기본 5f, `SerializeField` + Tooltip |
| `IsDelivered` / `MarkDelivered()` | 인계 판정 완료 여부. 서버(또는 오프라인) 전용 — 판정 자체가 서버 권위라 동기화 불필요 |
| `RequestDisturbancePulse()` | 기존 `EmitDisturbancePulse`에서 추출. 주기 스로틀(`m_nextDisturbancePulseTime`)을 공유하므로 펄스 타이머가 둘로 갈라지지 않는다 |

`EmitDisturbancePulse`는 상태 검사(Run/Attack) 후 `RequestDisturbancePulse()`를 호출하도록 바뀐다 — 동작 변화 없음.

### 3. `ArrestJudge` — static 제거

`public static HashSet<NpcController> JudgedNpcs`를 없애고 `npc.MarkDelivered()` / `npc.IsDelivered`로 대체한다.

부수 효과로 기존 해킹이 사라진다: 이 static은 씬 재로드 시 잔존해 `OnDestroy`에서 수동으로 `Clear()`해야 했다. 플래그가 NPC에 붙으면 NPC와 함께 죽으므로 그 문제 자체가 없어진다.

### 4. `HqDropoffZone` — 조회 경로 교체

`ArrestJudge.JudgedNpcs.Contains(npc)` → `npc.IsDelivered`.

### 5. `docs/GDD.md` — 신규 규칙 등재

GDD 7장(검거 시스템)에 **7-6. 인계 방치** 절을 7-5 다음에 추가한다. 내용상으로는 7-2(검거 후 처리 — "넘기면 어떻게 되는가")의 짝이지만, 중간에 끼워 넣으면 7-3~7-5를 전부 재번호해야 해 문서 전반의 상호 참조가 깨진다. 끝에 붙이고 7-2에서 상호 참조만 건다.

담을 내용: 체포 상태 방치 30초 → 수갑 해제 후 도주, 마지막 5초 소란 경고, 재검거 가능, 판정 완료분은 제외(유치장 #228 몫). GDD의 기존 표기 관례를 따라 **팀 검토를 거치지 않은 값(30초/5초)은 `(초안 — 팀 검토 필요)`로 표기**한다 — 플레이 테스트로 조정될 값이다.

## 데이터 흐름

```
Captured 진입  (다섯 경로가 전부 여기로 모인다)
 ├─ 수갑 채널링 성공 (순응형)
 ├─ 저항(Attack) 제압 성공
 ├─ 도주(Run) 제압 홀드 성공 — CaptureBySubdue
 ├─ 연행 놓기 (E) — Release → StopEscort
 └─ 연행 중 8m 이탈 자동 해제
      │
      ▼
 30초 타이머 시작 (Enter)
      │
      ├─ 25초 경과 → 소란 펄스 (주변 시민 패닉 = 플레이어·본부에 경고)
      │
      ├─ 재연행(E) → Escorted → 다시 놓으면 타이머 리셋 (Enter 재진입)
      │
      ├─ 인계 성공: Escorted로 존 도달 → Judge → MarkDelivered() → Release()
      │              → Captured로 떨어지지만 IsDelivered라 타이머 안 돎 (유치장 #228 몫)
      │
      └─ 30초 만료 → StartFlee(가장 가까운 플레이어) → Run
                     (없으면 Idle — 배회 복귀)
```

## 서버 권위 · 동기화

- FSM `Tick`은 서버 전용이므로 타이머도 서버에서만 돈다 (기존 구조).
- 도주 전환은 `m_networkState`를 통해 전 피어에 전파된다 (기존).
- `IsDelivered`는 **서버 전용 plain 필드**로 충분하다 — 판정(`ArrestJudge`)도 인계존 게이트도 이미 서버 전용이고, 기존 `JudgedNpcs` static도 실질 서버 전용이었다. 클라이언트가 읽을 필요가 생기면 그때 `NetworkVariable`로 승격한다.

## 공짜로 해결되는 것

- **라운드 종료 freeze** — `m_frozen`이 `Update`에서 `Tick`을 막으므로 타이머가 자동으로 멈춘다. 추가 작업 없음.
- **수갑 해제 연출** — `Captured` → `Run` 전환 시 `NpcAnimationDriver`가 수갑 포즈를 도주 애니메이션으로 바꾼다. 별도 작업 없음.

## 범위 밖

- **수갑 회수 규칙 (#229)** — 수갑 소모·반환 라이프사이클은 미구현(Backlog·다른 담당)이라 도주 시 회수는 현재 실질 no-op. #229 구현 시 이 도주 전환 지점에 붙이면 된다.
- **유치장 (#228)** — 판정 후 본부에 남는 NPC의 처리. `IsDelivered`가 그 트리거로 재사용될 수 있다.
- **남은 시간 UI** — 경고는 소란으로만. UI 노출은 별도 이슈.

## 알려진 갭

신원(`CitizenIdentity`)도 경범죄 마커(`MisdemeanorOffender`)도 없는 NPC는 `Judge`가 `null`을 반환해 `IsDelivered`가 찍히지 않는다. 따라서 본부에 놔두면 30초 뒤 탈출한다.

**기존 static 방식도 동일하게 뚫려 있던 갭**이고, 해당 NPC는 이미 `LogWarning`이 뜨는 비정상 개체(프리팹 구성 오류)라 이번 범위에서는 고치지 않는다.

## 검증

Play 모드(NPCMove 씬)에서 확인한다. `execute_code`는 호출마다 도메인 리로드가 일어나 런타임 상태가 초기화되므로 **한 호출에서 세팅+관찰까지 끝낸다**. 30초 실시간 대기는 불가능하므로 리플렉션으로 만료 시각을 과거로 밀어 관찰한다.

- [ ] 체포 후 타이머 만료 → `Run` 전환, 위협은 가장 가까운 플레이어
- [ ] 근처에 플레이어가 없으면 → `Idle` 복귀
- [ ] 판정 완료(`MarkDelivered`) NPC는 만료돼도 `Captured` 유지
- [ ] 재연행 후 다시 놓으면 타이머 리셋
- [ ] 경고 구간에서 소란 펄스 발생 (주변 시민 `Panic` 전이로 확인)
- [ ] 라운드 freeze 중 타이머 정지
- [ ] 기존 인계 판정 흐름 회귀 없음 (`JudgedNpcs` 제거 후 중복 판정 방지 유지)
- [ ] GDD 7-6 절 추가 (문서 작업 — 코드 검증 대상 아님)

MPPM 다중 클라이언트 동기화는 #213과 마찬가지로 미검증 — 서버 권위 구조는 유지하나 실제 피어 확인은 못 한다.

## 완료 기준 (이슈 #230)

- [ ] 인계 방치 타이머 초과 시 NPC가 수갑 해제 후 도주
- [ ] 타이머·재검거 규칙 확정 (GDD 반영)
- [ ] 서버 권위로 동작하고 전 피어 동기화
