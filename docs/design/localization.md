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
| (b) 테이블 분리 | **도메인/화면 단위로 12개** (§3) | 테이블은 Localization의 **로드 단위**다. 하나에 몰면 타이틀 화면이 인게임 문자열까지 들고 있게 된다. 도메인 접두가 곧 테이블이라 키만 봐도 어느 테이블인지 안다 |
| (c) 언어 선택 위치 | **설정 창**([SettingsPanel](../../Assets/Scripts/UI/Panels/SettingsPanel.cs))에 드롭다운 1개. `GameSettings`에 편입 | 로컬 전용 값이라는 점이 감도·음량과 완전히 같다 — [settings-ui.md](settings-ui.md) (f)의 static 저장소 선례를 그대로 쓴다. 기존 F10 토글(`LocaleSwitchTester`)은 이때 제거 |
| (d) 언어별 텍스트 반영 | **`LocalizedString.StringChanged` 구독** — 떠 있는 중에 언어를 바꿔도 갱신된다 | [LocalizedMessageView](../../Assets/Scripts/UI/Hud/LocalizedMessageView.cs)가 #251에서 확립한 관례. 정적 라벨은 `LocalizeStringEvent` 컴포넌트가 같은 일을 한다 |
| (e) 인자가 있는 문구 | **Smart String `{0}` + `LocalizedString.Arguments`**. 인자를 먼저 넣고 구독한다 | [SignalDecoder.ShowLocal](../../Assets/Scripts/Item/SignalDecoder.cs)의 선례. 순서를 어기면 구독 시점의 첫 발화가 인자 없는 문장으로 나간다 |
| (f) `string m_format` 필드 | **`LocalizedString`으로 타입 교체.** `string.Format` 호출을 Smart String으로 대체 | 인스펙터에 한국어 포맷이 박혀 있으면 그 필드는 영원히 번역되지 않는다. 해당 필드 8개는 §4 Phase 2 참고 |
| (g) 네트워크로 보내는 알림 | **완성된 문장이 아니라 `enum` + 숫자 인자를 보낸다.** 수신 클라가 자기 로케일로 조회 | 서버가 자기 언어로 문장을 만들어 보내면 클라 언어와 무관하게 그 언어가 뜬다. enum은 4바이트고, 문자열보다 RPC 크기도 작다 |
| (h) enum → 키 매핑 | **규약 기반** — `Item.Feedback.` + enum 이름. 매핑 SO를 만들지 않는다. 규약은 **enum 선언부에 `[LocalizedEnum]`으로 선언**하고 에디터 검증으로 받친다 | 매핑 에셋은 enum이 늘 때마다 같이 고쳐야 하는 두 번째 진실이 된다. 규약이면 enum 값 추가 = 테이블 키 추가로 끝. 대신 컴파일러가 막아 주지 못하므로 그 구멍은 검증으로 메운다 (§7) |
| (i) 몽타주 번역 | **번역한다.** 다만 전송 구조는 그대로 두고 `AppearanceDatabase`만 번역 (§5) | 국적이 다른 사람끼리 한 판을 하는 상황을 상정하지 않는다는 팀 결정. 이 전제에서는 전원이 같은 로케일이므로 서버가 만든 문구가 각자 언어와 일치한다 |
| (j) 시민 이름 | **번역하지 않는다** — ko 화면에서도 영문 | 고유명사. 무전으로 이름을 부르는 것이 대조의 핵심이라 표기가 흔들리면 안 된다 |

## 3. 테이블 구성

테이블 = 로드 단위이자 도메인 접두다. **키의 첫 마디를 보면 테이블을 알 수 있다.**

