# 스캔 카드 미스캔 비주얼 교체 (#942)

> 작성 2026-09-01. 미스캔 대상의 스캔 카드가 완전 불투명 검정 배경에 흰 `?` 하나만 떠 있어 어색하다. 반투명 배경 + 얇은 카드 + 정적 노이즈 오버레이로 "신호가 약한 미확인 대상" 느낌을 준다. 코드 변경 없이 프리팹 + 신규 노이즈 텍스처 1장으로 끝낸다.

## 1. 지금 상태

- [`ScanInfoView.cs`](../../../Assets/Scripts/UI/ScanInfoView.cs)의 `SetMasked(bool masked)`가 `m_background.color`를 `m_maskedColor`(현재 `Color.black`, 완전 불투명) / `m_scannedColor`(`(0, 0.05, 0.08, 0.72)`, 반투명 다크틸)로 스위칭하고, `m_maskedRoot`(`?` 텍스트)와 `m_contentRoot`(이름·아이콘)를 서로 반대로 토글한다.
- [`ScanResultPresenter.cs`](../../../Assets/Scripts/UI/ScanResultPresenter.cs)가 로컬 `m_scannedNpcIds`에 없는 NPC를 볼 때마다 `ShowMasked()`를 호출 — 이 판정 로직은 이번 범위 밖, 손대지 않는다.
- [`ScanInfoCard.prefab`](../../../Assets/Prefabs/NPC/ScanInfoCard.prefab): `Background` Image(스프라이트 없음, 단색 채움, 280×100), `MaskedText` TMP(`?`, 96pt, 흰색), NPC 머리 위 월드스페이스 캔버스(`localScale 0.0048`).
- 참고 패턴: [`MontagePortraitView.cs`](../../../Assets/Scripts/HQ/WantedList/MontagePortraitView.cs)의 `m_unknownTint`(반투명 회색 톤)가 이미 같은 문제("미확인 상태를 불투명 검정 대신 반투명으로")를 몽타주 쪽에서 풀어놓은 선례다. 다만 그쪽의 `MontageDegrader` 단계적 픽셀 페이드는 스캔 진행도와 연결된 별도 배선이 필요해 이번엔 가져오지 않는다(§4 범위 밖).

## 2. 비주얼 변경값

- `m_maskedColor`: `(0,0,0,1)` → `(0.08, 0.09, 0.11, 0.55)`. `m_scannedColor`와 같은 다크 계열 톤을 유지하면서 반투명화해 "미확인"과 "스캔 완료"가 색만으로도 구분되게 한다.
- 카드 크기: 280×100 → 280×68. 폭은 유지(NPC 머리 위 정렬 어긋남 방지), 높이만 줄여 "얇은 느낌".
- `MaskedText`(`?`): 96pt → 64pt(높이 축소 비율대로), 알파 100% → 75%로 낮춰 덜 튀게.

## 3. 노이즈 오버레이

- **에셋**: `Assets/Textures/UI/ScanCardNoise.png` — 그레이스케일 + 알파 채널에 진짜 랜덤 픽셀값을 넣은 정적 노이즈 텍스처(AI 생성 이미지가 아니라 픽셀 단위 랜덤 생성 스크립트로 만든다 — 스타일화된 그림이 아니라 "노이즈"여야 함). 크기는 128×128 정도, 카드에 맞춰 늘려 채우므로 타일링 이음새를 신경 쓸 필요 없다(Image Type = Simple).
- Sprite 임포트 설정: Texture Type `Sprite (2D and UI)`, Filter Mode `Bilinear`, Wrap Mode `Clamp`(타일 안 하므로).
- 프리팹 배선: `ScanInfoCard.prefab`의 `m_maskedRoot` 밑에 `NoiseOverlay`라는 자식 `Image`를 새로 추가하고 `MaskedText`보다 앞 형제로 둔다(그려지는 순서상 노이즈가 배경 위, 텍스트 아래). 알파는 Image 자체 tint에서 0.15~0.2로 낮춘다.
- `m_maskedRoot`는 이미 `SetMasked()`가 켜고 끄므로, `NoiseOverlay`를 그 밑에 두면 **`ScanInfoView.cs` 코드 수정 없이** 스캔 여부에 따라 자동으로 나타났다 사라진다.

## 4. 범위 밖

- `MontageDegrader` 식 단계적 픽셀 페이드/시간 경과 애니메이션 — 이번엔 정적 오버레이 1장만(간단한 쪽으로 확정).
- `ScanResultPresenter`의 마스킹 판정 로직(누가 스캔됐다고 볼지) — 변경 없음.
- 몽타주/수배자 목록 UI(`MontagePortraitView` 등) — 별도 시스템, 손대지 않는다.

## 5. 검증

테스트 프레임워크 코드가 없는 프로젝트라 수동이다.

**핵심 확인 사항**

1. 스캔 전 NPC 옆에서 카드가 얇고 반투명 + 노이즈로 보이는지, `?`가 과하게 튀지 않는지.
2. 스캔 완료 시 기존 `m_scannedColor` 배경 + 이름/아이콘 표시로 정상 전환되는지(마스킹 해제 경로 회귀 없는지).
3. 여러 NPC를 동시에 볼 때(카메라에 여러 카드가 겹치는 상황) 노이즈 텍스처가 반복 인스턴스에서도 무겁지 않은지(같은 Sprite 재사용이라 드로우콜 영향 없어야 함).
