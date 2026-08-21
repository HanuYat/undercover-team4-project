using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// NPC 특수 임무 부품 — 오검거 페널티(#277~#279)·납치(#371)·소매치기(#303)가 공유하는
/// 수용 → 추격 → 수렴 → 호송 파이프라인의 데이터·API 허브다. (#503)
///
/// 판정과 이동은 상태 클래스(<see cref="NpcDetainedState"/> · <see cref="NpcChaseState"/> ·
/// <see cref="NpcPenaltyEscortState"/>)가 하고, 이 부품은 그 상태들이 읽을 목표·대상·대형을 들고
/// 결과를 이벤트로 중계한다. FSM 전이가 필요하므로 코어의 <see cref="NpcController.StateMachine"/>을 쓴다.
/// 전이는 전부 서버 권위 — 부르는 쪽(WrongfulArrestPenalty · AbductionEvent · PickpocketEvent)이 서버다.
/// 클라 호출은 <see cref="NpcCustody.StartEscort"/>와 같은 방식으로 무시한다.
///
/// <b>임무별 행동은 여기 없다</b> — 이 부품은 "지금 어떤 임무인가"(<see cref="Duty"/>)만 들고 있고,
/// 그에 따른 행동 차이(추격 속도·표적 갈아타기·밀착 후 처리)는 읽는 쪽이 가른다
/// (<see cref="NpcChaseState"/> · <see cref="NpcStateRules"/> · 이벤트). 임무가 늘어도 이 클래스는 그대로다.
///
/// <b>반드시 <see cref="NpcController"/>와 같은 GameObject에 둔다</b> — 코어 쪽 [RequireComponent]가 이를 보장한다.
/// 반대 방향으로도 걸면 순환 의존이 되어 둘 중 하나만 떼는 것이 막히므로, 선언은 코어에만 둔다.
///
/// <b>개명 이력</b>: <c>NpcPenaltyAgent</c> → 오검거 페널티만 있던 시절의 이름이라 페널티가 아닌 임무
/// (납치·소매치기)가 늘면서 파일명이 내용과 어긋나게 되어 #303에서 바꿨다. 멤버의 <c>Penalty*</c>는
/// 호출부 50여 곳을 건드리지 않으려고 남겨 뒀다 — 이름을 맞추는 것은 별도 정리 몫이다.
/// </summary>
public class NpcDutyAgent : NetworkBehaviour
{
    private NpcController m_owner;

    /// <summary>원한 구역 수용 지점. 수용(Detained) 중이 아니면 null. 서버에서만 유효. (#277)</summary>
    public Transform DetentionSpot { get; private set; }

    /// <summary>원한 구역에서 이 시민이 설 자리의 오프셋 — <see cref="DetentionSpot"/>과 함께 배정된다
    /// (WrongfulArrestPenalty가 도착 순번으로 나눠 준다). 지점이 null이면 의미 없다. 서버에서만 유효.</summary>
    public Vector3 DetentionSlotOffset { get; private set; }

    /// <summary>추격 대상 플레이어. 추격 중이 아니면 null. 서버에서만 유효. (#278)</summary>
    public Transform ChaseTarget { get; private set; }

    // 지금 수행 중인 임무 — 서버가 정하고 전 피어가 읽는다. 마크를 그리는 쪽이 표현 계층(모든 피어)이라
    // 서버 전용 필드로는 부족하다 (#56). 오프라인(네트워크 미사용) Play는 아래 로컬 사본을 본다.
    private readonly NetworkVariable<NpcDutyKind> m_duty = new NetworkVariable<NpcDutyKind>();
    private NpcDutyKind m_dutyLocal;

    /// <summary>지금 수행 중인 임무. 임무가 없으면 <see cref="NpcDutyKind.None"/>. 전 피어에서 유효.</summary>
    public NpcDutyKind Duty => IsSpawned ? m_duty.Value : m_dutyLocal;

