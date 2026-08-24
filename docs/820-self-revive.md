# #820 부활 키트 자가 부활 — 구현 근거

## Context

부활 키트(#613)는 원래 남에게만 쓸 수 있었다. `ReviveKit.ResolveTarget`이 소지자 자신을 대상에서 잘라내고, 서버 `ServerTryRevive`가 자가 부활을 한 번 더 거부했다. 그래서 5000원짜리 키트를 품고 쓰러진 사람은, 손에 해답을 들고 동료가 와 주기를 기다려야 했다.

이 이슈는 쓰러진 본인이 **자기 키트로 스스로 일어날지 선택**하게 한다. 동료의 구조(Down: 맨손 E 3초 / Die: 동료 키트)는 그대로 두고, 자가 부활을 나란히 놓는다.

**확정 결정:**
- 허용 상태 — **Down + Die 둘 다**. 쓰러진 순간부터 선택할 수 있다.
- 입력 — **E 홀드 3초**. 좌클릭은 Die 중 관전 대상 순환(#590, `PlayerSpectateCamera.CycleNext`)이 이미 쓰고 있어 못 쓴다. E는 무력화 중 모든 소비 지점이 게이트로 막아 놀고 있었다.

## 함정 1 — Die 중 오너가 서버다, 그래서 채널링이 아이템에 있다

`PlayerIncapacitation.ApplyDeathOwnership`(#763)이 `Die` 진입 시 플레이어 오브젝트의 네트워크 오너를 서버로 옮긴다. 그래서 플레이어 오브젝트에 붙은 컴포넌트의 `SendTo.Owner`(채널링 게이지·`NotifyOwner`)는 Die 중 쓰러진 본인에게 닿지 않는다 — 서버가 스스로에게 보내는 것으로 끝난다.

반면 **소지품(아이템)의 소유권은 사망 중에도 그대로 남는다** — `ChangeOwnership` 호출부를 전수 확인해도 줍기·약탈·버리기뿐, 사망 경로에는 없다. 그래서 자가 부활의 채널링·게이지·피드백을 플레이어가 아니라 `ReviveKit`(아이템 자신)에 둔다. 아이템은 이미 `ItemBase → ChanneledInteractionBehaviour`라 게이지·`NotifyOwner`를 공짜로 쓴다.

이 구조가 아니었다면 `PlayerReviver`처럼 플레이어 오브젝트에 채널링을 두었을 텐데, 그러면 Die 중 자가 부활은 게이지도 안 뜨고 판정 로그도 못 받는 반쪽짜리가 됐을 것이다.

## 함정 2 — 무력화 중엔 슬롯 전환이 막힌다, 그래서 HeldItems를 직접 본다

`PlayerLoadout`은 무력화 중 슬롯 전환(휠·숫자키·스왑)을 전부 막는다(#105). 자가 부활 게이트를 "장착 중인 아이템이 부활 키트인가"로 판정하면, 쓰러질 때 마침 키트를 들고 있었는지가 운이 된다 — 5칸 인벤토리 어디에 있어도 써야 한다.

그래서 `PlayerLoadout.HeldReviveKit`은 오너 로컬인 `Slots`(장착 칸 모델)가 아니라 `HeldItems`(부착 자식, 서버 진실)를 `FirstOf<ReviveKit>()`로 직접 훑는다. NGO가 부착을 복제하므로 서버·오너 양쪽에서 같은 답이 나오고, Die 중 오너가 서버로 뒤집혀도(함정 1) 부착 관계 자체는 바뀌지 않아 영향받지 않는다. (`SyncHeldItemsRpc`가 `SendTo.Owner`라 Die 중엔 `Slots` 갱신이 본인에게 오지 않는다 — 그래서 `Slots`를 보면 안 된다.)

## 함정 3 — 부활과 소모의 순서

`ServerSelfChannelAsync`는 반드시 `health.ServerRevive()` → `ServerConsume()` 순서로 부른다. `ServerRevive()`가 부르는 `PlayerIncapacitation.Recover()`가 소유권을 본인에게 되돌린 **뒤에** `ServerConsume()`이 돌아야, 그 안의 `PlayerLoadout.ServerNotifyHeldItemsChanged` → `SyncHeldItemsRpc(SendTo.Owner)`가 서버가 아니라 본인에게 간다. 순서를 바꾸면 소모 통지가 허공에 날아가고, 본인 인벤토리 UI에서 키트가 안 사라진 채로 남는다(다음 슬롯 재동기화가 올 때까지).

남에게 쓰는 기존 경로(`ReviveKit.ServerTryRevive`)가 이미 이 순서였다 — 자가 경로도 같게 맞췄다.

## E 홀드 vs 동료 구조의 토글, 의도적으로 다르다

동료 구조(`PlayerReviver`)는 홀드가 아니라 **E 탭으로 시작 → E 재입력으로 토글 취소**다. 자가 부활만 진짜 홀드라 같은 키(E)의 의미가 상황에 따라 갈린다. 실수로 눌린 E가 3초 뒤 5000원짜리 키트를 태우는 사고를 막는 것이 홀드를 고른 이유다 — 사용자 승인을 받은 선택이다. `PlayerInputHandler.OnInteractCanceled`(신규, E 뗌)를 홀드 취소에 쓴다.

두 컴포넌트(`PlayerReviver`, `PlayerSelfRevive`)는 서로의 존재를 모른다. 둘 다 `PlayerIncapacitation.IsIncapacitated`에서 조기 반환하므로 겹치지 않는다 — 동료 구조를 시작하려면 구조자 본인이 서 있어야 하고, 자가 부활은 쓰러진 본인만 시작하므로 같은 순간 같은 사람이 두 경로를 동시에 탈 수 없다.

## `PlayerSelfRevive`가 plain `MonoBehaviour`인 이유

`PlayerInputHandler`는 **스폰 시점에 굳힌 오너 판정**(`m_isLocalOwner`, #774)으로 비오너에서 스스로 `enabled = false`가 되어 이벤트를 아예 발행하지 않는다. Die 중 소유권이 서버로 뒤집혀도(함정 1) 이 판정은 영향받지 않는다. 그래서 `PlayerSelfRevive`는 `IsOwner`를 따로 물을 필요가 없다 — 이벤트가 온다는 사실 자체가 "내 것"이라는 뜻이다. `NetworkBehaviour`로 만들 이유가 없으므로 `PlayerItemUser`와 같은 성격의 plain `MonoBehaviour`로 둔다.

## 검증 상태

컴파일은 Unity Editor(MCP `refresh_unity` + `read_console`)로 확인 — 에러 0건. **Play 테스트는 하지 않았다** — 이 프로젝트의 관례상 플레이 테스트는 사용자가 직접 한다. 아래는 실제로 켜서 봐야 하는 것들:

1. Down 중 자가 부활 — 다른 아이템을 장착한 채로도 되는지, 게이지가 뜨는지, 소모 후 인벤토리가 갱신되는지
2. Die 중 자가 부활 — **소유권 이관 구간이라 핵심 검증 지점.** 게이지·판정 로그가 본인에게 오는지, 부활 후 목록이 갱신되는지, 좌클릭 관전 순환(#590)과 E가 서로 간섭하지 않는지
3. 호스트 자신 — 호스트 몸은 애초에 서버 소유라 이관 분기가 다르다
4. 취소·경합 — E 짧게 눌렀다 뗌(키트 안 소모), 채널링 중 약탈로 손이 바뀜(채널 끊김), 동료가 먼저 살림(중복 부활 방지), 맨홀 납치(#775, 안내도 E도 안 먹음), 라운드 종료 후(안 먹음)

## 범위 밖

- 동료가 남에게 키트를 쓰는 좌클릭 경로(`Use`/`ResolveTarget`/`ServerTryRevive`)는 그대로 — 자기 자신 거부도 유지한다. 그 경로는 남에게 쓰는 것만 담당하고, 자가 부활은 완전히 별도 경로(`RequestSelfRevive`)다.
- `m_beingRevivedPrompt` 미배선(`Player.prefab`에 테이블·키가 비어 "동료가 구조 중" 문구가 원래 안 뜬다) — #820과 무관한 기존 결함.
- `HqRevivalDevice` 씬 미배치, GDD 아이템 표에 부활 키트 미등재 — 기존 상태 유지.
- 밸런스 — 자가 부활 3초·회복 HP는 동료 구조와 같은 값으로 시작한다. 자가 부활에 별도 페널티(더 긴 채널링·회복량 감소)를 둘지는 플레이 테스트 뒤 판단.
