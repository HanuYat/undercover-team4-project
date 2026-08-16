using UnityEngine;

/// <summary>
/// 소매치기 — 현장 플레이어에게 <b>시민 걸음으로 다가가</b> 밀착하면 손에 든 물건 하나를 채고 달아난다. (GDD 6-4, #303)
///
/// 접근은 추격 파이프라인을 빌려 쓴다(<see cref="NpcDutyAgent.StartPenaltyChase"/>에 <see cref="NpcDutyKind.Pickpocket"/>):
/// 밀착 판정·통보를 추격 상태가 이미 하므로 그 통보(<see cref="NpcDutyAgent.OnPenaltyCaught"/>)를 <b>탈취 신호</b>로 받는다.
/// 걸음 속도·표적 고정 같은 임무별 차이는 <see cref="NpcChaseState"/>가 임무 종류를 읽어 가른다.
///
/// 훔친 물건의 결말은 <see cref="Pickpocket"/> 부품이 쥔다 — 이 이벤트는 신호를 잇고 도주로 넘기는 것까지만 한다:
///  · <b>제압</b> → 그 자리에 떨군다. 주우면 회수 끝.
///  · <b>놓침</b> → 물건을 들린 채 도심에 잔류(#310). 뒤늦게 잡아도 되찾는다(<see cref="MisdemeanorLoiterer"/>).
///  · <b>라운드 종료까지 못 잡음</b> → 없어진다. 상점 구매품이면 팀 구매 목록에서도 빠져 영구 손실.
/// </summary>
public class PickpocketEvent : SpawnedNpcEventBase
{
    [Header("소매치기")]
    [Tooltip("표적에게 다가가는 제한 시간(초) — 이 안에 붙지 못하면 포기하고 시민으로 잔류한다. 걷는 속도라 표적이 계속 움직이면 못 붙는다")]
    [SerializeField]
    private float m_approachSeconds = 12f;

    // 접근을 포기할 시각. 0 이하면 접근 중 아님.
    private float m_giveUpTime;

    /// <summary>
    /// 조용히 시작한다 (팀 확정 2026-08-13) — 스폰형 중 <b>이것만</b> 알리지 않는다.
    /// 난동자·나체 난동꾼은 애초에 눈에 띄라고 있는 소란이라 토스트가 그 소란의 일부지만, 소매치기는
    /// <b>시민 걸음으로 다가와 스쳐 지나가는</b> 것이 전부다 — 발생을 알려 버리면 현장이 주위를 경계하게
    /// 되고, 알아채지 못한 사이에 털린다는 이 이벤트의 유일한 장치가 사라진다.
    /// 당한 사실은 그 순간 당사자에게만 알린다(<see cref="PlayerTheftView.ShowStolen"/>).
    /// </summary>
    public override bool AnnounceOnBegin => false;

    // 탈옥으로 방출되면 도주로 재개한다 — 수감되려면 제압을 거쳤으니 훔친 물건은 이미 떨궈진 뒤고,
    // 빈손 소매치기가 다시 노리게 두면 같은 사람이 몇 번이고 털린다.
    protected override ERiotBehavior RiotBehavior => ERiotBehavior.Flee;

    protected override void ApplyBehavior()
    {
        if (m_threat == null)
            return; // 표적이 사라졌다 — 배회로 두면 공통 골격이 이탈(잔류)로 끝낸다

        m_npc.Penalty.OnPenaltyCaught += HandleReach;

        // 무력화되는 순간을 직접 받는다 — <b>여기가 "제압당했다"의 실제 순간</b>이다.
        // 예전에는 Captured 전이에만 걸어 뒀는데, 스턴은 오버레이라 CurrentState를 바꾸지 않는다(#292):
        // 진압봉으로 때려 쓰러뜨려도 상태는 Run 그대로여서 물건이 떨어지지 않았고, 밧줄로 묶었다(Escorted)
        // 놓아준 순간(Captured)에야 나왔다. 테이저로 재운 경우도 같은 이유로 안 떨어졌다.
        m_npc.Stun.OnStunned += HandleStunned;

        m_npc.Penalty.StartPenaltyChase(m_threat, NpcDutyKind.Pickpocket);
        m_giveUpTime = Time.time + m_approachSeconds;
    }

    // 다가가다 못 붙으면 포기하고 시민으로 섞인다 — 걷는 속도라 표적이 계속 움직이면 영영 못 잡는데,
    // 그동안 뒤를 졸졸 따라다니는 그림이 나온다. 훔치는 것은 스쳐 지나가는 한순간이어야 한다.
    // 배회로 돌리면 공통 골격이 이탈로 받아 잔류시킨다 — 마커가 남아 잡으면 경범죄 수익은 그대로다.
    protected override bool OnServerTick()
    {
        if (m_giveUpTime <= 0f || Time.time < m_giveUpTime)
            return false;

        m_giveUpTime = 0f;
        if (!m_npc.Penalty.IsPickpocketDuty)
            return false; // 이미 탈취를 끝내고 도주 중이다

        Debug.Log($"[돌발이벤트] {DisplayName} — 접근 실패, 포기하고 시민으로 섞임");
        m_npc.Penalty.EndPenaltyDuty();
        return true;
    }

