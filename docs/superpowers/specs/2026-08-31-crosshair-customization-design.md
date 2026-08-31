# 크로스헤어 커스터마이징 (#945)

> 작성 2026-08-31. 지금 임시 흰 네모 크로스헤어를 모양·색·크기·굵기를 직접 고를 수 있는 UI로 바꾸고, 계정 클라우드에 저장해 다른 PC에서 로그인해도 따라오게 한다. 진입점은 설정 창 Look 탭의 버튼.

## 1. 지금 상태

- [`CrosshairUI`](../../../Assets/Scripts/UI/Hud/CrosshairUI.cs)의 크로스헤어는 스프라이트 없는 15×15 흰 `Image` 하나뿐이다(`Assets/Prefabs/UI/HUD.prefab`) — 모양 선택지가 없고, 굵기·크기를 조절할 방법 자체가 없다.
- 색은 이미 3단계로 바뀐다: 기본(흰색) / 상호작용 대상 조준 시(`m_interactableColor`, 노랑) / 무기 조준 시 명중 가능(`m_weaponTargetColor`, 빨강). 매 프레임 `InteractionFeedback.Refresh()`가 판정해 호출한다.
- **GDD 제약(568행)**: "동료를 겨누면 크로스헤어가 명중 색으로 켜진다"가 명시된 안전 신호다 — 오검거·오사 방지 목적이라 이 색 전환 로직은 커스터마이징과 무관하게 그대로 유지해야 한다.
- 설정 저장은 두 갈래다:
  - [`GameSettings`](../../../Assets/Scripts/Core/GameSettings.cs) — 감도·FOV 등, PC 로컬(`PlayerPrefs`)에만 저장. 계정별로 갈리지만 다른 PC로는 안 따라간다.
  - [`CosmeticLoadout`](../../../Assets/Scripts/Save/CosmeticLoadout.cs) + [`CosmeticsSaveService`](../../../Assets/Scripts/Save/CosmeticsSaveService.cs) — 로봇 색·액세서리처럼 계정 클라우드(Cloud Save)에 저장, 어느 PC에서 로그인해도 따라온다. 버전 관리·디바운스 저장까지 갖춘 기존 경로.
  - 크로스헤어는 "다른 PC에서도 따라와야 한다"(오버워치/발로란트 기준)는 요구라 **후자**를 쓴다.

## 2. 데이터 모델

```
ECrosshairShape { Cross, Dot, CrossDot, Circle }   // 프리셋 4종
CrosshairSettings                                   // 값 하나로 묶어 저장·전달
  - Shape: ECrosshairShape
  - Color: Color32 (기본 상태 색만 — 아래 §4 참고)
  - Size: float   (선 길이 / 원 지름 기준)
  - Thickness: float (선 굵기)
```

값 범위(Size·Thickness)는 슬라이더 min/max로만 제한한다 — 별도 ScriptableObject 테이블은 과함(GDD 10-4 대상은 콘텐츠 데이터에 한정, 이런 단순 슬라이더 범위는 필드 상수로 충분).

## 3. 저장 경로 — CosmeticsSaveData 확장

