# #816 거대 뿅망치 — 확정 근거 · 배선 현황 · 테스트 항목

## Context

[이슈 #816](https://github.com/hyunjin0814/undercover-team4-project/issues/816)은 "평타 데미지 1, 낮은 확률로 즉시 무력화"되는 개그 근접 아이템을 요구하면서 **"즉사"가 죽이는 것인지 기절시키는 것인지 팀 확인이 필요하다**고 열어 뒀다. 이번 작업에서 그 갈래를 확정했다(2026-08-24).

## 확정 결정

| 항목 | 결정 | 근거 |
|---|---|---|
| "즉사"의 의미 | **NPC는 실제 사망** | `NpcHealth.SetHp`는 체력 **0 도달**을 사망(`NpcDeath.ServerEnterDead`)으로 처리하고, 임계 비율 **하향 교차**만 기절로 본다. 9999는 둘 다 만족시키지만 그 함수가 사망을 먼저 보므로 결과는 사망 하나다. 죽여도 라운드가 막히지 않는다 — 시체도 산 신병과 같은 유치장 문에서 판정된다(`NpcDeath` ⑦ 주석, `ArrestJudge.JudgeCorpse`) |
| 동료(아군 오사) | **적용된다 — 단, 진압봉과 같은 규칙** | `PlayerHealth.TakeDamage`를 그대로 탄다. 건강한 동료가 맞으면 **다운(60초 유예, `IncapacitationCause.Down`)**에 먼저 들어간다 — HP를 0으로 떨어뜨리는 첫 타격은 9999든 1이든 결과가 같다. **이미 다운된 동료**를 맞히면 확인사살(`PlayerIncapacitation.ServerFinishOff`)로 그 자리에서 완전 사망한다. "봐주지 않는다"는 특별 취급을 하지 않는다는 뜻이지, 대박이 동료의 유예를 건너뛴다는 뜻이 아니다 — ⚠ 이 구분은 작업 중 재확인한 것으로, 최초 설계 논의에서는 "동료도 즉시 완전 사망"으로 잘못 서술됐었다 |
| 확률·데미지 위치 | 서버 전용, 유효타 확정 뒤 **타격당 한 번**만 굴림 | `Baton.RollSwingPower()`(신규 protected virtual 시임) — 클라가 조작할 경로가 없다 |
| 클래스 구조 | `ToyHammer : Baton` | `Baton`(709줄)이 부채꼴 스피어캐스트·임팩트 지연·원점 검증·크로스헤어 공유를 이미 갖고 있다. 판정 규칙이 두 벌로 갈라지지 않도록 `RollSwingPower`/`ImpactFxFor`/`WeaponLogName` 세 개의 시임만 열었다 |

## 구현 범위

| 구성요소 | 경로 | 역할 |
|---|---|---|
| 판정 시임 | [Baton.cs](../Assets/Scripts/Item/Weapons/Baton.cs) | `SwingPower` 구조체·`RollSwingPower()`·`ImpactFxFor(..., bool critical)`·`WeaponLogName`을 `protected virtual`로 추가 |
| 아이템 본체 | [ToyHammer.cs](../Assets/Scripts/Item/Weapons/ToyHammer.cs) | 평타 고정 데미지(프리팹 `m_damage`=1) + 확률(`m_criticalChance`, 기본 0.01) + 대박 데미지(`m_criticalDamage`, 기본 9999) |
| 효과음/연출 식별자 | `Enums.cs`의 `EAudioClip`/`EFx` | `HammerHit`/`HammerCrit` 각각 맨 뒤에 추가 (직렬화 순서 보존을 위해 중간 삽입 금지) |
| 프리팹 | `Assets/Prefabs/Items/ToyHammer.prefab` | `Baton.prefab` 복제 → 컴포넌트를 `ToyHammer`로 교체. NetworkObject의 `GlobalObjectIdHash`는 복제 직후 Baton과 충돌했었고, 프리팹 스테이지를 열었다 저장해 재생성함(중복 해시로 두면 NGO 프리팹 조회가 깨진다) |
| 손모델 래퍼 | `Assets/Prefabs/Items/ToyHammerHeldModel.prefab` | 서드파티 에셋 `Rubber Play Hammer/SM_Bouncy_Hammer_Toy.prefab`(원본 수정 금지)를 자식으로 0.45배 스케일해 담은 신규 래퍼. `ToyHammer.HeldModelPrefab`이 이걸 가리킨다 |
| 상점 등록 | `Assets/DefaultNetworkPrefabs.asset` | 등록 완료 |
| 로컬라이제이션 | `ItemTable Shared Data`/`_ko-KR`/`_en` | `Item.Name.ToyHammer`(id 816000000000001)·`Item.Description.ToyHammer`(id 816000000000002) |
| FxManager 조합표 | `Map_Cyberpunk.unity`·`Tutorial.unity`(디스크 직접 편집) | `HammerHit`→`ImpactDust`+오디오32, `HammerCrit`→`ImpactDust`+오디오33 |
| 오디오 항목 | `AudioLibrary.asset` | Id 32(HammerHit)·33(HammerCrit) — **`Clip`은 비워 둠**(미배선, 아래 참고) |

## 미배선·미완료 항목

- **효과음 클립 파일 자체가 없다.** `HammerHit`/`HammerCrit` 둘 다 `AudioLibrary.asset`에서 `Clip: {fileID: 0}`으로 남아 있다 — 파일이 들어오면 그 필드만 채우면 끝난다(#490 구역 스캔과 같은 처리 방침).
- **아이콘은 진압봉 아이콘을 임시로 재사용한다.** 전용 아이콘 에셋이 아직 없다.
- **모델·손 오프셋은 확정됐다.** `SM_Bouncy_Hammer_Toy`(래퍼로 0.45배 스케일) 사용. `HeldItemAnchor`에 임시 Player 리그를 붙여 스크린샷으로 그립 위치를 확인해 가며 잡은 값 → 팀에서 최종 미세 조정: `m_heldPositionOffset = (0.15, 0.02, -0.05)`, `m_heldRotationOffset = (10, 135, -20)`.
- **상점 진열대(ShopStand)가 씬에 아직 없다.** `Shop.unity`의 기존 진열대들은 3D 메시·가격표·설명 카드가 각각 개별 UI 오브젝트를 참조하는 구조라(예: 테이저 진열대 `SM_Wep_Stungun_01`은 자신이 곧 메시+콜라이더+`ShopStand`/`ShopStandView`이고, `ShopStandView`가 참조하는 가격표/카드 텍스트는 씬의 별도 오브젝트다) 단순 GameObject 복제로는 참조가 원본을 계속 가리켜 잘못된 이름/가격이 뜬다. 배치·카드 세트 복제는 레벨 배치 판단이 필요해 에디터에서 직접 할 것을 권한다. `m_itemPrefab`에 `Assets/Prefabs/Items/ToyHammer.prefab`을 연결하면 된다.
- **판매가(`m_shopPrice`)는 25로 임시 지정**했다 — 밸런스 확정 전 자리표시자.
- **`Map_Apocalypse.unity`에는 FxManager 항목이 없다.** 처음엔 이 씬에도 라이브 에디터로 항목을 반영했으나, 다른 세션이 같은 파일을 동시에 저장하면서 의도치 않은 변경(`=== _TEST ===` 그룹 활성화·"사용안함" 표시된 NavMesh Modifier Volume 삭제)이 함께 섞여 들어와, 분리하는 대신 이 PR 범위에서 파일 전체를 되돌렸다. 이 맵에서 타격음·연출을 내려면 `Map_Cyberpunk.unity`/`Tutorial.unity`와 같은 `HammerHit`/`HammerCrit` 2행을 별도로 추가할 것.

## 테스트 항목

컴파일 에러 0 확인 후 진행한다 (Unity Editor 콘솔 기준 확인 완료 — `dotnet build`는 이 저장소에서 무관한 사전 존재 오류(`PlayerSelfRevive` 미해결, origin/main 대비 11커밋 뒤처짐)로 항상 실패하니 참고하지 말 것).

### 확률 검증 (Play 전 — 인스펙터)
- [ ] `m_criticalChance`를 `1.0`으로 올리고 NPC 한 대만 쳐서 즉사하는지 확인
- [ ] `0`으로 내리고 여러 대 쳐서 항상 체력 1만 깎이는지 확인
- [ ] 확인 후 반드시 `0.01`로 되돌릴 것

### 평타·대박
- [ ] NPC 평타 — 체력 1만 깎이고, 넉다운 임계(기본 40) 밑으로 안 내려가면 쓰러지지 않는다
- [ ] NPC 대박(확률 1.0 상태) — 그 자리에서 사망 → 래그돌 → 유치장 문까지 끌고 가면 정상 판정되는지
- [ ] 동료 평타 — 체력 1만 깎인다 (아군 오사 로그 확인)
- [ ] 건강한 동료 대박 — **다운(쓰러짐, 60초 유예)** 에 들어가는지 확인 — 즉시 사망이 아님에 유의
- [ ] 이미 다운된 동료를 뿅망치로 확인사살 — 즉시 완전 사망(기능 정지)하는지
- [ ] 벽·허공 타격 — 진압봉과 동일한 문구·연출(`BatonHitWorld` 재사용)
- [ ] 추격 폭탄 타격 — 진압봉과 동일하게 즉발(`BatonHitMetal` 재사용)

### 배달·구매 (ShopStand 배치 후)
- [ ] 상점에서 구매 → 다음 라운드 본부 배달 → 눈에 보이는지(`m_heldModelPrefab` 누락 시 투명 스폰되는 회귀가 있었다, #490 사례 참고)
- [x] 손에 쥔 1인칭/3인칭 모델 각도·위치 — 확정값으로 확인 완료

### 멀티
- [ ] 클라이언트가 휘두를 때 판정·연출이 호스트(서버)에서 도는지
- [ ] 클라 콘솔에 `[서버 판정] 뿅망치 ...` 로그가 오는지