| 테이블 | 접두 | 범위 |
|--------|------|------|
| `CommonTable` | `Common.` | 예/아니오·확인/취소·닫기·적용, 단위(원), 로딩 화면, 확인창 3종(Quit·Leave·AccountConfirm), **세션 코드 HUD** |
| `TitleTable` | `Title.` | 타이틀 씬, `AuthPanel`, `AccountCredentials` 검증 메시지, `NicknameRules` |
| `LobbyTable` | `Lobby.` | 로비 씬, 로스터 행, 음성 상태 라벨 |
| `ShopTable` | `Shop.` | 상점 씬, 진열대 카드·가격표, 구매 응답 |
| `SettingsTable` | `Settings.` | 설정 패널 (전 씬 공용 — Title 포함 4개 씬) |
| `PauseTable` | `Pause.` | 일시정지 패널 (인게임 전용 — Lobby·Main·Shop) |
| `HudTable` | `Hud.` | 라운드 타이머·팀 자금·남은 범인·대기 안내·마이크 상태·토스트·검거 판정 배너·구조 프롬프트·페널티 경고 |
| `ItemTable` | `Item.` | 아이템 이름/설명 — **소지형(인스펙터에서 고름) + 설치형(`Item.Name.<EInstallable>` 규약)**. Phase 3에서 `Item.Feedback.*` 추가 |
| `WorldTable` | `World.` | 월드 설치물 라벨 (신호 해석기·부활 장치·스캐너 충전기·폭탄 매뉴얼·CCTV 장소명) |
| `HqTable` | `Hq.` | 인명부·수배 리스트·세력 문양 보드·CCTV 채널 라벨·라운드 종료 버튼 |
| `SettlementTable` | `Settlement.` | 정산 화면 (결과·종료 사유·수익 내역·복귀 카운트다운) |
| `NpcTable` | `Npc.` | 외형 축·옵션 이름(몽타주 원본), 시민 타입·세력 표기 |
| `EventTable` | `Event.` | 돌발 이벤트 (폭탄 해체, 탈옥, 거리 난동자) |

`UITable`은 아래 이관 후 **삭제한다.**

> **`PauseTable`은 Phase 1에서 갈라냈다.** 처음에는 일시정지 문구를 `SettingsTable`에 `Settings.Pause.*`로 두었는데 두 가지가 어긋났다.
> ① `SettingsCanvas`는 Title 포함 4개 씬에 있고 `PauseCanvas`는 인게임 3개 씬에만 있어서, 합쳐 두면 **Title 씬이 절대 표시할 수 없는 문구를 로드**한다 — 결정 (b)가 테이블을 쪼갠 바로 그 이유다.
> ② 결정 (a)는 "키 첫 마디를 보면 테이블을 안다"인데 `Settings.Pause.Title`은 그 규칙을 스스로 깬다.
> 테이블이 작아지는 것(4엔트리)은 감수한다 — `WorldTable` 3개, `HudTable` 6개도 같은 규모다.

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

> **정정 (Phase 1) — 참조 형태가 두 가지 섞여 있다.** 위 설명은 `UITable` 시절 참조에만 맞다.
>
> | 형태 | 직렬화 | 개명 | 엔트리 삭제·재생성 |
> |------|--------|------|--------------------|
> | **이름 참조** | `m_KeyId: 0` + `m_Key: <키>` | **끊어진다** | 이름이 같으면 살아남는다 |
> | **ID 참조** | `m_KeyId: <숫자>`, `m_Key` 빈칸 | 안전하다 | **끊어진다** |
>
> Editor의 `Localize` UI로 키를 고르면 **ID 참조**가 된다 — 그게 기본이다. 이름 참조는 손으로 써넣은 옛 항목들이다.
> 그래서 개명 위험(이름 참조)과 삭제 위험(ID 참조)을 **양쪽 다** 봐야 한다.
> Phase 1에서 `Settings.Pause.Title`을 `PauseTable`로 옮기며 엔트리를 삭제했을 때 `PauseCanvas/Window/Title`의 ID 참조가
> 실제로 끊어졌다(`id:...8 -> 테이블에 없는 ID`). 인스펙터에는 빈칸으로만 보이고 콘솔 에러도 나지 않으므로,
> **엔트리를 옮기거나 지울 때는 그 키를 쓰는 프리팹·씬을 반드시 다시 연결할 것.**

