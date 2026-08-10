using UnityEngine;

/// <summary>
/// 1인칭 시점 — 마우스 입력을 받아 몸통 yaw와 카메라 pitch를 돌리고, 카메라의 높이·자세를 매 프레임 잡는다.
/// 오너 로컬 전용. (#105, #216, #225, #236, #252, #348)
///
/// 회전과 카메라 자세를 한 컴포넌트에 둔 이유는 <c>m_pitch</c>·<c>m_downYaw</c>·<c>m_downLookTaken</c>를
/// 양쪽이 <b>읽고 쓰기</b> 때문이다 — 입력은 <see cref="HandleLook"/>이 넣고, 쓰러진 동안의 강제 자세와
/// 기상 시 범위 복귀는 <see cref="UpdateCameraPose"/>가 넣는다. 나누면 이 셋을 두 컴포넌트가 주고받게 된다.
///
/// 이름이 Camera가 아니라 Look인 이유: <see cref="HandleLook"/>이 카메라만이 아니라 <b>몸통도</b> 돌린다
/// (평상시 yaw는 transform 회전이다 — 쓰러진 동안만 카메라 로컬로 돌린다).
///
/// 실행은 <see cref="PlayerMovement"/>가 부른다(자체 Update 없음) — 몸통 yaw가 이동 방향의 기준이라
/// 같은 프레임에서 시점이 이동보다 먼저 돌아야 하고, 추종 중(<see cref="PlayerTowedMotion"/>)에는
/// 호출 순서가 또 달라지기 때문. Unity의 컴포넌트 실행 순서에 맡기면 이 보장이 깨진다.
/// </summary>
[RequireComponent(typeof(PlayerInputHandler))]
public class PlayerLook : MonoBehaviour
{
    [Header("1인칭 시점")]
    [SerializeField]
    private Camera m_playerCamera;

    [Tooltip("프리팹 기준 감도 — 실제 감도는 여기에 설정 창의 감도 배율(GameSettings.MouseSensitivity)을 곱한 값이다 (#225)")]
    [SerializeField]
    private float m_mouseSensitivity = 1f;

    [Tooltip("마우스 회전 스무딩 강도 — 클수록 반응이 빠르고 덜 부드러움. 0이면 스무딩 없음(원시 입력). (#216)")]
    [SerializeField]
    private float m_lookSmoothing = 20f;

    [SerializeField]
    private float m_minPitch = -80f;

    [SerializeField]
    private float m_maxPitch = 80f;

    [SerializeField]
    private Transform m_ownBodyRoot; // 내 카메라에서만 안 보이게 할 캐릭터 몸(머리) 루트

    [Header("다운(무력화) 시점")]
    [Tooltip("다운 중 카메라를 낮출 바닥 근처 높이(m)")]
    [SerializeField] private float m_downCamHeight = 0.35f;

    [Tooltip("다운 중 카메라 피치(양수=아래, 음수=위). 바닥에서 살짝 위를 보게 함")]
    [SerializeField] private float m_downCamPitch = -20f;

    [Tooltip("서기↔다운 시점 전환 보간 속도")]
    [SerializeField] private float m_camPoseLerpSpeed = 8f;

    // 쓰러진 동안에도 주변을 볼 수 있게 시야만 돌린다 (#252) — 몸은 누운 채 그대로다.
    [Tooltip("쓰러진 동안(다운·기절) 시야를 좌우로 돌릴 수 있는 범위(±도). 몸을 돌리지 않으므로 목이 꺾여 보이지 않을 만큼만 준다")]
    [SerializeField] private float m_downYawRange = 100f;

    [Tooltip("쓰러진 동안 시야 피치 하한(음수=위). 바닥에 누워 있으니 위로는 넉넉히 열어 둔다")]
    [SerializeField] private float m_downMinPitch = -80f;

