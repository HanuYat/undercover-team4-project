using Unity.Netcode;
using UnityEngine;

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
    private PlayerJump m_jump; // 점프 입력 수집·공중 상태 전파 (#189)
    private RoundManager Round => App.Game.Round; // 라운드 종료 시 이동·시점 차단용 (라운드 종료 freeze)
    private float m_pitch;
    private Vector2 m_smoothedLook; // 지수 감쇠로 부드럽게 만든 시점 입력 — 저속 픽셀 양자화 지터 완화 (#216)
    private float m_standCamHeight; // 평소(서기) 카메라 높이 — 프리팹 초기값에서 캡처 (#105)
    private float m_camCrouchDrop; // 시점에 실제로 반영 중인 앉기 하강량 — 공중에서는 얼린다 (#189)
    private float m_downCamBlend; // 서기 시점(0) ↔ 다운 시점(1) 보간 진행도 (#105)
    private float m_verticalVelocity;
    private Vector3 m_knockbackVelocity; // 외력으로 밀려나는 수평 속도 — 매 프레임 감쇠 (#232 폭발 넉백)
    private bool m_ignoreRoundEndFreeze; // 정산 화면을 닫은 로컬 플레이어는 라운드 종료 freeze를 무시하고 움직인다 (#107)

    // 끌려가기(#279) — 오검거 호송 중 오너 로컬이 끌기 NPC 2명을 추종한다. 앵커가 파괴돼도
    // m_carried가 참인 동안은 입력 이동으로 돌아가지 않는다(서버의 종료/스냅 텔레포트가 마무리).
    private bool m_carried;
    private Transform m_carryAnchorA;
    private Transform m_carryAnchorB;

    // 다운(무력화) 중 여부 — 무력화 컴포넌트가 없으면(테스트 구성 등) 항상 false
    private bool IsIncapacitated => m_incapacitation != null && m_incapacitation.IsIncapacitated;

    // 라운드 종료로 정지(freeze)됐는지 — RoundManager가 없으면(단독 테스트 씬) 항상 false.
    // 단 정산 화면을 닫은 로컬 플레이어는 예외 — 남은 카운트다운 동안 자유롭게 움직인다 (#107).
    private bool IsRoundOver => Round != null && Round.GameplayFrozen && !m_ignoreRoundEndFreeze;

    /// <summary>
    /// 라운드 종료 freeze를 이 플레이어에 한해 무시할지 설정한다 — 정산 화면(SettlementPanel)을 닫으면 켜진다.
    /// 다운(무력화) 잠금은 별개라 이 값과 무관하게 유지된다(전원 다운 종료 시 다운 플레이어는 그대로 못 움직임).
    /// </summary>
    public void SetIgnoreRoundEndFreeze(bool ignore) => m_ignoreRoundEndFreeze = ignore;

    // 이동·시점을 막아야 하는 상태 — 다운(무력화) 또는 라운드 종료
    private bool IsMovementLocked => IsIncapacitated || IsRoundOver;

    // 앉기 중 여부 — 앉기 컴포넌트가 없으면(테스트 구성 등) 항상 false (#236)
    private bool IsCrouching => m_crouch != null && m_crouch.IsCrouching;

    // 앉기 블렌딩으로 머리가 내려간 높이(m) — 카메라를 같은 만큼 낮춘다 (#236)
    private float CrouchHeadDrop => m_crouch != null ? m_crouch.HeadDrop : 0f;

    /// <summary>시선 pitch(도, +아래/-위) — PlayerHeadLook이 머리 본 회전에 사용한다. (#348)</summary>
    public float Pitch => m_pitch;

    private void Awake()
    {
        m_controller = GetComponent<CharacterController>();
        m_inputHandler = GetComponent<PlayerInputHandler>();
        m_incapacitation = GetComponent<PlayerIncapacitation>();
        m_crouch = GetComponent<PlayerCrouch>();
        m_jump = GetComponent<PlayerJump>();

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

        // 게임플레이 시작 — 커서를 푸는 UI가 없으면 잠긴다. 실제 Cursor 조작은 CursorLock만 한다. (#352)
        CursorLock.SetGameplayActive(true);
    }

    public override void OnNetworkDespawn()
    {
        if (IsOwner)
        {
            // 오너 로컬 플레이어가 사라지면(라운드 종료 리셋·연결 종료 등) 게임플레이가 끝난 것으로 보고 커서를 푼다.
            // Cursor.lockState는 전역 상태라 씬을 재로드해도 유지되는데, 재로드된 로비 씬에는 이 커서를 풀어 줄
            // PlayerMovement가 없어 커서가 잠긴 채 고착된다 — 마우스로 로비 UI를 못 누르는 원인. (#188)
            // 커서를 푼 UI가 아직 열려 있어도(디스폰 경합) CursorLock이 최종 상태를 단독으로 정한다. (#352)
            CursorLock.SetGameplayActive(false);

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
    /// 서버 전용 — 플레이어를 지정 스폰 포인트로 재배치한다. (#214 §8-2)
    /// 세션 유지 루프(Shop↔Game)에서 플레이어는 씬을 넘어 이월되므로, 각 씬 진입 시 재배치가 필요하다.
    /// 호스트(서버=오너)는 즉시 SetPose, 원격 클라이언트는 NetworkTransform이 오너 권한이라
    /// ApplyPoseRpc(SendTo.Owner)로 넘겨 오너가 스스로 적용해야 전 피어에 전파된다.
    /// </summary>
    public void ServerReposition(Vector3 position, Quaternion rotation)
    {
        if (!IsServer)
            return;

        m_serverSpawnPosition.Value = position;
        m_serverSpawnRotation.Value = rotation;

        if (IsOwner)
            SetPose(position, rotation);       // 호스트(서버=오너): 즉시 적용
        else
            ApplyPoseRpc(position, rotation);  // 원격 클라: 오너가 스스로 적용 (NetworkTransform 오너 권한)
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

        // 호송 중에는 HandleMove를 건너뛰어 접지 보고가 멈춘다 — 공중에서 붙잡히면 공중 상태가
        // 그대로 고착돼 끌려가는 내내 낙하 애니메이션이 재생된다. 여기서 한 번 내려준다. (#189)
        if (m_jump != null)
        {
            m_jump.ReportGrounded(true);
        }
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

        // 낙하·점프 도중 텔레포트되면 쌓인 수직 속도가 그대로 남아 도착지에서 바닥을 파고들거나
        // 튀어오른다 — 도착 즉시 접지 판정으로 이어지도록 초기화한다. (#189)
        m_verticalVelocity = 0f;
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
        // 끌려가는 중(#279) — 입력 이동 대신 끌기 NPC를 추종한다. 행동불능 상태라 시점 입력은 어차피
        // 막혀 있고(IsMovementLocked), 카메라는 다운 시점(UpdateCameraPose)이 계속 담당한다.
        // CharacterController가 꺼져 있어 HandleMove(중력 Move)를 타면 안 된다.
        if (m_carried)
        {
            UpdateCarriedFollow();
            UpdateCameraPose();
            return;
        }

        HandleLook();
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

    private void HandleLook()
    {
        // 다운 중·라운드 종료 시 시점 회전 차단 — 카메라 적용은 UpdateCameraPose가 담당.
        // 커서가 풀려 있을 때도 같다 — 마우스 이동이 화면을 돌리면 안 된다 (#352).
        if (IsMovementLocked || CursorLock.IsUnlocked)
        {
            m_smoothedLook = Vector2.zero; // 재개 시 잠긴 동안의 스무딩 잔여값으로 튀지 않도록 초기화 (#216)
            return;
        }

        Vector2 look = m_inputHandler.LookInput * m_mouseSensitivity;

        // 프레임률 독립 지수 감쇠 — 느린 회전 시 정수 픽셀 delta(0/1/0/1…)로 생기는 계단 지터를 완만하게 한다.
        // 감쇠 계수 0이면 원시 입력을 그대로 적용(스무딩 없음). (#216)
        float t = m_lookSmoothing <= 0f ? 1f : 1f - Mathf.Exp(-m_lookSmoothing * Time.deltaTime);
        m_smoothedLook = Vector2.Lerp(m_smoothedLook, look, t);

        transform.Rotate(Vector3.up * m_smoothedLook.x);

        m_pitch = Mathf.Clamp(m_pitch - m_smoothedLook.y, m_minPitch, m_maxPitch);
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

        bool grounded = m_controller.isGrounded;
        if (grounded && m_verticalVelocity < 0f)
        {
            m_verticalVelocity = -2f;
        }
        m_verticalVelocity += m_gravity * Time.deltaTime;

        // 점프 (#189) — 넉백의 상승 성분과 같은 수직 채널을 쓴다. 중력 적분 뒤에 덮어써야
        // 접지 유지용 -2f 클램프에 임펄스가 잡아먹히지 않는다.
        // 앉은 채로도 뛴다 — 애니메이터가 Crouch → Jump_Begin → Jump_Air_Crouch로 웅크린 자세를
        // 유지해 주고, 콜라이더도 눌림 여부를 따라 계속 작은 상태다(PlayerCrouch.UpdateBlend).
        if (m_jump != null)
        {
            if (m_jump.ConsumeJumpRequest() && grounded && !IsMovementLocked)
            {
                // v = sqrt(2gh) — 중력을 튜닝해도 목표 높이가 유지된다.
                // m_gravity가 잘못 0 이상으로 설정돼도 NaN이 나지 않게 바닥을 깐다.
                m_verticalVelocity = Mathf.Sqrt(
                    2f * m_jump.JumpHeight * Mathf.Max(-m_gravity, 0.01f)
                );
            }
        }

        // 앉기가 달리기보다 우선 — Ctrl을 누르는 동안은 Shift를 눌러도 앉은 채 느리게 이동한다.
        // (앉은 채 달리는 애니메이션 클립이 에셋에 없어 자세와 속도가 어긋나는 것도 막는다) (#236)
        float speed = IsCrouching ? m_crouchSpeed
            : m_inputHandler.IsSprinting ? m_sprintSpeed
            : m_moveSpeed;
        // 넉백은 입력 이동과 별개로 감쇠하며 합산된다 — 다운·라운드 종료로 입력이 막혀도 폭발엔 밀려난다
        Vector3 velocity = moveDirection * speed + m_knockbackVelocity + Vector3.up * m_verticalVelocity;
        m_controller.Move(velocity * Time.deltaTime);

        // 천장에 머리를 박으면 상승 속도를 즉시 죽인다 — CharacterController는 이동이 막혀도 속도를
        // 스스로 지우지 않아, 그냥 두면 남은 상승 속도가 중력에 다 깎일 때까지(점프 1회면 0.4초 남짓)
        // 천장에 붙어 있는다. 실내 천장이 낮은 경찰서에서 바로 드러난다. (#189)
        // 접지 쪽 -2f 클램프와 같은 역할을 위쪽에 해 주는 것.
        if ((m_controller.collisionFlags & CollisionFlags.Above) != 0 && m_verticalVelocity > 0f)
        {
            m_verticalVelocity = 0f;
        }

        // 접지 보고는 반드시 Move() 뒤의 신선한 값으로 한다 (#189).
        // isGrounded는 Move()가 갱신하므로 프레임 앞에서 읽으면 직전 프레임 결과가 나온다 —
        // 그만큼 착지 판정이 한 프레임 밀려 착지 모션이 늦게 뜨는 것으로 보인다.
        // 점프 가능 판정(위 grounded)은 반대로 프레임 앞의 값이 맞다 — 그 시점의 마지막 확정 접지다.
        if (m_jump != null)
        {
            m_jump.ReportGrounded(m_controller.isGrounded);
        }

        // 프레임률과 무관하게 같은 곡선으로 잦아들도록 지수 감쇠
        m_knockbackVelocity *= Mathf.Exp(-m_knockbackDamping * Time.deltaTime);
        if (m_knockbackVelocity.sqrMagnitude < 0.01f)
            m_knockbackVelocity = Vector3.zero;
    }
}