## 4. 단계

각 Phase = PR 1개. Phase 3은 리스크가 커서 반드시 단독으로 간다.

### Phase 0 — 기반 (문구 변화 없음) — **완료**
- 테이블 11개 생성 (`ko-KR` / `en` 양쪽) — `PauseTable`은 Phase 1에서 갈라내 12개가 됐다 (§3 주석)
- `UITable` 키 9개를 §3 이관표대로 옮기고(값·Smart 플래그 포함), 참조 9곳 재연결 후 `UITable` 삭제
- `GameSettings.Locale` 추가 — 백킹 필드를 두지 않고 `LocalizationSettings.SelectedLocale`을 그대로 읽으며,
  영속화는 `PlayerPrefLocaleSelector`(`selected-locale`)에 맡겨 저장 키를 한 곳에 유지했다
- `SettingsPanel`에 언어 드롭다운 추가(항목은 런타임 생성, 표시는 각 언어의 NativeName), `LocaleSwitchTester`(F10)와 그 전용 씬 오브젝트 제거.
  드롭다운은 `SettingsCanvas`의 **MicMute 행 우측**에 얹었다 — `Spacer`(flexible 1)가 좌측(마이크)과 우측(언어) 그룹을 가르고,
  그 행의 `HorizontalLayoutGroup`은 `Child Force Expand / Width` **OFF** · `Control Child Size / Width` **ON**이어야 한다
  (전자가 켜져 있으면 모든 자식에 flexible 1이 강제돼 지정한 폭이 무시되고, 후자가 꺼져 있으면 `LayoutElement`의 preferred/flexible을 아예 읽지 않는다)
- 폰트 확인 결과 **문제 없음**: 기본 폰트 `NotoSansKR-VF SDF`(Dynamic)는 한글·라틴 모두 보유.
  일부 UI가 쓰는 Roboto 계열(Static, 한글 없음)은 TMP **전역 fallback**이 `NotoSansKR-VF SDF`라 한글도 정상 표시된다
  (다만 그 라벨들은 ko에서 서체가 바뀌어 보인다 — 미관 이슈이며 차단 요소는 아니다)

### Phase 1 — 씬·프리팹 정적 라벨 (약 49개)
각 `TMP_Text`에 `LocalizeStringEvent`를 붙이고 키를 연결한다.

> **반드시 컴포넌트 헤더 우클릭 → `Localize`로 붙일 것.** `Add Component`로 직접 붙이면
> `On Update String (String)` 이벤트가 **빈 채로** 만들어진다. 이 상태에서도 컴포넌트는 있고 키도 정상이라
> 인스펙터로는 멀쩡해 보이지만, 가져온 문자열을 라벨에 써주는 쪽이 없어서 **언어를 바꿔도 아무 일도 안 일어난다.**
> 콘솔 에러도 없다. 실제로 SettingsCanvas 8개가 이 상태로 한 번 커밋됐다.
> 확인법: 프리팹 YAML에서 `m_Calls: []`가 아니라 `m_MethodName: set_text`가 들어 있어야 한다.
>
> **컴포넌트를 두 번 붙이지 않도록 주의.** 같은 라벨에 하나는 연결된 것, 하나는 빈 것이 남으면
> 연결된 쪽이 텍스트를 써서 **화면은 정상으로 보인다.** 언어가 바뀔 때마다 같은 키를 두 번 조회하고
> 다음 사람이 빈 쪽을 보고 헷갈릴 뿐이다 (PauseCanvas 버튼 2개에서 발생).

**씬 파일은 동시 편집 시 머지 충돌이 크다. 씬 하나 = 브랜치 하나 = PR 하나**로 끊어 진행한다.

