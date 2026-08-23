using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// 밧줄 도메인 부품 (#269/#369/#398/#503) — 묶임·끌기·무게를 들고 있다.
/// 끌리는 동안에는 NavMeshAgent를 끄고 위치를 직접 대입한다(NetworkTransform이 결과를 복제).
/// 장력 틱은 코어 Update가 넉백·스턴 게이트보다 <b>먼저</b> 돌린다 — 계획서 § 4-1.
/// </summary>
public class NpcRopeDrag : NetworkBehaviour
{
    private NpcController m_owner;
    private NpcRagdoll m_ragdoll; // 시체 밧줄용 — 리그가 없는 프리팹에서는 null일 수 있다

    // ---- 밧줄 끌기 (#269) ----

    // 끌리는 동안 매 프레임 바닥을 찾을 때의 탐색 반경(m) — 계단 한 칸을 넘길 만큼만.
    private const float k_dragGroundSnapRadius = 1f;

    private bool m_roped;

    // 끌리는 중인지의 클라 사본 — 표현 계층이 이 값으로 누운 모션을 고른다. 커스터디 상태는
    // Escorted(수갑 찬 걷기)라 이게 없으면 원격 피어에서 NPC가 서서 끌려간다. (#369)
    private readonly NetworkVariable<bool> m_ropedSynced = new(false);

    // 장력을 거는 쪽(끄는 플레이어들) — 여러 명이 함께 끌 수 있어(줄다리기) 목록이다.
    // E로 놓은 참가자는 여기서 빠지고 줄만 남는다. 서버(또는 오프라인) 전용.
    private readonly List<Transform> m_dragAnchors = new List<Transform>();

    // 끌기 추종 상태 — 끄는 쪽이 아니라 끌리는 쪽이 갖는다(한 명이 여럿을 끌면 NPC 수만큼 필요하다).
    private Vector3 m_dragVelocity; // SmoothDamp 관성
    private Quaternion m_dragFacing; // 흔들림을 뺀 몸 방향 — 여기에 sway를 얹어 최종 회전을 만든다
    private float m_dragTravel; // 끌린 누적 거리(m) — 흔들림 위상의 기준

    private void Awake()
    {
        m_owner = GetComponent<NpcController>();
        m_ragdoll = GetComponent<NpcRagdoll>();
    }

    /// <summary>밧줄로 묶여 <b>누군가에게</b> 끌리는 중인가 — 참가자별 판정은 <see cref="IsDraggedBy"/>.
    /// 서버·오프라인은 실제 값으로, 원격 피어는 동기화 플래그로 판정. (#269/#369)</summary>
    public bool IsRoped => IsSpawned && !IsServer ? m_ropedSynced.Value : m_roped;

    // ---- 묶임 (#513) ----
    // 끌림(IsRoped)과 묶임(IsTethered)은 다르다. E 놓기는 끌기만 멈추고 줄은 남으며, 실제로 푸는 건
    // E 풀기·인계 완료·방치 탈주·거리 초과 끊김뿐이다(GDD 8-2). 표현이 끌림에만 매달려 있으면
    // 놓는 순간 묶인 몸이 벌떡 일어선다.

    // 이 NPC에 걸린 줄 수 — 한 명이 자기 줄만 풀어도(#390 규칙 8) 남은 줄이 있으면 여전히 묶여 있다.
    private int m_tetherCount;

    // 묶임의 클라 사본 — 묶임 목록 자체는 PlayerEscorter에만 있어 표현 계층이 물을 수 없다.
    private readonly NetworkVariable<bool> m_tetheredSynced = new(false);

    /// <summary>밧줄이 묶여 있는가 — <b>끌리는 중이 아니어도</b> 참이다(E로 놓아둔 대상).
    /// 서버·오프라인은 실제 값으로, 원격 피어는 동기화 플래그로 판정. (#513)</summary>
    public bool IsTethered => IsSpawned && !IsServer ? m_tetheredSynced.Value : m_tetherCount > 0;

    /// <summary>줄 하나가 걸렸다 — <see cref="PlayerEscorter"/>의 연결 목록이 실제로 늘어날 때만 호출한다.
    /// 서버(또는 오프라인) 전용. (#513)</summary>
    internal void AddTether()
    {
        if (IsSpawned && !IsServer)
            return;

        m_tetherCount++;
        SyncTethered();
    }

    /// <summary>줄 하나가 풀렸다 — 연결 목록에서 실제로 빠질 때만 호출한다. 서버(또는 오프라인). (#513)</summary>
    internal void RemoveTether()
    {
        if (IsSpawned && !IsServer)
            return;

        m_tetherCount = Mathf.Max(0, m_tetherCount - 1);
        SyncTethered();
    }

