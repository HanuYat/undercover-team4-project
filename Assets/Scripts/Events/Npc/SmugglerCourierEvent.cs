using UnityEngine;

/// <summary>
/// 밀수 운반책 — 마약·무기 화물을 지고 <b>거래 지점을 향해 도시를 가로지른다</b>. (GDD 6-4, #991)
///
/// 화물이 무거워 <b>혼자서는 밧줄로 끌 수 없다</b> — 현장 둘 이상이 같은 대상에 덧걸어야(줄다리기 합류,
/// #390) 이송이 성립한다. 협동 강제 수단은 <b>무게 하나</b>다: 세력 소탕(#721)이 동시 조작을 뺀 것과
/// 같은 이유로, 현장 2인 판에서 잠기는 장치는 두지 않는다.
///
/// <b>무거운 이유와 잡을 이유가 같은 사실이다</b> — 짊어진 것이 밀수품이라, 현장은 실루엣만 보고
/// "저건 잡아야 한다"를 안다. 스캔·판독을 거치지 않는 것이 이 이벤트의 성격이다.
///
/// 결말은 셋이다:
///  · <b>거래 지점 도착</b> → 그대로 빠져나간다(<see cref="SpawnedNpcEventBase.ServerDespawnSpawned"/>).
///    도심에 남기지 <b>않는</b> 유일한 스폰형이다 — 남기면 나중에 주워 담는 공짜 보상이 되어
///    "목적지까지의 거리 = 제한시간"이라는 이 이벤트의 알맹이가 사라진다.
///  · <b>제압 → 인계</b> → 경범죄 판정. 수익은 공통 골격이 지급한다.
///  · <b>제압 실패로 뿌리침 · 경로 불발</b> → 도심에 잔류(#310). 마커가 남아 뒤늦게 잡아도 수익이 난다.
/// </summary>
public class SmugglerCourierEvent : SpawnedNpcEventBase
{
    [Header("밀수 운반책")]
    [Tooltip("화물 무게 — 표준 시민이 1.0(경량 0.6 / 중량 1.6)이다. 이 값과 아래 하한 면제가 함께 " +
             "'혼자서는 못 끈다'를 만든다: RopeDragLoad의 속도 페널티는 무게 1당 -25%다")]
    [Min(0f)]
    [SerializeField]
    private float m_cargoWeight = 4f;

    [Tooltip("운반 중 걸음 속도 배율 — 짐이 무거워 보이게 기본 걸음보다 느리게 간다. " +
             "개체별 속도 편차(스폰 시 추첨) 위에 곱해진다")]
    [Range(0.1f, 1f)]
    [SerializeField]
    private float m_walkSpeedMultiplier = 0.75f;

    // 결말 처리를 상태 전이 체인 밖(다음 틱)으로 미루기 위한 대기 슬롯 — 도착 통보는
    // NpcSmuggleState.Tick 안에서 오는데, 그 자리에서 곧바로 없애거나 상태를 갈아타면
    // 자기 상태를 돌리는 도중에 그 상태가 사라진다. 공통 골격이 잔류 전환을 미루는 것과 같은 이유다(#310).
    // 슬롯이 하나뿐인 것은 SpawnCount가 1이기 때문이다 — 여럿을 스폰하게 되면 목록으로 바꿀 것.
    private NpcController m_pendingNpc;
    private bool m_pendingReached;

    public override string NoticeKey => "Hud.Event.Notice.SmugglerCourier";

    // 탈옥으로 방출되면 도주로 재개한다 — 화물은 수감 시점에 이미 손을 떠났고, 다시 거래 지점으로
    // 걸어가게 두면 이벤트가 끝난 뒤에 결말이 한 번 더 난다.
    protected override ERiotBehavior RiotBehavior => ERiotBehavior.Flee;

    /// <summary>갈 곳이 없으면 발생하지 않는다 — 거래 지점 마커(<see cref="SmugglerDropoff"/>)가
    /// 씬에 하나도 없으면 운반책이 제자리에 서 있는 그림이 된다.</summary>
    public override bool CanTrigger() => base.CanTrigger() && SmugglerDropoff.Active.Count > 0;