    [Tooltip("쓰러진 동안 시야 피치 상한(양수=아래). 아래로는 바닥밖에 없어 좁게 잡는다")]
    [SerializeField] private float m_downMaxPitch = 20f;

    [Header("감정표현 시점 (#219)")]
    [Tooltip("감정표현 재생 중 카메라를 뒤로 뺄 거리(m)")]
    [SerializeField] private float m_emoteCamDistance = 2.5f;

    [Tooltip("감정표현 재생 중 카메라를 위로 올릴 높이(m)")]
    [SerializeField] private float m_emoteCamHeight = 0.4f;

    [Tooltip("1인칭↔감정표현 시점 전환 보간 속도")]
    [SerializeField] private float m_emoteCamLerpSpeed = 6f;

    [Tooltip("3인칭 카메라가 벽을 파고들지 않게 띄울 반경(m)")]
    [SerializeField] private float m_emoteCamProbeRadius = 0.25f;

    [Tooltip("3인칭 카메라 충돌 판정에 쓸 레이어 — 플레이어·트리거는 빼 둘 것")]
    [SerializeField] private LayerMask m_emoteCamCollision = ~0;

    private PlayerMovement m_movement;       // 라운드 종료 freeze 판정을 빌린다 (#107 예외 포함)
    private PlayerInputHandler m_inputHandler;
    private PlayerIncapacitation m_incapacitation; // 다운(무력화) 중 시점 처리 분기 (#105, #252)
    private PlayerCrouch m_crouch;           // 앉기 중 카메라 높이 조정 (#236)
    private PlayerJump m_jump;               // 공중에서는 앉기 시점 변화를 얼린다 (#189)
    private PlayerHandView m_handView;       // 3인칭 동안 1인칭 팔 감추기 (#219, #576)
    private PlayerSpectateCamera m_spectate; // 사망 관전 오빗 (#576)

    private float m_pitch;
    private Vector2 m_smoothedLook; // 지수 감쇠로 부드럽게 만든 시점 입력 — 저속 픽셀 양자화 지터 완화 (#216)
    private float m_standCamHeight; // 평소(서기) 카메라 높이 — 프리팹 초기값에서 캡처 (#105)
    private Vector2 m_camBaseLateral; // 카메라 로컬 x·z 기준값 — 흔들림을 되돌릴 자리 (#477)
    private float m_camCrouchDrop;  // 시점에 실제로 반영 중인 앉기 하강량 — 공중에서는 얼린다 (#189)
    private float m_downCamBlend;   // 서기 시점(0) ↔ 다운 시점(1) 보간 진행도 (#105)
    private float m_downYaw;        // 쓰러진 동안 누적한 시야 좌우 각도 — 몸 회전이 아니라 카메라 로컬 (#252)
    private bool m_downLookTaken;   // 쓰러진 뒤 플레이어가 시선을 직접 움직였는가 — 그 순간부터 강제 피치를 놓는다
    private bool m_lookSuspended;   // 시점 회전만 멈춘 상태 — 감정표현 휠 조준 중 (#219)
    private bool m_emoteView;       // 감정표현 3인칭 시점이 요청됐는가 (#219)
    private float m_emoteCamBlend;  // 1인칭(0) ↔ 3인칭(1) 보간 진행도
    private float m_emoteYaw;       // 감정표현 중 누적한 카메라 좌우 각 — 몸은 돌리지 않는다
    private bool m_spectateView;    // 사망 관전이 요청됐는가 — 오빗 각 진입/이탈 판정 (#576)
    private bool m_spectateShown;   // 관전 표시(내 몸·1인칭 팔)가 켜져 있는가 — 블렌드가 문턱을 넘은 뒤에 따라온다
    private int m_ownBodyLayer = -1; // OwnBody 레이어 번호 캐시 (-1 = 아직 조회 전)

    /// <summary>시선 pitch(도, +아래/-위) — PlayerHeadLook이 머리 본 회전에 사용한다. (#348)</summary>
    public float Pitch => m_pitch;

