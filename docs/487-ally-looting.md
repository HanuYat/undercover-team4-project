# #487 — 아군 약탈 (쓰러진 동료 털기)

- **날짜:** 2026-08-12
- **브랜치:** `feature/487-looting` (기준: `main` = `feef65f`)
- **커밋 3개:**
  - `ef63eef` 아군 약탈 — 쓰러진 동료의 소지품·개인 자금 빼앗기 (#487)
  - `656a095` 약탈 창 HUD 배선 — Loot 패널 루트 · 슬롯 3칸 (#487)
  - `2cb7d39` GDD 개정 — 아군 약탈 7-5 · 9-2 (#487)
- **상태:** 컴파일 통과(오류 0·신규 파일 경고 0). **핵심 경로 검증 완료** — 원격 클라 양방향 아이템
  약탈 · E 비파괴 · 자금 칸 이전 모두 확인. 잔가지 몇 개만 남았다(6장). PR 미생성.
- **선행 이슈:** [#484 개인 자금](https://github.com/hyunjin0814/undercover-team4-project/issues/484) (완료, `PlayerWallet`)

---

## 1. 한 줄 요약

기능 정지(`Die`)된 동료를 겨냥해 **E**를 누르면 약탈 창이 열린다. **E는 확인만 한다** — 소지품과
개인 자금이 보이고, **칸을 눌러야** 넘어온다. 소지품은 한 칸씩, 자금은 칸 하나에 **전액**이다.
열어 보고 아무것도 안 가져간 채 떠날 수 있다.

---

## 2. ⚠ 이슈 본문이 낡았다 — 먼저 읽을 것

[이슈 #487](https://github.com/hyunjin0814/undercover-team4-project/issues/487)은 지금 코드와 전제가
다르다. **이슈를 그대로 믿고 작업하면 안 된다.**

| 이슈가 말하는 것 | 실제 |
|---|---|
| 대상은 다운(`Down`) 상태 | **`Die`뿐이다.** #524로 `Down` 진입 경로가 끊겼다 |
| "구조(일으키기)와 E 키가 충돌한다 ⚠" | **충돌하지 않는다.** 현장 구조는 휴면(#524)이고 운반은 밧줄 좌클릭(#365)이라 **쓰러진 몸을 겨냥한 E는 비어 있었다** |
| "선행 조건: 개인 자금이 없다" | **있다.** #484로 `PlayerWallet` 완료 |
| "배신 게임으로 만들지 팀 합의 필요 ⚠⚠" | 한 발 들어가 있다 — #485 비밀 청탁이 반영됐고 GDD 9-2가 #487을 명시적으로 예고해 뒀다 |

---

## 3. 확정한 설계와 근거

### 3-1. 이슈에서 **뺀 것** — "동료를 눕힌 대가로 자금 지급"

이슈 "해야 할 일" 1번을 통째로 뺐다. 그게 이슈 스스로 경고한 **무한 증식**의 원인이다(무에서
자금을 발행하면 둘이 번갈아 때려 찍어낼 수 있다). 약탈만 남기면 전부 **이전(transfer)**이라
총량이 늘지 않아 **완료 기준 2번이 설계상 자동 충족**된다. 이슈의 "정해야 하는 것" 1번(이중 보상)도
함께 사라졌다.

### 3-2. 나머지 확정 사항

| 항목 | 확정 | 근거 |
|---|---|---|
| 대상 상태 | **`Die`만** (`Stun` 제외) | 기절까지 털리면 테이저가 최고의 강도 도구가 된다. 조준 히트박스가 `IsOutOfAction`에서만 켜져 배치로도 막힌다 |
| 입력 | **E** | 비어 있던 키(2장). 단 **운반 중엔 내려놓기가 선점**한다 |
| E의 성격 | **확인 전용 · 비파괴** | 열기만으로는 아무것도 안 옮긴다. 훔칠지 말지를 **내용을 보고** 정한다 (§3-3) |
| 자금 | **전액 · 창의 자금 칸 클릭** | 부분 이전이면 "몇 번 더 털기"가 최적 플레이. 버튼을 나눈 것은 *가져갈지* 정하게 하려는 것이지 *금액*을 나누려는 것이 아니다 |
| 인벤토리 꽉 참 | **그 항목만 거부** | 자동 드롭하면 의도 않은 아이템이 바닥에 떨어진다 |
| 보복 루프 | **허용** | 이전이라 총량이 안 는다. 못 되찾으면 털기가 확정 손실이 된다 |
| 이동 | **터는 동안 정지** | 등 뒤가 비는 것이 대가. "창 열고 도망가며 털기"도 원천 차단 |
| 부활 후 | **안 돌려준다** | |

### 3-3. E는 확인용이고 자금은 버튼이다 — 한 번 뒤집힌 부분

> ⚠ **이전 설계를 뒤집은 결정이다** (2026-08-12). 처음에는 **창을 여는 순간 자금이 전액 자동으로**
> 넘어왔다. 그 근거는 "약탈자 클라가 남의 잔액을 읽을 경로가 없으니 보여 줄 수가 없다"였다 —
> `PlayerWallet`의 잔액은 **읽기 권한이 `Owner`**다(#484 — #485/#487의 정보 비대칭이 여기 의존한다).
> **읽기 권한은 지금도 그대로다.** 대신 **서버가 약탈자에게만 금액을 실어 보낸다**(`LootOpenedRpc`) —
> 클라가 스스로 읽는 것이 아니라, 쓰러진 몸에 손이 닿은 사람에게 서버가 한정해서 알려 주는 것이다.
> NetworkVariable 권한 모델은 건드리지 않았다.

**바뀐 것:** E는 이제 **아무것도 옮기지 않는다.** 소지품과 자금을 보여 주기만 하고, 각 칸을 눌러야
넘어온다. 시체를 열어 보고 그냥 떠날 수 있다.

**얻은 것:** 훔칠지 말지가 **선택**이 된다. 자동 이전이던 시절엔 확인과 절도가 한 동작이라, 동료
시체를 살펴보는 것 자체가 이미 배신이었다.

**대가로 생긴 것:** 훔치지 않고 **잔액만 엿보는 것**이 가능해졌다 — 이전엔 알려면 뺏어야 했다.
어차피 뺏을 수 있는 상대이므로 새로 새는 정보는 아니지만, "털지 않고 정보만 얻고 간다"는 수가
생긴 것은 사실이다. 정보 비대칭(GDD 9-2)을 조이려면 여기가 손볼 지점이다.

**표시 금액은 스냅숏이다.** 창이 떠 있는 동안 남이 먼저 털어 갈 수 있고, 그때 눌러도 서버는 실제
잔액(0)만 옮긴다. 창을 믿지 않는다는 §4-3 원칙이 자금에도 그대로 적용된다.

---

## 4. 구조

### 4-1. 파일

| 파일 | 역할 |
|---|---|
| [PlayerLooter.cs](../Assets/Scripts/Player/PlayerLooter.cs) | **터는 쪽.** 요청 → RPC → 서버 검증(`CanLoot`) → 이전 실행 |
| [PlayerLootable.cs](../Assets/Scripts/Player/PlayerLootable.cs) | **털리는 쪽.** `CanBeLooted` · `Loadout`/`Wallet` 접근자 · 피해 알림 |
| [LootableBodyInteractable.cs](../Assets/Scripts/Player/LootableBodyInteractable.cs) | E 진입점. 겨냥당한 `PlayerLootable` + 누른 `PlayerLooter`를 짝지어 준다 |
| [LootPanel.cs](../Assets/Scripts/UI/Panels/LootPanel.cs) | 약탈 창 (`PanelBase`) |
| [LootSlotView.cs](../Assets/Scripts/UI/LootSlotView.cs) | 소지품 칸 하나. 클릭 → 가져가기 요청 |
| [LootFundsView.cs](../Assets/Scripts/UI/LootFundsView.cs) | 개인 자금 칸. 금액 표시 · 클릭 → 전액 가져가기 요청 |
| [HeldItemsWatcher.cs](../Assets/Scripts/Player/HeldItemsWatcher.cs) | 부착 지점 자식 변화를 이벤트로 중계 (§4-4) |
| [PlayerWallet.cs](../Assets/Scripts/Economy/PlayerWallet.cs) | `ServerTransferAllTo` 추가 |
| [PlayerLoadout.cs](../Assets/Scripts/Player/PlayerLoadout.cs) | watcher 부착 + `OnHeldItemsChangedAnyPeer` 공개 |

### 4-2. 왜 터는 쪽/털리는 쪽을 나눴나

[PlayerCarrier](../Assets/Scripts/Player/Escort/PlayerCarrier.cs)는 두 역할을 한 컴포넌트에 두는데,
그 근거는 **짝 상태 공유**다(`CarriedTarget` ↔ `m_carriedBy`, "끌면서 끌려가는" 조합 차단).
**약탈에는 짝 상태가 없어** 그 근거가 성립하지 않는다. 대신 소매치기([Pickpocket](../Assets/Scripts/Events/Pickpocket.cs), #303)가
세운 축을 따랐다 — *행위 주체가 로직을 갖고, 피해자는 목록만 내준다.*

부수 효과로 **알림 방향이 또렷해졌다**: `NotifyOwner`는 그 컴포넌트의 오너에게 가므로,
`PlayerLooter`는 약탈자에게 · `PlayerLootable`은 피해자에게 — "각자 자기 오너에게"가 된다.

### 4-3. 서버에 세션 상태를 두지 않는다

"누가 누구를 털고 있는가"를 서버가 **기억하지 않는다.** 열기·자금·아이템 요청마다 `CanLoot`으로
처음부터 다시 검증한다(#118 요청/실행 분리).

- **약탈 창은 권한이 아니다.** 열려 있다는 사실은 서버에서 아무것도 보장하지 않는다.
- 창을 띄운 채 대상이 부활하거나 멀어지면 **다음 요청이 그냥 거부된다.**
- 창의 자동 닫기는 순전히 UX다 — 닫기가 늦거나 실패해도 규칙이 새지 않는다.
- **창에 뜬 금액도 권한이 아니다.** 스냅숏이라 남이 먼저 털어 가면 어긋나고, 그때 눌러도 서버는
  실제 잔액(0)만 옮긴다.

`CanLoot` 검사 순서: 자기 자신 아님 → 내가 무력화 아님 → 대상 `IsDead` → 사거리+가시선
(`PlayerInteractor.IsWithinReach`). **열기·자금·아이템이 모두 같은 관문을 쓴다** — 나누면 한쪽만
조건이 밀려도 티가 안 난다.

### 4-4. 약탈 창의 갱신 경로 (한 번 갈아엎은 부분)

**남의 소지품 목록은 로컬에서 그냥 읽힌다.** 소지 = 부모 부착이고 부착은 NGO가 복제하므로
`victim.Loadout.CollectDetachableItems()`가 약탈자 클라에서도 같은 답을 낸다. 동기화 RPC가 필요 없다.
(다만 **칸 배치**는 오너 로컬이라 알 수 없어 **부착 순서대로** 채운다)

갱신 신호는 처음에 `Update` 폴링이었다가 **이벤트로 바꿨다.** 후보가 셋이었다:

| 후보 | 판정 |
|---|---|
| `PlayerLoadout.OnSlotsChanged` | ✗ 서버 동기화 RPC(`SendTo.Owner`)에서 나오는 **오너 로컬** 이벤트라 약탈자에게 안 온다 |
| 서버가 약탈자에게 푸시 | ✗ "내가 가져갔다"만 알리면 **둘이 같은 시체를 털 때** 남이 가져간 칸이 남는다. 제대로 하려면 §4-3에서 없앤 세션 상태가 필요하다 |
| **앵커의 `OnTransformChildrenChanged`** | ✅ 채택. 전 피어에서 발생하고 **원인을 가리지 않는다**(내 약탈·남의 약탈·디스폰) |

그래서 `HeldItemsWatcher`를 `PlayerLoadout`이 부착 지점에 런타임으로 붙이고
`OnHeldItemsChangedAnyPeer`로 중계한다. `WorldItemPickup`이 형제 훅(`OnTransformParentChanged`)을
쓰는 선례가 있다.

> ⚠ **두 이벤트를 혼동하지 말 것.** `OnSlotsChanged` = 오너 로컬 · 칸 배치 반영됨 · **자기** 인벤토리용.
> `OnHeldItemsChangedAnyPeer` = 전 피어 · 부착 순서만 · **남의** 소지품 보는 쪽용.

**`Update`는 남아 있다** — 거리 검사 때문이다. 남이 시체를 밧줄로 끌고 가는 것(#365)에는 이벤트가 없다.

### 4-5. 공짜로 얻은 것들

- **배터리 잔량 유지** — `ItemBattery`가 아이템 NetworkObject에 붙은 `Server write / Everyone read`
  NetworkVariable이라 소유권이 바뀌어도 따라온다. 추가 작업 0.
- **라운드 배달(#370)과 무충돌** — 회수는 **소지자 기준**(`HeldItems.DespawnAll`), 배달은 **팀 구매
  목록 기준**(`ShopPurchases`)이라 손이 바뀌어도 서로 무관.
- **피해자 알림** — `PlayerTheftView`(#303 소매치기용)를 그대로 재사용.

---

## 5. 에디터 배선 (이미 적용됨)

### `Assets/Prefabs/Player.prefab` — 루트에 3개

`PlayerLooter` · `PlayerLootable` · `LootableBodyInteractable`. **인스펙터 배선 없음**(전부 `GetComponent`).

> `[RequireComponent(typeof(PlayerLootable))]`은 `LootableBodyInteractable`에 걸려 있다 —
> **그쪽을 붙여야 `PlayerLootable`이 딸려온다.** 반대가 아니다.

### `Assets/Prefabs/UI/HUD.prefab`

```
HUD  ← LootPanel 컴포넌트
└─ Loot                 ← m_panelRoot, 비활성
   ├─ Title             Hud.Loot.Title
   ├─ Slot0/1/2 ─ Icon, Name          ← LootSlotView
   └─ Funds ─ Amount                  ← LootFundsView
```

- **패널 컴포넌트는 HUD 루트에, `m_panelRoot`만 자식** — `SignalInputPanel`과 같은 관례다.
  컴포넌트가 항상 켜진 루트에 있어야 `PanelBase.Awake`의 UI 매니저 등록이 보장된다.
- 슬롯·자금 칸 모두 **배경**이 클릭을 받는다(`Raycast Target` 켬). **자식(Icon·Amount)은 꺼야 한다** —
  켜면 클릭을 가로챈다.
- 지역화 키는 `Hud.Loot.Title`(`7100000000320`) · `Hud.Loot.Funds`(`7100000000321`).
  자금 칸은 `"{문구} {금액}"`으로 **코드에서 이어 붙인다** — 스마트 포맷(`{0}`) 메타데이터를 쓰지 않아
  YAML을 손으로 만질 일이 없다.
- 레이아웃(1920×1080 기준 중앙, 패널 700×**440**, 슬롯 180×180 · 간격 200 · y=20,
  자금 칸 600×90 · y=−130)은 **수치로만 짠 것**이라 눈으로 다듬을 여지가 있다.

---

## 6. 검증 상태

| 항목 | 상태 |
|---|---|
| 컴파일 | ✅ 오류 0 · 신규 7개 파일 경고 0 |
| E → 서버 검증 → 창 열기 | ✅ |
| 소지품 목록 표시 | ✅ |
| 아이템 이전 | ✅ |
| **원격 클라 양방향 (아이템)** | ✅ 호스트↔클라 상호 약탈 통과 — `OpenLootRpc`/`TakeItemRpc`/`LootOpenedRpc` 실제로 돌았다 |
| **E는 비파괴** (열어도 안 넘어옴) | ✅ E 직후 잔액 500/0 그대로 · 이전 로그 없음 |
| **자금 칸 표시** (`LootOpenedRpc`의 금액 · 지역화) | ✅ 원격 클라 화면에 `개인 자금 500` |
| **자금 이전** (자금 칸 클릭) | ✅ `TakeFundsRpc`→`ServerTakeFunds` 500 이전, 잔액 0/500, 피해자 알림 |
| **자금 칸 갱신** (`FundsTakenRpc`) | ✅ 클릭 후 `개인 자금 0`으로 바뀜 |
| ESC → 커서·이동 복귀 | ⚠ 미검증 |
| 슬롯 꽉 참 거부 | ⚠ 미검증 |
| 배터리 잔량 유지 | ⚠ 미검증 |
| 라운드 종료·상점 배달 | ⚠ 미검증 |

> ✅ **원격 양방향은 확인됐다** (2026-08-12). 호스트에서만 돌리면 `HasServerAuthority`가 항상 참이라
> RPC 경로가 한 번도 실행되지 않는데, 호스트↔클라 상호 약탈로 `OpenLootRpc` / `TakeItemRpc` /
> `LootOpenedRpc`가 실제로 도는 것을 봤다.

> ✅ **자금은 설계 변경(§3-3) 뒤에 다시 검증했다** (2026-08-12). E 자동 이전을 원격으로 통과시켜 놓고
> 같은 날 경로를 갈아엎었기 때문에, 옛 검증은 버리고 **새 경로로 처음부터 다시 봤다.** 두 단계로 나눠
> 확인한 것이 중요하다 — **① E만 누른 상태에서 잔액이 그대로**여야 "E는 비파괴"가 증명되고,
> **② 그다음 클릭에서 이전**이 돌아야 자금 칸이 증명된다. 한 번에 하면 둘이 구분되지 않는다.

### 6-1. 자금 검증 재현 절차

잔액이 생기는 경로는 셋뿐이고 셋 다 판을 한 바퀴 돌려야 한다 — 라운드 정산
([SettlementController.cs](../Assets/Scripts/Round/SettlementController.cs)) · 비밀 청탁 보상
([SecretFavorBroker.cs](../Assets/Scripts/HQ/SecretFavorBroker.cs), #485) · 세이브 복원
([PlayerWallet.OnNetworkSpawn](../Assets/Scripts/Economy/PlayerWallet.cs)).

**대신 MCP `execute_code`로 Play 중에 지갑을 채웠다.** `ServerAdd`가 `public`이라 코드를 건드릴 필요가
없고, `execute_code`는 파일을 만들지 않으므로 **디버그 훅이 커밋에 새어 들어갈 여지가 없다.**

```csharp
// 호스트(0번) 지갑에 500 — CodeDom(C# 6)이라 using 없이 전부 네임스페이스를 붙인다 (8장)
var wallets = UnityEngine.Object.FindObjectsByType<PlayerWallet>(UnityEngine.FindObjectsSortMode.None);
foreach (var w in wallets)
    if (w.IsSpawned && w.IsServer && w.OwnerClientId == 0UL) w.ServerAdd(500);
```

**방향이 중요하다 — 호스트가 털리고 원격 클라가 턴다.** 자금이 실린 `LootOpenedRpc`도, 자금을 옮기는
`TakeFundsRpc`/`FundsTakenRpc`도 **약탈자가 원격일 때만** 실행된다(`IsServer && !IsOwner`,
[PlayerLooter.cs](../Assets/Scripts/Player/PlayerLooter.cs)). 반대로 하면 RPC를 통째로 건너뛴다.

그다음 **가상 플레이어가 쓰러진 호스트에 E** → **여기서 한 번 끊는다**(자금 칸에 금액이 뜨는지 보고,
서버 잔액이 아직 그대로인지 확인) → **자금 칸 클릭**.

**확인은 호스트 콘솔 하나로 끝난다** — `NotifyOwner`가 원격 오너에게 RPC를 보내기 *전에* 서버에서도
`Debug.Log`를 찍기 때문이다([ChanneledInteractionBehaviour.cs](../Assets/Scripts/Core/ChanneledInteractionBehaviour.cs)).

```
[개인 자금] 1번 + 500 -> 잔액 500     ← ServerAdd가 이전 로그보다 먼저 나온다(호출 순서)
[개인 자금] 이전 — 0번 → 1번, 500      ← ServerTransferAllTo 실코드 통과
[약탈] 개인 자금을 빼앗겼다 — 500       ← 피해자 알림
```

**자금 칸 표시와 `[약탈] 개인 자금 강탈 — …에게서 500`은 MPPM 창에서 눈으로 봐야 한다** —
가상 플레이어 로그는 MCP로 안 읽힌다. 누른 뒤 자금 칸이 `개인 자금 0`으로 바뀌면 `FundsTakenRpc`가
도착한 것이다.

> ⚠ `ServerAdd`는 **무에서 발행**이라 총량이 는다(§3-1이 금지한 경로다). 테스트 편의로만 쓰고,
> 이 판에서 정산 밸런스를 함께 보지 말 것. 털린 쪽 `RoundEarned`가 줄지 않는 것은 설계대로다.

---

## 7. 남은 작업

1. 6장 미검증 항목 — 남은 것은 **ESC 복귀 · 슬롯 꽉 참 거부 · 배터리 잔량 · 상점 배달**.
   핵심 경로(아이템·자금·E 비파괴·원격 RPC)는 전부 확인됐다
2. ~~`HudTable`에 제목 지역화 항목 + 연결~~ ✅ `Hud.Loot.Title`(ko `약탈` / en `Loot`) ·
   `Hud.Loot.Funds`(ko `개인 자금` / en `Funds`) 추가 후 `HUD.prefab`에 연결
3. 약탈 창 레이아웃 다듬기 — 자금 칸이 붙으면서 패널이 700×440으로 커졌다. 눈으로 볼 것
4. PR 생성
5. **팀 합의 2건** (GDD에 ⚠/⏸로 남겨 둠)
   - 동료를 눕히는 것 자체의 페널티 유무 — 약탈이 이득이 된 이상 **"협동 유지" vs "배신 허용"이
     실제로 갈리는 지점**이다. 별도 이슈 권장
   - 개인 자금 잔액 표시 UI — 소비처(GDD 9-2)가 정해질 때 함께

---

## 8. 다음 세션이 밟을 함정

- **Unity가 켜져 있으면 `dotnet build`가 한 번 실패한다.** 새 `.cs`를 만들면 Unity가 `.meta`를 먼저
  쓰고 `.csproj`를 나중에 갱신해서, `.meta` 등장을 신호로 빌드하면 `CS0246: 타입을 찾을 수 없음`이 난다.
  **몇 초 뒤 재실행하면 통과한다** — 코드 문제가 아니다. (에디터를 한 번 포커스해야 임포트가 돈다)
- **MCP로 UI를 배선했다.** `execute_code`는 **메서드 본문**으로 실행되므로 `using` 지시문을 쓸 수 없다 —
  `UnityEngine.UI.Image`처럼 전부 네임스페이스를 붙일 것. 컴파일러는 CodeDom(**C# 6**)이라 `out var`·튜플 불가.
  배선 스크립트는 멱등하게 짰다(기존 `Loot` 자식과 `LootPanel` 컴포넌트를 지우고 다시 만든다).
- **`GDD 부록 B`는 건드리지 않았다** — 원본 문서 간 *모순* 확정용 표이고, `Down`→`Die` 전환은 이미
  3번 행(#524)에 있어 새로 추가할 모순이 없다.
- **`PlayerLoadout`을 건드렸다** — 코어 클래스다. watcher 부착(`Awake`)과 이벤트 하나가 전부지만,
  이 파일은 지급·회수·줍기·버리기 네 경로가 공유하므로 회귀가 나면 여기부터 볼 것.
