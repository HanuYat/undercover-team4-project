# 홈런 진압봉 — 플레이어도 래그돌로 (#815)

> 근거·실측·되살리면 안 되는 것들. `HomeRunBaton.cs`·`PlayerIncapacitation.cs`·`PlayerRagdoll.cs`를
> 고치기 전에 이 문서를 먼저 볼 것. `074e108c`가 처음 들어온 자리이고, 이 문서는 그 뒤 "플레이어도
> 래그돌로 날아가게" 확장을 다룬다.

관련 문서
- [player-ragdoll.md](player-ragdoll.md) §1·§9 — 진입 사유가 `Die` 하나에서 둘로 늘었다
- [759-ragdoll-slowmotion-handoff.md](759-ragdoll-slowmotion-handoff.md) — 캡슐 추종이 `FixedUpdate`인 이유. 여기서 새 코드를 추가할 때도 그대로 지켜야 한다
- [763-player-ragdoll-npc-parity.md](763-player-ragdoll-npc-parity.md) — 소유권을 권위 전이 통로로 쓰는 패턴의 원본
- [506-explosion-ragdoll.md](506-explosion-ragdoll.md) §9 — "진입 조건은 폭발이 아니라 사망"의 원래 결정

---

## 1. 왜 사양을 뒤집었나