    // 다운(무력화) 중 여부 — 무력화 컴포넌트가 없으면(테스트 구성 등) 항상 false
    // 자세 판정이라 IsIncapacitated가 아니라 IsProne을 본다 — 외곽 린치(#371 후속)는 서서 맞는
    // 무력화라 바닥 시점·회전 잠금이 걸리면 안 된다. 이름은 쓰임(쓰러졌는가)에 맞췄다.
    private bool IsProne => m_incapacitation != null && m_incapacitation.IsProne;

    // 앉기 블렌딩으로 머리가 내려간 높이(m) — 카메라를 같은 만큼 낮춘다 (#236)
    private float CrouchHeadDrop => m_crouch != null ? m_crouch.HeadDrop : 0f;

    private void Awake()
    {
        m_movement = GetComponent<PlayerMovement>();
        m_inputHandler = GetComponent<PlayerInputHandler>();
        m_incapacitation = GetComponent<PlayerIncapacitation>();
        m_crouch = GetComponent<PlayerCrouch>();
        m_jump = GetComponent<PlayerJump>();
        m_handView = GetComponent<PlayerHandView>();
        m_spectate = GetComponent<PlayerSpectateCamera>();

        if (m_playerCamera != null)
        {
            // 프리팹 배치값을 기준으로 기억한다. y만 쓰던 것에 x·z를 더한 이유는 흔들림(#477) 때문이다 —
            // 오프셋을 얹으려면 매 프레임 되돌아갈 자리가 있어야 하고, 없으면 누적돼 시점이 밀린다.
            m_standCamHeight = m_playerCamera.transform.localPosition.y; // 서기 시점 높이 기준값
            m_camBaseLateral = new Vector2(
                m_playerCamera.transform.localPosition.x,
                m_playerCamera.transform.localPosition.z
            );
        }
    }

    /// <summary>
    /// 소유권에 따라 시점 표시를 맞춘다 — <see cref="PlayerMovement"/>가 OnNetworkSpawn에서 부른다.
    /// 남의 플레이어 카메라는 끄고, 내 몸은 내 카메라에서만 안 보이게 레이어를 바꾼다.
    /// </summary>
    public void ApplyOwnerView(bool isOwner)
    {
        if (!isOwner)
        {
            if (m_playerCamera != null)
            {
                m_playerCamera.gameObject.SetActive(false);
            }

            return;
        }

        if (m_ownBodyRoot != null)
        {
            SetLayerRecursively(m_ownBodyRoot, LayerMask.NameToLayer("OwnBody")); // 내 카메라에서만 안 보이게
        }
    }

    /// <summary>
    /// 감정표현 3인칭 시점을 켜고 끈다 — 재생 중에만 켠다. (#219)
    ///
    /// 1인칭에서는 <see cref="ApplyOwnerView"/>가 내 몸을 OwnBody 레이어로 옮겨 내 카메라에서
    /// 걷어내므로, 이걸 켜지 않으면 감정표현을 발동해도 <b>내 화면에는 아무 일도 일어나지 않는다</b>.
    ///
    /// 컬링 마스크를 되살리는 것과 카메라를 빼는 것을 같은 진입점에 묶는 이유: 둘 중 하나만
    /// 걸리면 "몸은 보이는데 얼굴 안쪽이 보이는" 화면이나 "뒤로 빠졌는데 아무것도 없는" 화면이 된다.
    /// </summary>
    public void SetEmoteView(bool active)
    {
        if (m_emoteView == active)
            return;

        m_emoteView = active;
        ApplyThirdPersonView();
    }

    // 관전 진입/이탈 — 오빗 각만 여기서 잡는다. 표시(몸·팔)는 아래 ShowSpectateView가 블렌드를 보고 켠다. (#576)
    private void SetSpectateView(bool active)
    {
        if (m_spectate == null || m_spectateView == active)
            return;

        m_spectateView = active;

        m_spectate.SetSpectating(
            active,
            m_playerCamera != null ? m_playerCamera.transform.eulerAngles.y : transform.eulerAngles.y
        );
    }

