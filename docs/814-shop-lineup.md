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
  - `Assets/Prefabs/Items/Displays.prefab` — 진열대 7개(Table1~7), 각각 NetworkObject+ShopStand+ShopStandView+ShopStandDisplay+메시+MeshCollider 구성
  - 레이어 버그 발견·수정: Table1~7 전부 Default(0)였던 걸 Interactable(7)로 고침 — 안 고쳤으면 조준 자체가 안 됐을 것
  - `Shop.unity`에 `Displays` 프리팹 배치, 기존 구식 진열대 6개 제거 완료
  - `ShopLineup` 오브젝트 생성 + 카탈로그·Table1~7 배선 완료
  - Play 테스트: 추첨 로그 정상, 모델 실제로 올라오는 것 확인

## 막힌 것 — 새 진열대 UI 텍스트 깨짐

새로 만든 7개 진열대의 텍스트만 네모(□)로 깨짐 (기존 UI 텍스트는 정상). 폰트·머티리얼 참조는 파일상 전부 정상(`NotoSansKR-VF SDF` 정확히 참조, 소스 폰트 파일도 존재) — **와이어링 문제 아님.**

가설: Dynamic 아틀라스(`NotoSansKR-VF SDF`)에 진열대 7개가 한 프레임에 스폰되며 처음 보는 한글 글자가 몰려서 일부를 못 그린 것.

**다음에 확인할 것 (순서대로):**
1. 조준 카드를 켰다 껐다 하면(다른 진열대 봤다가 다시) 글자가 정상으로 돌아오는지 — 돌아오면 "그 프레임에 못 그림" 확정, 심각한 문제 아님
2. `Assets/Imported/Fonts/NotoSansKR-VF SDF.asset` 인스펙터 → **Multi Atlas Textures** 체크 → 재시도
3. 그래도면 같은 에셋에서 **Clear Dynamic Data** → 재시도

## 안 한 것

- 구매 응답 지역화(`ReplyRpc` 하드코딩 한국어 문자열) — 범위 밖, 별도 이슈
- `docs/GDD.md` 갱신 (8-3 또는 9-2에 진열 규칙 한 문단)
- 검증 시나리오 전체 재확인 (계획 파일 §검증 목록) — 품절/기보유/동시구매/세이브 연동 등
