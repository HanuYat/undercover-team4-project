using UnityEngine;

/// <summary>
/// 무기를 든 표현 — 상체 자세 레이어를 켜고, 신병이 되면 무기를 손에서 치운다. 동네 깡패용. (#806)
///
/// 상체 레이어가 필요한 이유는 <b>1H 걷기·달리기 클립이 없어서</b>다. Idle만 무기 자세로 갈면
/// 서 있을 때만 파이프를 든 것처럼 보이고 쫓아올 때는 맨손처럼 팔을 흔든다. 다리는 원래 로코모션에
/// 맡기고 상체만 덮는 레이어(<c>WeaponUpperBody</c>, 기본 가중치 0)를 여기서 올린다 —
/// 레이어 자체는 공용 컨트롤러에 있지만 올리는 것은 무기를 든 개체뿐이라 시민은 그대로다.
///
/// <b>신병이 되면 내려놓는다</b> — 수갑 자세·누운 자세는 무기를 든 상체와 맞지 않고, 무엇보다
/// 파이프를 든 채 연행되는 그림이 어색하다. 상태가 확보·무력화군으로 가면 레이어를 내리고 무기를 숨긴다.
/// 상태는 동기화 값이라(<see cref="NpcController.OnStateChanged"/>) 전 피어에서 같은 순간에 갈린다.
/// </summary>
public class NpcWeaponHold : MonoBehaviour
{
    [Tooltip("손에 든 무기 오브젝트 — 신병이 되면 숨긴다")]
    [SerializeField]
    private GameObject m_weapon;

    [Tooltip("상체 자세 레이어 이름 — 컨트롤러의 레이어와 같아야 한다 (NpcAnimatorControllerBuilder가 만든다)")]
    [SerializeField]
    private string m_layerName = "WeaponUpperBody";

    [Tooltip("무기를 든 동안 상체 레이어에 줄 가중치")]
    [Range(0f, 1f)]
    [SerializeField]
    private float m_layerWeight = 1f;

    private NpcController m_controller;
    private Animator m_animator;
    private int m_layerIndex = -1;

    private void Awake()
    {
        m_controller = GetComponentInParent<NpcController>();
        m_animator = GetComponentInChildren<Animator>();

        if (m_animator != null)
            m_layerIndex = m_animator.GetLayerIndex(m_layerName);

        if (m_controller == null || m_animator == null || m_layerIndex < 0)
        {
            Debug.LogWarning(
                $"NpcWeaponHold: 컨트롤러·Animator·'{m_layerName}' 레이어 중 하나를 찾지 못해 무기 자세가 꺼진다",
                this
            );
            enabled = false;
        }
    }

    private void OnEnable()
    {
        m_controller.OnStateChanged += HandleStateChanged;
        Apply(m_controller.CurrentState);
    }

    private void OnDisable()
    {
        m_controller.OnStateChanged -= HandleStateChanged;
    }

    private void HandleStateChanged(NpcState state) => Apply(state);

    private void Apply(NpcState state)
    {
        bool holding = KeepsWeapon(state);

        m_animator.SetLayerWeight(m_layerIndex, holding ? m_layerWeight : 0f);

        if (m_weapon != null && m_weapon.activeSelf != holding)
            m_weapon.SetActive(holding);
    }

    // 무기를 계속 들고 있는 상태인가 — 신병이 됐거나(체포·연행·수감) 쓰러졌으면 내려놓는다.
    // 제외 목록 방식이라 새 상태는 기본이 '든 채'다: 쫓고·때리고·달아나는 국면이 전부 그쪽이다.
    private static bool KeepsWeapon(NpcState state) =>
        state != NpcState.Captured
        && state != NpcState.Escorted
        && state != NpcState.Jailed
        && state != NpcState.Stunned
        && state != NpcState.Dead;
}