    // 3인칭 표시로 보는 블렌드 문턱 — 이 아래에서는 카메라가 아직 몸 안에 있는 셈이라 1인칭 표시를 쓴다.
    private const float k_spectateShowBlend = 0.2f;

    /// <summary>
    /// 관전 표시를 켜고 끈다 — <b>요청 상태가 아니라 블렌드 진행도</b>로 판정한다. (#576)
    ///
    /// 요청 상태로 걸면 부활 순간 표시만 먼저 1인칭으로 돌아가고 카메라는 1초에 걸쳐 따라온다 —
    /// 그동안 몸은 사라졌는데 카메라는 아직 뒤에 있어 허공에 1인칭 팔만 뜬 화면이 된다.
    /// 진입 쪽도 같은 이유로 문턱을 넘긴 뒤에 켠다(카메라가 아직 머리 안에 있을 때 몸을 되살리면
    /// 자기 얼굴 안쪽이 화면을 덮는다).
    /// </summary>
    private void ShowSpectateView(bool shown)
    {
        if (m_spectateShown == shown)
            return;

        m_spectateShown = shown;
        ApplyThirdPersonView();
    }

    /// <summary>
    /// 3인칭에 딸린 표시 둘을 함께 맞춘다 — 내 몸 컬링 복원과 1인칭 팔 숨김.
    ///
    /// 한 곳에 묶는 이유는 <see cref="SetEmoteView"/> 주석 그대로다: 둘 중 하나만 걸리면 "뒤로
    /// 빠졌는데 아무것도 없는" 화면이거나 "전신 위에 팔 한 쌍이 떠 있는" 화면이 된다.
    /// 3인칭 사용처가 감정표현·사망 관전 둘로 늘어(#576) 각자 끄고 켜면 겹치는 순간 한쪽이 다른
    /// 쪽을 되돌린다 — 죽으면서 감정표현이 끊기면 그쪽 종료 처리가 방금 감춘 팔을 되살린다.
    /// </summary>
    private void ApplyThirdPersonView()
    {
        bool thirdPerson = m_emoteView || m_spectateShown;

        // 1인칭 팔은 카메라 자식이라 그냥 두면 전신이 보이는 화면에 붙어 따라온다.
        m_handView?.SetViewmodelVisible(!thirdPerson);

        if (m_playerCamera == null)
            return;

        if (m_ownBodyLayer < 0)
            m_ownBodyLayer = LayerMask.NameToLayer("OwnBody");

        if (m_ownBodyLayer < 0)
            return; // 레이어가 없는 구성(테스트 씬 등) — 카메라만 빠지고 몸은 안 보인다

        int mask = 1 << m_ownBodyLayer;
        if (thirdPerson)
            m_playerCamera.cullingMask |= mask;
        else
            m_playerCamera.cullingMask &= ~mask;
    }

    /// <summary>
    /// 시점 회전을 잠시 멈춘다 — 감정표현 휠처럼 <b>같은 마우스 입력을 다른 용도로 쓰는</b> UI가 켠다. (#219)
    ///
    /// 입력 자체를 끄는 <see cref="PlayerInputHandler.SetSuspended"/>로는 이 일을 할 수 없다.
    /// 그쪽은 액션을 통째로 비활성화하므로 휠을 여는 홀드 입력까지 끊겨 휠이 그 순간 닫힌다.
    /// 여기서 막는 것은 <b>시점 회전 하나뿐</b>이고, 마우스 델타는 휠 조준이 계속 읽어 간다.
    /// </summary>
    public void SetLookSuspended(bool suspended)
    {
        m_lookSuspended = suspended;
    }

