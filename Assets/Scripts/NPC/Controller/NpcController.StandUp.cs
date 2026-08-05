using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 누워 있던 몸이 일어나는 구간 (#513) — 전 피어에 모션을 알리고, 클립이 끝난 뒤 후속 동작을 실행한다.
///
/// 밧줄에 묶인 대상은 놓여 있어도 누워 있으므로(<see cref="IsTethered"/>), 일어나는 것은 줄이
/// <b>실제로 풀리는</b> 네 경로뿐이다: 방치 만료 탈주(<see cref="NpcCapturedState"/>) · E 풀기 ·
/// 유치장 착석 · 유치장 안 풀기(뒤 셋은 <see cref="PlayerEscortCommands"/>·<see cref="JailIntake"/>).
/// 전부 대상이 체포(<see cref="NpcState.Captured"/>)로 멈춰 있는 상태에서 온다.
///
/// 끌기(NpcController.Rope)에서 갈라 둔 이유는 관심사가 다르기 때문이다 — 저쪽은 "누가 어떻게 끄는가",
/// 이쪽은 "언제 일어나는가"다. 기절 기상(#269)과 같은 클립·같은 알림(<c>RaiseStandUp</c>)을 쓴다.
/// </summary>
public partial class NpcController
{
    // 일어난 뒤 할 일 — null이면 그 자리에 서기만 한다(유치장 안 풀기).
    private System.Action m_standUpNext;
    private bool m_standUpPending;
    private float m_standUpRemaining;
    // 일어나기 전에 쓰러진 채로 버티는 시간(초) — 0이면 곧바로 일어난다 (#513)
    private float m_standUpDownRemaining;

    // 위 예약의 클라 사본(서버만 쓴다) — 자세를 결정하는 값이라 전 피어가 알아야 한다.
    // 지속 상태이므로 순간 이벤트(RaiseStandUp)가 아니라 NetworkVariable로 나간다 (architecture.md 연출 전파 규칙).
    private readonly NetworkVariable<bool> m_standUpPendingSynced = new(false);

    /// <summary>
    /// 일어나기가 예약된 구간인가 — <b>쓰러져 기다리는 동안 + 기상 모션</b>을 함께 덮는다. 전 피어에서 유효. (#513)
    ///
    /// 이 구간은 아직 <see cref="NpcState.Captured"/>라 다시 묶을 수 있는 <b>재포획 창</b>이다.
    /// 폴링으로 부르는 쪽(JailIntake)이 후속 동작을 중복 예약하지 않도록 물어보는 값이기도 하다.
    ///
    /// <b>클라도 읽어야 하는 이유</b>: 누운 자세의 근거가 밧줄 표시(<see cref="IsTethered"/>)뿐이었는데,
    /// 풀기는 예약을 걸자마자 줄을 빼므로 <b>쓰러져 기다리는 동안 몸을 눕혀 둘 것이 아무것도 없었다</b> —
    /// 푸는 즉시 벌떡 서고 몇 초 뒤 이미 서 있는 몸에 기상 모션이 나왔다. NpcAnimationDriver가 이 값을
    /// 함께 보게 해서 그 구간의 자세를 여기가 든다.
    /// </summary>
    public bool IsStandingUp =>
        IsSpawned && !IsServer ? m_standUpPendingSynced.Value : m_standUpPending;

    // 예약 표시를 세운다/거둔다 — 서버(또는 오프라인)에서만. 동기화 값은 세션 중에만 의미가 있다.
    private void SetStandUpPending(bool value)
    {
        m_standUpPending = value;
        if (IsSpawned && IsServer)
            m_standUpPendingSynced.Value = value;
    }

    /// <summary>
    /// 줄이 풀리는 순간의 일어나기 — 전 피어에 모션을 알리고, 클립 길이만큼 지난 뒤 <paramref name="next"/>를
    /// 실행한다. 서버(또는 오프라인) 전용. (#513)
    ///
    /// 묶여 있지 않으면(제압만으로 잡힌 Captured 등 이미 서 있는 몸) 기다리지 않고 곧바로 실행한다 —
    /// 호출부마다 자세를 따로 판정하지 않게 여기서 한 번에 가른다.
    ///
    /// 모션 길이는 기절 기상과 같은 클립을 쓰므로 <see cref="NpcStunConfig.StandUpSeconds"/>를 공유한다.
    /// </summary>
    /// <param name="downSeconds">일어나기까지 쓰러져 있는 총 시간(초). 마지막 구간이 기상 모션이므로
    /// 이 값에서 클립 길이를 뺀 만큼 누워 있다가 일어난다. 0이면 곧바로 일어난다.
    /// 이 구간 전체가 재포획 창이다 — 밧줄은 무력화된 대상만 묶으므로(#446) 달려가면 도로 잡는다.</param>
    public void ServerStandUpThen(System.Action next, float downSeconds = 0f)
    {
        if (IsSpawned && !IsServer)
            return;

        if (!IsTethered && !IsRoped)
        {
            next?.Invoke();
            return;
        }

        if (m_standUpPending)
            return; // 이미 일어나는 중 — 폴링 호출부가 매 틱 불러도 한 번만 건다

        SetStandUpPending(true);
        m_standUpNext = next;
        m_standUpRemaining = m_stunConfig.StandUpSeconds;
        m_standUpDownRemaining = Mathf.Max(0f, downSeconds - m_stunConfig.StandUpSeconds);

        // 누워 있는 구간이 있으면 모션 알림을 그 뒤로 미룬다 — 지금 알리면 3초 내내 일어서 있게 된다
        if (m_standUpDownRemaining <= 0f)
            RaiseStandUp(); // 전 피어에 일어나는 모션 재생을 알린다 (기절 기상과 같은 순간 이벤트)
    }

    // 일어나기 예약 취소 — 예약된 후속 동작도 함께 버린다.
    // 표시를 거두는 곳이 여기 하나라, 재포획(StartRopeDrag)·넉백·재기절 이탈(TickStandUp)·정상 완료가
    // 전부 같은 정리를 탄다 — 하나만 빠지면 그 대상이 영원히 누운 자세로 남는다.
    private void CancelStandUp()
    {
        SetStandUpPending(false);
        m_standUpNext = null;
        m_standUpRemaining = 0f;
        m_standUpDownRemaining = 0f;
    }

    /// <summary>
    /// 일어나기 대기 — <see cref="Update"/>가 밧줄 장력 직후, 넉백·스턴 게이트보다 <b>앞</b>에서 돌린다.
    /// 게이트 뒤로 내리면 일어나는 도중 기절한 대상의 예약이 영원히 남는다.
    /// </summary>
    private void TickStandUp()
    {
        if (!m_standUpPending)
            return;

        // 밖에서 상황이 바뀌었으면 일어나기가 성립하지 않는다 — 후속 동작도 함께 버린다.
        // 그 상태에서 도주·석방·수감을 걸면 새 상황(넉백 비행·페널티 연행·재기절)을 덮어쓴다.
        // 버려진 대상은 그대로 체포 상태에 남아 방치 타이머가 다시 만료시킨다.
        if (CurrentState != NpcState.Captured || m_knockbackActive || HasStunOverlay)
        {
            CancelStandUp();
            return;
        }

        // 먼저 쓰러진 채로 버틴다 — 다 지나야 일어나는 모션이 나간다
        if (m_standUpDownRemaining > 0f)
        {
            m_standUpDownRemaining -= Time.deltaTime;
            if (m_standUpDownRemaining > 0f)
                return;

            RaiseStandUp();
        }

        m_standUpRemaining -= Time.deltaTime;
        if (m_standUpRemaining > 0f)
            return;

        System.Action next = m_standUpNext;
        CancelStandUp(); // 먼저 비운다 — next가 다시 예약을 걸 수 있다
        next?.Invoke();
    }
}