| 대상 | 개수 | 상태 |
|------|------|------|
| Title Scene | **17** | ✅ 완료 (`TitleTable`, 브랜치 `feature/374-localization-title`) |
| Lobby / Shop | 6 | Lobby 2개(게임 시작·세션 나가기) · Shop 4개(시작 버튼 + `구매함` 3개) 진행 중 |
| Main Scene | 6 | 인명부 정렬·페이지 버튼 등 |
| 프리팹 | 약 26 | ✅ SettingsCanvas 8 · PauseCanvas 4 · LeaveConfirm 2 / 남음: QuitConfirm·AccountConfirm 5, Directory·FactionSymbolBoard·RoundEndButton, 월드 라벨 4 |

> **`TitleTable`에서 세션 코드를 뺐다.** §3이 "세션 코드 패널"을 `TitleTable`에 넣어 뒀는데,
> `SessionCodePanel.prefab`은 실제로 **Lobby·Shop 두 씬**에만 있고 Title 씬에는 없다.
> 두 씬이 `TitleTable`을 로드하게 되므로 `CommonTable`로 옮겼다 — `PauseTable`을 갈라낸 것과 같은 이유다.

> **Lobby의 Phase 2 항목을 앞당겼다.** 로비를 손대는 김에 코드가 대입하는 문구도 함께 옮겼다 —
> `LobbyRosterRowView`(접속 중·대기 중) · `LobbyRosterPanel`(무전 키·음성 상태) · `SessionCodePanel`(세션 코드).
> Phase 2 목록에서는 빠진다.
>
> **`LoadingScreen`은 프리팹 목록에서 코드 쪽으로 옮겼다.** `StatusText`는 지금 프리팹 기본 문구만 쓰는 정적
> 라벨이지만, `LoadingScreen.SetStatus`가 "전원 대기 표시 등 후속 확장용"으로 남아 있다. 여기에
> `LocalizeStringEvent`를 붙이면 그 확장이 들어오는 순간 대입과 컴포넌트가 서로 덮어써 조용히 깨진다.
> 그래서 `LocalizedString m_defaultStatus` + 구독으로 두고, `SetStatus`의 매개변수도 `string` →
> `LocalizedString`으로 바꿨다 — 완성된 한국어를 넘길 수 있게 두면 그 문구만 번역에서 빠지고,
> 로딩 중에는 언어를 바꿀 방법이 없어 눈에도 안 띈다.

> **`LocalizedStrings.Get(table, key, args)` 헬퍼를 뒀다** ([Assets/Scripts/Localization/LocalizedStrings.cs](../../Assets/Scripts/Localization/LocalizedStrings.cs)).
> 표시 문구는 원칙적으로 `LocalizedString` SerializeField지만, **인스펙터에서 고를 것이 없는 자리**에는 이 헬퍼를 쓴다.
>  · 규약 기반 키 — `접두 + enum 이름`. 결정 (h)가 매핑 에셋·인스펙터 배선을 두지 않기로 한 자리다.
>  · 전역 단위·서식 — `Common.Unit.Money`처럼 프로젝트 전체가 한 문구를 쓰는 자리.
>  · **같은 문구를 여러 인스턴스가 쓰는 자리** — 상점 진열대 3개가 그렇다. SerializeField로 두면 인스턴스마다
>    같은 키를 다시 배선해야 하고, 하나만 빠지면 그 진열대만 옛 문구로 조용히 남는다.
>
> 이 헬퍼는 **지금 언어로 한 번 읽어 주기만 한다** — 언어 변경 갱신은 호출부가 `SelectedLocaleChanged`를
> 구독해 다시 그려야 한다. `ShopStand`가 이미 그 방식이었고(항목별 `StringChanged`를 여럿 구독하는 대신
> 로케일 변경 한 곳에 걸고 표시를 통째로 다시 채운다), `TeamFundBalanceView`에도 같은 훅을 넣었다.

