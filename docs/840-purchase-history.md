# #840 구매 내역 · 남은 소모품 표시 — 설계

브랜치: `feature/840-purchase-history`. 구현 전 설계만 담은 문서다.

## 0. 만들려는 것

1. **장비 주문창(카탈로그)에 구매 내역** — 라운드 구분 없이, 세션 동안 팀이 무엇을 몇 개 샀는지.
2. **본부 게시판에 남은 소모품** — 지금 팀에 힐팩·부활 키트가 몇 개 남았는지.

둘은 같은 데이터의 두 얼굴이다("샀다"와 "남았다"). 그래서 데이터 소스를 하나로 두고 뷰를 둘 만든다.

## 1. 현황 — 왜 지금은 볼 수 없나

- 구매품은 **팀 소유**다. `ShopPurchases`(세션 상주 `NetworkedManagerBase`)가 `m_carried`(소지형, 중복 포함 `List<ItemBase>`)와 `m_installables`(설치형 `HashSet<EInstallable>`)를 들고, 매 라운드 `ShopDelivery`가 본부 택배로 다시 지급한다.
- **"남은 것"은 이미 데이터로 있다.** 소모(`ItemBase.ServerConsume`)와 소매치기 분실(`Pickpocket.ServerLoseStolenItem`)이 `RemoveCarried`로 목록에서 빼므로, `m_carried`가 곧 "산 것 중 아직 남은 것"이다.
- **막는 것은 두 가지다.**
  - 두 컬렉션이 **서버 전용이고 네트워크 동기화가 없다** — 클라가 볼 경로가 없다. 클래스 주석이 "클라가 목록을 알 필요가 없다"고 적어 둔 전제를 이 이슈가 뒤집는다.
  - **누적 구매 이력은 어디에도 없다** — `m_carried`는 소모·분실로 줄어들므로 "뭘 샀었는지"를 답하지 못한다. 새 데이터가 필요하다.
