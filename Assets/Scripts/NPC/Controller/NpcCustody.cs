using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 신병 도메인 부품 — 한 사람을 잡은 뒤 감옥에 넣거나 풀어주기까지의 전 과정을 들고 있다. (#503)
/// 연행(#59) · 인계 판정 표식(#230) · 수감과 배치 지점(#228/#537) · 감옥 퇴장(#415) · 수갑 해제(#228) ·
/// 반출 표식(#517)이 한 부품인 것은 전부 같은 참조(<see cref="EscortTarget"/> · <see cref="JailSpot"/>)를
/// 세우고 내리는 동작이라서다 — 연행을 별 부품으로 두면 <see cref="StartEscort"/>가 여러 도메인의
/// 진입점인 채로 경계 하나를 더 넘게 된다.
///
/// 이동·판정은 상태 클래스(<see cref="NpcEscortedState"/> · <see cref="NpcJailedState"/> ·
/// <see cref="NpcCapturedState"/>)가 하고, 이 부품은 그 상태들이 읽을 참조·표식을 들고 전이를 건다.
/// FSM 전이가 필요하므로 코어의 <see cref="NpcController.StateMachine"/>을 쓴다.
/// 전이는 전부 서버 권위 — 클라이언트 호출은 무시한다.
///
/// <b>반드시 <see cref="NpcController"/>와 같은 GameObject에 둔다</b> — 코어 쪽 [RequireComponent]가 이를 보장한다.
/// 반대 방향으로도 걸면 순환 의존이 되어 둘 중 하나만 떼는 것이 막히므로, 선언은 코어에만 둔다.
/// </summary>
public class NpcCustody : NetworkBehaviour
{
    private NpcController m_owner;
    private NpcRagdoll m_ragdoll; // 시체 수감용 — 리그가 없는 프리팹에서는 null일 수 있다 (NpcRopeDrag와 같은 관례)

    private void Awake()
    {
        m_owner = GetComponent<NpcController>();
        m_ragdoll = GetComponent<NpcRagdoll>();
    }

    // ---- 연행 (#59) ----

    /// <summary>이 부품이 붙은 NPC 코어 — 부품에서 코어로 되돌아갈 때 쓴다.</summary>
    internal NpcController Owner => m_owner;

    /// <summary>연행 중 따라갈 대상(체포한 플레이어). 연행 중이 아니면 null. 서버에서만 유효.</summary>
    public Transform EscortTarget => m_escortTarget;

    private Transform m_escortTarget;

    // 대상의 <b>유무</b>만 동기화한다 — 참조는 실어 보낼 수도 없고, 클라가 알아야 하는 것도
    // "누군가 끌고 있다"까지다 (<see cref="IsSecuredByAnyone"/>가 조준 안내에서 이 값을 읽는다, #664).
    private readonly NetworkVariable<bool> m_hasEscortTargetSynced = new(false);

    /// <summary>지금 누군가에게 연행되고 있는가 — 전 피어에서 유효. (#664)</summary>
    public bool HasEscortTarget =>
        IsSpawned && !IsServer ? m_hasEscortTargetSynced.Value : m_escortTarget != null;

    // 연행 대상을 세우고 내리는 유일한 문 — 동기화 플래그가 참조와 어긋나지 않게 한 곳으로 모은다.
    private void SetEscortTarget(Transform target)
    {
        m_escortTarget = target;
        if (IsSpawned && IsServer)
            m_hasEscortTargetSynced.Value = target != null;
    }

    /// <summary>연행 시작 — 체포 성공 직후 호출. NPC가 target(플레이어)을 따라 이동한다. (#59)</summary>
    public void StartEscort(Transform target)
    {
        // FSM 전이는 서버 권위 — 클라이언트에서 직접 부르면 무시한다
        if (IsSpawned && !IsServer)
            return;

        SetEscortTarget(target);
        m_owner.StateMachine.ChangeState(NpcState.Escorted);
    }

    /// <summary>연행 중단 — 그 자리에서 체포(Captured) 상태로 멈춘다. (#59)</summary>
    public void StopEscort()
    {
        if (IsSpawned && !IsServer)
            return;

        ClearEscortTarget();
        m_owner.StateMachine.ChangeState(NpcState.Captured);
    }

    /// <summary>연행 참조만 끊는다 — 전이는 부르는 쪽이 한다. 다른 부품이 쓰는 경로다(같은 어셈블리라 internal).
    /// <see cref="NpcDutyAgent.SendToDetention"/>이 수용 직전에 쓴다 — 갈 곳이 Detained라
    /// <see cref="StopEscort"/>(Captured로 간다)를 그대로 쓸 수 없다.
    /// 세터를 열지 않고 메서드로 두는 이유: 밖에서 연행 대상을 <b>지정</b>하는 문은
    /// <see cref="StartEscort"/> 하나여야 한다.</summary>
    internal void ClearEscortTarget()
    {
        SetEscortTarget(null);
    }

    /// <summary>참조만 <paramref name="from"/>에서 <paramref name="to"/>로 넘긴다(전이 없음) — 장부가
    /// <paramref name="from"/>일 때만 옮기고 옮겼으면 true. 줄다리기에서 손을 뗀 사람의 참조를 남은
    /// 참가자에게 돌려주는 문이다(#643). <see cref="StartEscort"/>를 다시 쓰지 않는 이유는 새로
    /// 세우는 문은 하나여야 해서다.</summary>
    internal bool HandOverEscortTarget(Transform from, Transform to)
    {
        if (IsSpawned && !IsServer)
            return false;
        if (to == null || m_escortTarget != from)
            return false;

        SetEscortTarget(to);
        return true;
    }

    // ---- 인계 판정 표식 (#230) ----

    /// <summary>
    /// 검거 판정이 끝났는가 — <see cref="MarkDelivered"/>로 ArrestJudge가 세팅한다. (#230)
    /// 판정 완료분은 인계 방치 타이머에서 빠진다(유치장에서 탈출하면 안 된다). 재판정 자체는 막지 않으며
    /// (#358 — 유치장 밖으로 데려갔다 다시 들여놓으면 다시 판정된다, #492), 재판정 후처리 중복은
    /// <see cref="ArrestResult.IsFirstDelivery"/>가 건다.
    /// 서버(또는 오프라인)에서만 유효 — 판정·인계 검증이 모두 서버 전용이라 동기화하지 않는다.
    /// 판정된 대상을 감옥에 넣고 계상하는 것은 JailIntake(#492)가 가져간다.
    /// </summary>
    public bool IsDelivered { get; private set; }

    /// <summary>인계 판정 완료로 표시 — ArrestJudge 전용. 서버(또는 오프라인)에서만 호출된다. (#230)</summary>
    public void MarkDelivered()
    {
        if (IsSpawned && !IsServer)
            return;

        IsDelivered = true;
    }

    /// <summary>
    /// 인계 판정 완료 표시를 되돌린다 — 범인 탈출 이벤트(#231) 전용. 서버(또는 오프라인)에서만 호출된다.
    ///
    /// <b>재검거의 핵심이다.</b> 이 플래그가 켜져 있으면 <see cref="ArrestResult.IsFirstDelivery"/>가 false가 되어,
    /// 탈출한 범인을 다시 잡아 인계해도 <see cref="RoundManager"/> 할당량이 다시 누적되지 않는다(#358). 되돌려야
    /// 재검거가 '첫 인계'로 잡혀 정상 카운트된다. (오검거 카운트는 IsFirstDelivery에 의존하지 않는다 — WrongfulArrestPenalty 참조)
    /// </summary>
    public void ClearDelivered()
    {
        if (IsSpawned && !IsServer)
            return;

        IsDelivered = false;
    }

    // ---- 감옥 (#228/#537) ----

    /// <summary>수감 중 서 있을 감옥 안 배치 지점. 수감 중이 아니면 null. 서버에서만 유효. (#228/#537)
    /// 지점의 Z축(forward)이 서서 바라보는 방향이다 — 순간이동한 뒤 그 방향으로 돌려 세운다.</summary>
    public Transform JailSpot { get; private set; }

    /// <summary>
    /// 수감 — 판정에서 진범·경범죄로 확정된 NPC를 감옥 안 배치 지점으로 보낸다. (<see cref="JailIntake"/> 경유, GDD 7-2)
    /// 문 앞에서 E를 누르는 순간 불린다 (#537 — 끌고 들어가 좌석에 놓던 경로는 폐기).
    /// spot으로 <b>순간이동</b>해 그 자리에 선다. spot이 null이면 그 자리에서 수용된 것으로 처리한다.
    /// 배치 지점 배정은 보내는 쪽(JailZone.ReservePlacement)이 한다 — 자리 계산이 아니라 손으로 배치한 목록이다.
    /// </summary>
    public void SendToJail(Transform spot)
    {
        // FSM 전이는 서버 권위 — StartEscort와 동일하게 클라이언트 호출은 무시한다
        if (IsSpawned && !IsServer)
            return;

        SetEscortTarget(null); // 판정 시점에 연행은 이미 풀렸지만, 참조가 남아 있으면 여기서 끊는다

        // <b>기절을 푼다.</b> 검거는 무력화가 전제라(NpcStateRules.CanRopeBind) 수감되는 대상은 거의
        // 항상 기절 오버레이를 달고 들어오고, 밧줄에 묶인 동안은 그 타이머마저 멈춰 있다(#269).
        // 오버레이가 남으면 Update의 스턴 게이트가 FSM Tick을 통째로 건너뛰어
        // (NpcController.Update) <b>감옥 안에서 꼼짝도 하지 않는다</b> — 배회가 돌지 않는 원인이었다.
        // resumeReaction=false — 밖에서 강제로 푸는 경우라 도주 전이를 걸지 않는다.
        m_owner.Stun.ExitStun(false);

        // 체력도 만으로 되돌린다 — 검거는 무력화가 전제라 수감되는 대상은 거의 항상 임계 아래로 깎여
        // 들어온다. 그대로 두면 탈옥으로 풀려난 수감자가 진압봉 한 대에 죽는다(#571의 "다음에 맞으면
        // 죽는다"는 전투 중인 몸을 겨눈 규칙이고, 판정까지 끝나 수감된 몸은 그 자리가 아니다).
        m_owner.Health.ServerRestoreFull();

        // <b>셀 바닥 통행을 연다</b> (#744). 셀 바닥은 Jail 영역이고 시민 프리팹의 마스크에서 빠져 있어
        // (#415의 방식이 #722에서 되살아났다), 열어 주지 않으면 방 안 배회가 목적지를 하나도 못 뽑아
        // 수감자가 마네킹이 된다 (NpcJailedState.BeginWander).
        //
        // 전이보다 <b>먼저</b> 건다 — Jailed 진입이 곧 배치 지점으로의 워프이고, 그 뒤 첫 배회가
        // 바로 이 마스크로 목적지를 고른다.
        m_owner.SetGrantedAreas(NpcNavAreas.JailMask);

        JailSpot = spot;
        m_owner.StateMachine.ChangeState(NpcState.Jailed);
    }

    /// <summary>
    /// 시체 수감 — 판정된 시체를 감옥 안 자리로 옮긴다. 서버(또는 오프라인) 전용. (#571)
    ///
    /// <b><see cref="SendToJail"/>과 갈리는 점은 상태다.</b> 저쪽은 <see cref="NpcState.Jailed"/>로
    /// 전이시켜 수감자가 방 안을 배회하게 하지만, 시체는 <see cref="NpcState.Dead"/>에서 나갈 수 없고
    /// (<c>NpcStateMachine</c>이 사망 이탈을 거부한다) 나갈 이유도 없다 — 옮기기만 하면 된다.
    /// 그래서 여기서 하는 일은 <b>순간이동 하나</b>다: 커스터디도, 기절 해제도, 배치 상태도 없다.
    ///
    /// <b>원격 배선은 배치 쪽이 쥔다</b> (#571 권위 반전). <see cref="NpcRagdoll.ServerPlaceCorpse"/>가
    /// 루트와 뼈를 같은 델타로 옮기고 자세를 보간 없이 한 번 더 쏜다 — 시체의 뼈는 동적으로 남으므로
    /// 루트를 따라오지 않는다. 자세가 가는 통로는 <see cref="RagdollPoseStreamer"/> 하나다.
    /// </summary>
    /// <param name="position">시체가 놓일 감옥 안 지점 — 유치장이 정한다
    /// (<see cref="JailZone.RandomRestPointInRoom"/>). <b>바닥에 정확히 스냅될 필요는 없다</b>:
    /// 시체는 물리에 남아 있어 도착지에서 알아서 무너져 눕는다.</param>
    public void SendCorpseToJail(Vector3 position) => ServerMoveCorpse(position);

    /// <summary>시체를 통째로 옮긴다 — 서버(또는 오프라인) 전용. 수감·퇴장이 함께 쓴다. (#571/#597)
    /// 산 대상의 <see cref="ServerExitJail"/>과 갈린다: 시체는 에이전트가 없어 워프가 아니고,
    /// 옮길 것도 루트가 아니라 뼈 전부다.</summary>
    public void ServerMoveCorpse(Vector3 position)
    {
        if (IsSpawned && !IsServer)
            return;

        if (m_ragdoll != null)
        {
            m_ragdoll.ServerPlaceCorpse(position);

            // 자른 쪽이 되돌린다 — ServerPlaceCorpse는 옮기기 전에 관절 밧줄을 전부 끊는다(발사 방지).
            // 밧줄 원장은 그 뒤에도 남으므로, 옮긴 자리에서 원장이 말하는 사람들에게 다시 건다.
            // 무동작인 경우(수감 — 배치 전에 원장도 이미 걷혔다)는 NpcRopeDrag 쪽 주석 참고.
            m_owner.Rope.ServerReattachCorpseRopes();
        }
        else
        {
            transform.position = position; // 리그가 없는 프리팹 — 옮길 뼈가 없다
        }
    }

    /// <summary>
    /// 수갑 해제 — 오검거로 판정된 무고한 시민을 풀어준다. 배회(Idle)로 복귀한다. (GDD 7-2/7-3, #228)
    /// 체포(Captured) 상태에서만 유효 — 판정 직후 ArrestJudge가 연행을 풀어 Captured로 만들어 둔 상태를 이어받는다.
    /// 오검거 카운트·페널티는 여기서 다루지 않는다 (ArrestJudge.OnArrestJudged를 구독하는 #101 담당).
    /// </summary>
    public void ReleaseFromCustody()
    {
        if (IsSpawned && !IsServer)
            return;
        if (m_owner.StateMachine.CurrentState != NpcState.Captured)
            return;

        JailSpot = null;
        m_owner.StateMachine.ChangeState(NpcState.Idle);
    }

    /// <summary>
    /// 셀 밖으로 내보낸다 — 퇴장 동행·탈옥 방출이 부른다. 서버(또는 오프라인) 전용. (#415/#537/#744/#838)
    ///
    /// 셀 바닥은 본관과 이어진 NavMesh 경로가 없는 섬이라(#722), 나가는 것은 곧 순간이동이다.
    /// 워프가 실패하면(퇴장 지점이 NavMesh 밖) 경고만 남기고 제자리에 둔다 — 셀 안에 남는 편이
    /// NavMesh 밖에 떨어져 굳는 것보다 낫다.
    ///
    /// <b>셀 통행을 내려놓는 것이 워프보다 앞이다</b> (#744) — 순서 자체는 그때와 같지만 받는 것이
    /// 없어졌다. 퇴장 지점인 본관 실내는 이제 시민 프리팹 마스크에 들어 있어(#838 — <c>HQ</c> 영역이
    /// 통행을 가르지 않는다) 따로 빌릴 것이 없고, 여기서는 셀만 반납한다.
    /// 반납이 실제로 언제 적용되는지는 <c>NpcController.ApplyGrantedAreas</c>가 정한다 — 아직 셀
    /// 바닥을 딛고 있으면(워프 실패로 셀에 남는 경우까지) 발밑을 비울 때까지 미룬다.
    /// </summary>
    public void ServerExitJail(Vector3 exitPosition)
    {
        if (IsSpawned && !IsServer)
            return;

        if (m_owner.Agent == null)
            return;

        m_owner.SetGrantedAreas(0);

        // 워프 유틸은 코어에 있다 — 밧줄 놓기(#369)와 공유하는 공용 헬퍼라서다 (계획서 § 3-6)
        if (!m_owner.TryWarpNear(exitPosition))
        {
            Debug.LogWarning(
                $"NpcCustody: 셀 퇴장 지점으로 워프 실패 — 제자리에 둔다: {name}",
                this
            );
        }
    }

    // ---- 반출 표식 (#517) ----

    /// <summary>
    /// <b>플레이어가 확보했던 대상인가</b> — 수감 버튼이 "끌고 온 신병"과 "그냥 거기 쓰러져 있던
    /// 대상"을 가르는 기준이다. 서버(또는 오프라인) 전용. (#637)
    ///
    /// <see cref="EscortTarget"/>으로는 못 가린다 — 밧줄을 완전히 풀어
    /// 문 앞에 세워 둔 신병은 둘 다 비어 있고 상태도 그냥 <c>Captured</c>라, 길에 쓰러진 대상과
    /// 구분되지 않는다. 그 조작(풀어 두고 누르기)을 살리려면 표식이 하나 더 필요했다.
    ///
    /// <b>누가 확보했는지는 담지 않는다</b> — 남이 끌고 온 신병을 대신 넣어 주는 협동이 설계에 있고
    /// (팀 확정 2026-08-06), 인계 몫도 밧줄 보유자 전원에게 가므로 가로채기가 성립하지 않는다.
    ///
    /// 전 피어에서 유효 — 조준 안내가 서버와 같은 답을 내야 한다 (<see cref="IsSecuredByAnyone"/>, #664).
    /// </summary>
    public bool WasSecuredByPlayer =>
        IsSpawned && !IsServer ? m_securedByPlayerSynced.Value : m_securedByPlayer;

    private bool m_securedByPlayer;

    private readonly NetworkVariable<bool> m_securedByPlayerSynced = new(false);

    /// <summary>
    /// <b>누군가 확보한 대상인가</b> — 수감 버튼이 "끌고 온 신병"과 "그냥 거기 쓰러져 있던 대상"을
    /// 가르는 문지기다. 셋 중 하나라도 있으면 누군가 손을 댄 것이다. 전 피어에서 유효. (#637/#664)
    ///
    /// 판정 자체는 서버가 하지만(<see cref="JailIntake"/>), 조준 안내도 같은 답을 내야 "회색인데
    /// 눌리긴 한다"가 안 생긴다 — 그래서 보는 값 셋이 전부 클라에서 읽히게 맞춰져 있다.
    ///
    /// <b>누가 확보했는지는 묻지 않는다</b> — 남이 끌고 온 신병을 대신 넣어 주는 협동이 설계에 있다
    /// (팀 확정 2026-08-06). 걸러내려는 것은 <b>아무도 손대지 않은</b> 대상이다.
    /// </summary>
    public bool IsSecuredByAnyone => HasEscortTarget || WasSecuredByPlayer;

    /// <summary>확보 표식 지정 — 켜는 곳은 밧줄 묶임(<see cref="NpcRopeDrag"/>) 하나다. (#637)
    /// 끄는 곳은 커스터디 이탈(<see cref="NpcController"/>의 상태 훅) — 도주·배회로 돌아가면 남의 몸이다.
    /// 시체는 사망 진입에서 한 번 꺼지고, 그 몸을 묶으면 다시 켜져 남는다 — "끌고 와서 내려놓은 시체"와
    /// "길에 사살해 둔 시체"를 가르는 것이 그 차이다.</summary>
    public void SetSecuredByPlayer(bool value)
    {
        if (IsSpawned && !IsServer)
            return;

        m_securedByPlayer = value;
        if (IsSpawned && IsServer)
            m_securedByPlayerSynced.Value = value;
    }

    // 수갑 소모·반환(#229)은 밧줄이 소모형이 아니게 되면서 통째로 제거됐다 — 밧줄 검거엔 회수할 자원이 없다. (#369)
}