    private void SyncTethered()
    {
        if (IsSpawned && IsServer)
            m_tetheredSynced.Value = m_tetherCount > 0;
    }

    /// <summary>묶임 표시를 통째로 내린다 — 커스터디를 벗어나는 FSM 전이(코어)가 부른다. (#513)
    ///
    /// <see cref="PlayerEscorter"/>의 매 프레임 정리와 평소엔 중복이지만, 끌던 플레이어가 접속을 끊으면
    /// 그 정리가 아예 돌지 않아 배회로 돌아간 몸이 누운 모션으로 걸어 다닌다.</summary>
    internal void ClearTethers()
    {
        if (m_tetherCount == 0)
            return;

        m_tetherCount = 0;
        SyncTethered();
    }

    /// <summary>지금 이 대상을 잡고 있는 밧줄의 길이(m) — 표시(<c>RopeDragView</c>)의 늘어짐 기준이다. (#644)
    ///
    /// 장력을 거는 쪽은 각자 자기 길이를 직접 읽는다(<see cref="Tick"/>은 설정 에셋, 관절은
    /// <see cref="RagdollRope"/>의 필드) — 이 프로퍼티는 그 둘 중 <b>지금 유효한 쪽</b>을 고를 뿐이라,
    /// 밖에서 보는 길이가 실제로 잡는 길이와 어긋나지 않는다.
    ///
    /// <b>끄는 방식이 갈리면 길이도 갈린다</b>(<see cref="UsesRagdollRope"/>) — 래그돌은 관절 밧줄이
    /// 잡고, 설정 에셋의 길이는 <see cref="Tick"/>의 위치 대입 경로에만 쓰인다. 갈라 두지 않으면
    /// 시체·기절한 몸을 끄는 내내 표시가 <b>남의 길이</b>로 늘어짐을 계산한다 — 관절 밧줄이 들어온
    /// #571 이후 실제로 어긋나 있었다. 길이가 0이면 리그를 못 잡은 프레임이라 설정값으로 버틴다.</summary>
    public float RopeLength =>
        UsesRagdollRope && m_ragdoll.RopeLength > 0f
            ? m_ragdoll.RopeLength
            : m_owner.RopeDragConfig.RopeLength;

    // ---- 무게 (#398) ----

    // 동기화하지 않는다 — 읽는 건 서버의 페널티 계산뿐이고 결과 배율만 동기화된다(RopeDragLoad).
    private float m_dragWeight = 1f;

    /// <summary>이 NPC의 무게 — 끄는 플레이어의 속도 페널티 기준. 서버(또는 오프라인)에서만 유효. (#398)</summary>
    public float DragWeight => m_dragWeight;

    // 참가자 수의 클라 사본 — 목줄 반경(끊김거리 ÷ 참가자 수)을 오너가 계산해야 해서 클라도 알아야 한다.
    private readonly NetworkVariable<byte> m_draggerCountSynced = new(0);

    /// <summary>지금 이 NPC에 장력을 걸고 있는 인원 수 — 페널티를 이 수로 나눈다. 전 피어에서 유효.
    /// E로 놓아 장력에서 빠진 참가자는 포함되지 않는다. (#398)</summary>
    public int DraggerCount =>
        IsSpawned && !IsServer ? m_draggerCountSynced.Value : m_dragAnchors.Count;

    /// <summary>무게 배정 — 코어의 InitBehavior에서 서버(또는 오프라인) 1회 호출된다. (#398)</summary>
    internal void InitDragWeight() => m_dragWeight = m_owner.CommonConfig.PickWeight();

