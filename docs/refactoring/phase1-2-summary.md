# 아키텍처 리팩토링 1~2단계 정리 (PR #245)

> 작성: 2026-07-19 · 브랜치 `refactoring/architecture` (커밋 3039cd2…63d93ae)
> 이 문서는 PR 리뷰 대응과 인수인계용이다. 규칙의 정본은 [architecture.md](../architecture.md).

## 1. 한 문장 요약

기존에는 매니저가 필요한 쪽이 씬을 뒤져서(`FindFirstObjectByType`) 찾아 썼는데, 이제는 **매니저가 게임 시작 때 App(전화번호부)에 자기를 등록하고, 필요한 쪽은 `App.그룹.매니저`로 찾아 쓴다.** 게임 동작 변화는 없다.

## 2. 무엇이 바뀌었나

| 지표 | 이전 | 이후 |
|---|---|---|
| 매니저 씬 검색 | 20곳+ | 0곳 (의도적 예외 3곳은 매니저 아님 — architecture.md §4) |
| 자체 `.Instance` 싱글톤 | 2개 | 0개 |
| 전역 접근 경로 | 제각각 | `App.Net`(3) · `App.Game`(7) · `App.UI`(2+뼈대) |
| 실수 방어 | 없음 | 중복 배치 에러 로그 / 씬 언로드 시 자동 해제 / 멤버 숨김 = 컴파일 에러(csc.rsp) |
| 코드량 | — | 약 90줄 순감소 |

## 3. 커밋별 흐름 (커밋 순서 = 작업 순서)

1. **3039cd2 — Core 프레임워크 (1단계)**: `Assets/Scripts/Core/` 8개 파일 신규. App(파사드)·ManagerHandler(리플렉션 자동 등록)·CommonManagerBase/NetworkedManagerBase·AppHelper(씬 로드, NGO 분기)·Enums(EScene, EExecutionOrder)·UIManagerBase/PanelBase(4단계용 뼈대)·Singleton. **기존 코드 무변경.**
2. **180670d — Round 도메인**: RoundManager 편입 + 소비자 6곳을 `private RoundManager Round => App.Game.Round;` 프로퍼티 패턴으로 교체.
3. **3ec317d — Events·HQ·NPC 도메인**: 매니저 6종 편입. 매니저 간 이벤트 구독을 Awake→Start로 이동(동순위 Awake 순서 비보장 때문). **NpcSpawner의 자체 Awake가 베이스 등록을 가리는 버그**를 겪고 수정 — 재발 방지로 `Assets/csc.rsp` 추가(CS0108/CS0114를 에러로 승격).
4. **449df48 — Net 도메인**: SessionManager·AuthBootstrap·VivoxManager 편입.
5. **0b7aaab — UI 싱글톤 흡수**: CrosshairUI·ChannelingGaugeUI의 static Instance 제거 → App.UI로.
6. **46e5310 — 규칙 문서화**: `docs/architecture.md`(R1~R8) 신설, CLAUDE.md·pr-review 스킬 연결.
7. **63d93ae — PR 템플릿** + 리뷰 스킬에 템플릿↔diff 교차 검증 추가.

## 4. 검증 기록

- 각 단계마다: dotnet 스크래치 빌드(전체 스크립트 + Library/ScriptAssemblies 참조)로 컴파일 0에러 확인 → 에디터 플레이 검증(NPC 스폰·범인 라벨·타이머·검거·종료 피드백) → 커밋.
- 수배 리스트는 리팩토링 전부터 호스트 세션에서만 채워지는 구조(WantedListManager.OnNetworkSpawn) — 오프라인에서 안 보이는 건 정상.

## 5. 리뷰어 예상 질문 Q&A

**Q1. 리플렉션 성능 괜찮나?**
등록 시점(씬 로드당 ~10회)에만 리플렉션. FieldInfo는 최초 1회 스캔 후 캐싱. `App.Game.Round` 같은 읽기 경로는 static 프로퍼티→필드라 리플렉션 0회, 기존 캐시 필드와 동일 성능.

