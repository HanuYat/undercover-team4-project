# #836·#869 처치 알림 + 사망 표식 — 진행 기록

## 배경

- **#869**: 막타를 친 사람 화면에 처치 대상 이름을 띄운다.
- **#836**: 기절(다운)과 사망을 화면에서 구분한다.

처음엔 #836을 "죽으면 몸 색을 어둡게 다크닝한다"(`DeadBodyMark`)로 구현했으나, 팀 리뷰에서 "갑자기 피부색만 바뀌는 게 어색하다"는 피드백이 나와 **이펙트 없이 HUD 텍스트 + 스캔카드 아이콘**으로 방향을 바꿨다. 아래는 최종 방향과 그 과정에서 잡은 사이드 버그 기록이다.

## 최종 구조

### 1. 처치 알림 (#869) — [CrosshairUI.cs](../Assets/Scripts/UI/Hud/CrosshairUI.cs)

막타를 친 사람 화면에 대상 이름이 뜨고, 일정 시간 뒤 UniTask 기반으로 서서히 페이드아웃된다(`FadeKillLabelAsync`). 연속 처치 시 이전 페이드는 취소하고 새로 시작한다([VerdictBanner.cs](../Assets/Scripts/UI/Hud/VerdictBanner.cs)와 같은 `CancellationTokenSource` 패턴).

집계·통지는 [PlayerKillCredit.cs](../Assets/Scripts/Player/PlayerKillCredit.cs)가 담당 — 사망 판정 지점(`NpcDeath.ServerEnterDead`, `PlayerHealth`의 확인사살 분기)에서 서버가 가해자 쪽에만 RPC로 알린다.

### 2. 사망 표식 (#836) — 몸 이펙트 폐기, 스캔카드로 이전

- **`DeadBodyMark.cs` 삭제** — 몸 색 다크닝(`BodyTint.SetSustainedDarken` 등 관련 API 포함) 통째로 되돌림. NPC 4종·Player 프리팹에서 컴포넌트 제거.
- 대신 **스캔카드에 생사 아이콘**을 추가 — 죽었는지 살았는지는 스캐너로 확인하는 정보로 통합.

### 3. 스캔카드 재설계 — [ScanInfoView.cs](../Assets/Scripts/UI/ScanInfoView.cs) · [ScanInfoCard.prefab](../Assets/Prefabs/NPC/ScanInfoCard.prefab)

같은 김에 스캔카드 자체를 정리했다:

- **세력(Faction) 표시 삭제** — 텍스트 라벨 + 문양 이미지 전부 제거. 팀 UI로 이미 대체됐다는 판단.
- **타입(인간/안드로이드)** — 텍스트 → 아이콘.
- **생사 상태** — 신규 아이콘(살아있음/사망).
- **미스캔 표현을 값 마스킹(`??`)에서 전체 교체 방식으로 변경**:
  - 미스캔: 배경을 검정으로 칠하고 큰 "?" 하나만 표시 (`MaskedText`)
  - 스캔 완료: 이름 + 아이콘 묶음 표시 (`Content`)
- **레이아웃**: `Content`(VerticalLayoutGroup, 이름+아이콘묶음) 안에 `IconsRow`(HorizontalLayoutGroup, 타입+생사 아이콘)를 중첩.

```
ScanInfoCard
  Background (Image — 마스킹 여부로 색 토글)
    MaskedText ("?" 큰 글씨, 미스캔 시에만 활성)
    Content (VerticalLayoutGroup, 스캔 완료 시에만 활성)
      NameText
      IconsRow (HorizontalLayoutGroup)
        TypeIcon
        StatusIcon
```

생사 상태는 `CitizenProfile`이 아니라 런타임 값(`NpcController.Death.IsDead`, 전 피어 동기화)에서 온다. [ScanResultPresenter.cs](../Assets/Scripts/UI/ScanResultPresenter.cs)가 조준 중인 대상이 있을 때만 매 프레임 `IsDead`를 비교해 변경 시에만 갱신한다 — `NpcDeath.OnDied`는 서버 전용 이벤트라 클라 로컬인 이 클래스에서는 못 쓴다.

아이콘 스프라이트(사람/안드로이드/생존/사망)는 이미 배정 완료.

## 사이드 버그: 프리팹 인스턴스 오버라이드가 base 수정을 막고 있었다

`ScanInfoCard.prefab`을 NPC_Citizen 등 4개 NPC 프리팹에 중첩 인스턴스로 넣을 때, 그 순간의 위치·크기 값(`SizeDelta 320×170`, `AnchoredPosition (0, 2.7)`)이 **4개 프리팹 전부에 개별 오버라이드로 박혀** 있었다. 오버라이드가 걸리면 base 프리팹 값이 바뀌어도 그 인스턴스는 따라가지 않는다 — Height를 100으로 고쳐도 스폰된 NPC는 계속 170을 썼던 원인.

**조치**: NPC_Citizen·NPC_Citizen_Generic·NPC_Streaker·NPC_StreetThug 4개 프리팹에서 `ScanInfoCard` 루트 RectTransform에 걸린 위치·크기 오버라이드를 전부 제거(Editor의 "Revert"와 동일 효과). 이제 `ScanInfoCard.prefab`을 고치면 4곳 전부에 바로 반영된다.

## 현재 값

- 크기: 원래의 80% (`localScale 0.006 → 0.0048`)
- 위치: 머리 위(`0, 2.7`) → 옆(`0.8, 1.6`)

## 남은 일 (내일 이어서)

- [ ] 실제 스폰된 NPC 옆에서 `(0.8, 1.6)` 위치가 자연스러운지 눈으로 확인 — 아직 비주얼 검증 안 함
- [ ] 기절/눕기 자세일 때의 카드 위치(`m_proneOffset`)는 이번에 손대지 않음 — "옆에 보이면 좋겠다"는 요청이 선 자세 기준이었어서 그대로 둠. 필요하면 같이 조정
- [ ] Multiplayer Play Mode로 여러 클라이언트에서 스캔카드 생사 아이콘이 제대로 갱신되는지 확인
- [ ] PR 아직 안 올림 — 이 브랜치는 처음 push하는 것이라 업스트림도 없음
