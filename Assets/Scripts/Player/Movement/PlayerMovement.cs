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

    // 접지 중 유지하는 하향 속도(m/s). 0으로 두면 CharacterController가 경사·계단에서 지면을 놓쳐
    // 접지 판정이 깜빡인다 — 살짝 눌러 붙여 둔다. 천장 상쇄(0으로 죽이기)의 반대쪽 짝이다. (#189)
    private const float k_groundedStickVelocity = -2f;

    // 설 수 없는 가파른 면에서 밀어내는 속도(m/s). 면에서 0.15m만 떨어지면 정상 낙하로 돌아온다
    // (격리벽 실측) — 3m/s면 세 프레임 남짓. 왜 필요한지는 IsStablyGrounded 참고.
    private const float k_steepSlideSpeed = 3f;

    // PlayerAnimationDriver가 속도 정규화에 사용 (실제 속도 ↔ 블렌드 트리 좌표 분리)
    // 실제 이동(HandleMove)도 같은 프로퍼티를 쓴다 — 배율이 걸린 값을 한 곳에서만 내야
    // 애니메이션 블렌드가 실제 속도와 어긋나지 않는다. (#398)
    public float MoveSpeed => m_moveSpeed * SpeedFactor;
    public float SprintSpeed => m_sprintSpeed * SpeedFactor;
    public float CrouchSpeed => m_crouchSpeed * SpeedFactor;

    /// <summary>
    /// 이동 속도에 걸린 외부 배율 — 지금은 밧줄로 끌고 있는 무게뿐이다(<see cref="RopeDragLoad.DragSpeedFactor"/>).
    /// 연행 컴포넌트가 없으면(단독 테스트 씬) 1. 소스가 여럿이 되면(스탯 강화 #368 등) 여기서 곱해
    /// 합성한다 — 이 프로퍼티를 거치는 한 애니메이션 정합은 따라온다. (#398)
    /// </summary>
    public float SpeedFactor => m_dragLoad != null ? m_dragLoad.DragSpeedFactor : 1f;

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
    private RopeDragLoad m_dragLoad; // 끌고 있는 무게로 깎인 이동속도 배율·목줄 제한을 읽는다 (#398)
    private PlayerTowedMotion m_towed; // 남이 내 몸을 옮기는 동안의 추종 — 입력 이동을 대신한다 (#279, #365)
    private PlayerLook m_look; // 시점 회전·카메라 자세 — 몸통 yaw가 이동 방향의 기준이라 여기서 순서를 잡는다
    private PlayerRagdoll m_ragdoll; // 사망 래그돌 — 켜져 있는 동안 외력(넉백)을 삼킨다 (#506)
    private RoundManager Round => App.Game.Round; // 라운드 종료 시 이동·시점 차단용 (라운드 종료 freeze)
    private float m_verticalVelocity;
    private Vector3 m_knockbackVelocity; // 외력으로 밀려나는 수평 속도 — 매 프레임 감쇠 (#232 폭발 넉백)
    private bool m_ignoreRoundEndFreeze; // 정산 화면을 닫은 로컬 플레이어는 라운드 종료 freeze를 무시하고 움직인다 (#107)

    // 이번 Move에서 밟은 면 중 법선이 가장 선 것 — OnControllerColliderHit이 채운다
    private Vector3 m_groundNormal = Vector3.up;
    private bool m_hasGroundContact;

    // 다운(무력화) 중 여부 — 무력화 컴포넌트가 없으면(테스트 구성 등) 항상 false
    private bool IsIncapacitated => m_incapacitation != null && m_incapacitation.IsIncapacitated;

    /// <summary>
    /// 딛고 선 면이 <see cref="CharacterController.slopeLimit"/>보다 가파른가 — 밟은 면이 없으면 false.
    /// </summary>
    private bool IsOnSteepSurface =>
        m_hasGroundContact && Vector3.Angle(m_groundNormal, Vector3.up) > m_controller.slopeLimit;

    /// <summary>
    /// 실제로 딛고 설 수 있는 지면 위인가 — <b>접지를 묻는 곳은 전부 이쪽을 쓴다</b>
    /// (점프 자격·접지 클램프·애니메이션이 갈라지면 "설 수 없는 곳에서 뛰는" 경계가 생긴다).
    ///
    /// <see cref="CharacterController.isGrounded"/>는 캡슐 아랫반구 접촉의 법선에 위쪽 성분이 조금이라도
    /// 있으면 <see cref="CharacterController.slopeLimit"/>과 무관하게 접지로 친다. 맵 콜리전 껍질은
    /// 렌더 메시에서 구운 볼록 껍질이라 벽의 '평평한' 면조차 눕어 있어(격리벽 하단 패널 실측 88.0도,
    /// 법선 y=+0.036) 그 2도로 <b>수직 벽면 위에 서게 된다</b> — 낙하가 통째로 막히고, 접지로 잡히니
    /// 거기서 또 뛰어 점프마다 더 높이 얹힌다. 이 프로젝트의 "벽 타기"가 정확히 이것이다.
    /// 에셋 팩 프리팹의 80~95%가 같은 성질이라 맵을 갈아도 사라지지 않는다.
    /// </summary>
    private bool IsStablyGrounded => m_controller.isGrounded && !IsOnSteepSurface;

    /// <summary>
    /// 라운드 종료로 정지(freeze)됐는지 — RoundManager가 없으면(단독 테스트 씬) 항상 false.
    /// 단 정산 화면을 닫은 로컬 플레이어는 예외 — 남은 카운트다운 동안 자유롭게 움직인다 (#107).
    /// 시점 차단 판정도 같은 값을 써야 해서(<see cref="PlayerLook"/>) 이 컴포넌트가 단독으로 들고 빌려준다 —
    /// 예외 플래그를 켜는 <see cref="SetIgnoreRoundEndFreeze"/>가 여기 있기 때문.
    /// </summary>
    internal bool IsRoundOver => Round != null && Round.GameplayFrozen && !m_ignoreRoundEndFreeze;

    /// <summary>
    /// 라운드 종료 freeze를 이 플레이어에 한해 무시할지 설정한다 — 정산 화면(SettlementPanel)을 닫으면 켜진다.
    /// 다운(무력화) 잠금은 별개라 이 값과 무관하게 유지된다(전원 다운 종료 시 다운 플레이어는 그대로 못 움직임).
    /// </summary>
    public void SetIgnoreRoundEndFreeze(bool ignore) => m_ignoreRoundEndFreeze = ignore;

    // 이동·시점을 막아야 하는 상태 — 다운(무력화) 또는 라운드 종료
    private bool IsMovementLocked => IsIncapacitated || IsRoundOver;

    // 앉기 중 여부 — 앉기 컴포넌트가 없으면(테스트 구성 등) 항상 false (#236)
    private bool IsCrouching => m_crouch != null && m_crouch.IsCrouching;

    private void Awake()
    {
        m_controller = GetComponent<CharacterController>();
        m_inputHandler = GetComponent<PlayerInputHandler>();
        m_incapacitation = GetComponent<PlayerIncapacitation>();
        m_crouch = GetComponent<PlayerCrouch>();
        m_jump = GetComponent<PlayerJump>();
        m_dragLoad = GetComponent<RopeDragLoad>();
        m_towed = GetComponent<PlayerTowedMotion>();
        m_look = GetComponent<PlayerLook>();
        m_ragdoll = GetComponent<PlayerRagdoll>();
    }

    public override void OnNetworkSpawn()
    {
        if (IsServer)
        {
            // 서버 인스턴스는 Approval이 지정한 위치에 생성된다 — 이 포즈가 오너에게 초기 동기화된다
            m_serverSpawnPosition.Value = transform.position;
            m_serverSpawnRotation.Value = transform.rotation;
        }

        // 남의 카메라 끄기·내 몸 숨기기는 시점 담당(PlayerLook)이 든다 — 카메라와 몸 루트 참조가 그쪽에 있다.
        m_look?.ApplyOwnerView(IsOwner);

        if (!IsOwner)
        {
            enabled = false; // 이동·시점 갱신은 오너만 — PlayerLook·PlayerTowedMotion도 이 Update가 돌린다
            return;
        }

        ApplyServerSpawnPose();

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

            // 끌려가는 도중 정리(라운드 리셋·연결 종료)되면 서버의 종료 지시(StopCarried·내려놓기)가
            // 못 올 수 있다 — CharacterController 비활성 + 추종 상태가 남지 않게 여기서 안전하게 푼다.
            // (#279 리뷰 반영, #365도 같은 사정)
            m_towed?.StopAll();
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

    // ---- 추종 컴포넌트(PlayerTowedMotion)와 공유하는 면 ----
    // 수직 속도와 CharacterController의 소유자는 이 컴포넌트다 — 중력·점프·넉백이 모두 같은 채널을
    // 쓰기 때문. 추종 쪽이 직접 만지면 같은 값을 두 컴포넌트가 따로 적분하게 되므로 연산만 빌려준다.

    /// <summary>
    /// 수평 이동만 받아 중력과 함께 적용한다 — 운반 추종(<see cref="PlayerTowedMotion"/>, #365)이 쓴다.
    /// 접지 클램프·중력 적분은 <see cref="HandleMove"/>와 같은 경로(<see cref="IntegrateGravity"/>)를 쓴다.
    /// </summary>
    internal void MoveWithGravity(Vector3 horizontalStep)
    {
        IntegrateGravity();
        MoveAndTrackGround(horizontalStep + Vector3.up * m_verticalVelocity * Time.deltaTime);
    }

    /// <summary>
    /// Move하면서 밟은 면을 기록한다 — 접지 판정(<see cref="IsStablyGrounded"/>)의 재료다.
    /// <b>Move 호출은 이 경로 하나로 모은다</b> — 지난 프레임 기록을 비우지 않으면 묵은 면을 보고 판정한다.
    /// </summary>
    private void MoveAndTrackGround(Vector3 displacement)
    {
        m_hasGroundContact = false;
        m_controller.Move(displacement);
    }

    /// <summary>
    /// 부딪힌 면 중 이번 프레임의 <b>지면 후보</b>를 고른다 — 벽과 바닥에 동시에 닿으면 법선이 가장 선
    /// 쪽(= 진짜 바닥)을 남긴다. 그래야 벽에 붙어 서 있어도 발밑에 바닥이 있으면 정상 접지로 잡힌다.
    /// </summary>
    private void OnControllerColliderHit(ControllerColliderHit hit)
    {
        if (hit.normal.y <= 0f)
        {
            return; // 천장이거나 완전 수직 — 지면 후보가 아니다
        }

        if (!m_hasGroundContact || hit.normal.y > m_groundNormal.y)
        {
            m_groundNormal = hit.normal;
            m_hasGroundContact = true;
        }
    }

    // 접지 유지 클램프 + 중력 적분 — 수직 속도의 유일한 적분 지점이다.
    // 입력 이동(HandleMove)과 운반 추종(MoveWithGravity)이 같은 규칙을 써야 하므로 여기 하나만 둔다.
    // 점프 임펄스는 이 뒤에 덮어써야 한다 — 클램프에 잡아먹히지 않게. (#189, HandleMove 참고)
    private void IntegrateGravity()
    {
        // isGrounded가 아닌 이유 — 벽면에 얹힌 채로 클램프가 걸리면 하향 속도가 -2로 고정돼 영원히 매달린다.
        if (IsStablyGrounded && m_verticalVelocity < 0f)
        {
            m_verticalVelocity = k_groundedStickVelocity;
        }

        m_verticalVelocity += m_gravity * Time.deltaTime;
    }

    /// <summary>
    /// 쌓인 외력(넉백)과 수직 속도를 지운다 — 래그돌 진입(#506)이 부른다.
    /// 진입 전에 이미 들어온 폭발 넉백이 남아 있으면, 뼈가 날아가는 동안 캡슐도 같이 미끄러진다.
    /// 넉백 가드(<see cref="AddKnockback"/>)가 막는 것은 진입 <b>이후</b>의 호출뿐이라 이 짝이 필요하다.
    /// </summary>
    internal void ClearExternalVelocity()
    {
        m_knockbackVelocity = Vector3.zero;
        m_verticalVelocity = 0f;
    }

    /// <summary>
    /// 진단 전용 — 래그돌 튐 추적(#506)이 캡슐의 수직 속도를 함께 찍는다. 원인 확정되면 지운다.
    /// </summary>
    internal float DiagnosticVerticalVelocity => m_verticalVelocity;

    /// <summary>
    /// CharacterController를 껐다 켠다 — transform을 직접 옮기는 호송 추종(#279)이 쓴다.
    /// 켠 채로 transform을 옮기면 CC 내부 캐시가 위치를 되돌린다 (<see cref="SetPose"/>와 동일 사정).
    /// </summary>
    internal void SetControllerEnabled(bool value) => m_controller.enabled = value;

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

    // 오너의 매 프레임 갱신 — 시점(PlayerLook)·추종(PlayerTowedMotion)도 여기서 순서를 잡아 돌린다.
    // 자기 Update에 맡기지 않는 이유: 시점이 몸통 yaw를 돌리고 이동이 그 yaw를 기준으로 방향을 잡으므로
    // 같은 프레임에서 시점 → 이동 순서가 보장돼야 한다(Unity의 컴포넌트 실행 순서는 미지정).
    private void Update()
    {
        // 남이 내 몸을 옮기는 중(#279 호송 / #365 운반) — 입력 이동 대신 추종한다.
        // HandleMove를 타면 안 되는 이유는 모드마다 다르다: 호송은 CharacterController가 꺼져 있고,
        // 운반은 켜져 있지만 중력이 이중으로 적분된다. 어느 쪽이든 이동은 추종 쪽이 든다.
        //
        // 시점은 두 모드 모두 열어 둔다 — 쓰러져도 주변은 볼 수 있어야 한다(#252). 몸은 추종이 돌리고
        // 시야는 카메라 로컬(PlayerLook의 다운 yaw)이 따로 드므로 서로 간섭하지 않는다.
        if (m_towed != null && m_towed.IsActive)
        {
            m_look?.HandleLook();
            m_towed.Tick();
            m_look?.UpdateCameraPose();
            return;
        }

        // 래그돌 비행 중(#506) — 몸을 끄는 주체가 자기 뼈 물리라는 점만 다른 세 번째 추종 모드다.
        // 캡슐이 시체를 따라가지 않으면 사망 지점에 남아 있다가 정착 순간 한 번에 1m 넘게
        // 텔레포트하고, 그 늦은 점프가 원격에서 시체를 발작시킨다 (PlayerRagdoll.TickCapsuleFollow).
        // 위 호송·운반이 먼저다 — 남이 내 몸을 옮기는 중이면 그쪽이 위치의 주인이다.
        if (m_ragdoll != null && m_ragdoll.IsCapsuleFollowingBody)
        {
            m_look?.HandleLook();
            m_ragdoll.TickCapsuleFollow();
            m_look?.UpdateCameraPose();
            return;
        }

        m_look?.HandleLook();
        m_look?.UpdateCameraPose(); // 카메라 높이/피치를 매 프레임 적용 (다운 시 바닥 시점) (#105)
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

        // 래그돌 중이면 삼킨다 — 몸은 뼈 물리가 날리고 있으므로 캡슐까지 같은 폭발로 미끄러지면
        // 시체와 판정 위치가 서로 다른 방향으로 벌어진다. (#506 §3-2)
        //
        // 호출부(BombExplosionView)에서 "죽은 사람은 건너뛴다"로 거르지 않는 이유: 원격 클라에서는
        // 사망 사실(PlayerIncapacitation의 NetworkVariable)과 폭발 사실(BombDevice의 것)이 서로 다른
        // 오브젝트에서 와 도착 순서가 보장되지 않아, 그 시점의 "이 사람 죽었나?"가 틀릴 수 있다.
        // 들어와도 무해하게 만드는 쪽이 순서와 무관하게 항상 옳다.
        if (m_ragdoll != null && m_ragdoll.IsRagdollActive) return;

        m_knockbackVelocity += new Vector3(velocity.x, 0f, velocity.z);

        // 위로 띄우는 성분은 중력과 같은 채널로 넣어야 접지 판정·낙하가 자연스럽게 이어진다.
        // 이미 더 크게 튀어오른 중이면 덮어쓰지 않는다(연쇄 폭발이 상승을 잘라먹지 않게).
        if (velocity.y > 0f)
            m_verticalVelocity = Mathf.Max(m_verticalVelocity, velocity.y);
    }

    private void HandleMove()
    {
        // 다운 중·라운드 종료 시 이동 입력 차단 — 단 중력·접지는 유지해 바닥에 서 있게 한다 (#105, 라운드 종료 freeze)
        Vector2 input = IsMovementLocked ? Vector2.zero : m_inputHandler.MoveInput;
        Vector3 moveDirection = (
            transform.right * input.x + transform.forward * input.y
        ).normalized;

        // 점프 자격 판정에는 Move() 앞의 값이 맞다 — 그 시점의 마지막 확정 접지다.
        // (착지 보고는 반대로 Move() 뒤의 신선한 값을 쓴다 — 아래 ReportGrounded 참고, #189)
        bool grounded = IsStablyGrounded;

        IntegrateGravity();

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
        // 상수가 아니라 프로퍼티 — 무게 배율(#398)이 곱해져 있고 애니메이션 블렌드도 같은 값을 읽는다.
        float speed = IsCrouching ? CrouchSpeed
            : m_inputHandler.IsSprinting ? SprintSpeed
            : MoveSpeed;

        // 팽팽해진 밧줄이 허용하는 만큼으로 입력 이동을 깎는다 — 줄다리기 힘겨루기 (#398).
        // 넉백에는 걸지 않는다: 폭발 같은 외력은 줄을 이겨야 하고, 막으면 벽과 줄 사이에 낀다.
        Vector3 inputVelocity = moveDirection * speed;
        if (m_dragLoad != null)
            inputVelocity = m_dragLoad.ConstrainByTautRopes(inputVelocity);

        // 벽면에 얹혔으면 밀어내 흘러내리게 한다 — 중력만으로는 영원히 붙어 있다(입력을 떼고 90프레임 돌려도 0mm).
        // 면으로 밀어 넣는 입력 성분도 함께 지운다: 남겨 두면 미는 힘이 이겨서 계속 붙어 있는다.
        // 넉백은 건드리지 않는다 — 폭발로 벽에 처박히는 것은 의도된 결과다. (IsStablyGrounded 참고)
        if (m_controller.isGrounded && IsOnSteepSurface)
        {
            Vector3 awayFromSurface = new Vector3(m_groundNormal.x, 0f, m_groundNormal.z).normalized;
            inputVelocity =
                Vector3.ProjectOnPlane(inputVelocity, awayFromSurface)
                + awayFromSurface * k_steepSlideSpeed;
        }

        // 넉백은 입력 이동과 별개로 감쇠하며 합산된다 — 다운·라운드 종료로 입력이 막혀도 폭발엔 밀려난다
        Vector3 velocity = inputVelocity + m_knockbackVelocity + Vector3.up * m_verticalVelocity;
        MoveAndTrackGround(velocity * Time.deltaTime);

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
            m_jump.ReportGrounded(IsStablyGrounded);
        }

        // 프레임률과 무관하게 같은 곡선으로 잦아들도록 지수 감쇠
        m_knockbackVelocity *= Mathf.Exp(-m_knockbackDamping * Time.deltaTime);
        if (m_knockbackVelocity.sqrMagnitude < 0.01f)
            m_knockbackVelocity = Vector3.zero;
    }
}
