using UnityEngine;

/// <summary>
/// 상태 전이가 아니라 <b>순간 이벤트</b>로 오는 단발 모션을 제안하는 부품. (#502에서 NpcAnimationDriver에서 분리)
///
/// 스윙(#220)·기상(#269/#513)·제압 전환(#332) 셋 다 FSM 상태를 바꾸지 않는다 — 저항 중에 스윙하고,
/// 기절한 채로 일어나고, Captured 진입 위에 전환 연출을 얹는다. 그래서 기준 상태 모션보다 위에서
/// 잠깐 덮어썼다가 유지 시간이 끝나면 물러난다.
///
/// <b>왜 트리거가 아니라 번호 펄스인가.</b> 이 컨트롤러의 로코모션이 전부 Any State(State==N) 전이라,
/// 트리거 오버레이를 쓰면 매 프레임 로코모션 전이가 단발 클립을 끊어 버린다. State int 하나만
/// 이 모션을 가리키고 있어야 클립이 끝까지 재생된다. (#220에서 정해진 방식)
/// </summary>
[RequireComponent(typeof(NpcAnimationDriver))]
public class NpcOneShotMotion : MonoBehaviour, INpcMotionSource
{
    [Header("공격 스윙 (#220)")]
    [Tooltip("스윙 1회당 Attack(단발) 모션을 유지하는 시간(초) — 이후 버틴 자세로 복귀한다. 저항 공격 주기보다 짧고 타격 오프셋보다 길게")]
    [SerializeField] private float m_swingAnimSeconds = 0.9f;

    [Header("일어나기 (#269/#513)")]
    [Tooltip("일어나기 모션을 유지하는 시간(초) — 이 뒤에는 기준 상태 모션으로 되돌린다. NpcStunConfig.StandUpSeconds와 같은 클립이라 값도 같게 둘 것 (2배속을 걷으며 0.585→1.17, #572)")]
    [SerializeField] private float m_standUpSeconds = 1.17f;

    [Header("제압 전환 (#332)")]
    [Tooltip("도주형 제압 시 구르기 모션을 유지하는 시간(초) — 클립(Roll01) 길이 1.3초에 맞춘 값. 이후 그로기로 넘어간다")]
    [SerializeField] private float m_subdueRollSeconds = 1.3f;

    // "제안 없음" 표시 — 0은 Idle이라 쓸 수 없다
    private const int k_noMotion = -1;

    private NpcAnimationDriver m_driver;

    // 현재 스윙 모션을 유지할 종료 시각. 0 이하면 스윙 중 아님 (#220)
    private float m_swingUntil;

    // 제압 전환 모션 — 지금 재생 중인 번호와, 구르기의 종료 시각. 그로기는 시간으로 끝나지 않는다:
    // 플레이어가 연행(E)하러 올 때까지 헤롱거리며 유지되고, 상태 전이가 오면 그때 걷힌다. (#332)
    private int m_subdueMotion = k_noMotion;
    private float m_subdueRollUntil;

    // 일어나는 모션을 붙들고 있는 중인가 (#269/#513). 누움 콜라이더(#363) 판정에도 쓰인다.
    private bool m_standingUp;
    private float m_standUpUntil;

    public ENpcMotionPriority Priority => ENpcMotionPriority.OneShot;

    /// <summary>
    /// 기상 모션이 재생 중인가 — 누움 판정(<see cref="NpcAnimationDriver.IsProne"/>)이 읽는다. (#363/#513)
    /// 몸이 일어나기 시작한 순간부터 콜라이더도 함께 선다.
    /// </summary>
    public bool IsStandingUp => m_standingUp;

    private void Awake() => m_driver = GetComponent<NpcAnimationDriver>();

