# 이슈 #222 — 세력 문양(Faction Symbol) 대조 계획

> 작성 2026-07-26. 세력별로 비슷한 문양 여러 개를 두고, **세션마다 진짜 하나를 랜덤 선정**해
> 스캔 UI·본부 대조자료에 쓰이게 한다. 위조범은 가짜 문양을 달아 대조 대상이 된다.

## 1. 목표 (확정된 요구사항)

1. 세력마다 **서로 비슷한 문양 이미지 세트**(variant 여러 개)를 준비한다.
2. **세션(방/Relay 세션) 시작 시 세력별로 "진짜" 문양 1개를 랜덤 선정** — 나머지는 가짜.
   - **세션 스코프**: 방을 파는 순간 정해지고 **그 방이 유지되는 동안 고정**. 라운드가 여러 번 돌아도 재추첨하지 않는다. 방을 새로 팔 때만 새로 정해진다.
3. 스캔 데이터 UI에서 **세력 이름 옆에 문양 이미지**를 함께 표시한다.
4. **본부 대조자료 뷰**에서 이번 세션의 세력별 진짜 문양을 확인할 수 있게 한다 (Directory 옆에 배치).
5. **위조범(#223)** 은 진짜와 다른 **가짜 문양**을 달아, 본부가 대조자료와 비교해 적발할 수 있게 한다.

## 2. 확정된 설계 결정

| 항목 | 결정 |
|------|------|
| (a) 위조 축 결합 | **①** — 기존 위조범이 **이름·문양 중 하나를 랜덤으로** 오염. 본부가 매번 "이름이 안 맞나 / 문양이 안 맞나"를 새로 대조 |
| (b) None 세력 | **랜덤 배정·문양 위조에서 제외** (None = 무소속·문양 없음) |
| (c) variant 1개짜리 세력 | 가짜로 뽑을 다른 variant가 없으므로 **그 세력 위조범은 이름 위조로 폴백** (①에서 자동 처리됨) |
| (d) 본부 대조자료 뷰 위치 | **Directory 옆 — 별도 설치물**(게시판)로. 인명부 책 옆에 놓고 E로 여는 독립 패널 (2026-07-27 확정: 인명부 패널 안에 끼워넣는 안은 기각) |
| 세력 수 | 현재 2개(A/B)지만 **데이터/동기화는 세력 수에 무관하게 설계** — 나중에 enum·에셋만 추가하면 늘어남. 실제 개수는 밸런싱하며 결정 |
| 진짜 index 동기화 방식 | **명시적 `NetworkList<byte>`** (호스트 시드 파생 방식보다 눈에 보이고 틀릴 여지가 적음) |

## 3. 데이터 흐름 (두 갈래)

- **per-NPC 문양** — `CitizenData`에 `SymbolIndex(byte)` 추가. 정직한 시민 = 진짜 index, 위조범 = 가짜 index.
  스캔 UI가 이 값으로 심볼을 해석한다. 기존 `NameView` 동기화와 완전히 같은 패턴.
- **세력별 세션 진짜 index** — 본부 대조자료 뷰는 "이번 판 진짜 문양"을 알아야 하는데,
  클라이언트는 NPC만 봐선 진짜/가짜를 구분할 수 없다. 그래서 **세력→진짜index 맵을 세션 1회 roll해 별도로 동기화**한다.
  서버가 위조범의 가짜 index를 "진짜 ≠ 가짜"로 뽑을 때도 이 값을 기준으로 쓴다.

## 3-1. 진행 상태 (2026-07-27 기준)

| 단계 | 상태 |
|---|---|
| 1) OfficialRecords variant 세트 | ✅ 완료 (실제 API명: `GetVariants` / `GetVariantsCount` / `GetFactionSymbol(faction, index)`) |
| 2) FactionSymbolManager + App 등록 + 프리팹 | ✅ 완료 (스폰 배선까지) |
| 3) per-NPC 문양 배정·동기화 | ✅ 완료 (컴파일 통과 확인) |
| 4) 스캔 UI 문양 표시 | ✅ 완료 (Play 확인) |
| 5) 본부 대조자료 뷰 (Directory 옆) | ☐ **여기서 이어서 시작** |

**에디터 배선 완료**
- `FactionSymbolManager` 프리팹: `DefaultNetworkPrefabs.asset` 등록 + Main/Title 두 씬의 `SessionObjectSpawner.m_persistentPrefabs`에 추가 완료.
  - 참고: `Spawn(destroyWithScene:false)`는 스폰 시점에 DontDestroyOnLoad로 옮기지 않는다. 씬 전환 때 `NetworkSceneManager`가 DDOL로 대피시켰다가 새 활성 씬으로 되돌리므로, 평상시 Hierarchy에서는 현재 씬 밑에 보이는 게 정상.
- `OfficialRecord.asset`의 `m_factionSymbolSets`: FactionA/FactionB × variant 2개씩 (`Assets/Sprites/Faction{A,B}-{1,2}.png`). None은 항목 자체를 넣지 않는다 — `GetVariantsCount`가 0이 되어 배정·표시에서 자동으로 빠진다.

