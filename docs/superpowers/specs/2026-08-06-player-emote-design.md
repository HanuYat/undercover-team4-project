# 플레이어 감정표현 — 휠 선택 · 애니메이션 · 이모지 설계 (#219)

> 상태: 설계 확정 — 구현 플랜 대기
> 선행: 없음 (main에서 분기)
> 에셋: Kevin Iglesias **Human Dance Animations** — 구매 완료, 임포트 대기 (4절)
> 관련: GDD 2-2(파티 게임 정체성) · GDD 10-2(Player 상태 enum) — enum 항목은 이 작업에서 갱신한다

## 0. 목표

파티 게임 정체성을 강화하는 **감정표현**을 넣는다. 플레이어가 로비에서 8칸 휠을 자기 취향대로 구성하고, 인게임에서 `T`를 홀드해 방향으로 고른 뒤 떼면 전 피어에게 보이는 애니메이션과 머리 위 이모지가 재생된다.

레퍼런스는 리그 오브 레전드·포트나이트의 감정표현 휠이다. 조작 감각(홀드 → 방향 조준 → 떼면 발동)을 그대로 따른다.

## 1. 범위

### 포함

1. **`EmoteDefinition` / `EmoteCatalog`** — 감정표현 데이터 (ScriptableObject)
2. **애니메이터 확장** — `PlayerAnimatorControllerBuilder`에 Emote 서브스테이트머신 자동 생성
3. **재생·동기화** — `PlayerEmote`(서버 권위 `NetworkVariable`) + `PlayerEmoteView`(전 피어 반영)
4. **감정표현 휠 UI** — `T` 홀드 8칸 방사형, 마우스 방향 선택
5. **이모지 말풍선** — 머리 위 빌보드 아이콘
6. **3인칭 카메라 전환** — 재생 중 자기 몸이 보이도록
7. **로비 구성 패널** — 카탈로그 ↔ 8칸 배치 편집, PlayerPrefs 로컬 저장
8. **`PlayerState.Dance` → `Emote`** 및 GDD 10-2 반영

### 제외