    /// <summary>
    /// 이 임무가 <b>납치</b>(#371)인가. 추격 상태(<see cref="NpcChaseState"/>)의 기습 갈래(#775)가
    /// 납치범만 다르게 다루므로 종류를 그대로 묻는 자리가 남아 있다.
    /// </summary>
    public bool IsAbductionDuty => Duty == NpcDutyKind.Abduction;

    /// <summary>
    /// 이 임무가 <b>소매치기</b>(#303)인가 — 물건만 채고 달아나는, 페널티가 아닌 임무다.
    /// 납치와 달리 붙잡아 끌고 가지 않으므로 포획 통보(<see cref="OnPenaltyCaught"/>)가 곧 탈취 시점이다.
    /// </summary>
    public bool IsPickpocketDuty => Duty == NpcDutyKind.Pickpocket;

    /// <summary>
    /// 시민인 척하는 임무인가 — 납치·소매치기가 참. 앵그리 마크를 띄우지 않고 표적도 갈아타지 않는다.
    /// 둘이 같은 답을 내야 하는 자리(마크·재타겟)가 이 이름을 읽는다.
    /// </summary>
    public bool IsUndercoverDuty => Duty is NpcDutyKind.Abduction or NpcDutyKind.Pickpocket;

    /// <summary>임무 종류가 바뀐 순간 발행 — 전 피어. 표현 계층이 앵그리 마크를 다시 판정한다. (#371)</summary>
    public event Action OnPenaltyDutyChanged;

    /// <summary>수렴 대상(포획된 플레이어) — 설정되면 추격 상태가 일반 추격 대신 이 대상에게 모인다. (#279)</summary>
    public Transform PenaltyConvergeTarget { get; private set; }

    /// <summary>격퇴를 건 플레이어 — 추격 상태가 이 대상 반대로 도주하고 재추격 쿨다운을 건다. (#278)</summary>
    public Transform ChaseRepelBy { get; private set; }

    /// <summary>격퇴 도주가 끝나는 시각(Time.time). 이 시각 전에는 추격 대신 도주한다. (#278)</summary>
    public float ChaseRepelUntil { get; private set; }

    /// <summary>호송 선두 NPC — null이면 자신이 선두(광장으로 직접 걷는다). (#279)
    /// 부품이 아니라 코어를 들고 있는 것은 대형 추종이 선두의 transform을 쓰기 때문이다
    /// (<see cref="NpcPenaltyEscortState"/>) — 부르는 쪽도 NPC를 NpcController로 들고 넘긴다.</summary>
    public NpcController PenaltyEscortLeader { get; private set; }

    /// <summary>호송 대형에서 선두 기준 로컬 오프셋 — 선두는 무시. (#279)</summary>
    public Vector3 PenaltyEscortOffset { get; private set; }

    /// <summary>호송 목적지(광장). 호송 중이 아니면 null. (#279)</summary>
    public Transform PenaltyEscortGoal { get; private set; }

    /// <summary>추격 NPC가 대상을 포획한 순간 발행 — WrongfulArrestPenalty가 구독해 수렴·호송을 개시한다. 서버에서만 발생. (#278)
    /// 인자가 부품이 아니라 코어인 것은 구독자(WrongfulArrestPenalty · AbductionEvent · PickpocketEvent)가 NPC를
    /// NpcController 목록으로 들고 대조하기 때문이다 — NpcIntruder와 같은 관례다.</summary>
    public event Action<NpcController, Transform> OnPenaltyCaught;

    private void Awake()
    {
        m_owner = GetComponent<NpcController>();
    }

    public override void OnNetworkSpawn()
    {
        m_duty.OnValueChanged += HandleDutyChanged; // 임무 종류 전파 (#371)
    }

    public override void OnNetworkDespawn()
    {
        m_duty.OnValueChanged -= HandleDutyChanged;
    }

    // 임무 종류 전파 — 앵그리 마크를 그리는 표현 계층이 모든 피어에서 다시 판정한다 (#371)
    private void HandleDutyChanged(NpcDutyKind previous, NpcDutyKind current)
    {
        OnPenaltyDutyChanged?.Invoke();
    }

