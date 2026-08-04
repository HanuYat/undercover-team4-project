# 로컬라이제이션 (#497 · 상위 #374)

> 설계 정본. 게임 안 **모든 표시 문자열**을 Unity Localization으로 옮기는 규칙과 단계를 정의한다.
> 코드 구조 규칙은 [architecture.md](../architecture.md), 게임 규칙은 [GDD.md](../GDD.md)가 정본 — 이 문서는 문자열 처리만 다룬다.
> 관련: [settings-ui.md](settings-ui.md) (언어 선택 UI가 여기 들어간다), [appearance-montage.md](../appearance-montage.md) (몽타주 텍스트), #251·#493 (이미 적용된 선례).

> **#497 본문 정정 (2026-08-04).** 이슈의 조사 결과 1번 — *"프리팹·씬에 박힌 한글이 하나도 없다 … TMP 텍스트에 저장된 한글이 0건"* — 은 **사실과 다르다.**
> 실제로는 씬 4개와 프리팹에 **약 45개의 정적 한글 라벨**이 있다 (Title 16 · Lobby 2 · Shop 4 · Main 6 · 프리팹 약 23).
> 코드가 매 프레임 덮어쓰는 디자인타임 자리표시자(`"라운드 성공!"`, `"HP: 100/100"`, `"0 / 0원"` 등)와 섞여 있어 0건으로 읽힌 것으로 보인다.
> 자리표시자는 번역 대상이 아니지만, `익명 로그인` · `마스터 볼륨` · `일시정지` · `세션 나가기` 같은 버튼·제목 라벨은 **코드에 대입부가 없는 진짜 정적 텍스트**다.
> 따라서 "코드만 고치면 된다"는 전제도 성립하지 않으며, 씬·프리팹 작업(§4 Phase 1)이 별도로 필요하다.

## 1. 목표 & 범위

플레이어에게 **보이는** 문자열을 전부 테이블로 옮기고, 게임 안에서 언어를 바꾸면 즉시 반영되게 한다. 지원 로케일은 **한국어(`ko-KR`) · 영어(`en`)** 2개.

### 범위 안
- 씬·프리팹의 정적 라벨 (버튼·제목·안내문)
- 코드가 조립하는 UI 문자열 (정산·판정·인증·HUD 포맷)
- 서버가 클라이언트로 보내는 **플레이어용** 알림 (토스트·상점 응답)
- 표시용 데이터 에셋 (몽타주 축·옵션 이름, 시민 타입·세력 표기)
- 설정 창의 언어 선택 UI

### 범위 밖 (번역하지 않는다)
| 대상 | 이유 |
|------|------|
| `Debug.Log*` 전부 (약 500건) | 개발자용. 콘솔은 항상 한국어로 둔다 |
| `[Tooltip]` · `[Header]` · `Assets/Scripts/Editor/*` | 에디터 전용 — 팀 내부 문자열 |
| `NotifyOwner(string)` 콘솔 로그 | 위와 같음. §4 Phase 3에서 토스트 경로와 **분리**한 뒤 그대로 둔다 |
| 플레이어 닉네임 | 사용자 입력 |
| 시민 이름 풀 (`Kai Vex` 등, [CitizenProfileFactory](../../Assets/Scripts/NPC/CitizenProfileFactory.cs)) | 고유명사. **ko 화면에서도 영문 그대로** 둔다 (팀 결정) |

## 2. 확정된 설계 결정