`074e108c`(#815)는 NPC만 물리로 날리고 플레이어는 `PlayerMovement.AddKnockback`(캡슐 넉백)으로
대신했다 — 그때 `PlayerRagdoll`이 사망(`Die`) 전용이라 살아 있는 몸을 못 태웠기 때문이다. 이번
작업은 `IncapacitationCause`에 `Launched`를 더해 그 벽을 없앤다.

## 2. 물리 권위 — 오너 vs 서버

`Launched`를 넣으면 이 선택은 `PlayerIncapacitation.ApplyDeathOwnership`의 한 줄로 줄어든다:

```csharp
bool wantsServerOwner = cause == IncapacitationCause.Die; // Launched를 넣으면 서버 시뮬로 뒤집힌다
```

**오너로 남긴다** (지금 코드의 선택). 근거:

- `PlayerRagdoll.HasMoveAuthority`가 `IsOwner`다. 소유권을 서버로 옮기면 **피격 당한 본인의 클라에서
  `TickCapsuleFollow`(#759)가 멈춘다** — 본인은 자기 텀블을 NetworkTransform 보간(RTT + 100ms)으로만
  보게 된다. 사망 래그돌은 이미 정착한 시체라 이게 문제였던 적이 없었지만, 2초짜리 격렬한 회전에서는
  체감이 갈린다.
- 구조 변경이 0이다 — `Player.prefab`의 NetworkTransform `AuthorityMode: 1`(Owner)과
  `RagdollPoseStreamer.m_authority: 1`(Owner)이 이미 정렬돼 있다.
- 소유권 이전은 이 프로젝트에서 세 번 긁힌 계열이다 — #763(이관 자체), #774(`m_isLocalOwner`를
  스폰 시점에 굳혀야 했다), #820(`SendTo.Owner`가 서버로 새서 부활 순서를 바꿔야 했다). 2초짜리
  비행에 이 비용을 새로 지불할 이유가 약하다.
- 치팅(비행 중 자기 위치 조작)은 회귀가 아니다 — `AddKnockback`도 이미 오너 로컬이었다.

**관측자 화면이 못 봐줄 정도면 뒤집을 것.** 그때는 위 한 줄에 `|| cause == Launched`를 더하고,
`PlayerLoadout`·`PlayerPenaltyView`·`PlayerTheftView`의 `SendTo.Owner` 경로가 비행 2초 창에 새는지
확인한다(#820과 같은 함정).

## 3. 상태 = `Launched`, 임펄스 = RPC (BombBlast와 같은 분리)

`PlayerIncapacitation.ServerLaunch`가 서버 권위 `Cause`를 세우면 전 피어가 `PlayerRagdoll.PollDeath`
폴링으로 진입한다 — 이벤트가 아니라 폴링인 이유는 [player-ragdoll.md §9](player-ragdoll.md)의
"원인만 바뀌면 안 울린다" 문제 그대로다. 임펄스는 `HomeRunBaton`이 때린 무기 자신의 NetworkObject로
피격자 **오너에게만** RPC를 보낸다 — `BombBlast.NotifyBlastDeaths`의 "사망은 폴링, 임펄스는 RPC"
분리와 정확히 같다. `EnterRagdoll`이 멱등이라(이미 물리 중이면 임펄스만 누적) 폴링과 RPC의 도착
순서는 무관하다.

`BombBlast`가 전 피어에 브로드캐스트하는 것과 달리 여기는 오너 하나에만 보낸다 — 원격 피어의 뼈는
어차피 키네마틱이라 임펄스를 받아도 버리기 때문에(`RagdollRig.ApplyImpulse`가 키네마틱 바디를
건너뛴다), `PlayerMovement.AddKnockback` 시절부터 있던 `RpcTarget.Single(owner)` 패턴을 그대로
잇는다.

## 4. 왜 정착이 곧 복구 신호인가

`Die`는 부활 키트·본부 장치가 명시적으로 `Recover()`를 부른다. `Launched`에는 그런 외부 장치가
없다 — 그래서 `PlayerRagdoll.Settle()`이 스스로 `PlayerIncapacitation.RequestLaunchSettled()`를
불러 서버에 알린다. `Settle()`은 권위 게이트(`Update`의 `HasMoveAuthority`) 뒤에서만 도는 코드
경로에서 호출되므로, 이 통보를 보내는 피어는 항상 그 몸의 오너다.

⚠ **이 통보는 회차 번호를 들고 오지 않는다.** `m_launchEpisode`는 `m_stunEpisode`와 같은 서버 전용
값이라, 원격 오너가 자기 인스턴스에서 그 필드를 읽으면 서버와 무관하게 항상 0이다(처음 구현에서
실제로 이 값을 RPC에 실어 보내는 버그가 있었다 — 서버의 회차와 어긋나 통보가 거의 매번 무시됐다).
고친 형태는 회차 대신 **"지금 원인이 여전히 비행인가"만** 본다(`ServerRecoverFromLaunch`) — 이미
다른 사유로 바뀌었으면 늦게 온 통보이므로 무동작이고, 그 침입 창이 실질적으로 사라지는 이유는
`ServerLaunch`가 이미 무력화된 대상을 거부해 같은 몸에 두 비행 에피소드가 동시에 살 수 없다는 데
있다.

서버 최대시간(`ServerLaunchTimeoutAsync`)은 이 통보와 **다른 문제**를 푼다 — 통보가 영영 안 오는
경우(연결 끊김 등)의 안전망이고, 이건 순수 서버 로컬 타이머라 `m_launchEpisode`를 회차로 비교해도
안전하다(`ServerStunTimerAsync`와 같은 구조). 값(`m_launchMaxSeconds`, 기본 6초)은 정상 정착
(1~3초)보다 넉넉하고, 시체 정착의 최악 타임아웃(`m_settleTimeoutSeconds`(5) ×
`k_lostBodyTimeoutFactor`(4) = 20초)보다는 짧게 잡았다.

## 5. 되살리면 안 되는 것

- **`TickCapsuleFollow`를 `Update`로 되돌리지 말 것.** #759가 프레임 레이트가 물리 스텝(50Hz)보다
  빠르면 발산한다는 것을 실측으로 닫았다 — `Launched`가 새 진입 사유라도 이 함수는 그대로 두고
  건드리지 않는다.
- **`RagdollPoseStreamer`의 `[DefaultExecutionOrder(100)]`을 지우지 말 것** — 루트 추종이
  `FixedUpdate`로 내려온 뒤 스트리머 캡처와 실행 순서가 맞아야 자세가 안 갈린다(#759).
- **`m_sawDeathThisEpisode`/`m_awaitingDeathSeconds`의 인과 가드를 없애지 말 것.** 이름은 사망
  전용이던 시절 그대로지만 뜻은 "래그돌 원인을 봤다"로 넓어졌을 뿐 로직은 그대로 유효하다 — 폭발
  임펄스가 사망 동기화보다 먼저 도착하는 것과 같은 순서 문제가 비행 임펄스에도 그대로 있다.
- **`isDownException`(`Baton.EvaluateNonNpcSwing`)에 `Launched`를 넣지 말 것.** 비행 중 무적이
  설계 결정이다(#815 계획 검토) — 넣으면 저글링(공중에서 재타격)이 가능해진다.

## 6. 아직 실측하지 못한 것 (플레이 테스트 대상)

- 1인칭 팔이 비행 중 화면에 남는가 — `PlayerHandView`는 오너 전용이고 `IsRagdollActive`를 보지
  않는다. 사망 때는 관전 시점(#576)이 이 문제를 가려 왔는데, 살아 있는 비행은 관전 경로를 안 탄다.
- 살아 있는 몸에 `SetControllerEnabled(false)` + `m_animator.enabled = false`를 거는 것이 처음이다.
  `PlayerAnimationDriver`가 `IsProne || IsRagdollActive`로 받으므로 이론상 맞지만 실측이 없다.
- 기상 자세(`m_rootYawOffset`)가 부활용으로 맞춰진 값이라 비행 착지 자세에서도 맞는지.
- 맵 밖으로 날아가 최대시간 안전장치가 걸리는 그림.
- 관측자 화면의 텀블 품질 — §2의 되돌림 기준.