    /// <summary>밧줄 끌기 시작 — PlayerEscorter가 서버에서 호출. 위치를 끄는 플레이어가 제어하므로
    /// NavMeshAgent를 끈다. 커스터디 전이(Escorted)는 호출부가 <b>이 호출 앞에</b> 한다 — 뒤에 하면
    /// 직전 상태의 Exit이 꺼진 에이전트를 건드린다. 끈 플레이어는 위협으로 기억한다.</summary>
    public void StartRopeDrag(Transform dragger = null)
    {
        if (IsSpawned && !IsServer)
            return;

        // 첫 참가자일 때만 추종 상태를 새로 잡는다 — 합류할 때 리셋하면 끌려가던 몸이 멈칫한다.
        if (!m_roped)
        {
            m_dragVelocity = Vector3.zero;
            m_dragFacing = transform.rotation;
            m_dragTravel = 0f;
        }

        if (dragger != null)
        {
            m_owner.Reaction.ThreatTarget = dragger;
            if (!m_dragAnchors.Contains(dragger))
                m_dragAnchors.Add(dragger);
        }

        // 일어나던 중이었으면 되돌린다 — 방치 만료로 일어나는 도중에 달려와 E를 누른 재포획이 이 경로다.
        // 예약된 후속 동작(도주 등)도 함께 버려진다. (#513)
        m_owner.StandUp.CancelStandUp();

        // 줄이 걸리는 순간 반출 흐름은 끝난다 — 이제 밧줄 신병이라 E는 놓기/재개로 갈린다 (#517)
        m_owner.Custody.SetJailExtracted(false);

        // 확보 표식 — 줄을 풀어 문 앞에 세워 둬도 남는다. 수감 버튼이 "끌고 온 신병"과
        // "그냥 거기 쓰러져 있던 대상"을 가르는 기준이다 (#637).
        m_owner.Custody.SetSecuredByPlayer(true);

        // 걸어가던 대상을 잡았다 — 반출은 여기서 무산된다 (#548). 풀어 주더라도 인도 지점으로 다시
        // 걷지 않는다. 호출부가 이미 Escorted로 전이해 뒀으므로 NpcController의 상태 훅이 목적지를
        // 지운 뒤지만, 시체 끌기처럼 전이 없이 여기로 오는 경로가 있어 방어선으로 남긴다(멱등).
        m_owner.Custody.ClearRelease();

        SetRoped(true);
        SyncDraggerCount();

        NavMeshAgent agent = m_owner.Agent;
        if (agent != null && agent.enabled)
            agent.enabled = false;

        // 래그돌인 몸은 여기부터 갈린다 — 위치 대입(Tick)이 아니라 관절 밧줄이 끈다
        // (#571 시체 / #572 기절). 기준이 <b>사망이 아니라 래그돌</b>인 이유는 코어 Update의
        // 게이트 주석과 같다: 위치 대입과 물리가 같은 프레임에 루트를 다투면 안 된다.
        //
        // 참가자마다 <b>자기 가닥</b>이 걸린다 (#638) — 관절이 하나뿐이던 시절에는 합류가 곧
        // 앞사람 줄의 탈취였고("합류는 허용, 탈취는 차단"이 이 경로에서만 뒤집혀 있었다, GDD 8-3),
        // 그 사람이 놓으면 관절이 떠난 손에 남아 몸이 계속 따라갔다. 이제 가닥이 여럿이라
        // 서로 다른 방향으로 당기면 그 차이가 힘겨루기가 된다.
        if (UsesRagdollRope)
            ServerAttachCorpseRope(dragger);
    }

    /// <summary>이 밧줄이 <b>관절</b>로 끄는가 — 대상이 래그돌이면 그렇다. (#571/#572)
    /// 거짓이면 <see cref="Tick"/>의 위치 대입이 끈다. 둘은 <b>배타적</b>이다.</summary>
    private bool UsesRagdollRope => m_ragdoll != null && m_ragdoll.IsRagdollActive;

    // 관절 밧줄을 실제로 걸었는가 — 풀 때 불필요한 RPC를 막는다. 서버(또는 오프라인) 전용.
    private bool m_corpseRopeAttached;

    /// <summary>이 플레이어가 지금 이 NPC에 장력을 걸고 있는가 — 서버(또는 오프라인) 전용.
    /// 끄는 쪽(PlayerEscorter)의 "내가 이걸 끌고 있나"가 이 값을 그대로 쓴다 — 따로 두면 어긋난다.</summary>
    public bool IsDraggedBy(Transform dragger) =>
        dragger != null && m_dragAnchors.Contains(dragger);

    /// <summary>지금 끌고 있는 참가자 중 아무나 하나 — 없으면 null. 커스터디 인계 대상 선정용(#643).</summary>
    internal Transform AnyDragger => m_dragAnchors.Count > 0 ? m_dragAnchors[0] : null;

    // 부채꼴 배치용 자리 번호 — 끄는 쪽이 매 프레임 알려준다.
    private int m_dragSlot;
    private int m_dragSlotCount = 1;

    /// <summary>끌리는 자리(부채꼴 배치용) 지정 — 끄는 플레이어가 매 프레임 갱신한다. 서버(또는 오프라인) 전용.</summary>
    public void SetDragSlot(int slot, int slotCount)
    {
        m_dragSlot = slot;
        m_dragSlotCount = Mathf.Max(1, slotCount);
    }

    private void SetRoped(bool value)
    {
        m_roped = value;
        if (IsSpawned && IsServer)
            m_ropedSynced.Value = value;
    }

