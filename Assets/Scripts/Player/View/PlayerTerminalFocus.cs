using UnityEngine;

/// <summary>
/// 단말 포커스 시점 — 본부 컴퓨터 앞으로 카메라를 옮긴다. (#689) 순수 로컬 표현이라 동기화할 것이 없다.
///
/// <b>포즈를 스스로 대입하지 않는다</b> — 월드 포즈를 내주기만 하고 카메라에 넣는 것은
/// <see cref="PlayerLook.UpdateCameraPose"/>다. 밖에서 만지면 그쪽이 매 프레임 덮어쓴다 (#477).
///
/// <b>상태에서 유도한다</b> — 매 틱 <see cref="Tick"/>이 포커스 성립을 되물어, 복구·라운드 종료·사망에
/// 별도 배선 없이 풀린다. 플래그를 껐다 켜는 방식은 반드시 새고, 그러면 카메라가 컴퓨터에 붙은 채 남는다.
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

    // 디스폰·비활성으로 빠져나가도 잠금은 되돌린다 — 다음 라운드에서 시점·이동이 잠긴 채 남지 않게.
    private void OnDisable() => Release();

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

        Release(); // 다른 단말을 보고 있었다면 먼저 놓는다 — 안 그러면 그 화면이 계속 내 키를 받는다

        m_terminal = terminal;
        terminal.SetLocalFocused(true);
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

    // 하나라도 깨지면 즉시 풀린다. 복구·라운드 종료는 단말이 오프라인이 되면서, 사망은 아래에서 걸린다
    // (포커스가 남으면 사망 관전과 같은 카메라를 두고 싸운다).
    private bool CanKeepFocus()
    {
        if (m_terminal == null || !m_terminal.IsOnline || m_terminal.FocusPoint == null)
            return false;

        return m_incapacitation == null || !m_incapacitation.IsIncapacitated;
    }

    /// <summary>
    /// 시점·이동 잠금을 짝 지어 넣고 뺀다. <b>커서는 풀지 않는다</b> — 커서가 풀리면
    /// <see cref="PlayerInteractor.HandleInteract"/>가 E를 통째로 막아(#352) 화면에서 나갈 수단이
    /// 사라진다. 이동은 시점과 별개라 따로 막는다.
    /// </summary>
    private void ApplyLocks(bool active)
    {
        if (active == m_locked)
            return;

        m_locked = active;

        // Push/Pop인 이유는 감정표현 휠(#219)이 같은 스위치를 쓰기 때문이다 — 단일 bool이면 휠을 닫을 때
        // 이쪽 잠금까지 풀린다.
        if (m_look != null)
        {
            if (active)
                m_look.PushLookSuspend();
            else
                m_look.PopLookSuspend();
        }

        if (m_movement != null)
            m_movement.SetViewLocked(active);
    }
}