| 항목 | 결정 | 근거 |
|------|------|------|
| (a) 키 규칙 | **`<Domain>.<Group>.<Name>` · PascalCase.** 도메인이 맨 앞 | 기존 `ItemTable`의 `Item.Name.Scanner`가 이미 이 형태다. `UITable`의 `signal.received`(소문자 스네이크)가 예외였고, 그쪽을 맞춘다 |
| (b) 테이블 분리 | **도메인/화면 단위로 11개** (§3) | 테이블은 Localization의 **로드 단위**다. 하나에 몰면 타이틀 화면이 인게임 문자열까지 들고 있게 된다. 도메인 접두가 곧 테이블이라 키만 봐도 어느 테이블인지 안다 |
| (c) 언어 선택 위치 | **설정 창**([SettingsPanel](../../Assets/Scripts/UI/Panels/SettingsPanel.cs))에 드롭다운 1개. `GameSettings`에 편입 | 로컬 전용 값이라는 점이 감도·음량과 완전히 같다 — [settings-ui.md](settings-ui.md) (f)의 static 저장소 선례를 그대로 쓴다. 기존 F10 토글(`LocaleSwitchTester`)은 이때 제거 |
| (d) 언어별 텍스트 반영 | **`LocalizedString.StringChanged` 구독** — 떠 있는 중에 언어를 바꿔도 갱신된다 | [LocalizedMessageView](../../Assets/Scripts/UI/Hud/LocalizedMessageView.cs)가 #251에서 확립한 관례. 정적 라벨은 `LocalizeStringEvent` 컴포넌트가 같은 일을 한다 |
| (e) 인자가 있는 문구 | **Smart String `{0}` + `LocalizedString.Arguments`**. 인자를 먼저 넣고 구독한다 | [SignalDecoder.ShowLocal](../../Assets/Scripts/Item/SignalDecoder.cs)의 선례. 순서를 어기면 구독 시점의 첫 발화가 인자 없는 문장으로 나간다 |
| (f) `string m_format` 필드 | **`LocalizedString`으로 타입 교체.** `string.Format` 호출을 Smart String으로 대체 | 인스펙터에 한국어 포맷이 박혀 있으면 그 필드는 영원히 번역되지 않는다. 해당 필드 8개는 §4 Phase 2 참고 |
| (g) 네트워크로 보내는 알림 | **완성된 문장이 아니라 `enum` + 숫자 인자를 보낸다.** 수신 클라가 자기 로케일로 조회 | 서버가 자기 언어로 문장을 만들어 보내면 클라 언어와 무관하게 그 언어가 뜬다. enum은 4바이트고, 문자열보다 RPC 크기도 작다 |
| (h) enum → 키 매핑 | **규약 기반** — `Item.Feedback.` + enum 이름. 매핑 SO를 만들지 않는다 | 매핑 에셋은 enum이 늘 때마다 같이 고쳐야 하는 두 번째 진실이 된다. 규약이면 enum 값 추가 = 테이블 키 추가로 끝 |
| (i) 몽타주 번역 | **번역한다.** 다만 전송 구조는 그대로 두고 `AppearanceDatabase`만 번역 (§5) | 국적이 다른 사람끼리 한 판을 하는 상황을 상정하지 않는다는 팀 결정. 이 전제에서는 전원이 같은 로케일이므로 서버가 만든 문구가 각자 언어와 일치한다 |
| (j) 시민 이름 | **번역하지 않는다** — ko 화면에서도 영문 | 고유명사. 무전으로 이름을 부르는 것이 대조의 핵심이라 표기가 흔들리면 안 된다 |

## 3. 테이블 구성

테이블 = 로드 단위이자 도메인 접두다. **키의 첫 마디를 보면 테이블을 알 수 있다.**

| 테이블 | 접두 | 범위 |
|--------|------|------|
| `CommonTable` | `Common.` | 예/아니오·확인/취소·닫기·적용, 단위(원), 로딩 화면, 확인창 3종(Quit·Leave·AccountConfirm) |
| `TitleTable` | `Title.` | 타이틀 씬, `AuthPanel`, `AccountCredentials` 검증 메시지, `NicknameRules`, 세션 코드 패널 |
| `LobbyTable` | `Lobby.` | 로비 씬, 로스터 행, 음성 상태 라벨 |
| `ShopTable` | `Shop.` | 상점 씬, 진열대 카드·가격표, 구매 응답 |
| `SettingsTable` | `Settings.` | 설정 패널, 일시정지 패널 (전 씬 공용) |
| `HudTable` | `Hud.` | 라운드 타이머·팀 자금·남은 범인·대기 안내·마이크 상태·토스트·검거 판정 배너·구조 프롬프트·페널티 경고 |
| `ItemTable` | `Item.` | 아이템 이름/설명 **+ 사용 피드백**(Phase 3에서 `Item.Feedback.*` 추가) — 기존 테이블 유지 |
| `WorldTable` | `World.` | 월드 설치물 라벨 (신호 해석기·부활 장치·스캐너 충전기·폭탄 매뉴얼·CCTV 장소명) |
| `HqTable` | `Hq.` | 인명부·수배 리스트·세력 문양 보드·CCTV 채널 라벨·라운드 종료 버튼 |
| `SettlementTable` | `Settlement.` | 정산 화면 (결과·종료 사유·수익 내역·복귀 카운트다운) |
| `NpcTable` | `Npc.` | 외형 축·옵션 이름(몽타주 원본), 시민 타입·세력 표기 |
| `EventTable` | `Event.` | 돌발 이벤트 (폭탄 해체, 탈옥, 거리 난동자) |