    /// <summary>
    /// 마우스 입력으로 시점을 돌린다 — 평상시엔 몸통 yaw + 카메라 pitch, 쓰러진 동안엔 카메라 로컬만. (#216, #252)
    /// </summary>
    public void HandleLook()
    {
        // 라운드 종료 freeze·커서 해제 시엔 시점 회전을 막는다 — 마우스 이동이 화면을 돌리면 안 된다 (#352).
        // 쓰러진 동안(다운·기절)은 열어 둔다 (#252) — 몸은 못 움직여도 주변은 볼 수 있어야 한다.
        // 감정표현 휠이 열려 있는 동안도 막는다 (#219) — 같은 마우스 이동이 칸을 고르는 조준이라,
        // 화면까지 함께 돌면 고르는 내내 시점이 휩쓸린다.
        if ((m_movement != null && m_movement.IsRoundOver) || CursorLock.IsUnlocked || m_lookSuspended)
        {
            m_smoothedLook = Vector2.zero; // 재개 시 잠긴 동안의 스무딩 잔여값으로 튀지 않도록 초기화 (#216)
            return;
        }

        Vector2 look = m_inputHandler.LookInput * m_mouseSensitivity * GameSettings.MouseSensitivity;

        // 프레임률 독립 지수 감쇠 — 느린 회전 시 정수 픽셀 delta(0/1/0/1…)로 생기는 계단 지터를 완만하게 한다.
        // 감쇠 계수 0이면 원시 입력을 그대로 적용(스무딩 없음). (#216)
        float t = m_lookSmoothing <= 0f ? 1f : 1f - Mathf.Exp(-m_lookSmoothing * Time.deltaTime);
        m_smoothedLook = Vector2.Lerp(m_smoothedLook, look, t);

        // 사망 관전 중에는 몸도 시야 각도도 아닌 오빗 각을 돌린다 (#576).
        // 아래 쓰러진 자세 분기보다 먼저 봐야 한다 — 사망도 IsProne이라, 순서가 뒤면 바닥 시점이 입력을 먼저 먹는다.
        if (m_spectateView)
        {
            m_spectate.AddLook(m_smoothedLook);
            return;
        }

        // 쓰러져 있으면 몸을 돌리지 않는다 (#252) — transform을 돌리면 누운 캐릭터가 바닥에서
        // 제자리 회전하는 그림이 되고, 그건 다른 플레이어 화면에도 그대로 보인다.
        // 좌우는 카메라 로컬 각도에 누적하고(범위 제한), 위아래는 누운 자세용 범위로 잡는다.
        if (IsProne)
        {
            if (m_smoothedLook.sqrMagnitude > 0.0001f)
                m_downLookTaken = true; // 이 순간부터 시선은 플레이어 것 — 바닥 시점 강제를 놓는다

            m_downYaw = Mathf.Clamp(
                m_downYaw + m_smoothedLook.x, -m_downYawRange, m_downYawRange);
            m_pitch = Mathf.Clamp(m_pitch - m_smoothedLook.y, m_downMinPitch, m_downMaxPitch);
            return;
        }

        // 감정표현 중에는 몸을 돌리지 않는다 (#219) — 춤추는 중에 몸통이 돌면 클립이 제자리
        // 회전하는 그림이 되고 그건 남의 화면에도 그대로 간다. 쓰러진 동안과 같은 처리다.
        // 마우스를 움직여도 감정표현이 취소되면 안 되므로 여기서 취소를 걸지 않는다.
        if (m_emoteView)
        {
            m_emoteYaw += m_smoothedLook.x; // 3인칭은 한 바퀴 돌 수 있어야 하므로 범위를 두지 않는다
            m_pitch = Mathf.Clamp(m_pitch - m_smoothedLook.y, m_minPitch, m_maxPitch);
            return;
        }

        transform.Rotate(Vector3.up * m_smoothedLook.x);

        m_pitch = Mathf.Clamp(m_pitch - m_smoothedLook.y, m_minPitch, m_maxPitch);
    }

