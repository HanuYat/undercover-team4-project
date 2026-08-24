# 세력 소탕 이벤트 — 설계 (#721)

> 작성 2026-08-24. 유치장에 갇힌 범인의 세력이 복수대를 보내 현장을 덮치는 돌발 이벤트.
> 여럿이 붙어야 풀리는 합동 이벤트를 **수적 압박** 하나로 만든다.

## 1. 이슈 본문과의 차이 (먼저 읽을 것)

#721 본문은 협동 강제 방식으로 세 후보(수적 압박 / 동시 조작 / 역할 분담)를 제시하고, 완료 기준에
"세력 무리가 **특정 구역을 점거**한다"를 두었다. **이 설계는 수적 압박만 채택하고 구역 점거를 뺀다.**

| #721 완료 기준 | 이 설계 |
|---|---|
| 세력 무리가 특정 구역을 점거한다 | **폐기** — 점거 대신 복수대가 현장으로 몰려온다 |
| 혼자서는 못 풀고 여럿이 붙어야 풀린다 | 수적 압박 (§3 표적 배분) |
| 인원이 적은 판에서도 잠기지 않는다 | §7 참고 — 라운드 표만 보므로 별도 장치 |
| 성공 시 보상이 손해를 납득시킨다 | 기존 경범죄 수익 경로 (§5) |
| 실패해도 라운드가 즉시 끝나지 않는다 | 충족 — HP만 깎는다 |

본부 원격문·CCTV 역할 분담도 이번 범위 밖이다. **이슈 본문을 이 방향으로 수정해야 한다.**

## 2. 무엇이 일어나는가

라운드 후반, **유치장에 갇힌 범인 중 한 명의 세력**을 골라 그 세력 조직원 N명이 복수하러 온다.

