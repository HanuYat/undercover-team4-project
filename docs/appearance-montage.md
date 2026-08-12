# 몽타주 & 외형 시스템 설계 (이슈 #221 / #222)

> 초안 — 팀 검토 필요. 이슈 #221(몽타주 데이터 세분화)·#222(Faction 문양 대조) 진행 근거 문서.
> 관련 정본: [GDD.md](GDD.md) 5-1(육안 관찰)·5-3(데이터 스키마)·5-4(본부 판독)·10-3(몽타주 표현)·10-4(기술 스택).
> 2026-07-21 대폭 개정: **2-생산자 아키텍처**(Generic 프롭 + SciFi 카탈로그 병행), 몽타주 표시 3모드 병행, Mark 축 제거, 색 노이즈 난이도 레버 편입.

## 1. 원칙

- **몽타주는 화면과 모순되지 않는다.** 몽타주가 **서술하는 축**은 화면 외형과 100% 일치해야 한다(코어: 육안 관찰 → 스캔 대조, GDD 5-1→5-4). 단 몽타주는 외형 전체가 아니라 **부분집합**을 서술할 수 있다(모든 특징을 다 부를 필요는 없음).
- **(텍스트 모드) 몽타주 값은 무전으로 구두 전달 가능해야 한다** — ① 단색/이산값 ② 말로 나열 가능 ③ (색이면) 부위 한정.
- **외형은 종족(인간/안드로이드)을 드러내지 않는다.** 종족은 오직 스캔으로만 확인(§8과 무관, GDD). 모든 외형 축은 종족과 완전 독립 랜덤.
- **표현 방식: 텍스트/픽셀/실루엣 3모드를 병행 구축하고 플레이 테스트로 확정.** (기존 "글 방식 확정" → 이미지 방식도 자동 생성 가능함이 확인돼 병행 실험으로 변경. §7)

## 2. 2-생산자 아키텍처 (핵심)

외형을 **"어떻게 만드느냐"(생산)** 와 **"몽타주가 무엇을 읽느냐"(소비)** 를 분리한다. 소비 측(몽타주 텍스트·매칭·디코이·수배UI)은 공통 표현만 읽고, 생산은 두 경로가 담당한다.

- **공통 통화:** `AppearanceProfile`(6축, 네트워크 직렬화) + `IAppearanceProfileSource { AppearanceProfile Profile; }`.
- **프리팹 2종**(둘 다 위 인터페이스 노출):

| | `NPC_Citizen_Generic` | `NPC_Citizen_SciFi` |
|---|---|---|
| 바디 | PolygonGeneric **민머리 클린베이스** | PolygonSciFiCity **통짜 캐릭터 20종** |
| 외형 조립 | 축 랜덤 배정 → **프롭/틴트 적용** | 20바디 중 하나 **토글** |
| Profile 출처 | 배정한 값이 곧 Profile (**입력**) | **카탈로그[모델] 룩업** (모델이 결정) |
| 몽타주 일치 | 배정대로 조립·렌더 → 일치 | 실물을 카탈로그화 → 일치 |
| 담당 컴포넌트 | `NpcAppearance` (기존) | `NpcCatalogAppearance` (신규) |
| 역할 | **조합형 "어려운 풀"** | **개성형 "플레이버 풀"** |

- 한 라운드에 **두 종류가 섞여도** 소비 측은 출처를 몰라도 된다(같은 `AppearanceProfile`). 스폰 시 Generic:SciFi 비율만 조절.
- 값 vocabulary는 **두 생산자의 합집합**. 각 프리팹은 표현 가능한 부분집합만 사용(SciFi 고유값은 카탈로그 전용).

## 3. 공통 축 스키마 (6축)

`AppearanceAxis` = **HairStyle / HairColor / SkinColor / FacialHair / Headwear / Eyewear** (6축, `k_axisCount=6`).

> **Mark(고유표식) 축은 도입하지 않는다** (2026-07-21 결정). SciFi 모델의 안테나·외눈·페이스페인트 등은 너무 단독 식별적이라 정식 몽타주/매칭 축으로 두면 게임이 쉬워진다. 이런 표식은 **화면에만 있는 플레이버**로 두고 몽타주는 6축의 부분집합만 서술한다(§1: 몽타주는 부분집합 서술 가능).