    /// <summary>밧줄 끌기 해제 — releaser를 장력에서 뺀다. 남은 참가자가 있으면 <b>true</b>(끌기 계속).
    /// 마지막 한 명이 놓았을 때만 에이전트를 되살려 NavMesh로 복귀(Warp)시키고 false를 돌려준다 —
    /// 안 하면 이후 이동·상태 전이가 조용히 실패한다. 커스터디 행선지는 호출부가 정한다.
    /// releaser는 목록에서 뺄 키이자, 놓은 자리가 NavMesh 밖일 때 대체 기준점이다.</summary>
    public bool StopRopeDrag(Transform releaser)
    {
        if (IsSpawned && !IsServer)
            return false;

        if (releaser != null)
            m_dragAnchors.Remove(releaser);
        PruneDeadAnchors();

        // 관절 밧줄이라면 이 사람의 가닥만 뗀다 — 남은 참가자의 가닥은 계속 몸을 끈다 (#638).
        // 마지막 한 명이면 아래에서 어차피 전부 걷으므로 여기서는 자기 것만 신경 쓴다.
        if (m_corpseRopeAttached && releaser != null)
            ServerDetachCorpseRope(releaser);

        // 아직 잡고 있는 사람이 남아 있으면 끌기는 이어진다 — 멈추면 줄다리기가 성립하지 않는다
        if (m_dragAnchors.Count > 0)
            return true;

        SetRoped(false);
        ServerDetachAllCorpseRopes(); // 시체가 아니면 무동작 (#571)

        NavMeshAgent agent = m_owner.Agent;
        if (agent == null)
            return false;

        // 넉백 비행 중이면 에이전트는 넉백이 쥐고 있다 — 여기서 되살리면 날아가던 몸을 NavMesh로 도로
        // 끌어내린다. 착지할 때 EndKnockback이 붙인다. (끌던 중 폭발에 맞은 경우)
        if (m_owner.Knockback.IsKnockedBack)
            return false;

        // 죽었으면 에이전트를 되살리지 않는다 — 시체는 NavMesh로 돌아가지 않는다 (#571).
        // 위 넉백 가드와 같은 이유이고, 이쪽은 <b>영구적</b>이라는 점만 다르다.
        if (m_owner.Death.IsDead)
            return false;

        // ⚠ <b>래그돌이 쥐고 있으면 손대지 않는다</b> — 넉백 가드와 정확히 같은 성격이다(일시적
        // 소유권 양보). 되살리는 것은 래그돌이 일어날 때 자기 자리에서 한다
        // (<c>NpcRagdoll.ServerReattachToNavMesh</c> — "뗀 쪽이 되돌린다").
        //
        // <b>실측으로 잡은 버그다</b> (#572 후속). 여기서 Warp하면 루트가 NavMesh 표면으로 끌려가고
        // (실측: 0.140 → 0.062) 다음 프레임에 <c>TickRootFollow</c>가 골반으로 도로 올려, 놓을 때마다
        // 몸이 6~8cm 잡아당겨졌다 돌아왔다. 바닥의 정의가 둘이라는 것이 그 6cm다 — 정착은
        // 레이캐스트 지면(0.000)에 붙이고 이쪽은 NavMesh 표면(0.062)에 붙인다.
        //
        // 시체가 멀쩡했던 이유도 이것이다: 위 <c>IsDead</c> 가드가 Warp를 아예 막고 있었다.
        // 기절 래그돌이 생기며 "산 채로 래그돌인 몸"이 처음 나타나 그 틈이 드러났다 (계획서 §2-3-3).
        if (m_ragdoll != null && m_ragdoll.IsRagdollActive)
            return false;

        agent.enabled = true;

        // NavMesh에 못 붙으면 이후 isStopped·SetDestination이 조용히 실패해 NPC가 굳는다(빌드 2 이슈 E).
        // 놓은 자리 → 놓는 플레이어 자리(거기까지 걸어왔으니 유효한 바닥) 순으로 시도.
        if (m_owner.TryWarpNear(transform.position))
            return false;
        if (releaser != null && m_owner.TryWarpNear(releaser.position))
            return false;

        // 포기해도 굳지는 않는다 — TickNavMeshRecovery가 1초 뒤 더 넓게 다시 붙인다 (#557).
        // 위치를 남기는 것은 회수가 원인 지점을 조용히 덮지 않게 하기 위해서다.
        Debug.LogWarning(
            "NpcRopeDrag: 밧줄을 놓은 지점을 NavMesh에 붙이지 못했다 — 회수 대기: "
                + $"{name} @{transform.position.ToString("F1")}",
            this
        );
        return false;
    }

