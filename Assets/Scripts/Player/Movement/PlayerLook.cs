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
    private Camera playerCamera;

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

    private PlayerMovement m_movement;       // 라운드 종료 freeze 판정을 빌린다 (#107 예외 포함)
    private PlayerInputHandler m_inputHandler;
    private PlayerIncapacitation m_incapacitation; // 다운(무력화) 중 시점 처리 분기 (#105, #252)
    private PlayerCrouch m_crouch;           // 앉기 중 카메라 높이 조정 (#236)
    private PlayerJump m_jump;               // 공중에서는 앉기 시점 변화를 얼린다 (#189)

    private float m_pitch;
    private Vector2 m_smoothedLook; // 지수 감쇠로 부드럽게 만든 시점 입력 — 저속 픽셀 양자화 지터 완화 (#216)
    private float m_standCamHeight; // 평소(서기) 카메라 높이 — 프리팹 초기값에서 캡처 (#105)
    private float m_camCrouchDrop;  // 시점에 실제로 반영 중인 앉기 하강량 — 공중에서는 얼린다 (#189)
    private float m_downCamBlend;   // 서기 시점(0) ↔ 다운 시점(1) 보간 진행도 (#105)
    private float m_downYaw;        // 쓰러진 동안 누적한 시야 좌우 각도 — 몸 회전이 아니라 카메라 로컬 (#252)
    private bool m_downLookTaken;   // 쓰러진 뒤 플레이어가 시선을 직접 움직였는가 — 그 순간부터 강제 피치를 놓는다

    /// <summary>시선 pitch(도, +아래/-위) — PlayerHeadLook이 머리 본 회전에 사용한다. (#348)</summary>
    public float Pitch => m_pitch;

    // 다운(무력화) 중 여부 — 무력화 컴포넌트가 없으면(테스트 구성 등) 항상 false
    private bool IsIncapacitated => m_incapacitation != null && m_incapacitation.IsIncapacitated;

    // 앉기 블렌딩으로 머리가 내려간 높이(m) — 카메라를 같은 만큼 낮춘다 (#236)
    private float CrouchHeadDrop => m_crouch != null ? m_crouch.HeadDrop : 0f;

    private void Awake()
    {
        m_movement = GetComponent<PlayerMovement>();
        m_inputHandler = GetComponent<PlayerInputHandler>();
        m_incapacitation = GetComponent<PlayerIncapacitation>();
        m_crouch = GetComponent<PlayerCrouch>();
        m_jump = GetComponent<PlayerJump>();

        if (playerCamera != null)
        {
            m_standCamHeight = playerCamera.transform.localPosition.y; // 서기 시점 높이 기준값
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
            if (playerCamera != null)
            {
                playerCamera.gameObject.SetActive(false);
            }

            return;
        }

        if (m_ownBodyRoot != null)
        {
            SetLayerRecursively(m_ownBodyRoot, LayerMask.NameToLayer("OwnBody")); // 내 카메라에서만 안 보이게
        }
    }

    /// <summary>
    /// 마우스 입력으로 시점을 돌린다 — 평상시엔 몸통 yaw + 카메라 pitch, 쓰러진 동안엔 카메라 로컬만. (#216, #252)
    /// </summary>
    public void HandleLook()
    {
        // 라운드 종료 freeze·커서 해제 시엔 시점 회전을 막는다 — 마우스 이동이 화면을 돌리면 안 된다 (#352).
        // 쓰러진 동안(다운·기절)은 열어 둔다 (#252) — 몸은 못 움직여도 주변은 볼 수 있어야 한다.
        if ((m_movement != null && m_movement.IsRoundOver) || CursorLock.IsUnlocked)
        {
            m_smoothedLook = Vector2.zero; // 재개 시 잠긴 동안의 스무딩 잔여값으로 튀지 않도록 초기화 (#216)
            return;
        }

        Vector2 look = m_inputHandler.LookInput * m_mouseSensitivity * GameSettings.MouseSensitivity;

        // 프레임률 독립 지수 감쇠 — 느린 회전 시 정수 픽셀 delta(0/1/0/1…)로 생기는 계단 지터를 완만하게 한다.
        // 감쇠 계수 0이면 원시 입력을 그대로 적용(스무딩 없음). (#216)
        float t = m_lookSmoothing <= 0f ? 1f : 1f - Mathf.Exp(-m_lookSmoothing * Time.deltaTime);
        m_smoothedLook = Vector2.Lerp(m_smoothedLook, look, t);

        // 쓰러져 있으면 몸을 돌리지 않는다 (#252) — transform을 돌리면 누운 캐릭터가 바닥에서
        // 제자리 회전하는 그림이 되고, 그건 다른 플레이어 화면에도 그대로 보인다.
        // 좌우는 카메라 로컬 각도에 누적하고(범위 제한), 위아래는 누운 자세용 범위로 잡는다.
        if (IsIncapacitated)
        {
            if (m_smoothedLook.sqrMagnitude > 0.0001f)
                m_downLookTaken = true; // 이 순간부터 시선은 플레이어 것 — 바닥 시점 강제를 놓는다

            m_downYaw = Mathf.Clamp(
                m_downYaw + m_smoothedLook.x, -m_downYawRange, m_downYawRange);
            m_pitch = Mathf.Clamp(m_pitch - m_smoothedLook.y, m_downMinPitch, m_downMaxPitch);
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
        if (playerCamera == null) return;

        float lerp = m_camPoseLerpSpeed * Time.deltaTime;
        bool downed = IsIncapacitated;

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

        Vector3 localPos = playerCamera.transform.localPosition;
        localPos.y = Mathf.Lerp(uprightHeight, m_downCamHeight, m_downCamBlend);
        playerCamera.transform.localPosition = localPos;

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

        playerCamera.transform.localEulerAngles = new Vector3(m_pitch, m_downYaw, 0f);
    }

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