| # | 축 | Generic(프롭) 값 | SciFi(카탈로그) 값 예시 | 적용(Generic) |
|---|---|---|---|---|
| 0 | HairStyle | 대머리/짧은/긴/스파이크/묶음 | (모델별 고정) | 머리 mesh 프롭 |
| 1 | HairColor | 검정/금발/빨강/파랑 | 검정/은발/청록/핑크/없음 | HairStyle 프롭 틴트 |
| 2 | SkinColor | 살/창백/갈색 | 살/파랑/은색메탈/네온 | 바디 `_Skin_Color` 틴트 |
| 3 | FacialHair | 없음/콧수염/턱수염/구레나룻 | 없음/회색풍성 | 프롭 |
| 4 | Headwear | 없음/비니/캡 | 경찰모/페도라/후드/헤드폰/풀헬멧/가스마스크 | 프롭 |
| 5 | Eyewear | 없음/선글라스 | 바이저/원형/AR고글/방독고글 | 프롭 |

- SkinColor는 **Generic 경로에서 부활**한다 — Generic 바디는 `Generic_Standard` 셰이더라 `_Skin_Mask` 지원(SciFiCity의 `Generic_Basic`은 미지원이라 기존 보류였음). SciFi는 카탈로그 고정값.
- 랜덤 규칙(Generic): 옵션별 **균등 랜덤**. 비율 조정은 옵션 개수로.

### 3-1. 옵션 ↔ 에셋 매핑 (Generic 프롭)

프롭 에셋: `Assets/Imported/Synty/PolygonGeneric/Prefabs/Characters/Attachments/`

| 축 | 옵션 | 에셋 프리팹 | 비고 |
|---|---|---|---|
| HairStyle | 대머리 | (없음) | 프롭 미부착 |
| | 짧은머리 | `SM_Gen_Chr_Attach_Hair_04`~`_06` 중 택1 | Editor 확정 |
| | 긴머리 | `SM_Gen_Chr_Attach_Hair_10`/`_11` 중 택1 | 〃 |
| | 묶음머리 | `SM_Gen_Chr_Attach_Ponytail_01`/`Bun_01` | 〃 |
| HairColor | 검정/금발/빨강/파랑 | (프롭 없음, 틴트) | 색값 아래 |
| SkinColor | 살/창백/갈색 | (프롭 없음, 바디 틴트) | 색값 아래 |
| FacialHair | 콧수염 | `SM_Gen_Chr_Attach_Moustache_01` | |
| | 턱수염 | `SM_Gen_Chr_Attach_Beard_01`(또는 `_02`) | |
| | 구레나룻 | `SM_Gen_Chr_Attach_Chops_01` | |
| Headwear | 비니 | `SM_Gen_Chr_Attach_Beanie_01` | |
| | 캡 | `SM_Gen_Chr_Attach_Hat_01`(또는 `_02`) | |
| Eyewear | 선글라스 | `SM_Gen_Chr_Attach_Sunglasses_01` | |

색값 제안 (linear RGB):

| 옵션 | R,G,B | | 옵션 | R,G,B |
|---|---|---|---|---|
| 머리 검정 | 0.12, 0.12, 0.12 | | 피부 살 | 0.85, 0.68, 0.55 |
| 머리 금발 | 0.95, 0.78, 0.35 | | 피부 창백 | 0.95, 0.85, 0.78 |
| 머리 빨강 | 0.75, 0.15, 0.12 | | 피부 갈색 | 0.55, 0.38, 0.26 |
| 머리 파랑 | 0.20, 0.40, 0.90 | | | |

## 4. Generic 경로 (프롭 방식)

Synty PolygonGeneric 캐릭터는 **모듈러**(민머리 두상 + 탈착 머리/모자/안경 부착물). 확인됨(2026-07-21 렌더): `Underwear_Male_01`은 완전 민머리, `Peasent_Female_01`의 머리는 별도 부착물 SMR(`Attach_Hair_02`).

- **클린 베이스**: 내장 머리/모자 부착물이 없는(또는 끈) Generic 바디 위에 몽타주 프롭이 외형을 100% 전담.
- 적용 순서: HairStyle(프롭) → HairColor(그 프롭 틴트) → SkinColor(바디 `_Skin_Color`) → FacialHair/Headwear/Eyewear(프롭). 색은 `MaterialPropertyBlock`(공유 머티리얼 불변).
- 담당: 기존 [NpcAppearance.cs](../Assets/Scripts/NPC/NpcAppearance.cs) (프롭 로직 이미 구현됨 — P1/P2).

