using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 플레이어 점프(#189) — 입력 수집과 공중 상태 전파를 담당한다.
///
/// 실제 수직 임펄스는 <see cref="PlayerMovement"/>가 준다. 이동 권한이 오너에게 있고
/// (CharacterController + 오너 권한 NetworkTransform) CharacterController를 만지는 곳을
/// 한 군데로 유지하기 위해서다 — 이 컴포넌트는 "점프 눌렸다"는 단발 요청과 점프 높이만 넘긴다.
///
/// 공중 여부(<see cref="IsAirborne"/>)는 서버 권위 <see cref="NetworkVariable{T}"/>로 전파한다.
/// 원격 피어의 CharacterController는 Move()를 타지 않아 isGrounded가 갱신되지 않으므로,
/// 접지를 아는 오너가 변화 시점에만 서버로 보고하고 서버가 전 피어에 뿌린다.
/// (PlayerCrouch와 동일한 권위 패턴 — 애니메이션은 PlayerAnimationDriver가 이 값을 폴링한다)
/// </summary>
[RequireComponent(typeof(CharacterController))]
public class PlayerJump : NetworkBehaviour
{
    [Header("점프")]
    [Tooltip(
        "제자리 점프로 올라가는 최고 높이(m). 실제 초기 속도는 중력에서 역산한다.\n"
            + "맵이 점프를 전제로 설계되지 않아(GDD 미정의) 보수적으로 잡았다 — 올라타면 안 되는 "
            + "구조물이 발견되면 여기부터 낮출 것."
    )]
    [SerializeField]
    private float m_jumpHeight = 0.8f;

    // 서버 권위 공중 플래그 — 서버만 쓰고 모든 클라가 읽는다. (PlayerCrouch.m_isCrouchingSynced와 동일 패턴)
    private readonly NetworkVariable<bool> m_isAirborneSynced = new NetworkVariable<bool>();
    private bool m_isAirborne; // 서버·오프라인의 진실값 (비네트워크 Play 테스트 폴백)
    private bool m_reportedAirborne; // 오너가 마지막으로 서버에 보낸 값 — 변화할 때만 RPC를 보낸다
    private bool m_jumpRequested; // 오너 로컬 단발 요청 — PlayerMovement가 소비한다

    private PlayerInputHandler m_inputHandler;

    /// <summary>점프 최고 높이(m) — PlayerMovement가 중력에서 초기 속도를 역산하는 데 쓴다.</summary>
    public float JumpHeight => m_jumpHeight;

    /// <summary>
    /// 공중에 떠 있는지. 서버·오프라인은 실참조, <b>남의 화면에 보이는 인스턴스만</b> 동기화값을 읽는다.
    ///
    /// 오너를 실참조 쪽에 넣는 것이 PlayerCrouch와 다른 점이다. 앉기는 홀드라 서버 왕복(RTT)만큼
    /// 늦어도 티가 안 나지만, 점프는 누른 즉시 발이 떠야 해서 내 화면에서 왕복을 기다리면 이륙 모션이
    /// 눈에 띄게 밀린다. 어차피 이동 자체가 클라 권위(오너 CharacterController + 오너 권한
    /// NetworkTransform)라 접지의 진실값은 오너가 쥐고 있다 — 로컬 즉시 반영이 권위와도 맞다.
    /// </summary>
    public bool IsAirborne =>
        IsSpawned && !IsServer && !IsOwner ? m_isAirborneSynced.Value : m_isAirborne;

    private void Awake()
    {
        m_inputHandler = GetComponent<PlayerInputHandler>();
    }

    public override void OnNetworkSpawn()
    {
        if (IsOwner && m_inputHandler != null)
        {
            m_inputHandler.OnJumpPressed += HandleJumpInput;
        }
    }

    public override void OnNetworkDespawn()
    {
        if (IsOwner && m_inputHandler != null)
        {
            m_inputHandler.OnJumpPressed -= HandleJumpInput;
        }

        m_jumpRequested = false;
    }

    private void HandleJumpInput() => m_jumpRequested = true;

    /// <summary>
    /// 점프 요청이 있었으면 true를 돌려주고 요청을 비운다 — PlayerMovement가 매 프레임 한 번 호출한다.
    /// 단발 소비라 한 번 누르면 한 번만 뛴다(입력이 프레임 사이에 쌓여도 중복 발동 없음).
    /// </summary>
    public bool ConsumeJumpRequest()
    {
        if (!m_jumpRequested)
            return false;

        m_jumpRequested = false;
        return true;
    }

    /// <summary>
    /// 오너가 접지 상태를 알린다 — PlayerMovement가 IsStablyGrounded(설 수 있는 지면 위인가)를 매 프레임 넘긴다.
    /// 원본 CharacterController.isGrounded가 아니다 — 수직 벽면도 접지로 치기 때문. (PlayerMovement 참고)
    /// 값이 바뀔 때만 서버로 보내 점프 한 번에 RPC 2회(이륙/착지)로 끝낸다.
    /// </summary>
    public void ReportGrounded(bool grounded)
    {
        bool airborne = !grounded;

        if (!IsSpawned)
        {
            m_isAirborne = airborne; // 세션 없는 Play 테스트 — 로컬 값이 곧 진실값
            return;
        }

        if (!IsOwner)
            return;

        m_isAirborne = airborne; // 내 화면은 왕복을 기다리지 않는다 (IsAirborne 주석 참고)

        if (m_reportedAirborne == airborne)
            return;

        m_reportedAirborne = airborne;
        ReportAirborneServerRpc(airborne); // 남의 화면용 — 변화 시점에만 보내 점프 1회당 RPC 2회
    }

    [ServerRpc]
    private void ReportAirborneServerRpc(bool airborne)
    {
        m_isAirborne = airborne;
        m_isAirborneSynced.Value = airborne;
    }
}
