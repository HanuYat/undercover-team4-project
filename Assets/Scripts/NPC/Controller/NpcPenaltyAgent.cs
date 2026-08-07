using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 페널티 임무 도메인 부품 — 오검거 페널티(#277~#279)와 납치(#371)가 공유하는
/// 수용 → 추격 → 수렴 → 호송 파이프라인의 데이터·API 허브다. (#503)
///
/// 판정과 이동은 상태 클래스(<see cref="NpcDetainedState"/> · <see cref="NpcChaseState"/> ·
/// <see cref="NpcPenaltyEscortState"/>)가 하고, 이 부품은 그 상태들이 읽을 목표·대상·대형을 들고
/// 결과를 이벤트로 중계한다. FSM 전이가 필요하므로 코어의 <see cref="NpcController.StateMachine"/>을 쓴다.
/// 전이는 전부 서버 권위 — 부르는 쪽(WrongfulArrestPenalty · AbductionEvent)이 서버다. 클라 호출은
/// <see cref="NpcCustody.StartEscort"/>와 같은 방식으로 무시한다.
///
/// <b>반드시 <see cref="NpcController"/>와 같은 GameObject에 둔다</b> — 코어 쪽 [RequireComponent]가 이를 보장한다.
/// 반대 방향으로도 걸면 순환 의존이 되어 둘 중 하나만 떼는 것이 막히므로, 선언은 코어에만 둔다.
/// </summary>
public class NpcPenaltyAgent : NetworkBehaviour
{
    private NpcController m_owner;

    /// <summary>원한 구역 수용 지점. 수용(Detained) 중이 아니면 null. 서버에서만 유효. (#277)</summary>
    public Transform DetentionSpot { get; private set; }

    /// <summary>원한 구역에서 이 시민이 설 자리의 오프셋 — <see cref="DetentionSpot"/>과 함께 배정된다
    /// (WrongfulArrestPenalty가 도착 순번으로 나눠 준다). 지점이 null이면 의미 없다. 서버에서만 유효.</summary>
    public Vector3 DetentionSlotOffset { get; private set; }

    /// <summary>추격 대상 플레이어. 추격 중이 아니면 null. 서버에서만 유효. (#278)</summary>
    public Transform ChaseTarget { get; private set; }

    // 이 페널티 임무가 납치인가 — 서버가 정하고 전 피어가 읽는다. 마크를 그리는 쪽이 표현 계층(모든 피어)이라
    // 서버 전용 필드로는 부족하다 (#56). 오프라인(네트워크 미사용) Play는 아래 로컬 사본을 본다.
    private readonly NetworkVariable<bool> m_abductionDuty = new NetworkVariable<bool>();
    private bool m_abductionDutyLocal;

    /// <summary>
    /// 이 페널티 임무가 <b>납치</b>(#371)인가 — 아니면 오검거(#276~#280)다. 두 곳이 이 값을 본다:
    /// 추격 재타겟(납치는 표적을 갈아타지 않는다)과 앵그리 마크(납치범은 시민과 구분되지 않아야 하므로 띄우지 않는다).
    /// </summary>
    public bool IsAbductionDuty => IsSpawned ? m_abductionDuty.Value : m_abductionDutyLocal;

    // 이 추격이 소매치기인가 (#303) — 납치 표식과 같은 이유로 전 피어가 읽어야 해서 동기화한다.
    private readonly NetworkVariable<bool> m_pickpocketDuty = new NetworkVariable<bool>();
    private bool m_pickpocketDutyLocal;

    /// <summary>
    /// 이 추격이 <b>소매치기</b>(#303)인가 — 물건만 채고 달아나는, 페널티가 아닌 임무다.
    /// 납치와 달리 붙잡아 끌고 가지 않으므로 포획 통보(<see cref="OnPenaltyCaught"/>)가 곧 탈취 시점이다.
    /// </summary>
    public bool IsPickpocketDuty => IsSpawned ? m_pickpocketDuty.Value : m_pickpocketDutyLocal;

    /// <summary>
    /// 시민인 척하는 임무인가 — 납치·소매치기가 참. 앵그리 마크를 띄우지 않고 표적도 갈아타지 않는다.
    /// 둘이 같은 답을 내야 하는 자리(마크·재타겟)가 이 이름을 읽는다.
    /// </summary>
    public bool IsUndercoverDuty => IsAbductionDuty || IsPickpocketDuty;

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
    /// 인자가 부품이 아니라 코어인 것은 구독자(WrongfulArrestPenalty · AbductionEvent)가 NPC를
    /// NpcController 목록으로 들고 대조하기 때문이다 — NpcIntruder와 같은 관례다.</summary>
    public event Action<NpcController, Transform> OnPenaltyCaught;

    private void Awake()
    {
        m_owner = GetComponent<NpcController>();
    }

    public override void OnNetworkSpawn()
    {
        m_abductionDuty.OnValueChanged += HandleAbductionDutyChanged; // 페널티 임무 종류 전파 (#371)
        m_pickpocketDuty.OnValueChanged += HandleAbductionDutyChanged; // 마크 판정이 같아 같은 핸들러 (#303)
    }

    public override void OnNetworkDespawn()
    {
        m_abductionDuty.OnValueChanged -= HandleAbductionDutyChanged;
        m_pickpocketDuty.OnValueChanged -= HandleAbductionDutyChanged;
    }

    // 페널티 임무 종류(오검거/납치) 전파 — 앵그리 마크를 그리는 표현 계층이 모든 피어에서 다시 판정한다 (#371)
    private void HandleAbductionDutyChanged(bool previous, bool current)
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
    /// abductionDuty를 켜면 납치 임무가 된다 — 표적을 갈아타지 않고 앵그리 마크도 띄우지 않는다 (#371).
    /// pickpocketDuty는 소매치기다 (#303) — 시민 속도로 걸어가 밀착하면 포획 대신 물건만 챈다.
    /// </summary>
    public void StartPenaltyChase(
        Transform target,
        bool abductionDuty = false,
        bool pickpocketDuty = false
    )
    {
        if (IsSpawned && !IsServer)
            return;

        DetentionSpot = null;
        ChaseTarget = target;
        // 상태 전이보다 먼저 — 마크 판정이 임무 종류를 이미 알고 있어야 한다
        SetDutyFlag(ref m_abductionDutyLocal, m_abductionDuty, abductionDuty);
        SetDutyFlag(ref m_pickpocketDutyLocal, m_pickpocketDuty, pickpocketDuty);
        m_owner.StateMachine.ChangeState(NpcState.Chasing);
    }

    // 임무 표식을 세우고 전 피어에 알린다. 오프라인이면 로컬 사본만 바꾸고 직접 발행한다(NetworkVariable이 안 돈다).
    private void SetDutyFlag(ref bool local, NetworkVariable<bool> synced, bool value)
    {
        if (local == value && (!IsSpawned || synced.Value == value))
            return;

        local = value;
        if (IsSpawned)
            synced.Value = value; // OnValueChanged가 전 피어에서 OnPenaltyDutyChanged로 이어진다
        else
            OnPenaltyDutyChanged?.Invoke();
    }

    /// <summary>
    /// 소매치기 표식을 내린다 — 추격을 벗어나는 순간 <see cref="NpcChaseState.Exit"/>가 부른다. (#303)
    /// 남겨 두면 <see cref="NpcStateRules"/>의 소매치기 예외가 계속 열려, 연행·수감된 뒤에도 때리거나
    /// 다시 묶을 수 있게 된다 — 그 예외들이 원래 막으려던 신병 빼내기가 그대로 뚫린다.
    /// </summary>
    public void ClearPickpocketDuty()
    {
        if (IsSpawned && !IsServer)
            return;

        SetDutyFlag(ref m_pickpocketDutyLocal, m_pickpocketDuty, false);
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
    /// 페널티 임무 종료 — 추격·수렴·호송 참조를 정리하고 배회(Idle)로 복귀한다(시민 복귀). (#279)
    /// 격퇴 잔여값도 지운다 — 다음 페널티 발동 때 이전 도주가 이어지지 않게.
    /// </summary>
    public void EndPenaltyDuty()
    {
        if (IsSpawned && !IsServer)
            return;

        DetentionSpot = null;
        ChaseTarget = null;
        SetDutyFlag(ref m_abductionDutyLocal, m_abductionDuty, false);
        SetDutyFlag(ref m_pickpocketDutyLocal, m_pickpocketDuty, false);
        PenaltyConvergeTarget = null;
        ChaseRepelBy = null;
        ChaseRepelUntil = 0f;
        PenaltyEscortLeader = null;
        PenaltyEscortGoal = null;
        m_owner.StateMachine.ChangeState(NpcState.Idle);
    }
}