    /// <summary>
    /// 장력·묶임을 통째로 끊는다 — <b>에이전트를 되살리지 않고</b>, 참가자를 하나씩 묻지도 않는다.
    /// 사망(<see cref="NpcDeath.ServerEnterDead"/>) 전용. 서버(또는 오프라인). (#571)
    ///
    /// <b><see cref="StopRopeDrag"/>로는 대신할 수 없다.</b> 저쪽은 참가자 <b>한 명</b>을 빼는
    /// 함수라 여럿이 끌던(줄다리기 #390) 대상은 전원을 순회해야 하는데, 죽는 쪽은 그 목록의 주인이
    /// 아니다(목록은 각 <see cref="PlayerEscorter"/>에 있다).
    ///
    /// ⚠ <b>사망 전이보다 반드시 앞이다.</b> <see cref="PlayerEscorter"/>의 매 프레임 정리는
    /// "커스터디(Escorted·Captured)를 벗어났으면 연결을 지운다"인데, 그 경로는 묶임 수만 줄이고
    /// <see cref="StopRopeDrag"/>를 부르지 않는다 — 넉백은 착지 상태가 Captured라 그 목록 안에
    /// 남아서 문제가 안 됐지만, 사망은 목록 밖으로 나가므로 <b>여기서 직접 끊지 않으면 시체가
    /// 끌기 상태로 남아 죽은 뒤에도 장력을 받는다.</b>
    /// </summary>
    internal void ServerClearDrag()
    {
        if (IsSpawned && !IsServer)
            return;

        m_dragAnchors.Clear();
        SetRoped(false);
        SyncDraggerCount();
        ClearTethers();

        // 사망 진입 경로에서는 아직 상태가 Dead가 아니라(ServerEnterDead ④는 전이 ⑤보다 앞이다)
        // 아래가 무동작이고, 그게 맞다 — 그때 걸려 있던 것은 산 대상의 위치 대입 밧줄이라 풀 관절이 없다.
        // 시체를 끌던 줄을 밖에서 끊는 경로(라운드 종료 등)가 생기면 여기가 받아 준다.
        ServerDetachAllCorpseRopes();
    }

    // ---- 시체 밧줄 (#571) ----
    //
    // <b>산 NPC와 끄는 방식이 다르다.</b> 저쪽은 서버가 <see cref="Tick"/>에서 transform.position을
    // 대입하고 NetworkTransform이 복제한다(#369). 시체는 동적 리지드바디라 그 방식이 통하지 않아
    // (부모 트랜스폼을 따르지 않는다) 관절 밧줄(<see cref="RagdollRope"/>)이 물리로 끈다.
    // 그래서 코어 Update의 사망 게이트가 <see cref="Tick"/>을 막는 것이 사양이다 —
    // 둘이 같이 돌면 같은 프레임에 위치를 다툰다. 갈리는 기준은 "대상이 래그돌이냐"다.
    //
    // 루트는 <c>NpcRagdoll.TickRootFollow</c>가 시체에 붙이고 서버 권한 NetworkTransform이 복제한다 —
    // 즉 <b>이 밧줄이 서버의 시체를 끌면 그 궤적이 저절로 전 피어로 나간다.</b>
    //
    // ⚠ 그 자동 추종은 <b>걸어서 갈 수 있는 거리</b>에만 통한다 — 유치장 수감처럼 맵을 가로지르는
    // 순간이동은 각 피어가 자기 시체를 직접 옮겨야 한다 (<c>NpcCustody.SendCorpseToJail</c>).

    /// <summary>
    /// 시체에 밧줄을 묶는다 — 서버(또는 오프라인) 진입점. <b>전 피어에 건다.</b>
    ///
    /// 각 피어가 <b>자기 로컬 시체에</b> 걸어야 한다. 안 걸면 원격 시체에는 끄는 힘이 하나도 없어,
    /// 서버가 보내 주는 루트만 가고 몸은 제자리에 남는다 — 그 뒤를
    /// 원격의 표류 보정(지금은 사라진 <c>TickAlignBonesToRoot</c>, 접지 시 1.5m/s)가 따라잡지 못해
    /// 스냅 거리에 계속 걸린다. 정렬은 동력이 아니라 표류 방지이기 때문이다.
    /// (<see cref="PlayerCarrier"/>의 운반 RPC가 같은 이유로 <c>SendTo.Everyone</c>이다)
    ///
    /// 서버 권한과 어긋나지 않는다: 각 피어의 로컬 물리는 <b>포즈</b>만 만들고 <b>궤적</b>은
    /// 스트리밍된 루트가 준다.
    /// </summary>
    private void ServerAttachCorpseRope(Transform dragger)
    {
        if (dragger == null)
            return;

        m_corpseRopeAttached = true;

        if (!IsSpawned)
        {
            AttachCorpseRope(dragger); // 오프라인 Play 폴백
            return;
        }

        // 스폰된 운반자만 참조로 넘길 수 있다(NetworkObjectReference 제약).
        NetworkObject carrier = dragger.GetComponentInParent<NetworkObject>();
        if (carrier == null || !carrier.IsSpawned)
            return;

        AttachCorpseRopeRpc(new NetworkObjectReference(carrier));
    }

