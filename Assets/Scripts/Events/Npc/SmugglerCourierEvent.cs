using UnityEngine;

/// <summary>
/// 밀수 운반책 — 마약·무기 화물을 지고 <b>맨홀을 향해 도시를 가로지른다</b>. (GDD 6-4, #991)
///
/// 화물이 무거워 <b>혼자서는 밧줄로 끌 수 없다</b> — 현장 둘 이상이 같은 대상에 덧걸어야(줄다리기 합류,
/// #390) 이송이 성립한다. 협동 강제 수단은 <b>무게 하나</b>다: 세력 소탕(#721)이 동시 조작을 뺀 것과
/// 같은 이유로, 현장 2인 판에서 잠기는 장치는 두지 않는다.
///
/// <b>무거운 이유와 잡을 이유가 같은 사실이다</b> — 짊어진 것이 밀수품이라, 현장은 실루엣만 보고
/// "저건 잡아야 한다"를 안다. 스캔·판독을 거치지 않는 것이 이 이벤트의 성격이다.
///
/// <b>맞으면 달린다.</b> 위협에 반응하지 않는 대신(도주·저항으로 새면 목적지를 잃는다) 속도만 올려
/// 맨홀로 서두른다 — 때린 쪽이 손해를 보지 않게 하는 장치다(<see cref="SmugglerCargo.ServerPanic"/>).
///
/// 결말은 셋이다:
///  · <b>맨홀 도착</b> → 뚜껑을 열고 그 아래로 내려가 사라진다. 도심에 남기지 <b>않는</b> 유일한
///    스폰형이다 — 남기면 나중에 주워 담는 공짜 보상이 되어 "맨홀까지의 거리 = 제한시간"이라는
///    이 이벤트의 알맹이가 사라진다. 지하로 내려간 납치범(#371/#775)과 같은 예외다.
///  · <b>제압 → 인계</b> → 경범죄 판정. 수익은 공통 골격이 지급한다.
///  · <b>제압 실패로 뿌리침 · 경로 불발</b> → 도심에 잔류(#310). 마커가 남아 뒤늦게 잡아도 수익이 난다.
/// </summary>
public class SmugglerCourierEvent : SpawnedNpcEventBase
{
    // 결말 처리 단계 — 도착 통보는 NpcSmuggleState.Tick 안에서 오는데, 그 자리에서 곧바로 없애거나
    // 상태를 갈아타면 자기 상태를 돌리는 도중에 그 상태가 사라진다. 그래서 다음 틱으로 미룬다
    // (공통 골격이 잔류 전환을 미루는 것과 같은 이유, #310).
    private enum EPhase
    {
        None,
        Failed,     // 경로 불발 — 도심에 잔류시킨다
        LidOpening, // 맨홀 도착 — 뚜껑이 열리는 동안 서 있는다
        Descending, // 맨홀 아래로 내려간다
    }

    [Header("밀수 운반책")]
    [Tooltip("화물 무게 — 표준 시민이 1.0(경량 0.6 / 중량 1.6)이다. 이 값과 RopeDragLoad의 하한 면제가 " +
             "함께 '혼자서는 못 끈다'를 만든다: 속도 페널티는 무게 1당 -25%다")]
    [Min(0f)]
    [SerializeField]
    private float m_cargoWeight = 5f;

    [Tooltip("평소 걸음 속도 배율 — 짐이 무거워 보이게 기본 걸음보다 느리다. 개체별 속도 편차 위에 곱해진다")]
    [Range(0.1f, 1f)]
    [SerializeField]
    private float m_walkSpeedMultiplier = 0.75f;

    [Tooltip("맞은 뒤 속도 배율 — 짐을 진 채 맨홀로 달린다. 한 번 켜지면 되돌아가지 않는다.\n\n" +
             "시민 기본 속도가 2m/s(개체 편차 0.8~1.2배)라 2.5배면 4~6m/s다 — 달리기 모션 전환 임계 " +
             "4m/s를 넘겨야 발이 미끄러지지 않고, 플레이어 걷기(5)보다 빠르되 달리기(8)로는 잡힌다")]
    [Min(0.1f)]
    [SerializeField]
    private float m_panicSpeedMultiplier = 2.5f;

    [Header("맨홀 지점")]
    [Tooltip("도망칠 맨홀 후보 — 납치(AbductionEvent)가 쓰는 것과 같은 지점을 꽂는다. " +
             "스폰 자리에서 가장 먼 곳을 고른다(이동 시간이 곧 제한시간이다). 비우면 발동하지 않는다")]
    [SerializeField]
    private Transform[] m_manholePoints;

    [Tooltip("맨홀 뚜껑이 열리는 것을 보여 주는 시간(초) — AbductionManhole의 슬라이드 시간보다 길게 둘 것")]
    [Min(0f)]
    [SerializeField]
    private float m_lidOpenSeconds = 1.4f;

