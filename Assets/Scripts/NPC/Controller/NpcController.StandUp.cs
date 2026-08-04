using UnityEngine;

/// <summary>
/// 누워 있던 몸이 일어나는 구간 (#513) — 전 피어에 모션을 알리고, 클립이 끝난 뒤 후속 동작을 실행한다.
///
/// 밧줄에 묶인 대상은 놓여 있어도 누워 있으므로(<see cref="IsTethered"/>), 일어나는 것은 줄이
/// <b>실제로 풀리는</b> 네 경로뿐이다: 방치 만료 탈주(<see cref="NpcCapturedState"/>) · 좌클릭 3초 풀기 ·
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

    /// <summary>지금 일어나는 모션 구간인가 — 서버(또는 오프라인) 전용.
    /// 이 구간은 아직 묶인 채 <see cref="NpcState.Captured"/>라 E로 다시 끌 수 있는 <b>재포획 창</b>이다. (#513)
    /// 폴링으로 부르는 쪽(JailIntake)이 후속 동작을 중복 예약하지 않도록 물어보는 값이기도 하다.</summary>
    public bool IsStandingUp => m_standUpPending;

    /// <summary>
    /// 줄이 풀리는 순간의 일어나기 — 전 피어에 모션을 알리고, 클립 길이만큼 지난 뒤 <paramref name="next"/>를
    /// 실행한다. 서버(또는 오프라인) 전용. (#513)
    ///
    /// 묶여 있지 않으면(제압만으로 잡힌 Captured 등 이미 서 있는 몸) 기다리지 않고 곧바로 실행한다 —
    /// 호출부마다 자세를 따로 판정하지 않게 여기서 한 번에 가른다.
    ///
    /// 모션 길이는 기절 기상과 같은 클립을 쓰므로 <see cref="NpcStunConfig.StandUpSeconds"/>를 공유한다.
    /// </summary>
    public void ServerStandUpThen(System.Action next)
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

        m_standUpPending = true;
        m_standUpNext = next;
        m_standUpRemaining = m_stunConfig.StandUpSeconds;
        RaiseStandUp(); // 전 피어에 일어나는 모션 재생을 알린다 (기절 기상과 같은 순간 이벤트)
    }

    // 일어나기 예약 취소 — 예약된 후속 동작도 함께 버린다.
    private void CancelStandUp()
    {
        m_standUpPending = false;
        m_standUpNext = null;
        m_standUpRemaining = 0f;
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

        m_standUpRemaining -= Time.deltaTime;
        if (m_standUpRemaining > 0f)
            return;

        System.Action next = m_standUpNext;
        CancelStandUp(); // 먼저 비운다 — next가 다시 예약을 걸 수 있다
        next?.Invoke();
    }
}