`UITable`은 아래 이관 후 **삭제한다.**

### 기존 키 이관표 (`UITable` → 신규) — **완료 (Phase 0)**

`UITable` 엔트리 **9개**를 아래대로 옮기고 `UITable`은 삭제했다.

| 기존 키 | 신규 키 | 테이블 | 참조하는 프리팹 |
|---------|---------|--------|-----------------|
| `signal.received` | `World.SignalDecoder.Received` | `WorldTable` | `Items/SignalDecoder.prefab` (`SignalDecoder`) |
| `signal.input_title` | `World.SignalDecoder.InputTitle` | `WorldTable` | `UI/HUD.prefab` (`SignalInputPanel`) |
| `signal.input_hint` | `World.SignalDecoder.InputHint` | `WorldTable` | `UI/HUD.prefab` (`SignalInputPanel`) |
| `revive.hint` | `Hud.Revive.Hint` | `HudTable` | `Player.prefab` (`PlayerReviveHud`) |
| `revive.downed` | `Hud.Revive.Downed` | `HudTable` | `Player.prefab` (`PlayerReviveHud`) |
| `revive.carrying` | `Hud.Revive.Carrying` | `HudTable` | `Player.prefab` (`PlayerReviveHud`) |
| `revive.dead_target` | `Hud.Revive.DeadTarget` | `HudTable` | `Player.prefab` (`PlayerReviveHud`) |
| `revive.self_dead` | `Hud.Revive.SelfDead` | `HudTable` | `Player.prefab` (`PlayerReviveHud`) |
| `penalty.chase_warning` | `Hud.Penalty.ChaseWarning` | `HudTable` | `Player.prefab` (`PlayerPenaltyView`) |

> **키 이름을 바꾸면 참조가 끊긴다 — 테이블을 옮기지 않아도 마찬가지다.**
> 이 프로젝트의 `LocalizedString`은 **테이블 이름 + 키 문자열**로 직렬화되어 있다
> (`m_TableReference.m_TableCollectionName: UITable` + `m_TableEntryReference.m_Key: revive.hint`, `m_KeyId: 0`).
> 엔트리 ID(`m_KeyId`)를 쓰는 형태였다면 같은 테이블 안에서의 개명은 안전했겠지만, 여기서는 **키 문자열이 곧 참조**라
> 이름만 바꿔도 끊어진다. 위 9개는 프리팹 3개에 걸쳐 총 9곳이 걸려 있었고 전부 다시 연결했다.
>
> 앞으로 키를 개명할 때도 같은 절차가 필요하다 — 테이블에서 바꾸고 끝내지 말고,
> `rg "m_Key: <옛 키>" Assets/Prefabs Assets/Scenes`로 참조를 찾아 함께 고칠 것.

## 4. 단계

각 Phase = PR 1개. Phase 3은 리스크가 커서 반드시 단독으로 간다.

### Phase 0 — 기반 (문구 변화 없음) — **완료**
- 테이블 11개 생성 (`ko-KR` / `en` 양쪽)
- `UITable` 키 9개를 §3 이관표대로 옮기고(값·Smart 플래그 포함), 참조 9곳 재연결 후 `UITable` 삭제
- `GameSettings.Locale` 추가 — 백킹 필드를 두지 않고 `LocalizationSettings.SelectedLocale`을 그대로 읽으며,
  영속화는 `PlayerPrefLocaleSelector`(`selected-locale`)에 맡겨 저장 키를 한 곳에 유지했다
- `SettingsPanel`에 언어 드롭다운 추가(항목은 런타임 생성, 표시는 각 언어의 NativeName), `LocaleSwitchTester`(F10)와 그 전용 씬 오브젝트 제거
- 폰트 확인 결과 **문제 없음**: 기본 폰트 `NotoSansKR-VF SDF`(Dynamic)는 한글·라틴 모두 보유.
  일부 UI가 쓰는 Roboto 계열(Static, 한글 없음)은 TMP **전역 fallback**이 `NotoSansKR-VF SDF`라 한글도 정상 표시된다
  (다만 그 라벨들은 ko에서 서체가 바뀌어 보인다 — 미관 이슈이며 차단 요소는 아니다)

