using System;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// 사망 도메인 부품 (#571/#916) — <b>되돌아오지 않는 끝</b>으로 넘긴다.
/// 들어오는 길은 <see cref="NpcHealth"/>의 <c>IsLethal</c>뿐이다 — 체력 0 자체는 사망이 아니다.
///
/// 기절(<see cref="NpcStun"/>)과 정확히 반대다. 기절은 링크를 <b>지키려고</b> 상태 enum을 건드리지
/// 않는 오버레이이고(#292), 사망은 그 링크를 전부 <b>끊어야</b> 하므로 <see cref="NpcState.Dead"/>
/// 전이를 쓴다. 그래서 이 부품에는 동기화 값이 없다 — 사망 사실은 코어의 <c>m_networkState</c>가
/// 이미 전 피어에 복제하고, <see cref="IsDead"/>는 그것을 읽을 뿐이다.
///
/// <b>NavMesh로 돌아가지 않는다</b> — 시체는 에이전트를 끈 채 그 자리에 남는다. 기절이 깨어나며
/// 체력을 회복하고 에이전트를 되살리던 경로와 갈리는 지점이 여기다.
/// </summary>
// ⚠ <b>NetworkBehaviour인 근거가 사라졌다.</b> 얼린 자세를 뿌리는 RPC 하나를 들고 있어서
// 이 부품만 NetworkBehaviour였는데, 그 역할을 <c>RagdollPoseStreamer</c>가 가져갔다.
// 지금은 네트워크 멤버가 하나도 없다 — MonoBehaviour로 되돌릴 수 있지만, 그러면 같은
// NetworkObject 위 다른 NetworkBehaviour들의 인덱스가 밀리므로 래그돌 검증과 섞지 않게
// 따로 넘긴다.
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
    }

    /// <summary>
    /// 사망 처리 — <see cref="NpcHealth"/>가 치명타로 판정한 순간에 부른다. 서버(또는 오프라인) 전용. <b>멱등</b>.
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

        // ⑦ <b>사망은 아무것도 판정하지 않는다.</b> 현상금도 오검거도 유치장 문 앞 수감 버튼에서만
        //    확정된다(ArrestJudge.JudgeCorpse) — 죽은 시민도 산 신병과 같은 문을 지난다.
        //
        //    예전에는 여기서 오검거 사살을 즉시 셌다(#571). 근거는 "죽여서 페널티를 회피하는 것이
        //    최적 전략이 되면 안 된다"였는데, 오검거가 페널티 없이 <b>횟수 집계만</b> 하게 되면서
        //    회피할 대상 자체가 없어졌다. 그리고 그 즉시 집계는 표식(MarkDelivered)을 세워
        //    <b>시체를 끌고 가도 판정이 조용히 끊기게</b> 만들고 있었다 — 버튼을 눌러도 배너가
        //    안 뜨던 원인이다.

        // ⑧ 처치 집계 — ⑦의 "사망은 아무것도 판정하지 않는다"와 상충하지 않는다. 저건 현상금·오검거
        //    같은 경제 판정 얘기고, 이건 때린 사람 화면에 붙는 즉시 피드백(#869)이다. 가해자가
        //    플레이어가 아니면(다른 NPC·차량·환경) GetComponent가 null이라 저절로 무동작.
        //    이름은 스캔 표시 이름(m_nameView)을 쓴다 — 스캐너로 이미 본 이름과 어긋나지 않는다.
        if (killer != null && killer.TryGetComponent(out PlayerKillCredit killCredit))
        {
            CitizenProfile profile = m_owner.GetComponent<CitizenIdentity>()?.Profile;
            string victimName = !string.IsNullOrEmpty(profile?.m_nameView) ? profile.m_nameView : "대상";
            killCredit.ServerCreditKill(victimName, friendlyFire: false);
        }

        // ⑨ 통보 — OnDied 구독자가 이벤트 뒷정리로 대상을 despawn할 수 있으므로(AbductionEvent.DisposeAbductors)
        //    이 뒤에 NPC를 건드리는 일을 두지 말 것.
        OnDied?.Invoke(m_owner, killer);
    }
}