    // 카메라 위치(높이)와 피치를 적용한다. 다운 중에는 바닥 근처 높이 + 상방 시선으로 부드럽게 눕히고,
    // 평소에는 서기 높이에서 시선 입력(m_pitch)을 그대로 반영한다. 구조되면 원위치로 복귀한다. (#105)
    // 앉기 중이면 서기 높이를 머리가 내려간 만큼 낮춘 값으로 대체한다. (#236)
    /// <summary>카메라 높이·피치를 매 프레임 적용한다 — 다운 시 바닥 시점, 앉기 시 하강. (#105, #236)</summary>
    public void UpdateCameraPose()
    {
        if (m_playerCamera == null) return;

        float lerp = m_camPoseLerpSpeed * Time.deltaTime;
        bool downed = IsProne;

        // 사망 관전 시점 — 기능 정지(Die) 동안만 켠다 (#576). 기절·매달기·납치처럼 스스로 풀리는
        // 무력화는 짧고 곧 일어나므로 지금의 바닥 시점을 그대로 둔다.
        SetSpectateView(m_incapacitation != null && m_incapacitation.IsDead);

        m_downCamBlend = Mathf.Lerp(m_downCamBlend, downed ? 1f : 0f, lerp);

        // 공중에서는 앉기에 따른 시점 높이 변화를 얼린다 (#189).
        // 몸이 웅크리는 건 다리를 접는 동작이지 머리가 내려가는 게 아닌데, 시점을 같이 내리면
        // 상승 중에 카메라만 0.8m 꺼져 발은 계속 오르는데도 점프 힘이 죽은 것처럼 보인다.
        // (측정: 발 0.45→0.73m 상승 구간에서 카메라 월드 높이는 2.05→1.59m로 하강)
        // 이륙 시점의 자세를 그대로 유지하므로 앉은 채 뛰면 앉은 시점, 서서 뛰면 선 시점으로 난다.
        //
        // 지상에서는 CrouchHeadDrop(PlayerCrouch가 k_blendDuration으로 블렌딩한 값)을 같은 속도로
        // 쫓아가므로 추가 지연이 붙지 않는다 — "카메라를 한 번 더 감쇠하지 않는다"는 #236 취지 유지.
        if (m_crouch == null)
        {
            m_camCrouchDrop = 0f;
        }
        else if (m_jump == null || !m_jump.IsAirborne)
        {
            m_camCrouchDrop = Mathf.MoveTowards(
                m_camCrouchDrop,
                CrouchHeadDrop,
                m_crouch.HeadDropRate * Time.deltaTime
            );
        }

        float uprightHeight = m_standCamHeight - m_camCrouchDrop;

        // 세 축을 전부 기준값에서 다시 만든다 — 읽어서 y만 덮어쓰면 x·z가 지난 프레임 값을 이어받아,
        // 아래 흔들림 오프셋이 매 프레임 누적돼 시점이 옆으로 밀린 채 돌아오지 않는다 (#477).
        Vector3 localPos = new Vector3(
            m_camBaseLateral.x,
            Mathf.Lerp(uprightHeight, m_downCamHeight, m_downCamBlend),
            m_camBaseLateral.y
        );

        // 쓰러지는 동안 피치를 바닥 시점으로 눕힌다 — 단 플레이어가 마우스를 움직인 뒤에는 놓는다 (#252).
        // 계속 강제하면 올려다본 각도가 매 프레임 되돌아가 시야 조작이 먹지 않는다.
        if (downed && !m_downLookTaken)
        {
            m_pitch = Mathf.Lerp(m_pitch, m_downCamPitch, lerp);
        }

        // 일어나면 시야 좌우 각도를 0으로 되돌린다 — 몸을 그 방향으로 돌리지는 않는다.
        // 기상 모션이 정해진 방향으로 일어나므로 몸을 순간 회전시키면 모션과 어긋난다.
        if (!downed)
        {
            m_downYaw = Mathf.Lerp(m_downYaw, 0f, lerp);
            m_downLookTaken = false;
            m_pitch = Mathf.Clamp(m_pitch, m_minPitch, m_maxPitch); // 누운 자세용 범위에서 서기 범위로 복귀
        }

        // 흔들림은 마지막에 최종 포즈 위에 얹는다 (#477) — 밖에서 카메라 transform을 직접 흔들면
        // 이 메서드가 매 프레임 localPosition·localEulerAngles를 덮어써 그 프레임에 지워진다.
        // 그래서 조립 지점을 여기 하나로 두고, 밖에서는 강도만 넘긴다.
        //
        // <b>오프셋은 위에서 만든 기준 포즈에 더해 한 번만 대입한다</b> — transform을 읽어 더하면
        // (`localPosition += ...`) 되돌아갈 자리가 없어 매 프레임 누적된다. 1인칭 팔이 같은 흔들림을
        // m_handBasePos에서 다시 만드는 것(PlayerHandView.UpdateHandPose)과 같은 이유다.

        // 감정표현 3인칭 — 카메라를 시선 뒤쪽으로 뺀다. (#219)
        // 여기서 조립하는 이유는 흔들림(#477)과 같다: 이 메서드가 매 프레임 localPosition을
        // 통째로 대입하므로 밖에서 얹은 오프셋은 그 프레임에 지워진다.
        // 쓰러지면 다운 시점이 이긴다 — 서버가 감정표현을 끊어 주지만 그 값이 돌아오기까지 왕복이 걸리고,
        // 그 사이 두 블렌드가 겹치면 카메라가 다운 높이와 3인칭 붐 사이 엉뚱한 자리로 간다.
        m_emoteCamBlend = Mathf.Lerp(m_emoteCamBlend, m_emoteView && !downed ? 1f : 0f, m_emoteCamLerpSpeed * Time.deltaTime);

        if (!m_emoteView && m_emoteCamBlend < 0.01f)
        {
            m_emoteYaw = 0f; // 1인칭으로 완전히 돌아온 뒤에만 각도를 버린다 — 도중에 버리면 화면이 튄다
        }
        else if (m_emoteCamBlend > 0.001f)
        {
            // 붐은 카메라가 보는 방향 기준이다 — 몸통이 아니라 m_emoteYaw를 축으로 돈다.
            Vector3 boom = Quaternion.Euler(0f, m_emoteYaw, 0f)
                * new Vector3(0f, m_emoteCamHeight, -m_emoteCamDistance);

            // 벽을 파고들지 않게 당긴다. SphereCast 1회로만 처리한다 — 맵 교체가 예정돼 있어
            // 여기서 완벽한 충돌 대응을 만들 이유가 없다.
            Vector3 pivot = transform.TransformPoint(localPos);
            Vector3 direction = transform.TransformDirection(boom);
            float distance = direction.magnitude;
            if (distance > 0.001f)
            {
                direction /= distance;
                if (Physics.SphereCast(pivot, m_emoteCamProbeRadius, direction, out RaycastHit hit,
                        distance, m_emoteCamCollision, QueryTriggerInteraction.Ignore))
                {
                    boom = boom.normalized * Mathf.Max(hit.distance - m_emoteCamProbeRadius, 0f);
                }
            }

            localPos += boom * m_emoteCamBlend;
        }

        // 좌우 각은 쓰러진 동안(m_downYaw)과 감정표현 중(m_emoteYaw) 각각 쓰이며 동시에 켜지지 않는다.
        Vector3 euler = new Vector3(m_pitch, m_downYaw + m_emoteYaw * m_emoteCamBlend, 0f);

        if (m_shakeIntensity > 0.001f)
        {
            EvaluateShake(out Vector3 shakeEuler, out Vector3 shakeOffset);
            localPos += shakeOffset;
            euler += shakeEuler;
        }

        Quaternion localRot = Quaternion.Euler(euler);

        // 사망 관전 — 시체를 도는 3인칭으로 갈아탄다 (#576). 여기서 얹는 이유는 위 둘과 같다:
        // 카메라 포즈를 통째로 대입하는 곳이 이 메서드 하나라, 밖에서 만들면 그 프레임에 지워진다.
        //
        // 피벗이 루트가 아니라 시체(골반)라 <b>월드에서 만들어 로컬로 되돌린다</b> — 래그돌 비행
        // 중에는 루트가 제자리에 남고 yaw만 몸을 따라가므로(PlayerRagdoll의 FollowBodyYaw),
        // 루트 기준으로 잡으면 날아가는 내 몸을 화면이 놓친다.
        if (m_spectate != null)
        {
            float spectateBlend = m_spectate.Tick(); // 관전 중이 아니어도 불러야 이탈 보간이 진행된다
            ShowSpectateView(spectateBlend > k_spectateShowBlend);

            if (spectateBlend > 0.001f
                && m_spectate.TryGetPose(out Vector3 spectatePos, out Quaternion spectateRot))
            {
                localPos = Vector3.Lerp(localPos, transform.InverseTransformPoint(spectatePos), spectateBlend);
                localRot = Quaternion.Slerp(localRot, Quaternion.Inverse(transform.rotation) * spectateRot, spectateBlend);
            }
        }

        m_playerCamera.transform.localPosition = localPos;
        m_playerCamera.transform.localRotation = localRot;
    }

