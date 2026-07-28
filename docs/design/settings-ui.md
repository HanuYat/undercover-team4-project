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
- 그래픽 옵션(창모드·해상도·품질)
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
| (f) 저장소 형태 | `GameSettings` **static 클래스** (`Assets/Scripts/Core/`) | 씬 오브젝트·라이프사이클이 필요 없는 로컬 값. [SessionFlow](../../Assets/Scripts/Network/SessionFlow.cs)와 같은 static 진입점 선례. R2(매니저 `static Instance` 금지) 위반이 아니며, R3(App 등록) 기준의 "씬 서비스"도 아니다 |
| (g) 감도 의미 | 설정값은 **배율** — `LookInput × 프리팹 기준값 × 설정 배율` | 프리팹/씬의 직렬화 값을 건드리지 않고, "기준값 × 사용자 취향"으로 의미가 분리된다 |
| (h) 왜곡 구간 음성 음량 | **이 이슈에서 함께 처리** — 음성 음량을 Vivox 전역 API와 **살아 있는 오디오 탭 양쪽**에 적용 | 왜곡 중에는 Vivox 믹스가 죽고 우리 `AudioSource`로 재생돼 전역 API가 통하지 않는다. 새 탭의 기본 `volume`은 1이므로, 방치하면 **음성을 0으로 내려둔 사람도 먹통 이벤트 순간 목소리가 원래 크기로 되살아난다** — 슬라이더 무반응보다 나쁜, 음소거가 저절로 풀리는 동작 |

## 3. 구조

```
GameSettings (Core · static)                     ← PlayerPrefs 읽기/쓰기 + 즉시 적용
   ├─ MouseSensitivity  0.2~3.0 (기본 1.0)  ──▶ PlayerMovement가 매 프레임 읽음 (구독 없음)
   ├─ MasterVolume      0~1     (기본 1.0)  ──▶ AudioListener.volume
   └─ VoiceVolume       0~1     (기본 1.0)  ──▶ VivoxManager.SetVoiceVolume()
                                                  ├─ 정상 경로: Vivox 전역 출력 볼륨
                                                  └─ 왜곡 경로: 살아 있는 오디오 탭 AudioSource.volume

SettingsPanel : PanelBase                        ← 슬라이더 3 + 값 표시 + [닫기] [기본값 복원]
SettingsCanvas.prefab                            ← Title · Lobby · Shop · Main 배치
```

- **적용 방향은 한 방향뿐** — 슬라이더 → `GameSettings` → 각 싱크. 싱크가 설정값을 되쓰는 경로는 없다.
- **감도는 이벤트를 쓰지 않는다.** `PlayerMovement`가 매 프레임 static 프로퍼티를 읽는다(필드 읽기 1회). 구독/해제가 없으므로 플레이어 스폰·씬 전환·오너십 경계에서 구독이 새거나 빠지는 사고가 원천적으로 없다.
- **볼륨은 이벤트가 필요 없다.** 싱크(`AudioListener`·Vivox)가 전역이라 setter에서 바로 적용된다.
- **시작 시 적용**: `[RuntimeInitializeOnLoadMethod]`로 로드+적용. 도메인 리로드 OFF 대비 static 리셋은 [App](../../Assets/Scripts/Core/App.cs)·`AppBootstrap` 방침과 동일하게 둔다.

### ESC 스택에서의 위치

`SettingsPanel`은 `CanCloseWithESC = true`, `IsStackable = true`, **`IsEscMenu = false`**.
`IsEscMenu`를 켜면 "씬당 진입 메뉴 하나" 규칙에 걸려 [UIManagerBase](../../Assets/Scripts/Core/UIManagerBase.cs)가 에러 로그를 낸다. 스택 패널로 두면 ESC가 `설정 → 일시정지` 순으로 자연히 풀린다.

**커서·입력 정지는 설정 패널이 건드리지 않는다** — [PausePanel](../../Assets/Scripts/UI/PausePanel.cs)이 이미 로컬 플레이어 입력 정지 + 커서 해제를 대칭으로 처리하고 있고, Title 씬에는 플레이어가 없다.

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
3. **`PlayerMovement`** — [L359](../../Assets/Scripts/Player/PlayerMovement.cs#L359)의 `LookInput * m_mouseSensitivity`에 `* GameSettings.MouseSensitivity`를 곱한다. 직렬화 필드 `m_mouseSensitivity`는 "프리팹 기준값"으로 남기고 툴팁에 설정 배율과의 관계를 적는다.
4. **`Assets/Scripts/UI/SettingsPanel.cs`** — `PanelBase` 상속. `Slider.onValueChanged` → `GameSettings` 대입, 값 텍스트 갱신. `ClosePanel()`·`OnDestroy()` 오버라이드에서 **`PlayerPrefs.Save()` 1회**(둘 다 `virtual`이라 override로 충분). [기본값 복원] → `ResetToDefaults()` + 슬라이더 값 재동기화.
5. **프리팹·씬 작업** — `Assets/Prefabs/UI/SettingsCanvas.prefab` 신규 → 4씬 배치 / `PauseCanvas.prefab`에 "설정" 버튼 추가 → `App.UI.Current.OpenPanel<SettingsPanel>()` / Title 메뉴에 설정 버튼 추가.

## 5. 함정 (구현 전에 알고 갈 것)

- **마스터 음량은 Vivox 음성에 걸리지 않는다.** Vivox는 자체 믹스로 재생돼 `AudioListener.volume` 밖에 있다 — 음성 슬라이더를 따로 두는 실제 이유.
- **먹통 음성 왜곡 중에는 경로가 뒤집힌다.** [VivoxManager.cs:405](../../Assets/Scripts/Network/VivoxManager.cs#L405)가 `silenceInChannelAudioMix=true`로 Vivox 믹스를 죽이고 우리 `AudioSource`로 재생하므로, 그 구간에는 `SetOutputDeviceVolume`이 먹지 않는다. **탭 `AudioSource.volume`에 함께 반영하는 것으로 해결한다** (결정 (h) · 작업 2). 남는 차이 하나: 이 경로는 Unity 믹스라 마스터 음량도 함께 걸려 왜곡 중 실효 음량이 `마스터 × 음성`이 된다 — 정상 구간(마스터 무관)과 미묘하게 다르지만 왜곡이 짧은 이벤트라 허용한다.
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
- **무전/근접 분리 볼륨** — Phase 2. 참가자 단위 `SetLocalVolume` 순회 + `ParticipantAddedToChannel` 훅([VivoxManager.cs:217](../../Assets/Scripts/Network/VivoxManager.cs#L217))으로 신규 참가자 처리가 필요.
- **UI 문자열 로컬라이즈** — 현재 UI 텍스트는 대부분 평문 TMP(로컬라이즈 사용처는 `ItemBase`·`InventoryBarView` 둘). 설정 창 라벨도 관례에 맞춰 평문으로 두고, 로컬라이즈 일괄 작업 때 함께 처리한다.

---
*작성: 2026-07-28 · 근거: #225 · 현행 코드(PanelBase/UIManagerBase/PausePanel/PlayerMovement/VivoxManager/AuthPanel) 조사 + [Vivox 오디오 레벨 문서](https://docs.unity.com/en-us/vivox-unity/developer-guide/in-game-audio-control/in-game-control-audio-levels)*