- **인게임/상점에서의 휠 재구성** — 구성은 로비에서만. 로비는 세션당 1회만 거치는 씬이므로 한 번 정하면 그 세션 동안 고정된다
- **감정표현 획득·해금·상점 판매** — 카탈로그 전체가 항상 열려 있다
- **감정표현 전용 사운드** — 클립에 소리를 붙이는 것은 후속. `SoundManager`(#483) 정착 이후가 자연스럽다
- **카메라 충돌 완전 대응** — 맵 교체가 예정돼 있어 SphereCast 1회 수준까지만 한다

## 2. 확정 결정

| 항목 | 결정 | 근거 |
|---|---|---|
| 범위 | 애니메이션 + 이모지 동시 | 이슈 완료 기준이 둘 다 요구 |
| 구성 시점 | 로비 전용 | 이슈 본문 그대로. 인게임 편집은 상태 관리만 늘린다 |
| 재생 규칙 | 정지 상태에서만 시작, 입력 즉시 취소 | 롤·포트나이트 방식. 감정표현으로 무적이 되는 악용 경로가 없다 |
| 루프 | 무한 루프, 취소 시 종료 | 포트나이트 방식. 취소 규칙이 있으므로 무한이어도 갇히지 않는다 |
| 휠 | `T` 홀드 · 8칸 · 떼면 발동 | 닫기 조작이 따로 없어 빠르다 |
| 동기화 | `NetworkVariable<sbyte>` 상태 폴링 | 아래 3절 |
| enum | `Dance` → `Emote`, 종류는 카탈로그 id | 애니메이션 에셋을 추가해도 코드·네트워크 계약이 안 바뀐다 |
| 자기 시점 | 재생 중 3인칭 전환 | 1인칭에서는 자기 감정표현이 보이지 않는다 |

## 3. 동기화 방식 — 이벤트가 아니라 상태

서버 권위 `NetworkVariable<sbyte> m_activeEmote`에 재생 중인 **카탈로그 인덱스**를 담는다(`-1` = 재생 없음). 전 피어가 이 값을 구독해 애니메이터를 구동한다. 기존 `Down`/`Crouch`/`Airborne`과 같은 구조다.

**일회성 RPC(`Baton.PlaySwingRpc` 방식)를 쓰지 않는 이유**는 이 기능에 취소가 있기 때문이다. 시작과 취소를 각각 RPC로 보내면 하나가 유실되거나 순서가 뒤집혔을 때 남의 화면에서 영원히 춤추는 플레이어가 남고, 이를 되돌릴 경로가 없다. 상태값 하나면 무슨 일이 있어도 마지막 값으로 수렴하고, 재생 도중 다가온 피어도 진행 중인 감정표현을 그대로 본다.

`NetworkAnimator`는 쓰지 않는다 — 프로젝트가 채택한 적이 없고, 파라미터 전량 브로드캐스트가 지금의 폴링 구조와 이질적이다.

**인덱스를 싣고 id를 싣지 않는 이유:** 카탈로그 에셋은 모든 피어가 동일한 빌드로 갖고 있으므로 인덱스면 충분하고 1바이트로 끝난다. 문자열 id는 카탈로그 순서 변경에 견뎌야 하는 **로컬 저장** 쪽에서만 쓴다.

## 4. 데이터

### `EmoteDefinition` (ScriptableObject)

| 필드 | 용도 |
|---|---|
| `m_id` (string) | 안정적 키. 로비 슬롯 구성을 PlayerPrefs에 저장할 때 사용 — 카탈로그 순서가 바뀌어도 사용자 설정이 깨지지 않는다 |
| `m_displayName` | 휠·로비 목록 표시 (기존 Localization 방식에 맞춘다) |
| `m_icon` (Sprite) | 휠 칸 아이콘 |
| `m_clip` (AnimationClip) | 있으면 재생. 없으면 애니메이션 없는 순수 이모지 |
| `m_loop` (bool) | 켜면 취소할 때까지 무한, 끄면 `m_clip.length` 후 자동 종료 |
| `m_bubbleSprite` (Sprite) | 있으면 머리 위 말풍선 표시 |

`kind` enum을 두지 않고 `m_clip`·`m_bubbleSprite`를 **각각 옵셔널**로 뒀다. "댄스만" / "이모지만" / "춤추면서 이모지" 세 경우가 필드 조합으로 자연히 나오고 분기 코드가 생기지 않는다. 클립 길이는 `m_clip.length`로 직접 읽어 중복 필드를 만들지 않는다.

### `EmoteCatalog` (ScriptableObject)

`EmoteDefinition` 리스트 하나. **리스트 인덱스가 네트워크 계약**이다. id → 인덱스 조회용 딕셔너리를 런타임에 구성한다.

### 초기 항목

Kevin Iglesias **Human Dance Animations**를 임포트해(아래) 댄스와 기존 감정 클립을 섞어 8칸을 채운다:

- 댄스 — `Male/Social/Dance/Steps` 아래 `HumanM@Dance01~18` 중 4종. 전부 **자체 루프 클립**이라 `m_loop = true`로 그대로 쓴다
- 제스처 — 기존 `Social/Emotions`의 `Cheer01` · `HandClap01` · `Angry01` · `Fear01`

같은 벤더·같은 리그(HumanM)라 아바타 호환 문제가 없다. `DancePose01~07`은 Begin/Loop/Stop 3단 구조라 상태 머신이 한 겹 더 필요하므로 이번 범위에서 제외한다 — 필요해지면 후속에서 다룬다.

이후 감정표현을 늘릴 때는 **카탈로그에 항목 추가 + 애니메이터 빌더 재실행**만 하면 된다. 코드 변경이 필요 없다.

### 에셋 임포트

`Human Dance Animations.unitypackage`는 `Assets/Kevin Iglesias/`로 풀린다. 프로젝트 관례가 `Assets/Imported/` 아래이므로 임포트 후 `Assets/Imported/Kevin Iglesias/Human Dance Animations/`로 옮긴다.

**기존 `Human Animations` 폴더와 합치지 않는다** — 패키지에 `HumanM@Idle01.fbx` 중복본이 들어 있어 합치면 충돌한다. 별도 폴더로 두면 충돌이 없고 어느 패키지에서 온 클립인지도 분명해진다.

## 5. 애니메이터 확장

`PlayerAnimatorControllerBuilder`에 Emote 구역을 추가한다 — Knockdown 상태 머신을 만든 방식 그대로다.

- 파라미터 `Emote`(bool) · `EmoteIndex`(int) 추가
- Base Layer에 `Emote` 서브스테이트머신 1개. **카탈로그 순서대로 클립 스테이트를 자동 생성**하고 각 스테이트 진입 조건은 `EmoteIndex == i`
- `Locomotion → Emote`: `Emote == true`, exit time 없이 0.15s 블렌드. 역방향은 `Emote == false`
- **Emote 서브스테이트머신에서 `Down`·`Airborne`으로 나가는 전이를 직접 넣는다.** 없으면 감정표현 중 쓰러질 때 Locomotion을 한 번 거쳐 쓰러짐이 한 박자 늦게 보인다. Knockdown의 즉시성은 `PlayerAnimationDriver`가 여러 주석에서 강조하는 부분이라 여기서 깨면 안 된다
- 비루프 클립의 종료는 애니메이터의 exit time이 아니라 **서버가 값을 내려서** 이뤄진다. 재생 여부의 단일 진실 원천은 `m_activeEmote` 하나다

## 6. 컴포넌트 분해

`Assets/Scripts/Player/Emote/` 아래로 모은다.

| 컴포넌트 | 실행 범위 | 책임 |
|---|---|---|
| `PlayerEmote` (NetworkBehaviour) | 서버 권위 | `m_activeEmote` 소유. `RequestEmoteServerRpc` 검증 후 세팅, `CancelEmoteServerRpc`. 비루프 클립은 길이 경과 후 서버가 자동 해제 |
| `PlayerEmoteInput` | 오너 전용 | `T` 홀드 감지 → 휠 열기, 마우스 방향 누적, 뗄 때 발동 요청. 재생 중 취소 입력 감지 |
| `PlayerEmoteView` | 전 피어 | `m_activeEmote` 구독 → Animator `Emote`/`EmoteIndex` 세팅 + 말풍선 표시/해제 |
| `PlayerEmoteCamera` | 오너 전용 | 재생 중 3인칭 전환 및 복귀 (7절) |
| `EmoteBubbleView` | 전 피어 | 머리 위 빌보드 아이콘. 기존 `PlayerNameTag` 앵커를 재사용한다 |
| `EmoteLoadout` (순수 C#) | 로컬 | 8칸 슬롯(id 배열) + PlayerPrefs 직렬화. MonoBehaviour가 아니다 |
| `EmoteWheelView` (UI) | 오너 | 8칸 방사형 렌더. 각도 → 인덱스 계산은 static 순수 함수로 분리 |
| `EmoteLoadoutPanel` (로비 UI) | 로컬 | 카탈로그 목록 ↔ 8칸 배치 편집 |

### `PlayerAnimationDriver`에 넣지 않는 이유

그 파일은 이미 크고 "이동·자세를 폴링해 애니메이터에 반영한다"는 축이 뚜렷하다. 감정표현을 얹으면 두 가지를 하는 파일이 된다. `PlayerEmoteView`가 자기 `Animator`를 직접 잡는다.

### 취소 권한을 나누는 이유

- **클라가 요청하는 취소:** 이동·점프·공격·아이템 사용/전환·상호작용·인벤토리·ESC, 그리고 휠을 다시 열어 다른 감정표현으로 갈아타기
- **서버가 독립적으로 거는 취소:** 다운·기절·사망, 호송 대상이 됨, 라운드 종료, 씬 전환

서버가 이동까지 감지해 취소하면 두 취소 경로가 경쟁하고, 지연 때문에 내 화면보다 남의 화면에서 먼저 끊긴다. 감정표현은 게임 유불리가 없어 클라를 신뢰하는 비용이 0이므로 이동 판정은 클라에 맡긴다.

### 시작 조건 (서버 검증)

인덱스 범위 유효 · 지상 · 수평 속도 ≈ 0 · 다운/기절/사망 아님 · 호송 중 아님 · 앉은 상태 아님.

## 7. 3인칭 전환

`PlayerLook.ApplyOwnerView`가 자기 몸을 `OwnBody` 레이어로 옮겨 자기 카메라에서 컬링한다. 그대로 두면 감정표현을 발동해도 **내 화면에는 아무 일도 일어나지 않는다** — 머리 위 이모지도 시야 밖이다.

`PlayerEmoteCamera`가 재생 중에만:

- 카메라 cullingMask에 `OwnBody`를 되살리고, 카메라를 뒤·위로 부드럽게 뺀다 (종료 시 원위치)
- **몸통 회전을 잠그고 카메라만 돌린다.** `PlayerLook.HandleLook`에 이미 "쓰러진 동안엔 카메라 로컬만" 도는 모드가 있으므로 그 경로를 재사용한다. 새 모드를 만들 필요가 없고, 마우스를 움직여도 취소되면 안 된다는 요구와도 맞는다
- 벽 뚫림은 SphereCast 1회로 카메라를 당기는 수준까지만 한다 (맵 교체 예정)

## 8. 입력

`Assets/InputSystem_Actions.inputactions`의 Player 맵에 `Emote` 액션을 추가한다 (키 `T`, Hold).

숫자키는 이미 `SelectSlot`(인벤토리 3칸)이 점유하고 있으므로 감정표현 숫자 단축키는 두지 않는다.

## 9. 테스트

EditMode 단위 테스트를 붙이는 대상은 순수 로직 둘이다:

1. **`EmoteLoadout`** — 저장/로드 왕복, 기본 구성, 카탈로그에 없는 id가 저장돼 있을 때의 처리(빈 칸 취급), 슬롯 수 불변
2. **휠 각도 → 인덱스 매핑** — 8칸 경계각, 정확히 경계에 놓인 각도, 중심 데드존

재생·동기화·카메라 전환은 Play 모드 검증 영역이며 사용자가 직접 확인한다.

## 10. 부수 정리

- `PlayerState.Dance` → `Emote`로 변경. 개별 감정표현 종류는 enum이 아니라 카탈로그 id로 다룬다
- GDD 10-2에 위 결정을 근거 한 줄로 반영

## 11. 완료 기준 대응 (#219)

| 이슈 완료 기준 | 대응 |
|---|---|
| 감정표현 애니메이션 1종 이상 + 이모지 표시 동작 | 4절 초기 항목 6종 + 6절 `EmoteBubbleView` |
| 전 피어 동기화 | 3절 `NetworkVariable<sbyte>` 상태 동기화 |
| 상태 enum 정리 방향 확정 (GDD 10-2 반영) | 10절 |