    // ---- 카메라 흔들림 (#477) ----

    // 감전 경련의 진폭. 큰 충격이 아니라 '떨림'이라 작게 잡는다 — 5초 내내 흔들리므로 키우면 멀미가 난다.
    // 급박함은 진폭이 아니라 주파수로 벌고, 그 주파수와 파형은 ShockShake가 손과 공유한다.
    private const float k_shakeDegrees = 1.6f;
    private const float k_shakeOffset = 0.012f;

    private float m_shakeIntensity;

    /// <summary>
    /// 카메라 흔들림 강도 — 0이면 흔들리지 않는다. 매 프레임 갱신하는 <b>지속형</b> 값이다. (#477)
    /// 감전(<see cref="PlayerHitView"/>)이 기절 동안 1에서 0으로 낮춰가며 잦아드는 인상을 만든다.
    /// </summary>
    /// <remarks>
    /// 단발 충격(폭발 킥 등)에 쓰려면 호출부가 스스로 감쇠시켜 넣어야 한다 — 여기에 자동 감쇠를
    /// 넣지 않은 이유는, 넣으면 지속형 사용처가 매 프레임 값을 되살려야 해서 두 방식이 싸우기 때문이다.
    /// </remarks>
    public void SetShakeIntensity(float intensity) =>
        m_shakeIntensity = Mathf.Clamp01(intensity);

    // 파형은 ShockShake가 갖는다 — 1인칭 팔(PlayerHandView)과 주파수가 어긋나면 두 진동이 서로
    // 미끄러져 경련이 아니라 고장난 화면처럼 보인다. 여기서는 진폭만 정한다.
    private void EvaluateShake(out Vector3 euler, out Vector3 offset) =>
        ShockShake.Evaluate(m_shakeIntensity, k_shakeDegrees, k_shakeOffset, out euler, out offset);

    /// <summary>
    /// 하위 전체의 레이어를 바꾼다 — "어느 카메라가 이걸 보는가"를 정하는 용도.
    /// 내 몸 숨기기(OwnBody) 외에 1인칭 손·3인칭 장착 표시(#45, #151)도 같은 처리가 필요해 공개한다.
    /// </summary>
    public static void SetLayerRecursively(Transform root, int layer)
    {
        root.gameObject.layer = layer;
        foreach (Transform child in root)
        {
            SetLayerRecursively(child, layer);
        }
    }
}
