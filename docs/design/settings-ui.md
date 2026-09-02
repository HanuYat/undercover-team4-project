# 설정 창 UI (#225)

> 설계 정본. 게임 설정(마우스 감도·음량)의 **저장·적용 경로**와 **창 진입 방식**을 정의한다.
> 코드 구조 규칙은 [architecture.md](../architecture.md), 게임 규칙은 [GDD.md](../GDD.md)가 정본 — 이 문서는 설정 창만 다룬다.
> 관련: [scene-flow.md](scene-flow.md) (씬 세트·전환), #216 (마우스 회전 스무딩), #100/PR #194 (근접 음성).

## 1. 목표 & 범위

플레이어가 **마우스 감도와 음량을 직접 조절하고, 그 값이 다음 실행에도 유지**되게 한다. 설정값은 전부 **로컬 전용** — 네트워크로 동기화하지 않는다(각자 자기 화면·자기 귀만 바꾼다).

### 범위 안 (이 이슈 = Phase 1)
- 설정 저장소(`GameSettings`) + PlayerPrefs 영속화
- 항목 3개: **마우스 감도 · 마스터 음량 · 음성 음량**
- 설정 창 패널 + 프리팹 + 4개 씬 배치 + 진입 버튼 2경로
- **먹통 음성 왜곡(#372) 구간에서도 음성 음량이 유지되게** 오디오 탭에 설정값 반영 (§5)

### 범위 밖 (후속 이슈 후보 = Phase 2)
- 무전/근접 음성 **분리** 볼륨, 마이크(입력) 감도
  - **갱신 (#430, 2026-08-01):** 마이크 **음소거 토글**은 Phase 2를 기다리지 않고 구현했다 — `GameSettings.MicMuted` + 입력 장치 뮤트. 아래 결정 (i) 참고. 입력 **감도·장치 선택**은 여전히 Phase 2다(Vivox 입력 장치 열거·전환 경로가 따로 필요).
- 그래픽 옵션(창모드·해상도·품질)
  - **갱신 (#796, 2026-08-21):** **창모드·해상도·수직동기화**를 구현했다 — 아래 결정 (j)·(k) 참고.
    **품질(Quality)은 여전히 범위 밖**이다(같은 섹션 자리만 비워 둔다).
- 언어 선택 (Localization 인프라는 이미 있음 — 저비용 추가 가능)
- 키 리바인딩

## 2. 확정된 설계 결정

| 항목 | 결정 | 근거 |
|------|------|------|
| (a) 접근 경로 | **메인 메뉴 + 인게임 양쪽** — 프리팹 1개를 Title·Lobby·Shop·Main 4씬에 배치 | `PauseCanvas.prefab`(Lobby·Shop·Main) / `QuitConfirmCanvas.prefab`(Title)로 이미 확립된 관례 |
| (b) 음량 항목 | **Phase 1은 마스터 + 음성 2개.** 무전/근접 분리는 Phase 2 | Vivox `SetOutputDeviceVolume`은 활성 채널 전체에 걸리는 전역 API — 채널별 분리는 참가자 단위 `SetLocalVolume` 순회 + 신규 참가자 훅이 필요 |
| (c) 닉네임 수정 | **설정 창에서 제외** | [AuthPanel](../../Assets/Scripts/UI/AuthPanel.cs)이 이미 담당하고, `!Auth.IsNetworkConnected`일 때만 편집 가능 → 인게임 설정 창에 넣으면 항상 비활성으로 보인다 |
| (d) 적용 모델 | **즉시 적용.** 저장/취소 버튼 없음, **닫기 + 기본값 복원** 2버튼 | 저장 버튼은 취소 의미를 데려오고, 그러면 값 2벌(적용 중/커밋됨) + 되돌림 재적용 경로가 붙는다. 볼륨·감도는 조절하며 확인하는 항목이라 미리 적용이 본질 |
| (e) 확인 버튼이 필요한 항목 | Phase 2 **그래픽 옵션에만** "적용"(+되돌림) 도입 | 해상도·창모드는 잘못 적용하면 되돌리기 어렵다 — 슬라이더에 미리 붙일 이유가 없다 |
| (e-2) **[적용] 버튼은 두지 않는다 (#796 후속, 결정 (e) 수정)** | 창모드·해상도도 **고르는 즉시 적용**하고, 확인창이 [유지]를 물어본다 | 안전장치는 버튼이 아니라 **확인창의 카운트다운**이 하고 있어, 버튼을 빼도 자동 복귀는 그대로다. 버튼이 벌어주는 것은 "창모드+해상도를 묶어 한 번에 적용"뿐인데, 그 대가로 모든 사람이 매번 한 번 더 눌러야 한다. 둘을 같이 바꾸면 화면 전환과 확인창을 두 번 겪지만, 그것이 버튼을 상시로 두는 값보다 싸다고 봤다 |
| (j) 되돌림 방식 (#796) | **드롭다운 선택 → 즉시 적용 → 확인창 15초 카운트다운 → [유지]/[되돌리기]**. 저장은 [유지]에서만 — 적용만 한 값은 `PlayerPrefs`에 남지 않는다 | 화면이 깨지면 아무것도 눌러 볼 수 없으므로 되돌림을 사람이 아니라 **시간**이 맡아야 한다. 저장을 미루는 것이 두 번째 그물이다 — 확인하지 못한 채 강제 종료해도 다음 실행은 이전 값으로 뜬다 |
| (k) [기본값 복원] 범위 (#796) | **창모드·해상도는 제외, 수직동기화만 포함** | 언어·로봇 색을 뺀 것과 같은 이유다 — 감도를 되돌리려던 사람이 버튼 한 번에 창이 통째로 바뀌고 확인창부터 마주하게 된다. 되돌릴 수단은 바로 위 드롭다운에 있다. 수직동기화는 잘못 돌아가도 화면이 깨지지 않는다 |
| (l) 수직동기화 (#796) | **예외 없이 즉시 적용 토글** — 다른 토글 3개와 같은 모양 | 잘못 켜고 꺼도 화면이 깨지지 않아 [적용]으로 감쌀 이유가 없다 |
| (m) 저장 단위 (#796 후속) | **계정 단위** — 키가 `settings.<계정>.<항목>`이다. 단, **창모드·해상도는 기기 단위**로 남기고 **언어도 제외**한다 | 설정은 기기가 아니라 그 사람의 것이라는 판단은 로봇 색과 같다(#432). 예외 둘의 이유는 서로 다르다 — **창모드·해상도**는 진짜로 기기의 성질이다(모니터가 다른 PC에 로그인하면 없는 해상도가 걸리고, 로그인 순간 확인창 없이 화면이 갈아치워진다). **언어**는 `PlayerPrefLocaleSelector`가 로그인 훨씬 전에 복원하는 값이라, 계정으로 가르면 로그인하는 순간 메뉴 언어가 통째로 바뀐다 — 결정 (k)에서 [기본값 복원]에 언어를 넣지 않은 것과 같은 이유다 |
| (n) 계정 자리 형식 (#796 후속) | 로그인 전('local')에는 **계정 자리를 넣지 않는다** — 키가 `settings.<항목>` 그대로다 | 계정으로 가르기 전에 쓰던 키가 곧 로그인 전 자리가 되어, 갱신해도 이미 맞춰 둔 값을 잃지 않는다. 마이그레이션 코드가 필요 없다 |
| (o) 계정에 값이 없을 때 (#796 후속) | **밑값이 아니라 '지금 값'을 그대로 둔다** (그리고 그 값이 계정 자리에 쓰인다) | 처음 로그인하는 계정에는 아무것도 없는데 밑값으로 떨어뜨리면, 로그인 전에 맞춰 둔 감도·볼륨이 로그인하는 순간 기본값으로 튄다 |
| (f) 저장소 형태 | `GameSettings` **static 클래스** (`Assets/Scripts/Core/`) | 씬 오브젝트·라이프사이클이 필요 없는 로컬 값. [SessionFlow](../../Assets/Scripts/Network/Session/SessionFlow.cs)와 같은 static 진입점 선례. R2(매니저 `static Instance` 금지) 위반이 아니며, R3(App 등록) 기준의 "씬 서비스"도 아니다 |
| (g) 감도 의미 | 설정값은 **배율** — `LookInput × 프리팹 기준값 × 설정 배율` | 프리팹/씬의 직렬화 값을 건드리지 않고, "기준값 × 사용자 취향"으로 의미가 분리된다 |
| (h) 왜곡 구간 음성 음량 | **이 이슈에서 함께 처리** — 음성 음량을 Vivox 전역 API와 **살아 있는 오디오 탭 양쪽**에 적용 | 왜곡 중에는 Vivox 믹스가 죽고 우리 `AudioSource`로 재생돼 전역 API가 통하지 않는다. 새 탭의 기본 `volume`은 1이므로, 방치하면 **음성을 0으로 내려둔 사람도 먹통 이벤트 순간 목소리가 원래 크기로 되살아난다** — 슬라이더 무반응보다 나쁜, 음소거가 저절로 풀리는 동작 |
| (i) 마이크 음소거 (#430) | **입력 장치 뮤트**(`MuteInputDevice`)로 구현 — 송신 모드로 구현하지 않는다. 음소거 중 PTT를 눌러도 음소거가 이기고 **안내만** 띄운다 | 송신 모드(`SetChannelTransmissionModeAsync`)는 PTT가 이미 쓴다 — 그걸로 음소거를 구현하면 무전 키를 누르는 순간 음소거가 풀린다. 입력 장치 뮤트는 PTT 경로와 겹치지 않아 두 상태가 자연히 독립이다(그래서 `SetRadioTransmit`에 가드를 넣지 않는다). 푸시투언뮤트는 도입하지 않는다 |

## 3. 구조

```
GameSettings (Core · static)                     ← PlayerPrefs 읽기/쓰기 + 즉시 적용
   ├─ MouseSensitivity  0.2~3.0 (기본 1.0)  ──▶ PlayerMovement가 매 프레임 읽음 (구독 없음)
   ├─ MasterVolume      0~1     (기본 1.0)  ──▶ AudioListener.volume        ← Unity 소리 전부
   ├─ BgmVolume         0~1     (기본 1.0)  ──▶ BgmPlayer.ApplyVolume()     ← 마스터 아래
   ├─ SfxVolume         0~1     (기본 1.0)  ──▶ SoundManager.SfxVolumeOf()  ← 마스터 아래
   │                                             + OnSfxVolumeChanged ─▶ 도는 루프(발소리·엔진·경보)가 되읽는다
   ├─ VoiceVolume       0~1     (기본 1.0)  ──▶ VivoxManager.ApplyVoiceVolume()
   │                                              ├─ 정상 경로: Vivox 전역 출력 볼륨
   │                                              └─ 왜곡 경로: 살아 있는 오디오 탭 AudioSource.volume
   ├─ MicMuted          bool    (기본 false) ─▶ VivoxManager.ApplyMicMute()   (#430)
   │                                              ├─ Vivox 입력 장치 Mute/Unmute (PTT와 독립)
   │                                              └─ OnMicMutedChanged ─▶ 설정 창 토글 · HUD 아이콘 · 로비 로스터 보고
   ├─ VSync             bool    (기본 true)  ──▶ QualitySettings.vSyncCount    (#796, 즉시 적용)
   └─ WindowMode/Resolution  (기본 = 빌드가 띄운 창)                            (#796)
        ApplyDisplay(mode, res) ──▶ Screen.SetResolution(w, h, mode) + CursorLock.Reassert()  ← 화면만
        KeepDisplay()           ──▶ PlayerPrefs + Save()                                      ← 확인창 [유지]
        확인 대기 중 다시 고르면 드롭다운을 되돌린다 — '이전 값'을 잃지 않게 (결정 (e-2))

SettingsPanel : PanelBase                        ← 슬라이더 3 + 값 표시 + [닫기] [기본값 복원]
DisplayConfirmPanel : PanelBase                  ← 창모드·해상도 확인 (15초 카운트다운, #796)
CreditsPanel : PanelBase                         ← 개발진 명단 ([일반] 탭의 [개발진] 버튼이 연다)
SettingsCanvas.prefab                            ← Title · Lobby · Shop · Main 배치 (확인창도 이 안)
```

> **음량은 셋으로 갈렸다 (2026-08-27).** 믹서가 없어 **재생 지점에서 곱한다** — BGM은 `BgmPlayer`가, 효과음은 `SoundManager`가 창구다. 실효 음량은 `마스터 x 배경음`·`마스터 x 효과음`이고, 음성(Vivox)은 마스터에 걸리지 않으므로 종전대로 자기 슬라이더 하나만 탄다. 자기 `AudioSource`로 직접 트는 쪽(발소리·엔진·경보·폭탄·뽑기)은 `SoundManager.SfxVolumeOf(entry)`를 거쳐야 슬라이더가 걸리고, 계속 도는 루프는 `GameSettings.OnSfxVolumeChanged`를 구독해 슬라이더를 끄는 동안 스스로 되읽는다.

### 저장 자리 (#796 후속)

```
settings.<항목>                       ← 로그인 전 (계정 자리를 넣지 않는다 — 결정 (n))
settings.<계정>.<항목>                ← 로그인 후
settings.playerColor.<계정>.<부위>    ← 색만 로그인 전에도 계정 자리를 쓴다 (#432, 형식 유지)

settings.windowMode / resolutionWidth / resolutionHeight   ← 기기 단위, 계정과 무관 (결정 (m))
```

- 갈아타는 것을 **부르는 곳**은 하나뿐이다 — `CosmeticsSaveService`의 로그인·로그아웃 훅
  (색 저장 예약을 막는 가드 안이라 그 자리가 안전하다). 거기서 계정 자리를 가진 셋을 나란히 부른다:
  `GameSettings.UseAccount` · `CosmeticLoadout.UseAccount`(색·액세서리, #931) ·
  `CosmeticInventory.UseAccount`(보유·토큰, #818).
- **색·액세서리는 이 저장소에 없다 (#931).** 설정이 아니라 계정 단위 외형이라
  [CosmeticLoadout](../../Assets/Scripts/Save/CosmeticLoadout.cs)이 따로 들고 있다 — 설계는
  [player-color.md](player-color.md).
- **언어는 여기 없다.** `PlayerPrefLocaleSelector`가 자기 키로 시작 시 복원한다 — 결정 (m) 참고.

- **적용 방향은 한 방향뿐** — 슬라이더 → `GameSettings` → 각 싱크. 싱크가 설정값을 되쓰는 경로는 없다.
- **감도는 이벤트를 쓰지 않는다.** `PlayerMovement`가 매 프레임 static 프로퍼티를 읽는다(필드 읽기 1회). 구독/해제가 없으므로 플레이어 스폰·씬 전환·오너십 경계에서 구독이 새거나 빠지는 사고가 원천적으로 없다.
- **볼륨은 이벤트가 필요 없다.** 싱크(`AudioListener`·Vivox)가 전역이라 setter에서 바로 적용된다.
- **예외 — 마이크 음소거만 이벤트를 쓴다 (#430).** 적용은 볼륨과 같이 전역 API 한 방향이지만, 이 항목은 **설정 창 토글과 토글 키 두 경로로 바뀌고 UI가 상태를 되읽어야** 한다(한쪽에서 바꾸면 다른 쪽 표시가 따라와야 함). 그래서 `GameSettings.OnMicMutedChanged`가 있고, 구독자는 표시만 갱신한다 — 값의 출처는 여전히 `GameSettings` 하나다. static 이벤트이므로 `Load()`에서 `null`로 리셋한다(도메인 리로드 OFF에서 죽은 구독자가 남는다).
- **시작 시 적용**: `[RuntimeInitializeOnLoadMethod]`로 로드+적용. 도메인 리로드 OFF 대비 static 리셋은 [App](../../Assets/Scripts/Core/App.cs)·`AppBootstrap` 방침과 동일하게 둔다.

### ESC 스택에서의 위치

`SettingsPanel`은 `CanCloseWithESC = true`, `IsStackable = true`, **`IsEscMenu = false`**.
`IsEscMenu`를 켜면 "씬당 진입 메뉴 하나" 규칙에 걸려 [UIManagerBase](../../Assets/Scripts/Core/UIManagerBase.cs)가 에러 로그를 낸다. 스택 패널로 두면 ESC가 `설정 → 일시정지` 순으로 자연히 풀린다.

**커서·입력 정지는 설정 패널이 건드리지 않는다** — [PausePanel](../../Assets/Scripts/UI/Panels/PausePanel.cs)이 이미 로컬 플레이어 입력 정지 + 커서 해제를 대칭으로 처리하고 있고, Title 씬에는 플레이어가 없다.

## 4. 작업 순서

1. **`Assets/Scripts/Core/GameSettings.cs`** — 프로퍼티 3개(setter = 클램프 → `PlayerPrefs.SetFloat` → 즉시 적용), 기본값 상수(`k_` 접두사), `Load()`/`ApplyAll()`/`ResetToDefaults()`, `[RuntimeInitializeOnLoadMethod]` 초기화, static 리셋.
2. **`VivoxManager.SetVoiceVolume(float 0~1)`** — 싱크가 둘이다.
   - **정상 경로**: `VivoxService.Instance.SetOutputDeviceVolume(int)`. 범위는 −50~50 **로그 스케일**이고 실사용 구간이 −10~25이므로 0~1을 대략 **−25~+10**에 매핑한다. 로그인 전 호출을 대비해 값을 보관하고 **채널 참가 시 재적용**한다.
   - **왜곡 경로**: 보관한 값을 `m_distortTaps`의 `AudioSource.volume`에 쓴다. 쓰는 지점이 **두 곳**이다 — ① `SetVoiceVolume`에서 이미 살아 있는 탭들을 순회, ② `ApplyDistortion`에서 탭을 만들어 `m_distortTaps`에 넣는 자리에 `source.volume = m_voiceVolume`. 탭은 왜곡이 켜지는 순간 새로 생성되므로 ①만으로는 이후 생긴 탭이 설정값을 모른다.

   ```csharp
   public void SetVoiceVolume(float volume)   // 0~1
   {
       m_voiceVolume = Mathf.Clamp01(volume);
       if (m_loggedIn)
           VivoxService.Instance.SetOutputDeviceVolume(ToVivoxScale(m_voiceVolume));
       foreach (AudioSource source in m_distortTaps.Values)
           if (source != null) source.volume = m_voiceVolume;
   }
   ```

   `VoiceDistortionProfile.Apply()`는 `pitch`만 건드리므로 `volume` 사용은 충돌하지 않고, `volume`은 곱셈으로 걸려 근접 채널 탭의 3D 거리 감쇠도 망치지 않는다.
3. **`PlayerMovement`** — [L359](../../Assets/Scripts/Player/Movement/PlayerMovement.cs#L359)의 `LookInput * m_mouseSensitivity`에 `* GameSettings.MouseSensitivity`를 곱한다. 직렬화 필드 `m_mouseSensitivity`는 "프리팹 기준값"으로 남기고 툴팁에 설정 배율과의 관계를 적는다.
4. **`Assets/Scripts/UI/Panels/SettingsPanel.cs`** — `PanelBase` 상속. `Slider.onValueChanged` → `GameSettings` 대입, 값 텍스트 갱신. `ClosePanel()`·`OnDestroy()` 오버라이드에서 **`PlayerPrefs.Save()` 1회**(둘 다 `virtual`이라 override로 충분). [기본값 복원] → `ResetToDefaults()` + 슬라이더 값 재동기화.
5. **프리팹·씬 작업** — `Assets/Prefabs/UI/SettingsCanvas.prefab` 신규 → 4씬 배치 / `PauseCanvas.prefab`에 "설정" 버튼 추가 → `App.UI.Current.OpenPanel<SettingsPanel>()` / Title 메뉴에 설정 버튼 추가.

## 5. 함정 (구현 전에 알고 갈 것)

- **마스터 음량은 Vivox 음성에 걸리지 않는다.** Vivox는 자체 믹스로 재생돼 `AudioListener.volume` 밖에 있다 — 음성 슬라이더를 따로 두는 실제 이유.
- **먹통 음성 왜곡 중에는 경로가 뒤집힌다.** [VivoxManager.cs:405](../../Assets/Scripts/Network/Voice/VivoxManager.cs#L405)가 `silenceInChannelAudioMix=true`로 Vivox 믹스를 죽이고 우리 `AudioSource`로 재생하므로, 그 구간에는 `SetOutputDeviceVolume`이 먹지 않는다. **탭 `AudioSource.volume`에 함께 반영하는 것으로 해결한다** (결정 (h) · 작업 2). 남는 차이 하나: 이 경로는 Unity 믹스라 마스터 음량도 함께 걸려 왜곡 중 실효 음량이 `마스터 × 음성`이 된다 — 정상 구간(마스터 무관)과 미묘하게 다르지만 왜곡이 짧은 이벤트라 허용한다.
- **캔버스 Sort Order 충돌** — `PauseCanvas`·`QuitConfirmCanvas`가 모두 100이다. `PauseCanvas`를 복제해 만든 `SettingsCanvas`도 100을 물려받아, Sort Order가 같은 Overlay 캔버스는 Hierarchy 루트 순서로 승부가 갈린다 → 씬마다 위아래가 달라진다(Shop에서 일시정지가 설정 창을 덮는 문제로 실제 발생). 설정은 일시정지 **위**에 겹치는 유일한 창이므로 프리팹에서 **110**으로 고정한다. 클릭 우선순위(`GraphicRaycaster`)도 같은 순서를 따르므로 이걸 고치면 슬라이더가 안 잡히던 문제도 함께 사라진다.
- **`PlayerPrefs`는 계정으로 갈렸지만 창모드·해상도는 아니다 (#796 후속).** MPPM 가상 플레이어끼리 감도·볼륨이 섞이는 문제는 계정 단위 저장으로 풀렸지만, **창모드·해상도는 여전히 기기 단위**라 가상 플레이어가 서로 덮는다. 그래픽 검증은 **혼자 켠 빌드**로 할 것.
- **그래픽 옵션은 에디터에서 검증되지 않는다 (#796).** `Screen.SetResolution`은 에디터 Game 뷰에서 동작하지 않는다 — **빌드로 확인해야 한다.** 또 `PlayerPrefs`는 MPPM 가상 플레이어끼리 공유되므로(계정으로 가른 키는 로봇 색뿐이다) 가상 플레이어로 테스트하면 한쪽 값이 다른 쪽을 덮는다. **혼자 켠 빌드로 확인할 것.**
- **창모드가 바뀌면 OS가 커서 잠금을 푼다 (#796).** `GameSettings.ApplyDisplay`가 적용 직후 [CursorLock](../../Assets/Scripts/Core/CursorLock.cs)`.Reassert()`를 부른다 — 요청 수는 건드리지 않으므로 판정 결과는 그대로다(설정 창이 떠 있는 동안은 계속 풀림). 설정·일시정지를 닫을 때 `PopUnlock`이 한 번 더 적용하므로 그물이 둘이다.
- **`Screen.resolutions`에는 중복이 있다 (#796).** 주사율별로 같은 폭×높이를 여러 번 돌려준다 — `GameSettings.AvailableResolutions`가 폭×높이로 묶어 걷어낸 목록을 준다. 드롭다운은 그것만 쓴다.
- **세로 720 미만은 목록에서 뺀다 (#796).** 모든 캔버스가 기준 1920×1080 · `match=height`라 **배율이 곧 세로 비율**이다 — 640×480이면 0.44배가 되어 본문 26pt가 **11.6px**로 그려지고 UI를 읽을 수 없다. 그 상태에서 되돌리려면 읽히지 않는 설정 창을 봐야 하므로 애초에 고를 수 없게 한다(빌드 검증 중 실제로 밟았다). 목록은 **큰 것부터** 낸다 — 오름차순이면 제일 쓸모없는 640×480이 맨 위에 온다.
  - **캔버스 설정으로는 풀리지 않는다.** `match=width`는 0.33배로 더 나빠지고, `ConstantPixelSize`는 설정 창(800×710)이 화면(640×480)보다 커진다. 기준을 1280×720으로 낮추면 저해상도는 살지만 1920×1080에서 UI 전체가 1.5배가 되어 모든 레이아웃을 다시 잡아야 한다. 픽셀이 없는 것은 배율로 만들 수 없다.
- **수직동기화를 끄면 프레임 상한이 사라진다 (#796).** 매 프레임 비용이 있는 것들이 그만큼 더 돈다 — 강수 화면 마스크(#782)와 차량 위치 풀이(#787)가 대상이다.
- **슬라이더 드래그마다 `PlayerPrefs.Save()`를 부르지 않는다.** `SetFloat`은 메모리, `Save()`는 디스크 쓰기 — 창 닫을 때 1회로 모은다.
- **감도 배율의 상한**을 프리팹 기준값과 곱해 확인한다 — 기준값이 이미 크면 3.0배가 조작 불가 수준이 될 수 있다.
- **로그 스케일 매핑 검증** — Vivox 볼륨은 선형이 아니라, 슬라이더 중앙이 "절반 크기"로 들리지 않는다. 2인 테스트에서 체감으로 매핑 구간을 조정한다.

## 6. 완료 확인 (테스트)

- [ ] 감도 변경 → 즉시 반영, 게임 재시작 후에도 유지
- [ ] 마스터·음성 각각 조절 → 효과음과 음성이 독립적으로 변함 (2인 테스트)
- [ ] ESC → 일시정지 → 설정 → ESC 두 번으로 정상 복귀, 패널 뒤 월드로 입력·시점이 새지 않음
- [ ] Title↔Lobby↔Shop↔Main 전환 후에도 값 유지
- [ ] [기본값 복원] → 세 값이 기본값으로 돌아가고 슬라이더 위치도 함께 갱신
- [ ] 설정 창을 열어둔 채 씬이 전환돼도 값이 저장됨(`OnDestroy` 경로)
- [ ] **음성을 0으로 내린 뒤 먹통 이벤트 발생 → 목소리가 되살아나지 않음** (결정 (h))
- [ ] 먹통 **중에** 음성 슬라이더를 움직여도 즉시 반영됨 (`SetVoiceVolume` 순회 경로)
- [ ] 먹통 중 새로 접속·근접 진입한 참가자의 목소리도 설정값을 따름 (`ApplyDistortion` 경로)
- [ ] 다른 플레이어에게 아무 영향 없음 (전부 로컬)

## 7. 미결 항목

- **#372 코드 변경 공유** — 이 이슈가 `VivoxManager`의 왜곡 탭 경로를 건드린다(작업 2). PR 설명에 명시하고 먹통 이벤트 담당과 공유한다 — 설정 창 이슈에서 왜곡 코드가 바뀌는 게 리뷰에서 예상 밖으로 보이지 않게.
- **무전/근접 분리 볼륨** — Phase 2. 참가자 단위 `SetLocalVolume` 순회 + `ParticipantAddedToChannel` 훅([VivoxManager.cs:217](../../Assets/Scripts/Network/Voice/VivoxManager.cs#L217))으로 신규 참가자 처리가 필요.
- **UI 문자열 로컬라이즈** — 현재 UI 텍스트는 대부분 평문 TMP(로컬라이즈 사용처는 `ItemBase`·`InventoryBarView` 둘). 설정 창 라벨도 관례에 맞춰 평문으로 두고, 로컬라이즈 일괄 작업 때 함께 처리한다.

---
*작성: 2026-07-28 · 근거: #225 · 현행 코드(PanelBase/UIManagerBase/PausePanel/PlayerMovement/VivoxManager/AuthPanel) 조사 + [Vivox 오디오 레벨 문서](https://docs.unity.com/en-us/vivox-unity/developer-guide/in-game-audio-control/in-game-control-audio-levels)*