## 5. SciFi 경로 (모델별 카탈로그)

PolygonSciFiCity 캐릭터는 **머리/모자/헬멧/고글이 메시에 통짜로 구워진 단일 mesh**(2026-07-21 렌더로 확정). 따라서 클린베이스/프롭 방식 불가 → **모델 자체를 몽타주 소스로 삼는다.**

- **바디 토글**: `NPC_Citizen_SciFi/Model` 아래 20바디가 `Root` 스켈레톤 공유, 1개만 active. `NpcAppearance.ApplyModel`이 인덱스로 토글(구현 완료, B안).
- **신규 `NpcCatalogAppearance`**: 서버가 뽑은 모델 인덱스(NetworkVariable)로 `AppearanceModelCatalog` SO에서 `AppearanceProfile`을 룩업해 그대로 세팅. 프롭 인스턴스화 없음.
- **`AppearanceModelCatalog` SO**: `모델 인덱스 → AppearanceProfile`(+텍스트 태그). 아래 카탈로그 표를 데이터화.

### 5-1. SciFi 20모델 특징 카탈로그 (렌더 기준)

> 몽타주 축(굵게)은 6축에 매핑되는 값. "고유표식" 열은 **플레이버**(몽타주 비서술).

| # | 모델 | **SkinColor** | **HairStyle/Color** | **Headwear** | **Eyewear** | **FacialHair** | 고유표식(플레이버) |
|---|---|---|---|---|---|---|---|
| 00 | Alien_Male_01 | 파랑 | 대머리 | 없음 | 없음 | 없음 | 안테나·뾰족귀 |
| 01 | Alien_Male_02 | 은색메탈 | 대머리 | 없음 | 없음 | 없음 | 콘두상·무표정 |
| 02 | Android_Female_01 | 은색메탈 | 두건/핑크 | 헤드스카프 | 없음 | 없음 | 안드로이드 금속얼굴 |
| 03 | Augmented_Male_01 | 살 | 짧은/갈색 | 이마밴드 | 없음 | 없음 | — |
| 04 | Cop_01 | 살 | (가림) | 경찰모 | 없음 | 없음 | 흉터·하관 마스크 |
| 05 | Cyber_Female_01 | 살 | 긴/검정 | 없음 | 없음 | 없음 | 페이스페인트·검은입술 |
| 06 | Cyber_Male_01 | 살 | 짧은/검정 | 없음 | 바이저 선글라스 | 없음 | — |
| 07 | CyberPunk_Male_01 | 살 | 짧은/검정 | 이마밴드 | 없음 | 없음 | — |
| 08 | CyborgNinja_01 | 은색메탈 | (가림) | 풀헬멧 | 외눈렌즈 | 없음 | 사이클롭스 외눈 |
| 09 | Garbage_Male_01 | 살 | (가림) | 갈색캡 | 고글 | 없음 | 가스마스크 |
| 10 | Hacker_Female_01 | 살 | 스파이크/청록 | 없음 | AR고글 | 없음 | 네온 바이저 |
| 11 | Hologram_Female_01 | 네온핑크 | 긴/핑크 | 없음 | 발광 눈 | 없음 | 전신 홀로그램 |
| 12 | Junky_Female_01 | 살 | 스파이크/금발 | 빨간 헤드폰 | 없음 | 없음 | 눈물자국 |
| 13 | Junky_Male_01 | 살 | (가림) | 갈색 가죽후드 | 원형 고글 | 없음 | 비행사풍 |
| 14 | Medical_Male_01 | (가림) | 후드 | 후드 | 노란 3렌즈 | 없음 | 가스마스크·트라이렌즈 |
| 15 | Monk_Male_01 | 살 | (가림) | 크림 캡 | 초록 바이저 | 없음 | — |
| 16 | Muscle_Male_01 | 살 | 짧은/검정 | 없음 | 파란 원형선글라스 | 회색 풍성 | — |
| 17 | Rich_Female_01 | 살 | 단발/은발 | 없음 | 없음 | 없음 | 귀걸이·흰눈썹 |
| 18 | Rich_Male_01 | 살 | 구레나룻/백발 | 흰 페도라 | 없음 | 흰 콧수염 | 신사풍 |
| 19 | Robot_01 | 은색메탈 | 대머리 | 로봇 헬멧 | T바이저(발광) | 없음 | 완전 로봇 |

## 6. 색 & 난이도