### Phase 1 — 씬·프리팹 정적 라벨 (약 45개)
각 `TMP_Text`에 `LocalizeStringEvent`를 붙이고 키를 연결한다.

**씬 파일은 동시 편집 시 머지 충돌이 크다. 씬 하나 = 브랜치 하나 = PR 하나**로 끊어 진행한다.

| 대상 | 개수 | 상태 |
|------|------|------|
| Title Scene | **17** | ✅ 완료 (`TitleTable`, 브랜치 `feature/374-localization-title`) |
| Lobby / Shop | 6 | |
| Main Scene | 6 | 인명부 정렬·페이지 버튼 등 |
| 프리팹 | 약 23 | SettingsCanvas 7, PauseCanvas 1, 확인창 3종 7, Directory·FactionSymbolBoard·RoundEndButton·LoadingScreen, 월드 라벨 4 |

> **자리표시자와 구분할 것.** 씬·프리팹의 TMP 텍스트 중 상당수는 코드가 런타임에 덮어쓰는 디자인타임 값이다
> (`"라운드 성공!"`, `"김시민 — 보상 10,000원"`, `"HP: 100/100"`, `"0 / 0원"`, `"0원"`, `"현상수배범 검거!"` 등).
> 이들은 Phase 2에서 코드 쪽이 처리하므로 **여기서 건드리지 않는다** — `LocalizeStringEvent`를 붙이면 코드 대입과 서로 덮어쓴다.
> 판별법: 해당 문구나 그 필드에 대한 `.text =` 대입이 코드에 있는가.
> Title 씬에서 이 기준으로 제외한 것: `AccountStatusText` · `NicknameStatusText` · `PlayerIdText`
> (전부 [AuthPanel](../../Assets/Scripts/UI/Panels/AuthPanel.cs)이 대입한다).

> **입력창은 Placeholder에 붙인다.** `TMP_InputField`의 본문 텍스트가 아니라 `.../Text Area/Placeholder` 쪽이 대상이다 —
> 본문은 사용자가 친 글이라 번역 대상이 아니고, 코드가 대입하기도 한다.

#### Title 씬에서 문구가 바뀐 것 (번역하며 정정)
| 키 | 이전 | 이후 |
|----|------|------|
| `Title.Button.Quit` | `Quit` (한국어 UI인데 영문) | ko `종료` / en `Quit` |
| `Title.Auth.NicknamePlaceholder` | `Enter text...` (TMP 기본 더미) | ko `닉네임` / en `Nickname` |

### Phase 2 — 코드 조립 문자열 (약 60개)
`LocalizedString` SerializeField + Smart String으로 교체. 관례는 [SignalDecoder](../../Assets/Scripts/Item/SignalDecoder.cs)와 같다.

- `SettlementPanel` (12) · `AuthPanel`+`AccountCredentials`+`NicknameRules` (21) · `ScanInfoView` · `ScanResultPresenter` · `ShopStandView` · `CCTVChannelLabelView` · `HqRevivalDevice` · `LobbyRosterRowView`/`LobbyRosterPanel` · `SessionCodePanel`/`SessionPanel`/`LeaveConfirmPanel` · `BombTimerView`
- **`string m_format` 필드 8개** → `LocalizedString`: `RoundFundHud` · `ReadyWaitHud` · `RemainingCriminalsHud` · `WantedEntryView` · `BombSerialView` · `MicStatusHud` · `HqRevivalDevice` · `CCTVNode`
- **곁다리 정리:** 검거 판정 문구가 [ArrestJudge](../../Assets/Scripts/Interaction/ArrestJudge.cs) · [VerdictBanner](../../Assets/Scripts/UI/Hud/VerdictBanner.cs) · [ArrestVerdictFeedback](../../Assets/Scripts/Interaction/ArrestVerdictFeedback.cs) **3곳에 중복 정의**돼 있다. 3벌을 번역하지 말고 한 곳으로 합친 뒤 번역한다

### Phase 3 — 네트워크 문자열 제거 (토스트만, 단독 PR)
서버가 완성된 한국어를 RPC로 실어 보내는 경로를 enum 전송으로 바꾼다.