> **설치형 판매 품목의 이름·설명을 `ItemTable`로 올렸다.** `ShopStand`가 인스펙터 `string` 필드
> (`m_installableName`/`m_installableDescription`)에 한국어를 담고 있었고 "판매 설치형이 늘면 승격한다"고
> 미뤄 뒀는데, 이미 둘(`신호 해석기`·`경보 버튼`)이라 결정 (f)대로 승격했다. 키는 `Item.Name.<EInstallable>` ·
> `Item.Description.<EInstallable>`이다 — 소지형이 이미 `Item.Name.*`을 쓰므로 **두 종류의 출처가 같아진다.**
> 조준 카드에 둘이 나란히 뜨는 화면이라 여기서 갈라지면 한쪽만 번역된 상태가 그대로 보인다.

> 음성 상태 5개가 **결정 (h) 규약 기반 매핑의 첫 사용처**다. `LobbyRosterPanel`이 `"Lobby.Voice." + EVoiceState`로
> 키를 만들어 조회하므로, 상태가 늘면 `LobbyTable`에 키만 추가하면 된다 — 인스펙터 배선도 매핑 에셋도 없다.
> 그래서 그 `LocalizedString`은 `SerializeField`가 아니다(고를 것이 없다).
> `VivoxManager.ToLabel`은 지우지 않고 **디버그 GUI 전용**으로 남겼다.

> **PauseCanvas가 1개에서 4개로 늘었다** — 제목만 보고 세었는데 버튼 3개(`계속하기`·`설정`·`메인으로 나가기`)가 빠져 있었다.
> 이 버튼들은 **서드파티 GUIPack 버튼 프리팹의 중첩 인스턴스**이고 문구는 인스턴스 오버라이드로 박혀 있다.
> `LocalizeStringEvent`는 `PauseCanvas` 안의 인스턴스에 **추가 컴포넌트 오버라이드**로 붙인다 —
> 소스 프리팹(`Assets/Imported/GUIPack-.../Button Rounded - Filled - *.prefab`)을 열어 붙이면
> 프로젝트의 모든 버튼에 번지고 서드파티 에셋을 수정하는 것이 된다(CLAUDE.md 금지).
> 같은 형태의 버튼을 쓰는 다른 프리팹(확인창 3종 등)도 Phase 1에서 같은 방식으로 처리한다.

> SettingsCanvas가 7개에서 **8개로 늘었다** — Phase 0에서 언어 드롭다운을 넣으며 그 라벨(`LocalizationLabel`, "언어")을 함께 추가했다.
> 위 §1의 조사 수치(약 45개)는 착수 전 시점의 기록이라 그대로 둔다.

> **자리표시자와 구분할 것.** 씬·프리팹의 TMP 텍스트 중 상당수는 코드가 런타임에 덮어쓰는 디자인타임 값이다
> (`"라운드 성공!"`, `"김시민 — 보상 10,000원"`, `"HP: 100/100"`, `"0 / 0원"`, `"0원"`, `"현상수배범 검거!"` 등).
> 이들은 Phase 2에서 코드 쪽이 처리하므로 **여기서 건드리지 않는다** — `LocalizeStringEvent`를 붙이면 코드 대입과 서로 덮어쓴다.
> 판별법: 해당 문구나 그 필드에 대한 `.text =` 대입이 코드에 있는가.
> Title 씬에서 이 기준으로 제외한 것: `AccountStatusText` · `NicknameStatusText` · `PlayerIdText`
> (전부 [AuthPanel](../../Assets/Scripts/UI/Panels/AuthPanel.cs)이 대입한다) · `SessionPanel/StatusText`.
>
> 이 중 `StatusText`만 **Phase 2 방식으로 앞당겨 처리했다** — [SessionPanel](../../Assets/Scripts/UI/Panels/SessionPanel.cs)의
> `SetStatus()`가 넣던 문구 8개를 `LocalizedString` 필드로 옮겼다. 나머지 셋은 Phase 2에서 `AuthPanel`을 손볼 때 함께 간다.

> **입력창은 Placeholder에 붙인다.** `TMP_InputField`의 본문 텍스트가 아니라 `.../Text Area/Placeholder` 쪽이 대상이다 —
> 본문은 사용자가 친 글이라 번역 대상이 아니고, 코드가 대입하기도 한다.