**다음 세션 재개 지점 — 5단계.** 산출물: 인명부 책 **옆에 놓인 별도 게시판**을 E로 열면
이번 세션의 세력별 진짜 문양 목록이 뜬다. 구성:
- `HqPanelView`(신규 베이스) — "E로 열고 Esc로 닫는 본부 패널"의 공통 뼈대(입력 정지·커서 복구·EscMenuGuard).
  같은 뭉치가 이미 `BombManualHud`(추격 폭탄 개편으로 삭제, #399)·`CitizenDirectoryView` 두 곳에 복제돼 있어 세 번째를 만들기 전에 추출한다.
  `CitizenDirectoryView`를 여기로 이관(`Open`/`Close`/`Update`/`m_root` 제거 → `OnOpened`/`OnClosed` 오버라이드).
- `FactionSymbolBoard`(상호작용 진입점, `CitizenDirectory` 패턴) / `FactionSymbolBoardView : HqPanelView` / `FactionSymbolRowView`(세력명+문양 한 줄).
- 에디터: `FactionSymbolRow.prefab`(Image는 **Preserve Aspect** 켤 것) · Main Scene에 패널 · `FactionSymbolBoard.prefab`을 인명부 옆에 배치.

⚠️ 테스트 시: `FactionSymbolManager`가 안 뜨면 뷰는 index 0을 보여주는데 서버 `CriminalAssigner`는
로컬 난수로 진짜를 정한다 → 정직한 시민이 전부 위조범으로 보인다. 그 증상이 나오면 매니저 스폰부터 의심할 것.

## 4. 작업 순서

각 단계 앞에 **[산출물]** 로 "이 단계를 끝내면 무엇이 생기는지"를 적어둔다.

### 1) 데이터 모델 — OfficialRecords variant 세트
- **[산출물]** 세력마다 문양을 **여러 개** 담을 수 있는 그릇과, "이 세력의 N번째 문양 줘"라고 꺼내는 함수.
- [`OfficialRecords`](../Assets/Scripts/Data/Npc/OfficialRecords.cs): `FactionSymbol{faction, symbol}` → `FactionSymbolSet{faction, Sprite[] variants}` 로 변경.
- `GetFactionSymbol(faction, int index)` + `GetVariantCount(faction)` 추가.
- 코드만 먼저. variant 이미지 채우기는 아트/에디터 작업.

### 2) 세션 진짜 index 관리·동기화 — FactionSymbolManager
- **[산출물]** "이번 방에서 세력별 진짜 문양은 몇 번"이라는 정보를 **세션 시작 시 한 번 정하고 모두에게 공유**하는 매니저.
- 신규 `FactionSymbolManager : NetworkedManagerBase` (architecture R4).
- 서버 `OnNetworkSpawn` 시 **1회** 세력별 진짜 index roll → `NetworkList<byte>`에 저장(index = `(int)faction`).
- 세션 내내 재추첨하지 않으므로 라운드가 바뀌어도 유지. 늦게 접속한 클라도 현재값 수신.
- `App.Game`에 노출, `RealIndex(faction)` 제공.

### 3) per-NPC 문양 배정·동기화
- **[산출물]** NPC 한 명 한 명이 "내 문양은 몇 번"을 갖고, 그게 전 클라에 동기화됨. 정직=진짜, 위조범=가짜.
- [`CitizenProfile`](../Assets/Scripts/Data/Npc/CitizenProfile.cs)에 `m_symbolIndex` 추가.
- [`CitizenData`](../Assets/Scripts/Data/Npc/CitizenData.cs)에 `SymbolIndex(byte)` 추가 (NetworkSerialize·Equals·FromProfile).
- [`CriminalAssigner`](../Assets/Scripts/NPC/Profile/CriminalAssigner.cs): 정직 = `RealIndex(faction)`, 위조범 = 진짜와 다른 가짜 index.
  위조범의 이름/문양 오염은 (a)①에 따라 택1(문양 불가 세력은 이름으로 폴백).
- [`CitizenIdentity.RebuildProfile`](../Assets/Scripts/NPC/Profile/CitizenIdentity.cs:74): 수신한 index로 `m_symbolView` 재조회.

### 4) 스캔 UI 문양 표시
- **[산출물]** 스캐너로 조준하면 NPC 카드에 **세력 이름 + 문양 이미지**가 같이 뜸. 미스캔이면 문양도 마스킹.
- [`ScanInfoView`](../Assets/Scripts/UI/Scan/ScanInfoView.cs)에 `Image` 슬롯 추가, `ShowReal`/`ShowMasked` 확장.
- [`ScanResultPresenter`](../Assets/Scripts/UI/Scan/ScanResultPresenter.cs:177)가 `profile.m_symbolView` 전달.
- `ScanInfoCard.prefab`에 Image 배선 (에디터 작업).

### 5) 본부 대조자료 뷰
- **[산출물]** 본부 화면 Directory 옆에, 이번 세션의 **세력별 진짜 문양+이름 목록**. 세션 내내 고정.
- `FactionSymbolManager.RealIndex(faction)` + `OfficialRecords`를 읽어 세력별 진짜 문양을 나열.
- HQ의 [`DirectoryEntryView`](../Assets/Scripts/HQ/Directory/DirectoryEntryView.cs) 패턴 재사용.

## 5. 이미 있는 스캐폴드 (참고)

- `OfficialRecords`: `Faction{None,FactionA,FactionB}` + `FactionSymbol[]`(세력당 1개) + `GetFactionSymbol(faction)`.
- `CitizenProfile.m_symbolView`(표시 문양 필드) — `Initialize`에서 faction으로 조회해 채움.
- 동기화: `CitizenData`는 문양 미포함. 각 클라가 `Faction`만 받아 공유 `OfficialRecords`에서 로컬 재조회.
- `ScanInfoView`: 이름/타입/세력 **텍스트만** 표시 — 문양 Image 슬롯 없음.

## 6. 진행 방식

- 실제 코드 편집은 담당자(김진아)가 직접 한다. 이 문서/작업 단계마다 **무엇을 왜 어떻게 바꾸는지 diff 수준으로 짚어** 진행한다.
- 각 단계 착수 시 **[산출물]** 부터 간략히 설명하고 들어간다.
