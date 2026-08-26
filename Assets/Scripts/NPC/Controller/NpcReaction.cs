using System;
using Unity.Netcode;
using UnityEngine;
using Random = UnityEngine.Random;

/// <summary>
/// 검거 반응 도메인 부품 — 도주(#213)·저항(#205)·스윙(#220)의 판정 진입점과 위협 참조를 들고 있다. (#503)
///
/// 이동·전투는 상태 클래스(<see cref="NpcFleeState"/> · <see cref="NpcResistState"/>)가 하고, 이 부품은
/// 그 상태들이 읽을 위협 대상·탐색 반경을 들고 스윙 순간을 전 피어에 중계한다. FSM 전이가 필요하므로
/// 코어의 <see cref="NpcController.StateMachine"/>을 쓴다.
/// 전이는 전부 서버 권위 — 클라이언트 호출은 <see cref="NpcCustody.StartEscort"/>와 같은 방식으로 무시한다.
///
/// <b>반드시 <see cref="NpcController"/>와 같은 GameObject에 둔다</b> — 코어 쪽 [RequireComponent]가 이를 보장한다.
/// 반대 방향으로도 걸면 순환 의존이 되어 둘 중 하나만 떼는 것이 막히므로, 선언은 코어에만 둔다.
/// </summary>
public class NpcReaction : NetworkBehaviour
{
    private NpcController m_owner;

    /// <summary>저항·도주 중 피해 다니는 위협 대상(체포를 시도한 플레이어). 배회 등 반응 중이 아니면 null. 서버에서만 유효. (#76)
    /// setter가 internal인 것은 끌기 시작(밧줄, #369)과 기절 진입(<see cref="NpcStun.EnterStunned"/>)이 그 순간의
    /// 가해자를 위협으로 기록하기 때문이다 — 둘 다 같은 어셈블리다. 분리가 끝난 지금도 그 둘이 남아
    /// 있어 닫지 못했다 — 닫으려면 메서드로 감싸야 한다 (계획서 § 9). (#503)</summary>
    public Transform ThreatTarget { get; internal set; }

    /// <summary>
    /// 질주하는 개체인가 — <b>깨어나거나 풀려났을 때 무엇으로 돌아갈지</b>를 가르는 표식. (#106)
    /// 상태 enum으로는 못 가른다: 기절(넉백 KO)·제압은 상태를 갈아엎어 원래 하던 것이 지워진다.
    /// </summary>
    public bool IsSprinter { get; private set; }

    /// <summary>포기하지 않는 개체인가 — 깨어나거나 풀려나면 도주가 아니라 <b>저항으로 돌아간다</b>.
    /// <see cref="IsSprinter"/>와 같은 자리·같은 목적의 표식이다. (#721)</summary>
    public bool IsRelentless { get; private set; }

    /// <summary>
    /// 위협(플레이어)을 찾는 반경(m) — 저항 패배 후 도주 대상 탐색(#205)과 도주 방향 산출(#213)이 같은 값을 쓴다.
    /// 두 경로가 다른 반경을 쓰면 "도망칠 상대"와 "피할 상대"의 기준이 어긋난다.
    /// </summary>
    public float ThreatSearchRadius =>
        m_owner.ResistConfig.AttackRange * m_owner.ResistConfig.ThreatSearchRadiusMultiplier;

    /// <summary>공격 스윙 1회를 휘두를 때 발행 — 전 피어에서 발생한다(서버는 로컬 발행 + ClientRpc 중계).
    /// 인자는 재생할 스윙 변형 index — 서버가 뽑아 전 피어가 같은 클립을 재생하므로, HP 감소 순간(서버가
    /// 그 클립의 타격 오프셋으로 판정)과 화면 속 주먹이 닿는 순간이 일치한다.
    /// 애니메이션 표현(<see cref="NpcAnimationDriver"/>)이 구독해 단발 스윙 모션을 트리거한다.
    /// FSM 상태와 독립한 순간 이벤트라 State 동기화와 별개로 스윙 타이밍을 정확히 맞춘다. (#220)</summary>
    public event Action<int> OnAttackSwing;