#### Title 씬에서 문구가 바뀐 것 (번역하며 정정)
| 키 | 이전 | 이후 |
|----|------|------|
| `Title.Button.Quit` | `Quit` (한국어 UI인데 영문) | ko `종료` / en `Quit` |
| `Title.Auth.NicknamePlaceholder` | `Enter text...` (TMP 기본 더미) | ko `닉네임` / en `Nickname` |

### Phase 2 — 코드 조립 문자열 (약 60개)
`LocalizedString` SerializeField + Smart String으로 교체. 관례는 [SignalDecoder](../../Assets/Scripts/Item/SignalDecoder.cs)와 같다.

- `SettlementPanel` (12) · `AuthPanel`+`AccountCredentials`+`NicknameRules` (21) · `ScanInfoView` · `ScanResultPresenter` · `ShopStandView` · `CCTVChannelLabelView` · `HqRevivalDevice` · `BombTimerView`
- ~~`SessionPanel`~~ · ~~`LeaveConfirmPanel`~~ · ~~`LobbyRosterRowView`/`LobbyRosterPanel`~~ · ~~`SessionCodePanel`~~ — **Phase 1에서 앞당겨 처리했다** (해당 씬·프리팹을 손대는 김에)
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

## 7. 규약 기반 키의 검증

결정 (h)는 매핑 에셋을 없애는 대신 **컴파일러가 막지 못하는 구멍**을 남긴다 — enum에 값을 추가하고
테이블 키를 잊으면 그 값에서만 문구가 비고, 그 코드 경로를 밟기 전까지 아무 신호도 없다.
테이블을 이름 문자열로 참조하는 것도 같은 성질이다(다른 곳은 인스펙터의 GUID 참조라 개명에 안전하다).

그물을 두 겹 둔다.

**① 선언 — [`[LocalizedEnum]`](../../Assets/Scripts/Localization/LocalizedEnumAttribute.cs)을 enum 선언부에 붙인다.**
값을 추가하는 사람이 가장 먼저 보는 자리에 규약이 적혀 있게 하는 것이 목적이고, 동시에 검증의 근거가 된다.
접두가 둘 이상이면 여러 번 붙이고, 표시 대상이 아닌 값은 `except`로 뺀다.

```csharp
[LocalizedEnum("LobbyTable", "Lobby.Voice.")]
public enum EVoiceState { Idle, LoggingIn, Joining, Connected, Failed }

[LocalizedEnum("ItemTable", "Item.Name.", nameof(EInstallable.None))]
[LocalizedEnum("ItemTable", "Item.Description.", nameof(EInstallable.None))]
public enum EInstallable { None, SignalDecoder, JailSirenButton }
```

**② 검사 — 에디터 메뉴 `Tools ▸ Localization ▸ 규약 키 검증`**
([LocalizedEnumValidator](../../Assets/Scripts/Editor/LocalizedEnumValidator.cs)).
선언이 붙은 enum을 전부 훑어 값마다 키가 있는지, 그리고 **로케일별 값이 비지 않았는지**까지 확인한다.
검사 대상 등록표를 따로 두지 않는다 — 선언 자체가 목록이라 새 규약은 어트리뷰트만 붙이면 자동 편입된다.
현재 기준 enum 2개 · 키 9개가 통과한다.

여기에 [`LocalizedStrings.Get`](../../Assets/Scripts/Localization/LocalizedStrings.cs)이 에디터에서만
**실제로 밟은 경로의 누락을 키마다 한 번 경고**한다. ②가 미리 훑는 그물이고 이쪽은 빠져나간 것을 잡는 그물이다.

> Phase 3에서 `Item.Feedback.*` · `Shop.Reply.*`를 추가할 때도 **`EItemFeedback`·`EShopReply`에 어트리뷰트를 붙이면
> 검증이 그대로 따라온다.** 별도 작업이 필요 없다.