    /// <summary>
    /// 저항 NPC의 공격 스윙 1회. (#220)
    /// variant는 서버가 뽑아 전 피어에 넘긴 클립 index — 데미지는 서버가 그 클립의 타격 오프셋에
    /// 맞춰 넣으므로, 같은 클립을 재생만 하면 주먹 닿는 순간과 HP 감소가 일치한다.
    /// </summary>
    public void PlaySwing(int variant)
    {
        // 상태 변경(NetworkVariable)과 스윙 알림(ClientRpc)은 서로 다른 네트워크 경로라 도착 순서가
        // 보장되지 않는다. 제압 직전에 발사된 스윙이 Captured 전이보다 늦게 도착하면 이미 제압된
        // NPC가 원격 클라에서 헛스윙을 한다.
        //
        // '스윙이 성립할 수 없는 상태'에서만 막는다 — 이 상태들은 저항으로 되돌아가지 않으므로 늦게
        // 온 스윙은 무조건 유령이다. 반대로 배회(Idle/Walk/Run)는 막지 않는다: 상태 동기화가 늦어
        // 아직 Attack을 못 받았을 뿐일 수 있고, 그때 버리면 예고 동작이 통째로 사라져 "언제 맞는지
        // 모른다"는 #220의 목적이 깨진다.
        if (!CanSwingIn(m_driver.BaseState))
            return;

        // 클립 지정은 반드시 모션 전환보다 먼저다 — Attack 상태에 들어간 뒤에 바꾸면 재생 중인
        // 클립이 도중에 갈아끼워져 모션이 튄다.
        m_driver.SetSwingVariant(variant);
        m_swingUntil = Time.time + m_swingAnimSeconds;
    }

    /// <summary>
    /// 누워 있던 몸이 일어나는 모션. (#269/#513)
    /// FSM 상태는 그대로다 — 기절 중이면 Stunned, 줄이 풀리는 중이면 Captured. NPC는 제자리에 있고
    /// 모션만 누운 자세에서 일어나는 자세로 바뀐다.
    /// </summary>
    public void PlayStandUp()
    {
        // 늦게 도착한 알림 — 누워 있을 수 없는 기준 상태면 유령 모션이 된다 (스윙과 같은 방어).
        // 기절(Stunned)에 더해 체포(Captured)를 받는다: 줄이 풀리며 일어나는 경로는 FSM 상태가
        // Captured인 채로 오기 때문이다 (#513).
        if (m_driver.BaseState != NpcState.Stunned && m_driver.BaseState != NpcState.Captured)
            return;

        m_standingUp = true;
        m_standUpUntil = Time.time + m_standUpSeconds;
        m_driver.RefreshProne(); // 몸이 일어나기 시작했다 — 콜라이더도 같이 선다 (#363)
    }

    /// <summary>
    /// 일어나던 것을 취소한다 — 다시 묶였을 때. (#513)
    /// 끌려가는데 서 있으면 안 되므로 누운 자세로 되돌아간다.
    /// </summary>
    public void CancelStandUp()
    {
        m_standingUp = false;
        m_standUpUntil = 0f;
    }

    /// <summary>
    /// 기준 상태가 바뀌었다 — 단발 모션을 정리하고, 제압 전환이면 새로 시드한다.
    /// 상태 전이는 스윙보다 우선한다: 저항 중 스윙하다 제압되면 그 프레임에 Captured로 넘어가야 한다. (#220)
    /// </summary>
    public void OnBaseStateChanged(NpcState state, NpcState previous)
    {
        m_swingUntil = 0f;
        m_subdueMotion = k_noMotion;
        m_subdueRollUntil = 0f;

        // 일어나기 표시는 <b>다시 누울 수 있는 상태</b>로 갈 때만 내린다 (#513).
        // 재포획(Escorted)·재기절(Stunned)은 몸이 도로 눕는 전이라 내려야 하고 — 내리지 않으면
        // 끌려가는 몸이 선 자세로 남는다. 다음 기절에서 누움 판정이 굳는 것도 이 정리가 막는다.
        // 반대로 도주·수감·배회 복귀는 <b>일어난 결과</b>라 유지해야 한다: 묶임 표시를 걷는 것은
        // PlayerEscorter의 매 프레임 정리라 한 박자 늦고, 그 사이에 내리면 그 프레임에 도로 눕는다.
        // 사망(#571)도 '다시 눕는' 쪽이다 — 일어나던 도중에 죽으면 그 모션이 끊기고 그대로 쓰러진다.
        if (state is NpcState.Stunned or NpcState.Escorted or NpcState.Captured or NpcState.Dead)
            CancelStandUp();

        // 제압 순간의 전환 연출 (#332) — 도주·저항 중이던 NPC가 Captured로 넘어오면 곧바로 대기
        // 자세로 스냅하지 않고 유형별 전환 모션을 거친다. 상태는 동기화 값이라 직전 상태 추적도
        // 모든 피어에서 같게 흐른다 — 전 화면에서 같은 전환이 보인다.
        if (state != NpcState.Captured)
            return;

        switch (previous)
        {
            case NpcState.Attack:
                m_subdueMotion = NpcAnimStates.k_subdueGroggy;
                break;

            // 질주(#106)도 달리다 붙잡힌 관성은 같다 — 도주와 같은 구르기 전환을 탄다
            case NpcState.Run:
            case NpcState.Sprinting:
                m_subdueMotion = NpcAnimStates.k_subdueRoll;
                m_subdueRollUntil = Time.time + m_subdueRollSeconds;
                break;
        }
        // 그 외(수갑 채널링 체포·기절 후 재제압 등)는 저항 없이 잡히는 그림이라 전환이 없다 —
        // 기준 상태 모션(Captured 대기 자세)이 그대로 쓰인다.
    }

