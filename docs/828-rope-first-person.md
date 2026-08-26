# #828 — 밧줄(LineRenderer) 1인칭 시점 정렬

## 증상과 실제 원인

이슈 본문은 "밧줄이 1인칭 카메라 기준으로 그려지고 있으니 3인칭 모델 기준으로 바꾸자"고 적혀 있었지만,
코드를 보면 `RopeDragView.HandPoint`는 애초부터 피어를 가리지 않고 항상 **3인칭 리그**의
`PlayerHeldItemView.HandAnchor`(`Root/.../Hand_R/HeldItemAnchor`)를 시작점으로 썼다. 그게 원인이었다.

`PlayerLook.ApplyOwnerView`는 오너 화면에서 자기 3인칭 리그를 `OwnBody` 레이어로 옮겨 컬링한다. 그래서
오너 눈에는 그 손이 안 보이는데, 밧줄만 계속 그 손(대략 카메라 아래·뒤)에서 뻗어 나왔다 — 실제로 보이는
1인칭 팔(`FPArm_Right`)과는 무관한 자리다.

## 왜 "1인칭 손 앵커의 월드 좌표를 그냥 쓰면" 안 되는가

오너가 보는 1인칭 팔과, 밧줄 같은 월드 표현은 **서로 다른 카메라**가 그린다.

| 카메라 | 그리는 것 | 레이어 | FOV |
|---|---|---|---|
| `Camera` (Base) | 월드 전체(밧줄 `LineRenderer` 포함, Default 레이어) | Viewmodel 제외 | `GameSettings.Fov` — 기본 **70**, 설정 범위 60~100 |
| `ViewmodelCamera` (Overlay, 자식) | FP 팔·든 아이템(Viewmodel 레이어) | Viewmodel만 | **60 고정** (프리팹 하드코딩) |

두 카메라는 트랜스폼이 완전히 같지만(부모-자식) **FOV가 다르다.** 같은 월드 점이라도 두 카메라에서 화면
위치가 어긋난다는 뜻이다.

### 어긋남 계산

FP 손 앵커(`HeldItemAnchor`)는 카메라 로컬로 대략 `(0.45, -0.25, 0.55)` 부근에 있다(팔 오프셋
`(0.4, -0.22, 0)` + 팔 내부 앵커 오프셋). 이 점을 각 카메라 뷰포트로 투영하면:

- FOV 60(`ViewmodelCamera`, 실제 화면): 뷰포트 `(0.90, 0.11)` 부근
- FOV 70(`Camera`, `GameSettings` 기본값): 뷰포트 `(0.83, 0.18)` 부근

**기본 설정에서 이미 화면 폭·높이의 약 7%가 어긋난다.** 사용자가 설정 창에서 FOV를 100까지 올리면
격차는 더 벌어진다. 즉 "손의 월드 좌표를 그대로 밧줄 시작점에 넣는" 방식은 FOV가 우연히 같을 때만
맞고, 이 프로젝트는 FOV가 애초에 사용자 설정값이라 항상 어긋난다.

## 해법 — 뷰포트로 갈아타서 카메라 차이를 흡수

`PlayerHandView.TryGetHandWorldPoint(depth, out point)`가 하는 일:

```
vp    = ViewmodelCamera.WorldToViewportPoint(handAnchor.position);
vp.z  = depth > 0 ? depth : vp.z;
point = Camera.ViewportToWorldPoint(vp);
```

화면에서 보이는 `(x, y)` 위치는 손 그대로 유지하고, 카메라로부터의 깊이(z)만 새로 정한다. 두 카메라가
어떤 FOV 조합이든 화면 위치가 항상 손에 붙는다 — FOV가 설정 창에서 바뀌어도(60~100 어느 값이든)
매 프레임 재계산되므로 깨지지 않는다.

`depth`를 인스펙터 노브(`RopeDragView.m_fpStartDepth`, 기본 0.6f)로 둔 이유는 손 자체의 깊이(카메라에서
~0.5m)를 그대로 쓰면 밧줄이 손 바로 앞에서 시작해 사물을 뚫고 지나가 보이기 쉬워서다. 살짝 더 앞으로
밀어 두는 편이 자연스럽다.