[ChanneledInteractionBehaviour.NotifyOwner](../../Assets/Scripts/Core/ChanneledInteractionBehaviour.cs)는 지금 **개발자 콘솔 로그와 플레이어 토스트를 한 메서드가 겸하고 있다.** 이걸 쪼개는 것이 이 Phase의 핵심이다:

```
NotifyOwner(string)                  ← Debug.Log 전용. 기존 toast:false 호출 전부 그대로, 번역 대상 아님
ToastOwner(EItemFeedback, args…)     ← 신설. RPC는 enum + 숫자 인자만 싣고, 수신 클라가 자기 로케일로 조회
```

- `toast:` bool 매개변수는 제거한다 — 두 책임이 갈렸으므로 분기가 필요 없다
- `Core/Enums.cs`에 `EItemFeedback` · `EShopReply` 추가. 키는 규약대로 `Item.Feedback.<enum 이름>` / `Shop.Reply.<enum 이름>`
- 적용 대상: `Scanner` 토스트 4건, [ShopStand.ReplyRpc](../../Assets/Scripts/Economy/ShopStand.cs) 3건, [ItemBattery](../../Assets/Scripts/Item/ItemBattery.cs)의 `ChargeBlockedReason`/`FullyChargedMessage`
- **몽타주는 이 PR에서 제외** — §5 참고

### Phase 4 — 데이터 에셋
- [AppearanceDatabase](../../Assets/Scripts/Data/AppearanceDatabase.cs)의 `AxisDefinition.AxisName` · `AppearanceOption.DisplayName` → `LocalizedString` (몽타주 번역)
- [OfficialRecords](../../Assets/Scripts/Data/OfficialRecords.cs)의 `CitizenTypeNames` · `FactionNames` Dictionary → `NpcTable` 조회.
  [DirectoryEntry](../../Assets/Scripts/HQ/Directory/DirectoryEntry.cs)가 이미 **enum을 동기화**하고 클라가 로컬에서 이름으로 바꾸므로 네트워크 변경이 필요 없다
- **곁다리 정리:** [CitizenProfile](../../Assets/Scripts/Data/CitizenProfile.cs)의 `m_typeView`/`m_factionView`가 `string`인데 실제로는 enum에서 파생된 값이다. **enum 타입으로 바꾸고 표시 시점에 번역**한다 — 위조(#223)는 이름·문양만 오염시키므로 안전하다
- `SpawnedNpcEvent.m_displayName` · `JailbreakEvent.DisplayName` · `CCTVNode.m_locationLabel`

### Phase 5 — 검증
- 설정 창에서 ko↔en 전환하며 전 화면 순회. **영문이 길어 생기는 버튼·라벨 잘림/오버플로**가 주 확인 대상
- 멀티 접속 1회(호스트/클라 같은 로케일)로 Phase 3 enum 경로가 양쪽에 뜨는지 확인

## 5. 남는 부채 — 몽타주 전송 구조 (별도 이슈)

Phase 4에서 `AppearanceDatabase`를 번역해도, 서버가 `BuildMontageText`로 만든 **완성 문자열**을 `FixedString128Bytes`로 실어 보내는 구조는 그대로다 ([WantedEntry](../../Assets/Scripts/Data/WantedEntry.cs)). 즉 모든 클라이언트가 **서버 로케일의 문구**를 본다.

결정 (i)의 전제(전원 같은 언어) 아래서는 정상 동작하므로 이번 작업에 넣지 않는다. 근본 해결은 `WantedEntry`가 문자열 대신 `AppearanceProfile` 인덱스 + `RevealedAxes`를 동기화하고 **클라가 직접 조립**하는 것이며, 이는 `WantedEntry`의 코드 주석이 이미 예고해 둔 방향이다.

## 6. 새 문자열을 추가할 때

1. 어느 도메인인가 → §3에서 테이블을 고른다
2. `<Domain>.<Group>.<Name>` (PascalCase)로 키를 만든다
3. `ko-KR` · `en` **양쪽을 채운다** — 한쪽만 채우면 폴백으로 반대 언어가 그대로 노출된다
4. 코드에서 쓰면 `LocalizedString` SerializeField, 정적 라벨이면 `LocalizeStringEvent`
5. **서버가 클라에 보내는 문구라면 문자열을 보내지 말 것** — enum + 인자로 보내고 수신 측에서 조회한다 (결정 (g))
