using UnityEngine;

/// <summary>
/// 단말 포커스 시점 — 본부 컴퓨터 앞으로 카메라를 옮겨 화면을 정면으로 크게 본다. (#689)
///
/// <b>포즈를 스스로 대입하지 않는다</b> — <see cref="PlayerSpectateCamera"/>와 같은 계약이다.
/// 월드 포즈를 내주기만 하고 카메라에 넣는 것은 <see cref="PlayerLook.UpdateCameraPose"/>다.
/// 카메라 transform을 밖에서 만지면 그쪽이 매 프레임 통째로 덮어써 그 프레임에 지워진다 (#477).
///
/// <b>상태에서 유도한다</b> — 켜고 끄는 플래그를 밖에서 관리하지 않고, 매 틱 <see cref="Tick"/>이
/// "지금도 포커스가 성립하는가"를 다시 묻는다. 그래서 단말이 꺼지거나(해킹 복구·라운드 종료로 인한
/// 리셋) 플레이어가 쓰러지면 별도 배선 없이 풀린다. 플래그를 들고 껐다 켜는 방식은 반드시 새는
/// 경로가 생긴다 — 카메라가 컴퓨터에 붙은 채 남으면 그 플레이어는 라운드가 끝날 때까지 화면을 잃는다.
///
/// 순수 로컬 표현이다 — 동기화할 상태가 없다. 상호작용한 본인의 클라이언트에서만 돈다.
/// </summary>
[RequireComponent(typeof(PlayerLook))]
public class PlayerTerminalFocus : MonoBehaviour
{
    [Tooltip("1인칭↔단말 화면 전환 보간 속도. 클수록 빨리 붙는다 — PlayerSpectateCamera와 같은 기준")]
    [SerializeField]
    private float m_blendSpeed = 7f;

    private BlackoutRecoveryTerminal m_terminal;
    private float m_blend;          // 1인칭(0) ↔ 단말 화면(1) 보간 진행도
    private bool m_locked;          // 시점·이동 잠금을 걸어 둔 상태인가 — 넣고 빼는 짝을 지키는 래치

    private PlayerLook m_look;
    private PlayerMovement m_movement;
    private PlayerIncapacitation m_incapacitation;

    /// <summary>지금 단말을 보고 있는가 — 요청 상태다. 화면 UI가 입력을 받을지 판단할 때 쓴다.</summary>
    public bool IsFocusing => m_terminal != null;

    /// <summary>보고 있는 단말 — 아니면 null.</summary>
    public BlackoutRecoveryTerminal Terminal => m_terminal;

    private void Awake()
    {
        m_look = GetComponent<PlayerLook>();
        m_movement = GetComponent<PlayerMovement>();
        m_incapacitation = GetComponent<PlayerIncapacitation>();
    }

    private void OnDisable()
    {
        // 디스폰·비활성으로 빠져나가도 시점·이동 잠금은 반드시 되돌린다 — 안 그러면 다음 라운드에서
        // 시점이 안 돌아가거나 이동이 잠긴 채 남는다.
        Release();
    }

    /// <summary>단말 화면 앞으로 간다 — 단말의 E 상호작용이 부른다.</summary>
    public void Begin(BlackoutRecoveryTerminal terminal)
    {
        if (terminal == null || terminal.FocusPoint == null)
            return;

        if (m_terminal == terminal)
        {
            Release(); // 보고 있는 단말을 다시 누르면 나간다
            return;
        }

        m_terminal = terminal;
        terminal.SetLocalFocused(true); // 화면이 키 입력을 받을 주인이 생겼다
        ApplyLocks(true);
    }

    /// <summary>1인칭으로 돌아간다 — 화면의 나가기, 복구 완료, 상태 이탈이 모두 여기로 모인다.</summary>
    public void Release()
    {
        if (m_terminal == null && !m_locked)
            return;

        // ?. 금지 — 파괴된 Unity 오브젝트의 fake null을 우회하지 않게 한다 (HqPanelView 관례)
        if (m_terminal != null)
            m_terminal.SetLocalFocused(false);

        m_terminal = null;
        ApplyLocks(false);
    }

    /// <summary>
    /// 보간을 진행하고 현재 진행도를 돌려준다 — <see cref="PlayerLook"/>이 매 프레임 부른다.
    /// 포커스 중이 아니어도 불러야 <b>이탈 보간</b>이 진행된다 (PlayerSpectateCamera와 같은 이유).
    /// </summary>
    public float Tick()
    {
        // 포커스가 아직 성립하는지 매 틱 되묻는다 — 여기가 이 클래스의 안전장치다.
        if (m_terminal != null && !CanKeepFocus())
            Release();

        float target = m_terminal != null ? 1f : 0f;
        m_blend = Mathf.Lerp(m_blend, target, 1f - Mathf.Exp(-m_blendSpeed * Time.deltaTime));

        // 다 빠져나왔으면 딱 0으로 떨어뜨린다 — 미세한 잔여값이 남아 1인칭 포즈가 계속 섞이지 않게.
        if (m_terminal == null && m_blend < 0.001f)
            m_blend = 0f;

        return m_blend;
    }

    /// <summary>카메라가 있어야 할 월드 포즈 — 보간이 0이면 false.</summary>
    public bool TryGetPose(out Vector3 position, out Quaternion rotation)
    {
        Transform point = m_terminal != null ? m_terminal.FocusPoint : null;
        if (point == null)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            return false;
        }

        position = point.position;
        rotation = point.rotation;
        return true;
    }

    // 포커스를 유지할 조건 — 하나라도 깨지면 즉시 풀린다.
    // 해킹이 복구되면 단말이 오프라인이 되므로 "복구하면 저절로 화면에서 나온다"가 여기서 성립한다.
    // 라운드 종료도 SuddenEventManager의 리셋이 해킹을 풀어 같은 경로로 빠져나온다.
    private bool CanKeepFocus()
    {
        if (m_terminal == null || !m_terminal.IsOnline || m_terminal.FocusPoint == null)
            return false;

        // 쓰러지면 화면에서 나온다 — 사망 관전(PlayerSpectateCamera)이 카메라를 가져가야 하는데
        // 포커스가 남아 있으면 두 연출이 같은 카메라를 두고 싸운다.
        if (m_incapacitation != null && m_incapacitation.IsIncapacitated)
            return false;

        return true;
    }

    /// <summary>
    /// 시점·이동 잠금을 짝 지어 넣고 뺀다.
    ///
    /// <b>커서는 풀지 않는다</b> — 예전에는 <c>CursorLock.PushUnlock</c>으로 시점 정지를 겸했다(그쪽
    /// 부수 효과로 <see cref="PlayerLook.HandleLook"/>이 멈춘다). 그런데 커서가 풀리면
    /// <see cref="PlayerInteractor.HandleInteract"/>가 E를 통째로 막아(#352) <b>화면에서 나갈 수단이
    /// 화면 안 버튼밖에 남지 않는다</b>. 입력이 숫자 키로 바뀌어 커서를 풀 이유도 사라졌으므로,
    /// 시점만 정확히 겨냥해 멈추고 커서는 잠근 채로 둔다 — E가 다른 상호작용과 같은 규칙으로 돌아온다.
    ///
    /// 이동은 따로 막는다. 안 막으면 화면을 보는 동안 걸어가 카메라만 컴퓨터에 남고 몸은 딴 데 가 있다.
    /// </summary>
    private void ApplyLocks(bool active)
    {
        if (active == m_locked)
            return;

        m_locked = active;

        if (m_look != null)
            m_look.SetLookSuspended(active);

        if (m_movement != null)
            m_movement.SetViewLocked(active);
    }
}