    [Tooltip("맨홀 아래로 내려가는 깊이(m)")]
    [Min(0.5f)]
    [SerializeField]
    private float m_descendDepth = 3f;

    [Tooltip("내려가는 데 걸리는 시간(초)")]
    [Min(0.1f)]
    [SerializeField]
    private float m_descendSeconds = 1.5f;

    // 진행 중인 결말 하나 — 슬롯이 하나뿐인 것은 SpawnCount가 1이기 때문이다.
    private EPhase m_phase;
    private NpcController m_endingNpc;
    private AbductionManhole m_endingManhole;
    private float m_phaseStartTime;
    private Vector3 m_descendFrom;

    public override string NoticeKey => "Hud.Event.Notice.SmugglerCourier";

    // 탈옥으로 방출되면 도주로 재개한다 — 화물은 수감 시점에 이미 손을 떠났고, 다시 맨홀로
    // 걸어가게 두면 이벤트가 끝난 뒤에 결말이 한 번 더 난다.
    protected override ERiotBehavior RiotBehavior => ERiotBehavior.Flee;

    /// <summary>갈 곳이 없으면 발생하지 않는다 — 맨홀 지점을 안 꽂으면 운반책이 제자리에 서 있는 그림이 된다.</summary>
    public override bool CanTrigger() => base.CanTrigger() && FindFarthestManhole(Vector3.zero) != null;

    protected override void ApplyBehavior(NpcController npc)
    {
        // ⚠ 무게는 <b>여기서</b> 박는다 — 스폰 직후(OnSpawned)가 아니다.
        // 코어의 InitBehavior가 무게를 추첨하는데(NpcRopeDrag.InitDragWeight) 그 시점이
        // 네트워크(OnNetworkSpawn)와 오프라인(Start)에서 다르다. ApplyBehavior는 스폰 <b>다음</b>
        // 프레임이라 양쪽 모두 그 뒤다 — 스폰 직후에 박으면 오프라인에서만 조용히 덮어써진다.
        npc.Rope.ServerSetDragWeight(m_cargoWeight, ignoreSpeedFloor: true);

        // 스폰 자리에서 가장 먼 맨홀 — 이동 시간이 곧 제한시간이라 가까운 곳이 뽑히면 잡을 창이
        // 사라진다. 스폰이 현장 근처(6~12m)라 이 기준이 곧 "현장에서도 멀다"가 된다.
        // (납치는 반대로 가장 가까운 맨홀을 고른다 — 그쪽은 끌고 가는 시간이 곧 구조 창이다)
        Transform manhole = FindFarthestManhole(npc.transform.position);

        npc.Health.OnDamaged += HandleDamaged;

        SmugglerCargo cargo = GetOrAddCargo(npc);
        cargo.OnFinished += HandleFinished;
        cargo.ServerBeginSmuggling(manhole, m_walkSpeedMultiplier, m_panicSpeedMultiplier);
    }

    /// <summary>
    /// 공통 소란 타이머를 쓰지 않는다 — 항상 <c>true</c>를 돌려 건너뛴다.
    /// 이 이벤트가 끝나는 조건은 시간이 아니라 <b>거리</b>(맨홀 도착)이고, 타이머가 함께 돌면
    /// 도착하기 전에 진정해 잔류하면서 화물이 도시에 눌러앉는다.
    ///
    /// 대신 이 자리에서 결말 단계를 돌린다 — 상태 전이 체인 밖에서 처리하기 위함이다.
    /// </summary>
    protected override bool OnServerTick()
    {
        if (m_endingNpc == null)
        {
            m_phase = EPhase.None;
            return true;
        }

        switch (m_phase)
        {
            case EPhase.Failed:
                // 없애지 않고 도심에 남긴다 — 배회로 돌리면 공통 골격이 이탈로 받아 잔류시키고(#310),
                // 마커가 남아 우연히라도 잡으면 경범죄 수익은 그대로 난다.
                Debug.Log($"[돌발이벤트] {DisplayName} — 경로 불발, 도심에 잔류");
                NpcController failed = m_endingNpc;
                ClearEnding();
                failed.Reaction.StartFlee(null);
                break;

            case EPhase.LidOpening:
                if (Time.time - m_phaseStartTime >= m_lidOpenSeconds)
                    BeginDescend();
                break;

            case EPhase.Descending:
                TickDescend();
                break;
        }

        return true;
    }

    // 맞았다 — 짐을 진 채 맨홀로 달린다. 상태는 그대로 두고 속도만 올린다. 서버에서만 발생.
    private void HandleDamaged(NpcController npc, GameObject attacker)
    {
        SmugglerCargo cargo = npc != null ? npc.GetComponent<SmugglerCargo>() : null;
        if (cargo == null || cargo.IsPanicked)
            return;

        cargo.ServerPanic();
        Debug.Log($"[돌발이벤트] {DisplayName} — 피격, 맨홀로 달리기 시작");
    }