    protected override void ApplyBehavior(NpcController npc)
    {
        // ⚠ 무게는 <b>여기서</b> 박는다 — 스폰 직후(OnSpawned)가 아니다.
        // 코어의 InitBehavior가 무게를 추첨하는데(NpcRopeDrag.InitDragWeight) 그 시점이
        // 네트워크(OnNetworkSpawn)와 오프라인(Start)에서 다르다. ApplyBehavior는 스폰 <b>다음</b>
        // 프레임이라 양쪽 모두 그 뒤다 — 스폰 직후에 박으면 오프라인에서만 조용히 덮어써진다.
        npc.Rope.ServerSetDragWeight(m_cargoWeight, ignoreSpeedFloor: true);

        // 스폰 지점에서 가장 먼 거래 지점 — 이동 시간이 곧 제한시간이라 가까운 곳이 뽑히면
        // 잡을 창이 사라진다. 스폰이 현장 근처(6~12m)라 이 기준이 곧 "현장에서도 멀다"가 된다.
        Transform dropoff = SmugglerDropoff.FindFarthestFrom(npc.transform.position);

        SmugglerCargo cargo = GetOrAddCargo(npc);
        cargo.OnFinished += HandleFinished;
        cargo.ServerBeginSmuggling(dropoff, m_walkSpeedMultiplier);
    }

    /// <summary>
    /// 공통 소란 타이머를 쓰지 않는다 — 항상 <c>true</c>를 돌려 건너뛴다.
    /// 이 이벤트가 끝나는 조건은 시간이 아니라 <b>거리</b>(거래 지점 도착)이고, 타이머가 함께 돌면
    /// 도착하기 전에 진정해 잔류하면서 화물이 도시에 눌러앉는다.
    ///
    /// 대신 이 자리에서 대기 슬롯을 비운다 — 상태 전이 체인 밖에서 결말을 처리하기 위함이다.
    /// </summary>
    protected override bool OnServerTick()
    {
        NpcController npc = m_pendingNpc;
        if (npc == null)
            return true;

        m_pendingNpc = null;

        if (m_pendingReached)
        {
            Debug.Log($"[돌발이벤트] {DisplayName} — 거래 지점 도착, 화물이 빠져나갔다");
            ServerDespawnSpawned(npc);
            return true;
        }

        // 불발(경로 실패) — 없애지 않고 도심에 남긴다. 배회로 돌리면 공통 골격이 이탈로 받아
        // 잔류시키고(#310), 마커가 남아 우연히라도 잡으면 경범죄 수익은 그대로 난다.
        Debug.Log($"[돌발이벤트] {DisplayName} — 경로 불발, 도심에 잔류");
        npc.Reaction.StartFlee(null);
        return true;
    }

    // 운반 종료 통보 — 처리는 다음 틱으로 미룬다(위 OnServerTick). 서버에서만 발생.
    private void HandleFinished(NpcController npc, bool reached)
    {
        if (npc == null)
            return;

        m_pendingNpc = npc;
        m_pendingReached = reached;
    }

    // 잔류·정리 두 경로가 모두 지난다 — 하나만 빠지면 이미 손 뗀 NPC의 통보를 계속 받는다.
    protected override void OnReleasing(NpcController npc) => Unsubscribe(npc);

    protected override void OnDespawning(NpcController npc) => Unsubscribe(npc);

    private void Unsubscribe(NpcController npc)
    {
        if (npc != null && npc.TryGetComponent(out SmugglerCargo cargo))
            cargo.OnFinished -= HandleFinished;

        if (m_pendingNpc == npc)
            m_pendingNpc = null;
    }

    // 화물 부품을 얹는다 — 이미 붙어 있으면 그것을 쓴다 (PickpocketEvent와 같은 관례).
    private static SmugglerCargo GetOrAddCargo(NpcController npc) =>
        npc.TryGetComponent(out SmugglerCargo cargo) ? cargo : npc.gameObject.AddComponent<SmugglerCargo>();
}
