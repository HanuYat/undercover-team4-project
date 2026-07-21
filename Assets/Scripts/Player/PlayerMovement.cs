using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(CharacterController))]
[RequireComponent(typeof(PlayerInputHandler))]
public class PlayerMovement : NetworkBehaviour
{
    [Header("이동")]
    [SerializeField]
    private float m_moveSpeed = 5f;

    [SerializeField]
    private float m_sprintSpeed = 8f;

    [SerializeField]
    private float m_crouchSpeed = 2.5f;

    [SerializeField]
    private float m_gravity = -9.81f;

    [Header("넉백 (폭발 등 외력)")]
    [Tooltip("넉백 속도가 잦아드는 감쇠율(1/초) — 클수록 빨리 멈춘다")]
    [SerializeField] private float m_knockbackDamping = 4f;

    // PlayerAnimationDriver가 속도 정규화에 사용 (실제 속도 ↔ 블렌드 트리 좌표 분리)
    public float MoveSpeed => m_moveSpeed;
    public float SprintSpeed => m_sprintSpeed;
    public float CrouchSpeed => m_crouchSpeed;

    [Header("1인칭 시점")]
    [SerializeField]
    private Camera playerCamera;

    [SerializeField]
    private float m_mouseSensitivity = 1f;

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

    // 서버가 Connection Approval에서 지정한 스폰 포즈. 프리팹의 NetworkTransform이 Owner 권한이라,
    // 씬 동기화를 거쳐 접속하면 오너 로컬 인스턴스가 프리팹 원점에 생성된 채 권한을 잡고 원점
    // 위치를 역전파해 스폰 위치를 덮어쓴다 — 오너가 이 값을 읽어 스스로 스폰 포즈로 이동해 바로잡는다.
    private readonly NetworkVariable<Vector3> m_serverSpawnPosition = new NetworkVariable<Vector3>();
    private readonly NetworkVariable<Quaternion> m_serverSpawnRotation = new NetworkVariable<Quaternion>(
        Quaternion.identity
    );

    private CharacterController m_controller;
    private PlayerInputHandler m_inputHandler;
    private PlayerIncapacitation m_incapacitation; // 다운(무력화) 중 이동·시점 차단용 (#105)
    private PlayerCrouch m_crouch; // 앉기 중 이동 속도·카메라 높이 조정용 (#236)
    private RoundManager Round => App.Game.Round; // 라운드 종료 시 이동·시점 차단용 (라운드 종료 freeze)
    private float m_pitch;
    private float m_standCamHeight; // 평소(서기) 카메라 높이 — 프리팹 초기값에서 캡처 (#105)
    private float m_downCamBlend; // 서기 시점(0) ↔ 다운 시점(1) 보간 진행도 (#105)
    private float m_verticalVelocity;
    private Vector3 m_knockbackVelocity; // 외력으로 밀려나는 수평 속도 — 매 프레임 감쇠 (#232 폭발 넉백)
    private bool m_cursorUnlocked; // 임시: OnGUI 버튼 조작용 커서 해제 상태

    // 끌려가기(#279) — 오검거 호송 중 오너 로컬이 끌기 NPC 2명을 추종한다. 앵커가 파괴돼도
    // m_carried가 참인 동안은 입력 이동으로 돌아가지 않는다(서버의 종료/스냅 텔레포트가 마무리).
    private bool m_carried;
    private Transform m_carryAnchorA;
    private Transform m_carryAnchorB;

    // 다운(무력화) 중 여부 — 무력화 컴포넌트가 없으면(테스트 구성 등) 항상 false
    private bool IsIncapacitated => m_incapacitation != null && m_incapacitation.IsIncapacitated;

    // 라운드 종료로 정지(freeze)됐는지 — RoundManager가 없으면(단독 테스트 씬) 항상 false
    private bool IsRoundOver => Round != null && Round.GameplayFrozen;