    /// <summary>이 참가자의 시체 밧줄 <b>한 가닥</b>을 푼다 — 서버(또는 오프라인) 진입점. <b>멱등</b>. (#638)
    /// 남은 참가자의 가닥은 그대로 둔다 — 줄다리기에서 한 명이 손을 떼는 경로다.</summary>
    private void ServerDetachCorpseRope(Transform dragger)
    {
        if (dragger == null)
            return;

        if (!IsSpawned)
        {
            DetachCorpseRope(dragger); // 오프라인 Play 폴백
            return;
        }

        NetworkObject carrier = dragger.GetComponentInParent<NetworkObject>();
        if (carrier == null || !carrier.IsSpawned)
            return;

        DetachCorpseRopeRpc(new NetworkObjectReference(carrier));
    }

    /// <summary>걸린 시체 밧줄을 <b>전부</b> 푼다 — 서버(또는 오프라인) 진입점. 시체가 아니면 무동작. <b>멱등</b>.</summary>
    private void ServerDetachAllCorpseRopes()
    {
        // 건 적이 없으면 풀 것도 없다 — 예전의 <c>IsDead</c> 가드를 대신한다. 대상이 끌리는
        // 도중에 래그돌을 벗어날 수 있으므로(기절이 풀리며 일어난다) <b>지금 상태가 아니라
        // 걸었다는 사실</b>을 봐야 한다. (#572)
        if (!m_corpseRopeAttached)
            return;

        m_corpseRopeAttached = false;

        if (!IsSpawned)
        {
            DetachAllCorpseRopes(); // 오프라인 Play 폴백
            return;
        }

        DetachAllCorpseRopesRpc();
    }

    /// <summary>
    /// 끊긴 관절 밧줄을 <b>지금 잡고 있는 사람들에게 다시 건다</b> — 시체 순간이동의 짝. <b>멱등</b>.
    /// 서버(또는 오프라인) 전용. 부르는 곳은 <see cref="NpcCustody.ServerMoveCorpse"/> 하나다.
    ///
    /// <b>이것이 없으면 원장과 관절이 어긋난다.</b> 배치는 옮기기 전에 줄을 전부 끊는데
    /// (<c>NpcRagdoll.ServerPlaceCorpse</c> — 묶인 채 먼 거리를 옮기면 관절 위반이 시체를 발사한다),
    /// 밧줄 원장(<see cref="m_dragAnchors"/>·묶임 수·<c>PlayerEscorter</c>의 목록)은 그대로 남는다.
    /// 원장을 읽는 쪽(줄 표시·무게 페널티·목줄)은 전부 "묶여 있다"로 계속 도는데 <b>몸을 실제로 끄는
    /// 관절만 없어</b>, 반출한 시체가 줄에 묶인 채로 따라오지 않는다 — 놓았다 다시 묶어야 끌렸다.
    ///
    /// 복원 기준을 원장으로 잡는 이유: 가닥마다 쥔 사람이 다르므로(#638) "누가 쥐고 있었나"를 관절 쪽에
    /// 물으면 하나로 답할 수 없다. 원장이 그 답을 이미 들고 있고, 거기서 되걸면 둘의 정합이 보장된다.
    /// </summary>
    internal void ServerReattachCorpseRopes()
    {
        if (IsSpawned && !IsServer)
            return;

        // 끌리는 중이 아니거나 관절로 끄는 대상이 아니면 되걸 것이 없다 — 수감 경로가 여기로 온다
        // (<c>JailIntake.ServerAdmitCorpse</c>가 배치 <b>앞</b>에서 줄을 전부 걷으므로 목록이 비어 있다).
        // ⚠ 그 순서가 곧 안전장치다: 되걸면 감옥에 눕힌 시체를 관절이 문 밖으로 도로 끌어낸다.
        if (!m_roped || !UsesRagdollRope)
            return;

        PruneDeadAnchors();

        // 최초 부착과 <b>같은 함수</b>를 지난다 — 손 앵커 해석·RPC 팬아웃이 같아야 나중에
        // <see cref="StopRopeDrag"/>가 Transform을 키로 자기 가닥을 찾을 수 있다 (#638).
        for (int i = 0; i < m_dragAnchors.Count; i++)
            ServerAttachCorpseRope(m_dragAnchors[i]);
    }