### 6-1. 색 시스템 (Synty 셰이더)

Synty `Generic_Standard`/`Generic_Basic`는 부위별 마스크 틴트를 지원.

| 레버 | 프로퍼티 | 몽타주 | 용도 |
|---|---|---|---|
| 아틀라스 스왑 | `_Albedo_Map` (`Materials/Alts/Generic_01~04_A/B/C`, 12종) | ❌ 비식별 | **코스메틱 색 노이즈**(§6-2) |
| 머리색 틴트 | `_Hair_Color`+`_Hair_Mask` | ✅ | (클린베이스에선 머리 프롭 틴트로 대체) |
| 피부색 틴트 | `_Skin_Color`+`_Skin_Mask` | ✅ | SkinColor 축(Generic) |

### 6-2. 색 팔레트 노이즈 (난이도 레버)

- **비몽타주 코스메틱 노이즈**: 옷·전체 배색을 흔들어 플레이어가 색 인상으로 찍지 못하게 하고 **실제 몽타주 축**을 보게 강제한다.
- `AppearanceProfile`에 **넣지 않고** 모델 인덱스처럼 서버 권위로 독립 동기화(`m_colorVariantIndex` NetworkVariable + `_Albedo_Map` 아틀라스 스왑). 몽타주는 이 색을 서술하지 않으므로 §1 원칙 불변.
- **주로 Generic 경로**가 담당(Alts가 PolygonGeneric 셰이더용). SciFi는 모델별 고정 텍스처.

### 6-3. 난이도 레버 (몽타주와 분리)

| 레버 | 경로 | 몽타주 영향 |
|---|---|---|
| Generic 조합형 미묘 차이 | Generic | 축 값 |
| 색 팔레트 노이즈(아틀라스 스왑) | Generic | ❌ 비몽타주 |
| 같은 모델/프로필 중복 스폰 | 둘 다 | 동일 몽타주 |
| revealedAxisCount(부분 공개) | 둘 다 | 공개 축 수 |

## 7. 몽타주 표시 — 레이어 포트레이트 (#607, 2026-08-12 확정)

> 3모드(텍스트/픽셀/실루엣) 병행 실험 계획을 대체한다. "NPC를 통째로 렌더해 뭉갠다"가 아니라
> **축별 레이어를 겹쳐 그리는** 방식으로 갔다 — 통짜 렌더는 공개되지 않은 축까지 그려 버려
> 부분 공개(§6-3 난이도 레버)와 몽타주 부합 인원 k의 보장이 무너지기 때문이다.

무채색 두상 위에 **공개된 축만** 얹어 그린다. 말하지 않은 축은 그리지 않으므로, 안 그려진 자리는
'없음'이 아니라 '미상'으로 읽힌다. 그림이 말하는 것이 공개 축 딱 그만큼이라 k 보장이 그대로 성립한다.

| 레이어 | 굽는 법 | 표시 |
|---|---|---|
| 살(베이스) | 마네킹 바디를 **평면 흰색**으로 렌더 | 피부색으로 칠함 |
| 이목구비 | 조명 렌더에서 피부보다 어두운 픽셀만 추출 | 칠하지 않음 |
| 머리스타일 | 프롭을 **흰 실루엣**으로 | **머리색으로 칠함** |
| 수염·모자·안경 | 프롭을 **실제 머티리얼 + 옵션 색**으로 | 칠하지 않음 |

- 프롭 레이어는 바디를 **검정 오클루더**로 남긴 채 렌더한다 — 바디를 끄면 머리 뒤로 넘어간
  뒷머리·모자 뒤통수까지 찍혀 얼굴을 덮는다. 색은 실물 렌더에서, 알파는 흰색 렌더에서 가져와 합친다.
- **미공개 색 축은 반투명**으로 표시한다. 무채색으로만 두면 은발(0.76,0.78,0.82)과 구분되지 않아
  미공개가 실제 축 값 하나를 사칭하게 된다.
