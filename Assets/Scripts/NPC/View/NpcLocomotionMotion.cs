using UnityEngine;

/// <summary>
/// 실제 이동 속도로 갈리는 모션을 제안하는 부품. (#502에서 NpcAnimationDriver에서 분리)
///
/// 아래 넷은 "한 FSM 상태 안에서 이동과 정지가 오간다"는 점이 같다. 그 구분은 서버 FSM 내부값이라
/// 클라가 모르므로 상태를 늘리는 대신 transform 이동량으로 가른다 — NetworkTransform이 움직여 주는
/// 값이라 별도 동기화가 없다. 경계는 전부 히스테리시스다(떨림 방지).
///
/// 연행·수감(#97/#228) · 저항(#254) · 침입 해제(#261) · 오검거 페널티(#277~#279)
/// </summary>
[RequireComponent(typeof(NpcAnimationDriver))]
public class NpcLocomotionMotion : MonoBehaviour, INpcMotionSource
{
    // 걷기 전환 임계값(m/s) — 꺼짐은 걷기 최저 속도(~1.6)보다 충분히 낮게 (#97)
    private const float k_moveOnSpeed = 0.5f;
    private const float k_moveOffSpeed = 0.25f;

    // 지수 평활 계수 — 평활이 식는 시간이 곧 정지 모션 전환 지연이다 (#97)
    private const float k_speedSmoothing = 25f;

    // 걷기↔달리기 전환 임계값(m/s) — 페널티 3단용 (#277~#279)
    private const float k_penaltyRunOnSpeed = 4f;
    private const float k_penaltyRunOffSpeed = 3.4f;

    [Header("자물쇠 해제 (#261)")]
    [Tooltip("해제 시작(Begin) 모션을 유지하는 시간(초) — 이후 반복(Loop)으로 넘어간다. Begin 클립 길이(0.63초)에 맞춘 값")]
    [SerializeField] private float m_unlockBeginSeconds = 0.63f;

    private NpcAnimationDriver m_driver;

    private Vector3 m_lastPosition;
    private float m_smoothedSpeed;

    private bool m_escortMoving;
    private bool m_resistMoving; // 저항(Attack) 추격 중 이동/정지 — 걷기 ↔ 버틴 자세 (#254)
    private bool m_intrudeMoving; // 침입(Intruding) 이동/해제 (#261)
    private int m_speedTierMotion; // 속도 3단 로코모션 번호(Idle/Walk/Run) — 오검거 페널티(#277~#279)와 밀수 운반(#991)이 공유한다

    // 해제 시작(Begin) 모션을 반복(Loop)으로 넘길 시각. 0 이하면 대기 중 아님 (#261)
    private float m_unlockBeginUntil;

    public ENpcMotionPriority Priority => ENpcMotionPriority.Locomotion;

    /// <summary>저항 추격 중인가 — 저항 base가 이동 여부로 갈려 기준 상태 매핑이 읽는다. (#254)</summary>
    public bool IsResistMoving => m_resistMoving;

    private void Awake() => m_driver = GetComponent<NpcAnimationDriver>();

    /// <summary>
    /// 기준 상태가 바뀌었다 — 판별 플래그와 속도 평활을 새 상태에 맞춰 시드한다. 직전 상태의
    /// 잔여 속도가 첫 전환 판정을 오염시키지 않게 하는 것이 요지다. (#97/#254/#261/#277~)
    /// </summary>
    public void OnBaseStateChanged(NpcState state)
    {
        m_unlockBeginUntil = 0f;

        if (state == NpcState.Attack)
        {
            // 저항 진입은 추격으로 시작하는 것이 일반적이라 달리기로 시드한다 (#254)
            m_resistMoving = true;
            Reseed(k_moveOnSpeed);
        }
        else if (IsHandcuffedMotion(state))
        {
            m_escortMoving = true;
            Reseed(k_moveOnSpeed);
        }
        else if (IsSpeedTierMotion(state))
        {
            m_speedTierMotion = state == NpcState.Chasing ? (int)NpcState.Run : (int)NpcState.Walk;
            Reseed(state == NpcState.Chasing ? k_penaltyRunOnSpeed : k_moveOnSpeed);
        }
        else if (state == NpcState.Intruding)
        {
            // 침입 진입은 언제나 걷기로 시작한다(자물쇠까지 이동)
            m_intrudeMoving = true;
            Reseed(k_moveOnSpeed);
        }
    }

    /// <summary>속도 추적을 지금 위치에서 다시 시작한다 — 풀린 직후 속도가 튀지 않게. (#369/#513)</summary>
    public void ResetSpeedTracking() => Reseed(0f);

    private void Reseed(float speed)
    {
        m_lastPosition = transform.position;
        m_smoothedSpeed = speed;
    }

    /// <summary>속도를 갱신하고 판별을 진행한다 — 멈춰야 하는 구간에서는 위치만 따라간다.</summary>
    public void Tick(float deltaTime)
    {
        if (m_driver.SuppressLocomotion)
        {
            // 위치는 계속 따라간다 — 풀린 직후 이동량이 한 프레임에 몰리는 것을 막는다
            m_lastPosition = transform.position;
            return;
        }

        if (!IsSpeedDriven(m_driver.BaseState))
            return;

        float rawSpeed = (transform.position - m_lastPosition).magnitude / deltaTime;
        m_lastPosition = transform.position;
        m_smoothedSpeed = Mathf.Lerp(m_smoothedSpeed, rawSpeed, deltaTime * k_speedSmoothing);

        switch (m_driver.BaseState)
        {
            case NpcState.Attack:
                TickResist();
                break;
            case NpcState.Intruding:
                TickIntrude();
                break;
            default:
                if (IsSpeedTierMotion(m_driver.BaseState))
                    TickSpeedTier();
                else
                    TickEscort();
                break;
        }
    }