    // 표적에 밀착했다 — 물건 하나를 채고 곧바로 도주로 넘어간다. 서버에서만 발생.
    private void HandleReach(NpcController npc, Transform caught)
    {
        if (m_npc == null || npc != m_npc)
            return;

        // 통보는 3초마다 재시도되므로(NpcChaseState) 한 번 챘으면 더 받지 않는다
        m_npc.Penalty.OnPenaltyCaught -= HandleReach;
        m_giveUpTime = 0f; // 붙었으니 접근 제한은 끝

        // 훔치는 것도 뒷일(떨구기·손실)도 소매치기 자신의 행동이다 — 이 이벤트는 대상만 넘긴다
        PlayerLoadout victim = caught != null ? caught.GetComponentInParent<PlayerLoadout>() : null;
        ItemBase stolen = victim != null ? GetOrAddPickpocket(m_npc).ServerStealFrom(victim) : null;

        if (stolen != null)
        {
            // 물건은 소리 없이 사라지므로 당사자에게 알린다 — 안 알리면 한참 뒤에야 없어진 걸 안다
            victim.GetComponent<PlayerTheftView>()?.ShowStolen();

            Debug.Log($"[돌발이벤트] {DisplayName} — {stolen.name} 탈취, 도주 시작");
        }
        else
        {
            // 뺏을 게 없었다(빈손·묶은 밧줄뿐) — 그래도 달아나게 둔다. 그 자리에 서 있으면 무슨 일이
            // 일어난 건지 알 수 없고, 쫓아가 잡으면 경범죄 수익은 그대로 난다.
            Debug.Log($"[돌발이벤트] {DisplayName} — 뺏을 소지품이 없어 빈손으로 도주");
        }

        m_npc.Reaction.StartFlee(m_threat);
    }

    // 무력화되면 그 자리에 떨군다 — 진압봉으로 쓰러뜨렸든 테이저로 재웠든 같다. 서버에서만 발생.
    private void HandleStunned(NpcController npc, Transform by)
    {
        if (m_npc == null || npc != m_npc)
            return;

        DropStolen(npc);
    }

    // 신병으로 잡힌 순간에도 떨군다 — <b>폴백이다.</b>
    // 무력화를 거치지 않고 Captured에 이르는 경로(밧줄 놓아두기 등)가 남아 있고,
    // ServerDropStolenItem은 들고 있는 게 없으면 무동작이라 두 번 불려도 안전하다.
    protected override void OnCaptured(NpcController npc) => DropStolen(npc);

    private static void DropStolen(NpcController npc)
    {
        if (npc != null && npc.TryGetComponent(out Pickpocket thief))
            thief.ServerDropStolenItem();
    }

    // 물건을 든 채 놓쳤어도 여기선 없애지 않는다 — 들린 채 도심에 남는다.
    // 나중에 우연히라도 잡으면 그 자리에 떨어뜨리므로(MisdemeanorLoiterer) 되찾을 길이 남는다.
    // 손실 확정은 라운드가 끝날 때다.
    protected override void OnReleasing(NpcController npc)
    {
        Unsubscribe(npc);
        m_giveUpTime = 0f;
    }

    // 라운드 종료 등으로 통째로 정리된다 — 들고 있던 물건도 함께 사라진다
    protected override void OnDespawning(NpcController npc)
    {
        Unsubscribe(npc);
        m_giveUpTime = 0f;

        if (npc != null && npc.TryGetComponent(out Pickpocket thief))
            thief.ServerLoseStolenItem();
    }

    // 밀착 통보 구독만 끊는다 — 손을 떼는 두 경로(잔류·정리)가 모두 지난다.
    // 하나만 빠지면 이미 손 뗀 NPC의 통보를 계속 받아 두 번 훔친다.
    private void Unsubscribe(NpcController npc)
    {
        if (npc == null)
            return;

        npc.Penalty.OnPenaltyCaught -= HandleReach;
        npc.Stun.OnStunned -= HandleStunned;
    }

    // 탈취 행동 부품을 얹는다 — 이미 붙어 있으면 그것을 쓴다.
    // 매번 새로 붙이면 TryGetComponent가 첫 번째만 돌려주므로, NPC 재사용 경로가 생기는 순간 조용히 어긋난다.
    private static Pickpocket GetOrAddPickpocket(NpcController npc) =>
        npc.TryGetComponent(out Pickpocket thief) ? thief : npc.gameObject.AddComponent<Pickpocket>();
}