- 이미지는 수배 항목에 싣지 않는다 — 재료(공개 축 + 값)만 실려 오고 각 피어가 조립한다 (#497과 같은 규칙).
- 생성 툴: **`Tools/몽타주 레이어 굽기`** 에디터 메뉴. 런타임 렌더가 없으므로 첫 프레임 글리치 문제도 없다.
- 글 방식은 표시에서 빠지고 행 프리팹의 토글(`m_showMontageText`)로만 남는다.

### 7-1. SciFi 전용 값 (미해결)

SciFi 바디는 통짜 메시라 부위를 떼어낼 수 없고, Generic 프롭도 없어 레이어를 굽지 못한다.
그림이 없는 값은 `AppearanceDatabase.CanDepict`가 **그 범인의 공개 축 후보에서 뺀다** — 그림을 채우면
저절로 후보로 돌아온다. 남은 값과 대안:

| 값 | 쓰는 모델 | 대안 프롭 |
|---|---|---|
| `Headwear[6]` 헬멧 | 04, 14 | PoliceStation `Helmet_01~04`, Apocalypse `RiotCop_Male_Helmet_01` |
| `Eyewear[4]` 발광렌즈 | 11, 16, 19 | PoliceStation `Goggles_01/02`, Apocalypse `Soldier_Male_Glass_01` |
| `Headwear[4]` 후드 | 13 | **없음** — 어느 팩에도 후드 부착물이 없다 |
| `Eyewear[2]` 바이저 | **없음** | 죽은 값 — 어떤 모델도 안 쓴다. 모델에 배정하거나 어휘에서 뺄 것 |

- `HairStyle[4]` 가림은 **그리지 않는 것이 곧 그 값**이라 그림이 필요 없다(대머리와 같은 취급).
- SciFiOnly 값에 프롭을 꽂아도 Generic NPC는 그 값을 뽑지 않는다(`GetGenericSelectableIndices`가 제외).
  다만 **SciFi 범인의 Generic 디코이**는 공개 축을 베끼므로 그 프롭을 실제로 착용한다 — 맞춤 확인 필요.

## 8. 이슈 #222 — Faction(문양) 대조

스캐폴드 존재, 매 라운드 랜덤 배정 동작 중([CriminalAssigner.cs:163](../Assets/Scripts/NPC/CriminalAssigner.cs:163)).

- 데이터: `OfficialRecords.Faction {None, FactionA, FactionB}` + `FactionSymbol[]`/`GetFactionSymbol()`, `CitizenProfile.Faction`+`m_symbolView`, `CitizenData`.
- **남은 작업**
  - [ ] AI 심볼 이미지 세트 제작 → `OfficialRecords.m_factionSymbols` 연결.
  - [ ] "대조용 참고자료" 정리 — 본부 참고자료 뷰가 `OfficialRecords`를 읽는지 확인.
  - [ ] None 정책 — 위조 대조 축으로 쓸 거면 랜덤 배정에서 None 제외 여부 결정.

## 9. 코드/에셋 변경 체크리스트 (#221)

| 항목 | 상태 |
|---|---|
| `AppearanceProfile` 6축 (enum·직렬화·Equals) | ✅ 완료 |
| `NpcAppearance` 프롭 적용 + 바디 토글(B안) | ✅ 완료 |
| `NPC_Citizen`(SciFi) = 20바디 토글 + `NpcCatalogAppearance` | ✅ 완료 (`m_modelRoot`·`m_catalog` 배선) |
| `AppearanceModelCatalog` SO/에셋 (모델→Profile, 20종) | ✅ 완료 (§5-1 데이터화·end-to-end 검증) |
| `NpcCatalogAppearance` 컴포넌트 | ✅ 완료 |
| `IAppearanceProfileSource` 공통 인터페이스 | ✅ 완료 |
| `AppearanceDatabase` 어휘 확장 (5/9/6/4/7/5) | ✅ 완료 |
| `NPC_Citizen_Generic` 프리팹 (Generic 민머리 바디 13종 + 프롭) | ✅ 완료 (Model 정리·Animator NPC 리타깃) |
| `AppearanceAssigner` 두 경로 공존 (SciFi 모델선택 / Generic SetProfile) | ✅ 완료 |
| `NpcSpawner` 혼합 스폰 (`m_npcPrefabAlt`+`m_altRatio`) | ✅ 완료 (호스트 Play 확인) |
| 네트워크 등록 (DefaultNetworkPrefabs, 해시 충돌 없음) | ✅ 완료 |
| **머리색 중립베이스 머티리얼** (Generic 머리색 선명) | ☐ 폴리시 미구현 |
| `m_colorVariantIndex` 색 노이즈 (Generic, §6-2) | ☐ 미착수 |
| 몽타주 포트레이트 레이어 + 굽기 툴 (§7) | ✅ 완료 (#607 — 레이어 15장, `Assets/Imported/Art/Montage/Layers`) |
| 공개 축을 범인별로 분리 (§7·#556 제약 완화) | ✅ 완료 (#607) |
| SciFi 전용 값 레이어 (§7-1 — 헬멧·발광렌즈·후드) | ☐ 미착수 |
| v1 정밀화 (다중범인 중복부합, SciFi 디코이 부족 처리) | ☐ 미착수 |
| `AppearanceAssigner` revealedAxisCount / 종속축(가림·대머리→머리색 '없음' 반영됨) | ☐ 재검토 |

## 10. GDD 10-3 갱신 제안 문구

> ### 10-3. 몽타주(외형) 표현 방식
> - **표현 3모드 병행 실험 후 확정**: 텍스트(축 나열) / 픽셀(모자이크) / 실루엣. 전부 NPC 렌더에서 자동 생성돼 화면과 일치.
> - 몽타주 축: 머리스타일 / 머리색 / 피부색 / 수염 / 모자 / 안경 (6축). 상세는 [appearance-montage.md](appearance-montage.md).
> - **외형 생산 2경로**: Generic 프롭 조합(조합형 난이도) + SciFiCity 모델 카탈로그(개성). 색 팔레트 노이즈는 비몽타주 난이도 레버.

## 11. 구현 순서 (원계획 — 대부분 완료, §12 참조)

1. ~~문서·데이터 — `AppearanceModelCatalog`~~ ✅
2. ~~SciFi 경로 — `NpcCatalogAppearance`~~ ✅
3. ~~Generic 경로 — `NPC_Citizen_Generic`~~ ✅
4. 색 노이즈 — `m_colorVariantIndex` (Generic) ☐
5. 몽타주 표시 — 포트레이트 툴 + 텍스트/픽셀/실루엣 + 수배UI 토글 ☐
6. 배정·마감 — revealedAxisCount, 종속축 재검토 ☐
7. #222 — §8 ☐

## 12. 현재 구현 상태 & 이어가기 (2026-07-21 기준)

> **몽타주 코어(관찰→후보 좁히기→스캔)가 SciFi·Generic 두 경로 다 실동작**. 혼합 스폰·배정·몽타주=화면 일치까지 호스트 Play로 검증됨. 남은 건 폴리시·다양성뿐.

### 파일 지도
| 파일 | 역할 |
|---|---|
| [AppearanceProfile.cs](../Assets/Scripts/Data/AppearanceProfile.cs) | 6축 프로필 struct (공통 통화), 네트워크 직렬화 |
| [AppearanceDatabase.cs](../Assets/Scripts/Data/AppearanceDatabase.cs) + `.asset` | 축별 옵션(표시이름·색·프롭) + `BuildMontageText` (5/9/6/4/7/5) |
| [AppearanceModelCatalog.cs](../Assets/Scripts/Data/AppearanceModelCatalog.cs) + `.asset` | SciFi 모델 인덱스 → 고정 Profile (20종, §5-1) |
| [IAppearanceProfileSource.cs](../Assets/Scripts/NPC/IAppearanceProfileSource.cs) | 소비 측 공통 접점 (`Profile`) |
| [NpcAppearance.cs](../Assets/Scripts/NPC/NpcAppearance.cs) | **Generic 경로** — 바디 토글 + 프롭/틴트 (`SetProfile`) |
| [NpcCatalogAppearance.cs](../Assets/Scripts/Data/NpcCatalogAppearance.cs) | **SciFi 경로** — 모델 토글 + 카탈로그 룩업 (`SetModelIndex`·`ModelIndex`·`Catalog`) |
| [AppearanceAssigner.cs](../Assets/Scripts/NPC/Appearance/AppearanceAssigner.cs) | 배정기 — 두 경로 공존(`RealizeCriminal/Decoy/NonMatching`), 디코이 k 보장, **범인별** 공개 축 선택 |
| [MontagePortraitView.cs](../Assets/Scripts/HQ/WantedList/MontagePortraitView.cs) | 수배 행의 그림 몽타주 — 공개 축 레이어를 겹쳐 그린다 (§7) |
| [MontageLayerBaker.cs](../Assets/Scripts/Editor/MontageLayerBaker.cs) | 레이어 굽기 에디터 툴 — `Tools/몽타주 레이어 굽기` |
| [NpcSpawner.cs](../Assets/Scripts/NPC/NpcSpawner.cs) | `m_npcPrefab`(SciFi)+`m_npcPrefabAlt`(Generic)+`m_altRatio` 혼합 스폰 |
| `Assets/Prefabs/NPC/NPC_Citizen.prefab` | SciFi NPC (20 통짜 바디 토글, `NpcCatalogAppearance`) |
| `Assets/Prefabs/NPC/NPC_Citizen_Generic.prefab` | Generic NPC (민머리 옷 바디 13종 + `NpcAppearance`, Animator=NPC 컨트롤러 휴머노이드 리타깃) |

### 핵심 동작 원리 (재확인용)
- **공통 통화** `AppearanceProfile`(6축 인덱스, 공유 `AppearanceDatabase` 옵션 가리킴) — 두 경로가 각자 방식으로 채워 내놓고 소비 측은 출처 무관.
- **SciFi**: 모델이 곧 외형. 배정기가 `SetModelIndex`로 역할(범인=유지 / 디코이=공개축 일치 모델 `FindModelMatchingRevealed` / 비부합=`PickNonMatchingModel`) 실현.
- **Generic**: 민머리 바디 랜덤 + 프롭 조합. 배정기가 `SetProfile`로 자유 배정(`CreateDecoyProfile`/`CreateNonMatchingProfile`).
- `CitizenIdentity.AssignAppearance`에 항상 실제 반영 프로필 → 스캔·판정·몽타주가 화면과 일치.

### 다음 작업 (우선순위 순, 전부 폴리시/다양성)
1. **머리색 중립베이스 머티리얼** (Generic) — 머리 프롭 기본 텍스처가 어두워 `_BaseColor` 곱셈틴트하면 밝은색(금발·은발·흰)이 탁함. `NpcAppearance`가 머리 프롭 인스턴스화 시 **밝은 중립 머티리얼**을 깔고 틴트하면 선명(렌더로 검증됨). 색 어휘/카탈로그는 이미 선명값 기준.
2. **색 노이즈** `m_colorVariantIndex` (§6-2) — Alts 아틀라스 `접미사 A/B/C=피부톤 × 숫자 01~04=옷배색`. 옷색만 흔드는 비몽타주 노이즈. (Generic 경로)
3. ~~**몽타주 표시 모드** (§7)~~ ✅ 그림 방식으로 확정 (#607). 남은 것은 **SciFi 전용 값 레이어**(§7-1)와 어휘 확장.
4. **v1 정밀화** — 다중 범인 시 디코이 중복부합, SciFi 디코이 일치모델 부족 시 처리.
5. **#222 Faction** (§8).

### 주의점 (gotcha)
- `NpcAppearance.cs` 주석 2곳(`ApplyModel` doc·`HandleModelIndexChanged`)이 옛 mesh-스왑 설명 그대로 — 실제는 바디 토글. 문구만 갱신 필요(기능 무관).
- SciFi 바디는 머리/모자가 **메시에 통짜로 구워짐** → 프롭 못 얹음(그래서 카탈로그 방식). Generic 바디만 프롭.
- Generic 바디 중 **Street_Male·비인간(Robot/Skeleton/Charred)·부착물은 정리로 제외**됨(민머리 옷 13종만). 바디 추가 시 "정수리 민머리 + 옷" 확인 필수.
- `Assets/Imported/Synty/**`는 [별도 저장소]로 팀 공유 — 여기 소스 프리팹/머티리얼은 임의 수정 금지.
- 배정 로직은 **서버 권위** — 실동작 확인은 반드시 **호스트 Play**로.

### ⚠️ 다른 컴퓨터 이어받기 전 — 반드시 커밋
아래 신규/수정 파일이 커밋돼야 다른 환경에서 동작한다 (브랜치 `feature/221-montage-appearance`):
- 신규: `NpcCatalogAppearance.cs`, `AppearanceModelCatalog.cs`+`.asset`, `IAppearanceProfileSource.cs`, `NPC_Citizen_Generic.prefab`, `docs/appearance-montage.md`
- 수정: `AppearanceProfile.cs`, `AppearanceDatabase.cs`+`.asset`, `AppearanceAssigner.cs`, `NpcAppearance.cs`, `NpcSpawner.cs`, `NPC_Citizen.prefab`, `DefaultNetworkPrefabs.asset`, `Main Scene.unity`(스포너 배선)
- (Synty Imported 에셋은 팀 공유 저장소에 이미 있음 — 별도)
