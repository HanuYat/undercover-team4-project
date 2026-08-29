using UnityEngine;

/// <summary>
/// 실제 이동 속도로 갈리는 모션을 제안하는 부품. (#502에서 NpcAnimationDriver에서 분리)
///
/// <b>왜 속도를 보는가.</b> 아래 넷은 전부 "한 FSM 상태 안에서 이동과 정지가 오간다" —
/// 그 구분은 서버 FSM 내부값이라 클라이언트가 모른다. 상태를 늘리는 대신 <b>transform 이동량</b>으로
/// 가른다. 클라이언트에서도 NetworkTransform이 움직여 주는 값을 그대로 쓸 수 있어 별도 동기화가 없다.
///
/// <list type="bullet">
/// <item>연행·수감(#97/#228) — 따라 걷기 ↔ 근접 정지</item>
/// <item>저항(#254) — 추격 달리기 ↔ 사거리 안 버틴 자세</item>
/// <item>침입(#261) — 자물쇠까지 걷기 ↔ 도착 후 해제(Begin→Loop)</item>
/// <item>오검거 페널티(#277~#279) — 속도 3단(Idle/Walk/Run)</item>
/// </list>
///
/// 경계는 전부 <b>히스테리시스</b>다(켜짐/꺼짐 임계값을 다르게 둔다) — 감속 구간에서 모션이 떨리는 것을 막는다.
/// </summary>
[RequireComponent(typeof(NpcAnimationDriver))]
public class NpcLocomotionMotion : MonoBehaviour, INpcMotionSource
{
    // 연행 근접 정지(#97) 모션 전환 임계값 — 실제 이동 속도(m/s) 기준.
    // 꺼짐 임계값은 걷기 최저 속도(~1.6m/s)보다 충분히 낮게 — 추종 중 순간 감속에 오작동하지 않는 선
    private const float k_moveOnSpeed = 0.5f;
    private const float k_moveOffSpeed = 0.25f;

    // 프레임 노이즈 완화용 지수 평활 계수 — 클수록 정지 반응이 빨라진다.
    // 근접 정지가 velocity를 즉시 0으로 끊으므로(#97) 평활이 식는 시간이 곧 모션 전환 지연이다
    private const float k_speedSmoothing = 25f;

    // 오검거 페널티 상태(#277~#279)의 걷기↔달리기 전환 임계값(m/s).
    // 추격 가속(걷기 속도→7)이 이 경계를 지나는 순간 달리기 모션으로 넘어간다
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
    private int m_penaltyMotion; // 오검거 페널티의 현재 로코모션 번호(Idle/Walk/Run) (#277~#279)

    // 해제 시작(Begin) 모션을 반복(Loop)으로 넘길 시각. 0 이하면 대기 중 아님 (#261)
    private float m_unlockBeginUntil;

    public ENpcMotionPriority Priority => ENpcMotionPriority.Locomotion;

    /// <summary>
    /// 저항 추격 중인가 — 기준 상태 매핑이 읽는다. (#254)
    /// 저항(Attack)의 base는 이동 여부로 갈리는데, 그 판별을 하는 것이 이 부품이다.
    /// </summary>
    public bool IsResistMoving => m_resistMoving;

    private void Awake() => m_driver = GetComponent<NpcAnimationDriver>();

    /// <summary>
    /// 기준 상태가 바뀌었다 — 판별 플래그와 속도 평활을 새 상태에 맞춰 시드한다.
    /// 직전 상태의 잔여 속도가 첫 전환 판정을 오염시키지 않게 하는 것이 요지다
    /// (예: 침입 진입 직후 잔여 속도가 낮으면 자물쇠에 도착하기도 전에 해제 모션이 나온다). (#97/#254/#261/#277~)
    /// </summary>
    public void OnBaseStateChanged(NpcState state)
    {
        m_unlockBeginUntil = 0f;

        if (state == NpcState.Attack)
        {
            // 저항 진입은 추격으로 시작하는 것이 일반적이라 달리기로 시드한다.
            // 표적이 이미 사거리 안이면 다음 몇 프레임 안에 버틴 자세로 낮아진다. (#254)
            m_resistMoving = true;
            Reseed(k_moveOnSpeed);
        }
        else if (IsHandcuffedMotion(state))
        {
            m_escortMoving = true;
            Reseed(k_moveOnSpeed);
        }
        else if (IsPenaltyLocomotion(state))
        {
            m_penaltyMotion = state == NpcState.Chasing ? (int)NpcState.Run : (int)NpcState.Walk;
            Reseed(state == NpcState.Chasing ? k_penaltyRunOnSpeed : k_moveOnSpeed);
        }
        else if (state == NpcState.Intruding)
        {
            // 침입 진입은 언제나 걷기로 시작한다(자물쇠까지 이동).
            m_intrudeMoving = true;
            Reseed(k_moveOnSpeed);
        }
    }

    /// <summary>속도 추적을 지금 위치에서 다시 시작한다 — 끌림이 풀린 직후 이동량이 몰려 속도가 튀지 않게. (#369/#513)</summary>
    public void ResetSpeedTracking() => Reseed(0f);