    [Rpc(SendTo.Everyone)]
    private void AttachCorpseRopeRpc(NetworkObjectReference carrierRef)
    {
        // 운반자가 이미 디스폰됐으면 걸지 않는다 — 서버의 끊김 판정(PlayerEscorter)이 곧 정리한다
        if (carrierRef.TryGet(out NetworkObject carrier))
            AttachCorpseRope(carrier.transform);
    }

    [Rpc(SendTo.Everyone)]
    private void DetachCorpseRopeRpc(NetworkObjectReference carrierRef)
    {
        if (carrierRef.TryGet(out NetworkObject carrier))
            DetachCorpseRope(carrier.transform);
    }

    [Rpc(SendTo.Everyone)]
    private void DetachAllCorpseRopesRpc() => DetachAllCorpseRopes();

    // 실제 묶기·풀기 — 전 피어에서 로컬로 돈다. 묶는 지점은 운반자의 <b>손</b>이다(근거는 저쪽 주석).
    // ⚠ 풀 때도 <b>같은 변환</b>을 거쳐야 한다 — 가닥은 이 Transform을 키로 찾는다 (#638).
    private void AttachCorpseRope(Transform carrier) =>
        m_ragdoll?.BeginRopePull(PlayerHeldItemView.ResolveRopeAnchor(carrier));

    private void DetachCorpseRope(Transform carrier) =>
        m_ragdoll?.EndRopePull(PlayerHeldItemView.ResolveRopeAnchor(carrier));

    private void DetachAllCorpseRopes() => m_ragdoll?.EndRopePull();

    // 파괴된 참가자(접속 종료 등)를 걷어낸다 — 남겨두면 장력 계산이 가짜 null을 만진다.
    private void PruneDeadAnchors()
    {
        for (int i = m_dragAnchors.Count - 1; i >= 0; i--)
            if (m_dragAnchors[i] == null)
                m_dragAnchors.RemoveAt(i);

        SyncDraggerCount(); // 놓기·이탈이 전부 이 경로를 지난다
    }

    // 매 프레임 불려도 대역폭을 안 먹는다 — NetworkVariable 세터가 같은 값이면 스스로 조기 반환한다.
    private void SyncDraggerCount()
    {
        if (IsSpawned && IsServer)
            m_draggerCountSynced.Value = (byte)m_dragAnchors.Count;
    }