    /// <summary>포획 통보 — NpcChaseState 전용. (#278)</summary>
    public void NotifyPenaltyCaught(Transform caught) => OnPenaltyCaught?.Invoke(m_owner, caught);

    /// <summary>
    /// 원한 구역 수용 — 오검거당한 시민을 석방 대신 전용 구역으로 보낸다. (#277)
    /// spot이 null이면(구역 미배선 씬) 그 자리에서 수용된 것으로 처리한다 — SendToJail의 null seat과 동일 관례.
    /// slotOffset은 구역을 여러 명이 나눠 쓸 때의 자리 오프셋이다 — 배정은 보내는 쪽(WrongfulArrestPenalty)이 한다.
    /// </summary>
    public void SendToDetention(Transform spot, Vector3 slotOffset)
    {
        if (IsSpawned && !IsServer)
            return;

        m_owner.Custody.ClearEscortTarget(); // 전이는 아래에서 Detained로 — StopEscort(Captured로 간다)는 못 쓴다
        DetentionSpot = spot;
        DetentionSlotOffset = slotOffset;
        m_owner.StateMachine.ChangeState(NpcState.Detained);
    }

    /// <summary>
    /// 추격 출동 — 임계치를 넘긴 플레이어를 초기 타겟으로 쫓기 시작한다. (#278)
    /// duty로 임무를 정한다. 기본값이 오검거인 것은 이 파이프라인이 거기서 나왔기 때문이고,
    /// 납치(#371)·소매치기(#303)는 부르는 쪽이 명시한다 — 표적을 갈아타지 않고 앵그리 마크도 띄우지 않는다.
    /// </summary>
    public void StartPenaltyChase(Transform target, NpcDutyKind duty = NpcDutyKind.WrongfulArrest)
    {
        if (IsSpawned && !IsServer)
            return;

        DetentionSpot = null;
        ChaseTarget = target;
        SetDuty(duty); // 상태 전이보다 먼저 — 마크 판정이 임무 종류를 이미 알고 있어야 한다
        m_owner.StateMachine.ChangeState(NpcState.Chasing);
    }

    // 임무를 세우고 전 피어에 알린다. 오프라인이면 로컬 사본만 바꾸고 직접 발행한다(NetworkVariable이 안 돈다).
    private void SetDuty(NpcDutyKind value)
    {
        if (m_dutyLocal == value && (!IsSpawned || m_duty.Value == value))
            return;

        m_dutyLocal = value;
        if (IsSpawned)
            m_duty.Value = value; // OnValueChanged가 전 피어에서 OnPenaltyDutyChanged로 이어진다
        else
            OnPenaltyDutyChanged?.Invoke();
    }

    /// <summary>
    /// 이 임무 표식만 내린다 — 지금 수행 중인 임무가 <paramref name="kind"/>일 때만 동작한다.
    /// <see cref="EndPenaltyDuty"/>와 달리 참조 정리·상태 전이가 없다: 전이 도중(상태 Exit)에 불릴 수 있어
    /// 여기서 또 전이를 걸면 통지가 중첩된다.
    ///
    /// 소매치기(#303)가 추격을 벗어나는 순간 <see cref="NpcChaseState.Exit"/>가 쓴다 — 표식을 남겨 두면
    /// <see cref="NpcStateRules"/>의 소매치기 예외가 계속 열려, 연행·수감된 뒤에도 때리거나 다시 묶을 수 있다.
    /// </summary>
    public void ClearDuty(NpcDutyKind kind)
    {
        if (IsSpawned && !IsServer)
            return;
        if (Duty != kind)
            return;

        SetDuty(NpcDutyKind.None);
    }

    /// <summary>추격 타겟 교체 — 범위 이탈 재타겟(NpcChaseState)·수렴 지시(매니저)가 호출한다. (#278)</summary>
    public void SetChaseTarget(Transform target)
    {
        if (IsSpawned && !IsServer)
            return;

        ChaseTarget = target;
    }