화면 가로 위치 자체를 옮기고 싶으면(밧줄이 손이나 총열을 가려 보일 때) `m_fpStartOffsetX`(뷰포트
비율, 기본 0)를 쓴다. `viewport.x`에 그대로 더해지는 값이라 음수면 왼쪽, 양수면 오른쪽으로 밀린다 —
`depth`와 달리 이 값은 손의 실제 화면 위치에서 <b>벗어나는</b> 보정이므로(손을 따라다니지 않고 화면에
고정된 오프셋) 크게 주면 밧줄이 손에서 눈에 띄게 떨어져 보인다. 0.01~0.03 정도의 작은 값으로 시작할 것.

## 굵기 테이퍼링

1인칭 시작점은 카메라에서 ~0.5m로 가깝다. 밧줄의 정상 굵기(`m_ropeWidth`, 기본 0.035m)를 그대로
두면 그 근접 거리 때문에 화면에서 두꺼운 띠처럼 잡힌다. `RopeDragView.m_fpStartWidth`(기본 0.012m)로
시작점만 가늘게 잡고, `LineRenderer.widthCurve`로 첫 25%(`k_fpTaperFraction`) 구간에서 정상 굵기로
빠르게 되돌아오게 한다. 3인칭 시작점(다른 피어가 보는 화면)은 이 근접 문제가 없으므로 항상 평평한
굵기를 쓴다.

## 두 카메라를 코드에서 구분하는 법

`PlayerLook.m_playerCamera`는 인스펙터에 직접 꽂힌 참조다 — 씬에 카메라가 두 대(`Camera`,
`ViewmodelCamera`) 있어 `GetComponentInChildren<Camera>()`만으로는 어느 쪽을 잡을지 모호하기
때문이다. `PlayerHandView.CacheCameras()`는 새 인스펙터 필드를 프리팹에 추가하는 대신(`Player.prefab`은
1만 줄이 넘는 공용 파일이라 손대면 충돌이 잦다), 각 카메라의 **컬링 마스크에 Viewmodel 레이어 비트가
섰는지**로 런타임에 가른다 — `ViewmodelCamera`는 그 비트만 켜져 있고(`m_CullingMask` 값 256),
`Camera`는 꺼져 있다(695). 씬 계층 순서에 기대지 않는 방식이라 프리팹을 재구성해도 깨지지 않는다.

## 폴백 조건

`TryGetHandWorldPoint`가 `false`를 반환해 3인칭 손 앵커로 폴백해야 하는 경우:

- 비오너 (`PlayerHandView.OnNetworkSpawn`이 비오너를 `enabled = false`로 끈다)
- FP 팔이 없거나 앵커를 못 찾은 구성(테스트 씬 등)
- 감정표현(#219)·사망 관전(#576)으로 3인칭에 빠진 상태 — `PlayerLook.ApplyThirdPersonView`가
  `SetViewmodelVisible(false)`로 FP 팔(`m_handsModel`)을 끄므로, 그 `activeInHierarchy`를 그대로
  "지금은 3인칭"의 신호로 재사용한다. 별도 상태를 만들 필요가 없었다.

## 건드리지 않은 것

- `PlayerHeldItemView.ResolveRopeAnchor`(밧줄의 **물리** 앵커 = 운반자 루트, 견인 발산 대응 #571)는
  그대로다. 이번 변경은 **보이는 줄**만 건드린다.
- 매듭점(NPC/시체 쪽 끝)은 항상 3인칭 몸 기준 — 그쪽은 어긋날 이유가 없다.
- 네트워크 동기화 없음. 밧줄 선은 원래부터 순수 로컬 연출이라 각 피어가 스스로 그린다(#269).

## 알려진 한계

이 해법은 로컬에 밧줄을 그리는 카메라가 하나(메인 카메라)라는 전제 위에 있다. 실제로 그렇다 — CCTV
카메라는 HQ 모니터를 켤 때만 활성화되고(`CCTVSwitcher`), 미니맵은 라이브 카메라가 아니라 베이크된
항공 텍스처를 쓴다. 나중에 로컬에서 월드를 그리는 카메라가 하나 더 생기면(예: 실시간 미러 카메라),
그 카메라에는 밧줄 시작점이 엉뚱한 자리(내 1인칭 카메라 앞 `m_fpStartDepth`m)에 찍힌다. 그때는 선을
두 벌로 나누는 게 해법이다 — 3인칭용은 지금처럼 `OwnBody` 레이어에, 1인칭용은 오너 전용 레이어에
그려 각 카메라 컬링 마스크로 갈라야 한다. 지금은 카메라가 하나뿐이라 과잉이다.
