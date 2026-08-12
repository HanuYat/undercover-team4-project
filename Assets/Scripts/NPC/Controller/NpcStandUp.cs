using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 기상 예약 도메인 부품 (#513/#503) — 누워 있던 몸이 일어나는 구간을 들고 있다.
/// 전 피어에 모션을 알리고, 클립이 끝난 뒤 후속 동작을 실행한다.
///
/// 묶인 대상은 놓여 있어도 누워 있으므로(<see cref="NpcRopeDrag.IsTethered"/>) 일어나는 것은 줄이
/// <b>실제로 풀리는</b> 경로뿐이다 — 방치 만료 탈주 · E 풀기 · 유치장 착석 · 유치장 안 풀기 ·
/// 거리 초과 끊김(#526). 전부 대상이 <see cref="NpcState.Captured"/>로 멈춰 있는 상태에서 온다.
///
/// 밧줄(<see cref="NpcRopeDrag"/>)에서 갈라 둔 이유는 관심사가 달라서다 — 저쪽은 "누가 어떻게 끄는가",
/// 이쪽은 "언제 일어나는가". 기절 기상(#269)과 같은 클립·같은 알림(코어의 RaiseStandUp)을 쓴다.
/// </summary>
public class NpcStandUp : NetworkBehaviour
{
    private NpcController m_owner;

    // 일어난 뒤 할 일 — null이면 그 자리에 서기만 한다(유치장 안 풀기).
    private System.Action m_standUpNext;
    private bool m_standUpPending;
    private float m_standUpRemaining;

    // 일어나기 전에 쓰러진 채로 버티는 시간(초) — 0이면 곧바로 일어난다 (#513)
    private float m_standUpDownRemaining;

    // 위 예약의 클라 사본 — 자세를 결정하는 값이라 전 피어가 알아야 한다. 순간 이벤트가 아니라
    // 지속 상태이므로 NetworkVariable로 나간다 (architecture.md 연출 전파 규칙).
    private readonly NetworkVariable<bool> m_standUpPendingSynced = new(false);

    // 기상 <b>모션이 실제로 도는</b> 구간인가 — 위 예약(m_standUpPending)의 <b>뒷부분</b>만이다.
    // 앞부분(쓰러진 채 버티기)과 갈라야 하는 이유는 재포획 창이 거기까지이기 때문이다. 클라도
    // 읽어야 한다: 밧줄 조기검증·조준 피드백이 이 값을 본다.
    private readonly NetworkVariable<bool> m_playingStandUpSynced = new(false);
    private bool m_playingStandUp;

    private void Awake()
    {
        m_owner = GetComponent<NpcController>();
    }

    /// <summary>
    /// 일어나기가 예약된 구간인가 — <b>쓰러져 기다리는 동안 + 기상 모션</b>을 함께 덮는다. 전 피어에서 유효. (#513)
    ///
    /// 이 구간은 아직 <see cref="NpcState.Captured"/>라 다시 묶을 수 있는 <b>재포획 창</b>이고,
    /// 폴링 호출부(JailIntake)가 후속 동작을 중복 예약하지 않으려 물어보는 값이기도 하다.
    ///
    /// <b>클라도 읽어야 하는 이유</b>: 풀기는 예약을 걸자마자 줄을 빼므로 밧줄 표시만으로는 쓰러져
    /// 기다리는 동안 몸을 눕혀 둘 근거가 없다 — 그 구간의 자세를 이 값이 든다.
    /// </summary>
    public bool IsStandingUp =>
        IsSpawned && !IsServer ? m_standUpPendingSynced.Value : m_standUpPending;

    private void SetStandUpPending(bool value)
    {
        m_standUpPending = value;
        if (IsSpawned && IsServer)
            m_standUpPendingSynced.Value = value;
    }

    /// <summary>
    /// 일어나는 <b>모션이 도는 중</b>인가 — 예약 구간의 뒷부분. 전 피어에서 유효. (#572 후속)
    ///
    /// <see cref="IsStandingUp"/>과 갈라 두는 이유는 <b>재포획 창의 끝</b>이 여기이기 때문이다.
    /// 쓰러져 기다리는 동안은 다시 묶을 수 있어야 하지만(#513이 연 문), 몸이 실제로 일어나기
    /// 시작한 뒤에 묶으면 <b>일어나던 몸이 도로 눕는 그림</b>이 나온다.
    /// (<see cref="NpcStun.IsRising"/>이 기절 기상에 대해 하는 일과 같다)
    /// </summary>
    public bool IsPlayingStandUp =>
        IsSpawned && !IsServer ? m_playingStandUpSynced.Value : m_playingStandUp;

    private void SetPlayingStandUp(bool value)
    {
        m_playingStandUp = value;
        if (IsSpawned && IsServer)
            m_playingStandUpSynced.Value = value;
    }

    /// <summary>
    /// 줄이 풀리는 순간의 일어나기 — 전 피어에 모션을 알리고, 클립 길이만큼 지난 뒤 <paramref name="next"/>를
    /// 실행한다. 서버(또는 오프라인) 전용. (#513)
    ///
    /// 묶여 있지 않으면(이미 서 있는 몸) 기다리지 않고 곧바로 실행한다 — 호출부마다 자세를 따로
    /// 판정하지 않게 여기서 한 번에 가른다. 모션 길이는 기절 기상과 같은 클립을 쓴다.
    /// </summary>
    /// <param name="downSeconds">일어나기까지 쓰러져 있는 총 시간(초). 마지막 구간이 기상 모션이므로
    /// 이 값에서 클립 길이를 뺀 만큼 누워 있다가 일어난다. 이 구간 전체가 재포획 창이다.</param>
    public void ServerStandUpThen(System.Action next, float downSeconds = 0f)
    {
        if (IsSpawned && !IsServer)
            return;

        // 이미 일어나는 중 — 폴링 호출부가 매 틱 불러도 한 번만 건다.
        // <b>아래 "이미 서 있다" 판정보다 반드시 앞이다</b>: 예약을 거는 순간 줄은 이미 빠져 있어서,
        // 재포획 창 안에서 다시 풀기가 들어오면 순서가 뒤집힌 채로는 "이미 서 있다"로 읽혀 next가
        // 그 자리에서 실행된다(누운 몸이 모션 없이 벌떡 일어난다). 이 창 안의 재호출은 정상 조작이다.
        if (m_standUpPending)
        {
            // 예약·타이머는 두고 후속 동작만 최신 것으로 갈아 끼운다 — 부르는 쪽은 콜백이 반드시
            // 돈다고 보고 뒷일을 거기 싣는다(JailIntake의 수감). 방치 만료의 도주 예약 위로 수감이
            // 들어오면 남아야 하는 것은 나중 것이다. null은 덮어쓰지 않는다 — 자세만 세우려는
            // 호출(오검거 해제 등)이 남의 예약을 지우면 안 된다.
            if (next != null)
                m_standUpNext = next;
            return;
        }

        // 기절 오버레이를 먼저 걷는다 — 남겨 두면 아래 Tick이 "일어날 수 없는 몸"으로 보고 예약을
        // 취소하면서 next까지 버린다(수감·석방·도주가 통째로 사라진다). 줄을 푸는 경로는 전부 줄이
        // 걸린 채 여기로 오므로(#513) 기절 창 안의 풀기가 그대로 걸린다.
        // 위 재예약 분기보다 <b>뒤</b>다 — 예약 후에 새로 기절한 것은 취소가 맞다.
        m_owner.Stun.ExitStun(false); // false — 밖에서 푸는 경우라 도주 전이를 걸지 않는다

        NpcRopeDrag rope = m_owner.Rope;
        if (!rope.IsTethered && !rope.IsRoped)
        {
            next?.Invoke();
            return;
        }

        SetStandUpPending(true);
        m_standUpNext = next;
        m_standUpRemaining = m_owner.StunConfig.StandUpSeconds;
        m_standUpDownRemaining = Mathf.Max(0f, downSeconds - m_owner.StunConfig.StandUpSeconds);

        // 누워 있는 구간이 있으면 모션 알림을 그 뒤로 미룬다 — 지금 알리면 3초 내내 일어서 있게 된다
        if (m_standUpDownRemaining <= 0f)
            m_owner.RaiseStandUp();
    }

    /// <summary>일어나기 예약 취소 — 예약된 후속 동작도 함께 버린다.
    /// 표시를 거두는 곳이 여기 하나라 재포획(<see cref="NpcRopeDrag.StartRopeDrag"/>)·넉백·재기절
    /// 이탈·정상 완료가 전부 같은 정리를 탄다 — 하나만 빠지면 그 대상이 영원히 누운 자세로 남는다.</summary>
    internal void CancelStandUp()
    {
        SetStandUpPending(false);
        SetPlayingStandUp(false);
        m_standUpNext = null;
        m_standUpRemaining = 0f;
        m_standUpDownRemaining = 0f;
    }

    /// <summary>일어나기 대기 — 코어 Update가 밧줄 장력 직후, 넉백·스턴 게이트보다 <b>앞</b>에서 돌린다.
    /// 게이트 뒤로 내리면 일어나는 도중 기절한 대상의 예약이 영원히 남는다. (§ 5-1)</summary>
    internal void Tick()
    {
        if (!m_standUpPending)
            return;

        // 밖에서 상황이 바뀌었으면 일어나기가 성립하지 않는다 — 후속 동작도 함께 버린다. 그 상태에서
        // 도주·석방·수감을 걸면 새 상황(넉백 비행·페널티 연행·재기절)을 덮어쓴다. 버려진 대상은 체포
        // 상태에 남아 방치 타이머가 다시 만료시킨다.
        // 수감(Jailed)도 받는다 — 감옥에 놓자마자 그 자리에서 일어나는 구간이 있어서, 여기서 끊으면
        // 기상 클립이 도는 동안 몸만 먼저 서 있다.
        if (
            (m_owner.CurrentState != NpcState.Captured && m_owner.CurrentState != NpcState.Jailed)
            || m_owner.Knockback.IsKnockedBack
            || m_owner.Stun.HasStunOverlay
        )
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

            m_owner.RaiseStandUp();
            SetPlayingStandUp(true); // 여기부터 재포획 창이 닫힌다 (#572 후속)
        }

        m_standUpRemaining -= Time.deltaTime;
        if (m_standUpRemaining > 0f)
            return;

        System.Action next = m_standUpNext;
        CancelStandUp(); // 먼저 비운다 — next가 다시 예약을 걸 수 있다
        next?.Invoke();
    }
}