기존 액세서리를 추가했던 방식(#818, `docs/superpowers/specs/2026-08-24-cosmetic-accessories-design.md`)을 그대로 반복한다:

1. `CosmeticLoadout`에 `Get/SetCrosshairSettings()` + `OnCrosshairSettingsChanged` 이벤트 추가. 로컬 캐시는 `PlayerPrefs`에 `settings.crosshair.<account>.*` 키로 저장(다른 캐시들과 같은 규약).
2. `CosmeticsSaveData`에 `Crosshair` 필드 추가, `k_version` +1. **마이그레이션 필수** — 새 버전을 읽었는데 필드가 없던 옛 레코드는 기본 크로스헤어 값으로 채운다(§5 "기존 사용자 값 유실" 사고를 반복하지 않는다).
3. `CosmeticsSaveService`의 기존 `Hook()`/`QueueSave()` 디바운스 경로에 `OnCrosshairSettingsChanged` 구독만 추가하면 저장 경로는 끝 — 새 서비스를 만들지 않는다.

## 4. 렌더링 — CrosshairUI 확장

지금의 단일 `Image`로는 "굵기·크기"를 조절할 수 없다 — 위/아래/좌/우 4개의 선(Image)과 가운데 점(Image), 원(Image, 기존 원형 스프라이트 재사용)을 각각 두고 모양에 따라 켜고 끄는 조합형으로 바꾼다(오버워치 크로스헤어 편집기와 같은 구조).

- `Size`/`Thickness`는 각 선분 Image의 `RectTransform.sizeDelta`(길이/두께)에 직접 반영한다 — 값이 바뀌면 그 자리에서 다시 그린다.
- **색 처리가 핵심 제약이다**: `m_defaultColor`(지금은 고정 흰색 인스펙터 값)를 "플레이어가 고른 색"으로 바꿔치기하고, `m_interactableColor`(노랑)·`m_weaponTargetColor`(빨강)는 **손대지 않는다**. 즉 커스텀 색은 중립 상태에서만 보이고, 안전 신호가 뜨는 순간엔 항상 같은 고정색으로 덮인다 — GDD 568행의 신호를 깨지 않는다.
- 모양·크기·굵기는 안전 신호와 무관하므로 어느 상태에서든 플레이어가 고른 대로 유지된다(색만 상태별로 바뀐다).
- 히트마커·킬 컨펌 라벨(`m_hitMarker`, `m_killLabel`)은 이번 범위 밖 — 건드리지 않는다.

## 5. 설정 UI — Look 탭 버튼 → 전용 패널

- `SettingsPanel`의 `LookPage`에 "크로스헤어 설정" 버튼 하나만 추가한다 — Look 탭 자체에 슬라이더 4개를 욱여넣지 않는다(항목이 늘어나도 탭이 안 늘어남).
- 버튼을 누르면 `PlayerColorPanel`과 같은 구조의 별도 패널(`CrosshairSettingsPanel` 신규, `PanelBase` 상속)이 뜬다:
  - 모양 4종 선택(버튼/토글 그룹)
  - 색상 선택(기존 색 스와치 그리드 또는 컬러 피커 — `PlayerColorPickerView`와 같은 배선 재사용 검토)
  - 크기·굵기 슬라이더 2개(`SettingsPanel`의 `SetupSlider` 관례와 동일)
  - 실시간 미리보기 — 실제 `CrosshairUI` 프리팹 인스턴스를 그대로 띄워 슬라이더를 움직이는 즉시 반영(별도 미리보기 렌더 파이프라인을 새로 만들지 않는다)
- 값은 슬라이더/버튼 변경 즉시 `CosmeticLoadout.SetCrosshairSettings(...)` 호출 → `CrosshairUI`가 이벤트로 받아 즉시 갱신 → 클라우드 저장은 기존 디바운스가 처리(설정 창의 "즉시 적용, 저장/취소 없음" 관례와 동일, `docs/design/settings-ui.md`).

## 6. 범위 밖

- 선 간격(gap) 조절, 아웃라인, 투명도 등 세부 옵션 — 이번엔 모양·색·크기·굵기 4개만.
- 다른 플레이어에게 내 크로스헤어가 보이는 기능 — 원래도 로컬 전용 HUD라 해당 없음(로봇 색과 달리 네트워크 동기화 대상이 아님).
- 프리셋 저장/공유(코드 붙여넣기 등) — 발로란트식 프로필 공유는 다루지 않는다.

## 7. 검증

테스트 프레임워크 코드가 없는 프로젝트라 수동이다.

**핵심 확인 사항**

1. 모양 4종 + 색상 + 크기/굵기 슬라이더를 바꿔가며 미리보기와 실제 게임 화면 크로스헤어가 즉시 일치하는지
2. 상호작용 대상·무기 조준 시 여전히 노랑/빨강으로 정확히 전환되는지(커스텀 색이 안전 신호를 가리지 않는지)

**회귀 지점**

- 로그아웃 후 다른 계정으로 로그인 시 크로스헤어가 그 계정 값으로 바뀌는지, 첫 로그인 계정은 기본값으로 나오는지
- `k_version` 마이그레이션 — 이번 버전 이전에 저장된 계정(로봇 색만 있는 레코드)을 불러왔을 때 크로스헤어가 기본값으로 채워지고 기존 값이 유실되지 않는지
