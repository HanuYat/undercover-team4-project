# Scanner 정리 · 배터리 분리 · 아이템 공통 기반 승격

> 작성: 2026-08-02 · 브랜치 `refactor/scanner-itembase-cleanup`
> (커밋 `73e5bf2` → `80e7240` → `46f8dc0` → `6310ab5`)
> 판단 기준은 [PlayerLoadout 분리 §2](playerloadout-split.md#2-판단-기준--unity에서-무엇이-위험한가)와 동일.
> 규칙의 정본은 [architecture.md](../architecture.md).

## 1. 한 문장 요약

`Scanner`(515줄)에서 **배터리를 독립 컴포넌트로** 떼고, 5개 클래스에 복붙돼 있던
**오너 판정 피드백**과 4벌이던 **사거리+가시선 판정(#360)** 을 공통 기반으로 모아 360줄로 줄였다.
게임 동작 변화 없음 — 토스트 문구·판정 기준 전부 종전과 동일하다.

## 2. 무엇이 바뀌었나

| 파일 | 이전 | 이후 |
|---|---:|---:|
| `Scanner.cs` | 515 | **360** |
| `ItemBase.cs` | 151 | **136** |
| `ChanneledInteractionBehaviour.cs` | 49 | **89** |
| `ScannerCharger.cs` | 37 | 40 |
| `ItemBattery.cs` | — | **113** (신설) |
| `HandGrip.cs` | — | **11** (신설) |

전체 **16개 파일, +365 / −415**.

### 제거된 중복

| 중복 | 사본 수 | 모은 곳 |
|---|---:|---|
| `NotifyOwner` / `OwnerLogRpc` (주석까지 동일) | 5 | `ChanneledInteractionBehaviour` |
| `IsInRange` — 사거리 + 가시선 (#360) | 4 | `PlayerInteractor.IsWithinReach` |
| `GetComponentInParent<PlayerInteractor>()` | 11 | `ItemBase.Holder` |
| 서버 권위 판정 `!IsSpawned \|\| IsServer` | 5 | `ChanneledInteractionBehaviour.HasServerAuthority` |
| NPC 참조 해석 3항식 (Scanner 내부) | 2 | `Scanner.TryResolveCitizen` |

## 3. 왜 이렇게 했나

### 3-1. 배터리는 `partial`이 아니라 컴포넌트로

`NetworkVariable`은 NGO ILPP가 `NetworkBehaviour`의 필드로만 인식하므로 `ServerChannel` 같은
plain 클래스로는 뺄 수 없다. 남은 선택지는 `partial` 파일과 별도 컴포넌트 둘뿐이었고,
`partial`은 팀 선호가 아니어서 컴포넌트로 갔다.

**역참조 문제와 해법.** 처음엔 "채널링 중 충전 거부"(#60) 때문에 배터리가 Scanner를 알아야 해서
분리 이득이 없다고 봤다. veto 콜백으로 해소된다:

```csharp
// Scanner.Awake — 참조는 Scanner → ItemBattery 한 방향뿐
m_battery.CanCharge = () => !m_channel.IsActive;
m_battery.ChargeBlockedReason = "충전 실패 — 스캔 채널링 중";
m_battery.FullyChargedMessage = "스캐너 배터리 가득 참";
m_battery.OnChargeToast += RaiseOwnerToast;   // 배터리 토스트를 스캐너 토스트 채널로 중계
```

`ItemBattery`는 자기가 어떤 아이템에 붙었는지 모른다.

### 3-2. 오너 피드백은 훅으로

`NotifyOwner(string, bool toast = false)`를 기반에 두고, 토스트를 **어떻게** 띄울지는
`protected virtual RaiseOwnerToast(string)` 훅으로 하위가 정한다.
`Scanner`만 이를 재정의해 `OnScanFeedback`으로 흘리고, 나머지 4곳은 기본(로그만)을 쓴다.
**호출부는 한 줄도 바뀌지 않았다** — `toast` 기본값이 false라서.

### 3-3. `IsWithinReach`는 줄 수가 아니라 규칙 때문

#360(거리만 보면 위조 RPC로 벽 너머 상호작용)은 익스플로잇 방지 규칙인데 사본이 4개였다.
`PlayerCarrier`의 주석은 아예 `(#360, PlayerReviver.IsInRange와 동일)`이라고 적혀 있었다.
복사본이 늘수록 다섯 번째 상호작용에서 가시선 검사를 조용히 빠뜨릴 위험이 커진다.

### 3-4. 하지 않은 것

- **`ItemBase`의 표시 필드 묶기** — `HeldModelPrefab` + 오프셋 + `HandGrip`을 `[Serializable]`
  구조체로 묶으면 직렬화 경로가 바뀌어 아이템 프리팹 6~7개의 값이 전부 날아간다.
  `[FormerlySerializedAs]`는 중첩 이동을 못 살린다.
- **먹통 게이트 분리** — 20줄이고 사용처가 `Scanner` 하나뿐. `SignalDecoder` 등 다른
  전자기기가 먹통을 받게 될 때 하는 게 맞다.
- **`PlayerCarrier.NotifyOwner`** — 이 클래스만 `NetworkBehaviour`를 직접 상속해서
  기반을 못 쓴다. 기반 교체가 타당한지는 따로 판단할 일.
- **주석 대량 삭제** — 이슈 근거 주석은 팀 컨벤션이라 유지. 압축만 했다.

## 4. 부수 정리

- **죽은 TODO 제거** — `ItemBase`의 "취소도 서버 권위로"는 `Scanner.CancelUse` →
  `RequestCancelScanRpc`, `Rope.CancelUse` → `Escorter.CancelCapture`로 이미 구현돼 있었다.
- **죽은 코드 제거** — `PlayerEscorter.AimOriginPosition`은 `IsInRange` 전용이었다.
- **폐기된 참조 정정** — `Handcuffs`는 #369로 밧줄에 이관되며 14줄 스텁이 됐는데
  `FindTarget`·`ResolveTarget`·`ServerCancelActiveUse`·`OnDisable`을 가리키는 주석이 4곳 남아
  있었다(`Scanner`·`Taser`·`PlayerEscorter`·`SignalDecoderHud`). 지금 그 일을 하는
  `Rope`·`Scanner`로 옮겼다.
- **`HandGrip` enum 분리** — 타입명=파일명 관례. int 직렬화라 프리팹 영향 없음.

## 5. 테스트 결과

Multiplayer Play Mode 2인(호스트 + 클라), 조작은 **클라이언트 쪽 플레이어**로 수행.
오너 로그 RPC는 원격 오너일 때만 타는 경로라 호스트 단독으로는 검증되지 않는다.

### 5-1. 스캐너 배터리 — 전부 통과

- [x] 스캔 1회 → 배터리 5→4, HUD 게이지 갱신
- [x] 5회 소진 → "스캐너 배터리 부족" 지속 토스트 + 조준 윤곽선 꺼짐
- [x] 충전기 E → 완충 + "스캐너 충전 완료" 토스트
- [x] 완충 상태에서 E → "스캐너 배터리 가득 참" (값이 안 변해도 발행)
- [x] 스캔 채널링 중 충전기 E → "충전 실패 — 스캔 채널링 중" (`CanCharge` veto)
- [x] 스캐너 버리고 다른 플레이어가 줍기 → 잔량이 따라감 (#88)

### 5-2. 충전기 윤곽선 — 전부 통과

- [x] 스캐너 든 채로 충전기 조준 → 윤곽선 (완충 상태에서도)
- [x] 스캐너 없이 조준 → 윤곽선 없음

### 5-3. 밧줄 — 코드 변경 0줄, 기반 클래스만 바뀜

- [x] 묶기 → 끌기 → E로 놓기 → 다시 끌기
- [x] 밧줄 풀기 채널링 중 좌클릭 뗌 → 취소
- ~~채널링 중 밧줄 버리기~~ — **항목 철회. 밧줄은 원래 버려지지 않는 게 맞다.**
  `PlayerLoadout.IsTetheredRope`가 묶어 둔 대상이 있는 밧줄의 버리기를 막는다 (#369 —
  줄이 손을 떠나면 묶인 NPC가 주인 없이 남는다). 풀기 채널링은 정의상 묶인 상태이므로
  버리기가 막히는 것이 설계대로다. 이 항목은 `ServerCancelActiveUse` 검증에 부적합하다.
- [x] **대체 항목: 스캔 채널링 중 스캐너 버리기 → 채널이 끊긴다** (스캐너는 버리기가
  막히지 않아 이 경로가 실제로 `ServerCancelActiveUse`를 검증한다)

### 5-4. 오너 판정 로그 — 전부 통과

클라 콘솔에 `[서버 판정] …` 도달 확인:

- [x] 스캐너 — 채널링 중 도망 → "스캔 실패 — 대상이 범위를 벗어남"
- [x] 테이저 — 발사 직후 재발사 → "테이저 충전 중 — N초 남음"
- [x] 진압봉 — 허공 스윙 → "진압봉 빗나감 — 허공"
- [x] 소생 — 채널링 시작 / 홀드 뗌 취소
- [x] 밧줄 — 풀기 채널링 시작

### 5-5. 오프라인 — 통과

- [x] 세션 없이 에디터 Play → 스캐너·진압봉·테이저 동작 (`HasServerAuthority`의 `!IsSpawned` 분기)

### 5-6. 테스트를 생략한 것과 근거

| 변경 | 생략 근거 |
|---|---|
| `IsInRange` 4곳 → `IsWithinReach` | 전개하면 원래 식과 완전히 동일 — origin·range·LoS 분기 그대로 |
| `GetComponentInParent<PlayerInteractor>()` 11곳 → `Holder` | 문자 그대로 같은 식, 이름 충돌 없음 |
| `HandGrip` 파일 이동 | enum은 int 직렬화 — 파일 위치와 무관 |
| `SignalDecoderHud` · `ItemBase` XML | 주석만 변경 |

## 6. 프리팹 · 운영 주의

- **Scanner 프리팹에 `ItemBattery` 컴포넌트가 필요하다.** `[RequireComponent]`가 자동 추가하지만
  `Max Battery = 5` 확인이 필요하다. (적용 완료)
- **NetworkBehaviour가 하나 늘었다.** 모든 피어가 같은 빌드를 써야 한다.
- `ItemBase`를 상속한 **Baton·Handcuffs·Rope·Scanner·Taser 전부**가 기반의 새 RPC 2개를
  상속받는다 — 코드를 바꾸지 않은 `Rope`도 네트워크 표면이 바뀌었다(§5-3에서 확인).

## 7. 다음에 배터리 아이템을 만들 때

```csharp
[RequireComponent(typeof(ItemBattery))]
public class 새아이템 : ItemBase
{
    private ItemBattery m_battery;

    private void Awake()
    {
        m_battery = GetComponent<ItemBattery>();
        m_battery.FullyChargedMessage = "○○ 배터리 가득 참";
        m_battery.OnChargeToast += RaiseOwnerToast;   // 토스트를 쓸 거면
        // m_battery.CanCharge = () => !사용중;        // 막을 조건이 있으면
    }

    public override bool CanUse() => !m_battery.IsDepleted;
    // 서버에서 사용이 성공한 지점에서 m_battery.ServerConsume();
}
```

본부 충전기는 그대로 동작한다 — `ScannerCharger`가 장착 아이템에서 `IChargeable`을 찾는
방식이라 타입을 가리지 않는다.

**따라오지 않는 것:**

1. **HUD.** 배터리 게이지·토스트 UI는 `ScanResultPresenter` 안에 `Scanner` 타입으로 묶여 있다.
   새 배터리 아이템은 잔량 표시가 없다 — 그때 배터리 HUD만 별도 프레젠터로 떼야 한다.
2. **소모 호출.** `ServerConsume()`은 본체가 서버 경로에서 직접 불러야 한다.
   `ItemBattery`는 "쓰면 준다"를 강제하지 않는다.
3. **스폰 전 잔량 0.** 초기값을 `OnNetworkSpawn`에서 채우므로 스폰되지 않은 상태에서는
   항상 소진으로 보인다. (기존 `Scanner` 동작을 그대로 옮긴 것)
4. **문구 현지화.** `ChargeBlockedReason`·`FullyChargedMessage`는 raw string이다.
   `ItemBase`의 이름·설명은 `LocalizedString`이므로 현지화 시 여기도 손봐야 한다.

두 번째 배터리 아이템이 실제로 생기는 시점에 **HUD 분리**와 `ScannerCharger` →
`ItemCharger` **개명**을 함께 하는 것이 자연스럽다.