    /// <summary>유지 시간을 진행한다 — 드라이버가 결정 직전에 부른다.</summary>
    public void Tick()
    {
        if (m_swingUntil > 0f && Time.time >= m_swingUntil)
            m_swingUntil = 0f;

        // 구르기가 끝나면 그로기로 넘어간다 (#332) — 역동적으로 굴러 일어난 몸이 곧바로 얌전해지는
        // 낙차가 어색해서(팀 피드백) 그로기를 한 번 더 거친다. 그로기는 상태 전이가 올 때까지 유지된다.
        if (m_subdueRollUntil > 0f && Time.time >= m_subdueRollUntil)
        {
            m_subdueRollUntil = 0f;
            m_subdueMotion = NpcAnimStates.k_subdueGroggy;
        }

        // 기상 유지 시간이 끝나면 물러난다 (#513). 상태 전이가 뒤따르는 경로는 그쪽이 먼저 이어받으므로
        // 여기 오지 않고, 안 오는 경로(유치장 안에서 줄만 푼 경우)만 여기서 받는다 — 없으면 일어난
        // 마지막 프레임에 굳는다.
        //
        // 두 가지 이유로 <b>버틴다</b>:
        //  · IsStandingUp(서버가 켜 두는 "아직 일어나는 중" 표시)이 살아 있으면, 일어난 뒤에 올 상태
        //    변경(배회 복귀·도주·수감)이 아직 안 온 것이다. 그때 물러나면 기준 상태가 아직 Captured라
        //    수갑 자세가 한 프레임 스친다 (#564). 서버가 상태 변경과 같은 프레임에 내리므로 기다리면 맞물린다.
        //  · 기준 상태가 아직 누운 상태(기절)면 물러나 봐야 도로 눕는다 — 곧 도착할 상태 전이가
        //    이어받는 것이 맞다 (#269의 기존 동작).
        if (m_standUpUntil > 0f && Time.time >= m_standUpUntil && m_driver.CanLeaveStandUp)
        {
            m_standUpUntil = 0f;
            // m_standingUp은 내리지 않는다 — 누움 판정에서 "이미 일어난 몸"이라는 뜻이라,
            // 여기서 내리면 콜라이더가 도로 눕는다. 상태 전이나 재결박이 정리한다.
        }
    }

    public bool TryGetMotion(out int animState)
    {
        // 우선순위는 재생 시간이 짧은 쪽부터 — 스윙 > 기상 > 제압 전환.
        // 셋이 동시에 성립하는 경로는 없다(각자 성립 상태가 배타적이다). 순서를 못 박아 두는 것은
        // 늦게 도착한 네트워크 알림이 겹칠 때 화면이 피어마다 달라지지 않게 하기 위해서다.
        if (m_swingUntil > 0f)
        {
            animState = (int)NpcState.Attack;
            return true;
        }

        if (m_standUpUntil > 0f)
        {
            animState = NpcAnimStates.k_standUp;
            return true;
        }

        if (m_subdueMotion != k_noMotion)
        {
            animState = m_subdueMotion;
            return true;
        }

        animState = 0;
        return false;
    }

    /// <summary>
    /// 스윙 모션이 성립할 수 있는 기준 상태인가 — 구속·무력화 상태에서는 공격이 나올 수 없다. (#220)
    /// 오검거 페널티 상태(#277~#279)도 저항으로 되돌아가지 않으므로 늦게 도착한 스윙은 유령이다.
    /// </summary>
    private static bool CanSwingIn(NpcState state) =>
        state is not (
            NpcState.Captured or NpcState.Escorted or NpcState.Stunned
            or NpcState.Detained or NpcState.Chasing or NpcState.PenaltyEscorting
        );
}