    // 이동·시점을 막아야 하는 상태 — 다운(무력화) 또는 라운드 종료
    private bool IsMovementLocked => IsIncapacitated || IsRoundOver;

    // 앉기 중 여부 — 앉기 컴포넌트가 없으면(테스트 구성 등) 항상 false (#236)
    private bool IsCrouching => m_crouch != null && m_crouch.IsCrouching;

    // 앉기 블렌딩으로 머리가 내려간 높이(m) — 카메라를 같은 만큼 낮춘다 (#236)
    private float CrouchHeadDrop => m_crouch != null ? m_crouch.HeadDrop : 0f;

    private void Awake()
    {
        m_controller = GetComponent<CharacterController>();
        m_inputHandler = GetComponent<PlayerInputHandler>();
        m_incapacitation = GetComponent<PlayerIncapacitation>();
        m_crouch = GetComponent<PlayerCrouch>();

        if (playerCamera != null)
        {
            m_standCamHeight = playerCamera.transform.localPosition.y; // 서기 시점 높이 기준값
        }
    }

    public override void OnNetworkSpawn()
    {
        if (IsServer)
        {
            // 서버 인스턴스는 Approval이 지정한 위치에 생성된다 — 이 포즈가 오너에게 초기 동기화된다
            m_serverSpawnPosition.Value = transform.position;
            m_serverSpawnRotation.Value = transform.rotation;
        }

        if (!IsOwner)
        {
            playerCamera.gameObject.SetActive(false);
            enabled = false;
            return;
        }

        ApplyServerSpawnPose();

        if (m_ownBodyRoot != null)
        {
            SetLayerRecursively(m_ownBodyRoot, LayerMask.NameToLayer("OwnBody")); // 내 카메라에서만 안 보이게
        }

        SetCursorUnlocked(false); // 커서 잠금 초기화 — 잠금/해제 로직 단일 경로 (아래 SetCursorUnlocked)
    }