    /// <summary>
    /// 밧줄 장력으로 끌리는 몸을 끌어당긴다 — 코어 Update가 매 프레임 돌린다. 서버(또는 오프라인). (#269)
    ///
    /// 뒤 고정점에 강체로 붙이지 않는다: 밧줄 길이를 넘을 때만 당기고, 늦게 따라오게 해서 코너를 돌면
    /// 몸이 바깥으로 끌려나오는 궤적이 생긴다.
    ///
    /// <b>호출 위치는 넉백·스턴 게이트보다 앞이다</b> — 묶인 채 기절한 대상도 끌려가야 하므로
    /// 게이트 뒤로 내리면 테이저→밧줄 콤보로 잡은 대상이 그 자리에 멈춘다. (§ 5-1)
    /// </summary>
    internal void Tick()
    {
        if (!m_roped)
            return;

        // 앵커가 사라지면(끌던 플레이어 파괴 등) 멈추기만 한다 — 상태 정리는 PlayerEscorter 몫이다.
        PruneDeadAnchors();
        if (m_dragAnchors.Count == 0)
            return;

        NpcRopeDragConfig config = m_owner.RopeDragConfig;
        Vector3 npcPosition = transform.position;
        float ropeLength = config.RopeLength;

        // 여러 명을 함께 끌 때 자리마다 옆으로 벌린다(혼자면 0이라 궤적이 예전과 같다).
        // 단 경합(줄다리기) 중에는 벌리지 않는다 — 자리 번호를 각자 자기 기준으로 매겨 같은 대상에
        // 다른 번호가 들어오고, 매 프레임 나중에 도는 쪽이 이겨 오프셋이 좌우로 떨린다.
        float lateral =
            m_dragAnchors.Count > 1
                ? 0f
                : (m_dragSlot - (m_dragSlotCount - 1) * 0.5f) * config.DragSpacing;

        // 참가자마다 "자기 밧줄이 허용하는 위치"를 내고 그 평균으로 간다 — 합력. 서로 반대로 당기면
        // 두 목표가 상쇄돼 가운데서 멈춘다(줄다리기). 늘어져 있으면 그 참가자는 당기지 않는다.
        Vector3 targetSum = Vector3.zero;
        Vector3 anchorSum = Vector3.zero;
        for (int i = 0; i < m_dragAnchors.Count; i++)
        {
            Vector3 anchorPoint = m_dragAnchors[i].position;
            anchorSum += anchorPoint;

            Vector3 toNpc = npcPosition - anchorPoint;
            toNpc.y = 0f;
            float distance = toNpc.magnitude;

            Vector3 pull = npcPosition;
            if (distance > ropeLength)
            {
                Vector3 direction = toNpc / distance;

                // 자리 오프셋을 태워도 앵커와의 거리는 밧줄 길이로 유지한다 — 벌린 만큼 늘어나면
                // 뒤로 갈수록 줄이 길어져 끊김 판정에 먼저 걸린다.
                Vector3 right = Vector3.Cross(Vector3.up, direction);
                pull =
                    anchorPoint
                    + (direction * ropeLength + right * lateral).normalized * ropeLength;
            }

            // 높이는 시드만 한다 — 지면 스냅·벽 판정은 ResolveDragPosition이 확정한다 (#369)
            pull.y = anchorPoint.y;
            targetSum += pull;
        }

        Vector3 target = targetSum / m_dragAnchors.Count;
        Vector3 anchor = anchorSum / m_dragAnchors.Count; // 몸 방향 기준점 — 참가자들의 중점

        Vector3 next = Vector3.SmoothDamp(
            npcPosition,
            target,
            ref m_dragVelocity,
            config.DragSmoothTime
        );

        // 몸 방향은 플레이어 회전이 아니라 밧줄 방향 — 제자리에서 마우스만 돌려도 NPC는 안 돈다
        Vector3 ropeDirection = anchor - next;
        ropeDirection.y = 0f;
        if (ropeDirection.sqrMagnitude > 0.0001f)
        {
            Quaternion facing = Quaternion.LookRotation(ropeDirection);
            m_dragFacing = Quaternion.Slerp(
                m_dragFacing,
                facing,
                1f - Mathf.Exp(-config.DragTurnSharpness * Time.deltaTime)
            );
        }

        // 흔들림은 시간이 아니라 끌린 거리 기준이라 멈추면 함께 멈춘다
        m_dragTravel += (next - npcPosition).magnitude;
        float sway = Mathf.Sin(m_dragTravel * config.DragSwayFrequency) * config.DragSwayAngle;

        transform.SetPositionAndRotation(
            ResolveDragPosition(next),
            m_dragFacing * Quaternion.Euler(0f, sway, 0f)
        );
    }

    /// <summary>끌리는 몸의 다음 위치를 지형에 맞춘다 (#369) — 에이전트를 끈 채 위치를 직접 대입하므로
    /// NavMesh가 대신 풀어 주던 벽·바닥을 스스로 처리해야 한다.</summary>
    private Vector3 ResolveDragPosition(Vector3 desired)
    {
        Vector3 delta = desired - transform.position;
        delta.y = 0f;
        float distance = delta.magnitude;

        // 벽 스윕(코어 공용 판정). 넉백처럼 '전부 멈춤'을 쓰면 끌기는 상태가 이어져서 벽을 따라
        // 빠져나가는 방향까지 막혀 영구히 낀다 — 파고드는 성분만 버리고 미끄러뜨린다. (#313/#339)
        if (
            distance > 0.001f
            && m_owner.SweepHitsObstacle(delta / distance, distance, out RaycastHit wall)
        )
        {
            Vector3 normal = wall.normal;
            normal.y = 0f;
            if (normal.sqrMagnitude > 0.0001f)
            {
                normal.Normalize();
                float into = Vector3.Dot(delta, normal);
                if (into < 0f)
                {
                    Vector3 slide = delta - normal * into;
                    desired.x = transform.position.x + slide.x;
                    desired.z = transform.position.z + slide.z;
                }
            }
        }

        // 지면 스냅 — 끄는 플레이어의 발밑 높이를 그대로 쓰면 계단·경사에서 뜨거나 박힌다.
        // 찾지 못하면(NavMesh 밖으로 끌려나간 순간 등) 넘겨받은 높이를 그대로 둔다.
        if (
            NavMesh.SamplePosition(
                desired,
                out NavMeshHit ground,
                k_dragGroundSnapRadius,
                NavMesh.AllAreas
            )
        )
            desired.y = ground.position.y;

        return desired;
    }
}