    /// <summary>스윙이 실제로 플레이어를 맞힌 순간 발행 — 전 피어에서 발생한다(스윙과 같은 중계 구조).
    /// 스윙과 나눠 두는 이유는 빗나간 스윙에 명중음이 나면 안 되기 때문이다. (#817)</summary>
    public event Action OnAttackHit;

    private void Awake()
    {
        m_owner = GetComponent<NpcController>();
    }

    /// <summary>
    /// 반응 판정 진입점 — 스캔·플레이어 타격이 공유한다. 서버(또는 오프라인) 전용. (#400)
    ///
    /// 도주형·저항형은 트리거와 무관하게 각자의 반응을 하고, 순응형만 갈린다 — 스캔에는 무반응,
    /// 피격에는 도주·저항 중 랜덤이며 뽑은 결과는 <see cref="CitizenIdentity.AssignReaction"/>으로
    /// 1회 확정이다(매번 재추첨하면 연타 도중 유형이 오가 전투가 성립하지 않는다).
    /// 밧줄 묶기는 더 이상 반응을 굴리지 않는다 — 순수 검거 수단이다.
    /// </summary>
    /// <param name="trigger">무엇이 반응을 촉발했는가 — 순응형 처리가 갈린다.</param>
    /// <param name="threat">위협 대상(가해자·스캔한 플레이어). 도주 방향과 저항 대상이 된다. null 허용.</param>
    public void ServerReactTo(ReactionTrigger trigger, Transform threat)
    {
        if (IsSpawned && !IsServer)
            return;

        // <b>이미 저항 중인 몸을 다른 사람이 때렸다 — 그쪽으로 돌아선다</b> (#879). 아래 게이트가
        // "이미 반응 중"으로 걸러 버리기 전에 갈래를 만든다: 그 게이트 때문에 세력 소탕 조직원이
        // 표적 한 명만 물고 옆에서 때리는 사람을 끝까지 무시했다.
        // 기절 중에는 하지 않는다 — 쓰러진 대상은 그대로 잡히는 것이 규칙이다(아래 스턴 게이트와 같은 이유).
        if (trigger == ReactionTrigger.Damage
            && m_owner.CurrentState == NpcState.Attack
            && !m_owner.Stun.IsStunned
            && TryRetargetTo(threat))
            return;

        // 이미 반응 중이거나 확보·페널티 상태면 재판정하지 않는다 — 규칙은 NpcStateRules가 갖는다.
        // 피격만 반출 보행(Releasing)까지 연다 (#548) — 스캔으로는 안 되고 때려야 돌아선다.
        bool allowed =
            trigger == ReactionTrigger.Damage
                ? NpcStateRules.CanReactToDamage(m_owner.CurrentState)
                : NpcStateRules.CanStartReaction(m_owner.CurrentState);
        if (!allowed)
            return;

        // 기절 중엔 반응하지 않는다 — 쓰러진 대상은 그대로 잡힌다 (테이저 콤보)
        if (m_owner.Stun.IsStunned)
            return;

        CitizenIdentity identity = GetComponent<CitizenIdentity>();
        if (identity == null)
            return;

        switch (ResolveReaction(identity, trigger))
        {
            case ReactionType.Flee:
                StartFlee(threat);
                return;

            case ReactionType.Resist:
                StartResist(threat);
                return;
        }
    }

    // 저항 표적을 마지막으로 바꾼 시각 기준의 잠금 만료 — 아래 TryRetargetTo가 쓴다.
    private float m_retargetLockUntil;