    public override void OnNetworkDespawn()
    {
        // 오너 로컬 플레이어가 사라지면(라운드 종료 리셋·연결 종료 등) OnNetworkSpawn에서 잠갔던 커서를 되돌린다.
        // Cursor.lockState는 전역 상태라 씬을 재로드해도 유지되는데, 재로드된 로비 씬에는 이 커서를 풀어 줄
        // PlayerMovement가 없어(ESC 토글도 못 돎) 커서가 잠긴 채 고착된다 — 마우스로 로비 UI를 못 누르는 원인. (#188)
        if (IsOwner)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            // 끌려가는 도중 정리(라운드 리셋·연결 종료)되면 서버의 StopCarried가 못 올 수 있다 —
            // CharacterController 비활성 + 추종 상태가 남지 않게 여기서 안전하게 푼다. (#279 리뷰 반영)
            EndCarriedFollow();
        }
    }

    private void ApplyServerSpawnPose()
    {
        SetPose(m_serverSpawnPosition.Value, m_serverSpawnRotation.Value);
        Debug.Log($"[PlayerMovement] 서버 지정 스폰 포즈 적용 — Owner {OwnerClientId}, 위치 {transform.position}");
    }

    /// <summary>
    /// 서버 전용 — 접속 시점에 스폰 포인트를 받지 못한 플레이어를 재배치한다. (#247)
    /// 호스트는 Title 씬에서 접속하므로(세션 생성=StartHost) Main Scene의 PlayerSpawnManager
    /// 콜백 등록 전에 기본 위치(원점)에 스폰된다 — InGame 로드 후 이 메서드로 바로잡는다.
    /// 원격 클라이언트는 서버가 InGame에 있을 때만 접속하므로 대상은 사실상 호스트(서버=오너)뿐이다.
    /// </summary>
    public void ServerReposition(Vector3 position, Quaternion rotation)
    {
        if (!IsServer)
            return;

        m_serverSpawnPosition.Value = position;
        m_serverSpawnRotation.Value = rotation;

        if (IsOwner)
            SetPose(position, rotation); // 호스트 플레이어: 서버=오너라 즉시 적용 (NetworkTransform 오너 권한)
    }

    /// <summary>
    /// 서버 전용 — 게임 도중 플레이어를 지정 위치로 순간이동한다. (오검거 광장 매달기 #101 등)
    /// NetworkTransform이 오너 권한이라 서버가 원격 클라 위치를 직접 못 바꾼다 —
    /// 오너에게 RPC로 넘겨 오너가 스스로 SetPose하게 한다(호스트 오너는 로컬로 즉시 적용).
    /// ServerReposition은 스폰 직후 재배치(#247) 전용이라, 도중 텔레포트는 이 경로를 쓴다.
    /// </summary>
    public void ServerTeleport(Vector3 position, Quaternion rotation)
    {
        if (IsSpawned && !IsServer)
            return;

        // 늦게 접속하거나 재스폰되는 피어를 위해 서버 지정 포즈도 함께 갱신해 둔다.
        if (IsSpawned)
        {
            m_serverSpawnPosition.Value = position;
            m_serverSpawnRotation.Value = rotation;
            ApplyPoseRpc(position, rotation); // 오너(호스트 포함)가 스스로 적용
        }
        else
        {
            SetPose(position, rotation); // 오프라인 Play 테스트
        }
    }

    // 오너에서만 실행 — NetworkTransform 오너 권한이라 위치 변경은 오너가 해야 전 피어에 전파된다.
    [Rpc(SendTo.Owner)]
    private void ApplyPoseRpc(Vector3 position, Quaternion rotation) => SetPose(position, rotation);

    /// <summary>
    /// 끌려가기 추종 시작 — 오너 로컬 전용, PlayerPenaltyView(오검거 호송 #279)가 호출한다.
    /// CharacterController를 끄고 매 프레임 두 앵커(양옆 끌기 NPC — 전 피어에 NetworkTransform으로
    /// 동기화된 위치) 중점 살짝 뒤를 따라간다 — 오너가 움직여야 내 위치가 전 피어에 전파된다.
    /// </summary>
    public void BeginCarriedFollow(Transform anchorA, Transform anchorB)
    {
        m_carried = true;
        m_carryAnchorA = anchorA;
        m_carryAnchorB = anchorB;
        m_controller.enabled = false; // 직접 transform 이동 — 켜 두면 내부 캐시가 위치를 되돌린다 (SetPose와 동일 사정)
    }

    /// <summary>끌려가기 추종 종료 — 호송 종료(광장 도착·중단) 시 PlayerPenaltyView가 호출한다.</summary>
    public void EndCarriedFollow()
    {
        m_carried = false;
        m_carryAnchorA = null;
        m_carryAnchorB = null;
        m_controller.enabled = true;
    }

    // 끌기 NPC 추종 — 두 앵커 중점 뒤(끌리는 몸)를 부드럽게 따라간다. 한쪽이 파괴되면 남은 쪽만 따른다.
    private void UpdateCarriedFollow()
    {
        Transform a = m_carryAnchorA != null ? m_carryAnchorA : m_carryAnchorB;
        if (a == null)
            return; // 앵커 전부 소실 — 그 자리에서 대기, 서버의 종료/스냅 텔레포트가 마무리한다
        Transform b = m_carryAnchorB != null ? m_carryAnchorB : a;

        Vector3 forward = a.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.001f)
            forward = transform.forward;
        forward.Normalize();

        Vector3 mid = (a.position + b.position) * 0.5f;
        Vector3 targetPos = mid - forward * 0.75f; // 끌기 담당들 살짝 뒤 — 질질 끌리는 그림

        float lerp = 12f * Time.deltaTime;
        transform.position = Vector3.Lerp(transform.position, targetPos, lerp);
        transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(forward), lerp);
    }

    // CharacterController가 켜진 상태에서 transform을 직접 옮기면 내부 캐시가 위치를 되돌릴 수 있어 잠시 끄고 옮긴다.
    private void SetPose(Vector3 pos, Quaternion rot)
    {
        m_controller.enabled = false;
        transform.SetPositionAndRotation(pos, rot);
        m_controller.enabled = true;
    }

    // 3인칭 장착 표시(#151)도 오너 화면에서 숨기려면 같은 처리가 필요해 공개한다.
    public static void SetLayerRecursively(Transform root, int layer)
    {
        root.gameObject.layer = layer;
        foreach (Transform child in root)
        {
            SetLayerRecursively(child, layer);
        }
    }

    private void Update()
    {
        // 임시: ESC로 커서 잠금/해제 토글 — OnGUI 버튼 조작용.
        // lockState를 명시적으로 None으로 바꿔야 클릭 시 엔진이 재잠금하지 않는다.
        // 정식 UI(메뉴/로비)가 들어오면 그쪽 시스템으로 옮기고 이 블록은 제거할 것.
        if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
        {
            SetCursorUnlocked(!m_cursorUnlocked);
        }

        // 끌려가는 중(#279) — 입력 이동 대신 끌기 NPC를 추종한다. 행동불능 상태라 시점 입력은 어차피
        // 막혀 있고(IsMovementLocked), 카메라는 다운 시점(UpdateCameraPose)이 계속 담당한다.
        // CharacterController가 꺼져 있어 HandleMove(중력 Move)를 타면 안 된다.
        if (m_carried)
        {
            UpdateCarriedFollow();
            UpdateCameraPose();
            return;
        }

        if (!m_cursorUnlocked)
        {
            HandleLook(); // 커서 해제 중에는 시점 회전 정지 (마우스 이동이 화면을 돌리지 않게)
        }

        UpdateCameraPose(); // 카메라 높이/피치를 매 프레임 적용 (다운 시 바닥 시점) (#105)
        HandleMove();
    }

    /// <summary>
    /// 외력으로 밀어낸다 — 폭발 넉백 등(<see cref="BombExplosionView"/>). 세기는 m/s 단위 속도로 준다.
    ///
    /// <b>오너 로컬 전용.</b> 이동 권한이 오너에게 있어(CharacterController + 오너 권한 NetworkTransform)
    /// 남의 인스턴스에서 밀어봤자 오너의 다음 위치 전파에 덮인다 — 그래서 오너가 아니면 조용히 무시한다.
    /// 각 피어가 자기 플레이어에만 적용하는 전제로 호출자가 전수 순회해도 되게 만든 방어다.
    /// (세션이 없는 오프라인 테스트에서는 IsOwner가 false이므로 스폰 여부로 먼저 거른다)
    /// </summary>
    public void AddKnockback(Vector3 velocity)
    {
        if (IsSpawned && !IsOwner) return;

        m_knockbackVelocity += new Vector3(velocity.x, 0f, velocity.z);

        // 위로 띄우는 성분은 중력과 같은 채널로 넣어야 접지 판정·낙하가 자연스럽게 이어진다.
        // 이미 더 크게 튀어오른 중이면 덮어쓰지 않는다(연쇄 폭발이 상승을 잘라먹지 않게).
        if (velocity.y > 0f)
            m_verticalVelocity = Mathf.Max(m_verticalVelocity, velocity.y);
    }

    /// <summary>커서 잠금/해제를 전환한다 — 해제 중엔 시점 회전도 정지. ESC 임시 토글·인벤토리 편집 모드(#144)가 공용.</summary>
    public void SetCursorUnlocked(bool unlocked)
    {
        m_cursorUnlocked = unlocked;
        Cursor.lockState = unlocked ? CursorLockMode.None : CursorLockMode.Locked;
        Cursor.visible = unlocked;
    }

    private void HandleLook()
    {
        if (IsMovementLocked) return; // 다운 중·라운드 종료 시 시점 회전 차단 — 카메라 적용은 UpdateCameraPose가 담당

        Vector2 look = m_inputHandler.LookInput * m_mouseSensitivity;

        transform.Rotate(Vector3.up * look.x);

        m_pitch = Mathf.Clamp(m_pitch - look.y, m_minPitch, m_maxPitch);
    }

    // 카메라 위치(높이)와 피치를 적용한다. 다운 중에는 바닥 근처 높이 + 상방 시선으로 부드럽게 눕히고,
    // 평소에는 서기 높이에서 시선 입력(m_pitch)을 그대로 반영한다. 구조되면 원위치로 복귀한다. (#105)
    // 앉기 중이면 서기 높이를 머리가 내려간 만큼 낮춘 값으로 대체한다. (#236)
    private void UpdateCameraPose()
    {
        if (playerCamera == null) return;

        float lerp = m_camPoseLerpSpeed * Time.deltaTime;
        bool downed = IsIncapacitated;

        m_downCamBlend = Mathf.Lerp(m_downCamBlend, downed ? 1f : 0f, lerp);

        // 앉기 높이는 PlayerCrouch가 이미 0.12초로 블렌딩한 값이라 여기서 추가 보간하지 않는다
        // (카메라만 한 번 더 감쇠되면 애니메이션보다 늦게 내려가 반응이 무겁게 느껴진다) (#236)
        float uprightHeight = m_standCamHeight - CrouchHeadDrop;

        Vector3 localPos = playerCamera.transform.localPosition;
        localPos.y = Mathf.Lerp(uprightHeight, m_downCamHeight, m_downCamBlend);
        playerCamera.transform.localPosition = localPos;

        // 다운 중엔 시선 입력이 멈추므로(HandleLook 차단) 피치를 바닥 시점으로 눕힌다.
        // (m_pitch를 함께 옮겨두면 구조 후에도 그 각도에서 자연스럽게 이어진다)
        if (downed)
        {
            m_pitch = Mathf.Lerp(m_pitch, m_downCamPitch, lerp);
        }
        playerCamera.transform.localEulerAngles = new Vector3(m_pitch, 0f, 0f);
    }

    private void HandleMove()
    {
        // 다운 중·라운드 종료 시 이동 입력 차단 — 단 중력·접지는 유지해 바닥에 서 있게 한다 (#105, 라운드 종료 freeze)
        Vector2 input = IsMovementLocked ? Vector2.zero : m_inputHandler.MoveInput;
        Vector3 moveDirection = (
            transform.right * input.x + transform.forward * input.y
        ).normalized;

        if (m_controller.isGrounded && m_verticalVelocity < 0f)
        {
            m_verticalVelocity = -2f;
        }
        m_verticalVelocity += m_gravity * Time.deltaTime;

        // 앉기가 달리기보다 우선 — Ctrl을 누르는 동안은 Shift를 눌러도 앉은 채 느리게 이동한다.
        // (앉은 채 달리는 애니메이션 클립이 에셋에 없어 자세와 속도가 어긋나는 것도 막는다) (#236)
        float speed = IsCrouching ? m_crouchSpeed
            : m_inputHandler.IsSprinting ? m_sprintSpeed
            : m_moveSpeed;
        // 넉백은 입력 이동과 별개로 감쇠하며 합산된다 — 다운·라운드 종료로 입력이 막혀도 폭발엔 밀려난다
        Vector3 velocity = moveDirection * speed + m_knockbackVelocity + Vector3.up * m_verticalVelocity;
        m_controller.Move(velocity * Time.deltaTime);

        // 프레임률과 무관하게 같은 곡선으로 잦아들도록 지수 감쇠
        m_knockbackVelocity *= Mathf.Exp(-m_knockbackDamping * Time.deltaTime);
        if (m_knockbackVelocity.sqrMagnitude < 0.01f)
            m_knockbackVelocity = Vector3.zero;
    }
}