- 표시 쪽 선례: 주문창은 `ShopBrowserPanel`(#843)이 이번 라운드 진열만 그린다. 본부 게시판은 `RoundFundBoard`가 씬 3D TMP로 서버 권위 값을 그대로 읽어 그리는 형태다.

## 2. 데이터

### 2-1. 품목 id는 `ShopCatalog` 인덱스로 한다

네트워크로 목록을 보내려면 품목 id가 필요한데, `ShopPurchases`는 지금 `ItemBase` 프리팹 참조만 들고 "품목 id 체계를 갖지 않는다"고 명시해 뒀다. 후보 셋을 봤다.

| 후보 | 장점 | 문제 |
|------|------|------|
| NGO `GlobalObjectIdHash` | 4바이트, NGO가 유일성 보장 | 설치형(`EInstallable`)은 프리팹이 아니라 표현이 갈린다. 아이콘·이름을 따로 찾아야 한다 |
| 프리팹 이름(`FixedString`) | 세이브(`SaveItemLookup`)와 같은 규칙, 사람이 읽기 쉬움 | 대역폭이 크고, 개명에 깨진다 |
| **`ShopCatalog` 인덱스** | **이미 네트워크 계약이다**(`ShopLineup`의 칸이 이 인덱스를 그대로 싣는다). 소지형·설치형을 한 표현으로 덮고, 아이콘·이름·가격이 `Entry`에 이미 있다 | 카탈로그에 없는 품목은 표현 불가 |

**인덱스를 택한다.** 구매는 전부 카탈로그를 거치므로 "카탈로그에 없는 구매"가 없고, 표시에 필요한 아이콘·이름을 뷰가 `ShopCatalog.Entry`에서 바로 읽어 별도 조회 경로를 만들지 않아도 된다. 인덱스가 빌드 사이에 흔들리는 것은 문제되지 않는다 — 이 목록의 수명은 세션이고, 세션은 한 빌드 안에서 끝난다.

**세이브 복원에 역인덱스가 필요하다.** `ShopPurchases.OnNetworkSpawn`은 저장된 프리팹 이름을 `SaveItemLookup`으로 되찾아 `m_carried`를 채운다. 그 자리에서 집계도 같이 세워야 하므로 `ItemBase`/`EInstallable` → 카탈로그 인덱스 역인덱스를 `ShopCatalog`에 메서드로 붙인다(`IndexOf(ItemBase)`, `IndexOf(EInstallable)`, 없으면 −1). 카탈로그에서 빠진 옛 품목은 −1로 걸러 건너뛰고 경고만 남긴다.

### 2-2. `NetworkList<PurchaseTally>` 한 줄에 "샀다"와 "남았다"를 같이 싣는다

```
struct PurchaseTally : INetworkSerializable, IEquatable<PurchaseTally>
    ushort CatalogIndex   // ShopCatalog 인덱스
    ushort Bought         // 세션 누적 구매 개수 — 줄지 않는다
    ushort Remaining      // 지금 남은 개수 — 소모·분실로 줄어든다
```

품목당 한 줄로 합친다(중복 구매를 개수로 접는다). 이렇게 두면 요구 두 개가 같은 줄에서 나온다 — 주문창은 `Bought`를, 본부 게시판은 `Remaining`을 읽는다. 목록 길이가 카탈로그 품목 종류 수로 묶여 진열 재추첨과 무관하게 짧게 유지된다.

설치형은 개수 개념이 없으므로 `Bought = Remaining = 1`로 고정한다. 뷰가 개수를 감출지는 3절에서 정한다.

### 2-3. 서버 변이 지점은 이미 네 곳으로 좁혀져 있다

`ShopPurchases`의 기존 진입점이 그대로 집계의 유일한 변이 지점이 된다. 새 훅을 배선할 곳이 없다는 것이 이 데이터를 `ShopPurchases` 안에 두는 이유다(별도 홀더로 빼면 같은 훅을 두 번 걸고 세이브도 두 곳이 된다).

| 기존 메서드 | 집계 변화 |
|-------------|-----------|
| `AddCarried` | `Bought++`, `Remaining++` (없으면 줄 추가) |
| `RemoveCarried` | `Remaining--` (0 미만으로 내리지 않는다) |
| `AddInstallable` | 줄 추가, `Bought = Remaining = 1` (재구매는 서버가 이미 거부) |
| `Clear` | 목록 비우기 — 라운드 실패로 판이 끝났을 때(`RoundEndResetter`) |

**중복 상태 주의.** `m_carried`/`m_installables`(배달이 쓰는 프리팹 참조)와 집계 목록은 같은 사실의 두 표현이라 어긋날 수 있다. 위 네 메서드 안에서만 둘을 함께 갱신하고, 밖에서 컬렉션을 직접 만지는 경로를 만들지 않는다. 리뷰 때 볼 지점이 여기다.

**라운드 성공 이월은 건드리지 않는다** — 성공하면 집계가 그대로 누적되므로 "라운드 구분 없는 구매 내역"이 자연히 성립한다. 초기화는 라운드 실패 한 경로뿐이고, 그건 팀 자금·구매품이 전부 초기화되는 자리이므로 내역도 같이 비는 것이 맞다.

## 3. UI

두 뷰가 같은 `NetworkList`를 읽는다. 데이터 경로가 하나라 표시가 서로 어긋날 수 없다.

### 3-1. 주문창 구매 내역 — `ShopPurchaseHistoryView`

- `ShopBrowserPanel`(#843) 안, 진열 그리드 **우측 컬럼**에 붙인다. 살지 말지 판단하는 자리에서 "이미 몇 개 샀는지"가 같은 화면에 보이는 것이 이 뷰의 목적이라, 탭으로 감추지 않는다.
- 행 프리팹 `ShopPurchaseRowView` — 아이콘 / 이름 / `구매 n · 남음 m`. 종류 수가 카탈로그 상한을 넘을 수 있으니 `ScrollRect`에 담는다.
- **`ShopOrderSlotView`를 재사용하지 않는다.** 그쪽은 가격·품절·주문 버튼을 든 진열 칸이라 책임이 다르다. 아이콘·이름을 읽는 코드만 겹치므로 `ShopCatalog.Entry`에서 각자 읽는다.
- 창은 권한이 아니다(`ShopBrowserPanel` 주석) — 이 뷰도 표시 전용이고 서버에 아무것도 요청하지 않는다.

### 3-2. 본부 재고 게시판 — `HqStockBoard`

- `RoundFundBoard` 선례를 따라 **씬에 놓는 3D TMP 게시판**으로 만든다. 본부 인원이 CCTV를 보다 눈으로 확인하고 무전으로 알려주는 그림이라, 열고 닫는 패널이 아니라 상시 노출이 맞다.
- 행 `HqStockRowView` — 아이콘 / 이름 / 남은 개수. `Remaining`만 쓴다.
- **무엇을 "소모품"으로 볼 것인가:** `ShopCatalog.Entry.IsStaple`을 재사용한다. `ShopLineup`이 이미 이 표시로 소모형 고정 칸을 뽑고 있어(힐팩·부활 키트) 개념이 이미 있다. 다만 **필드 이름은 `m_staple`("고정 등장 그룹")이고 뜻은 "소모형"이라 어긋나 있다** — 지금은 두 개념이 같은 항목을 가리키므로 그대로 쓰고, 갈리는 날이 오면 그때 카탈로그에 소모형 플래그를 따로 뺀다. 이 선택은 `IsStaple`의 주석에 근거로 남긴다.
- 남은 개수가 0인 줄은 회색으로 남긴다(숨기지 않는다) — "없다"도 본부가 알아야 하는 정보다.

## 4. 파일

**신규**

- `Assets/Scripts/Economy/PurchaseTally.cs` — 동기화 구조체
- `Assets/Scripts/UI/ShopPurchaseHistoryView.cs`, `ShopPurchaseRowView.cs`
- `Assets/Scripts/HQ/HqStockBoard.cs`, `HqStockRowView.cs`
- 행 프리팹 2종(`Assets/Prefabs/UI/`), 게시판 프리팹 1종

**수정**

- `Assets/Scripts/Economy/ShopPurchases.cs` — `NetworkList<PurchaseTally>` 추가, 네 메서드에 집계 갱신, `OnNetworkSpawn` 복원에 집계 세우기. 클래스 주석의 "클라가 목록을 알 필요가 없다" 전제를 갱신한다
- `Assets/Scripts/Data/ShopCatalog.cs` — `IndexOf` 역인덱스 2개, `IsStaple` 주석에 재사용 근거
- `Assets/Prefabs/UI/…` 주문창 프리팹 — 우측 컬럼 배치
- `Shop.unity` / 본부 씬 — 게시판 배치
- `ShopTable`, `HqTable` 지역화 3종(ko/en/shared)

## 5. 지역화 키

| 키 | 테이블 | ko |
|----|--------|-----|
| `Shop.History.Title` | ShopTable | 구매 내역 |
| `Shop.History.Row` | ShopTable | 구매 {0} · 남음 {1} |
| `Shop.History.Empty` | ShopTable | 아직 구매한 장비가 없습니다 |
| `Hq.Stock.Title` | HqTable | 소모품 재고 |
| `Hq.Stock.Row` | HqTable | {0} — {1}개 |

코드가 값을 대입하는 라벨이므로 `LocalizeStringEvent`를 붙이지 않고 `LocalizedString.StringChanged`를 구독한다(#497 관례 — `SessionCodePanel`·`RoundFundBoard`와 같다).

## 6. 범위 밖 (YAGNI)

- **라운드별 분리 집계** — 이슈가 "라운드 구분 하지 않고"라고 못박았다.
- **누가 샀는지 기록** — 구매는 팀 자금으로 하는 팀 행위다. 구매자를 남기면 책임 추궁 기제가 생긴다.
- **개인 인벤토리 표시** — `InventoryBarView` 핫바가 이미 한다.
- **정렬·필터·검색** — 목록이 카탈로그 종류 수로 묶여 짧다.
- **설치형 설치 위치 표시** — 별개 관심사.

## 7. 남은 결정 (씬 작업 시)

- 본부 게시판을 어느 벽에 둘지 — 수배 리스트·라운드 자금 게시판 옆이 자연스럽다.
- 주문창 우측 컬럼 폭 — 진열 그리드를 줄이지 않고 들어가는지 실물로 확인해야 한다.

## 8. 구현 노트 (2026-08-27)

설계대로 구현하면서 세 곳을 조정했다.

- **표시 이름 조회를 `ShopCatalog.Entry.DisplayName`으로 올렸다.** 3-1이 "아이콘·이름은 각자 `Entry`에서
  읽는다"고 했는데, 그대로 하면 같은 폴백 규칙(설치형은 규약 키, 소지형은 `ItemBase.ItemName`)이 세 뷰에
  복사된다. 조회를 `Entry`에 두고 `ShopOrderSlotView`도 그것을 쓰게 바꿨다 — 폴백 문구("품목 없음")는
  여전히 보는 쪽이 정한다.
- **본부 게시판 줄은 카탈로그의 소모품 항목 전부다.** 집계에 줄이 없으면 0개로 표시한다 — 3-2의
  "0인 줄은 숨기지 않는다"를 사기 전에도 성립하게 하려면 목록 기준이 집계가 아니라 카탈로그여야 한다.
  그래서 게시판에는 빈 목록 상태가 없고, 5절의 지역화 키도 그대로다.
- **`IsStaple` 재사용은 접고 `m_consumable` 플래그를 뺐다.** 3-2가 "두 개념이 같은 항목을 가리키니
  그대로 쓰고, 갈리는 날에 따로 뺀다"고 했는데 카탈로그를 보니 이미 갈려 있었다 — 홈런 진압봉이
  `m_staple`인데 소모품이 아니다(`ServerConsume`을 부르는 것은 힐팩·부활 키트뿐). 그대로 두면 "소모품
  재고" 게시판에 진압봉이 올라간다. `ShopCatalog.Entry.IsConsumable`을 새로 두고 게시판이 그것을 보게
  했다. 카탈로그 에셋에는 힐팩·부활 키트만 켰다 — 진열 고정 그룹(`IsStaple`)은 건드리지 않았다.
- **줄 문구는 `LocalizedStrings.Get`으로 읽는다.** 5절이 `LocalizedString.StringChanged`를 지목한 것은
  코드가 값을 대입하는 라벨이기 때문인데, 줄은 인자가 줄마다 다르고 재사용되므로 `WantedEntryView`
  관례(즉시 조회 + 부모가 로케일 변경 때 통째로 다시 그림)를 따랐다. 제목은 설계대로 `StringChanged`다.

세이브 복원분은 누적 구매 이력이 저장되지 않으므로 "남은 것 = 산 것"으로 세운다 (`RestoreFromSave`).

### UI 배치 (2차 — 1차 임시 배치를 되돌린 뒤)

1차에서 창을 넓혀 우측 컬럼을 붙였던 것은 전부 되돌렸다(진열 그리드가 고정 4열이라 폭을 못 줄인 탓에
창을 넓혔는데, 결과가 임시 처리로 보였다). 지금 구조는 이렇다.

- **주문창은 탭 둘이다** — 헤더 아래 `진열` / `구매 내역` 탭(`ShopTabs` 프리팹)을 두고, 구매 내역은
  그리드와 같은 자리를 덮는 뷰(`ShopPurchaseHistory` 프리팹)로 뜬다. 창 크기는 1400×860 그대로다.
  탭이 헤더의 빈 아래쪽(170~188px)에 걸치므로 그리드는 620 → 600으로 20px만 줄었다(2줄 = 596 필요).
  전환은 `ShopBrowserPanel.ShowTab` — 창을 열 때마다 진열 탭에서 시작한다.
- **구매 내역 뷰는 3열 그리드**다(칸 400×52). 배경·테두리는 `ShopOrderSlot`의 스프라이트·색을 그대로
  쓴다. 행은 아이콘 / 이름 / `구매 n · 남음 m`(오른쪽 정렬).
- **본부 게시판은 라운드 자금 게시판과 같은 액자**다 — `RoundFundBoard`의 프레임·스크린 메시를 복사해
  쓰고 그 안에 월드 캔버스(1.82×1.02m)를 넣었다. 위치는 같은 벽, 같은 높이(자금 게시판에서 2.5m 옆).
  행은 아이콘 / 이름 / 오른쪽에 `n개`(`Hq.Stock.Count`) — 한 줄에 서식을 다 넣던 `Hq.Stock.Row`는 뺐다.

지역화 키는 5절에서 둘이 바뀌었다: `Hq.Stock.Row` → `Hq.Stock.Count`("{0}개"), 탭 라벨용
`Shop.Tab.Catalog`("진열") 추가. 구매 내역 탭 라벨은 `Shop.History.Title`을 그대로 쓴다.

Play로 눈으로 보는 확인은 아직 남았다.