    private void Reseed(float speed)
    {
        m_lastPosition = transform.position;
        m_smoothedSpeed = speed;
    }

    /// <summary>
    /// 속도를 갱신하고 판별을 진행한다 — 드라이버가 결정 직전에 부른다.
    /// 로코모션이 멈춰 있어야 하는 구간(끌려 누움·스턴)에서는 위치만 따라가고 판별은 건너뛴다.
    /// </summary>
    public void Tick(float deltaTime)
    {
        if (m_driver.SuppressLocomotion)
        {
            // 위치는 계속 따라간다 — 풀린 직후 그동안의 이동량이 한 프레임에 몰려 속도가 튀는 것을 막는다
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
                if (IsPenaltyLocomotion(m_driver.BaseState))
                    TickPenalty();
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
            case NpcState.Attack:
                // 추격 중이면 달리기, 사거리 안에서 멈추면 버틴 자세 (#254)
                animState = m_resistMoving ? (int)NpcState.Run : (int)NpcState.Idle;
                return true;

            case NpcState.Intruding:
                animState = m_intrudeMoving
                    ? (int)NpcState.Walk
                    : (m_unlockBeginUntil > 0f ? NpcAnimStates.k_unlockingBegin : NpcAnimStates.k_unlockingLoop);
                return true;

            default:
                if (IsPenaltyLocomotion(m_driver.BaseState))
                {
                    animState = m_penaltyMotion;
                    return true;
                }

                // 연행·수감: 정지 중에는 수갑 찬 대기 자세(Captured 모션)를 빌려 쓴다 — FSM 상태는 그대로다
                animState = m_escortMoving ? (int)NpcState.Escorted : (int)NpcState.Captured;
                return true;
        }
    }

    // 저항(Attack) 이동/정지 — 추격 중이면 달리기, 사거리 안에서 멈추면 버틴 자세. (#254)
    private void TickResist()
    {
        if (m_resistMoving && m_smoothedSpeed < k_moveOffSpeed)
            m_resistMoving = false;
        else if (!m_resistMoving && m_smoothedSpeed > k_moveOnSpeed)
            m_resistMoving = true;
    }

    // 침입(Intruding): "자물쇠까지 걷기 ↔ 도착 후 해제"가 한 FSM 상태 안에서 일어난다 (#231/#261).
    // 해제 페이즈는 NpcIntrudeState 내부값이라 클라이언트가 알 수 없으므로 속도로 가른다 —
    // 해제 중엔 그 자리에 완전히 멈추므로 이 판별이 정확하다.
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
        // 시작 동작이 끝나면 반복으로 넘긴다. Animator의 exit time에 맡기지 않는 이유는, 번호가
        // Begin에 머물러 있으면 Loop로 넘어간 뒤에도 Any State 조건이 참이라 다시 Begin으로 끌려가
        // 동작이 무한 반복되기 때문이다. (TryGetMotion이 이 타이머로 Begin/Loop를 고른다)
        else if (m_unlockBeginUntil > 0f && Time.time >= m_unlockBeginUntil)
        {
            m_unlockBeginUntil = 0f;
        }
    }

    // 오검거 페널티 로코모션 — 속도 기준 Idle/Walk/Run 3단. 히스테리시스는 걷기 경계(연행과 공유)와
    // 달리기 경계 두 겹이다. 경계 사이 속도에서는 현재 모션을 유지해 떨림을 막는다 (#277~#279)
    private void TickPenalty()
    {
        if (m_smoothedSpeed < k_moveOffSpeed)
            m_penaltyMotion = (int)NpcState.Idle;
        else if (m_smoothedSpeed > k_penaltyRunOnSpeed)
            m_penaltyMotion = (int)NpcState.Run;
        else if (m_smoothedSpeed > k_moveOnSpeed && m_smoothedSpeed < k_penaltyRunOffSpeed)
            m_penaltyMotion = (int)NpcState.Walk;
    }

    // 연행(Escorted)은 "따라 걷기 ↔ 근접 정지"가, 수감(Jailed)은 "유치장까지 걷기 ↔ 수용 정지"가
    // 각각 한 FSM 상태 안에서 일어난다 (#97/#228).
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
        || IsPenaltyLocomotion(state)
        || state == NpcState.Attack
        || state == NpcState.Intruding;

    /// <summary>
    /// 수갑 찬 채 이동하는 상태인가 — 걷기(Escorted 모션) ↔ 정지(Captured 모션)를 속도로 구분하는 상태들.
    /// 수감(Jailed)은 대응하는 Animator 상태가 없어 연행 모션을 빌려 쓴다 (#228).
    /// </summary>
    public static bool IsHandcuffedMotion(NpcState state) =>
        state is NpcState.Escorted or NpcState.Jailed;

    /// <summary>
    /// 오검거 페널티 상태인가 — 수갑 없이 걷기/달리기 로코모션을 속도로 가르는 상태들 (#277~#279).
    /// 앵그리 마크(#280) 표시 조건과 같은 집합이다 — 페널티에 얽힌 동안 계속 표시된다.
    /// </summary>
    public static bool IsPenaltyLocomotion(NpcState state) =>
        state is NpcState.Detained or NpcState.Chasing or NpcState.PenaltyEscorting;
}