    /// <summary>
    /// 저항 중 표적을 때린 사람으로 갈아탄다 — 실제로 갈아탔으면 참. 서버(또는 오프라인) 전용. (#879)
    ///
    /// <b>한 번 휘두를 시간(<see cref="NpcResistConfig.AttackInterval"/>)만큼은 지금 상대에게
    /// 집중한다</b> — 여러 명이 번갈아 때리면 매 대마다 돌아서서 아무도 못 때리고 제자리에서 도는
    /// 그림이 된다. 잠금 중에 들어온 타격은 무시되고 지금 상대를 계속 노린다.
    /// </summary>
    private bool TryRetargetTo(Transform attacker)
    {
        if (attacker == null || attacker == ThreatTarget)
            return false;

        if (Time.time < m_retargetLockUntil)
            return false;

        m_retargetLockUntil = Time.time + m_owner.ResistConfig.AttackInterval;
        ThreatTarget = attacker;
        Debug.Log($"저항 표적 교체(피격) — {m_owner.name} → {attacker.name}");
        return true;
    }

    // 배정된 유형을 그대로 쓰되, 순응형만 트리거에 따라 갈린다. 서버 전용.
    private static ReactionType ResolveReaction(CitizenIdentity identity, ReactionTrigger trigger)
    {
        ReactionType assigned = identity.Reaction;
        if (assigned != ReactionType.Compliant)
            return assigned;

        // 순응형은 스캔에 반응하지 않는다 — "의심받아도 태연한 시민"이 그 유형의 정의다
        if (trigger != ReactionTrigger.Damage)
            return ReactionType.Compliant;

        // 맞으면 도주·저항 중 하나로 돌변하고, 그 유형으로 굳는다 (1회 확정)
        ReactionType rolled = Random.value < 0.5f ? ReactionType.Flee : ReactionType.Resist;
        identity.AssignReaction(rolled);
        return rolled;
    }

    /// <summary>도주 시작 — threat(플레이어) 반대 방향으로 달아난다. 반응 판정은 ServerReactTo가 한다.</summary>
    public void StartFlee(Transform threat)
    {
        if (IsSpawned && !IsServer)
            return;

        IsSprinter = false;
        IsRelentless = false;
        ThreatTarget = threat;
        m_owner.StateMachine.ChangeState(NpcState.Run);
    }

    /// <summary>
    /// 질주 시작 — 위협 없이 도심을 계속 뛰어다닌다. 공연음란범(<see cref="StreakerEvent"/>) 전용. (#106)
    /// 도주와 달리 대상이 없으므로 위협도 비운다 — 남겨 두면 이 상태를 빠져나갈 때 엉뚱한 대상이 딸려간다.
    /// </summary>
    public void StartSprint()
    {
        if (IsSpawned && !IsServer)
            return;

        IsSprinter = true;
        IsRelentless = false;
        ThreatTarget = null;
        m_owner.StateMachine.ChangeState(NpcState.Sprinting);
    }

    /// <summary>
    /// 무력화·제압에서 <b>풀려난 뒤 제 행동으로 돌아간다</b> — 보통은 도주, 질주하는 개체는 질주,
    /// 포기하지 않는 개체(<see cref="IsRelentless"/>)는 저항이다. (#106, #721)
    /// 공연음란범이 한 번 맞고 배회 시민이 되어 버리지 않게 하는 단일 복귀 지점이다.
    ///
    /// ⚠ <b>반출 목적지가 살아 있으면 질주로 가로채지 않는다</b> — 질주(Sprinting)는
    /// <c>NpcController.HandleFsmStateChanged</c>의 ClearRelease 예외 목록에 없어 진입하는 순간
    /// 청탁 인도 목적지가 지워진다. 쓰러뜨리기는 반출의 무산 수단이 아니다(무산은 밧줄·재수감·사망,
    /// #548). 도주로 돌려보내면 예외 목록의 Run에 걸려 목적지가 살아남고, 깨어난 뒤 인도 지점으로
    /// 되돌아가는 기존 경로를 그대로 탄다.
    /// </summary>
    public void ResumeReaction(Transform threat)
    {
        if (IsSprinter && !m_owner.Custody.HasReleaseDestination)
            StartSprint();
        else if (IsRelentless)
            StartResist(threat, relentless: true); // 가드가 없는 이유: Attack은 ClearRelease 예외 목록에 있다
        else
            StartFlee(threat);
    }

