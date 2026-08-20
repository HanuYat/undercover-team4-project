# 추격 폭탄 폭발이 NPC에도 피해를 주고 사망시킨다 (#768)

작성일: 2026-08-20 · 이슈 #768 · 선행 #688 · 브랜치 `feature/768-bomb-npc-damage`

## 배경

`BombDevice.ServerExplode`는 두 갈래로 나뉘어 있다. 플레이어에게는 `IDamageable.TakeDamage`로 피해를
주고 사망 시 래그돌 임펄스까지 보내지만(`ApplyBlastRagdoll`), NPC에게는 `ServerKnockbackNpcs`로
**넉백만** 건다. 시민 무리 한가운데서 터져도 아무도 죽지 않고 밀려나기만 한다.

GDD가 이미 갈려 있다. 6-4(235행)는 "반경 안 플레이어에게 피해를 주고 **주변 NPC를 넉백시킨다**"로
현재 구현을 못박고 있는데, 9장(383행)은 "차에 치이거나 **폭발**·낙뢰에 휘말려 죽고 그대로 남은 시민은
개인 기록에도 팀 카운트에도 오르지 않는다 — 차량 특례가 아니라 **환경 피해 일반 규칙**"이라고 쓴다.
**"폭발로 죽은 NPC"의 처리 규칙은 이미 서 있는데 그런 NPC가 생길 경로가 없다.**

코드 곳곳에도 이 자리가 예약돼 있다:

- `NpcRagdoll.cs:291` — `EnterRagdoll(Vector3.zero); // 힘없이 무너진다. 폭발은 임펄스를 따로 준다`
- `NpcHealth.cs:70` — 환경 피해 진입점 주석이 **"차·폭발"** 을 함께 부른다 (#690)

**이 작업의 원형은 `TrafficVehicle.ServerHitNpc`다.** 차량은 #634에서 "시민도 플레이어와 같은 피해를
받는다"를 이미 통과했고, #690에서 환경 피해 게이트까지 갈라 뒀다. 폭탄은 그 뒤를 따라가면 된다.

## 결정 사항

- **피해 세기는 플레이어와 같은 곡선을 그대로 쓴다.** `EvaluateDamage`를 재사용하고 NPC 전용 계수를
  두지 않는다. 현재 값(반경 8m · 폭심 150 · 가장자리 감쇠 0.2 · NPC `MaxHp` 100 · 넉다운 임계 40%)이면
  튜닝 없이 폭심 3.3m 즉사 / 6m까지 넉다운 / 그 밖 부상으로 갈린다. 노브를 하나 더 만들면 프리팹
  재배선과 두 벌 튜닝이 따라오는데, 그만한 이유가 아직 없다.

- **진입점은 `TakeEnvironmentalDamage`다 — `TakeDamage`가 아니다.** `TakeDamage`의 게이트
  (`CanBeDamaged`)는 신병 빼내기 우회를 막으려 연행 중을 제외하는데, 폭발은 신병 상태를 가리지 않는다.
  이 갈래는 #690이 차량 때문에 이미 만들어 뒀고 주석이 폭발을 함께 명시한다. `CanTakeEnvironmentalDamage`는
  죽은 대상만 막으므로 시체 재피격도 여기서 걸러진다.

- **즉사한 NPC는 플레이어와 같게 날린다.** `EvaluateRagdollImpulse`를 재사용한다. 감쇠·방향은 여전히
  `EvaluateKnockback` 한 곳이 쥐므로 사람과 NPC가 같은 곡선을 탄다.

- **NPC에는 RPC를 쓰지 않는다 — 서버에서 임펄스를 주면 끝이다.** `RagdollPoseStreamer`의
  `PoseAuthority`가 NPC는 `Server`로 못박혀 있고("NPC — 서버가 물리를 굴린다", 기본값도 `Server`),
  그 클래스의 전제가 "시뮬레이션은 하나뿐이고 원격은 결과만 받는다"이다. 플레이어 경로가 RPC인 것은
  플레이어 시체가 `PoseAuthority.Owner`이기 때문이고, **전파 모델이 다르다.**

  > ⚠ `TrafficVehicle.ServerHitNpc`의 주석은 "시체를 날리는 임펄스는 폭발(#506)과 같은 전 피어 통로가
  > 필요해 여기서는 걸지 않는다"고 쓰지만, 그 서술은 **#728 자세 스트리밍 이전**의 것이다. #506은
  > 플레이어 폭발 래그돌이라 오너 권한이었고 RPC가 필요했다. NPC는 아니다.

- **두 래그돌을 공통 인터페이스로 합치지 않는다.** `PlayerRagdoll`·`NpcRagdoll`은 `EnterRagdoll(Vector3)`
  시그니처가 같아 `IRagdoll` 추상화가 가능해 보이지만, 위의 전파 모델 차이가 인터페이스 뒤로 숨는다.
  1400줄·650줄짜리 두 클래스를 건드리는 대가로 얻는 것이 "호출부 한 줄 통일"뿐이다.

- **이미 정착한 시체는 건드리지 않는다.** 뒤집으면 유치장까지 끌고 가야 판정이 나는 시체가 폭발마다
  흩어진다.

  > ⚠ **정정(2026-08-20).** 이 문단은 원래 `NpcRagdoll.EnterRagdoll`이 `"이미 정착했다 — 다시
  > 날리지 않는다"`로 스스로 거부한다고 적었다. **사실이 아니다.** 정착한 시체는
  > `m_state == RagdollState.Ragdoll`이므로 첫 분기에 걸려 `ApplyImpulse`를 **받는다**
  > (`RagdollState`는 `Animated`/`Ragdoll` 둘뿐이고 정착은 `m_settled` bool이 든다 — 세 번째
  > 상태 `Settled`가 있는 것은 `PlayerRagdoll`이다). 실제 방어는 구현이 넣은 `wasAlive` 검사
  > **하나뿐**이며, "어차피 래그돌이 거부한다"며 지우면 시체가 흩어진다.

- **점수·판정은 손대지 않는다.** `NpcDeath` ⑦이 이미 "사망은 아무것도 판정하지 않는다"이고 현상금·
  오검거는 유치장 문 앞(`ArrestJudge.JudgeCorpse`)에서만 확정된다. 폭발사는 GDD 383·384의 환경 피해
  교리에 그대로 편입된다 — **새 규칙을 세우지 않는다.**

- **반응(도주·저항)은 유발하지 않는다.** GDD 171이 금지한다. `NpcReaction`은 호출자가 명시적으로 부르는
  구조라 피해 적용이 반응을 켜지 않으므로, 이 제약은 별도 가드 없이 지켜진다.

- **#688을 먼저 고친다.** 별도 이슈·브랜치다. 이 변경은 대량 사망을 일상으로 만드는데,
  `SpawnedNpcEventBase`에 사망 처리가 없어 이벤트가 시체를 계속 추적한다. 순서를 지키지 않으면 폭발
  검증이 에러 스팸에 묻힌다.

## 설계

변경 파일은 둘이다.

| 파일 | 변경 |
|---|---|
| `BombDevice.cs` | `ServerKnockbackNpcs()` → `ServerBlastNpcs()` |
| `NpcController.cs` | `public NpcRagdoll Ragdoll => m_ragdoll;` 한 줄 추가 |

`NpcController`는 `m_ragdoll`을 이미 캐싱하고 있지만(135행) 공개 접근자만 없다. 형제
(`Death`·`Health`·`Knockback`·`Rope`·`Stun`)는 전부 있으므로 같은 패턴으로 하나 더 얹는다.

```
ServerBlastNpcs()   // 서버·오프라인 전용
  1. OverlapSphereNonAlloc(폭심, m_explosionRadius, s_blastColliders)
  2. HashSet<NpcController>로 중복 제거
  3. NPC마다 (기준점 = npc.transform.position — 기존 넉백과 같은 기준):
     a. damage = EvaluateDamage(pos);  0이면 건너뛴다
     b. npc.Health.TakeEnvironmentalDamage(damage, gameObject)
     c. npc.Death.IsDead 이면 → npc.Ragdoll.EnterRagdoll(EvaluateRagdollImpulse(pos))
                        아니면 → npc.Knockback.ServerApplyKnockback(EvaluateKnockback(pos))
```

### 왜 이 순서인가

피해를 먼저 넣고 **결과에 따라 갈리는** 것이 이 설계의 핵심이다. 근거는 `TrafficVehicle`이 남긴 경고다:

> ⚠ **`ServerApplyKnockback`은 `Dead` 상태를 거르지 않아 시체를 `Stunned`로 되살린다.**

> ⚠ **정정(2026-08-20).** 위 경고는 **낡았다** — `NpcKnockback.ServerApplyKnockback`은 #634부터
> `Dead`·`Jailed`·`Intruding`을 거른다. 분기는 여전히 옳지만 **이유가 다르다**: 시체를 되살리지
> 않기 위해서가 아니라, 시체를 넉백이 아니라 **임펄스로 날리기 위해서**다.

차량은 그래서 넉백을 피해보다 **먼저** 걸어 회피했고, 그 대가로 "죽는 시민은 그 자리에 무너진다"를
받아들였다. 이 설계는 대신 **분기로** 회피한다 — 죽은 NPC에는 넉백을 아예 걸지 않으므로 되살리는 경로가
없고, 죽는 NPC도 임펄스로 날아간다.

**그래서 `c`의 분기는 최적화가 아니라 안전 장치다.** 나중에 "어차피 둘 다 미는 거니까" 하고 무조건
넉백으로 되돌리면 시체가 되살아난다.

사망은 동기적이라 `b` 직후의 `IsDead` 판정이 유효하다 — `NpcHealth.SetHp`가 HP 0 도달 엣지에서
`m_owner.Death.ServerEnterDead(attacker)`를 그 자리에서 부르고, `NpcDeath.IsDead`는 FSM 상태
(`CurrentState == Dead`)를 읽는데 `ServerEnterDead` ⑤가 그 전이를 동기로 수행한다.

`EnterRagdoll`이 멱등이라 순서 경합도 안전하다. 사망 폴링(`NpcRagdoll.PollRagdollTriggers`)이 먼저
`EnterRagdoll(zero)`를 걸어도 우리 임펄스는 "이미 물리 중 → 누적" 경로로 들어간다.

### 중복 피격과 버퍼

| 문제 | 처리 |
|---|---|
| 사람 하나가 래그돌 콜라이더 여러 개로 잡혀 피해가 여러 번 들어간다 | `HashSet<NpcController>`를 필드로 두고 폭발마다 `Clear()` |
| `s_blastColliders`가 64칸이라 8m 반경 군중에서 포화되고, 넘친 NPC는 조용히 무사해진다 | 256으로 확대 + `hitCount == Length`면 경고 로그 |

`ServerApplyKnockback`은 "비행 중 중복 호출을 무시"하는 자체 가드가 있어 지금까지 중복이 드러나지
않았다. 피해에는 그런 가드가 없으므로 **중복 제거는 선택이 아니라 필수다.** 차량은 같은 문제를
`m_hitNpcs` 장부로 풀지만 그쪽은 주행 단위 장부라 성격이 다르다 — 폭발은 1회성이므로 호출마다 비우는
집합이면 충분하다.

버퍼 포화는 조용히 틀리는 종류의 버그다. 크기를 늘리는 것만으로는 상한이 사라지지 않으므로 포화를
로그로 드러내, 실제로 넘치면 다음 조치(레이어 마스크로 래그돌 뼈 제외 등)를 판단할 근거를 남긴다.

## 엣지 케이스

대부분 기존 경로가 이미 처리한다. 새로 만들 것이 없다는 점을 확인해 둔다.

| 상황 | 결과 | 근거 |
|---|---|---|
| 이미 시체 | 피해가 막힌다 | `CanTakeEnvironmentalDamage`가 `Dead`만 거른다 |
| 연행(밧줄) 중 신병 | 그대로 맞는다 | `TakeEnvironmentalDamage` 게이트를 쓰는 이유 (#690) |
| 기절 중 NPC | 정상 피해·사망 | `ServerEnterDead` ②가 기절 오버레이 정리 |
| 밧줄로 끌리는 중 사망 | 밧줄이 끊기고 시체로 남는다 | `ServerEnterDead` ④ |
| 납치범 | 호송에서 떨어져 나간다 | `NpcHealth.OnDamaged` → `AbductionEvent`, GDD 249와 일치 |
| 반응(도주·저항) | 일어나지 않는다 | `NpcReaction`은 호출자가 부른다, GDD 171 |
| 점수·오검거 | 사망 시점엔 판정 없음 | `NpcDeath` ⑦, GDD 383·384 |
| 이미 정착한 시체의 재비행 | 일어나지 않는다 | 구현의 `wasAlive` 검사 (`EnterRagdoll`은 거부하지 않는다) |
| 수감 중 NPC | 사실상 닿지 않는다 | 유치장은 본부, 폭탄은 현장 전용 |

## 문서 변경

GDD 6-4(235행)의 "반경 안 플레이어에게 피해를 주고 **주변 NPC를 넉백시킨다**"를 실제 동작에 맞춰
고친다. 6-4 표(202행)의 "피해·주변 넉백"도 함께 본다.

결정 노트를 단다. 핵심은 **새 규칙이 아니라 383행 환경 피해 교리에 편입된다**는 점이다 — 폭발로 죽은
시민은 그대로 두면 세지 않고, 유치장까지 끌고 가면 인계로 판정된다. 차량과 같은 취급이다.

`TrafficVehicle.ServerHitNpc`의 "전 피어 통로가 필요해" 주석도 함께 고친다 — #728 이후로 사실이
아니게 됐고, 그대로 두면 다음 사람이 같은 결론을 다시 내린다.

## 테스트

`Map_Apocalypse` 호스트 + MPPM 클라이언트 1명.

1. 시민 무리 한가운데서 폭발 → 폭심 3.3m 안 즉사·비행 / 6m까지 넉다운 / 그 밖 부상
2. **원격 클라이언트에서 시체 비행이 호스트와 같게 보인다** — `PoseAuthority.Server` 전제가 맞는지가
   여기서 갈린다. 이 설계에서 가장 위험한 지점이다
3. 밧줄로 끌던 신병이 폭발에 죽는다 → 밧줄이 끊기고 시체가 남는다
4. 진범을 폭발로 죽인 뒤 시체를 유치장까지 끌고 가 수감 → 현상금 정상 지급
5. 폭발 직후 콘솔 에러 0건 (#688 수정 후) — 특히 "죽은 NPC를 Run으로 되돌리려 했다"가 없어야 한다
6. 반경 안 NPC가 도주·저항으로 돌변하지 않는다
7. NPC가 여럿(6명 이상) 겹친 자리에서 폭발 → 버퍼 포화 경고가 뜨는지, 뜬다면 누락이 있는지

## 범위 밖

- 이미 정착한 시체를 다시 날리기 — `EnterRagdoll`의 기존 거부 결정을 뒤집지 않는다
- NPC 전용 피해·임펄스 계수
- 폭발 피해의 화면 표시(NPC에는 원래 없다)
- `PlayerRagdoll`/`NpcRagdoll` 공통 인터페이스 추출
- `TrafficVehicle`이 시체 임펄스를 걸도록 바꾸는 것 — 주석만 고치고 동작은 그대로 둔다 (#634 결정)
