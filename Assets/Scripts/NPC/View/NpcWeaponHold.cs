using UnityEngine;

/// <summary>
/// 무기를 든 표현 — 상체 자세 레이어를 켜고, 신병이 되면 무기를 손에서 치운다. 동네 깡패용. (#806)
///
/// 레이어가 필요한 이유는 <b>1H 걷기·달리기 클립이 없어서</b>다 — 로코모션 클립을 갈아 끼울 수 없으니
/// 팩의 마스크드 포즈를 오른팔에만 얹는다(<c>WeaponUpperBody</c>, 기본 가중치 0). 왼팔·다리는 원래대로
/// 움직이고 오른팔만 파이프를 걸친 자세로 고정된다. 레이어 자체는 공용 컨트롤러에 있지만 올리는 것은
/// 무기를 든 개체뿐이라 시민은 그대로다.
///
/// <b>신병이 되면 내려놓는다</b> — 수갑 자세·누운 자세는 무기를 든 상체와 맞지 않고, 무엇보다
/// 파이프를 든 채 연행되는 그림이 어색하다. 상태가 확보·무력화군으로 가면 레이어를 내리고 무기를 숨긴다.
/// 상태는 동기화 값이라(<see cref="NpcController.OnStateChanged"/>) 전 피어에서 같은 순간에 갈린다.
///
/// ⚠ <b>기절은 상태 enum으로 오지 않는다</b> (#292) — 테이저·체력 0 기절은 오버레이라 CurrentState가
/// 그대로다. 상태만 보면 <b>쓰러진 몸이 파이프를 쥔 채 어깨에 걸친 자세로 굳는다</b>(테이저 → 밧줄이
/// 표준 검거 경로라 자주 보인다). <see cref="NpcStun.IsStunned"/>를 함께 보고
/// <see cref="NpcStun.OnStunnedChanged"/>로 재판정을 태우는 것이 그 대비다 —
/// <see cref="NpcAnimationDriver"/>가 같은 함정을 같은 방식으로 피한다.
/// </summary>
public class NpcWeaponHold : MonoBehaviour
{
    [Tooltip("손에 든 무기 오브젝트 — 신병이 되면 숨긴다")]
    [SerializeField]
    private GameObject m_weapon;

    [Tooltip("무기 자세 레이어 이름 — 컨트롤러의 레이어와 같아야 한다 (NpcAnimatorControllerBuilder가 만든다)")]
    [SerializeField]
    private string m_layerName = "WeaponUpperBody";

    [Tooltip("무기를 든 동안 자세 레이어에 줄 가중치 — 낮추면 원래 팔 움직임이 섞인다")]
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
        m_controller.Stun.OnStunnedChanged += HandleStunnedChanged;
        Apply();
    }

    private void OnDisable()
    {
        m_controller.OnStateChanged -= HandleStateChanged;
        m_controller.Stun.OnStunnedChanged -= HandleStunnedChanged;
    }

    private void HandleStateChanged(NpcState state) => Apply();

    private void HandleStunnedChanged(bool stunned) => Apply();

    private void Apply()
    {
        // 기절 중이면 상태와 무관하게 내려놓는다 — 오버레이라 상태 enum이 그대로이기 때문이다 (#292)
        bool stunned = m_controller.Stun.IsStunned;
        NpcState state = m_controller.CurrentState;

        bool holding = !stunned && KeepsWeapon(state);

        m_animator.SetLayerWeight(m_layerIndex, !stunned && PosesWeapon(state) ? m_layerWeight : 0f);

        if (m_weapon != null && m_weapon.activeSelf != holding)
            m_weapon.SetActive(holding);
    }

    // 무기를 계속 들고 있는 상태인가 — 신병이 됐거나(체포·연행·수감) 쓰러졌으면 내려놓는다.
    // 제외 목록 방식이라 새 상태는 기본이 '든 채'다: 쫓고·달아나는 국면이 전부 그쪽이다.
    private static bool KeepsWeapon(NpcState state) =>
        state != NpcState.Captured
        && state != NpcState.Escorted
        && state != NpcState.Jailed
        && state != NpcState.Stunned
        && state != NpcState.Dead;

    // ⚠ 때리는 동안은 자세 레이어를 내린다 — 스윙은 <b>오른팔로 하는 전신 클립</b>이라
    // 같은 팔을 덮는 이 레이어가 위에 있으면 휘두르는 팔이 걸친 자세로 눌려 스윙이 보이지 않는다.
    // 그 구간에는 스윙 클립 자체가 무기를 든 자세를 그리므로 레이어가 필요 없다.
    private static bool PosesWeapon(NpcState state) =>
        KeepsWeapon(state) && state != NpcState.Attack;
}