    public bool TryGetMotion(out int animState)
    {
        animState = 0;
        if (m_driver.SuppressLocomotion || !IsSpeedDriven(m_driver.BaseState))
            return false;

        switch (m_driver.BaseState)
        {
            case NpcState.Attack: // 추격이면 달리기, 사거리 안에서 멈추면 버틴 자세 (#254)
                animState = m_resistMoving ? (int)NpcState.Run : (int)NpcState.Idle;
                return true;

            case NpcState.Intruding:
                animState = m_intrudeMoving
                    ? (int)NpcState.Walk
                    : (m_unlockBeginUntil > 0f ? NpcAnimStates.k_unlockingBegin : NpcAnimStates.k_unlockingLoop);
                return true;

            default:
                if (IsSpeedTierMotion(m_driver.BaseState))
                {
                    animState = m_speedTierMotion;
                    return true;
                }

                // 연행·수감: 정지 중에는 수갑 찬 대기 자세(Captured 모션)를 빌린다 — FSM 상태는 그대로다
                animState = m_escortMoving ? (int)NpcState.Escorted : (int)NpcState.Captured;
                return true;
        }
    }

    // 저항 이동/정지 (#254)
    private void TickResist()
    {
        if (m_resistMoving && m_smoothedSpeed < k_moveOffSpeed)
            m_resistMoving = false;
        else if (!m_resistMoving && m_smoothedSpeed > k_moveOnSpeed)
            m_resistMoving = true;
    }

    // 침입: "자물쇠까지 걷기 ↔ 도착 후 해제"가 한 FSM 상태 안에서 일어난다 — 해제 중엔 완전히
    // 멈추므로 속도 판별이 정확하다 (#231/#261)
    private void TickIntrude()
    {
        if (m_intrudeMoving && m_smoothedSpeed < k_moveOffSpeed)
        {
            m_intrudeMoving = false;
            m_unlockBeginUntil = Time.time + m_unlockBeginSeconds;
        }
        else if (!m_intrudeMoving && m_smoothedSpeed > k_moveOnSpeed)
        {
            m_intrudeMoving = true;
            m_unlockBeginUntil = 0f;
        }
        // 시작 동작이 끝나면 반복으로 넘긴다 — exit time에 맡기면 Begin 번호가 남아 무한 반복된다
        else if (m_unlockBeginUntil > 0f && Time.time >= m_unlockBeginUntil)
        {
            m_unlockBeginUntil = 0f;
        }
    }

    // 속도 3단 로코모션 — 페널티(#277~)와 밀수 운반(#991)이 함께 쓴다. 히스테리시스가 두 겹이라 경계 사이에서는 현재 모션을 유지한다 (#277~)
    private void TickSpeedTier()
    {
        if (m_smoothedSpeed < k_moveOffSpeed)
            m_speedTierMotion = (int)NpcState.Idle;
        else if (m_smoothedSpeed > k_penaltyRunOnSpeed)
            m_speedTierMotion = (int)NpcState.Run;
        else if (m_smoothedSpeed > k_moveOnSpeed && m_smoothedSpeed < k_penaltyRunOffSpeed)
            m_speedTierMotion = (int)NpcState.Walk;
    }

    // 연행은 "따라 걷기 ↔ 근접 정지", 수감은 "감옥까지 걷기 ↔ 수용 정지" (#97/#228)
    private void TickEscort()
    {
        if (m_escortMoving && m_smoothedSpeed < k_moveOffSpeed)
            m_escortMoving = false;
        else if (!m_escortMoving && m_smoothedSpeed > k_moveOnSpeed)
            m_escortMoving = true;
    }

    /// <summary>이 기준 상태의 모션을 속도로 가르는가 — 아니면 기준 상태 모션이 그대로 쓰인다.</summary>
    private static bool IsSpeedDriven(NpcState state) =>
        IsHandcuffedMotion(state)
        || IsSpeedTierMotion(state)
        || state == NpcState.Attack
        || state == NpcState.Intruding;

    /// <summary>모션이 속도 3단(Idle/Walk/Run)으로 갈리는 상태인가 — 페널티(#277~)와 밀수 운반(#991).
    /// 밀수 운반은 맞으면 달리기로 올라가므로(SmugglerCargo.ServerPanic) 걷기 하나로 못 박을 수 없다.</summary>
    private static bool IsSpeedTierMotion(NpcState state) =>
        IsPenaltyLocomotion(state) || state == NpcState.Smuggling;

    /// <summary>수갑 찬 채 이동하는 상태인가 — 수감은 대응 Animator 상태가 없어 연행 모션을 빌린다. (#228)</summary>
    public static bool IsHandcuffedMotion(NpcState state) =>
        state is NpcState.Escorted or NpcState.Jailed;

    /// <summary>오검거 페널티 상태인가 — 앵그리 마크(#280) 표시 조건과 같은 집합이다. (#277~#279)</summary>
    public static bool IsPenaltyLocomotion(NpcState state) =>
        state is NpcState.Detained or NpcState.Chasing or NpcState.PenaltyEscorting;
}
