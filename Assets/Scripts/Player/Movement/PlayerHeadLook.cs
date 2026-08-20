using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 시선 pitch를 캐릭터 머리(Neck/Head) 본에 반영한다 — 다른 플레이어 화면에서 어디를 보는지 보이게. (#348)
/// 오너가 PlayerLook의 pitch를 owner-write NetworkVariable로 전파하고(PlayerNameTag와 같은 패턴),
/// 모든 인스턴스가 LateUpdate(Animator 평가 이후)에서 본 회전을 얹는다.
/// yaw(좌우)는 몸통 전체가 NetworkTransform으로 돌므로 여기서는 pitch만 담당한다.
/// </summary>
public class PlayerHeadLook : NetworkBehaviour
{
    [Header("본 참조")]
    [SerializeField] private Transform m_neckBone;
    [SerializeField] private Transform m_headBone;

    [Header("튜닝")]
    [Tooltip("목이 담당하는 pitch 비율 — 나머지는 머리가 담당 (나눠 얹어야 목이 자연스럽다)")]
    [Range(0f, 1f)]
    [SerializeField] private float m_neckWeight = 0.4f;

    [Tooltip("본에 반영할 pitch 한계(도) — 카메라(±80°)를 그대로 주면 목이 부러져 보인다")]
    [SerializeField] private float m_maxVisualPitch = 60f;

    [Tooltip("pitch 추종 감쇠율(1/초) — 원격 인스턴스의 틱 단위 스텝을 부드럽게 만든다")]
    [SerializeField] private float m_pitchLerpSpeed = 15f;

    [Tooltip("다운 등으로 오버라이드를 켜고 끌 때 가중치 블렌드 감쇠율(1/초)")]
    [SerializeField] private float m_weightLerpSpeed = 8f;

    // 오너가 값이 유의미하게 바뀔 때만 써서 전파한다 — 미세 지터로 매 틱 전송되는 것을 막는 문턱값(도)
    private const float k_sendThreshold = 0.1f;

    private readonly NetworkVariable<float> m_syncedPitch = new NetworkVariable<float>(
        0f,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Owner
    );

    private PlayerLook m_look;
    private PlayerIncapacitation m_incapacitation; // 다운 중 오버라이드 차단용 — 없으면(테스트 구성) 항상 활성
    private PlayerRagdoll m_ragdoll; // 사망 래그돌 — 켜져 있는 동안 가중치를 즉시 0으로 (#506)
    private float m_displayPitch; // 실제 본에 반영 중인 pitch — 목표값을 지수 감쇠로 추종
    private float m_weight; // 오버라이드 가중치 0~1 — 다운 중 0으로 블렌드해 쓰러짐 애니메이션과 싸우지 않게

    /// <summary>
    /// 마지막으로 본에 <b>실제로 얹은</b> pitch(도) — ⚠ 진단용이다
    /// (<see cref="PlayerRagdoll"/>의 진입 머리 추적).
    ///
    /// 래그돌이 켜지면 이 컴포넌트는 아래에서 즉시 빠지므로 값이 갱신되지 않는다. 그래서 사망 시점에
    /// 이 값은 <b>죽기 직전 프레임에 얹혀 있던</b> 기울기이고, 그것이 시체로 복사된 뒤 물리에 어떻게
    /// 처리되는지가 지금 묻는 것이다. <b>0에 가까우면 그 테이크는 아무것도 증명하지 못한다.</b>
    /// </summary>
    public float LastAppliedTilt { get; private set; }

    private void Awake()
    {
        m_look = GetComponent<PlayerLook>();
        m_incapacitation = GetComponent<PlayerIncapacitation>();
        m_ragdoll = GetComponent<PlayerRagdoll>();
    }

    private void Update()
    {
        // 오너만 전파 — 오프라인 Play 테스트(IsSpawned=false)에서는 네트워크 변수를 건드리지 않는다
        if (!IsSpawned || !IsOwner || m_look == null)
            return;

        if (Mathf.Abs(m_look.Pitch - m_syncedPitch.Value) > k_sendThreshold)
            m_syncedPitch.Value = m_look.Pitch;
    }

    private void LateUpdate()
    {
        if (m_neckBone == null || m_headBone == null)
            return;

        // 래그돌 중에는 블렌드를 기다리지 않고 가중치를 <b>즉시</b> 0으로 떨어뜨린다 (#506).
        // 아래 m_weightLerpSpeed 감쇠(8/초)로는 0에 닿기까지 수백 ms가 걸리고, 그 사이 이 오버라이드가
        // 뼈 물리(그리고 부활 블렌드)와 같은 목 본을 두고 싸워 목이 홱 돌아간다.
        if (m_ragdoll != null && m_ragdoll.IsRagdollActive)
        {
            m_weight = 0f;
            return;
        }

        // 오너(및 오프라인 테스트)는 로컬 pitch를 직접, 원격은 동기화값을 사용
        bool useLocal = (!IsSpawned || IsOwner) && m_look != null;
        float target = useLocal ? m_look.Pitch : m_syncedPitch.Value;
        target = Mathf.Clamp(target, -m_maxVisualPitch, m_maxVisualPitch);

        // 다운 중엔 가중치를 0으로 — 동기화된 상태값 폴링이라 원격 뷰도 동일하게 꺼진다 (PlayerAnimationDriver와 같은 방식)
        bool active = m_incapacitation == null || !m_incapacitation.IsIncapacitated;

        // 프레임률 독립 지수 감쇠 — 원격의 틱 스텝·다운 전환을 부드럽게
        float pitchT = 1f - Mathf.Exp(-m_pitchLerpSpeed * Time.deltaTime);
        float weightT = 1f - Mathf.Exp(-m_weightLerpSpeed * Time.deltaTime);
        m_displayPitch = Mathf.Lerp(m_displayPitch, target, pitchT);
        m_weight = Mathf.Lerp(m_weight, active ? 1f : 0f, weightT);

        float applied = m_displayPitch * m_weight;
        LastAppliedTilt = applied; // 조기 리턴보다 앞이다 — 0도 "얹은 값이 0"이라는 정보다
        if (Mathf.Abs(applied) < 0.01f)
            return;

        // Animator가 놓은 포즈 위에 월드 기준 회전을 얹는다 — 본 로컬 축 방향(리그마다 제각각)에 기대지 않기 위함.
        // +pitch = 아래 보기(카메라 localEuler.x와 동일 부호), 캐릭터 오른쪽 축 기준 회전이 정확히 그 방향이다.
        Quaternion neckTilt = Quaternion.AngleAxis(applied * m_neckWeight, transform.right);
        Quaternion headTilt = Quaternion.AngleAxis(applied * (1f - m_neckWeight), transform.right);
        m_neckBone.rotation = neckTilt * m_neckBone.rotation;
        m_headBone.rotation = headTilt * m_headBone.rotation;
    }
}
