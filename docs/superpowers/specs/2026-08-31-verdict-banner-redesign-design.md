# 검거 결과 배너 — 톤 리디자인 (#943)

> 작성 2026-08-31. "검거 결과 UI 교체"(#943) — 진범 검거/오검거/경범죄/생포조건불충족을 한눈에 구분되게 하고, 프리팹화까지 함께 정리한다.
>
> **갱신 (구현 후)**: 아래 §2·§3은 처음 합의한 "아이콘 배지 + 색" 안이다. 실제로 붙여 보니 아이콘이
> 안 어울려 뺐다 — 최종 구현은 **색만으로 구분**한다: 테두리는 판정 원색, 채움은 그보다 어둡게
> 칠해 두 톤이 뚜렷이 갈리게 한다. 아이콘 관련 서술은 기록으로 남기고 취소선으로 표시한다.

## 1. 지금 문제

- [`VerdictBanner`](../../../Assets/Scripts/UI/Hud/VerdictBanner.cs)는 4종 판정을 `m_toneTarget.color` 하나로만 구분한다 — 아이콘·레이아웃·애니메이션 차이가 전혀 없어 한눈에 구분이 안 된다(#943).
- 전용 프리팹이 없다 — `Map_Apocalypse.unity` / `Map_Cyberpunk.unity` / `Tutorial.unity` 세 씬에 각각 직접 박혀 있어, 값 하나 바꾸려면 세 곳을 따로 고쳐야 한다.
- 등장/퇴장 애니메이션이 없다 — `OpenPanel`/`ClosePanel`이 `SetActive`를 즉시 토글한다.

## 2. 시각 디자인

~~아이콘 배지(원형, 판정색으로 꽉 채움) + 카드 배경(판정색 22% 틴트 + 1px 테두리)으로 색과 아이콘 이중 구분한다.~~
~~아이콘은 새로 만들지 않고 이미 프로젝트에 임포트된 `Assets/Imported/GUIPack-Clean&Minimalist/.../UI/Basic/`의 Checkmark/X/Info/Alert를 그대로 쓴다.~~
→ 실제로 붙여 보니 아이콘이 어울리지 않아 뺐다. **최종: 테두리(원색) + 채움(어둡게, 9-sliced 라운드 카드)**만으로 구분한다.

| 판정 (`ArrestVerdict`) | 테두리(원색) | 채움 |
|---|---|---|
| 진범 검거 (`WantedCriminal`) | 초록 `#4ADE80` | 같은 색을 검정 쪽 25%로 섞고 알파 0.55 |
| 오검거 (그 외 기본값) | 진한 레드 `#DC2626` | 〃 |
| 경범죄 (`Misdemeanor`) | 회색 `#9CA3AF` | 〃 |
| 생포조건 불충족 (`ConditionUnmet`) | 주황 `#FBBF24` | 〃 |

## 3. 컴포넌트 구조

~~`VerdictBanner.cs` 계층에 아이콘 배지 하나만 추가한다.~~ → 아이콘 없이 카드 톤 두 겹으로 구분한다. 텍스트·로컬라이즈·판정음 로직은 그대로 둔다.

- 루트의 기존 `Image`(`m_toneTarget`)를 카드 테두리로 쓰고, 그 안쪽에 인셋을 준 `Fill`(`m_fillTarget`) 자식 Image를 채움으로 추가했다. 둘 다 9-sliced 둥근 코너 스프라이트(`UI/Skin/Background.psd`, `pixelsPerUnitMultiplier` 축소로 더 둥글게)를 쓴다.
- `VerdictToColor()`가 반환한 원색을 테두리에 그대로, `Color.Lerp(tone, Color.black, m_fillDarken)`(기본 0.25) 결과를 알파 `m_fillAlpha`(기본 0.55)로 채움에 칠한다.
- 제목·상세 텍스트는 `TextAlignmentOptions.Center`로 배너 전체 폭 기준 가운데 정렬한다.

## 4. 애니메이션

[`TimedMessageView`](../../../Assets/Scripts/UI/Hud/TimedMessageView.cs)처럼 `Update()` + `Time.time`으로 만들면 정산(라운드 종료) freeze 중에 멈춰 버린다 — `VerdictBanner.AutoHideAsync`는 이미 `ignoreTimeScale: true`로 그 상황에서도 흐르게 만들어 둔 성질이 있으므로, 애니메이션도 같은 UniTask(`ignoreTimeScale: true`) 방식으로 넣는다.

- 등장: 스케일 0.9 → 1.0 + 알파 0 → 1, 약 0.15~0.2초 (펀치인)
- 퇴장: 알파 1 → 0 약 0.2초, 끝난 뒤 실제 `SetActive(false)` (`ClosePanel` 오버라이드)
- 연속 판정이 들어오면 기존 `m_hideCts` 취소 패턴을 애니메이션에도 적용해 겹치지 않게 한다.
- `CanvasGroup` + `RectTransform.localScale`로 구현한다 — 프로젝트에 DOTween 등 트위닝 라이브러리가 없으므로 새로 들이지 않는다.

## 5. 프리팹 전환

`Assets/Prefabs/UI/VerdictBanner.prefab`을 새로 만들어 세 씬(`Map_Apocalypse` / `Map_Cyberpunk` / `Tutorial`)의 기존 임베드 오브젝트를 지우고 프리팹 인스턴스로 교체한다.

재배선은 필요 없다 — [`ArrestVerdictFeedback.cs:176`](../../../Assets/Scripts/Interaction/ArrestVerdictFeedback.cs)이 `App.UI.Current.TryGetPanel<VerdictBanner>()`로 찾고, `PanelBase.Awake()`가 씬에 배치된 인스턴스를 자동 등록한다. 색·아이콘·표시시간 값은 프리팹 하나에만 있으면 세 씬에 그대로 반영된다.

## 6. 딸려 나온 것 — ArrestNoticeBroadcaster 색 동기화

[`ArrestNoticeBroadcaster.cs:33`](../../../Assets/Scripts/Interaction/ArrestNoticeBroadcaster.cs)의 `m_noticeTone`이 "판정 배너의 진범 검거 초록과 맞춘 값"이라는 주석과 함께 기존 초록(`0.20, 0.70, 0.35`)을 별도로 하드코딩해 두고 있다. 이번에 `VerdictBanner`의 초록을 `#4ADE80`로 바꾸면서 이 필드 값도 같은 색으로 맞춘다 — 코드 구조는 건드리지 않고 값만 맞춘다.

## 7. 범위 밖

- 공용 색 팔레트 ScriptableObject 도입 — 프로젝트 전반이 색을 스크립트별로 하드코딩하는 관례라 이번 건 하나로 바꾸지 않는다.
- 경범죄·생포조건불충족 판정의 게임플레이 로직 변경 — 이번은 표시 계층만.

## 8. 검증

테스트 프레임워크 코드가 없는 프로젝트라 수동이다 (CLAUDE.md).

**핵심 확인 사항**

1. 4종 판정(진범 검거/오검거/경범죄/생포조건불충족)을 각각 유도해 테두리·채움 색·애니메이션이 맞는 조합으로 뜨는지
2. 세 씬(Map_Apocalypse / Map_Cyberpunk / Tutorial) 모두에서 배너가 정상 등록·표시되는지 — 프리팹 교체 후 등록 누락이 없는지

**회귀 지점**

- 연속 검거 시 배너가 겹치거나 애니메이션이 끊기지 않는지 (기존 취소 패턴이 애니메이션까지 커버하는지)
- 팀 전체 알림 토스트(`ArrestNoticeBroadcaster`)의 초록과 개인 판정 배너의 초록이 같은 색으로 보이는지
