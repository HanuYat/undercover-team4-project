using System;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// 사망 도메인 부품 (#571) — 체력 0에서 <b>되돌아오지 않는 끝</b>으로 넘긴다.
///
/// 기절(<see cref="NpcStun"/>)과 정확히 반대다. 기절은 링크를 <b>지키려고</b> 상태 enum을 건드리지
/// 않는 오버레이이고(#292), 사망은 그 링크를 전부 <b>끊어야</b> 하므로 <see cref="NpcState.Dead"/>
/// 전이를 쓴다. 그래서 이 부품에는 동기화 값이 없다 — 사망 사실은 코어의 <c>m_networkState</c>가
/// 이미 전 피어에 복제하고, <see cref="IsDead"/>는 그것을 읽을 뿐이다.
///
/// <b>얼린 자세를 뿌리는 통로다</b> (#571 권위 반전). 시체가 정착하는 순간 서버가 그 자세를
/// <b>1회</b> 전 피어에 보내고, 받은 쪽은 자기 로컬 물리의 결과를 버리고 그 자세로 갈아끼운 뒤 얼린다
/// (<see cref="NpcRagdoll.ApplyFrozenPose"/>). 매 틱 뼈를 동기화하는 것이 아니라 <b>얼리는 순간
/// 자세가 상수가 되어 보낼 것이 한 번뿐</b>이라는 점이 핵심이다.
///
/// <b>그래서 <c>NetworkBehaviour</c>가 됐다.</b> 원래는 "들 것이 없다"는 이유로 유일한
/// <c>MonoBehaviour</c> 부품이었는데, 이 자세가 바로 그 들 것이다. 래그돌 본체
/// (<see cref="NpcRagdoll"/>)가 직접 쏘지 않는 것은 그쪽이 표현 계층이라 전 피어에서 로컬로 도는
/// 컴포넌트여야 하기 때문이고, 밧줄이 같은 이유로 <see cref="NpcRopeDrag"/>를 통해 나가는 것과 같다.
///
/// <b>NavMesh로 돌아가지 않는다</b> — 시체는 에이전트를 끈 채 그 자리에 남는다. 기절이 깨어나며
/// 체력을 회복하고 에이전트를 되살리던 경로와 갈리는 지점이 여기다.
/// </summary>
public class NpcDeath : NetworkBehaviour
{
    private NpcController m_owner;
    private NpcRagdoll m_ragdoll; // 자세를 받아 입힐 쪽 — 리그가 없는 프리팹에서는 null일 수 있다

    /// <summary>죽었는가 — 세션 중에는 동기화된 상태 enum이라 클라에서도 읽을 수 있다. (#571)</summary>
    public bool IsDead => m_owner.CurrentState == NpcState.Dead;

    /// <summary>
    /// 죽는 순간 발행 — <b>서버(또는 오프라인) 전용.</b> 인자는 (죽은 NPC, 가해자).
    /// <see cref="NpcStun.OnStunned"/>와 같은 성격의 게임플레이 훅이다: 죽은 대상을 붙들고 있던
    /// 시스템(납치 호송·침입 진행·수배 목록)이 여기서 자기 참조를 정리한다.
    ///
    /// <b>정리가 전부 끝난 뒤에 발행한다</b> — 구독자가 상태를 바꿀 수 있는데, 그 전이가 사망 정리에
    /// 덮이면 안 된다. (<see cref="NpcStun.OnStunned"/>가 마지막에 나가는 것과 같은 이유)
    /// </summary>
    public event Action<NpcController, GameObject> OnDied;

    private void Awake()
    {
        m_owner = GetComponent<NpcController>();
        m_ragdoll = GetComponent<NpcRagdoll>();
    }

    // ---- 얼린 자세 전파 (#571) ----

    /// <summary>
    /// 정착한 시체의 자세를 전 피어에 <b>1회</b> 보낸다 — <see cref="NpcRagdoll"/> 전용 통로.
    /// 서버(또는 오프라인)에서만 부른다. 세션이 아니면 보낼 곳이 없어 무동작이다.
    /// </summary>
    /// <param name="boneRotations">뼈 <b>로컬</b> 회전 — 월드로 보내면 원격의 루트가 다른 자리라 어긋난다.</param>
    /// <param name="hipsLocalPosition">골반의 로컬 위치 — 나머지 뼈 길이는 관절이 유지하므로 이것 하나면 된다.</param>
    internal void ServerSendFrozenPose(Quaternion[] boneRotations, Vector3 hipsLocalPosition)
    {
        if (!IsSpawned || !IsServer)
            return;

        ApplyFrozenPoseRpc(boneRotations, hipsLocalPosition);
    }

    // 서버 자신도 받는다 — 이미 그 자세로 얼어 있으므로 같은 값을 다시 입힐 뿐이고(무해),
    // SendTo.Everyone이라 원격만 거르는 분기를 따로 두지 않는다. (밧줄 RPC와 같은 관례)
    [Rpc(SendTo.Everyone)]
    private void ApplyFrozenPoseRpc(Quaternion[] boneRotations, Vector3 hipsLocalPosition)
    {
        if (m_ragdoll != null)
            m_ragdoll.ApplyFrozenPose(boneRotations, hipsLocalPosition);
    }