- 한 지점에서 **덩어리로** 등장해 현장으로 몰려온다.
- **전원이 현장 플레이어 한 명**을 표적으로 잡는다.
- 파이프를 들고 동네 깡패(#806)와 **같은 방식**으로 때린다 — 텔레그래프 스윙, 정면 부채꼴 판정.
- **기절시켜도 일어나 다시 쫓아온다.** 죽이거나 밧줄로 묶어 연행해야 끝난다.
- N은 라운드가 오를수록 늘어난다.

## 3. 확정된 설계 결정

| 항목 | 결정 | 근거 |
|---|---|---|
| 협동 강제 방식 | **수적 압박만** | 동시 조작은 현장 2인 판에서 잠기고, 역할 분담은 본부 작업이 커진다 |
| 종료 조건 | **사망 또는 밧줄 연행** | 둘 다 열어 두면 기존 경범죄 흐름을 그대로 태울 수 있다 |
| 인원 스케일링 | **라운드만 본다** | 표 하나로 끝나고 예측 가능하다. 현장 인원은 안 본다 |
| 스폰 형태 | **한 덩어리** | "서쪽에서 넷 온다"가 눈으로도 무전으로도 읽힌다 |
| 표적 배분 | **전원이 한 명을** | 표적이 혼자 못 버텨 동료가 몰려와야 한다 — 협동 강제의 실체 |
| 외형 | **전원 `Muscle_Male_01`** | 같은 얼굴 N명이 한 방향에서 오면 "무리"로 즉시 읽힌다 |
| 세력별 외형 구분 | **없음** | 싸우는 데 어느 세력인지 알 필요가 없다 |
| 발동 빈도 | **라운드당 1회** | 이벤트가 자체 판정 (§4) |

### 외형 선택 노트 — 오검거 위험은 없다

`Muscle_Male_01`은 `AppearanceModelCatalog`에서 `NonHumanoid`가 아니라 **범인·디코이로도 나올 수 있는
모델**이다. 검토 중 "교전 도중 진범과 헷갈려 오검거가 난다"는 우려가 나왔으나 **성립하지 않는다** —
[`ArrestJudge.TryResolveVerdict`](../../../Assets/Scripts/Interaction/ArrestJudge.cs)가 신원 대조보다
`MisdemeanorOffender` 마커를 **먼저** 보기 때문이다. 복수대 조직원은 스폰 시 마커가 붙으므로 잡으면
`ArrestVerdict.Misdemeanor` + 경범죄 수익이고, **오검거로 집계되지 않는다.**

남는 것은 순간적 혼동뿐인데, 그것도 **파이프를 들고 달려온다**는 점으로 구분된다.

## 4. 트리거

```
CanTrigger() =
    RoundProgress.Current >= 발동 하한 라운드      (인스펙터)
    && 이번 라운드에 아직 발동하지 않음
    && JailZone.Inmates 중 Faction != None 인 자가 1명 이상
    && 현장 플레이어 존재                          (SuddenEventUtil.FindRandomFieldPlayer)
```

수감자 목록에서 `CitizenIdentity.Profile.Faction`을 읽어 `None`이 아닌 세력 중 하나를 무작위로 고른다.
전원 무소속이면 발동하지 않는다.

**라운드당 1회는 이벤트가 스스로 막는다.** `SuddenEventManager`에는 횟수 제한 개념이 없고(주기 추첨만
한다) 그쪽에 넣으면 모든 이벤트에 영향을 주므로, `RoundProgress.Current`를 기억해 이 이벤트 안에서
판정한다. 매니저는 건드리지 않는다.

## 5. 재사용 — 새 코드가 거의 없다

| 필요한 것 | 이미 있는 것 |
|---|---|
| 표적 추격 + 텔레그래프 스윙 | `NpcResistState` (#806이 검증) |
| 포기하지 않는 추격 | 전용 `NpcResistConfig` 에셋 (깡패가 쓰는 방식) |
| 파이프 + 스윙 모션 | `NPC_StreetThug` 프리팹 + `NPC_StreetThug.overrideController` |
| 스폰 지점 찾기·경범죄 마커·수익 롤·잔류·라운드 정리 | `SpawnedNpcEventBase` |
| 밧줄 → 유치장 → 경범죄 수익 | `ArrestJudge` 마커 경로 |
| 죽이기 | 진압봉으로 HP 0 → `NpcState.Dead` |
| 공격 사운드 | #817 (PR #822) — `NpcResistState`를 타므로 자동 적용 |
| 라운드별 수치 표 | `RoundWeatherTable` 선례 |

## 6. 신규·수정 작업

### ① 기절 후 저항 복귀 — `NpcReaction`

지금 [`NpcStunnedState`](../../../Assets/Scripts/NPC/States/NpcStunnedState.cs)가 깨어날 때
`ResumeReaction`을 부르고, 그것이 도주를 건다:

```csharp
public void ResumeReaction(Transform threat)
{
    if (IsSprinter && !m_owner.Custody.HasReleaseDestination)
        StartSprint();
    else
        StartFlee(threat);
}
```

**공연음란범이 "한 대 맞았다고 그만두지 않는" 장치(`IsSprinter`)가 이미 같은 자리에 있다.** 같은 형태로
`IsRelentless`를 하나 더 두고 저항으로 복귀시킨다. `IsSprinter`와 동일하게 `StartFlee`/`StartSprint`/
`StartResist` 진입 시 값을 정리한다.

`NpcStunnedState`는 수정하지 않는다 — `ResumeReaction`이 알아서 갈린다.

### ② 다인 스폰 — `SpawnedNpcEventBase`

- `virtual int SpawnCount => 1` 추가.
- 내부 `m_npc`(단수)를 리스트로 바꾼다.
- `protected abstract void ApplyBehavior()` → `ApplyBehavior(NpcController npc)`. NPC마다 호출된다.
- 상태 구독(`OnStateChanged`)·판정 수신·잔류·정리를 리스트 전체에 대해 돌린다.
- **종료 판정**: 전원이 이벤트에서 손을 뗐을 때(사망·판정·잔류) `IsActive`가 false가 된다.

**기존 3종은 호출부 한 줄씩만 바뀐다** (`m_npc.` → `npc.`). `SpawnCount`가 1이라 동작은 동일하다.

### ③ 신규 파일

| 파일 | 내용 |
|---|---|
| `FactionRevengeEvent.cs` | `SpawnedNpcEventBase` 상속. 세력 선택·라운드 1회 게이트·덩어리 스폰·표적 고정 |
| `FactionRevengeTable.cs` | ScriptableObject — 라운드 → 조직원 수 |
| 전용 `NpcResistConfig` 에셋 | 포기 거리를 크게. 공용 에셋은 시민 저항형이 쓰므로 건드리지 않는다 |

### ④ 덩어리 스폰

`SuddenEventUtil.TryFindSpawnPositionNear`로 **앵커 지점 하나**를 잡고(기존 `hiddenFromPlayers: true`
유지 — 눈앞 팝인 방지), 그 주위에 N명을 배치한다. 각 배치점은 앵커에서 짧은 반경 안으로 `NavMesh.SamplePosition`
한다. 실패한 자리는 앵커 자신으로 폴백한다(겹쳐 서더라도 스폰 자체가 불발되지 않게).

### ⑤ 표적 고정

`ApplyBehavior(npc)`에서 전원에게 **같은** `m_threat`를 넘긴다 (`npc.Reaction.StartResist(m_threat)`).
표적이 다운되면 `NpcResistState.IsStillEngaged`의 기존 규칙대로 각자 근처 플레이어로 넘어간다 — 쓰러진
사람을 계속 때려 구조를 막지 않는다.

## 7. 알려진 위험·미해결

- **회귀 위험은 ②뿐이다.** 기존 이벤트 3종(동네 깡패·공연음란범·소매치기)의 베이스 클래스를 수술한다.
  소매치기가 `m_npc`를 가장 많이 쓰지만 전부 1인 전제라 `SpawnCount=1`이면 동작이 같다. **3종 회귀
  확인이 필요하다.**
- **소인원 판 스케일링은 라운드 표에 맡긴다.** #721 완료 기준의 "인원이 적은 판에서 잠기지 않을 것"은
  현장 인원을 보지 않기로 했으므로 **표 값 자체로** 맞춰야 한다. 플레이 테스트로 조정.
- ⏸ **수치 전부 보류** (GDD 12장) — 발동 하한 라운드, 라운드별 인원, 포기 거리, 앵커 반경. 인스펙터 노출.
- ⏸ **보상 설계는 기존 경범죄 수익을 그대로 쓴다.** N명분이 쌓이므로 총액은 자동으로 커지지만, 그것이
  "소탕에 쓴 시간"을 납득시키는 수준인지는 미검증.
- **무한 추격의 출구가 죽음·연행뿐이다.** 소란 지속 타이머(`m_maxLifetimeSeconds`)를 0(무제한)으로 두므로
  방치하면 라운드 끝까지 따라온다. 이것이 의도지만, 플레이 테스트에서 지나치면 타이머를 열면 된다.