    // 운반 종료 통보 — 처리는 다음 틱으로 미룬다(위 OnServerTick). 서버에서만 발생.
    private void HandleFinished(NpcController npc, bool reached)
    {
        if (npc == null || m_endingNpc != null)
            return;

        m_endingNpc = npc;
        m_phaseStartTime = Time.time;

        if (!reached)
        {
            m_phase = EPhase.Failed;
            return;
        }

        // 도착 — 납치가 쓰는 것과 같은 뚜껑을 연다. 뚜껑 참조는 선택이라(AbductionManhole)
        // 없으면 연출만 빠지고 하강·소멸은 그대로 간다.
        SmugglerCargo cargo = npc.GetComponent<SmugglerCargo>();
        Transform point = cargo != null ? cargo.Destination : null;
        m_endingManhole = point != null ? point.GetComponentInChildren<AbductionManhole>() : null;
        if (m_endingManhole != null)
            m_endingManhole.ServerOpen();

        m_phase = EPhase.LidOpening;
        Debug.Log($"[돌발이벤트] {DisplayName} — 맨홀 도착, 뚜껑 열림");
    }

    // 하강 시작 — NavMeshAgent를 끄고 위치를 직접 내린다. 켜 둔 채 내리면 에이전트가 매 프레임
    // NavMesh 위로 되돌려 제자리에서 떨린다.
    private void BeginDescend()
    {
        m_phase = EPhase.Descending;
        m_phaseStartTime = Time.time;
        m_descendFrom = m_endingNpc.transform.position;

        if (m_endingNpc.Agent != null)
            m_endingNpc.Agent.enabled = false;
    }

    private void TickDescend()
    {
        float t = Mathf.Clamp01((Time.time - m_phaseStartTime) / m_descendSeconds);
        m_endingNpc.transform.position = m_descendFrom + Vector3.down * (m_descendDepth * t);
        if (t < 1f)
            return;

        // 다 내려갔으면 뚜껑을 덮는다 — 열린 채로 두면 지나가던 사람이 계속 들여다보는 그림이 된다
        if (m_endingManhole != null)
            m_endingManhole.ServerClose();

        NpcController npc = m_endingNpc;
        ClearEnding();
        Debug.Log($"[돌발이벤트] {DisplayName} — 화물이 지하로 빠져나갔다");
        ServerDespawnSpawned(npc, playVfx: false); // 지하라 보이지 않는다 — 이펙트를 터뜨릴 이유가 없다
    }

    private void ClearEnding()
    {
        m_phase = EPhase.None;
        m_endingNpc = null;
        m_endingManhole = null;
    }

    // 스폰 자리에서 가장 먼 맨홀. origin이 Vector3.zero인 호출(CanTrigger)은 "하나라도 있는가"를 묻는 것이다.
    private Transform FindFarthestManhole(Vector3 origin)
    {
        Transform farthest = null;
        float bestSqr = -1f;

        if (m_manholePoints == null)
            return null;

        for (int i = 0; i < m_manholePoints.Length; i++)
        {
            Transform p = m_manholePoints[i];
            if (p == null)
                continue;

            float sqr = (p.position - origin).sqrMagnitude;
            if (sqr <= bestSqr)
                continue;

            bestSqr = sqr;
            farthest = p;
        }

        return farthest;
    }

    // 잔류·정리 두 경로가 모두 지난다 — 하나만 빠지면 이미 손 뗀 NPC의 통보를 계속 받는다.
    protected override void OnReleasing(NpcController npc) => Unsubscribe(npc);

    protected override void OnDespawning(NpcController npc) => Unsubscribe(npc);

    private void Unsubscribe(NpcController npc)
    {
        if (npc == null)
            return;

        npc.Health.OnDamaged -= HandleDamaged;

        SmugglerCargo cargo;
        if (npc.TryGetComponent(out cargo))
            cargo.OnFinished -= HandleFinished;

        if (m_endingNpc == npc)
        {
            // 뚜껑을 열어 놓고 결말이 끊겼다(라운드 종료 등) — 열린 채로 남기지 않는다
            if (m_endingManhole != null)
                m_endingManhole.ServerClose();
            ClearEnding();
        }
    }

    // 화물 부품을 얹는다 — 이미 붙어 있으면 그것을 쓴다 (PickpocketEvent와 같은 관례).
    private static SmugglerCargo GetOrAddCargo(NpcController npc)
    {
        SmugglerCargo cargo;
        return npc.TryGetComponent(out cargo) ? cargo : npc.gameObject.AddComponent<SmugglerCargo>();
    }
}