    /// <summary>
    /// 사망 처리 — <see cref="NpcHealth"/>가 체력 0 도달 순간에 부른다. 서버(또는 오프라인) 전용. <b>멱등</b>.
    ///
    /// <b>순서가 사양이다.</b> 아래 주석의 근거를 지우고 재배열하면 각각 되살아나는 증상이 있다.
    /// </summary>
    /// <param name="killer">마지막 피해를 준 쪽 — 훅으로 넘긴다. null 허용.</param>
    public void ServerEnterDead(GameObject killer)
    {
        // 서버 판정은 코어에서 빌려 온다 — 이 부품은 NetworkBehaviour가 아니다(클래스 주석 참고)
        if (m_owner.IsSpawned && !m_owner.IsServer)
            return;
        if (IsDead)
            return; // 멱등 — 이미 0인 대상에 추가 피해가 들어와도 한 번만 죽는다

        // ① 넉백 비행을 먼저 끊는다. 착지 처리(EndKnockback)를 태우면 안 된다 — 그쪽은 NavMesh 위
        //    착지점을 찾아 에이전트를 되살리고 상태를 전이시키는데, 셋 다 시체에는 틀렸다.
        //    에이전트는 이미 비행이 꺼 뒀으므로 아래 ⑤가 무동작이 될 뿐이다.
        if (m_owner.Knockback.IsKnockedBack)
            m_owner.Knockback.ServerAbortFlight();

        // ② 기절 오버레이를 걷는다. 남겨 두면 코어 Update의 스턴 게이트가 사망 게이트보다 뒤라
        //    당장은 무해하지만, IsStunned가 참인 채로 남아 밧줄 묶기(CanRopeBind)가 열린다.
        m_owner.Stun.ClearStunOverlay();

        // ③ 기상 예약을 버린다. 남기면 TickStandUp이 예약을 든 채로 도는데, 죽은 몸이 일어나는
        //    모션을 내거나 예약된 후속 동작(도주·수감)이 시체에 걸린다.
        m_owner.StandUp.CancelStandUp();

        // ④ 밧줄을 끊는다 — <b>사망 전이보다 앞이어야 한다.</b> PlayerEscorter의 매 프레임 정리는
        //    커스터디를 벗어난 대상의 연결을 지우지만 StopRopeDrag까지는 부르지 않아, 전이가 먼저
        //    가면 시체가 끌기 상태로 남는다 (NpcRopeDrag.ServerClearDrag 주석).
        m_owner.Rope.ServerClearDrag();

        // ⑤ 상태 전이 — 에이전트를 끄기 <b>전</b>이다. 직전 상태의 Exit()이 살아 있는 에이전트를
        //    정리해야 한다(NpcStunnedState.Exit의 isStopped = false가 대표적). 넉백이 같은 순서를
        //    같은 이유로 지킨다(NpcKnockback.ServerApplyKnockback).
        //    이 전이가 커스터디 표식·반출 표식 정리까지 태운다(NpcController.HandleFsmStateChanged).
        m_owner.StateMachine.ChangeState(NpcState.Dead);

        // ⑥ 에이전트를 끈다 — <b>다시 켜지 않는다.</b> 시체는 NavMesh 위로 돌아가지 않는다.
        //    켜 둔 채로 두면 회수 로직(TickNavMeshRecovery)이 시체를 NavMesh로 끌어다 붙인다.
        NavMeshAgent agent = m_owner.Agent;
        if (agent.enabled)
        {
            if (agent.isOnNavMesh)
                agent.ResetPath();
            agent.enabled = false;
        }

        // ⑦ 사망 판정 — 판별·기록은 검거 판정 정본에 위임한다 (App 파사드 단일 경로, architecture.md R1).
        //    <b>현상금은 여기서 들어오지 않는다</b> (#571): 시체도 유치장까지 끌고 가 수감 버튼을 눌러야
        //    계상된다(ArrestJudge.JudgeCorpse). 여기서 끝나는 것은 오검거 사살 집계 하나다.
        //    "죽으면 어떻게 되는가"를 NPC별로 나눌 자리도 저쪽이다 — 여기서 갈래를 만들면 사망이
        //    정산 규칙을 알게 된다.
        //
        //    ⑧보다 <b>앞</b>이다: OnDied 구독자가 이벤트 뒷정리로 대상을 despawn할 수 있는데
        //    (AbductionEvent.DisposeAbductors), 그 뒤에 판정하면 사라진 NPC를 판정하게 된다.
        App.Game.ArrestJudge?.JudgeDeath(m_owner, killer);

        // ⑧ 통보는 마지막 — 위 주석 참고.
        OnDied?.Invoke(m_owner, killer);
    }
}
