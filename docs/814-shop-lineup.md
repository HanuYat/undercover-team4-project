# #814 상점 진열 랜덤화 — 진행 상황

브랜치: `feature/814-shop-lineup`. 상세 설계는 [814-majestic-falcon 계획](../../../../.claude/plans/814-majestic-falcon.md) 참고(로컬 경로 — 팀 공유용 아님).

## 완료

- **스크립트**
  - `Enums.cs` — `EStandStatus` 추가
  - `ShopCatalog.cs`/`.asset` — 판매 후보 명부 (소모형 5칸 + 랜덤 2칸), 항목 6개(힐팩·부활키트·구역스캐너·스턴건·신호해석기·사이렌버튼) 입력 완료
  - `ShopStand.cs` — 익명 슬롯으로 재작성 (`BindCatalog`/`ServerAssign`, 품절 시 모델 숨김)
  - `ShopStandDisplay.cs`, `ShopLineup.cs` — 신규
  - `ShopStandView.cs` — `SetStatus`/`SetEmpty` 추가
  - `ShopPurchases.cs`/`ItemBase.cs` — 주석·죽은 코드 정리
  - `ShopTable` 지역화 3종 — `Purchased`→`Owned` 개명, `SoldOut` 키 추가
- **씬 작업**
  - `Assets/Prefabs/Items/Displays.prefab` — 진열대 7개, 공통 구조는 `DisplayTable.prefab` 서브프리팹으로 추출
  - 레이어 버그 발견·수정: Table1~7 전부 Default(0)였던 걸 Interactable(7)로 고침 — 안 고쳤으면 조준 자체가 안 됐을 것
  - `Shop.unity`에 `Displays` 프리팹 배치, 기존 구식 진열대 6개 제거 완료
  - `ShopLineup` 오브젝트 생성 + 카탈로그·진열대 7개 배선 완료
- **버그 수정**
  - 새 진열대 UI 텍스트가 네모로 깨지던 문제 — 해결 완료(원인: 폰트, 사용자가 직접 조치)
  - 설치형(신호해석기·사이렌버튼) 진열 모델이 안 보이던 문제 — 원인은 카탈로그가 `SignalDecoder.prefab`/`JailSirenButton.prefab` **본체**(NetworkBehaviour 포함)를 가리켜서, `InstallableItem.Awake()`가 항상 미설치 상태로 자기 렌더러·콜라이더를 꺼버렸기 때문. 순수 아트 에셋으로 교체:
    - 신호해석기 → `SM_Prop_ControlPanel_01.prefab`(PolygonSciFiCity)
    - 사이렌버튼 → 신규 `Assets/Prefabs/Items/JailSirenButtonModel.prefab`(구체 메시만, 원래 실물 스케일 0.1/0.4/0.4를 카탈로그 `DisplayScale`로 재현)
- **검증**
  - MPPM 다중 클라이언트 테스트 완료 (늦은 접속자 동일 진열/품절 상태, 설치형 재구매 차단 등)
  - 설치형 중복 진열 방지(`ShopLineup.PickRandomSlots`가 뽑힌 설치형을 풀에서 제거) 재확인 — 라운드당 각 설치형 최대 1개만 뜨는 것 확인됨
- **문서**
  - `docs/GDD.md` 8-3에 진열 재추첨 규칙 한 문단 반영

## 안 한 것 (범위 밖)

- 구매 응답 지역화(`ReplyRpc` 하드코딩 한국어 문자열) — 별도 이슈로 미룸