    /// <summary>
    /// 수렴 개시 — 포획된 플레이어에게 모인다. 추격 중이 아니었어도(막 수용된 NPC 등) 추격 상태로 끌어와 모은다. (#279)
    /// </summary>
    public void StartPenaltyConverge(Transform caught)
    {
        if (IsSpawned && !IsServer)
            return;

        PenaltyConvergeTarget = caught;
        if (m_owner.StateMachine.CurrentState != NpcState.Chasing)
            m_owner.StateMachine.ChangeState(NpcState.Chasing);
    }

    /// <summary>
    /// 격퇴 — 호루라기(#250 후속)의 연결고리. by에게서 잠시 도주하고, 재추격 쿨다운 동안 그 플레이어를 노리지 않는다. (#278)
    /// 수렴 중(포획 확정 후)에는 무시한다 — 유예 창은 잡히기 전까지다.
    /// </summary>
    public void ApplyChaseRepel(Transform by)
    {
        if (IsSpawned && !IsServer)
            return;
        if (PenaltyConvergeTarget != null)
            return;

        ChaseRepelBy = by;
        ChaseRepelUntil = Time.time + m_owner.ChaseConfig.RepelFleeSeconds;
    }

    /// <summary>호송 시작 — goal(광장)으로 이동. leader가 null이면 자신이 선두, 아니면 선두 기준 offset 위치를 따라간다. (#279)</summary>
    public void StartPenaltyEscort(Transform goal, NpcController leader, Vector3 offset)
    {
        if (IsSpawned && !IsServer)
            return;

        PenaltyEscortGoal = goal;
        PenaltyEscortLeader = leader;
        PenaltyEscortOffset = offset;
        m_owner.StateMachine.ChangeState(NpcState.PenaltyEscorting);
    }

    /// <summary>
    /// 임무 종료 — 추격·수렴·호송 참조를 정리하고 배회(Idle)로 복귀한다(시민 복귀). (#279)
    /// 격퇴 잔여값도 지운다 — 다음 임무 발동 때 이전 도주가 이어지지 않게.
    /// </summary>
    public void EndPenaltyDuty()
    {
        if (IsSpawned && !IsServer)
            return;

        DetentionSpot = null;
        ChaseTarget = null;
        SetDuty(NpcDutyKind.None);
        PenaltyConvergeTarget = null;
        ChaseRepelBy = null;
        ChaseRepelUntil = 0f;
        PenaltyEscortLeader = null;
        PenaltyEscortGoal = null;
        m_owner.StateMachine.ChangeState(NpcState.Idle);
    }
}

/// <summary>
/// NPC가 수행 중인 특수 임무 종류 — 같은 추격 파이프라인을 쓰는 세 임무를 가른다. (#303)
/// 임무마다 표식 bool을 하나씩 더하던 방식을 대체한다: 종류가 늘어도 <see cref="NpcDutyAgent"/>에
/// 멤버가 늘지 않고, "표식이 전부 꺼짐"이 임무 없음인지 오검거인지 모호했던 것도 <see cref="None"/>으로 갈린다.
///
/// 동기화 값이라(NetworkVariable) 클라의 조준 피드백·앵그리 마크도 이 값을 읽는다.
/// 파일을 따로 두지 않은 것은 소유 클래스 옆에 붙여 두는 편이 읽기 쉬워서다 — 이 enum만 쓰는 곳이 그 클래스다.
/// </summary>
public enum NpcDutyKind
{
    /// <summary>임무 없음 — 배회 시민이다.</summary>
    None,

    /// <summary>오검거 페널티 (#277~#279) — 원한 구역에서 출동해 쫓고, 잡으면 광장으로 호송한다.</summary>
    WrongfulArrest,

    /// <summary>납치 (#371) — 혼자 있는 플레이어를 표적으로 잡아 끌고 간다. 시민과 구분되지 않아야 한다.</summary>
    Abduction,

    /// <summary>소매치기 (#303) — 시민 걸음으로 다가가 밀착하면 물건만 채고 달아난다.</summary>
    Pickpocket,
}
