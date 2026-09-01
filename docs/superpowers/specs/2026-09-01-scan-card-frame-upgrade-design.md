# 스캔 카드 프레임·노이즈 마감 (#942)

> 작성 2026-09-01. 노이즈 오버레이를 붙인 뒤에도 카드가 밋밋해서 마감을 다시 잡았다. 결과는 **금색 테두리 + 검정 안쪽 + 잔노이즈**, 물음표는 제거. 코드 변경은 주석뿐이고 나머지는 프리팹·텍스처 작업이다.
>
> 검토 과정에서 Layer Lab GUI 팩의 팔각(cut-corner) 프레임 스프라이트를 써봤으나 **폐기**했다 — 카드가 팔각으로 잘려 사각 노이즈와 맞지 않았다. 최종안은 스프라이트 없이 단색 Image 두 장으로 사각 테두리를 만든다.

## 1. 구조

`ScanInfoCard.prefab` 루트(월드스페이스 Canvas, `localScale 0.0048`, 280x68) 아래:

```
ScanInfoCard
├─ Frame        ← 금색 단색 Image, sizeDelta (+6, +6)
└─ Background   ← 카드 안쪽 단색 Image (상태에 따라 색 스위칭)
   ├─ Content     ← 이름·아이콘 (스캔 완료 시 표시)
   └─ MaskedRoot  ← 미스캔 시 표시
      └─ NoiseOverlay
```

`Frame`이 `Background`보다 **앞 형제**라서 뒤에 깔리고, 사방 3px씩 커서 테두리 링만 삐져나와 보인다. 9-slice 스프라이트도 `Mask` 컴포넌트도 쓰지 않는다 — 둘 다 사각형 풀스트레치라 노이즈가 배경 밖으로 새어나갈 구조가 아니다.

## 2. 값

| 대상 | 값 |
|---|---|
| `Frame` Image | 스프라이트 없음, 색 `(0.878, 0.663, 0.29, a=1)` (금색), Raycast Target 끔 |
| `Frame` RectTransform | 앵커 풀스트레치, `sizeDelta (6, 6)` → 테두리 두께 3px |
| `m_maskedColor` | `(0.02, 0.02, 0.024, a=0.92)` — 거의 검정. 알파를 높여 뒤 배경이 비쳐 탁해지는 것을 막는다 |
| `m_scannedColor` | `(0, 0.05, 0.08, a=0.72)` — 기존 유지 |
| `NoiseOverlay` Image | `ScanCardNoise.png`, Type **Tiled**, `PixelsPerUnitMultiplier 1`, 틴트 `(1,1,1, a=0.9)` |

## 3. 노이즈

첫 시도가 어색했던 원인은 텍스처가 아니라 **렌더 방식**이었다. 128px 텍스처를 카드 전체로 `Simple`(늘리기) 렌더 + bilinear 보간이라 알갱이 하나가 화면상 4px짜리 뭉갠 얼룩이 됐다.

- **텍스처 재생성** — `Assets/Textures/UI/ScanCardNoise.png`, 64x64. 알파는 낮은 평균(≈38/255)에 완만한 분산, 2% 확률로 밝은 알갱이가 튀게 한다. 색은 거의 흰색으로 고정 — 이전처럼 색·알파가 둘 다 크게 튀면 흙먼지처럼 보인다. GUID는 유지해 프리팹 참조를 그대로 쓴다.
- **Tiled 렌더** — 텍스처 1px = UI 1단위로 반복. 늘리기가 아니라 타일링이라 알갱이가 보존된다.
- **임포트 설정** — Filter `Point`(알갱이 뭉개짐 방지), Wrap `Repeat`(타일링 필수), Mipmap **켬**(멀어질 때 지직거림 억제).

## 4. 물음표 제거

`MaskedRoot` 아래 `MaskedText`(큰 `?` TMP)를 프리팹에서 삭제했다. `ScanInfoView`는 `m_maskedRoot`(컨테이너)만 참조하므로 코드 배선 변경은 없고, 물음표를 언급하던 주석 2곳만 정리했다.

## 5. NPC별 카드 크기 불일치 수정

`NPC_Citizen.prefab`이 카드 루트 RectTransform의 `m_SizeDelta`를 `280x100`으로 **오버라이드**하고 있었다. 원래 프리팹 값과 같아서 그동안 티가 안 났는데, 이 브랜치에서 프리팹을 `280x68`로 줄이자 오버라이드된 속성은 전파되지 않아 NPC_Citizen만 100으로 남았다.

→ 해당 `m_SizeDelta.x/y` 오버라이드 2개만 제거. 나머지 오버라이드(피벗·앵커·위치)는 프리팹 값과 동일해 그대로 뒀다. 이제 NPC 4종(`Citizen` / `Citizen_Generic` / `Streaker` / `StreetThug`) 모두 `280x68`.

## 6. 범위 밖

- `NPC_Abductor` · `NPC_FactionRevenge`에는 스캔 카드가 아예 없다 — 조준 시 `ScanResultPresenter`가 경고를 찍는다. 별건이라 이번엔 손대지 않는다.
- 스캔 진행도에 따른 노이즈 페이드 애니메이션 — 정적 오버레이 1장으로 유지.
- `ScanResultPresenter`의 마스킹 판정 로직 — 변경 없음.

## 7. 검증

테스트 프레임워크 코드가 없어 수동이다.

1. 미스캔 카드: 금색 테두리 안쪽이 검정으로 차고, 잔노이즈가 알갱이로 보이는지(뭉갠 얼룩 아님). 물음표 없음.
2. 스캔 완료 카드: 같은 금색 테두리 유지, 안쪽만 `m_scannedColor`로 바뀌고 이름·아이콘이 안 가려지는지.
3. NPC 종류를 바꿔가며 조준했을 때 카드 크기가 모두 같은지.
4. 멀리서 볼 때 노이즈가 지직거리지 않는지(밉맵 동작 확인).