**Q2. 왜 수동 등록이 아니라 리플렉션 자동 등록?**
수동 등록은 매니저마다 보일러플레이트 + 등록 누락 실수 여지. 자동 등록은 "베이스 상속만 하면 끝". 회사 검증 템플릿의 핵심 메커니즘이기도 함.

**Q3. App이 God Object 되지 않나?**
등록 기준 R3(씬에 하나뿐 + 두 도메인 이상이 참조)와 승격 절차로 통제. HqDropoffZone·RoundTimerSync는 자격 미달로 의도적 제외(§4 예외 표). App 자체는 로직 0줄인 참조 허브.

**Q4. SerializeField 연결이 사라졌는데 씬 안 고쳐도 되나?**
Unity는 코드에 없는 직렬화 필드를 무시함 → 씬 수정 불필요. 이 PR에 씬 파일 변경이 0개인 이유. 잔여 데이터는 다음 씬 저장 시 자연 정리.

**Q5. 구독을 Awake→Start로 옮겨서 이벤트 놓치지 않나?**
옮긴 대상(OnCriminalAssigned 등)은 전부 NPC 스폰 완료 후(수 프레임 뒤) 발화 → Start 구독으로 놓칠 수 없음. AppearanceAssigner의 기존 "놓친 경우 보정" 로직도 유지됨.

**Q6. Start 구독 / OnDisable 해제 비대칭은?**
인지한 트레이드오프. 매니저는 씬 수명 서비스라 mid-scene 비활성화 금지를 R6에 명문화.

**Q7. csc.rsp는 왜?**
NpcSpawner 실사례: 자체 Awake가 베이스 등록 Awake를 조용히 가려 게임이 반쯤 죽는 버그. 컴파일러 경고(CS0114)가 있었지만 경고라 묻힘 → 그 2개(CS0108/CS0114)만 에러로 승격해 재발을 컴파일 단계에서 차단. 기존 코드에 해당 경고 0건이라 부작용 없음.

**Q8. 매번 App 조회하는 프로퍼티보다 캐싱이 낫지 않나?**
비용은 static 필드 읽기 수준. 캐싱은 씬 전환 시 파괴된 매니저를 쥐는 버그의 원인 → R8이 프로퍼티 패턴을 규칙으로 정한 이유.

**Q9. 단독 테스트 씬 워크플로는?**
안 깨짐. 각 테스트 씬에 배치된 매니저가 그대로 자동 등록되고, 매니저 없는 씬은 App 프로퍼티가 null → 기존 Find 실패 시 null 가드 동작과 동일.

**Q10. 머지되면 나는 뭘 다르게 해야 하나?**
① 매니저 접근은 `App.그룹.X` (Find 금지) ② 전역 필요 시 Instance 만들지 말고 베이스 상속+App 필드 ③ 씬 전환은 `App.LoadScene`. 상세는 architecture.md — 초안 상태라 이 PR 리뷰가 규칙 검토를 겸함.

## 6. 머지 전략 (팀 공지사항)

이 PR은 매니저 파일 전반을 수정하므로 **열려 있는 다른 PR들을 먼저 머지하고 이 PR을 마지막에 머지**한다. 충돌 해소는 이 PR 쪽에서 수행(대부분 "Find 삭제 vs 그대로" 형태라 이 PR 쪽 채택이 정답). 머지 후 팀원들은 main을 리베이스/머지하고 architecture.md 규칙에 따라 작업.

## 7. 미해결 항목

- 3단계(부트스트랩+Title 씬): [phase3-plan.md](phase3-plan.md) 참고 — 다음 작업
- 4단계(UI 패널 실사용 + SoundManager)
- RoundEndResetter UniTask 취소 토큰 버그(플레이 종료 시 에디터 예외 — 기존 버그, 별도 수정)