    /// <summary>위협 참조 정리 — 반응(도주·저항)이 끝나는 지점에서 호출한다.</summary>
    public void ClearThreat() => ThreatTarget = null;

    /// <summary>저항 시작 — 그 자리에서 버틴다. 반응 판정은 ServerReactTo가 한다.</summary>
    /// <param name="relentless">참이면 <see cref="IsRelentless"/>로 굳어 기절·석방 뒤에도 저항으로 돌아온다 (#721).</param>
    public void StartResist(Transform subduer = null, bool relentless = false)
    {
        if (IsSpawned && !IsServer)
            return;

        // 저항을 유발한(수갑 채우려던) 플레이어를 위협으로 기억한다 — 제압 실패 시 이 대상에게서 도주한다.
        // (도주형이 StartFlee(subduer)로 위협을 받는 것과 대칭 — #205)
        IsSprinter = false;
        IsRelentless = relentless;
        ThreatTarget = subduer;
        m_owner.StateMachine.ChangeState(NpcState.Attack);
    }

    /// <summary>공격 스윙 1회를 전 피어에 알린다 — 애니메이션 표현용. 서버(또는 오프라인) FSM Tick에서만 호출한다.
    /// 서버는 로컬 발행 + ClientRpc로 원격 클라에 중계한다. (#220)</summary>
    public void RaiseAttackSwing(int variant)
    {
        OnAttackSwing?.Invoke(variant); // 서버·오프라인 로컬 발행
        if (IsSpawned && IsServer)
            PlayAttackSwingClientRpc(variant);
    }

    [ClientRpc]
    private void PlayAttackSwingClientRpc(int variant)
    {
        // 서버(호스트)는 위에서 이미 발행했으므로 원격 클라에서만 중계
        if (IsServer)
            return;
        OnAttackSwing?.Invoke(variant);
    }

    /// <summary>공격이 플레이어를 맞힌 것을 전 피어에 알린다 — 타격음용. 서버(또는 오프라인) FSM Tick에서만 호출한다.
    /// 판정 자체가 서버 전용이라(<see cref="NpcResistState"/>) 이 중계가 없으면 호스트에서만 소리가 난다. (#817)</summary>
    public void RaiseAttackHit()
    {
        OnAttackHit?.Invoke(); // 서버·오프라인 로컬 발행
        if (IsSpawned && IsServer)
            PlayAttackHitClientRpc();
    }

    [ClientRpc]
    private void PlayAttackHitClientRpc()
    {
        // 서버(호스트)는 위에서 이미 발행했으므로 원격 클라에서만 중계
        if (IsServer)
            return;
        OnAttackHit?.Invoke();
    }

    // E 제압 경로는 전부 제거됐다 — E는 신병 조작(재연행·줄다리기 복귀) 전용 키가 됐다.
    //  · 도주 NPC 근접 제압(CaptureBySubdue, #436) — 홀드 완주로 Run에서 Captured로 바로 점프했다.
    //  · 제압 타격(RequestSubdueHit/SubdueHitRpc/ServerSubdueHit, #438) — 맨몸 타격으로 체력을 깎았다.
    // 이제 체력을 깎는 플레이어 경로는 진압봉(Baton.ServerSwing)뿐이고, 즉시 무력화는 테이저(#292),
    // 신병 확보는 밧줄(#369)이 맡는다. 피격 반응(ReactionTrigger.Damage)도 진압봉이 직접 부른다.

    // 기절 진입(EnterStunned)은 NpcStun 부품에 있다 — 상태 전이가 아니라 오버레이 플래그가 됐기 때문이다 (#292).
}
