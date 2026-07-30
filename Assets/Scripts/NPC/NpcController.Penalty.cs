using System;
using UnityEngine;

public partial class NpcController
{
    // ---- 오검거 페널티: 수용·추격·호송 (#277/#278/#279) ----
    // FSM 전이는 전부 서버 권위 — WrongfulArrestPenalty(서버)만 호출한다. 클라 호출은 StartEscort와 같은 방식으로 무시.

    /// <summary>원한 구역 수용 지점. 수용(Detained) 중이 아니면 null. 서버에서만 유효. (#277)</summary>
    public Transform DetentionSpot { get; private set; }

    /// <summary>원한 구역에서 이 시민이 설 자리의 오프셋 — <see cref="DetentionSpot"/>과 함께 배정된다
    /// (WrongfulArrestPenalty가 도착 순번으로 나눠 준다). 지점이 null이면 의미 없다. 서버에서만 유효.</summary>
    public Vector3 DetentionSlotOffset { get; private set; }

    /// <summary>추격 대상 플레이어. 추격 중이 아니면 null. 서버에서만 유효. (#278)</summary>
    public Transform ChaseTarget { get; private set; }

    /// <summary>수렴 대상(포획된 플레이어) — 설정되면 추격 상태가 일반 추격 대신 이 대상에게 모인다. (#279)</summary>
    public Transform PenaltyConvergeTarget { get; private set; }

    /// <summary>격퇴를 건 플레이어 — 추격 상태가 이 대상 반대로 도주하고 재추격 쿨다운을 건다. (#278)</summary>
    public Transform ChaseRepelBy { get; private set; }

    /// <summary>격퇴 도주가 끝나는 시각(Time.time). 이 시각 전에는 추격 대신 도주한다. (#278)</summary>
    public float ChaseRepelUntil { get; private set; }

    /// <summary>호송 선두 NPC — null이면 자신이 선두(광장으로 직접 걷는다). (#279)</summary>
    public NpcController PenaltyEscortLeader { get; private set; }

    /// <summary>호송 대형에서 선두 기준 로컬 오프셋 — 선두는 무시. (#279)</summary>
    public Vector3 PenaltyEscortOffset { get; private set; }

    /// <summary>호송 목적지(광장). 호송 중이 아니면 null. (#279)</summary>
    public Transform PenaltyEscortGoal { get; private set; }

    /// <summary>추격 NPC가 대상을 포획한 순간 발행 — WrongfulArrestPenalty가 구독해 수렴·호송을 개시한다. 서버에서만 발생. (#278)</summary>
    public event Action<NpcController, Transform> OnPenaltyCaught;

    /// <summary>포획 통보 — NpcChaseState 전용. (#278)</summary>
    public void NotifyPenaltyCaught(Transform caught) => OnPenaltyCaught?.Invoke(this, caught);

    /// <summary>
    /// 원한 구역 수용 — 오검거당한 시민을 석방 대신 전용 구역으로 보낸다. (#277)
    /// spot이 null이면(구역 미배선 씬) 그 자리에서 수용된 것으로 처리한다 — SendToJail의 null cell과 동일 관례.
    /// slotOffset은 구역을 여러 명이 나눠 쓸 때의 자리 오프셋이다 — 배정은 보내는 쪽(WrongfulArrestPenalty)이 한다.
    /// </summary>
    public void SendToDetention(Transform spot, Vector3 slotOffset)
    {
        if (IsSpawned && !IsServer)
            return;

        EscortTarget = null;
        DetentionSpot = spot;
        DetentionSlotOffset = slotOffset;
        m_stateMachine.ChangeState(NpcState.Detained);
    }

    /// <summary>추격 출동 — 임계치를 넘긴 플레이어를 초기 타겟으로 쫓기 시작한다. (#278)</summary>
    public void StartPenaltyChase(Transform target)
    {
        if (IsSpawned && !IsServer)
            return;

        DetentionSpot = null;
        ChaseTarget = target;
        m_stateMachine.ChangeState(NpcState.Chasing);
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
        if (m_stateMachine.CurrentState != NpcState.Chasing)
            m_stateMachine.ChangeState(NpcState.Chasing);
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
        ChaseRepelUntil = Time.time + m_chaseConfig.RepelFleeSeconds;
    }

    /// <summary>호송 시작 — goal(광장)으로 이동. leader가 null이면 자신이 선두, 아니면 선두 기준 offset 위치를 따라간다. (#279)</summary>
    public void StartPenaltyEscort(Transform goal, NpcController leader, Vector3 offset)
    {
        if (IsSpawned && !IsServer)
            return;

        PenaltyEscortGoal = goal;
        PenaltyEscortLeader = leader;
        PenaltyEscortOffset = offset;
        m_stateMachine.ChangeState(NpcState.PenaltyEscorting);
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
        PenaltyConvergeTarget = null;
        ChaseRepelBy = null;
        ChaseRepelUntil = 0f;
        PenaltyEscortLeader = null;
        PenaltyEscortGoal = null;
        m_stateMachine.ChangeState(NpcState.Idle);
    }
}
